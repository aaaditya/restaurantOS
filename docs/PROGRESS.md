# Progress log

Newest entries first. Record only what was actually run; include the command and the result.

## 2026-10-27 — Phases 0–3: foundation, tenancy, menu/POS, kitchen and payments

### Completed

- Backend modular monolith: domain model, EF Core/PostgreSQL persistence with tenant query filters
  and a SaveChanges tenant guard, JWT auth with rotating refresh tokens, staff roles and permission
  matrix, plan limits and the subscription write block (HTTP 402).
- Menu (tax groups, categories, items, variants, modifier groups, outlet availability and price
  overrides), tables with QR tokens, orders with pricing snapshots, optimistic concurrency and
  idempotent creation, discounts, voids with cancellation KOTs, per-station KOTs and the SignalR hub.
- Settlement: split payments, idempotency keys, row lock, invoice numbers `PREFIX/FY/000001` per outlet
  per financial year, cash session linkage and variance on close.
- Frontend: login and signup, POS home (tables board, takeaway and delivery lists, QR order acceptance),
  order screen (item configurator, KOT, discount, customer, bill, reopen, void, cancel, split
  settlement), printable invoice, orders list, kitchen display, and the Menu, Tables and QR,
  Cash register, Staff and Settings pages.
- CI workflow (`.github/workflows/ci.yml`), Dockerfiles, `docker-compose.yml`, developer docs.

### Verified (commands run in the agent VM, PostgreSQL 16 local)

| Check | Result |
| --- | --- |
| `dotnet build -warnaserror` (backend) | 0 warnings, 0 errors |
| `dotnet test --project tests/RestaurantOS.UnitTests` | 35/35 passed |
| `dotnet test --project tests/RestaurantOS.IntegrationTests` | 22/22 passed |
| `npm run lint` (frontend) | 0 errors, 7 `only-export-components` warnings (hook and component co-located) |
| `npm run build` (frontend) | passed |
| `npx playwright test` | 2/2 passed: POS to kitchen display to split settlement to invoice; admin setup (table, menu item, outlet unavailability, staff) plus a cashier's restricted navigation |

### Not yet verified

- The CI workflow has not run yet; it runs on the pull request.
- Docker images and compose were not built: the agent VM has no Docker daemon.

### Remaining work

Phase 4 (inventory and procurement), Phase 5 (customers and loyalty, QR ordering, reports and dashboard,
audit log viewer), Phase 6 (billing, API keys, external orders, mock payment gateway and aggregator)
and Phase 7 hardening (forwarded headers behind the proxy, OpenTelemetry, backup runbook).

### Blockers

None. Real payment gateway and aggregator adapters need credentials and partner access; mock
adapters will be used.

### Next concrete action

Implement Phase 4: inventory item, stock ledger and adjustment endpoints, recipes with consumption
on settlement, suppliers and purchase orders with goods receipt, plus integration tests proving
no duplicate stock effects.
