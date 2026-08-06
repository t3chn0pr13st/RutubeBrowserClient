# Безопасность

Секреты: session cookies, localStorage, CSRF, access/refresh token, RTMP stream key. Публичные DTO
не включают raw provider response; исключения извлекают только короткие `code/message/request id`
и дополнительно редактируют named secrets, bearer tokens и query credentials.

- не сериализуйте `RutubeSession` в logs;
- не логируйте request/response body или multipart;
- не показывайте `StreamKey` вне защищённого admin UI;
- ограничивайте права session file и используйте шифрование на сервере;
- привязывайте импортированную сессию к проверенному `AccountId`;
- включайте private Studio API через feature flag и circuit breaker;
- при contract drift отключайте Rutube target, не ослабляйте видимость и не повторяйте ambiguous create.
