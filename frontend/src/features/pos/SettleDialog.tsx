import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { ExternalLink, Link2, Plus, Trash2 } from 'lucide-react'
import { useState } from 'react'
import { api, newId } from '../../app/api'
import { Badge, Button, ErrorBanner, Modal } from '../../components/ui'
import { money, paymentLabel } from '../../lib/format'
import type { CustomerDetail, Order, Outlet, PaymentIntent, PaymentMethod, SettlementResult } from '../../types'

type Row = { key: string; method: PaymentMethod; amount: string; tendered: string; reference: string }
const METHODS: PaymentMethod[] = ['Cash', 'Upi', 'Card', 'Wallet']
const LINK_METHOD = { method: 'Online' as PaymentMethod, label: 'Online payment link' }

const round2 = (n: number) => Math.round(n * 100) / 100

export function SettleDialog({ order, onClose, onSettled }: {
  order: Order
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
  const outlet = useQuery({ queryKey: ['outlet', order.outletId], queryFn: () => api<Outlet>(`/api/outlets/${order.outletId}`), staleTime: 60_000 })
  const customer = useQuery({
    queryKey: ['customer', order.customerId],
    queryFn: () => api<CustomerDetail>(`/api/customers/${order.customerId}`),
    enabled: !!order.customerId,
  })
  const points = customer.data?.customer.loyaltyPoints ?? 0
  const pointValue = outlet.data?.loyaltyPointValue ?? 0
  // Redemption needs ceil(amount / pointValue) points, so the cap is the value of the whole points held.
  const loyaltyMax = pointValue > 0 ? Math.min(Math.floor(points * pointValue * 100) / 100, order.grandTotal) : 0
  const loyaltyUsed = round2(rows.filter((r) => r.method === 'Loyalty').reduce((s, r) => s + (Number(r.amount) || 0), 0))
  const loyaltyOver = loyaltyUsed > loyaltyMax
  const intents = useQuery({
    queryKey: ['payment-intents', order.id],
    queryFn: () => api<PaymentIntent[]>(`/api/outlets/${order.outletId}/orders/${order.id}/payment-intents`),
    enabled: rows.some((r) => r.method === 'Online'),
    // Confirmation arrives by provider webhook, so poll while a link is awaiting payment.
    refetchInterval: (q) => (q.state.data?.some((i) => i.status === 'Pending') ? 2000 : false),
  })
  const intentFor = (r: Row) => intents.data?.find((i) => i.id === r.reference)
  const onlineUnconfirmed = rows.some((r) => r.method === 'Online' && Number(r.amount) > 0 && intentFor(r)?.status !== 'Succeeded')
  const methods = [
    ...METHODS.map((m) => ({ method: m, label: paymentLabel[m] })),
    LINK_METHOD,
    ...(loyaltyMax > 0 ? [{ method: 'Loyalty' as PaymentMethod, label: `Loyalty points (up to ${money(loyaltyMax)})` }] : []),
  ]

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
          reference: r.reference || null,
        })),
      },
    }),
    onSuccess: (r) => {
      void qc.invalidateQueries({ queryKey: ['order', order.outletId] })
      void qc.invalidateQueries({ queryKey: ['orders', order.outletId] })
      void qc.invalidateQueries({ queryKey: ['tables', order.outletId] })
      void qc.invalidateQueries({ queryKey: ['customer'] })
      void qc.invalidateQueries({ queryKey: ['customers'] })
      onSettled(r)
    },
  })

  const quick = (method: PaymentMethod) => setRows([{ key: newId(), method, amount: order.grandTotal.toFixed(2), tendered: '', reference: '' }])

  return (
    <Modal open onClose={onClose} title={`Settle order #${order.orderNumber}`} size="md"
      footer={<>
        <Button variant="secondary" onClick={onClose}>Cancel</Button>
        <Button variant="success" disabled={remaining !== 0 || loyaltyOver || onlineUnconfirmed} loading={settle.isPending} onClick={() => settle.mutate()}>
          Settle {money(order.grandTotal)}
        </Button>
      </>}>
      <div className="space-y-4">
        <ErrorBanner error={settle.error} />
        <div className="rounded-xl bg-slate-50 p-4 text-center">
          <div className="text-sm text-slate-500">Amount due</div>
          <div className="text-3xl font-semibold" data-testid="amount-due">{money(order.grandTotal)}</div>
        </div>
        {order.customerId && customer.data && (
          <p className="rounded-lg bg-brand-50 px-3 py-2 text-sm text-brand-800" data-testid="loyalty-balance">
            {customer.data.customer.name ?? customer.data.customer.phone}: {customer.data.customer.loyaltyPoints} points
            {loyaltyMax > 0 ? ` (worth ${money(loyaltyMax)})` : ''}
          </p>
        )}
        <div className="flex flex-wrap gap-2" role="group" aria-label="Quick pay">
          {METHODS.map((m) => <Button key={m} variant="secondary" size="sm" onClick={() => quick(m)}>Full {paymentLabel[m]}</Button>)}
        </div>
        <ul className="space-y-3">
          {rows.map((r, i) => (
            <li key={r.key} className="grid grid-cols-[1fr_1fr_auto] items-end gap-2 rounded-lg border border-slate-200 p-3">
              <label className="text-sm">
                <span className="mb-1 block font-medium text-slate-700">Method</span>
                <select className="w-full rounded-lg border-slate-300 text-sm" value={r.method} aria-label={`Payment ${i + 1} method`}
                  disabled={r.method === 'Online' && !!r.reference}
                  onChange={(e) => update(r.key, { method: e.target.value as PaymentMethod, reference: '' })}>
                  {methods.map((m) => <option key={m.method} value={m.method}>{m.label}</option>)}
                </select>
              </label>
              <label className="text-sm">
                <span className="mb-1 block font-medium text-slate-700">Amount</span>
                <input type="number" inputMode="decimal" step="0.01" min="0" className="w-full rounded-lg border-slate-300 text-sm"
                  aria-label={`Payment ${i + 1} amount`} value={r.amount} disabled={r.method === 'Online' && !!r.reference} onChange={(e) => update(r.key, { amount: e.target.value })} />
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
              {r.method === 'Online' && (
                <OnlinePayment order={order} amount={round2(Number(r.amount) || 0)} intent={intentFor(r)} linked={!!r.reference}
                  onCreated={(intent) => { update(r.key, { reference: intent.id }); void intents.refetch() }}
                  onDiscard={() => update(r.key, { reference: '' })} />
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
          {loyaltyOver && <dd className="col-span-2 text-rose-700">Loyalty payment exceeds the {money(loyaltyMax)} available.</dd>}
          {change > 0 && <><dt className="text-slate-500">Change to return</dt><dd className="text-right text-lg font-semibold">{money(change)}</dd></>}
        </dl>
      </div>
    </Modal>
  )
}

function OnlinePayment({ order, amount, intent, linked, onCreated, onDiscard }: {
  order: Order
  amount: number
  intent: PaymentIntent | undefined
  linked: boolean
  onCreated: (intent: PaymentIntent) => void
  onDiscard: () => void
}) {
  const create = useMutation({
    mutationFn: () => api<PaymentIntent>(`/api/outlets/${order.outletId}/orders/${order.id}/payment-intents`, { method: 'POST', body: { amount } }),
    onSuccess: onCreated,
  })
  if (!linked) {
    return (
      <div className="col-span-3 space-y-2">
        <ErrorBanner error={create.error} />
        <Button size="sm" variant="secondary" disabled={amount <= 0} loading={create.isPending} onClick={() => create.mutate()}>
          <Link2 className="size-4" /> Create payment link for {money(amount)}
        </Button>
      </div>
    )
  }
  if (!intent) return <p className="col-span-3 text-sm text-slate-500">Loading payment status…</p>
  const href = intent.paymentLink ?? ''
  const url = href.startsWith('/') ? `${window.location.origin}${href}` : href
  return (
    <div className="col-span-3 space-y-2 rounded-lg bg-slate-50 p-3 text-sm" data-testid="online-payment">
      <div className="flex items-center justify-between gap-2">
        <span className="font-medium">Payment link · {money(intent.amount)}</span>
        <Badge tone={intent.status === 'Succeeded' ? 'green' : intent.status === 'Failed' ? 'red' : 'amber'}>
          {intent.status === 'Succeeded' ? 'Paid' : intent.status === 'Failed' ? 'Failed' : 'Awaiting payment'}
        </Badge>
      </div>
      <input readOnly className="w-full rounded-lg border-slate-300 bg-white text-xs" value={url} aria-label="Payment link"
        onFocus={(e) => e.currentTarget.select()} />
      <div className="flex flex-wrap gap-2">
        {intent.status === 'Pending' && navigator.clipboard && (
          <Button size="sm" variant="ghost" onClick={() => void navigator.clipboard.writeText(url)}>Copy link</Button>
        )}
        {intent.provider === 'mock' && intent.status === 'Pending' && (
          <a href={href} target="_blank" rel="noreferrer" className="inline-flex items-center gap-1 rounded-lg px-2 py-1 text-sm font-medium text-brand-700 hover:bg-brand-50">
            <ExternalLink className="size-4" aria-hidden /> Open test checkout
          </a>
        )}
        {intent.status === 'Failed' && <Button size="sm" variant="secondary" onClick={onDiscard}>Create a new link</Button>}
      </div>
      {intent.provider === 'mock' && <p className="text-xs text-slate-500">Test mode: payments use the built-in mock gateway and no money moves.</p>}
    </div>
  )
}
