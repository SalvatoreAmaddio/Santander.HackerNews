using Microsoft.AspNetCore.Mvc;
using Santander.HackerNews.Api.Models;
using Santander.HackerNews.Api.Services;

namespace Santander.HackerNews.Api.Controller;

[ApiController]
[Route("api/stories")]
public sealed class StoriesController(IBestStoriesService service) : ControllerBase
{
    private readonly IBestStoriesService StoriesService = service;

    [HttpGet("best/{n:int}")]
    public async Task<ActionResult<IReadOnlyList<StoryResponse>>> GetBestStoriesAsync([FromRoute] int n, CancellationToken cancellationToken)
    {
        if (n <= 0)
        {
            ModelState.AddModelError(nameof(n), "n must be greater than 0.");
            return ValidationProblem(ModelState);
        }

        IReadOnlyList<StoryResponse> stories = await StoriesService.GetBestStoriesAsync(n, cancellationToken);

        return Ok(stories);
    }
}