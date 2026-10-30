import clsx from 'clsx'
import { ArrowLeft, Ban, ChefHat, Minus, Percent, Plus, Printer, Receipt, Search, Trash2, User, Wallet } from 'lucide-react'
import { useMemo, useState } from 'react'
import { Link, useNavigate, useParams, useSearchParams } from 'react-router'
import { api, errorMessage, newId } from '../../app/api'
import { useOutletRealtime } from '../../app/realtime'
import { useOutletId, useSession } from '../../app/session'
import { Badge, Button, ErrorBanner, FoodTypeMark, Input, Modal, Select, Spinner, useToast } from '../../components/ui'
import { money, orderTypeLabel, qty } from '../../lib/format'
import type { Order, OrderLine, OrderType, PosMenuItem, SettlementResult } from '../../types'
import { ItemConfigurator, needsConfiguration, simpleLine, type DraftLine } from './ItemConfigurator'
import { LiveIndicator } from './PosHome'
import { SettleDialog } from './SettleDialog'
import { orderUrl, posKeys, useOrder, useOrderAction, usePosMenu, useTables } from './usePos'

const toSelection = (d: DraftLine) => ({
  menuItemId: d.menuItemId, variantId: d.variantId, quantity: d.quantity, modifierOptionIds: d.modifierOptionIds, notes: d.notes || null,
})

export function OrderScreen() {
  const outletId = useOutletId()
  const { orderId } = useParams()
  const [params] = useSearchParams()
  const navigate = useNavigate()
  const toast = useToast()
  const { can } = useSession()

  const menu = usePosMenu(outletId)
  const tables = useTables(outletId)
  const orderQuery = useOrder(outletId, orderId)
  const order = orderQuery.data
  const live = useOutletRealtime(outletId, { orderChanged: orderId ? [posKeys.order(outletId, orderId)] : [] })

  const [clientOrderId] = useState(newId)
  const [draft, setDraft] = useState<DraftLine[]>([])
  const [category, setCategory] = useState<string | 'all'>('all')
  const [search, setSearch] = useState('')
  const [configuring, setConfiguring] = useState<PosMenuItem | null>(null)
  const [dialog, setDialog] = useState<null | 'settle' | 'discount' | 'customer' | 'cancel' | { voidLine: OrderLine }>(null)
  const [settled, setSettled] = useState<SettlementResult | null>(null)
  const [newMeta, setNewMeta] = useState({ customerName: '', customerPhone: '', deliveryAddress: '', guests: '2' })

  const type = (order?.type ?? params.get('type') ?? 'Takeaway') as OrderType
  const tableId = order?.tableId ?? params.get('table')
  const tableName = order?.tableName ?? tables.data?.find((t) => t.id === tableId)?.name

  const items = useMemo(() => {
    const q = search.trim().toLowerCase()
    return (menu.data?.items ?? []).filter((i) => (category === 'all' || i.categoryId === category) && (!q || i.name.toLowerCase().includes(q)))
  }, [menu.data, category, search])

  const editable = !order || order.status === 'Open' || order.status === 'AwaitingAcceptance'
  const pendingServer = order?.lines.filter((l) => l.status === 'Pending') ?? []

  const create = useOrderAction(outletId, (sendToKitchen: boolean) => api<Order>(orderUrl(outletId), {
    method: 'POST',
    body: {
      clientOrderId, type, tableId: type === 'DineIn' ? tableId : null, guests: Number(newMeta.guests) || 1,
      customerName: newMeta.customerName || null, customerPhone: newMeta.customerPhone || null,
      deliveryAddress: type === 'Delivery' ? newMeta.deliveryAddress || null : null,
      lines: draft.map(toSelection), sendToKitchen,
    },
  }))
  const addLines = useOrderAction(outletId, (sendToKitchen: boolean) => api<Order>(orderUrl(outletId, order!.id, '/lines'), {
    method: 'POST', body: { lines: draft.map(toSelection), version: order!.version, sendToKitchen },
  }))
  const sendKot = useOrderAction(outletId, () => api<Order>(orderUrl(outletId, order!.id, '/kot'), { method: 'POST', body: { version: order!.version } }))
  const changeQty = useOrderAction(outletId, (v: { line: OrderLine; quantity: number }) =>
    api<Order>(orderUrl(outletId, order!.id, `/lines/${v.line.id}`), { method: 'PATCH', body: { quantity: v.quantity, version: order!.version } }))
  const bill = useOrderAction(outletId, () => api<Order>(orderUrl(outletId, order!.id, '/bill'), { method: 'POST', body: { version: order!.version } }))
  const reopen = useOrderAction(outletId, () => api<Order>(orderUrl(outletId, order!.id, '/reopen'), { method: 'POST', body: { version: order!.version } }))

  const busy = create.isPending || addLines.isPending || sendKot.isPending

  const addItem = (item: PosMenuItem) => {
    if (!item.isAvailable || !editable) return
    if (needsConfiguration(item)) {
      setConfiguring(item)
      return
    }
    setDraft((d) => {
      const same = d.find((x) => x.menuItemId === item.id && !x.variantId && x.modifierOptionIds.length === 0 && !x.notes)
      return same ? d.map((x) => (x === same ? { ...x, quantity: x.quantity + 1 } : x)) : [...d, simpleLine(item)]
    })
  }

  const submit = (sendToKitchen: boolean) => {
    const onError = (e: unknown) => toast(errorMessage(e), 'error')
    if (!order) {
      create.mutate(sendToKitchen, {
        onSuccess: (o) => {
          setDraft([])
          toast(sendToKitchen ? `KOT sent for order #${o.orderNumber}` : `Order #${o.orderNumber} saved`)
          navigate(`/pos/orders/${o.id}`, { replace: true })
        },
        onError,
      })
    } else if (draft.length > 0) {
      addLines.mutate(sendToKitchen, {
        onSuccess: () => { setDraft([]); toast(sendToKitchen ? 'KOT sent' : 'Items saved') },
        onError,
      })
    } else if (sendToKitchen && pendingServer.length > 0) {
      sendKot.mutate(undefined, { onSuccess: () => toast('KOT sent'), onError })
    }
  }

  if (orderId && orderQuery.isLoading) return <Spinner label="Loading order…" />
  if (orderId && orderQuery.error) return <ErrorBanner error={orderQuery.error} />

  const draftSubtotal = draft.reduce((s, d) => s + d.unitPrice * d.quantity, 0)
  const canSend = editable && (draft.length > 0 || pendingServer.length > 0) && order?.status !== 'AwaitingAcceptance'

  return (
    <div className="-m-4 flex min-h-0 flex-1 flex-col sm:-m-6 lg:-m-8">
      <div className="flex flex-wrap items-center gap-3 border-b border-slate-200 bg-white px-4 py-3">
        <Link to="/pos" className="rounded-md p-1.5 hover:bg-slate-100" aria-label="Back to POS"><ArrowLeft className="size-5" /></Link>
        <div>
          <h1 className="text-lg font-semibold">
            {order ? `Order #${order.orderNumber}` : 'New order'} · {orderTypeLabel[type]}{tableName ? ` · Table ${tableName}` : ''}
          </h1>
          {order && <p className="text-xs text-slate-500">{order.customerName ?? 'Walk-in'}{order.customerPhone ? ` · ${order.customerPhone}` : ''}</p>}
        </div>
        {order && <StatusBadge status={order.status} />}
        <div className="ml-auto"><LiveIndicator state={live} /></div>
      </div>

      <div className="grid min-h-0 flex-1 grid-cols-1 md:grid-cols-[11rem_1fr_24rem]">
        <nav aria-label="Menu categories" className="hidden overflow-y-auto border-r border-slate-200 bg-white p-2 md:block">
          {[{ id: 'all', name: 'All items' }, ...(menu.data?.categories ?? [])].map((c) => (
            <button key={c.id} onClick={() => setCategory(c.id)}
              className={clsx('mb-1 w-full rounded-lg px-3 py-2 text-left text-sm font-medium',
                category === c.id ? 'bg-brand-700 text-white' : 'text-slate-700 hover:bg-slate-100')}>
              {c.name}
            </button>
          ))}
        </nav>

        <section aria-label="Menu items" className="flex min-h-0 flex-col bg-slate-50">
          <div className="border-b border-slate-200 bg-white p-3">
            <label className="relative block">
              <span className="sr-only">Search items</span>
              <Search className="pointer-events-none absolute left-3 top-2.5 size-4 text-slate-400" aria-hidden />
              <input value={search} onChange={(e) => setSearch(e.target.value)} placeholder="Search items…"
                className="w-full rounded-lg border-0 py-2 pl-9 pr-3 text-sm ring-1 ring-field focus:ring-2 focus:ring-brand-600" />
            </label>
          </div>
          <div className="grid flex-1 auto-rows-min grid-cols-2 gap-2 overflow-y-auto p-3 lg:grid-cols-3 2xl:grid-cols-4">
            {menu.isLoading && <Spinner />}
            {items.map((item) => (
              <button key={item.id} onClick={() => addItem(item)} disabled={!item.isAvailable || !editable}
                className={clsx('flex min-h-20 flex-col justify-between rounded-xl bg-white p-3 text-left shadow-sm ring-1 ring-slate-200 transition',
                  item.isAvailable && editable ? 'hover:ring-brand-500' : 'opacity-50')}>
                <span className="flex items-start gap-2 text-sm font-medium">
                  <FoodTypeMark type={item.foodType} /> {item.name}
                </span>
                <span className="mt-2 flex items-center justify-between text-xs text-slate-500">
                  <span>{item.variants.length > 0 ? `from ${money(Math.min(...item.variants.map((v) => v.price)))}` : money(item.price)}</span>
                  {!item.isAvailable && <Badge tone="red">Out of stock</Badge>}
                </span>
              </button>
            ))}
          </div>
        </section>

        <aside aria-label="Order" className="flex min-h-0 flex-col border-l border-slate-200 bg-white">
          {!order && (
            <div className="grid grid-cols-2 gap-2 border-b border-slate-100 p-3">
              <Input label="Customer phone" inputMode="tel" value={newMeta.customerPhone} onChange={(e) => setNewMeta((m) => ({ ...m, customerPhone: e.target.value }))} />
              <Input label="Name" value={newMeta.customerName} onChange={(e) => setNewMeta((m) => ({ ...m, customerName: e.target.value }))} />
              {type === 'DineIn' && <Input label="Guests" type="number" min={1} value={newMeta.guests} onChange={(e) => setNewMeta((m) => ({ ...m, guests: e.target.value }))} />}
              {type === 'Delivery' && <Input label="Delivery address" className="col-span-2" value={newMeta.deliveryAddress}
                onChange={(e) => setNewMeta((m) => ({ ...m, deliveryAddress: e.target.value }))} />}
            </div>
          )}

          <ul className="flex-1 divide-y divide-slate-100 overflow-y-auto" aria-label="Order items">
            {order?.lines.map((l) => (
              <li key={l.id} className={clsx('px-4 py-2.5', l.status === 'Voided' && 'opacity-50')}>
                <div className="flex items-start justify-between gap-2">
                  <div className="min-w-0">
                    <p className={clsx('text-sm font-medium', l.status === 'Voided' && 'line-through')}>
                      {l.itemName}{l.variantName ? ` (${l.variantName})` : ''}
                    </p>
                    {l.modifiers.length > 0 && <p className="text-xs text-slate-500">{l.modifiers.map((m) => m.name).join(', ')}</p>}
                    {l.notes && <p className="text-xs italic text-slate-500">“{l.notes}”</p>}
                    {l.status === 'Voided' && <p className="text-xs text-rose-600">Void: {l.voidReason}</p>}
                  </div>
                  <span className="text-sm font-medium">{money(l.unitPrice * l.quantity)}</span>
                </div>
                <div className="mt-1 flex items-center gap-2">
                  {l.status === 'Pending' && editable ? (
                    <QtyStepper value={l.quantity} disabled={changeQty.isPending}
                      onChange={(q) => changeQty.mutate({ line: l, quantity: q }, { onError: (e) => toast(errorMessage(e), 'error') })} />
                  ) : <span className="text-xs text-slate-500">Qty {qty(l.quantity)}</span>}
                  {l.status === 'Sent' && <Badge tone="teal">KOT sent</Badge>}
                  {l.status === 'Pending' && <Badge tone="blue">Not sent</Badge>}
                  {l.status === 'Sent' && editable && can('OrderVoidItem') && (
                    <button className="ml-auto text-xs font-medium text-rose-600 hover:underline" onClick={() => setDialog({ voidLine: l })}>Void</button>
                  )}
                </div>
              </li>
            ))}
            {draft.map((d) => (
              <li key={d.key} className="bg-brand-50/50 px-4 py-2.5">
                <div className="flex items-start justify-between gap-2">
                  <div className="min-w-0">
                    <p className="text-sm font-medium">{d.name}{d.variantName ? ` (${d.variantName})` : ''}</p>
                    {d.modifierNames.length > 0 && <p className="text-xs text-slate-500">{d.modifierNames.join(', ')}</p>}
                    {d.notes && <p className="text-xs italic text-slate-500">“{d.notes}”</p>}
                  </div>
                  <span className="text-sm font-medium">{money(d.unitPrice * d.quantity)}</span>
                </div>
                <div className="mt-1 flex items-center gap-2">
                  <QtyStepper value={d.quantity} onChange={(q) => setDraft((all) => q <= 0 ? all.filter((x) => x.key !== d.key)
                    : all.map((x) => (x.key === d.key ? { ...x, quantity: q } : x)))} />
                  <Badge tone="amber">New</Badge>
                  <button className="ml-auto text-slate-400 hover:text-rose-600" aria-label={`Remove ${d.name}`}
                    onClick={() => setDraft((all) => all.filter((x) => x.key !== d.key))}><Trash2 className="size-4" /></button>
                </div>
              </li>
            ))}
            {!order?.lines.length && draft.length === 0 && (
              <li className="px-4 py-12 text-center text-sm text-slate-500">Tap menu items to add them to the order.</li>
            )}
          </ul>

          <div className="border-t border-slate-200 p-4">
            <dl className="space-y-1 text-sm">
              {order && <>
                <Row label="Subtotal" value={order.subtotal} />
                {order.discountTotal > 0 && <Row label={`Discount${order.discountReason ? ` (${order.discountReason})` : ''}`} value={-order.discountTotal} />}
                {order.taxBreakdown.map((t) => <Row key={`${t.code}${t.ratePercent}`} label={`${t.code} ${t.ratePercent}%`} value={t.amount} muted />)}
                {order.roundOff !== 0 && <Row label="Round off" value={order.roundOff} muted />}
                <div className="flex justify-between pt-1 text-base font-semibold"><dt>Total</dt><dd data-testid="order-total">{money(order.grandTotal)}</dd></div>
              </>}
              {draft.length > 0 && <Row label="New items (before tax)" value={draftSubtotal} />}
            </dl>

            <div className="mt-3 grid grid-cols-2 gap-2">
              {canSend && <>
                <Button variant="secondary" loading={busy} onClick={() => submit(false)} disabled={draft.length === 0}>Save</Button>
                <Button loading={busy} onClick={() => submit(true)}><ChefHat className="size-4" /> Send KOT</Button>
              </>}
              {order?.status === 'AwaitingAcceptance' && (
                <p className="col-span-2 rounded-lg bg-amber-50 p-2 text-center text-sm text-amber-800">Accept this order from the POS home screen.</p>
              )}
              {order && editable && order.status === 'Open' && <>
                {can('OrderDiscount') && <Button variant="secondary" onClick={() => setDialog('discount')}><Percent className="size-4" /> Discount</Button>}
                <Button variant="secondary" onClick={() => setDialog('customer')}><User className="size-4" /> Customer</Button>
                <Button variant="secondary" disabled={pendingServer.length > 0 || draft.length > 0} loading={bill.isPending}
                  onClick={() => bill.mutate(undefined, {
                    onSuccess: () => window.open(`/orders/${order.id}/invoice?print=1`, '_blank'),
                    onError: (e) => toast(errorMessage(e), 'error'),
                  })}><Printer className="size-4" /> Print bill</Button>
              </>}
              {order?.status === 'Billed' && can('OrderVoidItem') && (
                <Button variant="secondary" loading={reopen.isPending} onClick={() => reopen.mutate(undefined, { onError: (e) => toast(errorMessage(e), 'error') })}>
                  Reopen
                </Button>
              )}
              {order && (order.status === 'Open' || order.status === 'Billed') && can('OrderSettle') && (
                <Button variant="success" className="col-span-2" size="lg" disabled={pendingServer.length > 0 || draft.length > 0 || order.lines.every((l) => l.status === 'Voided')}
                  onClick={() => setDialog('settle')}><Wallet className="size-4" /> Settle {money(order.grandTotal)}</Button>
              )}
              {order && order.status !== 'Settled' && order.status !== 'Cancelled' && can('OrderCancel') && (
                <Button variant="ghost" className="col-span-2 text-rose-700" onClick={() => setDialog('cancel')}><Ban className="size-4" /> Cancel order</Button>
              )}
              {order?.status === 'Settled' && (
                <Link to={`/orders/${order.id}/invoice`} className="col-span-2"><Button variant="secondary" className="w-full"><Receipt className="size-4" /> View invoice {order.invoiceNumber}</Button></Link>
              )}
            </div>
          </div>
        </aside>
      </div>

      {configuring && <ItemConfigurator item={configuring} onClose={() => setConfiguring(null)}
        onAdd={(line) => { setDraft((d) => [...d, line]); setConfiguring(null) }} />}
      {order && dialog === 'settle' && <SettleDialog order={order} onClose={() => setDialog(null)} onSettled={(r) => { setDialog(null); setSettled(r) }} />}
      {order && dialog === 'discount' && <DiscountDialog order={order} onClose={() => setDialog(null)} />}
      {order && dialog === 'customer' && <CustomerDialog order={order} onClose={() => setDialog(null)} />}
      {order && dialog === 'cancel' && <ReasonDialog order={order} title="Cancel order" action="Cancel order" suffix="/cancel" onClose={() => setDialog(null)} />}
      {order && typeof dialog === 'object' && dialog !== null && 'voidLine' in dialog && (
        <ReasonDialog order={order} title={`Void ${dialog.voidLine.itemName}`} action="Void item" suffix={`/lines/${dialog.voidLine.id}/void`}
          onClose={() => setDialog(null)} />
      )}
      {settled && (
        <Modal open onClose={() => navigate('/pos')} title="Order settled" size="sm"
          footer={<>
            <Button variant="secondary" onClick={() => window.open(`/orders/${settled.orderId}/invoice?print=1`, '_blank')}><Printer className="size-4" /> Print invoice</Button>
            <Button onClick={() => navigate('/pos')}>New order</Button>
          </>}>
          <div className="space-y-2 text-center">
            <p className="text-sm text-slate-500">Invoice</p>
            <p className="text-xl font-semibold" data-testid="invoice-number">{settled.invoiceNumber}</p>
            {settled.changeDue > 0 && <p className="text-lg">Return change: <strong>{money(settled.changeDue)}</strong></p>}
          </div>
        </Modal>
      )}
    </div>
  )
}

function Row({ label, value, muted }: { label: string; value: number; muted?: boolean }) {
  return (
    <div className={clsx('flex justify-between', muted && 'text-slate-500')}>
      <dt>{label}</dt><dd>{money(value)}</dd>
    </div>
  )
}

function QtyStepper({ value, onChange, disabled }: { value: number; onChange: (v: number) => void; disabled?: boolean }) {
  return (
    <div className="inline-flex items-center rounded-lg ring-1 ring-slate-300">
      <button className="px-2 py-1 disabled:opacity-40" aria-label="Decrease" disabled={disabled} onClick={() => onChange(value - 1)}><Minus className="size-3.5" /></button>
      <span className="min-w-6 text-center text-sm font-medium">{qty(value)}</span>
      <button className="px-2 py-1 disabled:opacity-40" aria-label="Increase" disabled={disabled} onClick={() => onChange(value + 1)}><Plus className="size-3.5" /></button>
    </div>
  )
}

export function StatusBadge({ status }: { status: Order['status'] }) {
  const tone = { AwaitingAcceptance: 'amber', Open: 'blue', Billed: 'amber', Settled: 'green', Cancelled: 'red' } as const
  const label = { AwaitingAcceptance: 'Awaiting acceptance', Open: 'Open', Billed: 'Billed', Settled: 'Settled', Cancelled: 'Cancelled' }
  return <Badge tone={tone[status]}>{label[status]}</Badge>
}

function DiscountDialog({ order, onClose }: { order: Order; onClose: () => void }) {
  const toast = useToast()
  const [type, setType] = useState<'Percent' | 'Flat'>(order.discountType ?? 'Percent')
  const [value, setValue] = useState(order.discountValue ? String(order.discountValue) : '')
  const [reason, setReason] = useState(order.discountReason ?? '')
  const apply = useOrderAction(order.outletId, (remove: boolean) => api<Order>(orderUrl(order.outletId, order.id, '/discount'), {
    method: 'PUT', body: remove ? { type: null, value: 0, version: order.version } : { type, value: Number(value), reason, version: order.version },
  }))
  const run = (remove: boolean) => apply.mutate(remove, { onSuccess: () => { toast(remove ? 'Discount removed' : 'Discount applied'); onClose() } })
  return (
    <Modal open onClose={onClose} title="Order discount" size="sm"
      footer={<>
        {order.discountType && <Button variant="ghost" onClick={() => run(true)}>Remove</Button>}
        <Button disabled={!Number(value) || !reason.trim()} loading={apply.isPending} onClick={() => run(false)}>Apply</Button>
      </>}>
      <div className="space-y-3">
        <ErrorBanner error={apply.error} />
        <Select label="Type" value={type} onChange={(e) => setType(e.target.value as 'Percent' | 'Flat')}>
          <option value="Percent">Percentage (%)</option>
          <option value="Flat">Flat amount (₹)</option>
        </Select>
        <Input label={type === 'Percent' ? 'Percent' : 'Amount'} type="number" min={0} step="0.01" value={value} onChange={(e) => setValue(e.target.value)} />
        <Input label="Reason" value={reason} onChange={(e) => setReason(e.target.value)} placeholder="Required for audit" />
      </div>
    </Modal>
  )
}

function CustomerDialog({ order, onClose }: { order: Order; onClose: () => void }) {
  const [phone, setPhone] = useState(order.customerPhone ?? '')
  const [name, setName] = useState(order.customerName ?? '')
  const save = useOrderAction(order.outletId, () => api<Order>(orderUrl(order.outletId, order.id, '/customer'), {
    method: 'PUT', body: { phone: phone || null, name: name || null, version: order.version },
  }))
  return (
    <Modal open onClose={onClose} title="Customer" size="sm"
      footer={<Button loading={save.isPending} onClick={() => save.mutate(undefined, { onSuccess: onClose })}>Save</Button>}>
      <div className="space-y-3">
        <ErrorBanner error={save.error} />
        <Input label="Phone" inputMode="tel" value={phone} onChange={(e) => setPhone(e.target.value)} hint="Links the order to the customer for loyalty" />
        <Input label="Name" value={name} onChange={(e) => setName(e.target.value)} />
      </div>
    </Modal>
  )
}

function ReasonDialog({ order, title, action, suffix, onClose }: { order: Order; title: string; action: string; suffix: string; onClose: () => void }) {
  const [reason, setReason] = useState('')
  const toast = useToast()
  const run = useOrderAction(order.outletId, () => api<Order>(orderUrl(order.outletId, order.id, suffix), {
    method: 'POST', body: { reason, version: order.version },
  }))
  return (
    <Modal open onClose={onClose} title={title} size="sm"
      footer={<>
        <Button variant="secondary" onClick={onClose}>Back</Button>
        <Button variant="danger" disabled={!reason.trim()} loading={run.isPending}
          onClick={() => run.mutate(undefined, { onSuccess: () => { toast(`${action} done`); onClose() } })}>{action}</Button>
      </>}>
      <div className="space-y-3">
        <ErrorBanner error={run.error} />
        <Input label="Reason" value={reason} onChange={(e) => setReason(e.target.value)} placeholder="Recorded in the audit log" autoFocus />
      </div>
    </Modal>
  )
}
