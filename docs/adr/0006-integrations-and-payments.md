# ADR-0006: External integrations, payment gateway and billing providers

Status: Accepted (2026-10-27)

## Context
Delivery partners, online payment providers and the SaaS billing provider all need authorized
accounts and credentials that are not available during development. The product still needs the
full workflow (partner order to kitchen to settlement, payment link to confirmed payment, plan
changes) to work and be tested end to end.

## Decision
- **Partner orders** come in through a versioned, documented API (`/api/v1/outlets/{id}/…`)
  authenticated with tenant API keys, not through per-vendor webhooks. Keys are `ros_<prefix>.<secret>`;
  only the SHA-256 hash of the secret is stored and the full key is shown once. Keys carry scopes
  (`orders:write`, `menu:read`, `reports:read`), can be restricted to one outlet, and are revocable.
  Using them requires a plan with integrations. A vendor-specific adapter (with that vendor's own
  signature scheme) can translate into this API once partner access exists.
- Ingestion is idempotent per tenant + provider + external id: an inbound-event row with a unique
  key and a deterministic `clientOrderId` mean retries and concurrent duplicate deliveries return the
  original order. Prices always come from our menu; a partner's `expectedTotal` is only compared.
  Partner orders enter `AwaitingAcceptance` like QR orders.
- **Payment gateway**: `PaymentIntent` rows are created per order and amount, and become `Succeeded`
  or `Failed` only through a provider webhook verified with HMAC-SHA256 over the raw body and
  deduplicated by event id. A final status never changes. Settlement with method `Online` must
  reference a succeeded intent of the same order and amount. The mock gateway hosts a test
  checkout page that signs its own events through the same webhook handler.
- **Billing**: `IBillingProvider` abstracts the SaaS subscription provider. The mock provider
  activates plans immediately without charging. Plan changes validate outlet and staff limits.
- Mock simulators (`/api/dev/…`, test checkout) are mapped only when
  `Integrations:EnableMockSimulators` is true, which is set in Development and tests only.

## Consequences
- A real gateway or billing provider is a new implementation of the same abstraction plus its
  webhook endpoint; settlement and the UI do not change.
- The mock webhook secret and simulator flag must not be enabled in production configuration.
