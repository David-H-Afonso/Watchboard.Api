# Watchboard API

Independent **.NET 10 / ASP.NET Core / EF Core SQLite** repository for personal software-release and game-price tracking. The React frontend is a separate repository; neither build nor tests require that repository or parent workspace files.

## Build, test, run

Install the .NET **10 SDK** with current security patches. SDK policy and the local EF tool manifest are included.

```sh
dotnet restore Watchboard.Api.sln --locked-mode
dotnet build Watchboard.Api.sln --no-restore
dotnet test Watchboard.Api.sln --no-build
dotnet run --project Watchboard.Api.csproj --no-launch-profile
```

API: `http://127.0.0.1:5197`. Standalone data: `storage/watchboard.db`, ignored. The name intentionally differs from the versioned `Data` source directory on case-insensitive Windows. `Watchboard__DatabasePath` supports another path. The optional parent workspace launcher explicitly retains its existing `../data/watchboard.db`; changing repo boundaries does not move that installation's data.

First user **admin** chooses a local password in the first-run UI (or setup API). No default/shared password. Additional accounts are admin-created. Private routes require HttpOnly/SameSite Strict cookie sessions; records/config/widget rules are owner-scoped. Host probes and user management require administrator role. There is no password recovery/change flow yet.

## Behavior

- Installed versus published provenance; GitHub URLs/npm/official release feeds; numeric and Fulldex date/hotfix comparison.
- Owned canonical location catalog, independent of project names/groups and `installedOn` observation provenance. Assigning a location never probes a remote device; explicit reassignment/manual installation disables that watch's fixed local-host probe.
- AllKeyShop PC keys/gifts and official Steam Spain/EUR purchases remain separate sources/history/targets. Generic unique exact-title metadata matching works in both directions, independently of stock/purchase-price availability. It validates appdetails ID/type/full title and the PC Product/SKU/canonical/base edition; bounded derived URLs are candidates, never proof by themselves. Unsafe/ambiguous matches stay reviewable.
- AKS prefers eligible **Steam activation** keys/gifts even when another DRM is cheaper; only without an eligible Steam offer does it use a recognized, explicitly labelled other-PC-DRM fallback. Accounts, gift cards, wallets, console/unknown classifications and Steam-direct market rows are excluded. Actual platform/delivery/language restriction are stored per reading.
- New market watches default to the lowest permitted published card/PayPal **total** (`best`) within that priority tier. Totals/coupons already include the published fees/discount: never apply them twice. Consistent explicit breakdowns are retained; missing breakdowns are not invented. Saved payment/region/merchant/edition filters still apply.
- Existing owned compatible sources are reused from either URL, preserving filters, IDs, goals and history. Upcoming/unpriced games retain valid identity and an explicit missing-price state, never a fake zero. Deletions/opt-outs suppress automatic re-seeding; transient discovery failures are daily-retryable or explicitly forced.
- Price and discovery share operation-scoped public success/failure snapshots, avoiding duplicate downloads; private DB scopes and source filters remain independent. Known independently checked public identities may cover a transport-only outage, never a live identity contradiction or an invented price. Healthy discovery stays generic.
- Inclusive fresh-data goals, reported discounts/optional expiry, honestly observed sale periods.
- Automatic prices default to daily (1440 minutes); explicit manual `{ "force": true, "scope": "prices" }` bypasses eligibility, not provider spacing/overlap/rate limits. Price freshness is interval plus two hours (26 hours by default).
- Lifetime/730-day minima include earliest actual observation dates and use all eligible SQLite readings, not only the latest2160 details. Coverage checks gaps. There is **no verified public two-year backfill**; no old quotes are synthesized and blocked/private AKS history endpoints are never called.
- Every minimum carries the platform/delivery/language of that exact earliest low reading, even outside the detail cap. Changes in platform, delivery, language, region or edition do not masquerade as comparable price drops. Unknown legacy context stays unknown, and cannot produce a fresh market goal/recommendation.
- Portable configuration exports version2 and accepts v1. Location names, not runtime IDs, are remapped per owner. One-to-one instance matching preserves multiple installations/legacy duplicates; preview is read-only and import is atomic.
- Widget software location IDs distinguish `null` (all, including unassigned) from `[]` (none). Updates-only defaults true and excludes missing/unknown/current/ahead/stale/error/detection-failed installs, without filtering independent game alerts.
- Outbound IO never holds the short mutation semaphore; delayed results cannot recreate deleted records.
- Optional in-process polling only; no OS task, Docker socket, SSH or arbitrary command.

This remains **loopback-only**, even with accounts. Do not expose through LAN/proxy/tunnel without designing HTTPS, proxy/origin policy, recovery, native widget/device scopes and operations. No production deployment is claimed.

## Data safety

Stop API and back up the entire configured runtime directory (`storage` standalone), including session keys and consistent DB/WAL/SHM. Windows keys are DPAPI-protected and user-bound. Migrations preserve legacy observations under the bootstrap admin; account migration refuses destructive downgrade. Restore a complete stopped data/runtime snapshot for rollback.

Tests are inside `Watchboard.Api.Tests`, use isolated temporary SQLite and synthetic upstream responses, and exercise real middleware/setup/login cookies—no test auth bypass. Parser and comparison fixtures are inside that test repository tree. The small comparison fixture snapshot is also owned by the independent frontend repository; integration tooling can compare them, but neither repo imports the other.

```sh
dotnet tool restore
dotnet ef migrations has-pending-model-changes --project Watchboard.Api.csproj
dotnet list Watchboard.Api.sln package --vulnerable --include-transitive
```

The design-time context is in-memory; generating/checking migrations does not open the normal database. Current additive contract: [docs/prices-v4-contract.md](docs/prices-v4-contract.md), preserving the [v3 location/config contract](docs/locations-prices-v3-contract.md) and [v2 baseline](docs/api-v2-contract.md). See [v4 backend/migration notes](docs/v4-backend-implementation.md) before applying to an existing installation. Frontend defaults to loopback port 5333, calling the API through its same-origin proxy. `X-Watchboard-User` is an expected-account assertion, never an auth credential. No public registration, external notifications, native widget tokens or remote deployment configured.

No distribution license has been selected. No secrets, environment credentials, generated builds, runtime keys or database files belong in Git.
