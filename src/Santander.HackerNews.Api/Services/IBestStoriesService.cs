using Santander.HackerNews.Api.Models;

namespace Santander.HackerNews.Api.Services;

public interface IBestStoriesService
{
    Task<IReadOnlyList<StoryResponse>> GetBestStoriesAsync(int count, CancellationToken cancellationToken);
}
