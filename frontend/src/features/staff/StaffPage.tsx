import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { KeyRound, Pencil, Plus, Trash2 } from 'lucide-react'
import { useState } from 'react'
import { api } from '../../app/api'
import { useSession } from '../../app/session'
import { Badge, Button, Card, Checkbox, EmptyState, ErrorBanner, Input, Modal, PageHeader, Select, Spinner, Table, Td, useToast } from '../../components/ui'
import { dateTime } from '../../lib/format'
import { ROLES, type Role, type Staff } from '../../types'

const STAFF_KEY = ['staff'] as const
const TENANT_WIDE_ONLY: Role[] = ['Owner']
type Assignment = { role: Role; outletId: string | null }

export function StaffPage() {
  const staff = useQuery({ queryKey: STAFF_KEY, queryFn: () => api<Staff[]>('/api/staff') })
  const { me } = useSession()
  const [editing, setEditing] = useState<Staff | 'new' | null>(null)
  const [resetting, setResetting] = useState<Staff | null>(null)
  const outletName = (id: string | null) => (id ? me?.outlets.find((o) => o.id === id)?.name ?? 'Unknown outlet' : 'All outlets')

  return (
    <div>
      <PageHeader title="Staff" description="Accounts, roles and outlet access. Roles assigned to “All outlets” apply tenant-wide."
        actions={<Button onClick={() => setEditing('new')}><Plus className="size-4" aria-hidden /> Add staff</Button>} />
      <Card>
        <ErrorBanner error={staff.error} />
        {staff.isLoading ? <Spinner /> : !staff.data?.length ? <EmptyState title="No staff" /> : (
          <Table head={['Name', 'Email', 'Roles', 'Last sign-in', 'Status', '']}>
            {staff.data.map((s) => (
              <tr key={s.id}>
                <Td className="font-medium text-slate-900">{s.name}{s.id === me?.user.id && <span className="ml-1 text-xs text-slate-500">(you)</span>}</Td>
                <Td>{s.email}</Td>
                <Td>
                  <div className="flex flex-wrap gap-1">
                    {s.roles.map((r, i) => <Badge key={i} tone={r.role === 'Owner' ? 'teal' : 'slate'}>{r.role} · {outletName(r.outletId)}</Badge>)}
                  </div>
                </Td>
                <Td>{s.lastLoginAt ? dateTime(s.lastLoginAt) : 'Never'}</Td>
                <Td><Badge tone={s.isActive ? 'green' : 'red'}>{s.isActive ? 'Active' : 'Deactivated'}</Badge></Td>
                <Td className="text-right whitespace-nowrap">
                  <Button variant="ghost" size="sm" onClick={() => setResetting(s)} aria-label={`Reset password for ${s.name}`}><KeyRound className="size-4" aria-hidden /></Button>
                  <Button variant="ghost" size="sm" onClick={() => setEditing(s)} aria-label={`Edit ${s.name}`}><Pencil className="size-4" aria-hidden /></Button>
                </Td>
              </tr>
            ))}
          </Table>
        )}
      </Card>
      {editing && <StaffEditor staff={editing === 'new' ? null : editing} onClose={() => setEditing(null)} />}
      {resetting && <ResetPasswordDialog staff={resetting} onClose={() => setResetting(null)} />}
    </div>
  )
}

function StaffEditor({ staff, onClose }: { staff: Staff | null; onClose: () => void }) {
  const { me, outletId } = useSession()
  const qc = useQueryClient()
  const toast = useToast()
  const isOwner = !!me?.tenantRoles.includes('Owner')
  const [name, setName] = useState(staff?.name ?? '')
  const [email, setEmail] = useState(staff?.email ?? '')
  const [password, setPassword] = useState('')
  const [isActive, setIsActive] = useState(staff?.isActive ?? true)
  const [roles, setRoles] = useState<Assignment[]>(staff?.roles ?? [{ role: 'Cashier', outletId }])
  const grantable = ROLES.filter((r) => r !== 'Owner' || isOwner)

  const save = useMutation({
    mutationFn: () => staff
      ? api(`/api/staff/${staff.id}`, { method: 'PUT', body: { name, isActive, roles } })
      : api('/api/staff', { method: 'POST', body: { name, email, password, roles } }),
    onSuccess: () => {
      void qc.invalidateQueries({ queryKey: STAFF_KEY })
      void qc.invalidateQueries({ queryKey: ['me'] })
      toast(staff ? 'Staff updated' : 'Staff account created')
      onClose()
    },
  })
  const update = (i: number, patch: Partial<Assignment>) => setRoles((rs) => rs.map((r, j) => {
    if (j !== i) return r
    const next = { ...r, ...patch }
    return TENANT_WIDE_ONLY.includes(next.role) ? { ...next, outletId: null } : next
  }))

  return (
    <Modal open onClose={onClose} title={staff ? `Edit ${staff.name}` : 'Add staff'}
      footer={<>
        <Button variant="secondary" onClick={onClose}>Cancel</Button>
        <Button type="submit" form="staff-form" loading={save.isPending}>Save</Button>
      </>}>
      <form id="staff-form" className="space-y-3" onSubmit={(e) => { e.preventDefault(); save.mutate() }}>
        <Input label="Name" required value={name} onChange={(e) => setName(e.target.value)} />
        {!staff && <>
          <Input label="Email" type="email" required autoComplete="off" value={email} onChange={(e) => setEmail(e.target.value)} />
          <Input label="Temporary password" type="password" required minLength={8} autoComplete="new-password" value={password}
            onChange={(e) => setPassword(e.target.value)} hint="At least 8 characters. Share it securely and ask them to change it." />
        </>}
        <fieldset className="space-y-2">
          <legend className="text-sm font-medium text-slate-700">Roles</legend>
          {roles.map((r, i) => (
            <div key={i} className="flex items-end gap-2">
              <Select label="Role" value={r.role} onChange={(e) => update(i, { role: e.target.value as Role })} className="flex-1"
                disabled={r.role === 'Owner' && !isOwner}>
                {(r.role === 'Owner' && !isOwner ? ROLES : grantable).map((role) => <option key={role} value={role}>{role}</option>)}
              </Select>
              <Select label="Outlet" value={r.outletId ?? ''} onChange={(e) => update(i, { outletId: e.target.value || null })} className="flex-1"
                disabled={TENANT_WIDE_ONLY.includes(r.role)}>
                <option value="">All outlets</option>
                {me?.outlets.map((o) => <option key={o.id} value={o.id}>{o.name}</option>)}
              </Select>
              <Button variant="ghost" aria-label="Remove role" disabled={roles.length === 1} onClick={() => setRoles((rs) => rs.filter((_, j) => j !== i))}>
                <Trash2 className="size-4" aria-hidden />
              </Button>
            </div>
          ))}
          <Button variant="secondary" size="sm" onClick={() => setRoles((rs) => [...rs, { role: 'Waiter', outletId }])}>
            <Plus className="size-4" aria-hidden /> Add role
          </Button>
        </fieldset>
        {staff && staff.id !== me?.user.id && (
          <Checkbox label="Active (deactivating signs them out everywhere)" checked={isActive} onChange={setIsActive} />
        )}
        <ErrorBanner error={save.error} />
      </form>
    </Modal>
  )
}

function ResetPasswordDialog({ staff, onClose }: { staff: Staff; onClose: () => void }) {
  const toast = useToast()
  const [password, setPassword] = useState('')
  const reset = useMutation({
    mutationFn: () => api(`/api/staff/${staff.id}/reset-password`, { method: 'POST', body: { password } }),
    onSuccess: () => { toast(`Password reset for ${staff.name}; their sessions were signed out`); onClose() },
  })
  return (
    <Modal open onClose={onClose} title={`Reset password · ${staff.name}`} size="sm"
      footer={<>
        <Button variant="secondary" onClick={onClose}>Cancel</Button>
        <Button type="submit" form="reset-form" loading={reset.isPending}>Reset password</Button>
      </>}>
      <form id="reset-form" className="space-y-3" onSubmit={(e) => { e.preventDefault(); reset.mutate() }}>
        <Input label="New password" type="password" required minLength={8} autoComplete="new-password" value={password} onChange={(e) => setPassword(e.target.value)} />
        <ErrorBanner error={reset.error} />
      </form>
    </Modal>
  )
}
