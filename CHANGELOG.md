# Changelog

All notable changes to this project are documented here.

## [1.0.0]

Initial public release.

- `ShruClient`: `GetAccountInfoAsync()`, `SyncServicesAsync()`, `SyncCatalogAsync()`, `PlaceOrderAsync()`, `CheckStatusAsync()`, plus the low-level `RequestAsync()`. Every method takes a `CancellationToken`.
- `ShruException`: one exception type for every failed call, with `Status`, `Response` and `IsAuthError`.
- Typed replies (`AccountInfo`, `Catalog`, `Service`, `PlacedOrder`, `OrderStatus`) with money as `decimal`, each keeping the full `Raw` JSON.
- The client finds the SHRU endpoint itself on first use: the domain + `/api/shru`, then the address exactly as given, then the address as given + `/api/shru`. `GetEndpointAsync()` returns the one in use; orders are only ever sent there. Safe to share between threads.
- Plain HTTP is refused by default; redirects are never followed; TLS 1.2 or newer; every request has a time limit.
- `ShruClient.Sign()` for anyone implementing the protocol elsewhere.
- Targets .NET 8 (no dependencies, trim and AOT safe) and .NET Standard 2.0 (.NET Framework 4.7.2 or newer).
