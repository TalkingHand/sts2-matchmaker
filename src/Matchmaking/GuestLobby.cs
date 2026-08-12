using System.Text.Json.Serialization;

namespace Sts2Matchmaker.Matchmaking;

/// <summary>
/// One entry from the DC-crawler lobby list (GET http://161.33.165.92:5000/lobbys.json - see
/// sts2-matching-server's docs/lobbys-api.md for the authoritative schema). These are vanilla lobbies posted by
/// hosts who aren't running this mod, so none of MatchTags' own Steam lobby tags apply to them - filtering has to
/// go entirely off what the crawler could scrape from the post title.
///
/// community/lang aren't in the API's current schema yet (server-side addition still pending as of this writing) -
/// declared here anyway so GuestMatchService's filtering starts working the moment the server ships them, with no
/// mod-side change needed. Until then they deserialize to null, which GuestMatchService treats as "unspecified".
/// </summary>
public sealed class GuestLobby
{
    [JsonPropertyName("no")]
    public string No { get; set; } = string.Empty;

    [JsonPropertyName("title")]
    public string Title { get; set; } = string.Empty;

    [JsonPropertyName("lobby_url")]
    public string LobbyUrl { get; set; } = string.Empty;

    [JsonPropertyName("is_beta")]
    public bool IsBeta { get; set; }

    [JsonPropertyName("min_asc")]
    public int? MinAsc { get; set; }

    [JsonPropertyName("max_asc")]
    public int? MaxAsc { get; set; }

    [JsonPropertyName("max_players")]
    public int? MaxPlayers { get; set; }

    [JsonPropertyName("game_mode")]
    public string GameMode { get; set; } = "standard";

    [JsonPropertyName("is_rehost")]
    public bool IsRehost { get; set; }

    [JsonPropertyName("collected_at")]
    public string CollectedAt { get; set; } = string.Empty;

    [JsonPropertyName("community")]
    public string? Community { get; set; }

    [JsonPropertyName("lang")]
    public string? Lang { get; set; }
}
