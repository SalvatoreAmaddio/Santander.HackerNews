# Santander Developer Coding Test — Hacker News Best Stories API

ASP.NET Core REST API that returns the best `n` Hacker News stories, ordered by **score descending**.

## Run

Requires the .NET 8 SDK.

```bash
dotnet restore
dotnet run --project src/Santander.HackerNews.Api
```

Then call (use the host/port printed by ASP.NET Core):

```text
GET /api/stories/best?n=10
```

Health endpoint:

```text
GET /health
```

## Why the implementation is slightly more defensive than the minimum

The Hacker News v0 API currently documents **no server-side rate limit**, but that does not mean a consumer should generate unlimited traffic. The exercise explicitly asks the API to efficiently service large numbers of requests without risking overload of Hacker News, so this implementation protects both sides.

### 1. Cache

- `/beststories` IDs: 20 seconds.
- Individual items: 60 seconds.
- A lock around ranking refresh prevents a cache stampede when many callers arrive immediately after expiry.
- Item caching matters most: Hacker News exposes item details one ID at a time, so without it every caller could fan out into hundreds of upstream calls.

### 2. Bounded outbound concurrency

At most 12 Hacker News calls are in flight per API instance. The service can create tasks for all candidate IDs, but the client-side `SemaphoreSlim` is the actual bulkhead. This avoids an uncontrolled fan-out against Firebase/Hacker News.

### 3. Short retries with exponential backoff + jitter

Transient failures (408, 429 and 5xx, plus transient `HttpRequestException`s) get two retries. Delays use exponential backoff plus jitter, reducing synchronized retry storms. Cancellation is propagated rather than swallowed.

### 4. Timeout

The upstream `HttpClient` has an 8-second timeout so requests do not occupy resources indefinitely when Hacker News is unhealthy.

### 5. Inbound rate limiting

A per-client-IP token bucket protects this API from abusive/high-volume callers: burst capacity 30, replenishing 15 requests every 10 seconds, with a very small queue. Excess traffic receives HTTP 429.

### 6. Input bounds

`n` must be 1–100. This prevents a caller from using the endpoint as an amplification mechanism. The limit is configuration-driven.

### 7. Failure semantics

Upstream HTTP failures become HTTP 503 rather than a misleading 500. Invalid input is returned as RFC-style validation problem details.

## Important interpretation of “best n stories”

The official API supplies IDs through `/v0/beststories.json`, but the brief says the returned stories must be the best `n` **as determined by their score** and sorted in descending score order. Therefore this solution treats `beststories` as the candidate set, loads the candidate items, filters unusable/dead/deleted entries, explicitly sorts by `score DESC` (then time as a deterministic tie-break), and only then takes `n`.

This is intentionally different from simply taking the first `n` IDs and sorting those: that shortcut assumes the `beststories` ordering is exactly equivalent to numeric score ordering, which is not guaranteed by the documented item model/brief.

## Hacker News API assumptions

Based on the official Hacker News API documentation:

- API version is currently `/v0/`.
- Stories/items are fetched individually at `/v0/item/<id>.json`.
- `beststories` supplies the best-story candidate IDs.
- Item fields are not all universally present. `url`, `descendants`, etc. are therefore handled defensively.
- Clients should tolerate additional fields in future API responses. `System.Text.Json` naturally ignores unknown properties by default.
- The API currently documents no rate limit, but this solution deliberately imposes its own outbound bulkhead and caching.

Official documentation: https://github.com/HackerNews/API

## Response shape

```json
[
  {
    "title": "Example story",
    "uri": "https://example.com/story",
    "postedBy": "someuser",
    "time": "2026-09-05T05:00:00+00:00",
    "score": 718,
    "commentCount": 572
  }
]
```

`time` is exposed as an ISO-8601 timestamp. Hacker News supplies Unix seconds; converting at the API boundary gives callers an unambiguous typed date/time rather than leaking the upstream wire representation.

## Tests

```bash
dotnet test
```

Tests cover score ordering/limit, filtering dead/deleted/non-story items, and mapping of Unix time/null comment counts.

## Production improvements with more time

1. **Distributed cache (Redis)** — `IMemoryCache` is per-process. With multiple API replicas, Redis would prevent every replica from independently refreshing the same Hacker News items.
2. **Stale-while-revalidate** — retain a slightly stale snapshot and serve it when HN is temporarily unavailable while one worker refreshes it. For a read-only news endpoint, availability is often preferable to failing every request.
3. **Framework resilience pipeline** — use `Microsoft.Extensions.Http.Resilience` / Polly for standardized retry, circuit breaker, timeout and telemetry policies. The submission keeps dependencies deliberately small and the retry policy explicit.
4. **Observability** — OpenTelemetry traces/metrics, cache hit ratio, upstream latency, retry count, 429 count and dependency failures.
5. **Configuration validation** — validate option ranges on startup and fail fast on invalid deployment configuration.
6. **Integration/load tests** — use a fake HTTP handler/WireMock and a load tool such as k6 to verify the bulkhead and cache-stampede behavior under concurrency.
7. **Output caching** — short ASP.NET output caching keyed by `n` could remove even service-layer work for hot requests. Individual upstream caching remains necessary because different `n` values overlap heavily.

## Trade-off

Fetching every ID returned by `beststories` is more upstream work on a cold cache than taking the first `n`. It is done to satisfy the brief literally: select by numeric score. The short item cache makes subsequent requests cheap. If Santander confirms that Hacker News guarantees `/beststories` is already ordered exactly by score, this can be optimized to fetch only the first `n` IDs.
