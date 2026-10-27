import { useMutation, useQuery } from '@tanstack/react-query'
import clsx from 'clsx'
import { CheckCircle2, Clock, Minus, Plus, Search, ShoppingBag, XCircle } from 'lucide-react'
import { useMemo, useState } from 'react'
import { useParams } from 'react-router'
import { api, ApiError, newId } from '../../app/api'
import { Button, EmptyState, ErrorBanner, FoodTypeMark, Input, Modal, Spinner } from '../../components/ui'
import { money, time } from '../../lib/format'
import type { GuestOrder, PosMenuItem, PublicTable } from '../../types'
import { ItemConfigurator, needsConfiguration, simpleLine, type DraftLine } from '../pos/ItemConfigurator'

const storageKey = (token: string) => `ros.qr.${token}`

function loadPlaced(token: string): string[] {
  try {
    const raw = localStorage.getItem(storageKey(token))
    return raw ? (JSON.parse(raw) as string[]) : []
  } catch {
    return []
  }
}

/** Public, unauthenticated page a guest reaches by scanning the table's QR code. */
export function GuestOrderPage() {
  const { token = '' } = useParams()
  const base = `/api/public/tables/${encodeURIComponent(token)}`
  const table = useQuery({
    queryKey: ['public-table', token],
    queryFn: () => api<PublicTable>(base, { auth: false }),
    retry: false,
    refetchInterval: 60_000,
  })
  const [placed, setPlaced] = useState<string[]>(() => loadPlaced(token))
  const [cart, setCart] = useState<DraftLine[]>([])
  const [configuring, setConfiguring] = useState<PosMenuItem | null>(null)
  const [checkout, setCheckout] = useState(false)
  const [search, setSearch] = useState('')
  const [category, setCategory] = useState<string | null>(null)

  const menu = table.data?.menu
  const items = useMemo(() => {
    const term = search.trim().toLowerCase()
    return (menu?.items ?? []).filter((i) => i.isAvailable && (term ? i.name.toLowerCase().includes(term) : !category || i.categoryId === category))
  }, [menu, search, category])

  const count = cart.reduce((s, l) => s + l.quantity, 0)
  const subtotal = cart.reduce((s, l) => s + l.unitPrice * l.quantity, 0)

  const add = (item: PosMenuItem) => {
    if (needsConfiguration(item)) return setConfiguring(item)
    setCart((c) => {
      const existing = c.find((l) => l.menuItemId === item.id && !l.variantId && l.modifierOptionIds.length === 0 && !l.notes)
      return existing ? c.map((l) => (l === existing ? { ...l, quantity: l.quantity + 1 } : l)) : [...c, simpleLine(item)]
    })
  }
  const changeQty = (key: string, delta: number) =>
    setCart((c) => c.flatMap((l) => (l.key !== key ? [l] : l.quantity + delta <= 0 ? [] : [{ ...l, quantity: l.quantity + delta }])))

  const onPlaced = (clientOrderId: string) => {
    const next = [clientOrderId, ...placed.filter((id) => id !== clientOrderId)].slice(0, 20)
    localStorage.setItem(storageKey(token), JSON.stringify(next))
    setPlaced(next)
    setCart([])
    setCheckout(false)
  }

  if (table.isLoading) return <div className="grid min-h-screen place-items-center"><Spinner label="Loading menu…" /></div>
  if (table.error) {
    const missing = table.error instanceof ApiError && table.error.status === 404
    return (
      <div className="grid min-h-screen place-items-center p-6 text-center">
        <div className="max-w-sm">
          <XCircle className="mx-auto mb-3 size-10 text-rose-500" aria-hidden />
          <h1 className="text-lg font-semibold">{missing ? 'This QR code is no longer valid' : 'We could not load the menu'}</h1>
          <p className="mt-1 text-sm text-slate-600">{missing ? 'Please ask a staff member for help.' : 'Check your connection and try again.'}</p>
        </div>
      </div>
    )
  }
  const t = table.data!

  return (
    <div className="mx-auto min-h-screen max-w-2xl bg-slate-50 pb-28">
      <header className="sticky top-0 z-10 border-b border-slate-200 bg-white/95 px-4 py-3 backdrop-blur">
        <p className="text-xs font-medium uppercase tracking-wide text-brand-700">{t.outlet}</p>
        <h1 className="text-lg font-semibold">{t.restaurant}</h1>
        <p className="text-sm text-slate-600">Table {t.table}</p>
      </header>

      <div className="space-y-4 p-4">
        {placed.length > 0 && <GuestOrders base={base} ids={placed} />}

        {!t.orderingOpen || !menu ? (
          <EmptyState title="Ordering from the table is not available right now" description="Please call a staff member to place your order." />
        ) : (
          <>
            <div className="relative">
              <Search className="pointer-events-none absolute left-3 top-1/2 size-4 -translate-y-1/2 text-slate-400" aria-hidden />
              <input type="search" placeholder="Search dishes" aria-label="Search dishes" value={search} onChange={(e) => setSearch(e.target.value)}
                className="w-full rounded-xl border-slate-300 py-2.5 pl-9 text-sm shadow-sm focus:border-brand-600 focus:ring-brand-600" />
            </div>
            {!search && (
              <nav aria-label="Categories" className="-mx-4 flex gap-2 overflow-x-auto px-4 pb-1">
                {[{ id: null, name: 'All' }, ...menu.categories].map((c) => (
                  <button key={c.id ?? 'all'} onClick={() => setCategory(c.id)} aria-pressed={category === c.id}
                    className={clsx('whitespace-nowrap rounded-full px-3 py-1.5 text-sm font-medium ring-1',
                      category === c.id ? 'bg-brand-700 text-white ring-brand-700' : 'bg-white text-slate-700 ring-slate-200')}>
                    {c.name}
                  </button>
                ))}
              </nav>
            )}
            {items.length === 0 ? <EmptyState title="No dishes match" /> : (
              <ul className="divide-y divide-slate-200 overflow-hidden rounded-xl bg-white ring-1 ring-slate-200">
                {items.map((item) => (
                  <li key={item.id} className="flex items-start gap-3 p-4">
                    <FoodTypeMark type={item.foodType} />
                    <div className="min-w-0 flex-1">
                      <p className="font-medium">{item.name}</p>
                      {item.description && <p className="mt-0.5 line-clamp-2 text-sm text-slate-500">{item.description}</p>}
                      <p className="mt-1 text-sm font-semibold">
                        {item.variants.length > 0 ? `from ${money(Math.min(...item.variants.map((v) => v.price)))}` : money(item.price)}
                      </p>
                    </div>
                    <Button size="sm" variant="secondary" onClick={() => add(item)} aria-label={`Add ${item.name}`}><Plus className="size-4" /> Add</Button>
                  </li>
                ))}
              </ul>
            )}
            <p className="text-center text-xs text-slate-500">
              {t.pricesIncludeTax ? 'Prices include taxes.' : 'Prices exclude taxes, which are added to your bill.'}
            </p>
          </>
        )}
      </div>

      {count > 0 && (
        <div className="fixed inset-x-0 bottom-0 z-20 mx-auto max-w-2xl p-4">
          <button onClick={() => setCheckout(true)}
            className="flex w-full items-center justify-between rounded-xl bg-brand-700 px-5 py-3.5 text-white shadow-lg hover:bg-brand-800">
            <span className="flex items-center gap-2 font-medium"><ShoppingBag className="size-5" aria-hidden /> {count} item{count === 1 ? '' : 's'}</span>
            <span className="font-semibold">Review order · {money(subtotal)}</span>
          </button>
        </div>
      )}

      {configuring && (
        <ItemConfigurator item={configuring} onClose={() => setConfiguring(null)}
          onAdd={(line) => { setCart((c) => [...c, line]); setConfiguring(null) }} />
      )}
      {checkout && (
        <CheckoutDialog base={base} cart={cart} subtotal={subtotal} pricesIncludeTax={t.pricesIncludeTax} onChangeQty={changeQty}
          onClose={() => setCheckout(false)} onPlaced={onPlaced} />
      )}
    </div>
  )
}

function CheckoutDialog({ base, cart, subtotal, pricesIncludeTax, onChangeQty, onClose, onPlaced }: {
  base: string; cart: DraftLine[]; subtotal: number; pricesIncludeTax: boolean
  onChangeQty: (key: string, delta: number) => void; onClose: () => void; onPlaced: (clientOrderId: string) => void
}) {
  // Reused on retries so a flaky network cannot create the order twice.
  const [clientOrderId] = useState(newId)
  const [name, setName] = useState('')
  const [phone, setPhone] = useState('')
  const place = useMutation({
    mutationFn: () => api<GuestOrder>(`${base}/orders`, {
      method: 'POST',
      auth: false,
      body: {
        clientOrderId,
        customerName: name.trim() || null,
        customerPhone: phone.trim() || null,
        lines: cart.map((l) => ({
          menuItemId: l.menuItemId, variantId: l.variantId, quantity: l.quantity, modifierOptionIds: l.modifierOptionIds, notes: l.notes || null,
        })),
      },
    }),
    onSuccess: (o) => onPlaced(o.clientOrderId),
  })

  return (
    <Modal open onClose={onClose} title="Your order" size="md"
      footer={<Button className="w-full" disabled={cart.length === 0} loading={place.isPending} onClick={() => place.mutate()}>
        Place order · {money(subtotal)}
      </Button>}>
      <div className="space-y-4">
        <ul className="divide-y divide-slate-100">
          {cart.map((l) => (
            <li key={l.key} className="flex items-center gap-3 py-2">
              <div className="min-w-0 flex-1">
                <p className="text-sm font-medium">{l.name}{l.variantName ? ` (${l.variantName})` : ''}</p>
                {l.modifierNames.length > 0 && <p className="text-xs text-slate-500">{l.modifierNames.join(', ')}</p>}
                {l.notes && <p className="text-xs italic text-slate-500">{l.notes}</p>}
              </div>
              <div className="flex items-center gap-1">
                <Button size="sm" variant="ghost" aria-label={`Fewer ${l.name}`} onClick={() => onChangeQty(l.key, -1)}><Minus className="size-4" /></Button>
                <span className="w-6 text-center text-sm font-semibold">{l.quantity}</span>
                <Button size="sm" variant="ghost" aria-label={`More ${l.name}`} onClick={() => onChangeQty(l.key, 1)}><Plus className="size-4" /></Button>
              </div>
              <span className="w-20 text-right text-sm">{money(l.unitPrice * l.quantity)}</span>
            </li>
          ))}
        </ul>
        <p className="text-xs text-slate-500">{pricesIncludeTax ? 'Total includes taxes.' : 'Taxes are added when the restaurant confirms your order.'}</p>
        <div className="grid gap-3 sm:grid-cols-2">
          <Input label="Your name (optional)" value={name} maxLength={120} onChange={(e) => setName(e.target.value)} autoComplete="name" />
          <Input label="Phone (optional)" inputMode="tel" value={phone} maxLength={20} onChange={(e) => setPhone(e.target.value)}
            hint="Earn loyalty points on this visit" autoComplete="tel" />
        </div>
        <ErrorBanner error={place.error} />
      </div>
    </Modal>
  )
}

const stateView = {
  pending: { label: 'Waiting for the restaurant to confirm', icon: Clock, tone: 'text-amber-700 bg-amber-50 ring-amber-200' },
  accepted: { label: 'Confirmed — being prepared', icon: CheckCircle2, tone: 'text-emerald-700 bg-emerald-50 ring-emerald-200' },
  completed: { label: 'Completed — thank you!', icon: CheckCircle2, tone: 'text-slate-700 bg-slate-100 ring-slate-200' },
  rejected: { label: 'Could not be accepted — please ask a staff member', icon: XCircle, tone: 'text-rose-700 bg-rose-50 ring-rose-200' },
} as const

function GuestOrders({ base, ids }: { base: string; ids: string[] }) {
  return (
    <section aria-label="Your orders" className="space-y-2">
      {ids.slice(0, 5).map((id) => <GuestOrderStatus key={id} base={base} id={id} />)}
    </section>
  )
}

function GuestOrderStatus({ base, id }: { base: string; id: string }) {
  const order = useQuery({
    queryKey: ['public-order', base, id],
    queryFn: () => api<GuestOrder>(`${base}/orders/${id}`, { auth: false }),
    retry: false,
    refetchInterval: (q) => (q.state.data?.state === 'pending' ? 5_000 : 30_000),
  })
  if (!order.data) return null
  const o = order.data
  const view = stateView[o.state]
  return (
    <div className={clsx('rounded-xl p-4 ring-1', view.tone)} data-testid="guest-order">
      <div className="flex items-center justify-between gap-2">
        <p className="flex items-center gap-2 text-sm font-semibold"><view.icon className="size-4" aria-hidden /> {view.label}</p>
        <span className="text-xs">#{o.orderNumber} · {time(o.createdAt)}</span>
      </div>
      <p className="mt-1 text-sm">
        {o.lines.map((l) => `${l.quantity} × ${l.itemName}${l.variantName ? ` (${l.variantName})` : ''}`).join(', ')}
      </p>
      {o.grandTotal != null && <p className="mt-1 text-sm font-medium">Total {money(o.grandTotal)}</p>}
    </div>
  )
}
