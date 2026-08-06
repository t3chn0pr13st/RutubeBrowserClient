using System.Text.Json.Serialization;

namespace RutubeBrowserClient;

public sealed class RutubeCookie
{
    public required string Name { get; set; }
    public required string Value { get; set; }
    public required string Domain { get; set; }
    public string Path { get; set; } = "/";
    public long? ExpiresAtUnix { get; set; }
    public bool HttpOnly { get; set; }
    public bool Secure { get; set; }
    public string? SameSite { get; set; }

    [JsonIgnore]
    public bool IsExpired => ExpiresAtUnix is > 0 && DateTimeOffset.FromUnixTimeSeconds(ExpiresAtUnix.Value) <= DateTimeOffset.UtcNow;

    public override string ToString() => $"{Name}=[REDACTED]; Domain={Domain}; Path={Path}";
}
