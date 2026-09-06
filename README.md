# Santander Hacker News API

ASP.NET Core (.NET 8) API returning the best `n` Hacker News stories in descending score order.

## Run

Install the .NET 8 SDK (or a compatible newer SDK with the .NET 8 runtime), then from the repository root:

```bash
dotnet restore
dotnet run --project src/Santander.HackerNews.Api --no-launch-profile --urls http://localhost:5000
```

```bash
curl http://localhost:5000/api/v1/stories/best/5
```

Alternatively, open `Santander.HackerNews.Api.sln` in Visual Studio and run the API project using its configured development ports.

The response is a JSON array:

```json
[
  {
    "title": "A uBlock Origin update was rejected from the Chrome Web Store",
    "uri": "https://github.com/uBlockOrigin/uBlock-issues/issues/745",
    "postedBy": "ismaildonmez",
    "time": "2019-10-12T13:43:01+00:00",
    "score": 1716,
    "commentCount": 572
  }
]
```

`n` must be a positive 32-bit integer. Zero, negatives, nonintegers and overflowing values return HTTP 400. There is no arbitrary result cap: if fewer valid stories exist, all available valid stories are returned.

## API versioning

The API uses URL-segment versioning through `Asp.Versioning.Mvc`:

```http
GET /api/v1/stories/best/5
```

Version `1.0` is currently supported (`v1` and `v1.0` are equivalent). Responses report supported versions in the `api-supported-versions` header. The version must be supplied in the URL; the previous unversioned route `/api/stories/best/5` and unsupported versions return 404. Update existing callers to use `/api/v1/stories/best/{n}`.

Breaking contract changes should introduce a new explicitly declared API version, allowing existing clients to keep using v1. Versioning is configured for controllers with `AddApiVersioning(...).AddMvc()` and `[ApiVersion(1.0)]`; query parameters and headers do not select versions.

## Design and upstream protection

The controller validates requests, the service ranks and maps stories, and the client manages HTTP communication and caching. All candidate IDs are fetched before taking `n`: the upstream list must not be assumed to be sorted by score.

The client is a singleton with one semaphore limiting upstream fetches across all incoming requests in this application instance. An in-flight task per resource coalesces simultaneous cache misses, including ranking-list refreshes. Completed in-flight entries are removed. Successful responses and missing items are cached; failed fetches are not cached as successful data.

The singleton uses a named `IHttpClientFactory` client created per fetch, preserving handler rotation rather than retaining a typed client indefinitely. Each fetch has a deadline covering semaphore queueing, HTTP headers, response-body reads and retries. Transient HTTP statuses (408, 429, 5xx) and transport failures are retried with exponential backoff and jitter; permanent HTTP failures are not. `Retry-After` is honored within the overall deadline. A slot stays reserved during retry backoff, keeping retrying work bounded.

Caller cancellation ends that caller's wait without cancelling shared work needed by other callers. Shared work can finish and populate the cache; it is bounded by its deadline and cancelled when the application stops.

## Configuration

Settings are in `src/Santander.HackerNews.Api/appsettings.json`; environment variables use the `HackerNews__` prefix, e.g. `HackerNews__MaxConcurrentUpstreamRequests=8`.

| Setting | Default | Meaning |
| --- | --- | --- |
| BaseUrl | https://hacker-news.firebaseio.com/v0/ | Absolute HTTP(S) URL with trailing slash |
| MaxConcurrentUpstreamRequests | 12 | Application-wide concurrent fetch limit (1–100) |
| RankingCacheSeconds | 20 | Candidate-list cache lifetime |
| StoryCacheSeconds | 60 | Item cache lifetime, including missing items |
| RetryCount | 2 | Additional attempts (0–5) |
| FetchTimeoutSeconds | 30 | Total per-resource deadline, including queueing (greater than 0, at most 300) |

Invalid settings fail application startup. Both cache lifetimes must be positive.

## Assumptions and failure behavior

- The candidate set is the Hacker News `beststories` list, not every story on Hacker News.
- Missing, dead, deleted and non-story items are excluded. Scores are sorted descending, then creation time descending for ties; remaining ties retain candidate order.
- Missing URLs are returned as `null` (for example text-only stories); missing titles/authors become empty strings and missing comment counts become zero. Timestamps are converted from Unix seconds to UTC.
- Results reflect independently cached items, not an atomic live snapshot. Scores may be up to the item cache lifetime old; the candidate list has its own lifetime. Cold requests fetch every candidate even when `n=1`.
- Any failed candidate fetch fails the request rather than silently returning an incomplete ranking. Upstream HTTP/transport errors and invalid JSON return 503; fetch deadlines return 504. Unexpected internal failures return 500. Error responses use Problem Details and omit internal exception details.
- A null candidate list is treated as an upstream failure; an empty list returns `[]`.
- Cache and concurrency protection are per process. Multiple replicas multiply the total upstream concurrency limit.

## Tests

```bash
dotnet test Santander.HackerNews.Api.sln --configuration Release
```

Tests use controlled upstream HTTP handlers without accessing Hacker News. They cover service ranking/filtering/mapping, the HTTP JSON contract, invalid inputs, concurrent requests through real DI registrations, warm-cache reuse, cache expiry and missing-item caching, cancellation isolation, retry counts and recovery, stalled response bodies, invalid upstream responses and startup validation.

## Enhancements with more time

- Measure cold/warm latency and allocations under sustained load, and tune concurrency/cache lifetimes against an explicit latency and freshness budget.
- For multiple replicas, coordinate refreshes and rate limits across instances or use a dedicated refresh worker with a shared snapshot.
- Consider a bounded stale-on-error snapshot and circuit breaker for prolonged outages; define acceptable staleness with the API consumer first.
- Add metrics for cache hits, coalesced fetches, queue time, upstream attempts and failures, plus an automated CI build/test workflow.
- Consider caching the sorted snapshot to reduce repeated sorting under very high warm-cache traffic.

Upstream documentation: https://github.com/HackerNews/API

## High-volume integration test

Run the load regression test with timing output:

```bash
dotnet test Santander.HackerNews.Api.sln --configuration Release --filter "Category=Load" --logger "console;verbosity=detailed"
```

It sends 3,000 HTTP requests through the in-memory ASP.NET Core test host, with up to 100 concurrent callers and 200 simulated candidate stories. Three phases exercise a cold cache, a warm cache, and a refresh after explicit cache invalidation. Every response must succeed and contain the requested number of correctly ranked stories. The test asserts exactly 201 upstream calls on cold load, no additional calls on warm load, and 201 more after invalidation, with upstream concurrency never exceeding 12. The fake upstream introduces a 5 ms asynchronous delay and never calls Hacker News.

Output includes elapsed time, observed requests/second, p95/p99 latency, upstream calls and peak upstream concurrency. Timing includes reading and checking responses. These timings are diagnostic, not production throughput claims: the generator, application and fake upstream share one process, and traffic is bounded-concurrency rather than a fixed arrival rate. No machine-dependent latency threshold is asserted; a two-minute cancellation deadline guards against hangs. Cache lifetimes are extended for this test so each phase measures the intended cache state. The separate expiry test checks actual TTL expiration.

The test also runs with the full suite. Use `--filter "Category!=Load"` to omit it. For deployment capacity, use an external load generator against Kestrel on representative hardware and measure CPU, memory and latency under sustained traffic.
