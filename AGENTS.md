# AGENTS.md — instructions for AI/human contributors

RestaurantOS is a multi-tenant restaurant management SaaS. Read these before changing code:

1. `docs/ROADMAP.md` — living checklist with phase acceptance criteria.
2. `docs/PROGRESS.md` — what was done, verified test results, blockers, **next action**.
3. `docs/ARCHITECTURE.md` and `docs/adr/*` — accepted decisions. Do not contradict an ADR without
   adding a new ADR that supersedes it.

## Resume protocol
- Start by reading `docs/PROGRESS.md` → "Next action". Continue from there; do not redo completed items.
- Work in small vertical slices: domain → persistence/migration → service/endpoint → UI → tests → docs.
- After each slice: `dotnet build`, `dotnet test`, `npm run lint && npm run build` (frontend),
  Playwright when UI flows change. Record **actual** results in `docs/PROGRESS.md`.
- Never tick a roadmap item that is not implemented and verified.

## Stack
- Backend: .NET 10, ASP.NET Core minimal APIs, EF Core 10 + Npgsql, SignalR, xUnit.
- Frontend: React 19, TypeScript, Vite, Tailwind CSS v4, TanStack Query, React Router, Playwright.
- DB: PostgreSQL 16+. Local dev without Docker: see `docs/DEVELOPMENT.md`.

## Non-negotiable rules
- Every tenant-owned entity implements `ITenantScoped`; every outlet route goes through the outlet
  access filter with an explicit `Permission`. Cross-tenant access must return 404.
- Money is `decimal`; use `BillCalculator` / `Money.Round` — never ad-hoc rounding.
- Order lines store pricing snapshots; never recompute historical bills from current menu data.
- Financial and stock writes are transactional and idempotent (see ADR-0004).
- Stock ledger is append-only; corrections are new entries.
- Sensitive actions (discounts, voids, settlement, refunds, role changes, stock adjustments) write
  an audit log entry.
- No secrets or real customer data in the repo. Third-party integrations use mock adapters until
  the product owner supplies credentials.
- Do not copy proprietary code, branding or private APIs from Petpooja or any other vendor.
- Do not deploy, publish, purchase, or touch production systems without explicit owner approval.

## Commands
```bash
# backend
cd backend && dotnet build && dotnet test
dotnet ef migrations add <Name> -p src/RestaurantOS.Infrastructure -s src/RestaurantOS.Api -o Persistence/Migrations
# frontend
cd frontend && npm ci && npm run lint && npm run build && npm test
npx playwright test   # needs API on :5080 and web on :5173 (see docs/DEVELOPMENT.md)
```
