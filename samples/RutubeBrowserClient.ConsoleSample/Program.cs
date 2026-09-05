using RutubeBrowserClient;
using System.Text;
using System.Text.Json;

var arguments = args.ToList();
var sessionPath = Environment.GetEnvironmentVariable("RUTUBE_SESSION_PATH")
                  ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".rutube-browser-client", "session.json");
var privateApi = string.Equals(Environment.GetEnvironmentVariable("RUTUBE_PRIVATE_STUDIO_API"), "true", StringComparison.OrdinalIgnoreCase);
await using var client = RutubeClient.Create(sessionPath, options =>
{
    options.StatusCallback = Console.WriteLine;
    options.EnablePrivateStudioApi = privateApi;
});

if (arguments.Count == 0) { Help(); return; }
try
{
    switch (arguments[0].ToLowerInvariant())
    {
        case "login":
            PrintIdentity(await client.EnsureAuthenticatedAsync());
            break;
        case "whoami":
            PrintIdentity(await client.EnsureAuthenticatedAsync());
            break;
        case "export":
            Require(2, "export <file>");
            await client.ExportSessionAsync(arguments[1]);
            Console.WriteLine($"Session exported to {Path.GetFullPath(arguments[1])}");
            break;
        case "import":
            Require(2, "import <file>");
            await client.ImportSessionAsync(arguments[1]);
            PrintIdentity(await client.EnsureAuthenticatedAsync());
            break;
        case "pair":
            Require(2, "pair <hub-pairing-url>");
            await PairWithHubAsync(client, arguments[1]);
            break;
        case "categories":
            await client.EnsureAuthenticatedAsync();
            foreach (var category in await client.Categories.GetAllAsync())
                Console.WriteLine($"{category.Id}\t{category.Title}\tactive={category.IsActive}");
            break;
        case "probe":
            await client.EnsureAuthenticatedAsync();
            var capability = await client.Live.ProbeCapabilityAsync();
            Console.WriteLine($"available={capability.Available} contract={capability.ContractVersion} account={capability.AccountId} {capability.SafeMessage}");
            break;
        case "live-create":
            Require(5, "live-create <title> <category-id> <public|link> <client-reference> [planned-utc]");
            await client.EnsureAuthenticatedAsync();
            var created = await client.Live.CreateAsync(new RutubeLiveCreateRequest
            {
                Title = arguments[1],
                CategoryId = arguments[2],
                Visibility = arguments[3].Equals("link", StringComparison.OrdinalIgnoreCase)
                    ? RutubeLiveVisibility.LinkOnly : RutubeLiveVisibility.Public,
                ClientReference = arguments[4],
                PlannedStartTime = arguments.Count > 5 ? DateTimeOffset.Parse(arguments[5]).ToUniversalTime() : null
            });
            PrintLive(created);
            Console.WriteLine("Use live-credentials <id> to reveal the OBS destination in this terminal.");
            break;
        case "live-get":
            Require(2, "live-get <id>");
            await client.EnsureAuthenticatedAsync();
            PrintLive(await client.Live.GetAsync(arguments[1]));
            break;
        case "live-credentials":
            Require(2, "live-credentials <id>");
            await client.EnsureAuthenticatedAsync();
            var stream = await client.Live.GetAsync(arguments[1]);
            Console.WriteLine($"Server: {stream.Ingest?.Url}");
            Console.WriteLine($"Key: {stream.Ingest?.StreamKey}");
            break;
        case "live-start":
        case "live-finish":
            Require(2, arguments[0] + " <id>");
            await client.EnsureAuthenticatedAsync();
            PrintLive(arguments[0] == "live-start"
                ? await client.Live.StartAsync(arguments[1])
                : await client.Live.FinishAsync(arguments[1]));
            break;
        case "live-delete":
            Require(3, "live-delete <id> --confirm");
            if (arguments[2] != "--confirm") throw new ArgumentException("Deletion requires --confirm.");
            await client.EnsureAuthenticatedAsync();
            await client.Live.DeleteAsync(arguments[1]);
            Console.WriteLine("Provider deletion requested.");
            break;
        case "live-thumbnail":
            Require(3, "live-thumbnail <id> <jpeg-or-png>");
            await client.EnsureAuthenticatedAsync();
            Console.WriteLine(await client.Live.UploadThumbnailAsync(arguments[1], RutubeUploadSource.FromFile(arguments[2])));
            break;
        case "video-upload":
            Require(4, "video-upload <file> <title> <category-id>");
            await client.EnsureAuthenticatedAsync();
            var video = await client.Videos.UploadAsync(RutubeUploadSource.FromFile(arguments[1]), new RutubeVideoCreateRequest
            {
                Title = arguments[2],
                CategoryId = arguments[3],
                ClientReference = "cli-" + Guid.NewGuid().ToString("N")
            });
            Console.WriteLine($"Video {video.Id}: {video.Status} {video.PlaybackUrl}");
            break;
        default:
            Help();
            Environment.ExitCode = 2;
            break;
    }
}
catch (RutubeClientException ex)
{
    Console.Error.WriteLine(ex.Message);
    Environment.ExitCode = 1;
}
catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or HttpRequestException or IOException or JsonException)
{
    Console.Error.WriteLine(ex.Message);
    Environment.ExitCode = 1;
}

void Require(int count, string usage)
{
    if (arguments.Count < count) throw new ArgumentException("Usage: " + usage);
}

static void PrintIdentity(RutubeIdentity identity) =>
    Console.WriteLine($"Account {identity.AccountId}; channel={identity.ChannelId}; name={identity.DisplayName}; phoneVerified={identity.PhoneVerified}");

static void PrintLive(RutubeLiveStream stream) =>
    Console.WriteLine($"Live {stream.Id}: {stream.Status}; visibility={stream.Visibility}; playback={stream.PlaybackUrl}; signal={stream.SignalPresent}");

static void Help() => Console.WriteLine("""
RutubeBrowserClient.ConsoleSample
  login | whoami | export <file> | import <file>
  pair <hub-pairing-url>
  categories | probe
  live-create <title> <category-id> <public|link> <client-reference> [planned-utc]
  live-get <id> | live-credentials <id> | live-start <id> | live-finish <id>
  live-delete <id> --confirm | live-thumbnail <id> <jpeg-or-png>
  video-upload <file> <title> <category-id>

Set RUTUBE_PRIVATE_STUDIO_API=true only after a canary confirms the pinned Studio contract.
Set RUTUBE_SESSION_PATH to override the default 0600 session file.
""");

static async Task PairWithHubAsync(RutubeClient client, string endpointText)
{
    var endpoint = ValidatePairingEndpoint(endpointText);
    Console.WriteLine($"Hub: {endpoint.GetLeftPart(UriPartial.Authority)}");
    Console.Write("One-time pairing code: ");
    var code = ReadSecret();
    if (string.IsNullOrWhiteSpace(code)) throw new ArgumentException("Pairing code is required.");

    var identity = await client.EnsureAuthenticatedAsync();
    Console.WriteLine($"Authenticated as Rutube account {identity.AccountId}. Sending the session directly to Hub...");

    var payload = await client.ExportSessionToBase64Async();
    using var handler = new HttpClientHandler { AllowAutoRedirect = false };
    using var http = new HttpClient(handler) { Timeout = TimeSpan.FromMinutes(2) };
    using var request = new HttpRequestMessage(HttpMethod.Post, endpoint);
    request.Headers.TryAddWithoutValidation("X-KH-Rutube-Pairing-Code", code.Trim());
    request.Content = new StringContent(payload, Encoding.UTF8, "text/plain");

    using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
    var responseText = await response.Content.ReadAsStringAsync();
    if (!response.IsSuccessStatusCode)
    {
        var detail = responseText.Length > 4096 ? responseText[..4096] : responseText;
        throw new InvalidOperationException($"Hub rejected pairing ({(int)response.StatusCode}): {detail}");
    }

    using var responseJson = JsonDocument.Parse(responseText);
    var connected = responseJson.RootElement.TryGetProperty("connected", out var connectedNode) && connectedNode.GetBoolean();
    var message = responseJson.RootElement.TryGetProperty("message", out var messageNode)
        ? messageNode.GetString()
        : null;
    Console.WriteLine(message ?? "Rutube pairing completed. The browser session is verified and stored in Hub.");
    if (!connected) Environment.ExitCode = 1;
}

static Uri ValidatePairingEndpoint(string value)
{
    if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) ||
        (!string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) &&
         !(string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) && uri.IsLoopback)) ||
        !string.IsNullOrEmpty(uri.UserInfo) ||
        !string.IsNullOrEmpty(uri.Query) ||
        !string.IsNullOrEmpty(uri.Fragment) ||
        !uri.AbsolutePath.StartsWith("/api/rutube-session-pairings/", StringComparison.Ordinal))
    {
        throw new ArgumentException("Pairing URL must be an HTTPS Hub /api/rutube-session-pairings/... URL (HTTP is allowed only for loopback development).");
    }

    return uri;
}

static string ReadSecret()
{
    if (Console.IsInputRedirected) return Console.ReadLine() ?? "";

    var characters = new List<char>();
    while (true)
    {
        var key = Console.ReadKey(intercept: true);
        if (key.Key == ConsoleKey.Enter)
        {
            Console.WriteLine();
            return new string(characters.ToArray());
        }

        if (key.Key == ConsoleKey.Backspace)
        {
            if (characters.Count > 0) characters.RemoveAt(characters.Count - 1);
            continue;
        }

        if (!char.IsControl(key.KeyChar)) characters.Add(key.KeyChar);
    }
}
