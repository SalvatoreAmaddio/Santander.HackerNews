using System.Text.Json.Serialization;

namespace Santander.HackerNews.Api.Models;

public sealed record StoryResponse(
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("uri")] string? Uri,
    [property: JsonPropertyName("postedBy")] string PostedBy,
    [property: JsonPropertyName("time")] DateTimeOffset Time,
    [property: JsonPropertyName("score")] int Score,
    [property: JsonPropertyName("commentCount")] int CommentCount);
