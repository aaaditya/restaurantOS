# ADR-0004: Transactions, idempotency and the stock ledger

Status: Accepted (2026-10-27)

## Decision
- **Settlement** runs in one serializable-enough transaction (`READ COMMITTED` + row lock on the
  order via `SELECT … FOR UPDATE`) covering payments, invoice number, order status, stock
  consumption, loyalty and customer stats.
- **Idempotency**: `orders.client_order_id` unique per outlet; `settlements.idempotency_key` unique
  per tenant. A retried request with the same key returns the stored result; the same key with a
  different order returns 409.
- **Stock ledger** is append-only. Corrections are new rows (adjustments). Unique index
  `(tenant_id, source_type, source_id, inventory_item_id)` prevents posting the same source twice.
  `stock_balances` is a denormalised projection updated in the same transaction.
- **Optimistic concurrency** on orders via PostgreSQL `xmin`; clients send `version` and get 409 on
  conflict.
- **Counters** (`outlet_counters`) produce order/KOT/invoice numbers atomically.

## Consequences
- A crashed client retrying settlement can never double-charge or double-deduct stock.
- Reports can rebuild stock from the ledger at any time.
