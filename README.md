# Santander Hacker News API

ASP.NET Core REST API that returns the best `n` stories from the Hacker News API, ordered by score in descending order.

## Requirements

- .NET 8 SDK

## Running the application

Clone the repository and run:

```bash
dotnet restore
dotnet run --project Santander.HackerNews.Api
```

Alternatively, open the solution in Visual Studio and run the API project.

## Usage

To retrieve the best stories:

```http
GET /api/stories/best/5
```

Example response:

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

`n` must be greater than zero.

## Implementation

The application uses the Hacker News `beststories` endpoint to retrieve candidate story IDs and then retrieves the details of each story.

Stories are ordered by score in descending order before the requested number of results is returned.

The application is separated into:

- **Controller** - handles the REST API request and validation.
- **Service** - handles story ranking and mapping.
- **HackerNewsClient** - handles communication with the Hacker News API.

## Performance

To avoid unnecessary load on the Hacker News API:

- Hacker News responses are cached in memory.
- The number of concurrent requests to Hacker News is limited.
- Transient HTTP failures are retried with a short backoff.
- HTTP requests have a timeout.
- Cancellation tokens are propagated through asynchronous operations.

This allows repeated requests to reuse cached data rather than repeatedly requesting the same information from Hacker News.

## Error Handling

Invalid values of `n` return HTTP `400 Bad Request`.

Failures when communicating with Hacker News are handled by the API and return an appropriate server error response.

## Tests

Run the tests with:

```bash
dotnet test
```

Unit tests cover the main story retrieval, ordering and mapping behaviour.

## Assumptions

Story IDs are retrieved from the Hacker News `beststories` endpoint. 
The corresponding story details are retrieved and the results are returned 
in descending order of score, limited to the number requested by the caller.

Hacker News may return optional or missing fields, which are handled 
defensively by the application.

## Hacker News API

Documentation:

https://github.com/HackerNews/API