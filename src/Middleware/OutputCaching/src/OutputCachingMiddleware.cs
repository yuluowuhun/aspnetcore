// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.ObjectPool;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Primitives;
using Microsoft.Net.Http.Headers;

namespace Microsoft.AspNetCore.OutputCaching;

/// <summary>
/// Enable HTTP response caching.
/// </summary>
public class OutputCachingMiddleware
{
    private static readonly TimeSpan DefaultExpirationTimeSpan = TimeSpan.FromSeconds(60);

    // see https://tools.ietf.org/html/rfc7232#section-4.1
    private static readonly string[] HeadersToIncludeIn304 =
        new[] { "Cache-Control", "Content-Location", "Date", "ETag", "Expires", "Vary" };

    private readonly RequestDelegate _next;
    private readonly OutputCachingOptions _options;
    private readonly ILogger _logger;
    private readonly IOutputCachingPolicyProvider _policyProvider;
    private readonly IOutputCache _cache;
    private readonly IOutputCachingKeyProvider _keyProvider;

    /// <summary>
    /// Creates a new <see cref="OutputCachingMiddleware"/>.
    /// </summary>
    /// <param name="next">The <see cref="RequestDelegate"/> representing the next middleware in the pipeline.</param>
    /// <param name="options">The options for this middleware.</param>
    /// <param name="loggerFactory">The <see cref="ILoggerFactory"/> used for logging.</param>
    /// <param name="poolProvider">The <see cref="ObjectPoolProvider"/> used for creating <see cref="ObjectPool"/> instances.</param>
    public OutputCachingMiddleware(
        RequestDelegate next,
        IOptions<OutputCachingOptions> options,
        ILoggerFactory loggerFactory,
        ObjectPoolProvider poolProvider,
        IEnumerable<IOutputCachingRequestPolicy> requestPolicies
        )
        : this(
            next,
            options,
            loggerFactory,
            new OutputCachingPolicyProvider(options),
            new MemoryOutputCache(new MemoryCache(new MemoryCacheOptions
            {
                SizeLimit = options.Value.SizeLimit
            })),
            new OutputCachingKeyProvider(poolProvider, options))
    { }

    // for testing
    internal OutputCachingMiddleware(
        RequestDelegate next,
        IOptions<OutputCachingOptions> options,
        ILoggerFactory loggerFactory,
        IOutputCachingPolicyProvider policyProvider,
        IOutputCache cache,
        IOutputCachingKeyProvider keyProvider)
    {
        if (next == null)
        {
            throw new ArgumentNullException(nameof(next));
        }
        if (options == null)
        {
            throw new ArgumentNullException(nameof(options));
        }
        if (loggerFactory == null)
        {
            throw new ArgumentNullException(nameof(loggerFactory));
        }
        if (policyProvider == null)
        {
            throw new ArgumentNullException(nameof(policyProvider));
        }
        if (cache == null)
        {
            throw new ArgumentNullException(nameof(cache));
        }
        if (keyProvider == null)
        {
            throw new ArgumentNullException(nameof(keyProvider));
        }

        _next = next;
        _options = options.Value;
        _logger = loggerFactory.CreateLogger<OutputCachingMiddleware>();
        _policyProvider = policyProvider;
        _cache = cache;
        _keyProvider = keyProvider;
    }

    /// <summary>
    /// Invokes the logic of the middleware.
    /// </summary>
    /// <param name="httpContext">The <see cref="HttpContext"/>.</param>
    /// <returns>A <see cref="Task"/> that completes when the middleware has completed processing.</returns>
    public async Task Invoke(HttpContext httpContext)
    {
        var context = new OutputCachingContext(httpContext, _logger);

        // Add IResponseCachingFeature
        AddOutputCachingFeature(context.HttpContext);

        await _policyProvider.OnRequestAsync(context);

        // Should we attempt any caching logic?
        if (context.AttemptResponseCaching)
        {
            // Can this request be served from cache?
            if (context.AllowCacheLookup && await TryServeFromCacheAsync(context))
            {
                return;
            }

            // Should we store the response to this request?
            if (context.AllowCacheStorage)
            {
                // Hook up to listen to the response stream
                ShimResponseStream(context);

                try
                {
                    await _next(httpContext);

                    // The next middleware might change the policy
                    await _policyProvider.OnServeResponseAsync(context);

                    // If there was no response body, check the response headers now. We can cache things like redirects.
                    StartResponse(context);

                    // Finalize the cache entry
                    FinalizeCacheBody(context);
                }
                finally
                {
                    UnshimResponseStream(context);
                }

                return;
            }
        }

        // Response should not be captured but add IOutputCachingFeature which may be required when the response is generated
        AddOutputCachingFeature(httpContext);

        try
        {
            await _next(httpContext);
        }
        finally
        {
            RemoveOutputCachingFeature(httpContext);
        }
    }

    internal async Task<bool> TryServeCachedResponseAsync(OutputCachingContext context, OutputCacheEntry cachedResponse)
    {
        context.CachedResponse = cachedResponse;
        context.CachedResponseHeaders = cachedResponse.Headers;
        context.ResponseTime = _options.SystemClock.UtcNow;
        var cachedEntryAge = context.ResponseTime.Value - context.CachedResponse.Created;
        context.CachedEntryAge = cachedEntryAge > TimeSpan.Zero ? cachedEntryAge : TimeSpan.Zero;

        if (context.IsCacheEntryFresh)
        {
            // Check conditional request rules
            if (ContentIsNotModified(context))
            {
                _logger.NotModifiedServed();
                context.HttpContext.Response.StatusCode = StatusCodes.Status304NotModified;

                if (context.CachedResponseHeaders != null)
                {
                    foreach (var key in HeadersToIncludeIn304)
                    {
                        if (context.CachedResponseHeaders.TryGetValue(key, out var values))
                        {
                            context.HttpContext.Response.Headers[key] = values;
                        }
                    }
                }
            }
            else
            {
                var response = context.HttpContext.Response;
                // Copy the cached status code and response headers
                response.StatusCode = context.CachedResponse.StatusCode;
                foreach (var header in context.CachedResponse.Headers)
                {
                    response.Headers[header.Key] = header.Value;
                }

                // Note: int64 division truncates result and errors may be up to 1 second. This reduction in
                // accuracy of age calculation is considered appropriate since it is small compared to clock
                // skews and the "Age" header is an estimate of the real age of cached content.
                response.Headers.Age = HeaderUtilities.FormatNonNegativeInt64(context.CachedEntryAge.Value.Ticks / TimeSpan.TicksPerSecond);

                // Copy the cached response body
                var body = context.CachedResponse.Body;
                if (body.Length > 0)
                {
                    try
                    {
                        await body.CopyToAsync(response.BodyWriter, context.HttpContext.RequestAborted);
                    }
                    catch (OperationCanceledException)
                    {
                        context.HttpContext.Abort();
                    }
                }
                _logger.CachedResponseServed();
            }
            return true;
        }

        return false;
    }

    internal async Task<bool> TryServeFromCacheAsync(OutputCachingContext context)
    {
        CreateCacheKey(context);

        if (String.IsNullOrEmpty(context.CacheKey))
        {
            throw new InvalidOperationException("Cache key must be defined");
        }

        var cacheEntry = _cache.Get(context.CacheKey);

        if (cacheEntry != null)
        {
            await _policyProvider.OnServeFromCacheAsync(context);

            if (await TryServeCachedResponseAsync(context, cacheEntry))
            {
                return true;
            }
        }

        if (HeaderUtilities.ContainsCacheDirective(context.HttpContext.Request.Headers.CacheControl, CacheControlHeaderValue.OnlyIfCachedString))
        {
            _logger.GatewayTimeoutServed();
            context.HttpContext.Response.StatusCode = StatusCodes.Status504GatewayTimeout;
            return true;
        }

        _logger.NoResponseServed();
        return false;
    }

    private void CreateCacheKey(OutputCachingContext context)
    {
        var varyHeaders = new StringValues(context.HttpContext.Response.Headers.GetCommaSeparatedValues(HeaderNames.Vary));
        var varyQueryKeys = context.CachedVaryByRules.QueryKeys;
        var varyByCustomKeys = context.CachedVaryByRules.VaryByCustom;
        var varyByPrefix = context.CachedVaryByRules.VaryByPrefix;

        // Check if any vary rules exist
        if (!StringValues.IsNullOrEmpty(varyHeaders) || !StringValues.IsNullOrEmpty(varyQueryKeys) || !StringValues.IsNullOrEmpty(varyByPrefix) || varyByCustomKeys.Count > 0)
        {
            // Normalize order and casing of vary by rules
            var normalizedVaryHeaders = GetOrderCasingNormalizedStringValues(varyHeaders);
            var normalizedVaryQueryKeys = GetOrderCasingNormalizedStringValues(varyQueryKeys);
            var normalizedVaryByCustom = GetOrderCasingNormalizedDictionary(varyByCustomKeys);

            // Update vary rules with normalized values
            context.CachedVaryByRules = new CachedVaryByRules
            {
                VaryByPrefix = varyByPrefix + normalizedVaryByCustom,
                Headers = normalizedVaryHeaders,
                QueryKeys = normalizedVaryQueryKeys
            };

            // TODO: Add same condition on LogLevel in Response Caching
            // Always overwrite the CachedVaryByRules to update the expiry information
            if (_logger.IsEnabled(LogLevel.Debug))
            {
                _logger.VaryByRulesUpdated(normalizedVaryHeaders.ToString(), normalizedVaryQueryKeys.ToString());
            }

            context.CacheKey = _keyProvider.CreateStorageVaryByKey(context);
        }
        else
        {
            context.CacheKey = _keyProvider.CreateBaseKey(context);
        }
    }

    /// <summary>
    /// Finalize cache headers.
    /// </summary>
    /// <param name="context"></param>
    private void FinalizeCacheHeaders(OutputCachingContext context)
    {
        if (context.IsResponseCacheable)
        {
            // Create the cache entry now
            var response = context.HttpContext.Response;
            var headers = response.Headers;

            context.CachedResponseValidFor = context.ResponseSharedMaxAge ??
                context.ResponseMaxAge ??
                (context.ResponseExpires - context.ResponseTime!.Value) ??
                DefaultExpirationTimeSpan;

            // Ensure date header is set
            if (!context.ResponseDate.HasValue)
            {
                context.ResponseDate = context.ResponseTime!.Value;
                // Setting the date on the raw response headers.
                headers.Date = HeaderUtilities.FormatDate(context.ResponseDate.Value);
            }

            // Store the response on the state
            context.CachedResponse = new OutputCacheEntry
            {
                Created = context.ResponseDate.Value,
                StatusCode = response.StatusCode,
                Headers = new HeaderDictionary()
            };

            foreach (var header in headers)
            {
                if (!string.Equals(header.Key, HeaderNames.Age, StringComparison.OrdinalIgnoreCase))
                {
                    context.CachedResponse.Headers[header.Key] = header.Value;
                }
            }

            return;
        }

        context.ResponseCachingStream.DisableBuffering();
    }

    /// <summary>
    /// Stores the response body
    /// </summary>
    internal void FinalizeCacheBody(OutputCachingContext context)
    {
        if (context.IsResponseCacheable && context.ResponseCachingStream.BufferingEnabled)
        {
            var contentLength = context.HttpContext.Response.ContentLength;
            var cachedResponseBody = context.ResponseCachingStream.GetCachedResponseBody();
            if (!contentLength.HasValue || contentLength == cachedResponseBody.Length
                || (cachedResponseBody.Length == 0
                    && HttpMethods.IsHead(context.HttpContext.Request.Method)))
            {
                var response = context.HttpContext.Response;
                // Add a content-length if required
                if (!response.ContentLength.HasValue && StringValues.IsNullOrEmpty(response.Headers.TransferEncoding))
                {
                    context.CachedResponse.Headers.ContentLength = cachedResponseBody.Length;
                }

                context.CachedResponse.Body = cachedResponseBody;
                _logger.ResponseCached();

                if (String.IsNullOrEmpty(context.CacheKey))
                {
                    throw new InvalidOperationException("Cache key must be defined");
                }

                _cache.Set(context.CacheKey, context.CachedResponse, context.CachedResponseValidFor);
            }
            else
            {
                _logger.ResponseContentLengthMismatchNotCached();
            }
        }
        else
        {
            _logger.LogResponseNotCached();
        }
    }

    /// <summary>
    /// Mark the response as started and set the response time if no response was started yet.
    /// </summary>
    /// <param name="context"></param>
    /// <returns><c>true</c> if the response was not started before this call; otherwise <c>false</c>.</returns>
    private bool OnStartResponse(OutputCachingContext context)
    {
        if (!context.ResponseStarted)
        {
            context.ResponseStarted = true;
            context.ResponseTime = _options.SystemClock.UtcNow;

            return true;
        }
        return false;
    }

    internal void StartResponse(OutputCachingContext context)
    {
        if (OnStartResponse(context))
        {
            FinalizeCacheHeaders(context);
        }
    }

    internal static void AddOutputCachingFeature(HttpContext context)
    {
        if (context.Features.Get<IOutputCachingFeature>() != null)
        {
            throw new InvalidOperationException($"Another instance of {nameof(OutputCachingFeature)} already exists. Only one instance of {nameof(OutputCachingMiddleware)} can be configured for an application.");
        }

        context.Features.Set<IOutputCachingFeature>(new OutputCachingFeature());
    }

    internal void ShimResponseStream(OutputCachingContext context)
    {
        // Shim response stream
        context.OriginalResponseStream = context.HttpContext.Response.Body;
        context.ResponseCachingStream = new ResponseCachingStream(
            context.OriginalResponseStream,
            _options.MaximumBodySize,
            StreamUtilities.BodySegmentSize,
            () => StartResponse(context));
        context.HttpContext.Response.Body = context.ResponseCachingStream;
    }

    internal static void RemoveOutputCachingFeature(HttpContext context) =>
        context.Features.Set<IOutputCachingFeature?>(null);

    internal static void UnshimResponseStream(OutputCachingContext context)
    {
        // Unshim response stream
        context.HttpContext.Response.Body = context.OriginalResponseStream;

        // Remove IResponseCachingFeature
        RemoveOutputCachingFeature(context.HttpContext);
    }

    internal static bool ContentIsNotModified(OutputCachingContext context)
    {
        var cachedResponseHeaders = context.CachedResponseHeaders;
        var ifNoneMatchHeader = context.HttpContext.Request.Headers.IfNoneMatch;

        if (!StringValues.IsNullOrEmpty(ifNoneMatchHeader))
        {
            if (ifNoneMatchHeader.Count == 1 && StringSegment.Equals(ifNoneMatchHeader[0], EntityTagHeaderValue.Any.Tag, StringComparison.OrdinalIgnoreCase))
            {
                context.Logger.NotModifiedIfNoneMatchStar();
                return true;
            }

            if (!StringValues.IsNullOrEmpty(cachedResponseHeaders.ETag)
                && EntityTagHeaderValue.TryParse(cachedResponseHeaders.ETag.ToString(), out var eTag)
                && EntityTagHeaderValue.TryParseList(ifNoneMatchHeader, out var ifNoneMatchEtags))
            {
                for (var i = 0; i < ifNoneMatchEtags.Count; i++)
                {
                    var requestETag = ifNoneMatchEtags[i];
                    if (eTag.Compare(requestETag, useStrongComparison: false))
                    {
                        context.Logger.NotModifiedIfNoneMatchMatched(requestETag);
                        return true;
                    }
                }
            }
        }
        else
        {
            var ifModifiedSince = context.HttpContext.Request.Headers.IfModifiedSince;
            if (!StringValues.IsNullOrEmpty(ifModifiedSince))
            {
                if (!HeaderUtilities.TryParseDate(cachedResponseHeaders.LastModified.ToString(), out var modified) &&
                    !HeaderUtilities.TryParseDate(cachedResponseHeaders.Date.ToString(), out modified))
                {
                    return false;
                }

                if (HeaderUtilities.TryParseDate(ifModifiedSince.ToString(), out var modifiedSince) &&
                    modified <= modifiedSince)
                {
                    context.Logger.NotModifiedIfModifiedSinceSatisfied(modified, modifiedSince);
                    return true;
                }
            }
        }

        return false;
    }

    // Normalize order and casing
    internal static StringValues GetOrderCasingNormalizedStringValues(StringValues stringValues)
    {
        if (stringValues.Count == 1)
        {
            return new StringValues(stringValues.ToString().ToUpperInvariant());
        }
        else
        {
            var originalArray = stringValues.ToArray();
            var newArray = new string[originalArray.Length];

            for (var i = 0; i < originalArray.Length; i++)
            {
                newArray[i] = originalArray[i]!.ToUpperInvariant();
            }

            // Since the casing has already been normalized, use Ordinal comparison
            Array.Sort(newArray, StringComparer.Ordinal);

            return new StringValues(newArray);
        }
    }

    internal static StringValues GetOrderCasingNormalizedDictionary(Dictionary<string, string> dictionary)
    {
        const char KeySubDelimiter = '\x1f';

        var newArray = new string[dictionary.Count];

        var i = 0;
        foreach (var (key, value) in dictionary)
        {
            newArray[i++] = $"{key.ToUpperInvariant()}{KeySubDelimiter}{value}";
        }

        // Since the casing has already been normalized, use Ordinal comparison
        Array.Sort(newArray, StringComparer.Ordinal);

        return new StringValues(newArray);
    }
}