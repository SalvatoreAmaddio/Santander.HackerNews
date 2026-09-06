using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Santander.HackerNews.Api.Models;
using Xunit.Abstractions;

namespace Santander.HackerNews.Api.Tests;

public sealed class LoadTests(ITestOutputHelper output)
{
    [Fact]
    [Trait("Category", "Load")]
    public async Task ThousandsOfRequests_ReturnCorrectResults_WithoutMultiplyingUpstreamTraffic()
    {
        const int storyCount = 200;
        const int upstreamLimit = 12;
        const int requestsPerPhase = 1_000;
        const int concurrentCallers = 100;
        ConcurrentDictionary<string, int> upstreamCalls = new();
        int active = 0, peak = 0;

        using ApiTests.ApiFactory factory = new(async (request, token) =>
        {
            string path = request.RequestUri!.AbsolutePath;
            upstreamCalls.AddOrUpdate(path, 1, (_, count) => count + 1);
            int current = Interlocked.Increment(ref active);
            int previous;

            do { previous = Volatile.Read(ref peak); }
            while (current > previous && Interlocked.CompareExchange(ref peak, current, previous) != previous);

            try
            {
                await Task.Delay(5, token);

                if (path.EndsWith("beststories.json", StringComparison.Ordinal))
                    return new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(Enumerable.Range(1, storyCount).ToArray()) };

                int id = int.Parse(request.RequestUri.Segments[^1].Replace(".json", ""));

                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = JsonContent.Create(new
                    {
                        id, type = "story", title = $"Story {id}", url = $"https://example.com/{id}",
                        by = "author", time = 1_700_000_000, score = id, descendants = id
                    })
                };
            }
            finally { Interlocked.Decrement(ref active); }
        }, timeout: 120, cacheSeconds: 600, concurrency: upstreamLimit);

        using HttpClient client = factory.CreateClient();

        using CancellationTokenSource deadline = new(TimeSpan.FromMinutes(2));

        await RunPhaseAsync("Cold cache");
        AssertUpstreamCalls(expectedPerResource: 1);

        await RunPhaseAsync("Warm cache");
        AssertUpstreamCalls(expectedPerResource: 1);

        // Deterministic invalidation exercises a refresh burst without waiting for wall-clock expiry.
        MemoryCache cache = Assert.IsType<MemoryCache>(factory.Services.GetRequiredService<IMemoryCache>());
        cache.Compact(1.0);

        await RunPhaseAsync("After cache invalidation");

        AssertUpstreamCalls(expectedPerResource: 2);

        Assert.Equal(0, Volatile.Read(ref active));
        Assert.InRange(Volatile.Read(ref peak), 1, upstreamLimit);

        output.WriteLine($"Total: {requestsPerPhase * 3:N0} API requests; {upstreamCalls.Values.Sum()} upstream calls; peak upstream concurrency {peak}/{upstreamLimit}.");

        void AssertUpstreamCalls(int expectedPerResource)
        {
            Assert.Equal(storyCount + 1, upstreamCalls.Count);
            Assert.All(upstreamCalls.Values, count => Assert.Equal(expectedPerResource, count));
        }

        async Task RunPhaseAsync(string name)
        {
            double[] latencies = new double[requestsPerPhase];
            Stopwatch elapsed = Stopwatch.StartNew();

            await Parallel.ForEachAsync(Enumerable.Range(0, requestsPerPhase), new ParallelOptions
            {
                MaxDegreeOfParallelism = concurrentCallers,
                CancellationToken = deadline.Token
            }, async (index, token) =>
            {
                int requested = index % 20 + 1;
                long started = Stopwatch.GetTimestamp();
                using HttpResponseMessage response = await client.GetAsync($"/api/v1/stories/best/{requested}", token);
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                StoryResponse[]? stories = await response.Content.ReadFromJsonAsync<StoryResponse[]>(token);
                Assert.NotNull(stories);
                Assert.Equal(requested, stories.Length);
                Assert.Equal(Enumerable.Range(storyCount - requested + 1, requested).Reverse(), stories.Select(story => story.Score));
                Assert.All(stories, story => Assert.Equal($"Story {story.Score}", story.Title));
                latencies[index] = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            });

            elapsed.Stop();
            Array.Sort(latencies);

            output.WriteLine($"{name}: {requestsPerPhase:N0} requests, up to {concurrentCallers} callers; " +
                $"{elapsed.Elapsed.TotalSeconds:F2}s; {requestsPerPhase / elapsed.Elapsed.TotalSeconds:F0} requests/s; " +
                $"p95 {latencies[(int)Math.Ceiling(requestsPerPhase * .95) - 1]:F1}ms; " +
                $"p99 {latencies[(int)Math.Ceiling(requestsPerPhase * .99) - 1]:F1}ms.");
        }
    }
}