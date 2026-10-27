# ADR-0002: Shared-schema multi-tenancy with query filters

Status: Accepted (2026-10-27)

## Context
Thousands of small restaurants, each with 1–N outlets. Cost per tenant must be low; isolation must
be strong.

## Decision
- Shared database and schema; every tenant-owned table has `tenant_id`.
- EF Core global query filters on `ITenantScoped` entities, keyed by `ITenantContext.TenantId`.
- `SaveChanges` guard: stamps tenant on insert, rejects cross-tenant modifications.
- Outlet authorization via endpoint filter on `/api/outlets/{outletId}` route groups.
- Integration tests exercise cross-tenant access attempts and must return 404 (not 403) so tenants
  cannot probe for each other's IDs.

## Alternatives considered
- Database-per-tenant: strongest isolation, but operational overhead too high for SMB pricing.
- PostgreSQL RLS: good defence-in-depth; deferred (planned in Phase 7) because EF filters + tests
  already cover the application path and RLS complicates migrations and connection pooling.

## Consequences
- Any raw SQL must include `tenant_id` explicitly; reviewed in code review.
- `IgnoreQueryFilters()` is only allowed in auth/public/webhook resolution code.
