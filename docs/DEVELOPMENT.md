# Development guide

## Prerequisites

- .NET SDK 10.0.100 or later (`backend/global.json` pins the feature band).
- Node.js 22 or later.
- PostgreSQL 16 (local install or the `postgres` service from `docker-compose.yml`).

The default local database credentials are user `restaurantos`, password `restaurantos`. The role
needs `CREATEDB`, because integration tests create a throwaway database per test run:

```sql
CREATE ROLE restaurantos LOGIN PASSWORD 'restaurantos' CREATEDB;
CREATE DATABASE restaurantos OWNER restaurantos;
```

## Run locally

```bash
# API on http://localhost:5080 (applies migrations on startup in Development)
cd backend
dotnet run --project src/RestaurantOS.Api

# Web on http://localhost:5173 (proxies /api, /hubs and /health to the API)
cd frontend
npm install
npm run dev
```

Open http://localhost:5173/signup, create a restaurant with the sample menu, and you land on the POS.
The OpenAPI document is served at `/openapi/v1.json` in Development.

## Tests

| Suite | Command | Needs |
| --- | --- | --- |
| Domain unit tests | `dotnet test --project tests/RestaurantOS.UnitTests` (in `backend/`) | nothing |
| API integration tests | `dotnet test --project tests/RestaurantOS.IntegrationTests` (in `backend/`) | PostgreSQL |
| Frontend lint and types | `npm run lint && npm run build` (in `frontend/`) | nothing |
| End-to-end | `npx playwright test` (in `frontend/`) | PostgreSQL, Chromium (`npx playwright install chromium`) |

Integration tests connect with `RESTAURANTOS_TEST_PG` (default
`Host=localhost;Port=5432;Database=postgres;Username=restaurantos;Password=restaurantos`), create a
database named `restaurantos_it_<guid>` for the run and drop it afterwards.

Playwright starts the API (against database `restaurantos_e2e`, override with `E2E_DB`) and the Vite
dev server itself, or reuses ones already running on ports 5080/5173 outside CI. Each test signs up
a fresh tenant, so no database reset is needed.

The test projects use xUnit v3 on Microsoft Testing Platform (configured in `global.json`), so
`dotnet test` needs `--project`.

## Migrations

```bash
cd backend
dotnet tool install --global dotnet-ef   # once
ConnectionStrings__Default="Host=localhost;Database=restaurantos;Username=restaurantos;Password=restaurantos" \
  dotnet ef migrations add <Name> --project src/RestaurantOS.Infrastructure --startup-project src/RestaurantOS.Infrastructure \
  --output-dir Persistence/Migrations
```

Migrations are applied automatically when `Database:MigrateOnStartup` is true (Development and the
compose stack). In other environments apply them as an explicit release step.

## Configuration

| Key | Purpose |
| --- | --- |
| `ConnectionStrings:Default` | PostgreSQL connection string (required) |
| `Jwt:SigningKey` | HMAC key, at least 32 characters; keys starting with `dev-` are rejected in Production |
| `Jwt:AccessTokenMinutes`, `Jwt:RefreshTokenDays` | Token lifetimes (15 minutes / 30 days by default) |
| `Cors:Origins` | Allowed browser origins |
| `RateLimiting:AuthPermitPerMinute`, `RateLimiting:PublicPermitPerMinute` | Per-IP limits for auth and public endpoints |
| `Integrations:MockPaymentWebhookSecret`, `Integrations:EnableMockSimulators` | Mock payment and aggregator adapters for demos |

Never commit real secrets. Use environment variables or a secret store; `.env` files are git-ignored.

## Docker

`docker compose up --build` builds the API and web images and serves the app on
http://localhost:8080 (copy `.env.example` to `.env` and set `JWT_SIGNING_KEY` first). The images
have not been built in the agent environment, which has no Docker daemon; CI does not build them yet.
