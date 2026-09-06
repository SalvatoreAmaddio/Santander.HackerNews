using Santander.HackerNews.Api.Models;

namespace Santander.HackerNews.Api.Clients;

public interface IHackerNewsClient
{
    Task<IReadOnlyList<long>> GetBestStoryIdsAsync(CancellationToken cancellationToken);
    Task<HackerNewsItem?> GetItemAsync(long id, CancellationToken cancellationToken);
}