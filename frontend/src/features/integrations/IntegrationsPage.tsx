import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { Copy, KeyRound, Plus, Send } from 'lucide-react'
import { useState } from 'react'
import { Link } from 'react-router'
import { api } from '../../app/api'
import { useSession } from '../../app/session'
import { Badge, Button, Card, Checkbox, EmptyState, ErrorBanner, Input, Modal, PageHeader, Select, Spinner, Table, Td, useToast } from '../../components/ui'
import { dateTime, money } from '../../lib/format'
import type { ApiKey, ExternalOrderResult, IntegrationConfig } from '../../types'

const KEYS = ['api-keys'] as const
const scopeLabel: Record<string, string> = {
  'orders:write': 'Push orders',
  'menu:read': 'Read menu',
  'reports:read': 'Read reports',
}

export function IntegrationsPage() {
  const { me, outlet, can, canTenant } = useSession()
  const config = useQuery({ queryKey: ['integrations-config'], queryFn: () => api<IntegrationConfig>('/api/integrations/config'), staleTime: 300_000 })
  const keys = useQuery({ queryKey: KEYS, queryFn: () => api<ApiKey[]>('/api/integrations/api-keys') })
  const [creating, setCreating] = useState(false)
  const [revealed, setRevealed] = useState<string | null>(null)
  const [revoking, setRevoking] = useState<ApiKey | null>(null)
  const planAllows = me?.tenant.planCode !== 'starter'
  const outletName = (id: string | null) => (id ? me?.outlets.find((o) => o.id === id)?.name ?? 'Unknown outlet' : 'All outlets')

  return (
    <div className="space-y-6">
      <PageHeader title="Integrations" description="Connect delivery partners and other systems through the RestaurantOS order API."
        actions={planAllows && <Button onClick={() => setCreating(true)}><Plus className="size-4" aria-hidden /> Create API key</Button>} />

      {!planAllows && (
        <p className="rounded-lg bg-amber-50 px-4 py-3 text-sm text-amber-800" role="note">
          API keys and delivery partner integrations are available on the Growth and Scale plans.
          {canTenant('BillingManage') && <> <Link to="/billing" className="font-medium underline">Change plan</Link></>}
        </p>
      )}

      <Card title="API keys">
        <ErrorBanner error={keys.error} />
        {keys.isLoading ? <Spinner /> : !keys.data?.length ? (
          <EmptyState title="No API keys" description="Create a key for each partner so access can be revoked independently." />
        ) : (
          <Table head={['Name', 'Key', 'Outlet', 'Access', 'Last used', 'Status', '']}>
            {keys.data.map((k) => (
              <tr key={k.id}>
                <Td className="font-medium text-slate-900">{k.name}</Td>
                <Td><code className="text-xs">{k.prefix}.••••</code></Td>
                <Td>{outletName(k.outletId)}</Td>
                <Td><div className="flex flex-wrap gap-1">{k.scopes.map((s) => <Badge key={s}>{scopeLabel[s] ?? s}</Badge>)}</div></Td>
                <Td>{k.lastUsedAt ? dateTime(k.lastUsedAt) : 'Never'}</Td>
                <Td><Badge tone={k.revokedAt ? 'red' : 'green'}>{k.revokedAt ? 'Revoked' : 'Active'}</Badge></Td>
                <Td className="text-right">
                  {!k.revokedAt && <Button variant="ghost" size="sm" onClick={() => setRevoking(k)} aria-label={`Revoke ${k.name}`}>Revoke</Button>}
                </Td>
              </tr>
            ))}
          </Table>
        )}
      </Card>

      {config.data?.mockSimulators && outlet && can('OrderCreate') && <SimulatorCard outletId={outlet.id} outletName={outlet.name} />}

      {outlet && <ApiReference outletId={outlet.id} />}

      {creating && config.data && (
        <CreateKeyDialog scopes={config.data.scopes} onClose={() => setCreating(false)}
          onCreated={(key) => { setCreating(false); setRevealed(key) }} />
      )}
      {revealed && <RevealKeyDialog value={revealed} onClose={() => setRevealed(null)} />}
      {revoking && <RevokeDialog apiKey={revoking} onClose={() => setRevoking(null)} />}
    </div>
  )
}

function CreateKeyDialog({ scopes, onClose, onCreated }: { scopes: string[]; onClose: () => void; onCreated: (key: string) => void }) {
  const { me } = useSession()
  const qc = useQueryClient()
  const [name, setName] = useState('')
  const [outletId, setOutletId] = useState('')
  const [selected, setSelected] = useState<string[]>(['orders:write', 'menu:read'])
  const create = useMutation({
    mutationFn: () => api<{ apiKey: ApiKey; key: string }>('/api/integrations/api-keys', {
      method: 'POST', body: { name: name.trim(), outletId: outletId || null, scopes: selected },
    }),
    onSuccess: (r) => {
      void qc.invalidateQueries({ queryKey: KEYS })
      onCreated(r.key)
    },
  })
  const toggle = (s: string, on: boolean) => setSelected((cur) => (on ? [...cur, s] : cur.filter((x) => x !== s)))

  return (
    <Modal open onClose={onClose} title="Create API key" size="md"
      footer={<>
        <Button variant="secondary" onClick={onClose}>Cancel</Button>
        <Button loading={create.isPending} disabled={!name.trim() || selected.length === 0} onClick={() => create.mutate()}>Create key</Button>
      </>}>
      <form className="space-y-4" onSubmit={(e) => { e.preventDefault(); if (name.trim() && selected.length) create.mutate() }}>
        <ErrorBanner error={create.error} />
        <Input label="Name" hint="For example the partner's name, so you can tell keys apart." value={name} maxLength={80}
          onChange={(e) => setName(e.target.value)} autoFocus />
        <Select label="Outlet" value={outletId} onChange={(e) => setOutletId(e.target.value)}>
          <option value="">All outlets</option>
          {me?.outlets.map((o) => <option key={o.id} value={o.id}>{o.name}</option>)}
        </Select>
        <fieldset className="space-y-2">
          <legend className="mb-1 text-sm font-medium text-slate-700">Access</legend>
          {scopes.map((s) => <Checkbox key={s} label={`${scopeLabel[s] ?? s} (${s})`} checked={selected.includes(s)} onChange={(v) => toggle(s, v)} />)}
        </fieldset>
      </form>
    </Modal>
  )
}

function RevealKeyDialog({ value, onClose }: { value: string; onClose: () => void }) {
  const [copied, setCopied] = useState(false)
  return (
    <Modal open onClose={onClose} title="Your new API key" size="md" footer={<Button onClick={onClose}>Done</Button>}>
      <div className="space-y-3 text-sm">
        <p className="rounded-lg bg-amber-50 px-3 py-2 text-amber-800">Copy this key now. For security it is stored hashed and will not be shown again.</p>
        <div className="flex gap-2">
          <input readOnly value={value} aria-label="API key" data-testid="new-api-key" onFocus={(e) => e.currentTarget.select()}
            className="min-w-0 flex-1 rounded-lg border-slate-300 font-mono text-xs" />
          {navigator.clipboard && (
            <Button variant="secondary" onClick={() => void navigator.clipboard.writeText(value).then(() => setCopied(true))}>
              <Copy className="size-4" aria-hidden /> {copied ? 'Copied' : 'Copy'}
            </Button>
          )}
        </div>
        <p className="text-slate-500">Send it in the <code>X-Api-Key</code> header of every request.</p>
      </div>
    </Modal>
  )
}

function RevokeDialog({ apiKey, onClose }: { apiKey: ApiKey; onClose: () => void }) {
  const qc = useQueryClient()
  const toast = useToast()
  const revoke = useMutation({
    mutationFn: () => api<ApiKey>(`/api/integrations/api-keys/${apiKey.id}/revoke`, { method: 'POST' }),
    onSuccess: () => {
      void qc.invalidateQueries({ queryKey: KEYS })
      toast(`Revoked ${apiKey.name}`)
      onClose()
    },
  })
  return (
    <Modal open onClose={onClose} title={`Revoke ${apiKey.name}?`} size="sm"
      footer={<>
        <Button variant="secondary" onClick={onClose}>Keep key</Button>
        <Button variant="danger" loading={revoke.isPending} onClick={() => revoke.mutate()}>Revoke</Button>
      </>}>
      <ErrorBanner error={revoke.error} />
      <p className="text-sm">Requests using this key will be rejected immediately. This cannot be undone.</p>
    </Modal>
  )
}

function SimulatorCard({ outletId, outletName }: { outletId: string; outletName: string }) {
  const toast = useToast()
  const [last, setLast] = useState<ExternalOrderResult | null>(null)
  const send = useMutation({
    mutationFn: () => api<ExternalOrderResult>(`/api/dev/mock-aggregator/outlets/${outletId}/orders`, { method: 'POST' }),
    onSuccess: (r) => {
      setLast(r)
      toast(`Test order #${r.orderNumber} sent to ${outletName}`)
    },
  })
  return (
    <Card title="Test delivery partner" actions={<Badge tone="amber">Test mode</Badge>}>
      <div className="space-y-3 p-4 text-sm">
        <p className="text-slate-600">
          Sends a prepaid delivery order from a simulated partner to {outletName} through the same pipeline a real integration uses.
          It appears on the POS under “Awaiting acceptance”.
        </p>
        <ErrorBanner error={send.error} />
        <div className="flex flex-wrap items-center gap-3">
          <Button variant="secondary" loading={send.isPending} onClick={() => send.mutate()}><Send className="size-4" aria-hidden /> Send test order</Button>
          {last && (
            <span data-testid="simulated-order" className="flex flex-wrap items-center gap-x-4 gap-y-1">
              <span className="font-medium">Order #{last.orderNumber}</span>
              <span className="text-slate-500">Partner ref {last.externalId}</span>
              <span>{money(last.grandTotal)}</span>
              <Link to="/pos" className="font-medium text-brand-700 underline">Open POS</Link>
            </span>
          )}
        </div>
      </div>
    </Card>
  )
}

function ApiReference({ outletId }: { outletId: string }) {
  const base = `${window.location.origin}/api/v1/outlets/${outletId}`
  const example = `curl -X POST ${base}/orders \\
  -H "X-Api-Key: ros_xxxxxxxxxx.<secret>" \\
  -H "Content-Type: application/json" \\
  -d '{
    "provider": "my-delivery-app",
    "externalId": "A-10042",
    "type": "Delivery",
    "prepaid": true,
    "customer": { "name": "Asha", "phone": "9876543210", "address": "12 MG Road" },
    "lines": [{ "menuItemId": "<item id from GET /menu>", "quantity": 2 }]
  }'`
  return (
    <Card title="Order API" actions={<KeyRound className="size-4 text-slate-400" aria-hidden />}>
      <div className="space-y-3 p-4 text-sm text-slate-600">
        <ul className="list-disc space-y-1 pl-5">
          <li><code>GET /api/v1/outlets/{'{outletId}'}/menu</code> returns available items, variants and modifiers (<code>menu:read</code>).</li>
          <li><code>POST /api/v1/outlets/{'{outletId}'}/orders</code> creates an order awaiting acceptance (<code>orders:write</code>). Resending the same <code>provider</code> + <code>externalId</code> returns the original order instead of a duplicate.</li>
          <li><code>GET /api/v1/outlets/{'{outletId}'}/orders/{'{provider}'}/{'{externalId}'}</code> returns its current state (<code>orders:write</code>).</li>
        </ul>
        <p>Prices are always taken from your menu. If the partner sends <code>expectedTotal</code>, the response reports whether it matches.</p>
        <pre className="overflow-x-auto rounded-lg bg-slate-900 p-3 text-xs text-slate-100"><code>{example}</code></pre>
      </div>
    </Card>
  )
}
