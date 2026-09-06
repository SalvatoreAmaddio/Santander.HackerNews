using System.Net;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using Santander.HackerNews.Api.Models;
using Santander.HackerNews.Api.Options;

namespace Santander.HackerNews.Api.Clients;

public sealed class HackerNewsClient : IHackerNewsClient, IDisposable
{
    private const string BestStoriesCacheKey = "hn:beststories";
    private readonly HttpClient Client;
    private readonly IMemoryCache Cache;
    private readonly HackerNewsOptions Options;
    private readonly SemaphoreSlim UpstreamGate;
    private readonly SemaphoreSlim RankingRefreshLock = new(1, 1);

    public HackerNewsClient(HttpClient httpClient, IMemoryCache cache, IOptions<HackerNewsOptions> options)
    {
        Client = httpClient;
        Cache = cache;
        Options = options.Value;
        UpstreamGate = new SemaphoreSlim(Options.MaxConcurrentUpstreamRequests, Options.MaxConcurrentUpstreamRequests);
    }

    public async Task<IReadOnlyList<long>> GetBestStoryIdsAsync(CancellationToken cancellationToken)
    {
        if (Cache.TryGetValue(BestStoriesCacheKey, out IReadOnlyList<long>? cached) && cached is not null)
            return cached;

        await RankingRefreshLock.WaitAsync(cancellationToken);

        try
        {
            if (Cache.TryGetValue(BestStoriesCacheKey, out cached) && cached is not null)
                return cached;

            IReadOnlyList<long> ids = await SendAsync<IReadOnlyList<long>>("beststories.json", cancellationToken) ?? [];
            Cache.Set(BestStoriesCacheKey, ids, TimeSpan.FromSeconds(Options.RankingCacheSeconds));
            return ids;
        }
        finally
        {
            RankingRefreshLock.Release();
        }
    }

    public async Task<HackerNewsItem?> GetItemAsync(long id, CancellationToken cancellationToken)
    {
        string key = $"hn:item:{id}";

        if (Cache.TryGetValue(key, out HackerNewsItem? cached))
            return cached;

        HackerNewsItem? item = await SendAsync<HackerNewsItem>($"item/{id}.json", cancellationToken);

        if (item is not null)
            Cache.Set(key, item, TimeSpan.FromSeconds(Options.StoryCacheSeconds));

        return item;
    }

    private async Task<T?> SendAsync<T>(string path, CancellationToken cancellationToken)
    {
        await UpstreamGate.WaitAsync(cancellationToken);

        try
        {
            for (int attempt = 0; ; attempt++)
            {
                try
                {
                    using HttpResponseMessage response = await Client.GetAsync(path, HttpCompletionOption.ResponseHeadersRead, cancellationToken);

                    if (response.IsSuccessStatusCode)
                        return await response.Content.ReadFromJsonAsync<T>(cancellationToken: cancellationToken);

                    if (!IsTransient(response.StatusCode) || attempt >= Options.RetryCount)
                        throw new HttpRequestException($"Hacker News returned {(int)response.StatusCode} ({response.StatusCode}).", null, response.StatusCode);
                }
                catch (HttpRequestException) when (attempt < Options.RetryCount)
                {
                    // Retry below. Cancellation is deliberately not swallowed.
                }

                TimeSpan delay = TimeSpan.FromMilliseconds(150 * Math.Pow(2, attempt) + Random.Shared.Next(25, 125));

                await Task.Delay(delay, cancellationToken);
            }
        }
        finally
        {
            UpstreamGate.Release();
        }
    }

    private static bool IsTransient(HttpStatusCode statusCode)
    {
        return statusCode is HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests || (int)statusCode >= 500;
    }

    public void Dispose()
    {
        UpstreamGate.Dispose();
        RankingRefreshLock.Dispose();
    }
}