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
- Live: capability probe, create/detail/update/start/finish/delete, обложка, временный ключ,
  подключение существующего постоянного ключа без его смены и явная ротация постоянного ключа;
- owner-scoped reconciliation по client-reference или неизменяемым title/planned time после неоднозначного timeout;
- безопасные исключения: response body, cookies, access/refresh/stream keys не попадают в сообщения;
- NuGet `RutubeBrowserClient` версии `0.1.5`, MIT.

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

// Подключить уже существующий постоянный ключ аккаунта без генерации нового.
// Запрос отправляет только is_active=true и намеренно не передаёт new_key:
live = await client.Live.UsePermanentStreamKeyAsync(live.Id);

// Отдельная явная операция: сгенерировать новый постоянный ключ аккаунта.
// Используйте только когда действительно нужна ротация ключа.
live = await client.Live.RotateStreamKeyAsync(live.Id, RutubeStreamKeyMode.Permanent);
```

## Private Studio contract

Автоматизация live опирается на наблюдавшийся контракт `studio-v2-2026-08-06-r2`, потому по умолчанию
`EnablePrivateStudioApi=false`. Перед включением новая версия должна пройти `ProbeCapabilityAsync()`
и приватный canary: создать скрытую трансляцию, загрузить обложку, запустить, завершить и убедиться,
что запись доступна по прежнему provider id. Все paths и transition status values конфигурируемы в
`RutubeClientOptions`, но прикладной код должен использовать только typed services.

Для выбора существующего постоянного ключа Studio JS `release-baldr-354` отправляет в
`permkey` только `is_active=true`. Поле `new_key=true` зарезервировано для явного сброса/ротации
и не отправляется методом `UsePermanentStreamKeyAsync`.

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

## Выпуск пакета — локально, не через CI

**GitHub Actions у аккаунта не выполняются: биллинг выключен и включать его не планируется.**
Workflow здесь падает, не начав работу, поэтому пакет собирается и выпускается с машины
разработчика:

```bash
dotnet test RutubeBrowserClient.slnx -c Release
dotnet pack src/RutubeBrowserClient/RutubeBrowserClient.csproj -c Release -o artifacts -p:Version=X.Y.Z
gh release create vX.Y.Z artifacts/*.nupkg --generate-notes
```

Потребитель — KundaliniHub — берёт пакет не из фида, а из `.nupkg`, лежащего в его репозитории
и запиннованного по версии и SHA-256. После выпуска там надо обновить пин:

```bash
cd ~/Projects/KundaliniHub
./scripts/update-pinned-package.sh RutubeBrowserClient X.Y.Z ~/Projects/RutubeBrowserClient/artifacts
```

`dotnet pack` недетерминирован: пересборка той же версии даёт `.nupkg` с другим SHA-256.
Копируйте в Hub файл из того же `artifacts/`, что выложили в релиз, иначе сборка Hub упадёт
на несовпадении хеша.
