namespace RutubeBrowserClient;

public sealed record RutubeIdentity(
    string AccountId,
    string? UserName,
    string? DisplayName,
    string? ChannelId,
    bool PhoneVerified);
