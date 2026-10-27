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

- [ ] Repository layout (`backend/`, `frontend/`, `docs/`, `deploy/`)
- [ ] ASP.NET Core 10 Web API host with ProblemDetails, OpenAPI, health checks
- [ ] PostgreSQL via EF Core 10 + Npgsql, migrations, snake_case naming
- [ ] React 19 + TypeScript + Vite + Tailwind shell
- [ ] Docker Compose (postgres, api, web)
- [ ] GitHub Actions CI (backend build+tests against Postgres service, frontend lint/build, Playwright)
- [ ] Developer documentation (`README.md`, `docs/DEVELOPMENT.md`)

Acceptance: `dotnet test` passes, `npm run build` passes, `/health` returns healthy against Postgres.

## Phase 1 — Tenancy, authentication, staff roles

- [ ] Self-serve signup creates tenant + owner + first outlet (trial subscription)
- [ ] JWT access tokens + rotating refresh tokens, password hashing (PBKDF2 via ASP.NET Identity hasher)
- [ ] Roles (Owner, Manager, Cashier, Waiter, Kitchen) and permission matrix
- [ ] Tenant isolation via EF global query filters + SaveChanges guard
- [ ] Outlet-level authorization on every outlet route
- [ ] Staff management (invite/create, assign roles per outlet, deactivate)
- [ ] Audit log for security-sensitive actions

Acceptance: integration tests prove tenant A cannot read/write tenant B data and that outlet
assignment restricts access.

## Phase 2 — Menu and POS ordering

- [ ] Tax groups with components (e.g. CGST/SGST), inclusive/exclusive pricing per outlet
- [ ] Categories, items, variants, modifier groups (min/max), food type, kitchen station
- [ ] Outlet availability overrides (in stock / out of stock, price override)
- [ ] Tables and areas
- [ ] Orders: dine-in, takeaway, delivery; pricing snapshots on each line
- [ ] Order discounts (percent/flat) with permission + reason, audited
- [ ] Deterministic bill calculator (unit tested)
- [ ] Optimistic concurrency on orders, client-generated idempotent order creation

## Phase 3 — Kitchen and payments

- [ ] KOT generation per station when items are sent; KOT numbering per outlet per day
- [ ] Live kitchen display (SignalR) with status flow New → Preparing → Ready → Served
- [ ] Line void after KOT (permission + reason), cancellation KOT
- [ ] Atomic settlement: split payments, idempotency key, sequential invoice number per outlet per financial year
- [ ] Printable invoice / KOT views
- [ ] Cash sessions (shift open/close, expected vs counted)

Acceptance: Playwright test drives POS → KOT on kitchen display → settle → invoice.

## Phase 4 — Inventory and procurement

- [ ] Raw materials with units and reorder levels
- [ ] Append-only stock ledger with unique source references (no duplicate effects)
- [ ] Recipes per item/variant; consumption posted on settlement
- [ ] Suppliers, purchase orders, goods receipt into stock
- [ ] Wastage and manual adjustments (audited)
- [ ] Low-stock report

## Phase 5 — CRM, loyalty, QR ordering, reports

- [ ] Customers (phone unique per tenant), visit/spend stats
- [ ] Loyalty earn on settlement, redeem as payment method, loyalty ledger
- [ ] QR table ordering (public, token per table) → staff accepts → KOT
- [ ] Reports: sales summary, item sales, category sales, payment mix, tax summary, hourly
- [ ] Day-end reconciliation report (payments vs cash sessions)
- [ ] Dashboard with live KPIs from real data

## Phase 6 — Subscriptions and integrations

- [ ] Plan catalog (Starter / Growth / Scale) with outlet and staff limits enforced
- [ ] Subscription status (trial/active/past-due/cancelled), mock billing provider
- [ ] Tenant API keys (hashed, scoped) for authorized external integrations
- [ ] Delivery-aggregator webhook with HMAC signature and idempotent ingestion (mock adapter)
- [ ] Payment-gateway abstraction with mock adapter (UPI/online intents)

## Phase 7 — Production hardening

- [ ] Rate limiting on auth/public endpoints
- [ ] Security headers, CORS allow-list, structured logging, correlation IDs
- [ ] Dockerfiles (multi-stage) and compose for local/prod-like runs
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
