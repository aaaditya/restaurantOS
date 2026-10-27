# Progress log

Newest entries first. Record only what was actually run; include the command and the result.

## 2026-10-27 — Phase 7: production hardening

### Completed

- API: forwarded headers trusted only from configured proxies/networks (rate limits partition by the
  real client IP), HSTS outside Development, JSON console logs with UTC timestamps and correlation-id
  scopes, OpenTelemetry traces and metrics over OTLP when `OTEL_EXPORTER_OTLP_ENDPOINT` is set, and a
  startup refusal when mock simulators are enabled in Production.
- Web: route-level code splitting; nginx serves a strict CSP and security headers on every static
  response, forwards client IPs on the SignalR route, gzip, no server tokens.
- Tables: printable QR code images (SVG download) for each table's ordering link.
- Fix found by the full e2e run: in the purchase order dialog, finishing "Mark as ordered" reset a
  receive form opened meanwhile, losing entered quantities. Other actions are now disabled while one
  is in flight and only receipt returns the dialog to view mode.
- `docs/OPERATIONS.md`: configuration reference, health, backup policy, restore and verification
  steps, incident notes. Compose passes the trusted proxy network and allows a Staging demo mode.

### Verified (commands run in the agent VM, PostgreSQL 16 local)

| Check | Result |
| --- | --- |
| `dotnet build -warnaserror` | 0 warnings, 0 errors |
| Unit / integration tests | 35/35 and 40/40 passed |
| `npm run lint` / `npm run build` | 0 errors (7 existing warnings) / passed; initial JS 341 kB (was 613 kB), no chunk-size warning |
| `npx playwright test` (Vite dev server) | 7/7 passed; inventory spec now also asserts actions are disabled while a status change is in flight; QR spec decodes the rendered QR image and checks it encodes the table link |
| Playwright 7/7 against the production bundle behind local nginx 1.24 using `frontend/deploy/nginx.conf` and the API in Staging (JSON logs, trusted proxy 127.0.0.1) | 7/7 passed; a page sweep logged no console errors or CSP violations |
| API with `ASPNETCORE_ENVIRONMENT=Production` and simulators enabled | refused to start with the intended message |
| OTLP export to a local OTLP/HTTP receiver | `/v1/traces` and `/v1/metrics` received with service `restaurantos-api` |
| `pg_dump -Fc` then `pg_restore` into a new database | row counts matched for every table checked (tenants, orders, payments, settlements, stock ledger, audit logs, payment intents, inbound events); migration history intact |

### Not yet verified

- Docker images and compose (no Docker daemon in the agent VM).

### Remaining work

Offline-tolerant POS, thermal printing and e-invoicing are planned post-MVP items. Real payment,
billing and delivery-partner adapters wait on provider choice and credentials.

### Blockers

Owner decisions and credentials needed for: payment gateway (for example Razorpay, Cashfree or
PayU), SaaS billing provider, delivery-partner API access, GST e-invoicing provider. Public
deployment needs owner approval and hosting choice.

### Next concrete action

Final MVP audit; then post-MVP items (offline POS queue, ESC/POS print bridge) unless the owner
supplies provider credentials.

## 2026-10-27 — Phase 6: subscriptions, API keys, partner orders, online payments

### Completed

- Billing: `/api/billing` (plan, status, usage, catalog), subscribe/switch with outlet and staff limit
  checks, cancel and resubscribe through an `IBillingProvider` (mock, no charge), all audited. A
  cancelled workspace is read-only (402 on writes) until it resubscribes. Billing page in the app.
- API keys: create (shown once, stored hashed, scoped, optionally restricted to one outlet), list,
  revoke; plan-gated to Growth/Scale. Integrations page with the order API reference.
- Partner order API `/api/v1/outlets/{id}/menu|orders|orders/{provider}/{externalId}`: idempotent per
  provider + external id (including concurrent duplicate deliveries), menu-priced, waits for acceptance
  on the POS (labelled with provider and external id, live via `externalOrderPlaced`). Development-only
  simulator button sends a test delivery-partner order.
- Online payments: payment links (intents) from the settle dialog, status polled until the HMAC-signed,
  deduplicated webhook confirms or fails them; settlement with `Online` requires a confirmed intent of
  the same amount. Development-only test checkout page `/pay/mock/:ref`.
- ADR-0006 records the integration, payment and billing provider decisions.
- Fixes: pending cards said "1 items"; the mock webhook returned 500 instead of 401 when no secret is
  configured.

### Verified (commands run in the agent VM, PostgreSQL 16 local)

| Check | Result |
| --- | --- |
| `dotnet build -warnaserror` (backend) | 0 warnings, 0 errors |
| `dotnet test --project tests/RestaurantOS.UnitTests` | 35/35 passed |
| `dotnet test --project tests/RestaurantOS.IntegrationTests` | 40/40 passed (new: plan change/cancel/resubscribe and 402 lock, API key scopes/outlet restriction/revocation/plan gate, partner order replay and concurrent duplicates, simulator permissions, payment intents with signature, replay and late-event handling) |
| `npm run lint` / `npm run build` (frontend) | 0 errors (7 existing warnings) / passed |
| `npx playwright test` | 7/7 passed (new: upgrade → API key → partner order with retry → accept → payment link paid on test checkout → settle → revoke → cancel → resubscribe) |

### Remaining work

Phase 7 hardening: forwarded headers behind a proxy, route code-splitting, OpenTelemetry, backup and
restore runbook.

### Blockers

Real payment gateway, billing provider and delivery-partner adapters need the owner to choose
providers and supply credentials or partner access. Mock adapters are used meanwhile.

### Next concrete action

Phase 7: forwarded headers, code-splitting, backup runbook, observability.

## 2026-10-27 — Phases 4–5: inventory, procurement, CRM, QR ordering, reports

### Completed

- Inventory: ingredients (unit locked once the ledger has entries), per-outlet stock balances backed by
  an append-only stock ledger, idempotent manual adjustments (opening, wastage, correction), recipes per
  item or variant with consumption on settlement, low-stock flags and stock valuation.
- Procurement: suppliers, purchase orders (draft, ordered, received, cancelled), partial-quantity goods
  receipt that posts purchase entries once and updates cost per unit.
- Customers and loyalty: customer CRUD and lookup, earn on settlement, redemption as a payment method
  under a customer row lock, manual adjustments (tenant-wide managers only), points history.
- QR ordering: public, rate-limited table endpoints (menu, place order, track). Guest orders wait for
  staff acceptance. Accepting on a table with a running order merges the items into that order (the QR
  order is cancelled with `mergedIntoOrderId`, and the guest still sees "accepted").
- Reports: sales (summary, order types, channels, payments, tax with taxable value per rate, items,
  categories, daily, hourly, cancellations and voids), day-end reconciliation, a multi-outlet dashboard
  limited to outlets the caller may report on, and a role-scoped audit log with cursor pagination.
- Frontend: Inventory, Purchasing, Customers, Reports (CSV export), Dashboard (owner home) and Audit
  log pages, the public guest ordering page `/qr/:token`, and loyalty redemption in the settle dialog.
  Cards now expose an accessible region name.
- Fixes found by tests: the accept toast never appeared because the pending card unmounted before
  per-call mutation callbacks ran (now uses `mutateAsync`), and guest tracking returned lines in an
  unspecified order (now ordered by creation).

### Verified (commands run in the agent VM, PostgreSQL 16 local)

| Check | Result |
| --- | --- |
| `dotnet build -warnaserror` (backend) | 0 warnings, 0 errors |
| `dotnet test --project tests/RestaurantOS.UnitTests` | 35/35 passed |
| `dotnet test --project tests/RestaurantOS.IntegrationTests` | 36/36 passed (new: inventory, QR and merge, loyalty incl. concurrent redemption, customers access, reports math, day-end, dashboard scoping, audit scoping and paging) |
| `npm run lint` (frontend) | 0 errors, 7 `only-export-components` warnings |
| `npm run build` (frontend) | passed (single bundle above 500 kB; route code-splitting is planned for Phase 7) |
| `npx playwright test` | 6/6 passed: POS to kitchen to settlement; admin setup; inventory and purchasing; QR guest order to live acceptance to settlement and points; invalid QR token; loyalty earn and redeem, customer adjustment, reports, day-end, dashboard and audit |

### Not yet verified

- Docker images and compose were not built: the agent VM has no Docker daemon.

### Remaining work

Phase 6 (billing page with the mock provider, tenant API keys, external order ingestion with HMAC and
idempotency, mock payment gateway) and Phase 7 hardening (forwarded headers, route code-splitting,
OpenTelemetry, backup runbook).

### Blockers

None. Real payment gateway and aggregator adapters need credentials and partner access; mock adapters
are used.

### Next concrete action

Phase 6: subscription and billing endpoints and page backed by a mock billing provider, then API keys
and the external orders webhook.

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
