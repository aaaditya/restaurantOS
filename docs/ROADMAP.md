# RestaurantOS Roadmap

RestaurantOS is a multi-tenant restaurant management SaaS (POS, kitchen, inventory, CRM,
reporting). It takes inspiration from publicly observable restaurant-POS workflows; every line of
design and code here is original.

This file is the **living checklist**. Tick items only when they are implemented *and* verified by
the tests or checks listed in `docs/PROGRESS.md`. Each phase lists acceptance criteria; a phase is
done when all criteria are met.

Legend: `[x]` done and verified, `[~]` partially done, `[ ]` not started.

---

## Phase 0 — Foundation

- [x] Repository layout (`backend/`, `frontend/`, `docs/`, `.github/`)
- [x] ASP.NET Core 10 Web API host with ProblemDetails, OpenAPI, health checks
- [x] PostgreSQL via EF Core 10 + Npgsql, migrations, snake_case naming
- [x] React 19 + TypeScript + Vite + Tailwind shell
- [~] Docker Compose (postgres, api, web)
- [x] GitHub Actions CI (backend build+tests against Postgres service, frontend lint/build, Playwright)
- [x] Developer documentation (`README.md`, `docs/DEVELOPMENT.md`)

Acceptance: `dotnet test` passes, `npm run build` passes, `/health` returns healthy against Postgres.

## Phase 1 — Tenancy, authentication, staff roles

- [x] Self-serve signup creates tenant + owner + first outlet (trial subscription)
- [x] JWT access tokens + rotating refresh tokens, password hashing (PBKDF2 via ASP.NET Identity hasher)
- [x] Roles (Owner, Manager, Cashier, Waiter, Kitchen) and permission matrix
- [x] Tenant isolation via EF global query filters + SaveChanges guard
- [x] Outlet-level authorization on every outlet route
- [x] Staff management (invite/create, assign roles per outlet, deactivate)
- [x] Audit log for security-sensitive actions

Acceptance: integration tests prove tenant A cannot read/write tenant B data and that outlet
assignment restricts access.

## Phase 2 — Menu and POS ordering

- [x] Tax groups with components (e.g. CGST/SGST), inclusive/exclusive pricing per outlet
- [x] Categories, items, variants, modifier groups (min/max), food type, kitchen station
- [x] Outlet availability overrides (in stock / out of stock, price override)
- [x] Tables and areas
- [x] Orders: dine-in, takeaway, delivery; pricing snapshots on each line
- [x] Order discounts (percent/flat) with permission + reason, audited
- [x] Deterministic bill calculator (unit tested)
- [x] Optimistic concurrency on orders, client-generated idempotent order creation

## Phase 3 — Kitchen and payments

- [x] KOT generation per station when items are sent; KOT numbering per outlet per day
- [x] Live kitchen display (SignalR) with status flow New → Preparing → Ready → Served
- [x] Line void after KOT (permission + reason), cancellation KOT
- [x] Atomic settlement: split payments, idempotency key, sequential invoice number per outlet per financial year
- [x] Printable invoice view (KOTs shown on the kitchen display; ticket printing is Phase 7)
- [x] Cash sessions (shift open/close, expected vs counted)

Acceptance: Playwright test drives POS → KOT on kitchen display → settle → invoice.

## Phase 4 — Inventory and procurement

- [x] Raw materials with units and reorder levels
- [x] Append-only stock ledger with unique source references (no duplicate effects)
- [x] Recipes per item/variant; consumption posted on settlement
- [x] Suppliers, purchase orders, goods receipt into stock
- [x] Wastage and manual adjustments (audited)
- [x] Low-stock report

## Phase 5 — CRM, loyalty, QR ordering, reports

- [x] Customers (phone unique per tenant), visit/spend stats, search/sort, profile with bills and points history
- [x] Loyalty earn on settlement, redeem as payment method (row-locked, cannot overspend), manual adjustments, loyalty ledger
- [x] QR table ordering (public, token per table, rate-limited) → staff accepts live → KOT; merges into a running table order; guest tracking
- [x] Reports: sales summary, item sales, category sales, payment mix, tax summary (taxable value per rate), daily, hourly, leakage; CSV export
- [x] Day-end reconciliation report (payments vs settled bills, cash sessions, variance, closing checklist)
- [x] Dashboard with KPIs from real data across permitted outlets (today, 7-day trend, top items, attention flags)
- [x] Audit log viewer (role-scoped, filter by area/outlet, paginated)

## Phase 6 — Subscriptions and integrations

- [x] Plan catalog (Starter / Growth / Scale) with outlet and staff limits enforced
- [x] Subscription status (trial/active/past-due/cancelled) enforced on writes; billing page with mock billing provider (subscribe, switch, cancel, resubscribe; audited)
- [x] Tenant API keys (hashed, scoped, outlet-restrictable, revocable, plan-gated) for authorized external integrations
- [x] External order ingestion API (API key + scope), idempotent by provider/external id, partner orders await acceptance on the POS; mock delivery-partner simulator
- [x] Payment-gateway abstraction with mock adapter: payment links (intents) per order, HMAC-signed and deduplicated webhooks, settlement requires a confirmed intent; test checkout page

Acceptance: Playwright test upgrades the plan, creates an API key, pushes a partner order (with retry), accepts it, settles it through a payment link paid on the test checkout, revokes the key, cancels and resubscribes.

## Phase 7 — Production hardening

- [x] Rate limiting on auth/public endpoints
- [~] Security headers, CORS allow-list, structured logging, correlation IDs
- [~] Dockerfiles (multi-stage) and compose for local/prod-like runs
- [ ] Backups/restore runbook (documented, not automated)
- [ ] Observability exporters (OpenTelemetry) — planned
- [ ] Offline-tolerant POS (queued writes) — planned
- [ ] Real payment gateway / aggregator adapters — **blocked on credentials and partner access**
- [ ] Thermal printer integration (ESC/POS bridge) — planned
- [ ] Multi-language invoices / e-invoicing (IRN) — planned, needs GST suvidha provider access

## Backlog (post-MVP)

- Central kitchen / inter-outlet stock transfers
- Menu scheduling (time-based availability), combos
- Reservations and waitlist
- Feedback collection, campaigns (SMS/WhatsApp — needs provider credentials)
- Accounting exports (Tally/Zoho) — needs product decision on target
