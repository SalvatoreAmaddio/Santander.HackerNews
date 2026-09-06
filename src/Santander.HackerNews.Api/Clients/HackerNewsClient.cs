using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using Santander.HackerNews.Api.Models;
using Santander.HackerNews.Api.Options;

namespace Santander.HackerNews.Api.Clients;

public sealed class HackerNewsClient : IHackerNewsClient, IDisposable
{
    public const string HttpClientName = "HackerNews";
    private readonly IHttpClientFactory _clientFactory;
    private readonly IMemoryCache _cache;
    private readonly HackerNewsOptions _options;
    private readonly CancellationToken _stopping;
    private readonly SemaphoreSlim _upstreamGate;
    private readonly ConcurrentDictionary<string, Lazy<Task<object?>>> _inFlight = new();

    public HackerNewsClient(IHttpClientFactory clientFactory, IMemoryCache cache,
                            IOptions<HackerNewsOptions> options, IHostApplicationLifetime lifetime)
    {
        _clientFactory = clientFactory;
        _cache = cache;
        _options = options.Value;
        _stopping = lifetime.ApplicationStopping;
        _upstreamGate = new(_options.MaxConcurrentUpstreamRequests);
    }

    public async Task<IReadOnlyList<long>> GetBestStoryIdsAsync(CancellationToken cancellationToken) =>
        await GetCachedAsync<long[]>("beststories.json", _options.RankingCacheSeconds, cancellationToken)
        ?? throw new HttpRequestException("Hacker News returned a null story list.");

    public Task<HackerNewsItem?> GetItemAsync(long id, CancellationToken cancellationToken) =>
        GetCachedAsync<HackerNewsItem>($"item/{id}.json", _options.StoryCacheSeconds, cancellationToken);

    private async Task<T?> GetCachedAsync<T>(string path, int cacheSeconds, CancellationToken cancellationToken)
        where T : class
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_cache.TryGetValue(path, out T? cached))
            return cached;

        Lazy<Task<object?>> pending = _inFlight.GetOrAdd(path, _ => new(() => RefreshAsync<T>(path, cacheSeconds)));
        // A disconnected caller stops waiting, but cannot cancel work needed by other callers.
        return (T?)await pending.Value.WaitAsync(cancellationToken);
    }

    private async Task<object?> RefreshAsync<T>(string path, int cacheSeconds) where T : class
    {
        try
        {
            if (_cache.TryGetValue(path, out T? cached))
                return cached;

            T? value = await SendAsync<T>(path);
            if (value is null && typeof(T) == typeof(long[]))
                throw new HttpRequestException("Hacker News returned a null story list.");
            // Cache missing items too, to avoid repeatedly fetching unavailable stories.
            _cache.Set(path, value, TimeSpan.FromSeconds(cacheSeconds));
            return value;
        }
        finally
        {
            _inFlight.TryRemove(path, out _);
        }
    }

    private async Task<T?> SendAsync<T>(string path)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(_stopping);
        deadline.CancelAfter(TimeSpan.FromSeconds(_options.FetchTimeoutSeconds));
        CancellationToken token = deadline.Token;
        try
        {
            // The deadline includes queueing, retry delays, headers and body consumption.
            await _upstreamGate.WaitAsync(token);
            try
            {
                using HttpClient client = _clientFactory.CreateClient(HttpClientName);
                for (int attempt = 0; ; attempt++)
                {
                    TimeSpan delay = TimeSpan.FromMilliseconds(150 * Math.Pow(2, attempt) + Random.Shared.Next(25, 125));
                    try
                    {
                        using HttpResponseMessage response = await client.GetAsync(path, HttpCompletionOption.ResponseHeadersRead, token);
                        if (response.IsSuccessStatusCode)
                            return await response.Content.ReadFromJsonAsync<T>(cancellationToken: token);

                        if (!IsTransient(response.StatusCode) || attempt >= _options.RetryCount)
                            throw new HttpRequestException($"Hacker News returned {(int)response.StatusCode}.", null, response.StatusCode);

                        TimeSpan? retryAfter = response.Headers.RetryAfter?.Delta
                            ?? (response.Headers.RetryAfter?.Date - DateTimeOffset.UtcNow);
                        if (retryAfter > delay)
                            delay = retryAfter.Value;
                    }
                    catch (HttpRequestException exception) when (attempt < _options.RetryCount &&
                        (exception.StatusCode is null || IsTransient(exception.StatusCode.Value)))
                    {
                        // Transport errors and transient statuses only; permanent failures escape.
                    }
                    await Task.Delay(delay, token);
                }
            }
            finally
            {
                _upstreamGate.Release();
            }
        }
        catch (OperationCanceledException exception) when (!_stopping.IsCancellationRequested)
        {
            throw new TimeoutException("Hacker News fetch exceeded its deadline.", exception);
        }
        catch (JsonException exception)
        {
            throw new HttpRequestException("Hacker News returned invalid JSON.", exception);
        }
    }

    private static bool IsTransient(HttpStatusCode statusCode) =>
        statusCode is HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests || (int)statusCode >= 500;

    public void Dispose() => _upstreamGate.Dispose();
}
