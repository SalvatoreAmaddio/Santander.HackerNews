namespace Santander.HackerNews.Api.Options;

public sealed class HackerNewsOptions
{
    public const string SectionName = "HackerNews";
    public string BaseUrl { get; init; } = "https://hacker-news.firebaseio.com/v0/";
    public int MaxConcurrentUpstreamRequests { get; init; } = 12;
    public int StoryCacheSeconds { get; init; } = 60;
    public int RankingCacheSeconds { get; init; } = 20;
    public int RetryCount { get; init; } = 2;
    public double FetchTimeoutSeconds { get; init; } = 30;
}