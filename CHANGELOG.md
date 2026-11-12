# Changelog

## Unreleased

- Match equivalent sequel notation (Roman/Arabic numerals, comma-separated thousands, and numeric “K” shorthand) only when the complete product title agrees.
- Discover public marketplace pages for edition-qualified Steam titles, including variants listed on a base product page; preserve the selected edition and independent price history.

- Apply a newly created game's initial target to both newly created sources, preserving existing goals and subsequent independent edits.

- Recognize Early Access as the base release phase and match explicitly published product editions to their exact Steam titles.
- Retry previous unmatched sources on forced price refresh without overriding explicit opt-outs; log individual source failures.

- Edit software and game watches in place with owner-scoped validation and preserved history, IDs and installation provenance.
- Protect historical price definitions and reject stale source results after concurrent edits.

## 1.0.0 — 2026-11-10

- Software release tracking with installed-version comparison and canonical locations.
- Private accounts, per-user widgets and portable configuration.
- Reciprocal Steam/AllKeyShop tracking with platform-aware key/gift offers and fee-inclusive totals.
- Daily or manual collection, dated observed minima and explicit history coverage.
- SQLite migrations preserve existing records; back up the complete data directory before upgrading.
- Non-root Docker images for Linux amd64, automated GHCR publication and explicit container origin controls.

Local or explicitly configured trusted-LAN use, not public Internet hosting. External two-year price backfill and remote device agents are not included.
