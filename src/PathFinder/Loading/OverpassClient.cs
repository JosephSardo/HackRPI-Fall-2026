using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace PathFinder.Loading;

/// <summary>
/// Sends queries to the public Overpass API (OpenStreetMap data). Retries with growing delays
/// when the server is busy or the network hiccups, since the public instance often is.
/// </summary>
internal sealed class OverpassClient : IDisposable
{
    private const string Endpoint = "https://overpass-api.de/api/interpreter";
    private const int MaxAttempts = 5;
    private static readonly TimeSpan InitialTimeout = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan MaxTimeout = TimeSpan.FromSeconds(30);

    private readonly HttpClient _client;

    public OverpassClient()
    {
        _client = new HttpClient();
        _client.DefaultRequestHeaders.UserAgent.ParseAdd("PathFinder/1.0");
    }

    public void Dispose() => _client.Dispose();

    /// <summary>Runs an Overpass QL query and returns the response body.</summary>
    public async Task<string> QueryAsync(string query, CancellationToken cancellationToken)
    {
        TimeSpan timeout = InitialTimeout;

        for (var attempt = 1; ; attempt++)
        {
            try
            {
                // Fresh content per attempt; a sent HttpContent shouldn't be reused.
                using var content = new FormUrlEncodedContent(new Dictionary<string, string> { { "data", query } });
                using var response = await _client.PostAsync(Endpoint, content, cancellationToken);

                if (response.IsSuccessStatusCode)
                    return await response.Content.ReadAsStringAsync(cancellationToken);

                if (!IsTransient(response.StatusCode) || attempt >= MaxAttempts)
                    response.EnsureSuccessStatusCode(); // throws
            }
            catch (HttpRequestException ex) when (ex.StatusCode is null && attempt < MaxAttempts)
            {
                // Network-level failure (DNS, connection reset, etc.) — retry.
            }
            catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested && attempt < MaxAttempts)
            {
                // HttpClient timeout — retry.
            }

            await Task.Delay(timeout, cancellationToken);
            timeout = timeout > MaxTimeout ? MaxTimeout : TimeSpan.FromSeconds(2 * timeout.Seconds);
        }
    }

    private static bool IsTransient(HttpStatusCode status) =>
        status == HttpStatusCode.TooManyRequests      // 429: Overpass rate limit
        || status == HttpStatusCode.RequestTimeout    // 408
        || (int)status >= 500;                        // 502/503/504: Overpass overloaded
}
