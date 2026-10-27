import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import clsx from 'clsx'
import { Pencil, Plus, Trash2 } from 'lucide-react'
import { useState } from 'react'
import { api, newId } from '../../app/api'
import { useOutletId, useSession } from '../../app/session'
import {
  Badge, Button, Card, Checkbox, EmptyState, ErrorBanner, Input, Modal, PageHeader, Select, Spinner, Table, Td, Textarea, useToast,
} from '../../components/ui'
import { dateTime, money, qty } from '../../lib/format'
import { STOCK_UNITS, type InventoryItem, type PoStatus, type PurchaseOrder, type PurchaseOrderSummary, type Supplier } from '../../types'

const unitShort = (u: string | null | undefined) => STOCK_UNITS.find((x) => x.value === u)?.short ?? ''
const STATUS_TONE = { Draft: 'slate', Ordered: 'blue', Received: 'green', Cancelled: 'red' } as const

function useSuppliers() {
  return useQuery({ queryKey: ['suppliers'], queryFn: () => api<Supplier[]>('/api/suppliers') })
}

export function PurchasingPage() {
  const [tab, setTab] = useState<'orders' | 'suppliers'>('orders')
  return (
    <div>
      <PageHeader title="Purchasing" description="Purchase orders for this outlet. Receiving an order adds the stock and updates ingredient costs." />
      <div role="tablist" aria-label="Purchasing sections" className="mb-4 flex gap-1 border-b border-slate-200">
        {([['orders', 'Purchase orders'], ['suppliers', 'Suppliers']] as const).map(([id, label]) => (
          <button key={id} role="tab" aria-selected={tab === id} onClick={() => setTab(id)}
            className={clsx('-mb-px border-b-2 px-4 py-2 text-sm font-medium',
              tab === id ? 'border-brand-700 text-brand-800' : 'border-transparent text-slate-500 hover:text-slate-800')}>
            {label}
          </button>
        ))}
      </div>
      {tab === 'orders' ? <OrdersTab /> : <SuppliersTab />}
    </div>
  )
}

function OrdersTab() {
  const outletId = useOutletId()
  const [status, setStatus] = useState<PoStatus | ''>('')
  const [open, setOpen] = useState<string | 'new' | null>(null)
  const list = useQuery({
    queryKey: ['purchase-orders', outletId, status],
    queryFn: () => api<PurchaseOrderSummary[]>(`/api/outlets/${outletId}/purchase-orders${status ? `?status=${status}` : ''}`),
  })
  const suppliers = useSuppliers()
  const noSuppliers = suppliers.data && suppliers.data.filter((s) => s.isActive).length === 0

  return (
    <Card actions={<Button size="sm" onClick={() => setOpen('new')} disabled={!!noSuppliers} title={noSuppliers ? 'Add a supplier first' : undefined}>
      <Plus className="size-4" aria-hidden /> New purchase order</Button>} title="Purchase orders">
      <div className="border-b border-slate-100 p-4">
        <Select label="Status" value={status} onChange={(e) => setStatus(e.target.value as PoStatus | '')} className="w-48">
          <option value="">All</option>
          {(['Draft', 'Ordered', 'Received', 'Cancelled'] as const).map((s) => <option key={s} value={s}>{s}</option>)}
        </Select>
      </div>
      <ErrorBanner error={list.error} />
      {list.isLoading ? <Spinner /> : !list.data?.length ? (
        <EmptyState title="No purchase orders" description={noSuppliers ? 'Add a supplier in the Suppliers tab to start.' : undefined} />
      ) : (
        <Table head={['PO', 'Supplier', 'Status', 'Lines', 'Total', 'Created', '']}>
          {list.data.map((p) => (
            <tr key={p.id}>
              <Td className="font-medium text-slate-900">PO-{p.number}</Td>
              <Td>{p.supplierName}</Td>
              <Td><Badge tone={STATUS_TONE[p.status]}>{p.status}</Badge></Td>
              <Td>{p.lineCount}</Td>
              <Td>{money(p.total)}</Td>
              <Td>{dateTime(p.createdAt)}</Td>
              <Td className="text-right"><Button variant="ghost" size="sm" onClick={() => setOpen(p.id)}>Open</Button></Td>
            </tr>
          ))}
        </Table>
      )}
      {open === 'new' && <PoEditor onClose={() => setOpen(null)} onCreated={(id) => setOpen(id)} />}
      {open && open !== 'new' && <PoDetail id={open} onClose={() => setOpen(null)} />}
    </Card>
  )
}

type DraftLine = { key: string; inventoryItemId: string; quantity: string; unitCost: string }

function PoEditor({ po, onClose, onCreated }: { po?: PurchaseOrder; onClose: () => void; onCreated?: (id: string) => void }) {
  const outletId = useOutletId()
  const qc = useQueryClient()
  const toast = useToast()
  const suppliers = useSuppliers()
  const items = useQuery({ queryKey: ['inventory', 'items'], queryFn: () => api<InventoryItem[]>('/api/inventory/items') })
  const [supplierId, setSupplierId] = useState(po?.supplierId ?? '')
  const [notes, setNotes] = useState(po?.notes ?? '')
  const [lines, setLines] = useState<DraftLine[]>(po?.lines.map((l) => ({
    key: l.id, inventoryItemId: l.inventoryItemId, quantity: String(l.quantity), unitCost: String(l.unitCost),
  })) ?? [{ key: newId(), inventoryItemId: '', quantity: '', unitCost: '' }])
  const activeItems = (items.data ?? []).filter((i) => i.isActive)
  const total = lines.reduce((s, l) => s + (Number(l.quantity) || 0) * (Number(l.unitCost) || 0), 0)

  const save = useMutation({
    mutationFn: () => api<PurchaseOrder>(`/api/outlets/${outletId}/purchase-orders${po ? `/${po.id}` : ''}`, {
      method: po ? 'PUT' : 'POST',
      body: {
        supplierId, notes: notes || null,
        lines: lines.filter((l) => l.inventoryItemId).map((l) => ({ inventoryItemId: l.inventoryItemId, quantity: Number(l.quantity), unitCost: Number(l.unitCost) })),
      },
    }),
    onSuccess: (saved) => {
      void qc.invalidateQueries({ queryKey: ['purchase-orders', outletId] })
      qc.setQueryData(['purchase-order', outletId, saved.id], saved)
      toast(po ? 'Purchase order updated' : `PO-${saved.number} created`)
      if (po) onClose()
      else onCreated?.(saved.id)
    },
  })
  const update = (key: string, patch: Partial<DraftLine>) => setLines((ls) => ls.map((l) => (l.key === key ? { ...l, ...patch } : l)))
  const onPick = (key: string, id: string) => update(key, { inventoryItemId: id, unitCost: String(activeItems.find((i) => i.id === id)?.costPerUnit ?? '') })

  return (
    <Modal open onClose={onClose} title={po ? `Edit PO-${po.number}` : 'New purchase order'} size="xl"
      footer={<>
        <Button variant="secondary" onClick={onClose}>Cancel</Button>
        <Button type="submit" form="po-form" loading={save.isPending}>{po ? 'Save changes' : 'Create draft'}</Button>
      </>}>
      <form id="po-form" className="space-y-4" onSubmit={(e) => { e.preventDefault(); save.mutate() }}>
        <Select label="Supplier" required value={supplierId} onChange={(e) => setSupplierId(e.target.value)} className="max-w-sm">
          <option value="">Choose…</option>
          {suppliers.data?.filter((s) => s.isActive || s.id === supplierId).map((s) => <option key={s.id} value={s.id}>{s.name}</option>)}
        </Select>
        <fieldset className="space-y-2">
          <legend className="text-sm font-medium text-slate-700">Lines</legend>
          {lines.map((l) => {
            const unit = activeItems.find((i) => i.id === l.inventoryItemId)?.unit
            return (
              <div key={l.key} className="flex flex-wrap items-end gap-2">
                <Select label="Ingredient" required value={l.inventoryItemId} onChange={(e) => onPick(l.key, e.target.value)} className="min-w-48 flex-1">
                  <option value="">Choose…</option>
                  {activeItems.map((i) => <option key={i.id} value={i.id}>{i.name}</option>)}
                </Select>
                <Input label={`Quantity${unit ? ` (${unitShort(unit)})` : ''}`} type="number" min={0.001} step="0.001" required value={l.quantity}
                  onChange={(e) => update(l.key, { quantity: e.target.value })} className="w-32" />
                <Input label="Unit cost (₹)" type="number" min={0} step="0.01" required value={l.unitCost}
                  onChange={(e) => update(l.key, { unitCost: e.target.value })} className="w-32" />
                <p className="w-28 pb-2 text-right text-sm">{money((Number(l.quantity) || 0) * (Number(l.unitCost) || 0))}</p>
                <Button variant="ghost" aria-label="Remove line" disabled={lines.length === 1} onClick={() => setLines((ls) => ls.filter((x) => x.key !== l.key))}>
                  <Trash2 className="size-4" aria-hidden />
                </Button>
              </div>
            )
          })}
          <Button variant="secondary" size="sm" onClick={() => setLines((ls) => [...ls, { key: newId(), inventoryItemId: '', quantity: '', unitCost: '' }])}>
            <Plus className="size-4" aria-hidden /> Add line
          </Button>
        </fieldset>
        <p className="text-right font-semibold">Total {money(total)}</p>
        <Textarea label="Notes" value={notes} onChange={(e) => setNotes(e.target.value)} />
        <ErrorBanner error={save.error} />
      </form>
    </Modal>
  )
}

function PoDetail({ id, onClose }: { id: string; onClose: () => void }) {
  const outletId = useOutletId()
  const qc = useQueryClient()
  const toast = useToast()
  const url = `/api/outlets/${outletId}/purchase-orders/${id}`
  const po = useQuery({ queryKey: ['purchase-order', outletId, id], queryFn: () => api<PurchaseOrder>(url) })
  const [mode, setMode] = useState<'view' | 'edit' | 'receive'>('view')
  const [received, setReceived] = useState<Record<string, { qty: string; cost: string }>>({})

  const action = useMutation({
    mutationFn: (kind: 'order' | 'cancel' | 'receive') => api<PurchaseOrder>(`${url}/${kind}`, {
      method: 'POST',
      body: kind === 'receive'
        ? { lines: Object.entries(received).map(([lineId, r]) => ({ lineId, receivedQuantity: Number(r.qty), unitCost: r.cost === '' ? null : Number(r.cost) })) }
        : {},
    }),
    onSuccess: (p, kind) => {
      qc.setQueryData(['purchase-order', outletId, id], p)
      void qc.invalidateQueries({ queryKey: ['purchase-orders', outletId] })
      if (kind === 'receive') void qc.invalidateQueries({ queryKey: ['inventory'] })
      toast({ order: 'Marked as ordered', cancel: 'Purchase order cancelled', receive: 'Goods received into stock' }[kind])
      if (kind === 'receive') setMode('view')
    },
  })

  if (po.data && mode === 'edit') return <PoEditor po={po.data} onClose={() => setMode('view')} />
  const p = po.data
  const startReceive = () => {
    setReceived(Object.fromEntries((p?.lines ?? []).map((l) => [l.id, { qty: String(l.quantity), cost: String(l.unitCost) }])))
    setMode('receive')
  }
  const canAct = p && (p.status === 'Draft' || p.status === 'Ordered')

  return (
    <Modal open onClose={onClose} title={p ? `PO-${p.number} · ${p.supplierName}` : 'Purchase order'} size="xl"
      footer={p && <>
        {canAct && mode === 'view' && <Button variant="ghost" className="mr-auto text-rose-700" loading={action.isPending && action.variables === 'cancel'}
          disabled={action.isPending} onClick={() => action.mutate('cancel')}>Cancel PO</Button>}
        {p.status === 'Draft' && mode === 'view' && <Button variant="secondary" disabled={action.isPending} onClick={() => setMode('edit')}><Pencil className="size-4" aria-hidden /> Edit</Button>}
        {p.status === 'Draft' && mode === 'view' && <Button variant="secondary" loading={action.isPending && action.variables === 'order'} disabled={action.isPending} onClick={() => action.mutate('order')}>Mark as ordered</Button>}
        {canAct && mode === 'view' && <Button disabled={action.isPending} onClick={startReceive}>Receive goods</Button>}
        {mode === 'receive' && <>
          <Button variant="secondary" onClick={() => setMode('view')}>Back</Button>
          <Button variant="success" loading={action.isPending} onClick={() => action.mutate('receive')}>Confirm receipt</Button>
        </>}
      </>}>
      {po.isLoading ? <Spinner /> : p && (
        <div className="space-y-4">
          <div className="flex flex-wrap gap-6 text-sm">
            <div><span className="text-slate-500">Status</span> <Badge tone={STATUS_TONE[p.status]}>{p.status}</Badge></div>
            <div><span className="text-slate-500">Created</span> {dateTime(p.createdAt)}</div>
            {p.orderedAt && <div><span className="text-slate-500">Ordered</span> {dateTime(p.orderedAt)}</div>}
            {p.receivedAt && <div><span className="text-slate-500">Received</span> {dateTime(p.receivedAt)}</div>}
          </div>
          {mode === 'receive' && <p className="rounded-lg bg-sky-50 p-3 text-sm text-sky-800">Enter the quantities and costs actually delivered. Use 0 for items not delivered.</p>}
          <Table head={['Ingredient', 'Ordered', 'Unit cost', mode === 'receive' ? 'Received' : 'Received', 'Amount']}>
            {p.lines.map((l) => (
              <tr key={l.id}>
                <Td className="font-medium text-slate-900">{l.itemName}</Td>
                <Td>{qty(l.quantity)} {unitShort(l.unit)}</Td>
                <Td>{mode === 'receive' ? (
                  <input aria-label={`Unit cost for ${l.itemName}`} type="number" min={0} step="0.01" className="w-24 rounded-md border-slate-300 text-sm"
                    value={received[l.id]?.cost ?? ''} onChange={(e) => setReceived((r) => ({ ...r, [l.id]: { ...r[l.id], cost: e.target.value } }))} />
                ) : money(l.unitCost)}</Td>
                <Td>{mode === 'receive' ? (
                  <input aria-label={`Received quantity for ${l.itemName}`} type="number" min={0} step="0.001" className="w-24 rounded-md border-slate-300 text-sm"
                    value={received[l.id]?.qty ?? ''} onChange={(e) => setReceived((r) => ({ ...r, [l.id]: { ...r[l.id], qty: e.target.value } }))} />
                ) : l.receivedQuantity != null ? `${qty(l.receivedQuantity)} ${unitShort(l.unit)}` : '—'}</Td>
                <Td>{money(l.amount)}</Td>
              </tr>
            ))}
          </Table>
          <p className="text-right text-base font-semibold">Total {money(p.total)}</p>
          {p.notes && <p className="text-sm text-slate-600">Notes: {p.notes}</p>}
          <ErrorBanner error={action.error} />
        </div>
      )}
    </Modal>
  )
}

function SuppliersTab() {
  const suppliers = useSuppliers()
  const { canTenant } = useSession()
  const editable = canTenant('PurchaseManage')
  const [editing, setEditing] = useState<Supplier | 'new' | null>(null)
  return (
    <Card title="Suppliers" actions={editable && <Button size="sm" onClick={() => setEditing('new')}><Plus className="size-4" aria-hidden /> New supplier</Button>}>
      <ErrorBanner error={suppliers.error} />
      {suppliers.isLoading ? <Spinner /> : !suppliers.data?.length ? <EmptyState title="No suppliers yet" /> : (
        <Table head={['Name', 'Contact', 'Phone', 'GSTIN', 'Status', '']}>
          {suppliers.data.map((s) => (
            <tr key={s.id}>
              <Td className="font-medium text-slate-900">{s.name}</Td>
              <Td>{s.contactName ?? '—'}{s.email && <p className="text-xs text-slate-500">{s.email}</p>}</Td>
              <Td>{s.phone ?? '—'}</Td>
              <Td>{s.gstin ?? '—'}</Td>
              <Td><Badge tone={s.isActive ? 'green' : 'slate'}>{s.isActive ? 'Active' : 'Archived'}</Badge></Td>
              <Td className="text-right">{editable && <Button variant="ghost" size="sm" aria-label={`Edit ${s.name}`} onClick={() => setEditing(s)}><Pencil className="size-4" aria-hidden /></Button>}</Td>
            </tr>
          ))}
        </Table>
      )}
      {editing && <SupplierEditor supplier={editing === 'new' ? null : editing} onClose={() => setEditing(null)} />}
    </Card>
  )
}

function SupplierEditor({ supplier, onClose }: { supplier: Supplier | null; onClose: () => void }) {
  const qc = useQueryClient()
  const toast = useToast()
  const [form, setForm] = useState({
    name: supplier?.name ?? '', contactName: supplier?.contactName ?? '', phone: supplier?.phone ?? '', email: supplier?.email ?? '',
    gstin: supplier?.gstin ?? '', address: supplier?.address ?? '', isActive: supplier?.isActive ?? true,
  })
  const save = useMutation({
    mutationFn: () => api(supplier ? `/api/suppliers/${supplier.id}` : '/api/suppliers', {
      method: supplier ? 'PUT' : 'POST',
      body: {
        ...form, contactName: form.contactName || null, phone: form.phone || null, email: form.email || null,
        gstin: form.gstin || null, address: form.address || null,
      },
    }),
    onSuccess: () => { void qc.invalidateQueries({ queryKey: ['suppliers'] }); toast('Supplier saved'); onClose() },
  })
  const field = (k: keyof Omit<typeof form, 'isActive'>) => ({ value: form[k], onChange: (e: { target: { value: string } }) => setForm({ ...form, [k]: e.target.value }) })
  return (
    <Modal open onClose={onClose} title={supplier ? `Edit ${supplier.name}` : 'New supplier'}
      footer={<>
        <Button variant="secondary" onClick={onClose}>Cancel</Button>
        <Button type="submit" form="supplier-form" loading={save.isPending}>Save</Button>
      </>}>
      <form id="supplier-form" className="grid gap-3 sm:grid-cols-2" onSubmit={(e) => { e.preventDefault(); save.mutate() }}>
        <Input label="Name" required {...field('name')} className="sm:col-span-2" />
        <Input label="Contact person" {...field('contactName')} />
        <Input label="Phone" {...field('phone')} />
        <Input label="Email" type="email" {...field('email')} />
        <Input label="GSTIN" {...field('gstin')} />
        <Input label="Address" {...field('address')} className="sm:col-span-2" />
        <Checkbox label="Active" checked={form.isActive} onChange={(v) => setForm({ ...form, isActive: v })} />
        <div className="sm:col-span-2"><ErrorBanner error={save.error} /></div>
      </form>
    </Modal>
  )
}
