# RestaurantOS

Multi-tenant restaurant management SaaS: browser POS for dine-in, takeaway and delivery, live
kitchen display, split-payment settlement with sequential GST invoices, menu and multi-outlet
management, inventory with recipes and purchasing, customers and loyalty, QR table ordering, sales
and day-end reports, staff roles, cash sessions, audit logs, subscription plans, a partner order API
with scoped API keys, and online payment links. Payment, billing and delivery-partner providers run
on built-in test adapters until real provider accounts are connected (see the [roadmap](docs/ROADMAP.md)).

| Layer | Technology |
| --- | --- |
| Frontend | React 19, TypeScript, Vite, Tailwind CSS, TanStack Query, SignalR client |
| Backend | ASP.NET Core 10 minimal APIs (modular monolith), EF Core 10 + Npgsql, SignalR |
| Database | PostgreSQL 16 |
| Tests | xUnit v3 (domain and PostgreSQL integration), Playwright (end-to-end) |

## Quick start

```bash
cd backend && dotnet run --project src/RestaurantOS.Api   # http://localhost:5080
cd frontend && npm install && npm run dev                  # http://localhost:5173
```

See [docs/DEVELOPMENT.md](docs/DEVELOPMENT.md) for prerequisites, tests, migrations and configuration.

## Documentation

- [Architecture](docs/ARCHITECTURE.md) and [decision records](docs/adr/)
- [Roadmap and checklist](docs/ROADMAP.md)
- [Progress log](docs/PROGRESS.md)
- [Operations runbook](docs/OPERATIONS.md): configuration, backups, restore
- [Agent instructions](AGENTS.md)
