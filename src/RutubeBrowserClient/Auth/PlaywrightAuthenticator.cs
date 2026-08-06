using System.Text.Json;
using Microsoft.Playwright;

namespace RutubeBrowserClient;

/// <summary>Visible Chromium login. The operator completes password, OTP, or CAPTCHA directly on Rutube.</summary>
public sealed class PlaywrightAuthenticator : IInteractiveAuthenticator
{
    private readonly RutubeClientOptions _options;

    public PlaywrightAuthenticator(RutubeClientOptions options) =>
        _options = options ?? throw new ArgumentNullException(nameof(options));

    public async Task<RutubeSession> AuthenticateAsync(CancellationToken cancellationToken = default)
    {
        using var playwright = await Playwright.CreateAsync().ConfigureAwait(false);
        var browser = await LaunchAsync(playwright).ConfigureAwait(false);
        try
        {
            var context = await browser.NewContextAsync(new BrowserNewContextOptions
            {
                ViewportSize = new ViewportSize { Width = 1280, Height = 900 },
                UserAgent = _options.UserAgent
            }).ConfigureAwait(false);
            var page = await context.NewPageAsync().ConfigureAwait(false);
            Status($"Opening Rutube Studio login: {_options.LoginUrl}");
            Status("Complete password, OTP, and CAPTCHA in the browser. Secrets are never read by the client.");
            await page.GotoAsync(_options.LoginUrl, new PageGotoOptions
            {
                WaitUntil = WaitUntilState.DOMContentLoaded,
                Timeout = 60_000
            }).ConfigureAwait(false);

            await WaitForLoginAsync(context, page, cancellationToken).ConfigureAwait(false);
            try
            {
                await page.GotoAsync(_options.StudioBaseUrl, new PageGotoOptions
                {
                    WaitUntil = WaitUntilState.DOMContentLoaded,
                    Timeout = 30_000
                }).ConfigureAwait(false);
                await Task.Delay(1500, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is PlaywrightException or TimeoutException)
            {
                // Cookies already prove login; the API validates them after capture.
            }

            var session = await CaptureAsync(context, page).ConfigureAwait(false);
            if (!session.HasCredentials)
                throw new RutubeClientException("Rutube sign-in completed without a portable cookie or token session.");
            Status("Rutube session captured. Store the exported file as a password-equivalent secret.");
            return session;
        }
        finally
        {
            try { await browser.CloseAsync().ConfigureAwait(false); } catch { }
        }
    }

    private async Task<IBrowser> LaunchAsync(IPlaywright playwright)
    {
        var launch = new BrowserTypeLaunchOptions { Headless = _options.HeadlessLogin };
        try { return await playwright.Chromium.LaunchAsync(launch).ConfigureAwait(false); }
        catch (PlaywrightException ex) when (LooksLikeMissingBrowser(ex))
        {
            Status("Playwright Chromium is missing; installing it once.");
            var exit = Microsoft.Playwright.Program.Main(["install", "chromium"]);
            if (exit != 0)
                throw new RutubeClientException($"Playwright Chromium installation failed with exit code {exit}.", ex);
            return await playwright.Chromium.LaunchAsync(launch).ConfigureAwait(false);
        }
    }

    private async Task WaitForLoginAsync(IBrowserContext context, IPage page, CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow + _options.LoginTimeout;
        var identityUrl = new Uri(_options.PublicApiUri, _options.IdentityPath).ToString();
        while (DateTimeOffset.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var uri = Uri.TryCreate(page.Url, UriKind.Absolute, out var parsed) ? parsed : null;
                var onStudio = uri?.Host.EndsWith("rutube.ru", StringComparison.OrdinalIgnoreCase) == true
                    && !uri.AbsolutePath.Contains("login", StringComparison.OrdinalIgnoreCase)
                    && !uri.AbsolutePath.Contains("auth", StringComparison.OrdinalIgnoreCase);
                if (onStudio && await HasAuthenticatedVisitorAsync(page, identityUrl).ConfigureAwait(false))
                {
                    Status("Rutube sign-in confirmed by the authenticated visitor endpoint.");
                    return;
                }
            }
            catch (PlaywrightException ex) when (IsClosed(ex))
            {
                throw new RutubeClientException("The login browser was closed before Rutube authentication completed.", ex);
            }
            catch (PlaywrightException) { }
            await Task.Delay(1000, cancellationToken).ConfigureAwait(false);
        }
        throw new RutubeClientException($"Rutube login did not complete within {_options.LoginTimeout.TotalMinutes:0} minutes.");
    }

    private static async Task<bool> HasAuthenticatedVisitorAsync(IPage page, string identityUrl)
    {
        try
        {
            return await page.EvaluateAsync<bool>("""
                async url => {
                  try {
                    const response = await fetch(url, { credentials: 'include' });
                    if (!response.ok) return false;
                    const visitor = await response.json();
                    return visitor && visitor.id !== undefined && visitor.id !== null && String(visitor.id).length > 0;
                  } catch (_) {
                    return false;
                  }
                }
                """, identityUrl).ConfigureAwait(false);
        }
        catch (PlaywrightException)
        {
            return false;
        }
    }

    private static async Task<RutubeSession> CaptureAsync(IBrowserContext context, IPage page)
    {
        var session = new RutubeSession();
        try { session.UserAgent = await page.EvaluateAsync<string>("() => navigator.userAgent").ConfigureAwait(false); } catch { }
        var cookies = await context.CookiesAsync().ConfigureAwait(false);
        foreach (var cookie in cookies.Where(x => x.Domain.Contains("rutube.ru", StringComparison.OrdinalIgnoreCase)))
        {
            session.Cookies.Add(new RutubeCookie
            {
                Name = cookie.Name,
                Value = cookie.Value,
                Domain = cookie.Domain,
                Path = string.IsNullOrWhiteSpace(cookie.Path) ? "/" : cookie.Path,
                ExpiresAtUnix = cookie.Expires > 0 ? (long)cookie.Expires : null,
                HttpOnly = cookie.HttpOnly,
                Secure = cookie.Secure,
                SameSite = cookie.SameSite.ToString()
            });
        }
        session.CsrfToken = session.Cookies.FirstOrDefault(x =>
            x.Name.Contains("csrf", StringComparison.OrdinalIgnoreCase) ||
            x.Name.Equals("XSRF-TOKEN", StringComparison.OrdinalIgnoreCase))?.Value;

        try
        {
            var storageJson = await page.EvaluateAsync<string>("""
                () => {
                  const result = {};
                  for (let i = 0; i < localStorage.length; i++) {
                    const k = localStorage.key(i);
                    if (k) result[k] = localStorage.getItem(k);
                  }
                  return JSON.stringify(result);
                }
                """).ConfigureAwait(false);
            var storage = JsonSerializer.Deserialize<Dictionary<string, string>>(storageJson) ?? [];
            foreach (var (key, value) in storage) session.LocalStorage[key] = value;
            session.AccessToken = FindToken(storage, "access");
            session.RefreshToken = FindToken(storage, "refresh");
        }
        catch { }
        return session;
    }

    private static string? FindToken(Dictionary<string, string> storage, string type)
    {
        foreach (var (key, raw) in storage)
        {
            if (!key.Contains(type, StringComparison.OrdinalIgnoreCase) || !key.Contains("token", StringComparison.OrdinalIgnoreCase)) continue;
            if (string.IsNullOrWhiteSpace(raw)) continue;
            if (raw.TrimStart().StartsWith('{'))
            {
                try
                {
                    using var doc = JsonDocument.Parse(raw);
                    foreach (var name in new[] { type + "_token", type + "Token", "token", "value" })
                        if (doc.RootElement.TryGetProperty(name, out var property) && property.GetString() is { Length: > 0 } parsed)
                            return parsed;
                }
                catch (JsonException) { }
            }
            else return raw.Trim('"');
        }
        return null;
    }

    private static bool LooksLikeMissingBrowser(PlaywrightException ex) =>
        ex.Message.Contains("Executable doesn't exist", StringComparison.OrdinalIgnoreCase) ||
        ex.Message.Contains("playwright install", StringComparison.OrdinalIgnoreCase);
    private static bool IsClosed(PlaywrightException ex) => ex.Message.Contains("closed", StringComparison.OrdinalIgnoreCase);
    private void Status(string message) => _options.StatusCallback?.Invoke(message);
}
