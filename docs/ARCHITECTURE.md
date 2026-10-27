# RestaurantOS Architecture

## Overview

```
┌────────────────────────┐     HTTPS/JSON + SignalR      ┌──────────────────────────────┐
│ frontend (React/Vite)  │ ─────────────────────────────▶│ RestaurantOS.Api (ASP.NET 10) │
│  - Back office         │                               │  Modules/*  (endpoints+svc)   │
│  - POS                 │◀───── /hubs/outlet ───────────│  Realtime/OutletHub           │
│  - Kitchen display     │                               └──────────────┬───────────────┘
│  - Public QR ordering  │                                              │ EF Core 10 / Npgsql
└────────────────────────┘                                              ▼
                                                               ┌─────────────────┐
                                                               │ PostgreSQL 16+  │
                                                               └─────────────────┘
```

RestaurantOS is a **modular monolith** (ADR-0001). One deployable API process, one database, with
code organised by business module. Modules talk through application services in-process; there is
no message bus. Real-time fan-out to POS and kitchen screens uses SignalR groups per outlet.

## Backend projects

| Project | Responsibility |
| --- | --- |
| `RestaurantOS.Domain` | Entities, enums, invariants, the pure `BillCalculator`. No EF/ASP.NET dependencies. |
| `RestaurantOS.Infrastructure` | `AppDbContext`, entity configurations, migrations, tenant query filters, counters. |
| `RestaurantOS.Api` | HTTP endpoints (minimal APIs) grouped per module, auth, SignalR, application services. |
| `RestaurantOS.UnitTests` | Pure domain tests (pricing, tax, rounding, state transitions). |
| `RestaurantOS.IntegrationTests` | `WebApplicationFactory` tests against a real PostgreSQL database. |

### Modules (in `RestaurantOS.Api/Modules`)

| Module | Owns |
| --- | --- |
| `Tenancy` | Tenants, outlets, signup, outlet settings |
| `Identity` | Users, role assignments, login/refresh, staff management |
| `Menu` | Tax groups, categories, items, variants, modifiers, outlet overrides |
| `Tables` | Dining areas/tables, QR tokens |
| `Orders` | Order lifecycle, discounts, pricing snapshots |
| `Kitchen` | KOTs and kitchen display state |
| `Payments` | Settlement, payments, invoices, cash sessions, gateway abstraction |
| `Inventory` | Raw materials, stock ledger, recipes, suppliers, purchase orders, wastage |
| `Customers` | Customers, loyalty ledger |
| `Reports` | Read-only reporting queries |
| `Audit` | Audit log write/read |
| `Billing` | Subscription plans and limits, mock billing provider |
| `Integrations` | API keys, aggregator webhooks (mock adapter) |
| `Public` | Anonymous QR ordering endpoints |

Each module exposes `MapXxxEndpoints(this IEndpointRouteBuilder)` and registers its services in
`AddXxxModule(this IServiceCollection)`. Modules may read other modules' tables through the shared
`AppDbContext` for queries, but **writes to another module's aggregate go through that module's
service** (e.g. settlement calls `InventoryService.PostConsumptionAsync`).

## Multi-tenancy (ADR-0002)

- Every tenant-owned row implements `ITenantScoped` (`tenant_id` column, indexed).
- `AppDbContext` applies a global query filter `TenantId == currentTenant` to all `ITenantScoped`
  entities. The tenant comes from the authenticated JWT (`tid` claim) via `ITenantContext`.
- `SaveChanges` stamps `TenantId` on inserts and throws if a tracked entity belongs to a different
  tenant (defence in depth).
- Outlet-scoped routes live under `/api/outlets/{outletId}/…`. An endpoint filter verifies the
  outlet belongs to the tenant **and** the user has an assignment for that outlet (owners are
  tenant-wide), then checks the required permission.
- Anonymous flows (QR ordering, webhooks) resolve the tenant from an unguessable token / API key and
  set the tenant context explicitly.

## Money, tax and rounding (ADR-0003)

- All money is `decimal` (`numeric(18,2)`); rates and quantities `numeric(18,4)`.
- `BillCalculator` is a pure function: lines + tax components + discount + outlet settings →
  per-line and order totals. Rounding is `MidpointRounding.AwayFromZero` to 2 dp per line per tax
  component; the grand total is optionally rounded to the nearest rupee with an explicit `round_off`.
- Supports tax-exclusive and tax-inclusive menu prices (outlet setting).
- Order-level discounts are allocated to lines before tax; remainder goes to the largest line.
- Each order line snapshots item name, variant, modifiers, unit price and tax components, so later
  menu edits never change historical bills.

## Financial integrity (ADR-0004)

- Settlement is a single database transaction: payments, invoice number, order status, stock
  consumption, loyalty and customer stats commit together or not at all.
- Idempotency: order creation uses a client-generated `clientOrderId`; settlement and payments use
  an `Idempotency-Key`. Both are unique-indexed so retries return the original result.
- Stock ledger rows carry `(source_type, source_id, inventory_item_id)` unique keys, preventing
  duplicate consumption or receipt.
- Orders use PostgreSQL `xmin` as an optimistic concurrency token; conflicting edits return 409.
- Invoice/KOT/order numbers come from a per-outlet counter table incremented with
  `INSERT … ON CONFLICT DO UPDATE … RETURNING` inside the transaction (gap-free per commit).

## Authentication (ADR-0005)

- Email + password; hashes via ASP.NET Core Identity `PasswordHasher` (PBKDF2).
- Short-lived JWT access tokens (claims: `sub`, `tid`, `name`, `email`) + rotating opaque refresh
  tokens stored hashed. Refresh-token reuse revokes the whole token family.
- Authorization data (roles per outlet) is loaded per request from the database (cached per
  request), so revocations take effect immediately rather than at token expiry.

## QR ordering

- `/api/public/tables/{qrToken}` is anonymous and rate-limited (`public` policy). The token resolves the
  table, outlet and tenant; ordering is open only when the outlet enables it and the subscription is usable.
- The guest's client-generated `clientOrderId` makes placement idempotent and is the credential for
  tracking that one order. Guest orders are `AwaitingAcceptance` and send no KOT until staff accept.
- Accepting a QR order for a table with a running open order copies its lines into that order (sending
  them to the kitchen) and cancels the QR order with `merged_into_order_id`, so one table has one bill.

## Reporting

- Reports read settled orders' stored line amounts, never current menu prices. Net sales are the
  pre-tax amounts after discounts (sum of line `taxable`); collected is the grand total including tax
  and round-off. Business dates are outlet-local.
- Day-end reconciliation compares payments with settled bills for the business date, and lists cash
  sessions overlapping that date with expected cash, counted cash and variance.
- The dashboard and audit log include only outlets where the caller's roles grant `ReportsView` or
  `AuditView`; outlet-level auditors never see tenant-wide entries.

## Real-time

`/hubs/outlet` (SignalR). Clients call `JoinOutlet(outletId)`; the hub verifies access and adds the
connection to group `outlet:{id}`. Server events: `kotChanged`, `orderChanged`, `qrOrderPlaced`,
`stockChanged`. Payloads are small; clients refetch via HTTP for full state.

## Frontend

- `frontend/src/app` — router, providers, auth context, API client.
- `frontend/src/features/*` — one folder per module (pos, kitchen, menu, inventory, …).
- Data fetching with TanStack Query; SignalR events invalidate queries.
- Tailwind CSS v4; accessible components (labelled inputs, focus rings, keyboard support).
- `frontend/e2e` — Playwright tests against the real API + database.
