import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { Plus, Search } from 'lucide-react'
import { useDeferredValue, useState } from 'react'
import { api } from '../../app/api'
import { useSession } from '../../app/session'
import { Badge, Button, Card, EmptyState, ErrorBanner, Input, Modal, PageHeader, Select, Spinner, Table, Td, Textarea, useToast } from '../../components/ui'
import { dateTime, money, orderTypeLabel } from '../../lib/format'
import type { Customer, CustomerDetail } from '../../types'

type Sort = 'recent' | 'spend' | 'visits' | 'points'

export function CustomersPage() {
  const { can } = useSession()
  const [search, setSearch] = useState('')
  const [sort, setSort] = useState<Sort>('recent')
  const term = useDeferredValue(search.trim())
  const list = useQuery({
    queryKey: ['customers', term, sort],
    queryFn: () => api<Customer[]>(`/api/customers?${new URLSearchParams({ search: term, sort, limit: '200' })}`),
    placeholderData: (prev) => prev,
  })
  const [editing, setEditing] = useState<Customer | 'new' | null>(null)
  const [viewing, setViewing] = useState<string | null>(null)

  return (
    <div className="space-y-6">
      <PageHeader title="Customers" description="Guests are added automatically when a phone number is entered on an order. Points are earned on every settled bill."
        actions={can('CustomerManage') && <Button onClick={() => setEditing('new')}><Plus className="size-4" /> Add customer</Button>} />
      <Card>
        <div className="flex flex-wrap items-end gap-3 border-b border-slate-100 p-4">
          <div className="relative min-w-60 flex-1">
            <Search className="pointer-events-none absolute left-3 top-1/2 size-4 -translate-y-1/2 text-slate-400" aria-hidden />
            <input type="search" aria-label="Search customers" placeholder="Search by name or phone" value={search} onChange={(e) => setSearch(e.target.value)}
              className="w-full rounded-lg border-slate-300 pl-9 text-sm focus:border-brand-600 focus:ring-brand-600" />
          </div>
          <Select label="Sort by" value={sort} onChange={(e) => setSort(e.target.value as Sort)} className="w-48">
            <option value="recent">Most recent visit</option>
            <option value="spend">Total spend</option>
            <option value="visits">Visits</option>
            <option value="points">Points balance</option>
          </Select>
        </div>
        <ErrorBanner error={list.error} />
        {list.isLoading ? <Spinner /> : !list.data?.length ? (
          <EmptyState title={term ? 'No customers match' : 'No customers yet'}
            description={term ? undefined : 'Customers appear here once you attach a phone number to an order.'} />
        ) : (
          <Table head={['Customer', 'Phone', 'Visits', 'Total spend', 'Points', 'Last visit', '']}>
            {list.data.map((c) => (
              <tr key={c.id}>
                <Td className="font-medium text-slate-900">{c.name ?? <span className="text-slate-400">Unnamed</span>}</Td>
                <Td>{c.phone}</Td>
                <Td>{c.visitCount}</Td>
                <Td>{money(c.totalSpend)}</Td>
                <Td><Badge tone={c.loyaltyPoints > 0 ? 'green' : 'slate'}>{c.loyaltyPoints} pts</Badge></Td>
                <Td>{c.lastVisitAt ? dateTime(c.lastVisitAt) : '—'}</Td>
                <Td className="text-right"><Button variant="ghost" size="sm" onClick={() => setViewing(c.id)} aria-label={`View ${c.name ?? c.phone}`}>View</Button></Td>
              </tr>
            ))}
          </Table>
        )}
      </Card>
      {editing && <CustomerForm customer={editing === 'new' ? null : editing} onClose={() => setEditing(null)} />}
      {viewing && <CustomerDrawer id={viewing} onClose={() => setViewing(null)} onEdit={(c) => { setViewing(null); setEditing(c) }} />}
    </div>
  )
}

function CustomerForm({ customer, onClose }: { customer: Customer | null; onClose: () => void }) {
  const qc = useQueryClient()
  const toast = useToast()
  const [form, setForm] = useState({
    phone: customer?.phone ?? '', name: customer?.name ?? '', email: customer?.email ?? '', birthday: customer?.birthday ?? '', notes: customer?.notes ?? '',
  })
  const set = (k: keyof typeof form) => (e: { target: { value: string } }) => setForm((f) => ({ ...f, [k]: e.target.value }))
  const save = useMutation({
    mutationFn: () => api<Customer>(customer ? `/api/customers/${customer.id}` : '/api/customers', {
      method: customer ? 'PUT' : 'POST',
      body: { phone: form.phone, name: form.name || null, email: form.email || null, birthday: form.birthday || null, notes: form.notes || null },
    }),
    onSuccess: () => {
      void qc.invalidateQueries({ queryKey: ['customers'] })
      void qc.invalidateQueries({ queryKey: ['customer'] })
      toast(customer ? 'Customer updated' : 'Customer added')
      onClose()
    },
  })
  return (
    <Modal open onClose={onClose} title={customer ? 'Edit customer' : 'Add customer'} size="md"
      footer={<>
        <Button variant="secondary" onClick={onClose}>Cancel</Button>
        <Button type="submit" form="customer-form" loading={save.isPending}>Save</Button>
      </>}>
      <form id="customer-form" className="grid gap-3 sm:grid-cols-2" onSubmit={(e) => { e.preventDefault(); save.mutate() }}>
        <Input label="Phone" inputMode="tel" required value={form.phone} onChange={set('phone')} autoFocus />
        <Input label="Name" value={form.name} onChange={set('name')} maxLength={200} />
        <Input label="Email" type="email" value={form.email} onChange={set('email')} />
        <Input label="Birthday" type="date" value={form.birthday} onChange={set('birthday')} />
        <Textarea label="Notes" className="sm:col-span-2" value={form.notes} onChange={set('notes')} maxLength={1000} />
        <div className="sm:col-span-2"><ErrorBanner error={save.error} /></div>
      </form>
    </Modal>
  )
}

function CustomerDrawer({ id, onClose, onEdit }: { id: string; onClose: () => void; onEdit: (c: Customer) => void }) {
  const { can, canTenant } = useSession()
  const detail = useQuery({ queryKey: ['customer', id], queryFn: () => api<CustomerDetail>(`/api/customers/${id}`) })
  const [adjusting, setAdjusting] = useState(false)
  const c = detail.data?.customer
  return (<>
    <Modal open onClose={onClose} title={c?.name ?? c?.phone ?? 'Customer'} size="lg"
      footer={c && <>
        {canTenant('CustomerManage') && <Button variant="secondary" onClick={() => setAdjusting(true)}>Adjust points</Button>}
        {can('CustomerManage') && <Button onClick={() => onEdit(c)}>Edit</Button>}
      </>}>
      <ErrorBanner error={detail.error} />
      {!detail.data || !c ? <Spinner /> : (
        <div className="space-y-5">
          <dl className="grid grid-cols-2 gap-4 sm:grid-cols-4">
            <Stat label="Phone" value={c.phone} />
            <Stat label="Points" value={`${c.loyaltyPoints}`} testId="customer-points" />
            <Stat label="Visits" value={`${c.visitCount}`} />
            <Stat label="Total spend" value={money(c.totalSpend)} />
          </dl>
          {(c.email || c.birthday || c.notes) && (
            <p className="text-sm text-slate-600">{[c.email, c.birthday && `Birthday ${c.birthday}`, c.notes].filter(Boolean).join(' · ')}</p>
          )}
          <section>
            <h3 className="mb-2 text-sm font-semibold">Recent bills</h3>
            {detail.data.orders.length === 0 ? <p className="text-sm text-slate-500">No settled bills yet.</p> : (
              <Table head={['Invoice', 'Type', 'Amount', 'Settled']}>
                {detail.data.orders.map((o) => (
                  <tr key={o.id}>
                    <Td>{o.invoiceNumber ?? `#${o.orderNumber}`}</Td>
                    <Td>{orderTypeLabel[o.type]}</Td>
                    <Td>{money(o.grandTotal)}</Td>
                    <Td>{dateTime(o.settledAt)}</Td>
                  </tr>
                ))}
              </Table>
            )}
          </section>
          <section>
            <h3 className="mb-2 text-sm font-semibold">Points history</h3>
            {detail.data.loyalty.length === 0 ? <p className="text-sm text-slate-500">No points activity yet.</p> : (
              <Table head={['When', 'Type', 'Points', 'Balance', 'Notes']}>
                {detail.data.loyalty.map((t) => (
                  <tr key={t.id}>
                    <Td>{dateTime(t.createdAt)}</Td>
                    <Td><Badge tone={t.type === 'Earn' ? 'green' : t.type === 'Redeem' ? 'amber' : 'blue'}>{t.type}</Badge></Td>
                    <Td className={t.points < 0 ? 'text-rose-700' : 'text-emerald-700'}>{t.points > 0 ? `+${t.points}` : t.points}</Td>
                    <Td>{t.balanceAfter}</Td>
                    <Td className="max-w-xs truncate">{t.notes}</Td>
                  </tr>
                ))}
              </Table>
            )}
          </section>
        </div>
      )}
    </Modal>
    {adjusting && c && <AdjustPointsDialog customer={c} onClose={() => setAdjusting(false)} />}
  </>)
}

function AdjustPointsDialog({ customer, onClose }: { customer: Customer; onClose: () => void }) {
  const qc = useQueryClient()
  const toast = useToast()
  const [points, setPoints] = useState('')
  const [reason, setReason] = useState('')
  const value = Number(points)
  const valid = Number.isInteger(value) && value !== 0 && customer.loyaltyPoints + value >= 0 && reason.trim() !== ''
  const save = useMutation({
    mutationFn: () => api<Customer>(`/api/customers/${customer.id}/loyalty-adjustments`, { method: 'POST', body: { points: value, reason } }),
    onSuccess: (c) => {
      void qc.invalidateQueries({ queryKey: ['customer', customer.id] })
      void qc.invalidateQueries({ queryKey: ['customers'] })
      toast(`Balance is now ${c.loyaltyPoints} points`)
      onClose()
    },
  })
  return (
    <Modal open onClose={onClose} title="Adjust points" size="sm"
      footer={<>
        <Button variant="secondary" onClick={onClose}>Cancel</Button>
        <Button type="submit" form="points-form" disabled={!valid} loading={save.isPending}>Apply</Button>
      </>}>
      <form id="points-form" className="space-y-3" onSubmit={(e) => { e.preventDefault(); if (valid) save.mutate() }}>
        <p className="text-sm text-slate-600">Current balance: <strong>{customer.loyaltyPoints}</strong> points</p>
        <Input label="Points to add or remove" type="number" step={1} value={points} onChange={(e) => setPoints(e.target.value)}
          hint="Use a negative number to remove points" autoFocus />
        <Input label="Reason" required value={reason} maxLength={200} onChange={(e) => setReason(e.target.value)} />
        <ErrorBanner error={save.error} />
      </form>
    </Modal>
  )
}

function Stat({ label, value, testId }: { label: string; value: string; testId?: string }) {
  return (
    <div>
      <dt className="text-xs font-medium uppercase tracking-wide text-slate-500">{label}</dt>
      <dd className="mt-1 text-lg font-semibold text-slate-900" data-testid={testId}>{value}</dd>
    </div>
  )
}
