using Microsoft.Extensions.Options;
using Santander.HackerNews.Api;
using Santander.HackerNews.Api.Clients;
using Santander.HackerNews.Api.Options;
using Santander.HackerNews.Api.Services;

WebApplicationBuilder builder = WebApplication.CreateBuilder(args);

builder.Services.AddProblemDetails();
builder.Services.AddMemoryCache();
builder.Services.Configure<HackerNewsOptions>(builder.Configuration.GetSection(HackerNewsOptions.SectionName));

builder.Services.AddExceptionHandler<GlobalExceptionHandler>();
builder.Services.AddControllers();

builder.Services.AddHttpClient<IHackerNewsClient, HackerNewsClient>((sp, client) =>
{
    HackerNewsOptions options = sp.GetRequiredService<IOptions<HackerNewsOptions>>().Value;
    client.BaseAddress = new Uri(options.BaseUrl);
    client.Timeout = TimeSpan.FromSeconds(8);
    client.DefaultRequestHeaders.UserAgent.ParseAdd("Santander-HackerNews-Coding-Test/1.0");
});

builder.Services.AddScoped<IBestStoriesService, BestStoriesService>();

WebApplication app = builder.Build();

app.UseExceptionHandler();
app.MapControllers();

app.Run();

public partial class Program;