# RestaurantOS

Multi-tenant restaurant management SaaS: browser POS for dine-in, takeaway and delivery, live
kitchen display, split-payment settlement with sequential GST invoices, menu and outlet management,
staff roles, cash sessions and audit logs. Inventory, CRM, QR ordering, reports and integrations
are next on the [roadmap](docs/ROADMAP.md).

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
- [Agent instructions](AGENTS.md)
