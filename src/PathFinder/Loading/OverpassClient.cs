using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace PathFinder.Loading;

/// <summary>
/// Sends queries to the public Overpass API (OpenStreetMap data). Public Overpass servers are
/// often busy, and some networks can't reach a particular one, so this tries several servers
/// in turn and gives each attempt a short timeout. One slow server can't use up the whole budget.
/// </summary>
internal sealed class OverpassClient : IDisposable
{
    /// <summary>
    /// The Overpass servers to try, in order. Edit this list to add, remove or reorder servers.
    /// </summary>
    private static readonly string[] DefaultEndpoints =
    {
        "https://overpass-api.de/api/interpreter",
        "https://overpass.kumi.systems/api/interpreter",
        "https://maps.mail.ru/osm/tools/overpass/api/interpreter",
        "https://overpass.private.coffee/api/interpreter",
    };

    /// <summary>How long one request to one server may take (connect + response + body).</summary>
    private static readonly TimeSpan DefaultPerAttemptTimeout = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Upper bound for the whole query, all servers and both passes included. HostMapLoader gives
    /// the entire load 45 s, so stop a little before that.
    /// </summary>
    private static readonly TimeSpan DefaultTotalBudget = TimeSpan.FromSeconds(40);

    /// <summary>Pause before the second pass over the server list.</summary>
    private static readonly TimeSpan DelayBetweenPasses = TimeSpan.FromSeconds(1);

    private const int Passes = 2;

    private readonly HttpClient _client;
    private readonly IReadOnlyList<string> _endpoints;
    private readonly TimeSpan _perAttemptTimeout;
    private readonly TimeSpan _totalBudget;

    public OverpassClient()
        : this(DefaultEndpoints, DefaultPerAttemptTimeout, DefaultTotalBudget)
    {
    }

    /// <summary>Test hook: point the client at other servers and use other timeouts.</summary>
    internal OverpassClient(IReadOnlyList<string> endpoints, TimeSpan perAttemptTimeout, TimeSpan totalBudget)
    {
        _endpoints = endpoints;
        _perAttemptTimeout = perAttemptTimeout;
        _totalBudget = totalBudget;

        // Timeouts come from our own per-attempt token, not HttpClient's 100 s default.
        _client = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        _client.DefaultRequestHeaders.UserAgent.ParseAdd("PathFinder/1.0 (HackRPI Fall 2026 student project)");
    }

    public void Dispose() => _client.Dispose();

    /// <summary>
    /// Runs an Overpass QL query and returns the JSON response body. Tries each server in order,
    /// then once more after a short pause. Throws <see cref="HttpRequestException"/> listing
    /// what went wrong per server if none of them answered. If <paramref name="cancellationToken"/>
    /// is cancelled, throws <see cref="OperationCanceledException"/> right away.
    /// </summary>
    public async Task<string> QueryAsync(string query, CancellationToken cancellationToken)
    {
        var clock = Stopwatch.StartNew();
        // The latest failure per server, for the final error message.
        var failures = new Dictionary<string, string>();

        for (var pass = 1; pass <= Passes; pass++)
        {
            if (pass > 1)
            {
                // No point pausing if the budget is already (nearly) spent.
                if (_totalBudget - clock.Elapsed <= DelayBetweenPasses)
                    break;
                await Task.Delay(DelayBetweenPasses, cancellationToken);
            }

            foreach (var endpoint in _endpoints)
            {
                // Never let an attempt run past the overall budget.
                var remaining = _totalBudget - clock.Elapsed;
                if (remaining <= TimeSpan.Zero)
                    throw AllFailed(failures);
                var attemptTimeout = remaining < _perAttemptTimeout ? remaining : _perAttemptTimeout;

                var (body, failure) = await TryServerAsync(endpoint, query, attemptTimeout, cancellationToken);
                if (body is not null)
                    return body;
                failures[endpoint] = failure!;
            }
        }

        throw AllFailed(failures);
    }

    /// <summary>
    /// One request to one server. Returns the body on success, or a short failure reason if the
    /// next server should be tried. Throws if the caller cancelled or the query itself is bad.
    /// </summary>
    private async Task<(string? Body, string? Failure)> TryServerAsync(
        string endpoint, string query, TimeSpan timeout, CancellationToken cancellationToken)
    {
        // Linked token: cancelled by our per-attempt timeout OR by the caller.
        using var attemptCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        attemptCts.CancelAfter(timeout);

        try
        {
            // Fresh content per attempt; a sent HttpContent shouldn't be reused.
            using var content = new FormUrlEncodedContent(new Dictionary<string, string> { { "data", query } });
            using var response = await _client.PostAsync(endpoint, content, attemptCts.Token);

            if (!response.IsSuccessStatusCode)
            {
                if (IsServerProblem(response.StatusCode))
                    return (null, ((int)response.StatusCode).ToString());

                // Any other 4xx (e.g. 400 = bad query): every server would say the same, so stop.
                throw new HttpRequestException(
                    $"{HostOf(endpoint)} rejected the query: {(int)response.StatusCode} {response.ReasonPhrase}",
                    null, response.StatusCode);
            }

            var body = await response.Content.ReadAsStringAsync(attemptCts.Token);

            // An overloaded Overpass server sometimes answers 200 with an HTML/XML error page.
            return LooksLikeOverpassJson(body) ? (body, null) : (null, "not JSON");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // Our per-attempt timeout fired (the caller's token wasn't cancelled).
            return (null, "timed out");
        }
        catch (HttpRequestException ex) when (ex.StatusCode is null)
        {
            // Network-level failure: DNS, refused connection, TLS error, reset, etc.
            return (null, "network error");
        }
    }

    /// <summary>Statuses that mean "this server can't help right now", so try the next one.</summary>
    private static bool IsServerProblem(HttpStatusCode status) =>
        status == HttpStatusCode.TooManyRequests      // 429: rate limited
        || status == HttpStatusCode.NotAcceptable     // 406: seen when a server blocks a client
        || status == HttpStatusCode.RequestTimeout    // 408
        || (int)status >= 500;                        // 502/503/504: overloaded

    /// <summary>Cheap check that a body is Overpass JSON rather than an error page.</summary>
    private static bool LooksLikeOverpassJson(string body) =>
        body.AsSpan().TrimStart().StartsWith("{") && body.Contains("\"elements\"");

    private HttpRequestException AllFailed(Dictionary<string, string> failures)
    {
        // e.g. "overpass-api.de: timed out; overpass.kumi.systems: 429; ..."
        var parts = new List<string>();
        foreach (var endpoint in _endpoints)
            parts.Add($"{HostOf(endpoint)}: {(failures.TryGetValue(endpoint, out var f) ? f : "not tried")}");
        return new HttpRequestException("No Overpass server answered (" + string.Join("; ", parts) + ")");
    }

    private static string HostOf(string endpoint) =>
        Uri.TryCreate(endpoint, UriKind.Absolute, out var uri) ? uri.Host : endpoint;
}
