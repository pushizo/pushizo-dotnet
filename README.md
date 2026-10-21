<!-- pushizo:header -->
<p align="center">
  <a href="https://pushizo.com"><img src="https://raw.githubusercontent.com/pushizo/.github/main/profile/assets/mark.svg" alt="Pushizo" width="56" height="56"></a>
</p>
<p align="center">
  <a href="https://docs.pushizo.com/docs/sdk/dotnet"><img src="https://img.shields.io/badge/docs-docs.pushizo.com-5b4bdb" alt="Docs"></a>
  <a href="https://docs.pushizo.com/docs/api"><img src="https://img.shields.io/badge/API-reference-b44fc4" alt="API reference"></a>
  <a href="https://github.com/pushizo/pushizo-dotnet/blob/main/LICENSE"><img src="https://img.shields.io/badge/license-MIT-16130f" alt="License: MIT"></a>
</p>
<p align="center"><a href="#english">English</a> · <a href="#türkçe">Türkçe</a></p>

## English

**Pushizo .NET SDK** — Server-side SDK for .NET 8+. Models and resources are generated from the Pushizo OpenAPI schema; retries, idempotency, pagination and webhook verification are built in.

> **Status:** Publishing to NuGet is in progress.<br>
> **Durum:** NuGet yayını hazırlanıyor.

**Install from GitHub until then · O zamana kadar GitHub'dan kurulum:**

```bash
curl -LO https://github.com/pushizo/pushizo-dotnet/releases/download/v0.1.0/Pushizo.0.1.0.nupkg
dotnet add package Pushizo --version 0.1.0 --source .
```

**After publication · Yayından sonra:**

```bash
dotnet add package Pushizo
```

The detailed guide below is in Turkish; code samples are language-neutral. Full documentation:
[docs.pushizo.com/docs/sdk/dotnet](https://docs.pushizo.com/docs/sdk/dotnet) · API reference: [docs.pushizo.com/docs/api](https://docs.pushizo.com/docs/api).

## Türkçe
<!-- /pushizo:header -->

# Pushizo .NET SDK

Pushizo public API'si için C#/.NET sunucu SDK'sı (.NET 8, `HttpClient`, `System.Text.Json`; ek bağımlılık yok).
Modeller (`Pushizo.Models`), parametre sınıfları (`Pushizo.Params`) ve kaynaklar (`Pushizo.Resources`)
[Pushizo OpenAPI şemasından](https://github.com/pushizo/pushizo-openapi) üretilir (`src/Pushizo/Generated/`); kimlik doğrulama, yeniden deneme, idempotency,
sayfalama ve webhook imza doğrulaması el yazımıdır.

## Kurulum

```bash
dotnet add package Pushizo
```

## Kullanım

```csharp
using Pushizo;
using Pushizo.Models;
using Pushizo.Params;

using var hs = new PushizoClient(new PushizoOptions
{
    ApiKey = Environment.GetEnvironmentVariable("HS_API_KEY")!,
    AppId = "app_…",
    // BaseUrl = "http://localhost:4000",
});

// Bildirim gönder: Idempotency-Key verilmezse otomatik UUID (yeniden denemelerde aynı anahtar)
var res = await hs.Notifications.CreateAsync(new CreateNotificationDto
{
    Target = new CreateNotificationDtoTarget { IncludeAliases = new() { ["external_id"] = new() { "user-42" } } },
    Contents = new() { ["en"] = "Your order shipped", ["tr"] = "Siparişiniz kargoya verildi" },
}, new RequestOptions { IdempotencyKey = "order-1001" });
Console.WriteLine($"{res.Id} {res.Status}");

// Kullanıcı oluştur/güncelle
await hs.Users.UpsertAsync(new UpsertUserDto { ExternalId = "user-42", Tags = new() { ["plan"] = "pro" } });

// Cursor sayfalama: IAsyncEnumerable, sayfalar tembel yüklenir
await foreach (var segment in hs.Segments.ListAllAsync(new SegmentsListParams { Limit = 100 }))
{
    Console.WriteLine(segment.Name);
}

// Başka bir app için tek çağrı
await hs.Apps.GetAsync(RequestOptions.ForApp("app_diger"));

// SDK identity verification
var identityHash = PushizoClient.IdentityHash(Environment.GetEnvironmentVariable("HS_SDK_SECRET")!, "user-42");
```

ASP.NET Core'da `IHttpClientFactory` ile: `new PushizoOptions { ApiKey = …, HttpClient = factory.CreateClient("pushizo") }`
(verilen `HttpClient`'ın ömrü çağırana aittir). Tüm model özellikleri nullable'dır; `null` alanlar gönderilmez.
SDK'da metodu olmayan uçlar veya JSON dışı yanıtlar için `hs.RequestAsync("GET", "/v1/apps/{appId}/analytics/messages",
new Dictionary<string, object?> { ["format"] = "csv" })` gövdeyi metin olarak döner.

### Ayarlar

| `PushizoOptions` | Varsayılan | Açıklama |
|---------------------|------------|----------|
| `ApiKey` | — | `hs_live_…` API anahtarı (Bearer) |
| `AppId` | — | Çağrıda `RequestOptions.AppId` verilmezse kullanılan app |
| `BaseUrl` | `https://api.pushizo.com` | API adresi |
| `Timeout` | 30 sn | Deneme başına zaman aşımı |
| `MaxRetries` | 3 | En fazla yeniden deneme (politika: aşağıda) |
| `AutoIdempotencyKey` | `true` | `Idempotency-Key` destekleyen çağrılarda otomatik UUID |
| `HttpClient` | yeni örnek | Özel `HttpClient` |

Her metot `RequestOptions` (AppId, IdempotencyKey, Timeout, MaxRetries, Headers) ve `CancellationToken` alır.

### Hatalar

429 her metotta; 5xx ve ağ hataları GET/HEAD/OPTIONS/PUT/DELETE'te, POST/PATCH'te yalnızca `Idempotency-Key` varsa (otomatik anahtar dahil) veya istek sunucuya hiç gönderilemediyse (bağlantı reddi, DNS hatası, bağlantı zaman aşımı) üstel geri çekilmeyle (0,3 / 1 / 3 sn) yeniden denenir; `Retry-After` başlığına uyulur.
Kalıcı hatalar `PushizoException` alt sınıflarıdır ve `Status`, `Code` (`HS_…`), `RequestId`, `Details` taşır:

```csharp
try
{
    await hs.Notifications.GetAsync("msg_yok");
}
catch (NotFoundException) { /* 404 */ }
catch (InvalidRequestException e) when (e.Code == ErrorCodes.NoRecipients) { /* 422 */ }
catch (PushizoException e) { Console.WriteLine($"{e.Status} {e.Code} {e.Message}"); }
```

Diğer alt sınıflar: `AuthenticationException`, `PermissionDeniedException`, `ConflictException`, `RateLimitException`,
`ServerException`, `NetworkException`.

## Webhook imza doğrulaması

```csharp
app.MapPost("/hooks/pushizo", async (HttpRequest request) =>
{
    using var ms = new MemoryStream();
    await request.Body.CopyToAsync(ms);                               // ham gövde
    try
    {
        var events = Webhooks.ConstructEvents(ms.ToArray(),
            request.Headers[Webhooks.SignatureHeader],
            new[] { Environment.GetEnvironmentVariable("HS_WEBHOOK_SECRET")! }); // rotasyonda eski + yeni secret
        foreach (var ev in events) { /* ev.Id ile tekrarları ayıklayın; ev.Type, ev.Data */ }
        return Results.NoContent();
    }
    catch (WebhookSignatureException e)
    {
        return Results.BadRequest(e.Reason);
    }
});
```

Zaman damgası 5 dakikadan eskiyse istek tekrar (replay) sayılır. Yalnızca doğrulama için
`Webhooks.VerifySignature(...)` `WebhookVerifyResult(Ok, Timestamp, Reason)` döner.

## Geliştirme

```bash
dotnet test                                                # sdk/dotnet içinde (xUnit)
node sdk/scripts/generate-server-sdks.mjs --lang dotnet    # depo kökünde; Generated/ yenilenir
```

`src/Pushizo/Generated/` elle düzenlenmez. Webhook testleri `sdk/testdata/webhook-signature-vectors.json`
ortak vektörlerini kullanır.

<!-- pushizo:footer -->
---

## Support · Destek

- **Guide · Rehber:** [docs.pushizo.com/docs/sdk/dotnet](https://docs.pushizo.com/docs/sdk/dotnet)
- **API reference & OpenAPI · API referansı ve OpenAPI:** [docs.pushizo.com/docs/api](https://docs.pushizo.com/docs/api) ·
  [pushizo-openapi](https://github.com/pushizo/pushizo-openapi)
- **Bugs & ideas · Hata ve öneri:** [Issues](https://github.com/pushizo/pushizo-dotnet/issues)
- **Security · Güvenlik:** please do not open public issues; email [security@pushizo.com](mailto:security@pushizo.com) ·
  herkese açık issue açmayın, bu adrese yazın.
- **Help · Destek:** [support@pushizo.com](mailto:support@pushizo.com)

This repository is mirrored automatically from the Pushizo main repository on every release. Please open an issue
instead of a pull request; fixes land in the main repository and ship with the next release.
Bu depo her sürümde Pushizo ana deposundan otomatik yansıtılır. Pull request yerine issue açın; düzeltmeler ana depoda
yapılır ve bir sonraki sürümle buraya gelir.

Released under the MIT License · MIT lisanslıdır — [LICENSE](https://github.com/pushizo/pushizo-dotnet/blob/main/LICENSE).
<!-- /pushizo:footer -->
