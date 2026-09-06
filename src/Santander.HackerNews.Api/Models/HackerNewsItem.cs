using System.Text.Json.Serialization;

namespace Santander.HackerNews.Api.Models;

public sealed record HackerNewsItem(
    [property: JsonPropertyName("id")] long Id,
    [property: JsonPropertyName("title")] string? Title,
    [property: JsonPropertyName("url")] string? Url,
    [property: JsonPropertyName("by")] string? By,
    [property: JsonPropertyName("time")] long Time,
    [property: JsonPropertyName("score")] int Score,
    [property: JsonPropertyName("descendants")] int? Descendants,
    [property: JsonPropertyName("type")] string? Type,
    [property: JsonPropertyName("deleted")] bool? Deleted,
    [property: JsonPropertyName("dead")] bool? Dead);
