# HeroSignal (.NET)

HeroSignal sunucu SDK'sı: bildirim gönderme, kullanıcı/abonelik yönetimi, segment/şablon/kampanya, analitik ve webhook imza doğrulaması. API anahtarı yalnızca sunucuda kullanılır.

## Kurulum
```
dotnet add package HeroSignal
```

## Kullanım
```csharp
using var hs = new HeroSignalClient(new HeroSignalClientOptions { ApiKey = key, AppId = "app_…" });
await hs.Notifications.CreateAsync(new CreateNotificationDto
{
    Target = new CreateNotificationDtoTarget { IncludeAliases = new() { ["external_id"] = new() { "u1" } } },
    Contents = new() { ["en"] = "Shipped" },
}, new RequestOptions { IdempotencyKey = "order-42" });
```

- Kimlik doğrulama (Bearer API anahtarı), zaman aşımı, 429/5xx/ağ hatalarında üstel geri çekilmeli yeniden deneme (`Retry-After`'a uyar), otomatik `Idempotency-Key`, hata tipi (status, code, message, request_id), cursor sayfalama ve `HeroSignal-Signature` webhook doğrulaması (sabit zamanlı, tolerans, secret rotasyonu).
- Kaynaklar ve modeller `api/openapi.yaml`'dan `node sdk/scripts/generate-server-sdks.mjs` ile üretilir; üretilmiş dosyaları elle düzenlemeyin.

Ayrıntılı rehber: [docs/sdk/dotnet.md](../../docs/sdk/dotnet.md)

## Test
```
dotnet test HeroSignal.sln
```
