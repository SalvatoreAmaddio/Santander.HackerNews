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
    private readonly IHttpClientFactory ClientFactory;
    private readonly IMemoryCache Cache;
    private readonly HackerNewsOptions Options;
    private readonly CancellationToken Stopping;
    private readonly SemaphoreSlim UpstreamGate;
    private readonly ConcurrentDictionary<string, Lazy<Task<object?>>> InFlight = new();

    public HackerNewsClient(IHttpClientFactory clientFactory, IMemoryCache cache,
                            IOptions<HackerNewsOptions> options, IHostApplicationLifetime lifetime)
    {
        ClientFactory = clientFactory;
        Cache = cache;
        Options = options.Value;
        Stopping = lifetime.ApplicationStopping;
        UpstreamGate = new(Options.MaxConcurrentUpstreamRequests);
    }

    public async Task<IReadOnlyList<long>> GetBestStoryIdsAsync(CancellationToken cancellationToken)
    {
        return await GetCachedAsync<long[]>("beststories.json", Options.RankingCacheSeconds, cancellationToken)
        ?? throw new HttpRequestException("Hacker News returned a null story list.");
    }

    public Task<HackerNewsItem?> GetItemAsync(long id, CancellationToken cancellationToken)
    {
        return GetCachedAsync<HackerNewsItem>($"item/{id}.json", Options.StoryCacheSeconds, cancellationToken);
    }

    private async Task<T?> GetCachedAsync<T>(string path, int cacheSeconds, CancellationToken cancellationToken) where T : class
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (Cache.TryGetValue(path, out T? cached))
            return cached;

        Lazy<Task<object?>> pending = InFlight.GetOrAdd(path, _ => new(() => RefreshAsync<T>(path, cacheSeconds)));
        // A disconnected caller stops waiting, but cannot cancel work needed by other callers.
        return (T?)await pending.Value.WaitAsync(cancellationToken);
    }

    private async Task<object?> RefreshAsync<T>(string path, int cacheSeconds) where T : class
    {
        try
        {
            if (Cache.TryGetValue(path, out T? cached))
                return cached;

            T? value = await SendAsync<T>(path);

            if (value is null && typeof(T) == typeof(long[]))
                throw new HttpRequestException("Hacker News returned a null story list.");

            // Cache missing items too, to avoid repeatedly fetching unavailable stories.
            Cache.Set(path, value, TimeSpan.FromSeconds(cacheSeconds));
            return value;
        }
        finally
        {
            InFlight.TryRemove(path, out _);
        }
    }

    private async Task<T?> SendAsync<T>(string path)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(Stopping);
        deadline.CancelAfter(TimeSpan.FromSeconds(Options.FetchTimeoutSeconds));
        CancellationToken token = deadline.Token;
        try
        {
            // The deadline includes queueing, retry delays, headers and body consumption.
            await UpstreamGate.WaitAsync(token);

            try
            {
                using HttpClient client = ClientFactory.CreateClient(HttpClientName);

                for (int attempt = 0; ; attempt++)
                {
                    TimeSpan delay = TimeSpan.FromMilliseconds(150 * Math.Pow(2, attempt) + Random.Shared.Next(25, 125));

                    try
                    {
                        using HttpResponseMessage response = await client.GetAsync(path, HttpCompletionOption.ResponseHeadersRead, token);

                        if (response.IsSuccessStatusCode)
                            return await response.Content.ReadFromJsonAsync<T>(cancellationToken: token);

                        if (!IsTransient(response.StatusCode) || attempt >= Options.RetryCount)
                            throw new HttpRequestException($"Hacker News returned {(int)response.StatusCode}.", null, response.StatusCode);

                        TimeSpan? retryAfter = response.Headers.RetryAfter?.Delta ?? (response.Headers.RetryAfter?.Date - DateTimeOffset.UtcNow);

                        if (retryAfter > delay)
                            delay = retryAfter.Value;
                    }
                    catch (IOException exception)
                    {
                        if (attempt >= Options.RetryCount)
                        {
                            throw new HttpRequestException("Failed to read the Hacker News response body.", exception);
                        }
                    }
                    catch (HttpRequestException exception) when (attempt < Options.RetryCount &&
                          (exception.StatusCode is null || IsTransient(exception.StatusCode.Value)))
                    {
                        // Transport errors and transient statuses only; permanent failures escape.
                    }
                    await Task.Delay(delay, token);
                }
            }
            finally
            {
                UpstreamGate.Release();
            }
        }
        catch (OperationCanceledException exception) when (!Stopping.IsCancellationRequested)
        {
            throw new TimeoutException("Hacker News fetch exceeded its deadline.", exception);
        }
        catch (JsonException exception)
        {
            throw new HttpRequestException("Hacker News returned invalid JSON.", exception);
        }
    }

    private static bool IsTransient(HttpStatusCode statusCode) 
    { 
        return statusCode is HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests || (int)statusCode >= 500;    
    }

    public void Dispose() => UpstreamGate.Dispose();
}