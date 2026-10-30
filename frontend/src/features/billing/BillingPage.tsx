import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import clsx from 'clsx'
import { Check, Minus } from 'lucide-react'
import { useState } from 'react'
import { api } from '../../app/api'
import { Badge, Button, Card, ErrorBanner, Modal, PageHeader, Spinner, useToast } from '../../components/ui'
import { money } from '../../lib/format'
import type { BillingView, Plan } from '../../types'

const KEY = ['billing'] as const
const statusTone = { Trialing: 'amber', Active: 'green', PastDue: 'red', Cancelled: 'red' } as const
const statusLabel = { Trialing: 'Free trial', Active: 'Active', PastDue: 'Payment overdue', Cancelled: 'Cancelled' } as const
const longDate = (iso: string | null) => (iso ? new Date(iso).toLocaleDateString('en-IN', { day: 'numeric', month: 'long', year: 'numeric' }) : '—')

export function BillingPage() {
  const qc = useQueryClient()
  const toast = useToast()
  const billing = useQuery({ queryKey: KEY, queryFn: () => api<BillingView>('/api/billing') })
  const [choosing, setChoosing] = useState<Plan | null>(null)
  const [cancelling, setCancelling] = useState(false)

  const refresh = (data: BillingView) => {
    qc.setQueryData(KEY, data)
    void qc.invalidateQueries({ queryKey: ['me'] })
  }
  const subscribe = useMutation({
    mutationFn: (planCode: string) => api<BillingView>('/api/billing/subscription', { method: 'POST', body: { planCode } }),
    onSuccess: (data) => {
      refresh(data)
      setChoosing(null)
      toast(`Subscribed to ${data.plans.find((p) => p.code === data.current.planCode)?.name ?? 'plan'}`)
    },
  })
  const cancel = useMutation({
    mutationFn: () => api<BillingView>('/api/billing/cancel', { method: 'POST' }),
    onSuccess: (data) => {
      refresh(data)
      setCancelling(false)
      toast('Subscription cancelled')
    },
  })

  if (billing.isLoading) return <Spinner />
  if (!billing.data) return <ErrorBanner error={billing.error} />
  const { current, usage, plans, provider } = billing.data
  const plan = plans.find((p) => p.code === current.planCode)
  const subscribed = current.status === 'Active'

  return (
    <div className="space-y-6">
      <PageHeader title="Billing" description="Your RestaurantOS subscription, plan limits and usage." />
      {provider === 'mock' && (
        <p className="rounded-lg bg-amber-50 px-4 py-3 text-sm text-amber-800" role="note">
          Test billing mode: plan changes take effect immediately and no card is charged. A real billing provider is connected before launch.
        </p>
      )}

      <Card title="Current subscription">
        <dl className="grid gap-4 p-4 sm:grid-cols-4">
          <div>
            <dt className="text-sm text-slate-500">Plan</dt>
            <dd className="mt-1 text-lg font-semibold" data-testid="current-plan">{plan?.name ?? current.planCode}</dd>
          </div>
          <div>
            <dt className="text-sm text-slate-500">Status</dt>
            <dd className="mt-1"><Badge tone={statusTone[current.status]}>{statusLabel[current.status]}</Badge></dd>
          </div>
          <div>
            <dt className="text-sm text-slate-500">{current.status === 'Trialing' ? 'Trial ends' : 'Current period ends'}</dt>
            <dd className="mt-1 text-sm font-medium">{longDate(current.status === 'Trialing' ? current.trialEndsAt : current.currentPeriodEndsAt)}</dd>
          </div>
          <div>
            <dt className="text-sm text-slate-500">Usage</dt>
            <dd className="mt-1 text-sm font-medium" data-testid="usage">
              {usage.outlets}{plan ? ` / ${plan.maxOutlets}` : ''} outlets · {usage.staff}{plan ? ` / ${plan.maxStaff}` : ''} staff
            </dd>
          </div>
        </dl>
        {!current.usable && (
          <p className="border-t border-slate-100 px-4 py-3 text-sm text-rose-700">The workspace is read-only. Choose a plan below to restore access.</p>
        )}
        {subscribed && (
          <div className="border-t border-slate-100 px-4 py-3">
            <Button variant="ghost" size="sm" onClick={() => setCancelling(true)}>Cancel subscription</Button>
          </div>
        )}
      </Card>

      <div className="grid gap-4 md:grid-cols-3">
        {plans.map((p) => {
          const isCurrent = p.code === current.planCode && subscribed
          const tooSmall = usage.outlets > p.maxOutlets || usage.staff > p.maxStaff
          return (
            <section key={p.code} aria-label={`${p.name} plan`}
              className={clsx('flex flex-col rounded-xl bg-white p-5 shadow-sm ring-1', isCurrent ? 'ring-2 ring-brand-600' : 'ring-slate-200')}>
              <div className="flex items-center justify-between">
                <h2 className="text-lg font-semibold">{p.name}</h2>
                {isCurrent && <Badge tone="teal">Current plan</Badge>}
              </div>
              <p className="mt-2"><span className="text-3xl font-semibold">{money(p.monthlyPrice)}</span><span className="text-sm text-slate-500"> / month + GST</span></p>
              <ul className="mt-4 flex-1 space-y-2 text-sm">
                <Feature ok>Up to {p.maxOutlets} {p.maxOutlets === 1 ? 'outlet' : 'outlets'}</Feature>
                <Feature ok>Up to {p.maxStaff} staff accounts</Feature>
                <Feature ok>POS, kitchen display, QR ordering, reports</Feature>
                <Feature ok={p.inventory}>Inventory, recipes and purchasing</Feature>
                <Feature ok={p.integrations}>API keys and delivery partner integrations</Feature>
              </ul>
              <Button className="mt-5" variant={isCurrent ? 'secondary' : 'primary'} disabled={isCurrent || tooSmall}
                onClick={() => setChoosing(p)}>
                {isCurrent ? 'Current plan' : tooSmall ? 'Exceeds plan limits' : p.code === current.planCode ? `Subscribe to ${p.name}` : `Switch to ${p.name}`}
              </Button>
            </section>
          )
        })}
      </div>

      {choosing && (
        <Modal open onClose={() => setChoosing(null)} title={`Subscribe to ${choosing.name}`} size="sm"
          footer={<>
            <Button variant="secondary" onClick={() => setChoosing(null)}>Back</Button>
            <Button loading={subscribe.isPending} onClick={() => subscribe.mutate(choosing.code)}>Confirm</Button>
          </>}>
          <div className="space-y-3 text-sm">
            <ErrorBanner error={subscribe.error} />
            <p>You will be billed {money(choosing.monthlyPrice)} per month plus GST, starting today.</p>
            {provider === 'mock' && <p className="text-slate-500">Test billing mode: no payment is collected.</p>}
          </div>
        </Modal>
      )}
      {cancelling && (
        <Modal open onClose={() => setCancelling(false)} title="Cancel subscription?" size="sm"
          footer={<>
            <Button variant="secondary" onClick={() => setCancelling(false)}>Keep subscription</Button>
            <Button variant="danger" loading={cancel.isPending} onClick={() => cancel.mutate()}>Cancel subscription</Button>
          </>}>
          <div className="space-y-3 text-sm">
            <ErrorBanner error={cancel.error} />
            <p>The workspace becomes read-only straight away: staff can view data but cannot take orders or make changes until you subscribe again. Your data is kept.</p>
          </div>
        </Modal>
      )}
    </div>
  )
}

function Feature({ ok, children }: { ok: boolean; children: React.ReactNode }) {
  return (
    <li className={clsx('flex items-start gap-2', !ok && 'text-slate-400')}>
      {ok ? <Check className="mt-0.5 size-4 text-emerald-600" aria-hidden /> : <Minus className="mt-0.5 size-4" aria-hidden />}
      <span>{children}{!ok && <span className="sr-only"> (not included)</span>}</span>
    </li>
  )
}
