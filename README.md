# RutubeBrowserClient

Неофициальный typed-клиент Rutube Studio для C# / **.NET 10**. Он открывает обычный Chromium
через Playwright только для интерактивного входа (пароль, OTP и CAPTCHA вводит сам оператор),
сохраняет переносимую cookie/token-сессию и после этого работает в фоне без UI.

Пакет изолирует недокументированный Studio-контракт от прикладного сервиса: в приложении нет
строк с внутренними Rutube endpoint'ами, а изменение контракта отключает только Rutube-интеграцию.

## Возможности

- интерактивный Playwright-вход с OTP/CAPTCHA и portable export/import сессии;
- `IRutubeSessionStore` и атомарный `FileRutubeSessionStore` с правами `0600` (`0700` для каталога);
- cookie, CSRF и bearer-заголовки, одно безопасное обновление access token после `401`;
- typed identity и категории;
- VOD: потоковая загрузка, чтение, изменение, удаление и обложка;
- Live: capability probe, create/detail/update/start/finish/delete, обложка, временный/постоянный ключ;
- owner-scoped reconciliation по client-reference или неизменяемым title/planned time после неоднозначного timeout;
- безопасные исключения: response body, cookies, access/refresh/stream keys не попадают в сообщения;
- NuGet `RutubeBrowserClient` версии `0.1.2`, MIT.

## Быстрый старт

```bash
dotnet restore RutubeBrowserClient.slnx
dotnet test RutubeBrowserClient.slnx --no-restore
RUTUBE_PRIVATE_STUDIO_API=true dotnet run \
  --project samples/RutubeBrowserClient.ConsoleSample -- login
RUTUBE_PRIVATE_STUDIO_API=true dotnet run \
  --project samples/RutubeBrowserClient.ConsoleSample -- probe
```

```csharp
using RutubeBrowserClient;

await using var client = RutubeClient.Create("rutube.session.json", options =>
{
    options.EnablePrivateStudioApi = true; // только после canary
    options.StatusCallback = Console.WriteLine;
});

var identity = await client.EnsureAuthenticatedAsync();
var live = await client.Live.CreateAsync(new RutubeLiveCreateRequest
{
    Title = "Утренняя практика",
    Description = "Прямой эфир",
    CategoryId = "8",
    Visibility = RutubeLiveVisibility.LinkOnly,
    ClientReference = "hub-event-018f...",
    PlannedStartTime = DateTimeOffset.UtcNow.AddHours(1),
    StreamKeyMode = RutubeStreamKeyMode.Temporary
});

// Эти значения — секреты; не логируйте весь объект/DTO.
var rtmpServer = live.Ingest?.Url;
var streamKey = live.Ingest?.StreamKey;
```

## Private Studio contract

Автоматизация live опирается на наблюдавшийся контракт `studio-v2-2026-08-06-r2`, потому по умолчанию
`EnablePrivateStudioApi=false`. Перед включением новая версия должна пройти `ProbeCapabilityAsync()`
и приватный canary: создать скрытую трансляцию, загрузить обложку, запустить, завершить и убедиться,
что запись доступна по прежнему provider id. Все paths и transition status values конфигурируемы в
`RutubeClientOptions`, но прикладной код должен использовать только typed services.

Подробнее:

- [Использование API](docs/USAGE.md)
- [Архитектура и контракт](docs/ARCHITECTURE.md)
- [Сессия и сервер](docs/SERVER.md)
- [Безопасность и redaction](docs/SECURITY.md)

## Ограничения

- Rutube не публикует стабильный API создания трансляций; Studio UI может изменить контракт.
- Первая авторизация и повторный вход после окончательного истечения сессии требуют браузер.
- Библиотека не ретранслирует RTMP, не хранит видео и не выполняет транскодирование.

## Лицензия

[MIT](LICENSE).
