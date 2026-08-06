using System.Diagnostics;
using System.Text.Json.Serialization;

namespace RutubeBrowserClient;

/// <summary>Portable authenticated Rutube browser state. Treat serialized values as credentials.</summary>
[DebuggerDisplay("RutubeSession(AccountId={AccountId}, Secrets=[REDACTED])")]
public sealed class RutubeSession
{
    public int FormatVersion { get; set; } = 1;
    public List<RutubeCookie> Cookies { get; set; } = [];
    public Dictionary<string, string> LocalStorage { get; set; } = new(StringComparer.Ordinal);
    public string? AccessToken { get; set; }
    public string? RefreshToken { get; set; }
    public long? AccessTokenExpiresAtUnix { get; set; }
    public string? CsrfToken { get; set; }
    public string? AccountId { get; set; }
    public string? ChannelId { get; set; }
    public string? DisplayName { get; set; }
    public string? UserAgent { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

    [JsonIgnore]
    public bool HasCredentials => Cookies.Any(x => !x.IsExpired) || !string.IsNullOrWhiteSpace(AccessToken);

    [JsonIgnore]
    public DateTimeOffset? AccessTokenExpiresAt => AccessTokenExpiresAtUnix is > 0
        ? DateTimeOffset.FromUnixTimeSeconds(AccessTokenExpiresAtUnix.Value)
        : null;

    public override string ToString() => $"RutubeSession(AccountId={AccountId ?? "unknown"}, Secrets=[REDACTED])";
}
