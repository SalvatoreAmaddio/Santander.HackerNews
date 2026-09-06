using Microsoft.Extensions.Options;
using Santander.HackerNews.Api;
using Santander.HackerNews.Api.Clients;
using Santander.HackerNews.Api.Options;
using Santander.HackerNews.Api.Services;

WebApplicationBuilder builder = WebApplication.CreateBuilder(args);

builder.Services.AddProblemDetails();
builder.Services.AddMemoryCache();
builder.Services.AddOptions<HackerNewsOptions>()
    .BindConfiguration(HackerNewsOptions.SectionName)
    .Validate(o => Uri.TryCreate(o.BaseUrl, UriKind.Absolute, out var uri)
        && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp)
        && o.BaseUrl.EndsWith('/'), "BaseUrl must be an absolute HTTP(S) URL ending in '/'.")
    .Validate(o => o.MaxConcurrentUpstreamRequests is > 0 and <= 100, "Concurrency must be between 1 and 100.")
    .Validate(o => o.StoryCacheSeconds > 0 && o.RankingCacheSeconds > 0, "Cache durations must be positive.")
    .Validate(o => o.RetryCount is >= 0 and <= 5, "RetryCount must be between 0 and 5.")
    .Validate(o => o.FetchTimeoutSeconds is > 0 and <= 300, "Fetch timeout must be between 0 and 300 seconds.")
    .ValidateOnStart();

builder.Services.AddExceptionHandler<GlobalExceptionHandler>();
builder.Services.AddControllers();

builder.Services.AddHttpClient(HackerNewsClient.HttpClientName, (sp, client) =>
{
    HackerNewsOptions options = sp.GetRequiredService<IOptions<HackerNewsOptions>>().Value;
    client.BaseAddress = new Uri(options.BaseUrl);
    client.Timeout = Timeout.InfiniteTimeSpan;
    client.DefaultRequestHeaders.UserAgent.ParseAdd("Santander-HackerNews-Coding-Test/1.0");
});

builder.Services.AddSingleton<IHackerNewsClient, HackerNewsClient>();
builder.Services.AddScoped<IBestStoriesService, BestStoriesService>();

WebApplication app = builder.Build();

app.UseExceptionHandler();
app.MapControllers();

app.Run();

public partial class Program;