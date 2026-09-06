using Santander.HackerNews.Api.Clients;
using Santander.HackerNews.Api.Models;
using Santander.HackerNews.Api.Services;

namespace Santander.HackerNews.Api.Tests;

public sealed class BestStoriesServiceTests
{
    [Fact]
    public async Task GetBestStoriesAsync_SortsByScoreDescending_AndTakesRequestedCount()
    {
        FakeClient client = new([1, 2, 3],
                                new Dictionary<long, HackerNewsItem?>
                                {
                                    [1] = Story(1, 10),
                                    [2] = Story(2, 99),
                                    [3] = Story(3, 50)
                                });

        BestStoriesService service = new(client);

        IReadOnlyList<StoryResponse> result = await service.GetBestStoriesAsync(2, CancellationToken.None);

        Assert.Equal([99, 50], result.Select(x => x.Score));
    }

    [Fact]
    public async Task GetBestStoriesAsync_IgnoresDeadDeletedAndNonStoryItems()
    {
        FakeClient client = new([1, 2, 3, 4],
                                new Dictionary<long, HackerNewsItem?>
                                {
                                    [1] = Story(1, 100) with { Dead = true },
                                    [2] = Story(2, 90) with { Deleted = true },
                                    [3] = Story(3, 80) with { Type = "job" },
                                    [4] = Story(4, 70)
                                });

        BestStoriesService service = new(client);
        IReadOnlyList<StoryResponse> result = await service.GetBestStoriesAsync(10, CancellationToken.None);

        Assert.Single(result);
        Assert.Equal(70, result[0].Score);
    }

    [Fact]
    public async Task GetBestStoriesAsync_MapsUnixTimeAndNullCommentCount()
    {
        HackerNewsItem item = Story(1, 10) with { Time = 1_700_000_000, Descendants = null };
        BestStoriesService service = new(new FakeClient([1], new Dictionary<long, HackerNewsItem?> { [1] = item }));

        IReadOnlyList<StoryResponse> result = await service.GetBestStoriesAsync(1, CancellationToken.None);

        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1_700_000_000), result[0].Time);
        Assert.Equal(0, result[0].CommentCount);
    }

    private static HackerNewsItem Story(long id, int score) =>
        new(id, $"Story {id}", $"https://example.com/{id}", "author", 1_700_000_000, score, 3, "story", false, false);

    private sealed class FakeClient(IReadOnlyList<long> ids, IReadOnlyDictionary<long, HackerNewsItem?> items) : IHackerNewsClient
    {
        public Task<IReadOnlyList<long>> GetBestStoryIdsAsync(CancellationToken cancellationToken) => Task.FromResult(ids);
        public Task<HackerNewsItem?> GetItemAsync(long id, CancellationToken cancellationToken) => Task.FromResult(items[id]);
    }
}
