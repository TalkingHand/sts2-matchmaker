using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using MegaCrit.Sts2.Core.Logging;

namespace Sts2Matchmaker.Matchmaking;

/// <summary>
/// Fetches and caches the DC-crawler lobby list. The server enforces 3-seconds-per-request and returns a plain-text
/// "Too Many Requests" body (not JSON) if that's violated - this caches the last successful result for
/// MinFetchInterval (comfortably above the server's own floor) so every caller, regardless of how often
/// GuestMatchService itself gets invoked, funnels through one shared rate limit. Also doubles as the "wait 5s since
/// exhausting the list, then refresh" behavior GuestMatchService wants - callers just call GetAsync every time they
/// want to try, and get a fresh list only when enough time has actually passed.
///
/// Never throws - a dead/unreachable/rate-limited server should degrade to "no guest candidates this cycle", not
/// break the matching loop it's feeding into.
/// </summary>
public static class GuestLobbyListClient
{
    private const string Endpoint = "http://161.33.165.92:5000/lobbys.json";
    // The crawler itself only rewrites lobbys.json once every 5s, so polling faster than that never gets newer
    // data - it just changes how much of that 5s window we might catch it in. 3.5s (comfortably above the
    // server's hard 3s floor) trims worst-case staleness from "up to 5s late" to "up to 3.5s late" without
    // meaningfully raising request volume. Internal (not private) so AutoMatchService's guest-poll loop can pace
    // itself against the exact same interval instead of duplicating the number.
    internal static readonly TimeSpan MinFetchInterval = TimeSpan.FromSeconds(3.5);
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(10) };
    private static readonly SemaphoreSlim FetchLock = new(1, 1);

    private static List<GuestLobby> s_cache = new();
    private static DateTime s_lastFetchUtc = DateTime.MinValue;

    public static async Task<IReadOnlyList<GuestLobby>> GetAsync(CancellationToken cancelToken)
    {
        await FetchLock.WaitAsync(cancelToken);
        try
        {
            if (DateTime.UtcNow - s_lastFetchUtc < MinFetchInterval)
            {
                return s_cache;
            }

            try
            {
                using HttpResponseMessage response = await Http.GetAsync(Endpoint, cancelToken);
                if (response.StatusCode == HttpStatusCode.NotFound)
                {
                    s_cache = new List<GuestLobby>();
                }
                else
                {
                    response.EnsureSuccessStatusCode();
                    await using System.IO.Stream stream = await response.Content.ReadAsStreamAsync(cancelToken);
                    s_cache = await JsonSerializer.DeserializeAsync<List<GuestLobby>>(stream, cancellationToken: cancelToken) ?? new List<GuestLobby>();
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Log.Warn($"[sts2_matchmaker] Failed to fetch guest lobby list, keeping previous {s_cache.Count} cached entries: {ex.Message}");
            }
            finally
            {
                // Advance the timestamp even on failure - a broken/rate-limited server should back off just as
                // much as a working one, not get hammered every call until it succeeds.
                s_lastFetchUtc = DateTime.UtcNow;
            }

            return s_cache;
        }
        finally
        {
            FetchLock.Release();
        }
    }
}
