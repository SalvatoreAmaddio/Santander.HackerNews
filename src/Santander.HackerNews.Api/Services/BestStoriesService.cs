using Santander.HackerNews.Api.Clients;
using Santander.HackerNews.Api.Models;

namespace Santander.HackerNews.Api.Services;

public sealed class BestStoriesService(IHackerNewsClient client) : IBestStoriesService
{
    public async Task<IReadOnlyList<StoryResponse>> GetBestStoriesAsync(int count, CancellationToken cancellationToken)
    {
        IReadOnlyList<long> ids = await client.GetBestStoryIdsAsync(cancellationToken);

        IEnumerable<Task<HackerNewsItem?>> tasks = ids.Select(id => client.GetItemAsync(id, cancellationToken));

        HackerNewsItem?[] items = await Task.WhenAll(tasks);

        return items.Where(item => item is { Type: "story", Deleted: not true, Dead: not true })
                    .OrderByDescending(static item => item!.Score)
                    .ThenByDescending(static item => item!.Time)
                    .Take(count)
                    .Select(item => new StoryResponse(
                            item!.Title ?? string.Empty,
                            item.Url,
                            item.By ?? string.Empty,
                            DateTimeOffset.FromUnixTimeSeconds(item.Time),
                            item.Score,
                            item.Descendants ?? 0))
                    .ToArray();
    }
}