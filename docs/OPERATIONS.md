# Operations runbook

How to configure, back up, restore and observe a RestaurantOS deployment. Nothing here has been run
against a production system; the backup and restore commands were exercised against a local
PostgreSQL 16 database (see `docs/PROGRESS.md`).

## Configuration

Settings are read from `appsettings.json`, then environment variables (`Section__Key`). Secrets must
come from the environment or a secret store, never from committed files.

| Variable | Required | Notes |
| --- | --- | --- |
| `ConnectionStrings__Default` | yes | PostgreSQL connection string. Use a role that owns the schema only. |
| `Jwt__SigningKey` | yes | At least 32 random characters (`openssl rand -base64 48`). Production refuses the development key. Rotating it signs everyone out. |
| `Database__MigrateOnStartup` | no | `true` applies EF Core migrations at boot. For multi-instance deployments, run migrations as a release step instead and leave this `false`. |
| `Cors__Origins__0..n` | when the web app is on another origin | Exact origins allowed to call the API. |
| `ReverseProxy__KnownProxies__0..n` / `ReverseProxy__KnownNetworks__0..n` | behind a proxy | IPs or CIDRs whose `X-Forwarded-For` / `X-Forwarded-Proto` are trusted. Without them, rate limiting sees the proxy's IP for every client. |
| `RateLimiting__AuthPermitPerMinute`, `RateLimiting__PublicPermitPerMinute` | no | Per client IP; defaults 20 and 60. |
| `OTEL_EXPORTER_OTLP_ENDPOINT` | no | Enables OpenTelemetry traces and metrics over OTLP. `OTEL_SERVICE_NAME` defaults to `restaurantos-api`; other standard `OTEL_*` variables apply. |
| `Integrations__EnableMockSimulators`, `Integrations__MockPaymentWebhookSecret` | no | Test-mode partner and payment simulators. The API refuses to start in `Production` with simulators enabled. |

Outside Development the API logs JSON to stdout (one object per line, UTC timestamps, with
`CorrelationId` in scopes; send `X-Correlation-Id` to correlate a request end to end) and sends HSTS
on HTTPS responses. The web container's nginx adds a strict Content-Security-Policy and other
security headers (`frontend/deploy/security-headers.conf`).

## Health

- `GET /health` returns `Healthy` when the API can reach the database. Use it for load-balancer and
  container health checks.

## Backups

All state is in PostgreSQL; the API and web containers are stateless.

Minimum policy for a paying tenant base:

1. **Nightly logical backup** with `pg_dump` in custom format, kept for at least 30 days, copied to
   storage in a different account or region from the database:

   ```bash
   pg_dump --format=custom --compress=6 --no-owner \
     --dbname="$DATABASE_URL" --file="restaurantos-$(date -u +%Y%m%dT%H%M%SZ).dump"
   ```

2. **Point-in-time recovery** (continuous WAL archiving) through the managed database provider, or
   `archive_mode`/`archive_command` with a tool such as pgBackRest when self-hosting. This bounds data
   loss to minutes rather than a day, which matters because bills and payments are written all day.
3. Encrypt backups at rest; they contain customer phone numbers and staff password hashes.

## Restore

1. Stop the API (or scale it to zero) so no writes land during the restore.
2. Restore into a **new** database, never over the live one:

   ```bash
   createdb --host=$HOST --username=$USER restaurantos_restore
   pg_restore --host=$HOST --username=$USER --dbname=restaurantos_restore \
     --no-owner --exit-on-error restaurantos-YYYYMMDDTHHMMSSZ.dump
   ```

3. Verify before switching over:
   - `select migration_id from __ef_migrations_history order by 1 desc limit 1;` matches the
     latest migration of the API version being deployed (newer API versions migrate forward on boot
     when `Database__MigrateOnStartup` is true).
   - Row counts of `tenants`, `orders`, `payments`, `settlements`, `stock_ledger` and `audit_logs` are
     plausible for the backup time.
   - The day-end report for the last full business day matches what outlets reported.
4. Point `ConnectionStrings__Default` at the restored database and start the API. Check `/health`,
   sign in, and open the POS for one outlet.
5. Keep the old database read-only until the restore is confirmed, then drop it.

Practise a restore at least quarterly and record the date and duration.

## Incident notes

- **Duplicate charge reported**: settlements are idempotent per `Idempotency-Key` and payments per
  settlement; check `audit_logs` for `order.settled` on the order and `payment_intents` / `inbound_events`
  for the provider reference before refunding.
- **Partner says an order was not received**: look up `inbound_events` by provider and external id.
  Re-sending the same external id is always safe.
- **Leaked API key**: revoke it on the Integrations page; requests fail immediately. Issue a new key.
