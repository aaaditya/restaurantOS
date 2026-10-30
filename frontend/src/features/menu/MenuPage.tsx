import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import clsx from 'clsx'
import { Pencil, Plus, Trash2 } from 'lucide-react'
import { useMemo, useState, type FormEvent } from 'react'
import { api, errorMessage } from '../../app/api'
import { useOutletId, useSession } from '../../app/session'
import {
  Badge, Button, Card, Checkbox, EmptyState, ErrorBanner, FoodTypeMark, Input, Modal, PageHeader, Select, Spinner, Table, Td, Textarea, useToast,
} from '../../components/ui'
import { money } from '../../lib/format'
import type { Category, FoodType, MenuItem, ModifierGroup, PosMenu, TaxGroup } from '../../types'
import { posKeys, usePosMenu } from '../pos/usePos'

const keys = {
  items: ['admin-menu', 'items'] as const,
  categories: ['admin-menu', 'categories'] as const,
  modifiers: ['admin-menu', 'modifiers'] as const,
  taxes: ['admin-menu', 'taxes'] as const,
}

type Tab = 'items' | 'categories' | 'modifiers' | 'taxes'
const TABS: { id: Tab; label: string }[] = [
  { id: 'items', label: 'Items' }, { id: 'categories', label: 'Categories' },
  { id: 'modifiers', label: 'Modifier groups' }, { id: 'taxes', label: 'Tax groups' },
]

function useMenuData() {
  const items = useQuery({ queryKey: keys.items, queryFn: () => api<MenuItem[]>('/api/menu/items') })
  const categories = useQuery({ queryKey: keys.categories, queryFn: () => api<Category[]>('/api/menu/categories') })
  const modifiers = useQuery({ queryKey: keys.modifiers, queryFn: () => api<ModifierGroup[]>('/api/menu/modifier-groups') })
  const taxes = useQuery({ queryKey: keys.taxes, queryFn: () => api<TaxGroup[]>('/api/menu/tax-groups') })
  return { items, categories, modifiers, taxes }
}

/** Saves a tenant-level menu definition and refreshes both the admin lists and every outlet's POS menu. */
function useSave<T>(key: readonly unknown[], onDone: () => void) {
  const qc = useQueryClient()
  const toast = useToast()
  return useMutation({
    mutationFn: ({ url, id, body }: { url: string; id?: string; body: T }) =>
      api(id ? `${url}/${id}` : url, { method: id ? 'PUT' : 'POST', body }),
    onSuccess: () => {
      void qc.invalidateQueries({ queryKey: key })
      void qc.invalidateQueries({ queryKey: ['menu'] })
      toast('Saved')
      onDone()
    },
  })
}

export function MenuPage() {
  const [tab, setTab] = useState<Tab>('items')
  const { canTenant } = useSession()
  const editable = canTenant('MenuManage')
  const data = useMenuData()

  return (
    <div>
      <PageHeader title="Menu" description={editable
        ? 'Menu definitions are shared by all outlets. Availability and price overrides apply to the selected outlet only.'
        : 'You can view the menu and, if permitted, change availability for this outlet.'} />
      <div role="tablist" aria-label="Menu sections" className="mb-4 flex gap-1 border-b border-slate-200">
        {TABS.map((t) => (
          <button key={t.id} role="tab" aria-selected={tab === t.id} onClick={() => setTab(t.id)}
            className={clsx('-mb-px border-b-2 px-4 py-2 text-sm font-medium',
              tab === t.id ? 'border-brand-700 text-brand-800' : 'border-transparent text-slate-500 hover:text-slate-800')}>
            {t.label}
          </button>
        ))}
      </div>
      {tab === 'items' && <ItemsTab data={data} editable={editable} />}
      {tab === 'categories' && <CategoriesTab categories={data.categories.data} loading={data.categories.isLoading} editable={editable} />}
      {tab === 'modifiers' && <ModifiersTab groups={data.modifiers.data} loading={data.modifiers.isLoading} editable={editable} />}
      {tab === 'taxes' && <TaxesTab groups={data.taxes.data} loading={data.taxes.isLoading} editable={editable} />}
    </div>
  )
}

function ItemsTab({ data, editable }: { data: ReturnType<typeof useMenuData>; editable: boolean }) {
  const outletId = useOutletId()
  const { can } = useSession()
  const outletMenu = usePosMenu(outletId)
  const [editing, setEditing] = useState<MenuItem | 'new' | null>(null)
  const [overriding, setOverriding] = useState<MenuItem | null>(null)
  const [search, setSearch] = useState('')
  const [category, setCategory] = useState('')
  const qc = useQueryClient()
  const toast = useToast()

  const outletInfo = useMemo(() => new Map(outletMenu.data?.items.map((i) => [i.id, i])), [outletMenu.data])
  const categoryName = useMemo(() => new Map(data.categories.data?.map((c) => [c.id, c.name])), [data.categories.data])
  const items = (data.items.data ?? []).filter((i) =>
    (!category || i.categoryId === category) && i.name.toLowerCase().includes(search.trim().toLowerCase()))

  const toggle = useMutation({
    mutationFn: ({ item, isAvailable }: { item: MenuItem; isAvailable: boolean }) =>
      api(`/api/outlets/${outletId}/menu/items/${item.id}/override`, {
        method: 'PUT', body: { isAvailable, priceOverride: outletInfo.get(item.id)?.priceOverride ?? null },
      }),
    onMutate: async ({ item, isAvailable }) => {
      await qc.cancelQueries({ queryKey: posKeys.menu(outletId) })
      const previous = qc.getQueryData<PosMenu>(posKeys.menu(outletId))
      if (previous) {
        qc.setQueryData<PosMenu>(posKeys.menu(outletId), {
          ...previous, items: previous.items.map((i) => (i.id === item.id ? { ...i, isAvailable } : i)),
        })
      }
      return { previous }
    },
    onSuccess: (_, v) => toast(`${v.item.name} is now ${v.isAvailable ? 'available' : 'unavailable'} at this outlet`),
    onError: (e, _, ctx) => {
      if (ctx?.previous) qc.setQueryData(posKeys.menu(outletId), ctx.previous)
      toast(errorMessage(e), 'error')
    },
    onSettled: () => void qc.invalidateQueries({ queryKey: posKeys.menu(outletId) }),
  })

  if (data.items.isLoading || data.categories.isLoading) return <Spinner />
  return (
    <Card>
      <div className="flex flex-wrap items-end gap-3 border-b border-slate-100 p-4">
        <Input label="Search" placeholder="Item name" value={search} onChange={(e) => setSearch(e.target.value)} className="w-56" />
        <Select label="Category" value={category} onChange={(e) => setCategory(e.target.value)} className="w-48">
          <option value="">All categories</option>
          {data.categories.data?.map((c) => <option key={c.id} value={c.id}>{c.name}</option>)}
        </Select>
        <div className="flex-1" />
        {editable && (
          <Button onClick={() => setEditing('new')} disabled={!data.categories.data?.length}
            title={data.categories.data?.length ? undefined : 'Create a category first'}>
            <Plus className="size-4" aria-hidden /> New item
          </Button>
        )}
      </div>
      <ErrorBanner error={data.items.error} />
      <Table head={['Item', 'Category', 'Price', 'Station', 'At this outlet', '']} empty={items.length === 0}>
        {items.map((item) => {
          const info = outletInfo.get(item.id)
          return (
            <tr key={item.id} className={clsx(!item.isActive && 'opacity-60')}>
              <Td>
                <div className="flex items-center gap-2 font-medium text-slate-900">
                  <FoodTypeMark type={item.foodType} /> {item.name}
                  {!item.isActive && <Badge>Archived</Badge>}
                </div>
                {item.variants.filter((v) => v.isActive).length > 0 && (
                  <p className="text-xs text-slate-500">{item.variants.filter((v) => v.isActive).map((v) => `${v.name} ${money(v.price)}`).join(' · ')}</p>
                )}
              </Td>
              <Td>{categoryName.get(item.categoryId) ?? '—'}</Td>
              <Td>
                {money(item.price)}
                {info?.priceOverride != null && <p className="text-xs text-amber-700">Outlet price {money(info.priceOverride)}</p>}
              </Td>
              <Td>{item.station}</Td>
              <Td>
                {item.isActive && info ? (
                  can('MenuManage') ? (
                    <Checkbox label={info.isAvailable ? 'Available' : 'Unavailable'} checked={info.isAvailable}
                      disabled={toggle.isPending} onChange={(v) => toggle.mutate({ item, isAvailable: v })} />
                  ) : <Badge tone={info.isAvailable ? 'green' : 'red'}>{info.isAvailable ? 'Available' : 'Unavailable'}</Badge>
                ) : <span className="text-slate-500">{item.isActive ? 'Not listed' : 'Inactive'}</span>}
              </Td>
              <Td className="text-right whitespace-nowrap">
                {can('MenuManage') && item.isActive && info && (
                  <Button variant="ghost" size="sm" onClick={() => setOverriding(item)}>Outlet price</Button>
                )}
                {editable && (
                  <Button variant="ghost" size="sm" onClick={() => setEditing(item)} aria-label={`Edit ${item.name}`}>
                    <Pencil className="size-4" aria-hidden />
                  </Button>
                )}
              </Td>
            </tr>
          )
        })}
      </Table>
      {editing && (
        <ItemEditor item={editing === 'new' ? null : editing} categories={data.categories.data ?? []}
          taxes={data.taxes.data ?? []} defaultTaxGroupId={mostUsedTaxGroup(data.items.data ?? [], data.taxes.data ?? [])} modifiers={data.modifiers.data ?? []} onClose={() => setEditing(null)} />
      )}
      {overriding && (
        <PriceOverrideDialog item={overriding} isAvailable={outletInfo.get(overriding.id)?.isAvailable ?? true}
          current={outletInfo.get(overriding.id)?.priceOverride ?? null} onClose={() => setOverriding(null)} />
      )}
    </Card>
  )
}

function PriceOverrideDialog({ item, isAvailable, current, onClose }: {
  item: MenuItem; isAvailable: boolean; current: number | null; onClose: () => void
}) {
  const outletId = useOutletId()
  const qc = useQueryClient()
  const toast = useToast()
  const [value, setValue] = useState(current?.toString() ?? '')
  const save = useMutation({
    mutationFn: (priceOverride: number | null) =>
      api(`/api/outlets/${outletId}/menu/items/${item.id}/override`, { method: 'PUT', body: { isAvailable, priceOverride } }),
    onSuccess: () => {
      void qc.invalidateQueries({ queryKey: posKeys.menu(outletId) })
      toast('Outlet price saved')
      onClose()
    },
  })
  const submit = (e: FormEvent) => {
    e.preventDefault()
    save.mutate(value.trim() === '' ? null : Number(value))
  }
  return (
    <Modal open onClose={onClose} title={`Outlet price · ${item.name}`} size="sm"
      footer={<>
        <Button variant="secondary" onClick={onClose}>Cancel</Button>
        <Button type="submit" form="override-form" loading={save.isPending}>Save</Button>
      </>}>
      <form id="override-form" onSubmit={submit} className="space-y-3">
        <p className="text-sm text-slate-600">Base price is {money(item.price)}. Leave empty to use the base price at this outlet.</p>
        <Input label="Outlet price (₹)" type="number" min={0} step="0.01" value={value} onChange={(e) => setValue(e.target.value)} />
        {item.variants.some((v) => v.isActive) && <p className="text-xs text-slate-500">Variant prices are not affected by the outlet price.</p>}
        <ErrorBanner error={save.error} />
      </form>
    </Modal>
  )
}

const FOOD_TYPES: { value: FoodType; label: string }[] = [
  { value: 'Veg', label: 'Vegetarian' }, { value: 'NonVeg', label: 'Non-vegetarian' },
  { value: 'Egg', label: 'Contains egg' }, { value: 'NotApplicable', label: 'Not applicable' },
]

type VariantDraft = { id: string | null; name: string; price: string; isActive: boolean }

function mostUsedTaxGroup(items: MenuItem[], taxes: TaxGroup[]) {
  const active = taxes.filter((t) => t.isActive)
  const uses = (id: string) => items.filter((i) => i.taxGroupId === id).length
  return [...active].sort((a, b) => uses(b.id) - uses(a.id))[0]?.id
}

function ItemEditor({ item, categories, taxes, defaultTaxGroupId, modifiers, onClose }: {
  item: MenuItem | null; categories: Category[]; taxes: TaxGroup[]; defaultTaxGroupId?: string; modifiers: ModifierGroup[]; onClose: () => void
}) {
  const [form, setForm] = useState({
    name: item?.name ?? '', description: item?.description ?? '', sku: item?.sku ?? '', price: item?.price.toString() ?? '',
    categoryId: item?.categoryId ?? categories.find((c) => c.isActive)?.id ?? '', taxGroupId: item ? item.taxGroupId ?? '' : defaultTaxGroupId ?? '',
    foodType: item?.foodType ?? 'Veg', station: item?.station ?? 'Main', sortOrder: item?.sortOrder.toString() ?? '0',
    isActive: item?.isActive ?? true,
  })
  const [variants, setVariants] = useState<VariantDraft[]>(
    item?.variants.filter((v) => v.isActive).map((v) => ({ id: v.id, name: v.name, price: v.price.toString(), isActive: true })) ?? [])
  const [groupIds, setGroupIds] = useState<string[]>(item?.modifierGroupIds ?? [])
  const save = useSave(keys.items, onClose)
  const set = <K extends keyof typeof form>(k: K, v: (typeof form)[K]) => setForm((f) => ({ ...f, [k]: v }))

  const submit = (e: FormEvent) => {
    e.preventDefault()
    save.mutate({
      url: '/api/menu/items', id: item?.id,
      body: {
        categoryId: form.categoryId, name: form.name, description: form.description || null, sku: form.sku || null,
        price: Number(form.price || 0), taxGroupId: form.taxGroupId || null, foodType: form.foodType, station: form.station || null,
        sortOrder: Number(form.sortOrder || 0), isActive: form.isActive,
        variants: variants.map((v) => ({ id: v.id, name: v.name, price: Number(v.price || 0), isActive: true })),
        modifierGroupIds: groupIds,
      },
    })
  }

  return (
    <Modal open onClose={onClose} title={item ? `Edit ${item.name}` : 'New menu item'} size="lg"
      footer={<>
        <Button variant="secondary" onClick={onClose}>Cancel</Button>
        <Button type="submit" form="item-form" loading={save.isPending}>Save item</Button>
      </>}>
      <form id="item-form" onSubmit={submit} className="grid gap-4 sm:grid-cols-2">
        <Input label="Name" required maxLength={200} value={form.name} onChange={(e) => set('name', e.target.value)} className="sm:col-span-2" />
        <Select label="Category" required value={form.categoryId} onChange={(e) => set('categoryId', e.target.value)}>
          {categories.map((c) => <option key={c.id} value={c.id}>{c.name}{c.isActive ? '' : ' (archived)'}</option>)}
        </Select>
        <Input label="Price (₹)" type="number" min={0} step="0.01" required value={form.price} onChange={(e) => set('price', e.target.value)}
          hint={variants.length ? 'Used only when no variant is chosen; variants carry their own price.' : undefined} />
        <Select label="Tax group" value={form.taxGroupId} onChange={(e) => set('taxGroupId', e.target.value)}>
          <option value="">No tax</option>
          {taxes.map((t) => <option key={t.id} value={t.id}>{t.name}</option>)}
        </Select>
        <Select label="Food type" value={form.foodType} onChange={(e) => set('foodType', e.target.value as FoodType)}>
          {FOOD_TYPES.map((f) => <option key={f.value} value={f.value}>{f.label}</option>)}
        </Select>
        <Input label="Kitchen station" value={form.station} maxLength={60} onChange={(e) => set('station', e.target.value)}
          hint="KOTs are split per station, e.g. Main, Bar, Tandoor." />
        <Input label="SKU" value={form.sku} maxLength={60} onChange={(e) => set('sku', e.target.value)} />
        <Input label="Sort order" type="number" value={form.sortOrder} onChange={(e) => set('sortOrder', e.target.value)} />
        <div className="flex items-end pb-2"><Checkbox label="Active" checked={form.isActive} onChange={(v) => set('isActive', v)} /></div>
        <Textarea label="Description" value={form.description} maxLength={1000} onChange={(e) => set('description', e.target.value)} className="sm:col-span-2" />

        <fieldset className="sm:col-span-2">
          <legend className="mb-2 text-sm font-medium text-slate-700">Variants (e.g. Half / Full)</legend>
          <div className="space-y-2">
            {variants.map((v, i) => (
              <div key={v.id ?? `new-${i}`} className="flex items-end gap-2">
                <Input label="Variant name" value={v.name} required className="flex-1"
                  onChange={(e) => setVariants((vs) => vs.map((x, j) => (j === i ? { ...x, name: e.target.value } : x)))} />
                <Input label="Price (₹)" type="number" min={0} step="0.01" value={v.price} required className="w-32"
                  onChange={(e) => setVariants((vs) => vs.map((x, j) => (j === i ? { ...x, price: e.target.value } : x)))} />
                <Button variant="ghost" aria-label={`Remove variant ${v.name}`} onClick={() => setVariants((vs) => vs.filter((_, j) => j !== i))}>
                  <Trash2 className="size-4" aria-hidden />
                </Button>
              </div>
            ))}
            <Button variant="secondary" size="sm" onClick={() => setVariants((vs) => [...vs, { id: null, name: '', price: '', isActive: true }])}>
              <Plus className="size-4" aria-hidden /> Add variant
            </Button>
          </div>
        </fieldset>

        <fieldset className="sm:col-span-2">
          <legend className="mb-2 text-sm font-medium text-slate-700">Modifier groups</legend>
          {modifiers.filter((m) => m.isActive).length === 0
            ? <p className="text-sm text-slate-500">No modifier groups yet.</p>
            : (
              <div className="flex flex-wrap gap-4">
                {modifiers.filter((m) => m.isActive).map((m) => (
                  <Checkbox key={m.id} label={m.name} checked={groupIds.includes(m.id)}
                    onChange={(on) => setGroupIds((ids) => (on ? [...ids, m.id] : ids.filter((x) => x !== m.id)))} />
                ))}
              </div>
            )}
        </fieldset>
        <div className="sm:col-span-2"><ErrorBanner error={save.error} /></div>
      </form>
    </Modal>
  )
}

function CategoriesTab({ categories, loading, editable }: { categories?: Category[]; loading: boolean; editable: boolean }) {
  const [editing, setEditing] = useState<Category | 'new' | null>(null)
  if (loading) return <Spinner />
  return (
    <Card actions={editable && <Button size="sm" onClick={() => setEditing('new')}><Plus className="size-4" aria-hidden /> New category</Button>} title="Categories">
      {!categories?.length ? <EmptyState title="No categories" description="Categories group items on the POS." /> : (
        <Table head={['Name', 'Sort order', 'Status', '']}>
          {categories.map((c) => (
            <tr key={c.id}>
              <Td className="font-medium text-slate-900">{c.name}</Td>
              <Td>{c.sortOrder}</Td>
              <Td><Badge tone={c.isActive ? 'green' : 'slate'}>{c.isActive ? 'Active' : 'Archived'}</Badge></Td>
              <Td className="text-right">
                {editable && <Button variant="ghost" size="sm" onClick={() => setEditing(c)} aria-label={`Edit ${c.name}`}><Pencil className="size-4" aria-hidden /></Button>}
              </Td>
            </tr>
          ))}
        </Table>
      )}
      {editing && <CategoryEditor category={editing === 'new' ? null : editing} onClose={() => setEditing(null)} />}
    </Card>
  )
}

function CategoryEditor({ category, onClose }: { category: Category | null; onClose: () => void }) {
  const [name, setName] = useState(category?.name ?? '')
  const [sortOrder, setSortOrder] = useState(category?.sortOrder.toString() ?? '0')
  const [isActive, setIsActive] = useState(category?.isActive ?? true)
  const save = useSave(keys.categories, onClose)
  return (
    <Modal open onClose={onClose} title={category ? 'Edit category' : 'New category'} size="sm"
      footer={<>
        <Button variant="secondary" onClick={onClose}>Cancel</Button>
        <Button type="submit" form="category-form" loading={save.isPending}>Save</Button>
      </>}>
      <form id="category-form" className="space-y-3" onSubmit={(e) => {
        e.preventDefault()
        save.mutate({ url: '/api/menu/categories', id: category?.id, body: { name, sortOrder: Number(sortOrder || 0), isActive } })
      }}>
        <Input label="Name" required value={name} onChange={(e) => setName(e.target.value)} />
        <Input label="Sort order" type="number" value={sortOrder} onChange={(e) => setSortOrder(e.target.value)} />
        <Checkbox label="Active" checked={isActive} onChange={setIsActive} />
        <ErrorBanner error={save.error} />
      </form>
    </Modal>
  )
}

function ModifiersTab({ groups, loading, editable }: { groups?: ModifierGroup[]; loading: boolean; editable: boolean }) {
  const [editing, setEditing] = useState<ModifierGroup | 'new' | null>(null)
  if (loading) return <Spinner />
  return (
    <Card title="Modifier groups" actions={editable && <Button size="sm" onClick={() => setEditing('new')}><Plus className="size-4" aria-hidden /> New group</Button>}>
      {!groups?.length ? <EmptyState title="No modifier groups" description="Use modifier groups for add-ons like extra cheese or spice level." /> : (
        <Table head={['Group', 'Selection', 'Options', 'Status', '']}>
          {groups.map((g) => (
            <tr key={g.id}>
              <Td className="font-medium text-slate-900">{g.name}</Td>
              <Td>{g.minSelect === 0 ? 'Optional' : `Min ${g.minSelect}`} · max {g.maxSelect}</Td>
              <Td>{g.options.filter((o) => o.isActive).map((o) => `${o.name}${o.price ? ` +${money(o.price)}` : ''}`).join(', ')}</Td>
              <Td><Badge tone={g.isActive ? 'green' : 'slate'}>{g.isActive ? 'Active' : 'Archived'}</Badge></Td>
              <Td className="text-right">
                {editable && <Button variant="ghost" size="sm" onClick={() => setEditing(g)} aria-label={`Edit ${g.name}`}><Pencil className="size-4" aria-hidden /></Button>}
              </Td>
            </tr>
          ))}
        </Table>
      )}
      {editing && <ModifierEditor group={editing === 'new' ? null : editing} onClose={() => setEditing(null)} />}
    </Card>
  )
}

function ModifierEditor({ group, onClose }: { group: ModifierGroup | null; onClose: () => void }) {
  const [name, setName] = useState(group?.name ?? '')
  const [minSelect, setMin] = useState(group?.minSelect.toString() ?? '0')
  const [maxSelect, setMax] = useState(group?.maxSelect.toString() ?? '1')
  const [isActive, setIsActive] = useState(group?.isActive ?? true)
  const [options, setOptions] = useState(group?.options.filter((o) => o.isActive).map((o) => ({ id: o.id as string | null, name: o.name, price: o.price.toString() }))
    ?? [{ id: null, name: '', price: '0' }])
  const save = useSave(keys.modifiers, onClose)
  return (
    <Modal open onClose={onClose} title={group ? 'Edit modifier group' : 'New modifier group'}
      footer={<>
        <Button variant="secondary" onClick={onClose}>Cancel</Button>
        <Button type="submit" form="modifier-form" loading={save.isPending}>Save</Button>
      </>}>
      <form id="modifier-form" className="space-y-3" onSubmit={(e) => {
        e.preventDefault()
        save.mutate({
          url: '/api/menu/modifier-groups', id: group?.id,
          body: { name, minSelect: Number(minSelect), maxSelect: Number(maxSelect), isActive, options: options.map((o) => ({ id: o.id, name: o.name, price: Number(o.price || 0) })) },
        })
      }}>
        <Input label="Name" required value={name} onChange={(e) => setName(e.target.value)} />
        <div className="grid grid-cols-2 gap-3">
          <Input label="Minimum selections" type="number" min={0} value={minSelect} onChange={(e) => setMin(e.target.value)} />
          <Input label="Maximum selections" type="number" min={1} value={maxSelect} onChange={(e) => setMax(e.target.value)} />
        </div>
        <fieldset className="space-y-2">
          <legend className="text-sm font-medium text-slate-700">Options</legend>
          {options.map((o, i) => (
            <div key={o.id ?? `new-${i}`} className="flex items-end gap-2">
              <Input label="Option" required value={o.name} className="flex-1"
                onChange={(e) => setOptions((os) => os.map((x, j) => (j === i ? { ...x, name: e.target.value } : x)))} />
              <Input label="Extra price (₹)" type="number" min={0} step="0.01" value={o.price} className="w-32"
                onChange={(e) => setOptions((os) => os.map((x, j) => (j === i ? { ...x, price: e.target.value } : x)))} />
              <Button variant="ghost" aria-label={`Remove option ${o.name}`} disabled={options.length === 1}
                onClick={() => setOptions((os) => os.filter((_, j) => j !== i))}><Trash2 className="size-4" aria-hidden /></Button>
            </div>
          ))}
          <Button variant="secondary" size="sm" onClick={() => setOptions((os) => [...os, { id: null, name: '', price: '0' }])}>
            <Plus className="size-4" aria-hidden /> Add option
          </Button>
        </fieldset>
        <Checkbox label="Active" checked={isActive} onChange={setIsActive} />
        <ErrorBanner error={save.error} />
      </form>
    </Modal>
  )
}

function TaxesTab({ groups, loading, editable }: { groups?: TaxGroup[]; loading: boolean; editable: boolean }) {
  const [editing, setEditing] = useState<TaxGroup | 'new' | null>(null)
  if (loading) return <Spinner />
  return (
    <Card title="Tax groups" actions={editable && <Button size="sm" onClick={() => setEditing('new')}><Plus className="size-4" aria-hidden /> New tax group</Button>}>
      {!groups?.length ? <EmptyState title="No tax groups" /> : (
        <Table head={['Name', 'Components', 'Total rate', 'Status', '']}>
          {groups.map((g) => (
            <tr key={g.id}>
              <Td className="font-medium text-slate-900">{g.name}</Td>
              <Td>{g.components.map((c) => `${c.code} ${c.ratePercent}%`).join(' + ') || 'None'}</Td>
              <Td>{g.components.reduce((s, c) => s + c.ratePercent, 0)}%</Td>
              <Td><Badge tone={g.isActive ? 'green' : 'slate'}>{g.isActive ? 'Active' : 'Archived'}</Badge></Td>
              <Td className="text-right">
                {editable && <Button variant="ghost" size="sm" onClick={() => setEditing(g)} aria-label={`Edit ${g.name}`}><Pencil className="size-4" aria-hidden /></Button>}
              </Td>
            </tr>
          ))}
        </Table>
      )}
      {editing && <TaxEditor group={editing === 'new' ? null : editing} onClose={() => setEditing(null)} />}
    </Card>
  )
}

function TaxEditor({ group, onClose }: { group: TaxGroup | null; onClose: () => void }) {
  const [name, setName] = useState(group?.name ?? '')
  const [isActive, setIsActive] = useState(group?.isActive ?? true)
  const [components, setComponents] = useState(group?.components.map((c) => ({ code: c.code, rate: c.ratePercent.toString() }))
    ?? [{ code: 'CGST', rate: '2.5' }, { code: 'SGST', rate: '2.5' }])
  const save = useSave(keys.taxes, onClose)
  return (
    <Modal open onClose={onClose} title={group ? 'Edit tax group' : 'New tax group'}
      footer={<>
        <Button variant="secondary" onClick={onClose}>Cancel</Button>
        <Button type="submit" form="tax-form" loading={save.isPending}>Save</Button>
      </>}>
      <form id="tax-form" className="space-y-3" onSubmit={(e) => {
        e.preventDefault()
        save.mutate({ url: '/api/menu/tax-groups', id: group?.id, body: { name, isActive, components: components.map((c) => ({ code: c.code, ratePercent: Number(c.rate) })) } })
      }}>
        <Input label="Name" required value={name} onChange={(e) => setName(e.target.value)} />
        <fieldset className="space-y-2">
          <legend className="text-sm font-medium text-slate-700">Components</legend>
          {components.map((c, i) => (
            <div key={i} className="flex items-end gap-2">
              <Input label="Code" required value={c.code} className="flex-1"
                onChange={(e) => setComponents((cs) => cs.map((x, j) => (j === i ? { ...x, code: e.target.value } : x)))} />
              <Input label="Rate (%)" type="number" min={0} max={100} step="0.01" required value={c.rate} className="w-28"
                onChange={(e) => setComponents((cs) => cs.map((x, j) => (j === i ? { ...x, rate: e.target.value } : x)))} />
              <Button variant="ghost" aria-label={`Remove ${c.code}`} onClick={() => setComponents((cs) => cs.filter((_, j) => j !== i))}>
                <Trash2 className="size-4" aria-hidden />
              </Button>
            </div>
          ))}
          <Button variant="secondary" size="sm" onClick={() => setComponents((cs) => [...cs, { code: '', rate: '0' }])}>
            <Plus className="size-4" aria-hidden /> Add component
          </Button>
        </fieldset>
        {group && <p className="text-xs text-slate-500">Rate changes apply to new order lines only; existing orders keep their tax snapshot.</p>}
        <Checkbox label="Active" checked={isActive} onChange={setIsActive} />
        <ErrorBanner error={save.error} />
      </form>
    </Modal>
  )
}
