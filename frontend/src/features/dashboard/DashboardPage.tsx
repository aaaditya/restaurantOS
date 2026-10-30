import { useQuery } from '@tanstack/react-query'
import { AlertTriangle } from 'lucide-react'
import { Link } from 'react-router'
import { api } from '../../app/api'
import { useSession } from '../../app/session'
import { Badge, Card, EmptyState, ErrorBanner, PageHeader, Spinner, Table, Td } from '../../components/ui'
import { money, qty } from '../../lib/format'
import type { Dashboard } from '../../types'

const weekday = (iso: string) => new Date(`${iso}T00:00:00`).toLocaleDateString('en-IN', { weekday: 'short', day: 'numeric' })

export function DashboardPage() {
  const { selectOutlet } = useSession()
  const dashboard = useQuery({ queryKey: ['dashboard'], queryFn: () => api<Dashboard>('/api/dashboard'), refetchInterval: 60_000 })
  const d = dashboard.data
  const maxTrend = Math.max(...(d?.trend.map((t) => t.totalCollected) ?? [0]), 0)

  return (
    <div className="space-y-6">
      <PageHeader title="Dashboard" description="Today across your outlets, from settled bills. Refreshes every minute." />
      <ErrorBanner error={dashboard.error} />
      {!d ? (dashboard.error ? null : <Spinner />) : (
        <>
          <div className="grid gap-4 sm:grid-cols-2 xl:grid-cols-4">
            <Kpi label="Collected today" value={money(d.totals.totalCollected)} testId="dash-collected" />
            <Kpi label="Net sales today" value={money(d.totals.netSales)} />
            <Kpi label="Bills today" value={String(d.totals.orders)} testId="dash-orders" />
            <Kpi label="Average bill" value={money(d.totals.averageOrderValue)} />
          </div>

          <div className="grid gap-6 xl:grid-cols-[2fr_1fr]">
            <Card title="Outlets">
              <Table head={['Outlet', 'Bills', 'Collected', 'Running', 'Attention']}>
                {d.outlets.map((o) => {
                  const alerts = [
                    o.pendingQrOrders > 0 && <Badge key="qr" tone="amber">{o.pendingQrOrders} QR awaiting</Badge>,
                    o.lowStockItems > 0 && <Badge key="stock" tone="red">{o.lowStockItems} low stock</Badge>,
                    !o.cashSessionOpen && o.orders > 0 && <Badge key="cash" tone="slate">No cash session</Badge>,
                  ].filter(Boolean)
                  return (
                    <tr key={o.outletId}>
                      <Td className="font-medium text-slate-900">
                        <Link to="/reports" className="hover:underline" onClick={() => selectOutlet(o.outletId)}>{o.name}</Link>
                      </Td>
                      <Td>{o.orders}</Td>
                      <Td>{money(o.totalCollected)}</Td>
                      <Td>{o.runningOrders}</Td>
                      <Td><div className="flex flex-wrap gap-1">{alerts.length ? alerts : <span className="text-slate-500">All clear</span>}</div></Td>
                    </tr>
                  )
                })}
              </Table>
            </Card>
            <Card title="Top items today">
              {d.topItems.length === 0 ? <EmptyState title="No items sold yet today" /> : (
                <ol className="divide-y divide-slate-100">
                  {d.topItems.map((i, n) => (
                    <li key={i.menuItemId} className="flex items-center justify-between gap-3 px-4 py-2.5 text-sm">
                      <span><span className="mr-2 text-slate-400">{n + 1}.</span>{i.name}</span>
                      <span className="text-slate-600">{qty(i.quantity)} · <span className="font-medium text-slate-900">{money(i.total)}</span></span>
                    </li>
                  ))}
                </ol>
              )}
            </Card>
          </div>

          <Card title="Last 7 days">
            {maxTrend === 0 ? <p className="p-4 text-sm text-slate-500">No settled bills in the last 7 days.</p> : (
              <ul className="flex h-48 items-end gap-3 p-4" aria-label="Collections over the last 7 days">
                {d.trend.map((t) => (
                  <li key={t.date} className="flex h-full flex-1 flex-col items-center justify-end gap-1">
                    <span className="text-xs text-slate-600">{t.totalCollected > 0 ? money(t.totalCollected).replace('.00', '') : ''}</span>
                    <span className="w-full max-w-12 rounded-t bg-brand-600" style={{ height: `${Math.max((t.totalCollected / maxTrend) * 100, t.totalCollected > 0 ? 2 : 0)}%` }}
                      title={`${t.date}: ${money(t.totalCollected)}`} />
                    <span className="text-xs text-slate-500">{weekday(t.date)}</span>
                  </li>
                ))}
              </ul>
            )}
          </Card>
          {d.outlets.some((o) => o.lowStockItems > 0) && (
            <p className="flex items-center gap-2 text-sm text-amber-800">
              <AlertTriangle className="size-4" aria-hidden /> Some ingredients are at or below their reorder level. <Link to="/inventory" className="underline">Review stock</Link>
            </p>
          )}
        </>
      )}
    </div>
  )
}

function Kpi({ label, value, testId }: { label: string; value: string; testId?: string }) {
  return (
    <div className="rounded-xl bg-white p-4 shadow-sm ring-1 ring-slate-200">
      <p className="text-sm text-slate-500">{label}</p>
      <p className="mt-1 text-2xl font-semibold text-slate-900" data-testid={testId}>{value}</p>
    </div>
  )
}
