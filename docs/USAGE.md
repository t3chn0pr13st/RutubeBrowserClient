# Использование

## Вход и перенос сессии

```csharp
await using var local = RutubeClient.Create("local.session.json");
await local.EnsureAuthenticatedAsync();
await local.ExportSessionAsync("rutube.portable.session.json");

await using var server = RutubeClient.Create("server.session.json");
await server.ImportSessionAsync("rutube.portable.session.json");
```

Можно экспортировать/importировать base64 через `ExportSessionToBase64Async` и
`ImportSessionFromBase64Async`. Base64 не является шифрованием.

KundaliniHub поддерживает одноразовое прямое сопряжение без файла. Получите URL и код в
`Настройки → Площадки → RutubeBrowserClient`, выполните команду `pair <hub-pairing-url>` и
вставьте код в интерактивный prompt. Код передаётся отдельным HTTPS-заголовком, а session
export формируется в памяти и отправляется непосредственно в Hub.

## Live lifecycle

1. `EnsureAuthenticatedAsync` и `Live.ProbeCapabilityAsync`.
2. `Live.CreateAsync`: сохранить `Id`, owner, playback URL и ingest secrets до следующего шага.
3. `Live.UploadThumbnailAsync`.
4. `Live.UpdateAsync` до/во время эфира.
5. `Live.StartAsync`, затем периодический `Live.GetAsync`.
6. `Live.FinishAsync`; опрашивать тот же id до `Ready`.
7. `Live.DeleteAsync` только отдельным подтверждённым действием.

При `RutubeOutcomeUnknownException` не повторяйте create:

```csharp
var existing = await client.Live.ReconcileOwnedAsync(new(
    OwnerId: accountId,
    ClientReference: stableJobId,
    Title: frozenTitle,
    PlannedStartTime: frozenPlannedAt));
```

`RutubeLiveStream.ToString()` и `RutubeIngest.ToString()` скрывают ключ. Поле `StreamKey` доступно
для передачи доверенному relay, но не должно попадать в structured logs.

## VOD

`RutubeUploadSource` повторно открывает поток и не загружает весь файл в память. Методы:
`Videos.UploadAsync`, `GetAsync`, `UpdateAsync`, `DeleteAsync`, `UploadThumbnailAsync`.
Обложка должна быть JPEG/PNG и не больше 1 MiB.
