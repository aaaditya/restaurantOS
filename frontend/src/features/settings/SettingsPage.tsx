import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { useState } from 'react'
import { api } from '../../app/api'
import { useOutletId, useSession } from '../../app/session'
import { Button, Card, Checkbox, ErrorBanner, Input, Modal, PageHeader, Select, Spinner, useToast } from '../../components/ui'
import type { Outlet } from '../../types'

export function SettingsPage() {
  const outletId = useOutletId()
  const { canTenant } = useSession()
  const outlet = useQuery({ queryKey: ['outlet', outletId], queryFn: () => api<Outlet>(`/api/outlets/${outletId}`) })
  const [creating, setCreating] = useState(false)

  return (
    <div className="space-y-6">
      <PageHeader title="Settings" description="Outlet billing, tax and ordering settings."
        actions={canTenant('OutletManage') && <Button variant="secondary" onClick={() => setCreating(true)}>Add outlet</Button>} />
      <ErrorBanner error={outlet.error} />
      {outlet.isLoading ? <Spinner /> : outlet.data && <OutletForm key={outlet.data.id} outlet={outlet.data} />}
      <PasswordCard />
      {creating && <CreateOutletDialog onClose={() => setCreating(false)} />}
    </div>
  )
}

function OutletForm({ outlet }: { outlet: Outlet }) {
  const qc = useQueryClient()
  const toast = useToast()
  const [form, setForm] = useState({
    name: outlet.name, address: outlet.address ?? '', phone: outlet.phone ?? '', gstin: outlet.gstin ?? '',
    invoicePrefix: outlet.invoicePrefix, pricesIncludeTax: outlet.pricesIncludeTax, rounding: outlet.rounding,
    loyaltyPointsPer100: outlet.loyaltyPointsPer100.toString(), loyaltyPointValue: outlet.loyaltyPointValue.toString(),
    qrOrderingEnabled: outlet.qrOrderingEnabled,
  })
  const set = <K extends keyof typeof form>(k: K, v: (typeof form)[K]) => setForm((f) => ({ ...f, [k]: v }))
  const save = useMutation({
    mutationFn: () => api<Outlet>(`/api/outlets/${outlet.id}`, {
      method: 'PUT',
      body: {
        ...form, address: form.address || null, phone: form.phone || null, gstin: form.gstin || null,
        loyaltyPointsPer100: Number(form.loyaltyPointsPer100), loyaltyPointValue: Number(form.loyaltyPointValue),
      },
    }),
    onSuccess: (o) => {
      qc.setQueryData(['outlet', o.id], o)
      void qc.invalidateQueries({ queryKey: ['me'] })
      toast('Settings saved')
    },
  })

  return (
    <form onSubmit={(e) => { e.preventDefault(); save.mutate() }} className="space-y-6">
      <Card title="Outlet details">
        <div className="grid gap-4 p-4 sm:grid-cols-2">
          <Input label="Outlet name" required value={form.name} onChange={(e) => set('name', e.target.value)} />
          <Input label="Phone" value={form.phone} maxLength={20} onChange={(e) => set('phone', e.target.value)} />
          <Input label="Address" value={form.address} onChange={(e) => set('address', e.target.value)} className="sm:col-span-2" />
          <Input label="GSTIN" value={form.gstin} maxLength={20} onChange={(e) => set('gstin', e.target.value)} hint="Printed on invoices." />
          <Input label="Outlet code" value={outlet.code} readOnly disabled hint="Used for external integrations." />
        </div>
      </Card>
      <Card title="Billing and tax">
        <div className="grid gap-4 p-4 sm:grid-cols-2">
          <Input label="Invoice prefix" required pattern="[A-Za-z0-9\-]+" maxLength={20} value={form.invoicePrefix}
            onChange={(e) => set('invoicePrefix', e.target.value)} hint="Invoices are numbered PREFIX/FY/000001, restarting each financial year (April–March)." />
          <Select label="Grand total rounding" value={form.rounding} onChange={(e) => set('rounding', e.target.value as Outlet['rounding'])}>
            <option value="NearestRupee">Round to nearest rupee</option>
            <option value="None">No rounding</option>
          </Select>
          <div className="sm:col-span-2">
            <Checkbox label="Menu prices include tax" checked={form.pricesIncludeTax} onChange={(v) => set('pricesIncludeTax', v)} />
            <p className="mt-1 text-xs text-slate-500">Applies to new orders. Existing orders keep the setting they were created with.</p>
          </div>
        </div>
      </Card>
      <Card title="Loyalty and QR ordering">
        <div className="grid gap-4 p-4 sm:grid-cols-2">
          <Input label="Points earned per ₹100 spent" type="number" min={0} step="0.01" value={form.loyaltyPointsPer100}
            onChange={(e) => set('loyaltyPointsPer100', e.target.value)} />
          <Input label="Value of one point (₹)" type="number" min={0} step="0.01" value={form.loyaltyPointValue}
            onChange={(e) => set('loyaltyPointValue', e.target.value)} />
          <div className="sm:col-span-2">
            <Checkbox label="Allow guests to order by scanning table QR codes" checked={form.qrOrderingEnabled} onChange={(v) => set('qrOrderingEnabled', v)} />
          </div>
        </div>
      </Card>
      <ErrorBanner error={save.error} />
      <div className="flex justify-end"><Button type="submit" loading={save.isPending}>Save settings</Button></div>
    </form>
  )
}

function PasswordCard() {
  const toast = useToast()
  const [current, setCurrent] = useState('')
  const [next, setNext] = useState('')
  const change = useMutation({
    mutationFn: () => api('/api/me/password', { method: 'POST', body: { currentPassword: current, newPassword: next } }),
    onSuccess: () => { setCurrent(''); setNext(''); toast('Password changed') },
  })
  return (
    <Card title="Your password">
      <form className="grid gap-4 p-4 sm:grid-cols-3 sm:items-end" onSubmit={(e) => { e.preventDefault(); change.mutate() }}>
        <Input label="Current password" type="password" required autoComplete="current-password" value={current} onChange={(e) => setCurrent(e.target.value)} />
        <Input label="New password" type="password" required minLength={8} autoComplete="new-password" value={next} onChange={(e) => setNext(e.target.value)} />
        <Button type="submit" variant="secondary" loading={change.isPending}>Change password</Button>
        <div className="sm:col-span-3"><ErrorBanner error={change.error} /></div>
      </form>
    </Card>
  )
}

function CreateOutletDialog({ onClose }: { onClose: () => void }) {
  const qc = useQueryClient()
  const toast = useToast()
  const { selectOutlet } = useSession()
  const [form, setForm] = useState({ name: '', address: '', phone: '', gstin: '' })
  const create = useMutation({
    mutationFn: () => api<Outlet>('/api/outlets', {
      method: 'POST', body: { name: form.name, address: form.address || null, phone: form.phone || null, gstin: form.gstin || null },
    }),
    onSuccess: async (o) => {
      await qc.invalidateQueries({ queryKey: ['me'] })
      selectOutlet(o.id)
      toast(`${o.name} created`)
      onClose()
    },
  })
  return (
    <Modal open onClose={onClose} title="Add outlet"
      footer={<>
        <Button variant="secondary" onClick={onClose}>Cancel</Button>
        <Button type="submit" form="outlet-form" loading={create.isPending}>Create outlet</Button>
      </>}>
      <form id="outlet-form" className="space-y-3" onSubmit={(e) => { e.preventDefault(); create.mutate() }}>
        <Input label="Outlet name" required value={form.name} onChange={(e) => setForm({ ...form, name: e.target.value })} />
        <Input label="Address" value={form.address} onChange={(e) => setForm({ ...form, address: e.target.value })} />
        <Input label="Phone" value={form.phone} onChange={(e) => setForm({ ...form, phone: e.target.value })} />
        <Input label="GSTIN" value={form.gstin} onChange={(e) => setForm({ ...form, gstin: e.target.value })} />
        <p className="text-xs text-slate-500">The menu is shared across outlets. Tables, cash sessions and stock are per outlet.</p>
        <ErrorBanner error={create.error} />
      </form>
    </Modal>
  )
}
