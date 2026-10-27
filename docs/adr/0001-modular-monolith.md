# ADR-0001: Modular monolith on ASP.NET Core + PostgreSQL

Status: Accepted (2026-10-27)

## Context
RestaurantOS needs POS, kitchen, inventory, CRM, reporting and billing. Most workflows (settlement)
touch several of these in one business transaction. The team is small and the product is pre-PMF.

## Decision
- One ASP.NET Core 10 API process, one PostgreSQL database, one `AppDbContext`.
- Code organised by module under `RestaurantOS.Api/Modules/<Module>`; domain entities in
  `RestaurantOS.Domain/<Module>`.
- Cross-module writes go through the owning module's service; reads may join across modules.
- Minimal APIs instead of MVC controllers (less ceremony, endpoint filters for authorization).

## Consequences
- Settlement can be a single ACID transaction (no sagas/outbox needed yet).
- Modules can be extracted later because boundaries are explicit in folders and services.
- A single DbContext means migrations are global; acceptable at this scale.
