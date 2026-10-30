import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import clsx from 'clsx'
import { useEffect, useState } from 'react'
import { api, errorMessage } from '../../app/api'
import { useOutletRealtime } from '../../app/realtime'
import { useOutletId, useSession } from '../../app/session'
import { Button, EmptyState, ErrorBanner, PageHeader, Spinner, useToast } from '../../components/ui'
import { minutesSince, qty, time } from '../../lib/format'
import type { Kot, KotStatus } from '../../types'
import { LiveIndicator } from '../pos/PosHome'

const COLUMNS: { status: KotStatus; title: string }[] = [
  { status: 'New', title: 'New' },
  { status: 'Preparing', title: 'Preparing' },
  { status: 'Ready', title: 'Ready to serve' },
]

export function KitchenPage() {
  const outletId = useOutletId()
  const { can } = useSession()
  const toast = useToast()
  const qc = useQueryClient()
  const [station, setStation] = useState('')
  const [now, setNow] = useState(() => Date.now())
  useEffect(() => {
    const t = setInterval(() => setNow(Date.now()), 30_000)
    return () => clearInterval(t)
  }, [])

  const key = ['kots', outletId, station]
  const kots = useQuery({
    queryKey: key,
    queryFn: () => api<Kot[]>(`/api/outlets/${outletId}/kots${station ? `?station=${encodeURIComponent(station)}` : ''}`),
    refetchInterval: 20_000,
  })
  const stations = useQuery({ queryKey: ['stations', outletId], queryFn: () => api<string[]>(`/api/outlets/${outletId}/kots/stations`) })
  const live = useOutletRealtime(outletId, { kotChanged: [['kots', outletId]] })

  const move = useMutation({
    mutationFn: (v: { kot: Kot; status: KotStatus }) => api<Kot>(`/api/outlets/${outletId}/kots/${v.kot.id}/status`, { method: 'POST', body: { status: v.status } }),
    onSuccess: () => qc.invalidateQueries({ queryKey: ['kots', outletId] }),
    onError: (e) => toast(errorMessage(e), 'error'),
  })

  const canUpdate = can('KitchenUpdate')
  const list = kots.data ?? []
  const cancellations = list.filter((k) => k.kind === 'Cancellation')

  return (
    <div>
      <PageHeader title="Kitchen display" description="Tickets update live as orders are sent from the POS."
        actions={<>
          <LiveIndicator state={live} />
          <label className="text-sm">
            <span className="sr-only">Station</span>
            <select value={station} onChange={(e) => setStation(e.target.value)} className="rounded-lg border-slate-300 text-sm" aria-label="Station filter">
              <option value="">All stations</option>
              {stations.data?.map((s) => <option key={s} value={s}>{s}</option>)}
            </select>
          </label>
        </>} />
      <ErrorBanner error={kots.error} />
      {kots.isLoading && <Spinner />}

      {cancellations.length > 0 && (
        <section aria-label="Cancelled items" className="mb-6 grid gap-3 sm:grid-cols-2 xl:grid-cols-4">
          {cancellations.map((k) => (
            <article key={k.id} className="rounded-xl border-2 border-rose-400 bg-rose-50 p-3">
              <header className="flex justify-between text-sm font-semibold text-rose-800">
                <span>STOP · {k.kotNumber} · #{k.orderNumber}</span><span>{k.station}</span>
              </header>
              <ul className="mt-2 text-sm">{k.items.map((i) => <li key={i.id}><strong>{qty(i.quantity)} ×</strong> {i.name}{i.variantName ? ` (${i.variantName})` : ''}</li>)}</ul>
              {canUpdate && <Button size="sm" variant="danger" className="mt-3 w-full" onClick={() => move.mutate({ kot: k, status: 'Served' })}>Acknowledge</Button>}
            </article>
          ))}
        </section>
      )}

      <div className="grid gap-4 lg:grid-cols-3">
        {COLUMNS.map((col) => {
          const items = list.filter((k) => k.kind === 'Normal' && k.status === col.status)
          return (
            <section key={col.status} aria-label={col.title} className="rounded-xl bg-slate-100 p-3">
              <h2 className="mb-3 flex items-center justify-between text-base font-semibold text-slate-800">
                {col.title} <span className="rounded-full bg-white px-2 py-0.5 text-xs">{items.length}</span>
              </h2>
              <div className="space-y-3">
                {items.length === 0 && <EmptyState title="No tickets" />}
                {items.map((k) => {
                  const age = minutesSince(k.createdAt, now)
                  return (
                    <article key={k.id} data-testid="kot-card" className={clsx('rounded-xl bg-white p-3 shadow-sm ring-2',
                      age >= 20 ? 'ring-rose-400' : age >= 10 ? 'ring-amber-300' : 'ring-transparent')}>
                      <header className="flex items-start justify-between">
                        <div>
                          <p className="font-semibold">{k.kotNumber} · #{k.orderNumber}</p>
                          <p className="text-xs text-slate-500">{k.orderTypeLabel}{k.tableName ? ` · Table ${k.tableName}` : ''} · {k.station}</p>
                        </div>
                        <span className={clsx('text-sm font-medium', age >= 20 ? 'text-rose-700' : 'text-slate-600')}>{age}m · {time(k.createdAt)}</span>
                      </header>
                      <ul className="mt-2 space-y-1">
                        {k.items.map((i) => (
                          <li key={i.id} className="text-sm">
                            <strong>{qty(i.quantity)} ×</strong> {i.name}{i.variantName ? ` (${i.variantName})` : ''}
                            {i.modifiersText && <div className="pl-5 text-xs text-slate-600">{i.modifiersText}</div>}
                            {i.notes && <div className="pl-5 text-xs italic text-amber-700">{i.notes}</div>}
                          </li>
                        ))}
                      </ul>
                      {canUpdate && (
                        <div className="mt-3 flex gap-2">
                          {k.status === 'New' && <Button size="sm" className="flex-1" onClick={() => move.mutate({ kot: k, status: 'Preparing' })}>Start</Button>}
                          {(k.status === 'New' || k.status === 'Preparing') && (
                            <Button size="sm" variant="success" className="flex-1" onClick={() => move.mutate({ kot: k, status: 'Ready' })}>Ready</Button>
                          )}
                          {k.status === 'Ready' && <Button size="sm" variant="secondary" className="flex-1" onClick={() => move.mutate({ kot: k, status: 'Served' })}>Served</Button>}
                        </div>
                      )}
                    </article>
                  )
                })}
              </div>
            </section>
          )
        })}
      </div>
    </div>
  )
}
