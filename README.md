# Watchboard API

Software release monitoring and game-price tracking for a local dashboard. Built with **ASP.NET Core 10, Entity Framework Core and SQLite**.

[Español](README.es.md) · [Frontend](https://github.com/David-H-Afonso/Watchboard.Front) · [Changelog](CHANGELOG.md)

## Features

- Compare installed software with releases from GitHub, npm and supported official feeds.
- Organize installations by reusable locations and configure per-user widgets.
- Match Steam and AllKeyShop sources while keeping their targets and histories separate.
- Prefer Steam-activation keys or gifts, with an explicit alternative PC-platform fallback.
- Store fee-inclusive published totals, observed minima and their dates.
- Private accounts and portable configuration import/export.

## Getting started

Requires the **.NET 10 SDK**.

```sh
dotnet restore Watchboard.Api.sln --locked-mode
dotnet build Watchboard.Api.sln --no-restore
dotnet run --project Watchboard.Api.csproj --no-launch-profile
```

API: **http://127.0.0.1:5197**. Health: `GET /api/health`.

Run the frontend separately on port **5333**. On first access, choose the password for `admin`; there is no default password. Additional accounts are administrator-created.

## Configuration

Defaults are in `appsettings.json`. Environment variables override them:

| Variable | Default |
| --- | --- |
| `Watchboard__DatabasePath` | `storage/watchboard.db` |
| `Watchboard__EnablePolling` | `true` |
| `Watchboard__PriceIntervalMinutes` | `1440` |
| `Watchboard__VersionIntervalMinutes` | `360` |
| `Watchboard__AllowRemoteAccess` | `false` |
| `Watchboard__AllowedOrigins__0` | No override; local origins only |
| `Watchboard__SecureCookies` | `false` (HTTPS requests still use Secure cookies) |

Polling runs only while the API is active. Prices can also be refreshed manually. Host-version detection runs on the API machine (inside the container when using Docker), not remote devices or the Docker host.

## Docker

GitHub Actions publishes **`ghcr.io/david-h-afonso/watchboard-api`** for **Linux amd64**, using the built-in `GITHUB_TOKEN`. Pushes to the default branch update `latest`; all builds receive a full `sha-…` tag. Pushing `v1.0.0` additionally publishes `v1.0.0`, `1.0.0` and `1`.

ARM64 publication is deferred until runtime validation on native hardware; .NET execution under QEMU was not reliable.

Use the [frontend's Compose setup](https://github.com/David-H-Afonso/Watchboard.Front#docker) to run both images. It keeps port **8080 internal**, proxies `/api` from the web container and persists SQLite plus session keys in a named volume. No separate API port should be published.

For a local image build: `docker build -t watchboard-api .`. The image runs as a non-root user; Compose explicitly enables container connections and exact browser origins. Outside that configuration the loopback-only default is unchanged.

## Data and security

Native operation is **loopback-only**; Compose also binds the web port to loopback by default. Remote access is an explicit trusted-LAN option, not an Internet-ready security guarantee. It requires exact `AllowedOrigins` and matching `AllowedHosts`, without wildcards. Sessions use protected HttpOnly, SameSite=Strict cookies; private records belong to the authenticated user. For HTTPS termination, set `Watchboard__SecureCookies=true`. Forwarded client headers are not trusted.

Migrations run at startup. Before upgrading or restoring, stop the API and back up the **entire configured data directory**, including session keys and SQLite files. Never mix database, WAL and SHM files from different snapshots.

Protect volume access: Linux session keys are stored in that volume, without Windows DPAPI protection. Do not assume Windows-protected session keys are portable to Linux.

External prices may be unavailable or estimated. History contains actual saved observations, not a complete imported two-year history. Account offers, wallets and gift cards are excluded.

## License

[MIT](LICENSE) © 2026 David-H-Afonso.
