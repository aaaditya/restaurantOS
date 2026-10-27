import clsx from 'clsx'
import { Bike, Check, Plus, ShoppingBag, X } from 'lucide-react'
import { useMemo, useState } from 'react'
import { Link, useNavigate } from 'react-router'
import { api, errorMessage } from '../../app/api'
import { HubConnectionState, useOutletRealtime } from '../../app/realtime'
import { useOutletId, useSession } from '../../app/session'
import { Badge, Button, Card, EmptyState, ErrorBanner, Input, Modal, PageHeader, Spinner, useToast } from '../../components/ui'
import { money, minutesSince, orderTypeLabel } from '../../lib/format'
import type { Order, OrderSummary } from '../../types'
import { posKeys, useOrderAction, useRunningOrders, useTables } from './usePos'

export function LiveIndicator({ state }: { state: HubConnectionState }) {
  const live = state === HubConnectionState.Connected
  return (
    <span className="inline-flex items-center gap-1.5 text-xs text-slate-500" aria-live="polite">
      <span className={clsx('size-2 rounded-full', live ? 'bg-emerald-500' : 'bg-amber-500')} />
      {live ? 'Live' : 'Reconnecting…'}
    </span>
  )
}

export function PosHome() {
  const outletId = useOutletId()
  const navigate = useNavigate()
  const toast = useToast()
  const tables = useTables(outletId)
  const running = useRunningOrders(outletId)
  const state = useOutletRealtime(outletId, {
    orderChanged: [posKeys.tables(outletId), ['orders', outletId]],
    qrOrderPlaced: [posKeys.tables(outletId), ['orders', outletId]],
    externalOrderPlaced: [['orders', outletId]],
  }, (event) => {
    if (event === 'qrOrderPlaced') toast('New QR order received', 'info')
    if (event === 'externalOrderPlaced') toast('New delivery partner order received', 'info')
  })

  const areas = useMemo(() => {
    const map = new Map<string, NonNullable<typeof tables.data>>()
    for (const t of tables.data?.filter((x) => x.isActive) ?? []) map.set(t.area, [...(map.get(t.area) ?? []), t])
    return [...map.entries()]
  }, [tables.data])

  const pending = running.data?.filter((o) => o.status === 'AwaitingAcceptance') ?? []
  const nonTable = running.data?.filter((o) => !o.tableId && o.status !== 'AwaitingAcceptance') ?? []

  return (
    <div>
      <PageHeader title="Point of sale" description="Pick a table or start a takeaway / delivery order."
        actions={<>
          <LiveIndicator state={state} />
          <Button variant="secondary" onClick={() => navigate('/pos/new?type=Takeaway')}><ShoppingBag className="size-4" /> Takeaway</Button>
          <Button variant="secondary" onClick={() => navigate('/pos/new?type=Delivery')}><Bike className="size-4" /> Delivery</Button>
        </>} />

      {pending.length > 0 && <PendingOrders orders={pending} />}

      <div className="grid gap-6 xl:grid-cols-[1fr_22rem]">
        <div className="space-y-6">
          {tables.isLoading && <Spinner />}
          <ErrorBanner error={tables.error} />
          {tables.data && areas.length === 0 && (
            <Card><EmptyState title="No tables yet" description="Add tables to take dine-in orders."
              action={<Link to="/tables"><Button size="sm"><Plus className="size-4" /> Add tables</Button></Link>} /></Card>
          )}
          {areas.map(([area, list]) => (
            <section key={area} aria-label={`${area} tables`}>
              <h2 className="mb-2 text-sm font-semibold uppercase tracking-wide text-slate-500">{area}</h2>
              <div className="grid grid-cols-2 gap-3 sm:grid-cols-3 md:grid-cols-4 2xl:grid-cols-6">
                {list.map((t) => (
                  <button key={t.id}
                    onClick={() => navigate(t.currentOrder ? `/pos/orders/${t.currentOrder.id}` : `/pos/new?type=DineIn&table=${t.id}`)}
                    className={clsx('flex h-28 flex-col justify-between rounded-xl border-2 p-3 text-left transition hover:shadow-md',
                      t.status === 'free' && 'border-slate-200 bg-white',
                      t.status === 'occupied' && 'border-sky-300 bg-sky-50',
                      t.status === 'billed' && 'border-amber-300 bg-amber-50')}
                    aria-label={`Table ${t.name}, ${t.status}`}>
                    <div className="flex items-center justify-between">
                      <span className="text-lg font-semibold">{t.name}</span>
                      <span className="text-xs text-slate-500">{t.capacity} seats</span>
                    </div>
                    {t.currentOrder ? (
                      <div className="text-sm">
                        <div className="font-semibold">{money(t.currentOrder.grandTotal)}</div>
                        <div className="text-xs text-slate-500">#{t.currentOrder.orderNumber} · {minutesSince(t.currentOrder.createdAt)} min
                          {t.status === 'billed' && ' · billed'}</div>
                      </div>
                    ) : <span className="text-xs font-medium text-emerald-700">Available</span>}
                  </button>
                ))}
              </div>
            </section>
          ))}
        </div>

        <Card title="Takeaway & delivery" className="h-fit">
          {running.isLoading && <Spinner />}
          {nonTable.length === 0 && !running.isLoading && <EmptyState title="No running orders" />}
          <ul className="divide-y divide-slate-100">
            {nonTable.map((o) => (
              <li key={o.id}>
                <Link to={`/pos/orders/${o.id}`} className="flex items-center justify-between gap-2 px-4 py-3 hover:bg-slate-50">
                  <div>
                    <div className="text-sm font-medium">#{o.orderNumber} · {orderTypeLabel[o.type]}</div>
                    <div className="text-xs text-slate-500">{o.customerName ?? 'Walk-in'} · {minutesSince(o.createdAt)} min</div>
                  </div>
                  <div className="text-right">
                    <div className="text-sm font-semibold">{money(o.grandTotal)}</div>
                    {o.status === 'Billed' && <Badge tone="amber">Billed</Badge>}
                    {o.pendingItems > 0 && <Badge tone="blue">{o.pendingItems} unsent</Badge>}
                  </div>
                </Link>
              </li>
            ))}
          </ul>
        </Card>
      </div>
    </div>
  )
}

function pendingSourceLabel(o: OrderSummary) {
  if (o.source === 'Qr') return `QR · Table ${o.tableName}`
  if (o.source === 'Aggregator' && o.externalRef) {
    const [provider, ...id] = o.externalRef.split(':')
    return `${provider} · ${orderTypeLabel[o.type]} · ${id.join(':')}`
  }
  return `Online ${orderTypeLabel[o.type]}`
}

function PendingOrders({ orders }: { orders: OrderSummary[] }) {
  const outletId = useOutletId()
  const { can } = useSession()
  const toast = useToast()
  const [rejecting, setRejecting] = useState<OrderSummary | null>(null)
  const [reason, setReason] = useState('')
  const accept = useOrderAction(outletId, (o: OrderSummary) =>
    api<Order>(`/api/outlets/${outletId}/orders/${o.id}/accept`, { method: 'POST', body: { version: o.version } }))
  const reject = useOrderAction(outletId, (o: OrderSummary) =>
    api<Order>(`/api/outlets/${outletId}/orders/${o.id}/cancel`, { method: 'POST', body: { reason, version: o.version } }))

  return (
    <Card title={`Awaiting acceptance (${orders.length})`} className="mb-6 ring-2 ring-amber-300">
      <ul className="divide-y divide-slate-100">
        {orders.map((o) => (
          <li key={o.id} className="flex flex-wrap items-center justify-between gap-3 px-4 py-3">
            <div>
              <div className="font-medium">#{o.orderNumber} · {pendingSourceLabel(o)}</div>
              <div className="text-sm text-slate-500">{o.itemCount} {o.itemCount === 1 ? 'item' : 'items'} · {money(o.grandTotal)} · {o.customerName ?? 'Guest'}</div>
            </div>
            <div className="flex gap-2">
              <Link to={`/pos/orders/${o.id}`}><Button variant="ghost" size="sm">View</Button></Link>
              {can('OrderCancel') && <Button variant="secondary" size="sm" onClick={() => { setReason(''); setRejecting(o) }}><X className="size-4" /> Reject</Button>}
              <Button variant="success" size="sm" loading={accept.isPending && accept.variables?.id === o.id}
                // mutateAsync rather than per-call callbacks: the card unmounts once the list refreshes, before mutate's callbacks would run.
                onClick={() => void accept.mutateAsync(o).then(
                  (result) => toast(result.id === o.id
                    ? `Order #${o.orderNumber} accepted and sent to kitchen`
                    : `Items from #${o.orderNumber} added to running order #${result.orderNumber} and sent to kitchen`),
                  (e: unknown) => toast(errorMessage(e), 'error'),
                )}>
                <Check className="size-4" /> Accept
              </Button>
            </div>
          </li>
        ))}
      </ul>
      <Modal open={!!rejecting} onClose={() => setRejecting(null)} title="Reject order" size="sm"
        footer={<>
          <Button variant="secondary" onClick={() => setRejecting(null)}>Back</Button>
          <Button variant="danger" disabled={!reason.trim()} loading={reject.isPending}
            onClick={() => rejecting && void reject.mutateAsync(rejecting).then(() => setRejecting(null), (e: unknown) => toast(errorMessage(e), 'error'))}>
            Reject
          </Button>
        </>}>
        <Input label="Reason" value={reason} onChange={(e) => setReason(e.target.value)} placeholder="e.g. Kitchen closed" autoFocus />
      </Modal>
    </Card>
  )
}
