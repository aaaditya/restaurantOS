import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import clsx from 'clsx'
import { Pencil, Plus, Trash2 } from 'lucide-react'
import { useMemo, useState } from 'react'
import { api, newId } from '../../app/api'
import { useOutletRealtime } from '../../app/realtime'
import { useOutletId, useSession } from '../../app/session'
import {
  Badge, Button, Card, Checkbox, EmptyState, ErrorBanner, Input, Modal, PageHeader, Select, Spinner, Table, Td, useToast,
} from '../../components/ui'
import { dateTime, money, qty } from '../../lib/format'
import { STOCK_UNITS, type InventoryItem, type LedgerEntry, type Recipe, type StockReport, type StockUnit } from '../../types'
import { usePosMenu } from '../pos/usePos'

const unitShort = (u: StockUnit | null | undefined) => STOCK_UNITS.find((x) => x.value === u)?.short ?? ''
const keys = {
  items: ['inventory', 'items'] as const,
  recipes: ['inventory', 'recipes'] as const,
  stock: (outletId: string) => ['inventory', outletId, 'stock'] as const,
  ledger: (outletId: string, itemId: string) => ['inventory', outletId, 'ledger', itemId] as const,
}

type Tab = 'stock' | 'items' | 'recipes' | 'ledger'

export function InventoryPage() {
  const outletId = useOutletId()
  const { can, canTenant } = useSession()
  const [tab, setTab] = useState<Tab>('stock')
  useOutletRealtime(outletId, { stockChanged: [['inventory', outletId]] })
  const tabs: { id: Tab; label: string }[] = [
    { id: 'stock', label: 'Stock' }, { id: 'items', label: 'Ingredients' }, { id: 'recipes', label: 'Recipes' }, { id: 'ledger', label: 'Movements' },
  ]
  return (
    <div>
      <PageHeader title="Inventory" description="Ingredient stock for this outlet. Sales deduct recipe quantities when an order is settled." />
      <div role="tablist" aria-label="Inventory sections" className="mb-4 flex gap-1 border-b border-slate-200">
        {tabs.map((t) => (
          <button key={t.id} role="tab" aria-selected={tab === t.id} onClick={() => setTab(t.id)}
            className={clsx('-mb-px border-b-2 px-4 py-2 text-sm font-medium',
              tab === t.id ? 'border-brand-700 text-brand-800' : 'border-transparent text-slate-500 hover:text-slate-800')}>
            {t.label}
          </button>
        ))}
      </div>
      {tab === 'stock' && <StockTab canAdjust={can('InventoryManage')} />}
      {tab === 'items' && <ItemsTab editable={canTenant('InventoryManage')} />}
      {tab === 'recipes' && <RecipesTab editable={canTenant('InventoryManage')} />}
      {tab === 'ledger' && <LedgerTab />}
    </div>
  )
}

function useItems() {
  return useQuery({ queryKey: keys.items, queryFn: () => api<InventoryItem[]>('/api/inventory/items') })
}

function StockTab({ canAdjust }: { canAdjust: boolean }) {
  const outletId = useOutletId()
  const [lowOnly, setLowOnly] = useState(false)
  const [adjusting, setAdjusting] = useState<'Opening' | 'Wastage' | 'Correction' | null>(null)
  const stock = useQuery({ queryKey: keys.stock(outletId), queryFn: () => api<StockReport>(`/api/outlets/${outletId}/inventory/stock`) })
  const rows = (stock.data?.items ?? []).filter((r) => !lowOnly || r.isLow)

  return (
    <div className="space-y-4">
      <div className="grid gap-4 sm:grid-cols-3">
        <Card><div className="p-4"><p className="text-sm text-slate-500">Stock value</p>
          <p className="mt-1 text-2xl font-semibold" data-testid="stock-value">{money(stock.data?.totalValue)}</p></div></Card>
        <Card><div className="p-4"><p className="text-sm text-slate-500">Items tracked</p>
          <p className="mt-1 text-2xl font-semibold">{stock.data?.items.length ?? 0}</p></div></Card>
        <Card><div className="p-4"><p className="text-sm text-slate-500">Low or negative</p>
          <p className={clsx('mt-1 text-2xl font-semibold', (stock.data?.lowCount ?? 0) > 0 && 'text-rose-700')}>{stock.data?.lowCount ?? 0}</p></div></Card>
      </div>
      <Card actions={canAdjust && (
        <div className="flex flex-wrap gap-2">
          <Button size="sm" onClick={() => setAdjusting('Opening')}>Add stock</Button>
          <Button size="sm" variant="secondary" onClick={() => setAdjusting('Wastage')}>Record wastage</Button>
          <Button size="sm" variant="secondary" onClick={() => setAdjusting('Correction')}>Stock count correction</Button>
        </div>
      )} title="Current stock">
        <div className="border-b border-slate-100 px-4 py-2"><Checkbox label="Show only low stock" checked={lowOnly} onChange={setLowOnly} /></div>
        <ErrorBanner error={stock.error} />
        {stock.isLoading ? <Spinner /> : !stock.data?.items.length ? (
          <EmptyState title="No ingredients yet" description="Create ingredients in the Ingredients tab, then add opening stock." />
        ) : (
          <Table head={['Ingredient', 'In stock', 'Reorder level', 'Cost / unit', 'Value', 'Updated']} empty={rows.length === 0}>
            {rows.map((r) => (
              <tr key={r.inventoryItemId}>
                <Td className="font-medium text-slate-900">{r.name} {r.isLow && <Badge tone="red">{r.quantity < 0 ? 'Negative' : 'Low'}</Badge>}</Td>
                <Td className={clsx(r.quantity < 0 && 'text-rose-700')}>{qty(r.quantity)} {unitShort(r.unit)}</Td>
                <Td>{r.reorderLevel ? `${qty(r.reorderLevel)} ${unitShort(r.unit)}` : '—'}</Td>
                <Td>{money(r.costPerUnit)}</Td>
                <Td>{money(r.value)}</Td>
                <Td>{r.updatedAt ? dateTime(r.updatedAt) : '—'}</Td>
              </tr>
            ))}
          </Table>
        )}
      </Card>
      {adjusting && <AdjustDialog kind={adjusting} onClose={() => setAdjusting(null)} />}
    </div>
  )
}

const ADJUST_COPY = {
  Opening: { title: 'Add stock', hint: 'Opening stock or stock received without a purchase order.', action: 'Add stock' },
  Wastage: { title: 'Record wastage', hint: 'Spoiled, expired or dropped stock. Quantities are removed from stock.', action: 'Record wastage' },
  Correction: { title: 'Stock count correction', hint: 'Signed quantity: positive adds stock, negative removes it.', action: 'Save correction' },
} as const

function AdjustDialog({ kind, onClose }: { kind: keyof typeof ADJUST_COPY; onClose: () => void }) {
  const outletId = useOutletId()
  const items = useItems()
  const qc = useQueryClient()
  const toast = useToast()
  const [clientRef] = useState(newId)
  const [reason, setReason] = useState('')
  const [lines, setLines] = useState([{ key: newId(), inventoryItemId: '', quantity: '', unitCost: '' }])
  const active = (items.data ?? []).filter((i) => i.isActive)
  const copy = ADJUST_COPY[kind]

  const save = useMutation({
    mutationFn: () => api(`/api/outlets/${outletId}/inventory/adjustments`, {
      method: 'POST',
      body: {
        clientRef, kind, reason: reason || null,
        lines: lines.filter((l) => l.inventoryItemId).map((l) => ({
          inventoryItemId: l.inventoryItemId, quantity: Number(l.quantity), unitCost: l.unitCost === '' ? null : Number(l.unitCost),
        })),
      },
    }),
    onSuccess: () => {
      void qc.invalidateQueries({ queryKey: ['inventory'] })
      toast(`${copy.title} saved`)
      onClose()
    },
  })
  const update = (key: string, patch: Partial<(typeof lines)[number]>) => setLines((ls) => ls.map((l) => (l.key === key ? { ...l, ...patch } : l)))

  return (
    <Modal open onClose={onClose} title={copy.title} size="lg"
      footer={<>
        <Button variant="secondary" onClick={onClose}>Cancel</Button>
        <Button type="submit" form="adjust-form" loading={save.isPending}>{copy.action}</Button>
      </>}>
      <form id="adjust-form" className="space-y-3" onSubmit={(e) => { e.preventDefault(); save.mutate() }}>
        <p className="text-sm text-slate-600">{copy.hint}</p>
        {lines.map((l) => {
          const item = active.find((i) => i.id === l.inventoryItemId)
          return (
            <div key={l.key} className="flex flex-wrap items-end gap-2">
              <Select label="Ingredient" required value={l.inventoryItemId} onChange={(e) => update(l.key, { inventoryItemId: e.target.value })} className="min-w-48 flex-1">
                <option value="">Choose…</option>
                {active.map((i) => <option key={i.id} value={i.id}>{i.name}</option>)}
              </Select>
              <Input label={`Quantity${item ? ` (${unitShort(item.unit)})` : ''}`} type="number" step="0.001" required value={l.quantity}
                min={kind === 'Correction' ? undefined : 0.001} onChange={(e) => update(l.key, { quantity: e.target.value })} className="w-36" />
              {kind === 'Opening' && (
                <Input label="Cost / unit (₹)" type="number" min={0} step="0.01" value={l.unitCost} placeholder={item ? String(item.costPerUnit) : ''}
                  onChange={(e) => update(l.key, { unitCost: e.target.value })} className="w-36" />
              )}
              <Button variant="ghost" aria-label="Remove line" disabled={lines.length === 1} onClick={() => setLines((ls) => ls.filter((x) => x.key !== l.key))}>
                <Trash2 className="size-4" aria-hidden />
              </Button>
            </div>
          )
        })}
        <Button variant="secondary" size="sm" onClick={() => setLines((ls) => [...ls, { key: newId(), inventoryItemId: '', quantity: '', unitCost: '' }])}>
          <Plus className="size-4" aria-hidden /> Add line
        </Button>
        <Input label={kind === 'Opening' ? 'Note (optional)' : 'Reason'} required={kind !== 'Opening'} value={reason} onChange={(e) => setReason(e.target.value)} />
        <ErrorBanner error={save.error} />
      </form>
    </Modal>
  )
}

function ItemsTab({ editable }: { editable: boolean }) {
  const items = useItems()
  const [editing, setEditing] = useState<InventoryItem | 'new' | null>(null)
  return (
    <Card title="Ingredients" actions={editable && <Button size="sm" onClick={() => setEditing('new')}><Plus className="size-4" aria-hidden /> New ingredient</Button>}>
      <ErrorBanner error={items.error} />
      {items.isLoading ? <Spinner /> : !items.data?.length ? <EmptyState title="No ingredients yet" /> : (
        <Table head={['Name', 'SKU', 'Unit', 'Reorder level', 'Cost / unit', 'Status', '']}>
          {items.data.map((i) => (
            <tr key={i.id}>
              <Td className="font-medium text-slate-900">{i.name}</Td>
              <Td>{i.sku ?? '—'}</Td>
              <Td>{STOCK_UNITS.find((u) => u.value === i.unit)?.label}</Td>
              <Td>{qty(i.reorderLevel)} {unitShort(i.unit)}</Td>
              <Td>{money(i.costPerUnit)}</Td>
              <Td><Badge tone={i.isActive ? 'green' : 'slate'}>{i.isActive ? 'Active' : 'Archived'}</Badge></Td>
              <Td className="text-right">
                {editable && <Button variant="ghost" size="sm" aria-label={`Edit ${i.name}`} onClick={() => setEditing(i)}><Pencil className="size-4" aria-hidden /></Button>}
              </Td>
            </tr>
          ))}
        </Table>
      )}
      {editing && <ItemEditor item={editing === 'new' ? null : editing} onClose={() => setEditing(null)} />}
    </Card>
  )
}

function ItemEditor({ item, onClose }: { item: InventoryItem | null; onClose: () => void }) {
  const qc = useQueryClient()
  const toast = useToast()
  const [form, setForm] = useState({
    name: item?.name ?? '', sku: item?.sku ?? '', unit: item?.unit ?? 'Kilogram' as StockUnit,
    reorderLevel: item?.reorderLevel.toString() ?? '0', costPerUnit: item?.costPerUnit.toString() ?? '0', isActive: item?.isActive ?? true,
  })
  const save = useMutation({
    mutationFn: () => api(item ? `/api/inventory/items/${item.id}` : '/api/inventory/items', {
      method: item ? 'PUT' : 'POST',
      body: { ...form, sku: form.sku || null, reorderLevel: Number(form.reorderLevel), costPerUnit: Number(form.costPerUnit) },
    }),
    onSuccess: () => { void qc.invalidateQueries({ queryKey: ['inventory'] }); toast('Ingredient saved'); onClose() },
  })
  return (
    <Modal open onClose={onClose} title={item ? `Edit ${item.name}` : 'New ingredient'}
      footer={<>
        <Button variant="secondary" onClick={onClose}>Cancel</Button>
        <Button type="submit" form="ingredient-form" loading={save.isPending}>Save</Button>
      </>}>
      <form id="ingredient-form" className="grid gap-3 sm:grid-cols-2" onSubmit={(e) => { e.preventDefault(); save.mutate() }}>
        <Input label="Name" required value={form.name} onChange={(e) => setForm({ ...form, name: e.target.value })} className="sm:col-span-2" />
        <Select label="Unit" value={form.unit} onChange={(e) => setForm({ ...form, unit: e.target.value as StockUnit })}>
          {STOCK_UNITS.map((u) => <option key={u.value} value={u.value}>{u.label}</option>)}
        </Select>
        <Input label="SKU" value={form.sku} onChange={(e) => setForm({ ...form, sku: e.target.value })} />
        <Input label="Reorder level" type="number" min={0} step="0.001" value={form.reorderLevel} onChange={(e) => setForm({ ...form, reorderLevel: e.target.value })}
          hint="Flagged as low stock at or below this quantity." />
        <Input label="Cost per unit (₹)" type="number" min={0} step="0.01" value={form.costPerUnit} onChange={(e) => setForm({ ...form, costPerUnit: e.target.value })}
          hint="Updated automatically from goods receipts." />
        <Checkbox label="Active" checked={form.isActive} onChange={(v) => setForm({ ...form, isActive: v })} />
        <div className="sm:col-span-2"><ErrorBanner error={save.error} /></div>
      </form>
    </Modal>
  )
}

function RecipesTab({ editable }: { editable: boolean }) {
  const outletId = useOutletId()
  const menu = usePosMenu(outletId)
  const items = useItems()
  const recipes = useQuery({ queryKey: keys.recipes, queryFn: () => api<Recipe[]>('/api/inventory/recipes') })
  const [editing, setEditing] = useState<{ menuItemId: string; variantId: string | null; label: string } | null>(null)
  const itemName = useMemo(() => new Map(items.data?.map((i) => [i.id, i])), [items.data])

  const targets = (menu.data?.items ?? []).flatMap((m) => [
    { menuItemId: m.id, variantId: null as string | null, label: m.variants.length ? `${m.name} (default)` : m.name },
    ...m.variants.map((v) => ({ menuItemId: m.id, variantId: v.id as string | null, label: `${m.name} · ${v.name}` })),
  ])
  const recipeFor = (menuItemId: string, variantId: string | null) =>
    recipes.data?.find((r) => r.menuItemId === menuItemId && r.variantId === variantId)

  if (menu.isLoading || recipes.isLoading) return <Spinner />
  return (
    <Card title="Recipes">
      <p className="border-b border-slate-100 px-4 py-2 text-sm text-slate-500">
        Ingredients deducted per unit sold. A variant recipe overrides the item’s default recipe.
      </p>
      <ErrorBanner error={recipes.error} />
      <Table head={['Menu item', 'Ingredients', '']}>
        {targets.map((t) => {
          const r = recipeFor(t.menuItemId, t.variantId)
          return (
            <tr key={`${t.menuItemId}:${t.variantId ?? ''}`}>
              <Td className="font-medium text-slate-900">{t.label}</Td>
              <Td>
                {r ? r.lines.map((l) => {
                  const i = itemName.get(l.inventoryItemId)
                  return `${i?.name ?? 'Unknown'} ${qty(l.quantity)} ${unitShort(i?.unit)}`
                }).join(', ') : <span className="text-slate-400">No recipe</span>}
              </Td>
              <Td className="text-right">
                {editable && <Button variant="ghost" size="sm" onClick={() => setEditing(t)}>{r ? 'Edit' : 'Add recipe'}</Button>}
              </Td>
            </tr>
          )
        })}
      </Table>
      {editing && (
        <RecipeEditor target={editing} recipe={recipeFor(editing.menuItemId, editing.variantId)} ingredients={items.data ?? []}
          onClose={() => setEditing(null)} />
      )}
    </Card>
  )
}

function RecipeEditor({ target, recipe, ingredients, onClose }: {
  target: { menuItemId: string; variantId: string | null; label: string }; recipe?: Recipe; ingredients: InventoryItem[]; onClose: () => void
}) {
  const qc = useQueryClient()
  const toast = useToast()
  const [lines, setLines] = useState(recipe?.lines.map((l) => ({ key: l.id, inventoryItemId: l.inventoryItemId, quantity: String(l.quantity) }))
    ?? [{ key: newId(), inventoryItemId: '', quantity: '' }])
  const save = useMutation({
    mutationFn: (clear: boolean) => api('/api/inventory/recipes', {
      method: 'PUT',
      body: {
        menuItemId: target.menuItemId, variantId: target.variantId,
        lines: clear ? [] : lines.filter((l) => l.inventoryItemId).map((l) => ({ inventoryItemId: l.inventoryItemId, quantity: Number(l.quantity) })),
      },
    }),
    onSuccess: (_, clear) => { void qc.invalidateQueries({ queryKey: keys.recipes }); toast(clear ? 'Recipe removed' : 'Recipe saved'); onClose() },
  })
  return (
    <Modal open onClose={onClose} title={`Recipe · ${target.label}`} size="lg"
      footer={<>
        {recipe && <Button variant="ghost" className="mr-auto text-rose-700" onClick={() => save.mutate(true)}>Remove recipe</Button>}
        <Button variant="secondary" onClick={onClose}>Cancel</Button>
        <Button type="submit" form="recipe-form" loading={save.isPending}>Save recipe</Button>
      </>}>
      <form id="recipe-form" className="space-y-3" onSubmit={(e) => { e.preventDefault(); save.mutate(false) }}>
        {ingredients.length === 0 && <p className="text-sm text-amber-700">Create ingredients first in the Ingredients tab.</p>}
        {lines.map((l) => {
          const unit = ingredients.find((i) => i.id === l.inventoryItemId)?.unit
          return (
            <div key={l.key} className="flex items-end gap-2">
              <Select label="Ingredient" required value={l.inventoryItemId} className="flex-1"
                onChange={(e) => setLines((ls) => ls.map((x) => (x.key === l.key ? { ...x, inventoryItemId: e.target.value } : x)))}>
                <option value="">Choose…</option>
                {ingredients.filter((i) => i.isActive || i.id === l.inventoryItemId).map((i) => <option key={i.id} value={i.id}>{i.name}</option>)}
              </Select>
              <Input label={`Quantity per unit sold${unit ? ` (${unitShort(unit)})` : ''}`} type="number" min={0.0001} step="0.0001" required value={l.quantity} className="w-48"
                onChange={(e) => setLines((ls) => ls.map((x) => (x.key === l.key ? { ...x, quantity: e.target.value } : x)))} />
              <Button variant="ghost" aria-label="Remove ingredient" disabled={lines.length === 1} onClick={() => setLines((ls) => ls.filter((x) => x.key !== l.key))}>
                <Trash2 className="size-4" aria-hidden />
              </Button>
            </div>
          )
        })}
        <Button variant="secondary" size="sm" onClick={() => setLines((ls) => [...ls, { key: newId(), inventoryItemId: '', quantity: '' }])}>
          <Plus className="size-4" aria-hidden /> Add ingredient
        </Button>
        <ErrorBanner error={save.error} />
      </form>
    </Modal>
  )
}

const MOVEMENT_LABEL: Record<LedgerEntry['type'], string> = {
  Opening: 'Stock added', Purchase: 'Purchase', Consumption: 'Sale', Wastage: 'Wastage', Adjustment: 'Correction', ConsumptionReversal: 'Sale reversed',
}

function LedgerTab() {
  const outletId = useOutletId()
  const items = useItems()
  const [itemId, setItemId] = useState('')
  const ledger = useQuery({
    queryKey: keys.ledger(outletId, itemId),
    queryFn: () => api<LedgerEntry[]>(`/api/outlets/${outletId}/inventory/ledger?limit=200${itemId ? `&itemId=${itemId}` : ''}`),
  })
  return (
    <Card title="Stock movements">
      <div className="border-b border-slate-100 p-4">
        <Select label="Ingredient" value={itemId} onChange={(e) => setItemId(e.target.value)} className="w-64">
          <option value="">All ingredients</option>
          {items.data?.map((i) => <option key={i.id} value={i.id}>{i.name}</option>)}
        </Select>
      </div>
      <ErrorBanner error={ledger.error} />
      {ledger.isLoading ? <Spinner /> : (
        <Table head={['When', 'Ingredient', 'Movement', 'Quantity', 'Balance after', 'Note']} empty={!ledger.data?.length}>
          {ledger.data?.map((e) => (
            <tr key={e.id}>
              <Td>{dateTime(e.createdAt)}</Td>
              <Td className="font-medium text-slate-900">{e.itemName}</Td>
              <Td>{MOVEMENT_LABEL[e.type]}</Td>
              <Td className={e.quantity < 0 ? 'text-rose-700' : 'text-emerald-700'}>{e.quantity > 0 ? '+' : ''}{qty(e.quantity)} {unitShort(e.unit)}</Td>
              <Td>{qty(e.balanceAfter)} {unitShort(e.unit)}</Td>
              <Td className="max-w-xs truncate">{e.notes}</Td>
            </tr>
          ))}
        </Table>
      )}
    </Card>
  )
}
