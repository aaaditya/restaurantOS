# ADR-0003: Money, tax and rounding rules

Status: Accepted (2026-10-27)

## Decision
1. Money is `decimal`, stored as `numeric(18,2)`. Rates/quantities are `numeric(18,4)`. No floats.
2. Rounding mode is `MidpointRounding.AwayFromZero`, applied at 2 dp.
3. Line gross = round(unit price incl. modifiers × qty).
4. Order discount (percent or flat) is allocated to lines **before tax**:
   - percent: each line `round(gross × pct / 100)`;
   - flat: proportional to gross, rounding remainder assigned to the largest line;
   - discount never exceeds the subtotal.
5. Tax-exclusive outlets: each tax component = round(taxable × rate / 100), line total = taxable + Σ tax.
6. Tax-inclusive outlets: net = gross − discount; taxable = round(net / (1 + Σrate/100));
   total tax = net − taxable split across components, remainder on the last component; line total = net.
7. Grand total = Σ line totals, then optional outlet rounding (`None` or `NearestRupee`) with the
   difference recorded as `round_off`.
8. Order lines snapshot name, variant, modifiers, unit price and tax components at the time they are
   added. Totals are recomputed from snapshots only.
9. Invoice numbers are sequential per outlet per Indian financial year (April–March):
   `{prefix}/{FY}/{seq:000000}`.

## Consequences
- Bills are reproducible from stored snapshots; unit tests pin exact paise values.
- Changing rounding policy only affects new orders.
