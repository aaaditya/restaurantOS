import { useMutation, useQueryClient } from '@tanstack/react-query'
import { Plus, Trash2 } from 'lucide-react'
import { useState } from 'react'
import { api, newId } from '../../app/api'
import { Button, ErrorBanner, Modal } from '../../components/ui'
import { money, paymentLabel } from '../../lib/format'
import type { Order, PaymentMethod, SettlementResult } from '../../types'

type Row = { key: string; method: PaymentMethod; amount: string; tendered: string; reference: string }
const METHODS: PaymentMethod[] = ['Cash', 'Upi', 'Card', 'Wallet']

const round2 = (n: number) => Math.round(n * 100) / 100

export function SettleDialog({ order, extraMethods = [], onClose, onSettled }: {
  order: Order
  extraMethods?: { method: PaymentMethod; label: string; max?: number; reference?: string }[]
  onClose: () => void
  onSettled: (r: SettlementResult) => void
}) {
  const qc = useQueryClient()
  // One key per dialog: network retries and double-clicks reuse it, so the order can only be charged once.
  const [idempotencyKey] = useState(newId)
  const [rows, setRows] = useState<Row[]>([{ key: newId(), method: 'Cash', amount: order.grandTotal.toFixed(2), tendered: '', reference: '' }])

  const paid = round2(rows.reduce((s, r) => s + (Number(r.amount) || 0), 0))
  const remaining = round2(order.grandTotal - paid)
  const change = rows.reduce((s, r) => (r.method === 'Cash' && Number(r.tendered) > Number(r.amount) ? s + Number(r.tendered) - Number(r.amount) : s), 0)
  const methods = [...METHODS.map((m) => ({ method: m, label: paymentLabel[m] })), ...extraMethods]

  const update = (key: string, patch: Partial<Row>) => setRows((rs) => rs.map((r) => (r.key === key ? { ...r, ...patch } : r)))

  const settle = useMutation({
    mutationFn: () => api<SettlementResult>(`/api/outlets/${order.outletId}/orders/${order.id}/settle`, {
      method: 'POST',
      body: {
        idempotencyKey,
        version: order.version,
        payments: rows.filter((r) => Number(r.amount) > 0).map((r) => ({
          method: r.method,
          amount: round2(Number(r.amount)),
          tendered: r.method === 'Cash' && r.tendered ? round2(Number(r.tendered)) : null,
          reference: r.reference || extraMethods.find((m) => m.method === r.method)?.reference || null,
        })),
      },
    }),
    onSuccess: (r) => {
      void qc.invalidateQueries({ queryKey: ['order', order.outletId] })
      void qc.invalidateQueries({ queryKey: ['orders', order.outletId] })
      void qc.invalidateQueries({ queryKey: ['tables', order.outletId] })
      onSettled(r)
    },
  })

  const quick = (method: PaymentMethod) => setRows([{ key: newId(), method, amount: order.grandTotal.toFixed(2), tendered: '', reference: '' }])

  return (
    <Modal open onClose={onClose} title={`Settle order #${order.orderNumber}`} size="md"
      footer={<>
        <Button variant="secondary" onClick={onClose}>Cancel</Button>
        <Button variant="success" disabled={remaining !== 0} loading={settle.isPending} onClick={() => settle.mutate()}>
          Settle {money(order.grandTotal)}
        </Button>
      </>}>
      <div className="space-y-4">
        <ErrorBanner error={settle.error} />
        <div className="rounded-xl bg-slate-50 p-4 text-center">
          <div className="text-sm text-slate-500">Amount due</div>
          <div className="text-3xl font-semibold" data-testid="amount-due">{money(order.grandTotal)}</div>
        </div>
        <div className="flex flex-wrap gap-2" role="group" aria-label="Quick pay">
          {METHODS.map((m) => <Button key={m} variant="secondary" size="sm" onClick={() => quick(m)}>Full {paymentLabel[m]}</Button>)}
        </div>
        <ul className="space-y-3">
          {rows.map((r, i) => (
            <li key={r.key} className="grid grid-cols-[1fr_1fr_auto] items-end gap-2 rounded-lg border border-slate-200 p-3">
              <label className="text-sm">
                <span className="mb-1 block font-medium text-slate-700">Method</span>
                <select className="w-full rounded-lg border-slate-300 text-sm" value={r.method} aria-label={`Payment ${i + 1} method`}
                  onChange={(e) => update(r.key, { method: e.target.value as PaymentMethod })}>
                  {methods.map((m) => <option key={m.method} value={m.method}>{m.label}</option>)}
                </select>
              </label>
              <label className="text-sm">
                <span className="mb-1 block font-medium text-slate-700">Amount</span>
                <input type="number" inputMode="decimal" step="0.01" min="0" className="w-full rounded-lg border-slate-300 text-sm"
                  aria-label={`Payment ${i + 1} amount`} value={r.amount} onChange={(e) => update(r.key, { amount: e.target.value })} />
              </label>
              <Button variant="ghost" size="sm" aria-label="Remove payment" disabled={rows.length === 1}
                onClick={() => setRows((rs) => rs.filter((x) => x.key !== r.key))}><Trash2 className="size-4" /></Button>
              {r.method === 'Cash' && (
                <label className="col-span-2 text-sm">
                  <span className="mb-1 block font-medium text-slate-700">Cash received (optional)</span>
                  <input type="number" inputMode="decimal" step="0.01" min="0" className="w-full rounded-lg border-slate-300 text-sm"
                    aria-label="Cash received" value={r.tendered} onChange={(e) => update(r.key, { tendered: e.target.value })} />
                </label>
              )}
              {(r.method === 'Card' || r.method === 'Upi' || r.method === 'Wallet') && (
                <label className="col-span-2 text-sm">
                  <span className="mb-1 block font-medium text-slate-700">Reference (optional)</span>
                  <input className="w-full rounded-lg border-slate-300 text-sm" value={r.reference} maxLength={120}
                    onChange={(e) => update(r.key, { reference: e.target.value })} />
                </label>
              )}
            </li>
          ))}
        </ul>
        <Button variant="ghost" size="sm" onClick={() => setRows((rs) => [...rs, {
          key: newId(), method: 'Upi', amount: remaining > 0 ? remaining.toFixed(2) : '', tendered: '', reference: '',
        }])}><Plus className="size-4" /> Split payment</Button>
        <dl className="grid grid-cols-2 gap-1 text-sm">
          <dt className="text-slate-500">Paid</dt><dd className="text-right font-medium">{money(paid)}</dd>
          <dt className="text-slate-500">Remaining</dt>
          <dd className={remaining === 0 ? 'text-right font-medium text-emerald-700' : 'text-right font-medium text-rose-700'}>{money(remaining)}</dd>
          {change > 0 && <><dt className="text-slate-500">Change to return</dt><dd className="text-right text-lg font-semibold">{money(change)}</dd></>}
        </dl>
      </div>
    </Modal>
  )
}
