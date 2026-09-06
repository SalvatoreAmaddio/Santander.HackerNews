using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using Santander.HackerNews.Api.Clients;
using Santander.HackerNews.Api.Options;

namespace Santander.HackerNews.Api.Tests;

public sealed class ApiTests
{
    [Fact]
    public async Task ConcurrentRequests_ShareColdFetches_AndRespectGlobalLimit()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int active = 0, maximum = 0, calls = 0;
        using var factory = new ApiFactory(async (request, token) =>
        {
            Interlocked.Increment(ref calls);
            if (request.RequestUri!.AbsolutePath.EndsWith("beststories.json"))
                return Json(new[] { 1, 2, 3, 4 });
            int current = Interlocked.Increment(ref active);
            int seen;
            do { seen = maximum; } while (current > seen && Interlocked.CompareExchange(ref maximum, current, seen) != seen);
            if (current == 2) entered.TrySetResult();
            try
            {
                await release.Task.WaitAsync(token);
                int id = int.Parse(request.RequestUri.Segments[^1].Replace(".json", ""));
                return Json(new { id, type = "story", title = $"Story {id}", url = "https://example.com", by = "author", time = 1570887781, score = id * 10, descendants = 7 });
            }
            finally { Interlocked.Decrement(ref active); }
        });
        using HttpClient client = factory.CreateClient();
        Task<HttpResponseMessage>[] requests = Enumerable.Range(0, 30).Select(_ => client.GetAsync("/api/stories/best/2")).ToArray();
        try { await entered.Task.WaitAsync(TimeSpan.FromSeconds(10)); }
        finally { release.TrySetResult(); }
        HttpResponseMessage[] responses = await Task.WhenAll(requests);
        foreach (HttpResponseMessage response in responses)
        {
            using (response)
            {
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                using JsonDocument body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
                Assert.Equal(2, body.RootElement.GetArrayLength());
                JsonElement story = body.RootElement[0];
                Assert.Equal(40, story.GetProperty("score").GetInt32());
                Assert.Equal(new[] { "commentCount", "postedBy", "score", "time", "title", "uri" }, story.EnumerateObject().Select(p => p.Name).Order());
                Assert.Equal("author", story.GetProperty("postedBy").GetString());
                Assert.Equal(7, story.GetProperty("commentCount").GetInt32());
                Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1570887781), story.GetProperty("time").GetDateTimeOffset());
            }
        }
        using var warm = await client.GetAsync("/api/stories/best/4");
        Assert.Equal(HttpStatusCode.OK, warm.StatusCode);
        Assert.Equal(5, calls);
        Assert.Equal(2, maximum);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("abc")]
    [InlineData("2147483648")]
    public async Task InvalidCount_Returns400_WithoutUpstreamCalls(string count)
    {
        int calls = 0;
        using var factory = new ApiFactory((_, _) => { calls++; return Task.FromResult(Json(Array.Empty<int>())); });
        using var client = factory.CreateClient();
        using var response = await client.GetAsync($"/api/stories/best/{count}");
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(0, calls);
    }

    [Theory]
    [InlineData(404, 1)]
    [InlineData(401, 1)]
    [InlineData(429, 3)]
    [InlineData(503, 3)]
    public async Task FailedStatuses_UseExpectedRetryCount(int status, int expectedCalls)
    {
        int calls = 0;
        using var factory = new ApiFactory((_, _) =>
        {
            Interlocked.Increment(ref calls);
            return Task.FromResult(new HttpResponseMessage((HttpStatusCode)status));
        });
        using var client = factory.CreateClient();
        using var response = await client.GetAsync("/api/stories/best/1");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType!.MediaType);
        Assert.Equal(expectedCalls, calls);
    }

    [Fact]
    public async Task TransportFailure_CanRecoverOnRetry()
    {
        int calls = 0;
        using var factory = new ApiFactory((_, _) =>
        {
            if (Interlocked.Increment(ref calls) == 1) throw new HttpRequestException("connection reset");
            return Task.FromResult(Json(Array.Empty<int>()));
        });
        using var client = factory.CreateClient();
        using var response = await client.GetAsync("/api/stories/best/1");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task StalledBody_IsBoundedByDeadline()
    {
        using var factory = new ApiFactory((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        { Content = new StreamContent(new StalledStream()) }), timeout: 0.2);
        using var client = factory.CreateClient();
        using var response = await client.GetAsync("/api/stories/best/1").WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(HttpStatusCode.GatewayTimeout, response.StatusCode);
    }

    [Fact]
    public async Task CancelledWaiter_DoesNotCancelSharedFetch()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int calls = 0;
        using var factory = new ApiFactory(async (_, token) =>
        {
            Interlocked.Increment(ref calls);
            entered.TrySetResult();
            await release.Task.WaitAsync(token);
            return Json(new[] { 1 });
        });
        using var scope1 = factory.Services.CreateScope();
        using var scope2 = factory.Services.CreateScope();
        var first = scope1.ServiceProvider.GetRequiredService<IHackerNewsClient>();
        var second = scope2.ServiceProvider.GetRequiredService<IHackerNewsClient>();
        using var cancellation = new CancellationTokenSource();
        var abandoned = first.GetBestStoryIdsAsync(cancellation.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var survivor = second.GetBestStoryIdsAsync(CancellationToken.None);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => abandoned);
        release.TrySetResult();
        Assert.Equal(new long[] { 1 }, await survivor);
        Assert.Equal(1, calls);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("invalid json")]
    public async Task InvalidRanking_IsNotCachedAsSuccess(string body)
    {
        int calls = 0;
        using var factory = new ApiFactory((_, _) => Task.FromResult(
            Interlocked.Increment(ref calls) == 1
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json") }
                : Json(Array.Empty<int>())));
        using var client = factory.CreateClient();
        using var failed = await client.GetAsync("/api/stories/best/1");
        using var recovered = await client.GetAsync("/api/stories/best/1");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, failed.StatusCode);
        Assert.Equal(HttpStatusCode.OK, recovered.StatusCode);
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task ExpiredEntries_AreRefreshed_AndMissingItemsAreCached()
    {
        int calls = 0;
        using var factory = new ApiFactory((_, _) =>
        {
            Interlocked.Increment(ref calls);
            return Task.FromResult(Json<object?>(null));
        }, cacheSeconds: 1);
        var client = factory.Services.GetRequiredService<IHackerNewsClient>();
        Assert.Null(await client.GetItemAsync(1, CancellationToken.None));
        Assert.Null(await client.GetItemAsync(1, CancellationToken.None));
        Assert.Equal(1, calls);
        await Task.Delay(TimeSpan.FromMilliseconds(1100));
        await Task.WhenAll(Enumerable.Range(0, 20).Select(_ => client.GetItemAsync(1, CancellationToken.None)));
        Assert.Equal(2, calls);
    }

    [Fact]
    public void InvalidConfiguration_FailsAtStartup()
    {
        using var factory = new ApiFactory((_, _) => Task.FromResult(Json(Array.Empty<int>())), concurrency: 0);
        Assert.Throws<OptionsValidationException>(() => factory.CreateClient());
    }
    private static HttpResponseMessage Json<T>(T value) => new(HttpStatusCode.OK) { Content = JsonContent.Create(value) };

    private sealed class ApiFactory(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send, double timeout = 30, int cacheSeconds = 60, int concurrency = 2)
        : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["HackerNews:MaxConcurrentUpstreamRequests"] = concurrency.ToString(),
                ["HackerNews:FetchTimeoutSeconds"] = timeout.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["HackerNews:RankingCacheSeconds"] = cacheSeconds.ToString(),
                ["HackerNews:StoryCacheSeconds"] = cacheSeconds.ToString()
            }));
            builder.ConfigureServices(services =>
            {
                services.AddHttpClient(HackerNewsClient.HttpClientName).ConfigurePrimaryHttpMessageHandler(() => new Handler(send));
            });
        }
    }

    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(request, cancellationToken);
    }

    private sealed class StalledStream : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        { await Task.Delay(Timeout.Infinite, cancellationToken); return 0; }
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) => ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}

