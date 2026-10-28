# SHRU_Cloud-DotNet-SDK

[![Tests](https://github.com/shrucloud/SHRU_Cloud-DotNet-SDK/actions/workflows/test.yml/badge.svg)](https://github.com/shrucloud/SHRU_Cloud-DotNet-SDK/actions/workflows/test.yml)

Official .NET client for the **SHRU Cloud** API (S-SERVER) - one class that syncs a supplier's catalog, places orders and checks their status over the HMAC-signed SHRU protocol. Async, typed, trim and AOT safe.

Part of the SHRU Cloud SDK family, with the same calls and the same safety rules in every language: [PHP](https://github.com/shrucloud/SHRU_Cloud-PHP-SDK) · [Node.js](https://github.com/shrucloud/SHRU_Cloud-Node-SDK) · [Python](https://github.com/shrucloud/SHRU_Cloud-Python-SDK) · **.NET**.

**Contents:** [Requirements](#requirements) · [Install](#install) · [Quickstart](#quickstart) · [The base URL](#the-base-url) · [Getting your credentials](#getting-your-credentials) · [API reference](#api-reference) · [Error handling](#error-handling) · [Protocol reference](#protocol-reference) · [Security](#security)

## Requirements

- .NET 8 or newer, or .NET Framework 4.7.2 or newer (through the .NET Standard 2.0 build). **Recommended: .NET 10 (LTS).**
- No dependencies on .NET 8 or newer. On .NET Standard 2.0 (.NET Framework and others), Microsoft's own `System.Text.Json` package.

## Install

**As a NuGet package**, from this repository's [Releases](https://github.com/shrucloud/SHRU_Cloud-DotNet-SDK/releases): in your project's folder, download `ShruCloud.Sdk.1.0.0.nupkg` into a `packages` folder, add that folder as a package source, then add the package:

```bash
mkdir packages
curl -fsSL -o packages/ShruCloud.Sdk.1.0.0.nupkg https://github.com/shrucloud/SHRU_Cloud-DotNet-SDK/releases/download/v1.0.0/ShruCloud.Sdk.1.0.0.nupkg
dotnet new nugetconfig
dotnet nuget add source ./packages --name shrucloud --configfile nuget.config
dotnet add package ShruCloud.Sdk --version 1.0.0
```

In Visual Studio, the same folder can be added under Tools -> NuGet Package Manager -> Package Sources.

**From source:**

```bash
git clone --branch v1.0.0 https://github.com/shrucloud/SHRU_Cloud-DotNet-SDK.git
dotnet add reference SHRU_Cloud-DotNet-SDK/src/ShruCloud/ShruCloud.csproj
```

## Quickstart

```csharp
using ShruCloud;

using var client = new ShruClient("https://reseller.example.com", "YOUR_KEY_ID", "YOUR_SECRET");

try
{
    var services = await client.SyncServicesAsync();
    var order = await client.PlaceOrderAsync(services[0].Id, "356789101234567");
    var status = await client.CheckStatusAsync(order.OrderId);
    Console.WriteLine(status.Status + " " + status.Reply);
}
catch (ShruException error)
{
    Console.WriteLine(error.Message);
}
```

Create one client and reuse it (in ASP.NET Core, register it as a singleton): it keeps its connections open and is safe to share between threads.

Runnable examples are in [`examples/`](examples/ShruCloud.Examples/):

```bash
dotnet run --project examples/ShruCloud.Examples -- sync-services
dotnet run --project examples/ShruCloud.Examples -- place-order <service_id> <input>
dotnet run --project examples/ShruCloud.Examples -- check-status <order_id>
```

## The base URL

The first argument is the address the platform gave you, usually just its domain. Any of these work:

```
https://reseller.example.com/
https://reseller.example.com
reseller.example.com
https://reseller.example.com/api/shru
https://reseller.example.com/any/page/of/the/site
```

On first use the client finds the SHRU endpoint itself, trying in this order:

1. the domain + `/api/shru` (where every SHRU Cloud platform has it)
2. the address exactly as given
3. the address as given + `/api/shru`

It asks each one for `account_info`, a read-only call, and uses the first that answers as a SHRU endpoint, including a refusal in SHRU's own format (a wrong key, an IP not allowed), since that still proves the address is right. The endpoint found is kept for the client's lifetime; `GetEndpointAsync()` returns it. Orders are only ever sent to that one endpoint, never retried at another address. Passing the full `https://reseller.example.com/api/shru` skips the check entirely.

## Getting your credentials

Every SHRU Cloud platform issues credentials from its own admin panel: Reseller API Keys -> your account -> **SHRU** tab -> Enable SHRU Access (on [SHRU_Cloud-Provider-Starter](https://github.com/shrucloud/SHRU_Cloud-Provider-Starter), that's Credentials -> New Credential):

| | |
|---|---|
| **Key ID** | Identifies your account, sent on every request. |
| **Secret** | Shown once, at creation/regeneration time only. Store it like a database password. |
| **Allowed IPs** | The platform rejects every request from an IP not on this list. Get the IP(s) your integration actually calls from added *before* your first request, or you'll get a 403. |

## API reference

```csharp
new ShruClient(baseUrl, keyId, secret, new ShruClientOptions
{
    TimeoutSeconds = 15,        // at least 1
    VerifyTls = true,           // turn off only for a local test server
    AllowInsecureHttp = false,  // plain http:// only for a local test server
});
```

Every method is async and takes an optional `CancellationToken` as its last argument.

### `GetAccountInfoAsync()` -> `AccountInfo`

`Balance` (decimal), `Currency`, `Email`. A lightweight credentials/connectivity check - useful before doing anything else.

### `SyncServicesAsync()` -> `IReadOnlyList<Service>` / `SyncCatalogAsync()` -> `Catalog`

The full catalog of services you're allowed to order, priced exactly as your account is billed (any tier pricing already applied server-side - never compute or cache pricing yourself). `SyncCatalogAsync()` returns `Balance`, `Currency` and `Services` in one call. Each `Service`:

| Property | Type | |
|---|---|---|
| `Id` | `int` | Pass to `PlaceOrderAsync()`. |
| `Name`, `Group` | `string` | |
| `Type` | `string` | `"fixed"` or `"quantity"`. |
| `Price` | `decimal` | |
| `Delivery` | `string` | eg. `"Instant"`, `"1-24h"`. |
| `InputLabel` | `string?` | `null` when this service takes no primary input. |
| `InputRequired` | `bool` | |
| `Quantity` | `QuantityRange?` | `Min`/`Max`, only for type `"quantity"`. |
| `Bulk` | `bool` | Only ever true for type `"fixed"`. |
| `Fields` | `IReadOnlyList<ServiceField>` | `Label`, `Key`, `Type` (`"input"` or `"textarea"`), `Required`. |

Never assume `InputLabel` means "IMEI" - it's whatever this specific service actually asks for (SN, ECID, an account ID, ...). Each field's `Key` is exactly the name to use for it in `PlaceOrderAsync()`'s `fields`, not necessarily the same string as its `Label`. Every reply object also has `Raw`, the full decoded JSON, for any field added after this SDK's release.

### `PlaceOrderAsync(serviceId, input, quantity = 1, fields = null)` -> `PlacedOrder`

`OrderId` (long), `Status`.

- `input` - the service's primary input (eg. an IMEI/serial). Pass `""` for a service whose `InputLabel` is `null`.
- `quantity` - only meaningful for a `"quantity"` service.
- `fields` - extra field values (`IReadOnlyDictionary<string, string>`), keyed by each field's `Key`.
- `Bulk == true` means the service accepts several independent inputs - call `PlaceOrderAsync()` once per value.

Re-submitting the exact same input, fields and quantity for a service you already have a still-queued order for returns that same order instead of creating (and billing) a duplicate - safe to retry after a network timeout without checking first. This applies to orders that carry an input or field values; an order with neither is always a new order. The exact same signed request sent twice within one second is refused with HTTP 409.

### `CheckStatusAsync(orderId)` -> `OrderStatus`

`OrderId`, `Status`, `Reply`. `Status` is one of `queued`, `processing`, `completed`, `rejected`, `refunded`. `Reply` is always plain text and is `""` until the order finishes.

### `RequestAsync(action, parameters = null)` -> `JsonElement`

Calls any action by name, with its parameters as a `JsonObject`, and returns the decoded reply (`success` included) - for an action a platform has added since this SDK's release, without waiting on a new version.

### `ShruClient.Sign(secret, timestamp, body)`

The signature itself, for anyone implementing the protocol elsewhere.

## Error handling

Every failed call - transport (network error, time limit, invalid JSON), auth (bad/expired signature, IP not allowlisted) or a normal API rejection (insufficient balance, service not found, ...) - throws a single `ShruException`:

```csharp
try
{
    await client.PlaceOrderAsync(serviceId, input);
}
catch (ShruException error)
{
    _ = error.Message;      // the server's own "message", almost always safe to show a user directly
    _ = error.Status;       // the HTTP status the server returned (0 = never reached the server)
    _ = error.IsAuthError;  // true for 401/403 - bad Key ID/Secret, or your IP isn't allowlisted
    _ = error.Response;     // the full decoded JSON body (JsonElement?), when one exists
}
```

An unusable argument (a service ID below 1, a null input, ...) throws an `ArgumentException` before anything is sent. A `CancellationToken` you cancel throws `OperationCanceledException`, as everywhere in .NET.

## Protocol reference

- **Endpoint** - `POST {domain}/api/shru`, JSON body, one `action` per request.
- **Headers** - `X-Key-Id`, `X-Timestamp` (Unix epoch seconds), `X-Signature`.
- **Actions** - `account_info`, `sync`, `submit_order`, `check_status`.
- **Response envelope** - always `{"success": true, ...}` or `{"success": false, "message": "..."}`.

**Signing algorithm** (also available as `ShruClient.Sign(secret, timestamp, body)`):

```
signable  = "POST\n/api/shru\n{timestamp}\n{raw JSON request body}"
signature = lowercase hex HMAC-SHA256(signable, your secret)
```

Servers reject a timestamp more than 5 minutes old; a rejected signature almost always means clock drift (NTP).

## Security

- Plain HTTP is refused by default - the constructor throws before any request is sent. Set `AllowInsecureHttp` only for a local test server, never in production.
- Redirects are never followed - a signed request has exactly one valid destination.
- TLS 1.2 or newer only (TLS 1.3 too on .NET 8 or newer), set by the client itself, so .NET Framework needs no extra setup. Every request has a time limit, and a reply larger than 10 MB is refused.
- Treat your Secret like a database password: never commit it, never log it, and regenerate it immediately if you suspect it leaked.

## License

MIT - see [LICENSE](LICENSE).
