# Архитектура и зафиксированный контракт

## Границы

`RutubeClient` владеет сессией и предоставляет четыре typed-сервиса: `Identity`, `Categories`,
`Videos`, `Live`. Внешнему приложению не нужен `HttpClient` и не нужно знать Studio URL.
`RutubeStudioApi` — единственная точка cookie/CSRF/bearer/refresh, разбора ошибок и redaction.

## Сессия

Playwright открывает `LoginUrl`. Клиент не читает поля формы: оператор сам проходит пароль,
OTP и CAPTCHA. После появления auth-cookie или access token сохраняются Rutube cookies,
релевантный localStorage, access/refresh token, CSRF и User-Agent. Сессия переносима, но является
эквивалентом пароля. После `401` transport ровно один раз вызывает refresh endpoint и повторяет
исходный запрос с новым access token.

## Live contract `studio-v2-2026-08-06-r2`

По умолчанию paths имеют вид:

- `POST v2/video/create/stream/` — создание (`stream_status=wait`);
- `GET/POST v2/video/stream/{id}/` — detail/update/transition;
- `GET v2/video/stream/owner/?stream_status=...` — capability и owner-scoped reconciliation;
- `POST v1/video/stream/{id}/permkey/` — смена типа или ротация ключа;
- `POST video/{id}/thumbnail/?client=vulp` — обложка.

Start переводит `access_status` в `public`; finish/delete отправляют `stream_status=done/deleted`.
Create несёт `Idempotency-Key` и `X-Request-ID`; Studio не сохраняет client reference в объекте.
Если соединение оборвалось до ответа,
клиент выбрасывает `RutubeOutcomeUnknownException`: вызывающий код обязан выполнить
`ReconcileOwnedAsync`, а не повторять create. Reconciliation только читает данные и требует
совпадения client reference, если Studio его вернул, либо точного title + planned time в окне 2 минуты
внутри авторизованного owner endpoint.

Контракт недокументирован и включается явно. `ProbeCapabilityAsync` не заменяет приватный canary.

## Ответы и устойчивость к форме JSON

Парсеры допускают распространённые wrappers `data/result/response/video/stream`, альтернативные
имена provider id и статусов, но требуют стабильный provider anchor. Сырые JSON-ответы намеренно
не входят в публичные модели и исключения.
