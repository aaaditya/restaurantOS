import { useQuery } from '@tanstack/react-query'
import clsx from 'clsx'
import { AlertTriangle, CheckCircle2, Download } from 'lucide-react'
import { useState, type ReactNode } from 'react'
import { api } from '../../app/api'
import { useOutletId, useSession } from '../../app/session'
import { useBusinessToday } from '../../app/useBusinessToday'
import { Badge, Button, Card, EmptyState, ErrorBanner, Input, PageHeader, Spinner, Table, Td } from '../../components/ui'
import { downloadCsv } from '../../lib/csv'
import { dateTime, money, orderTypeLabel, paymentLabel, qty } from '../../lib/format'
import type { DayEndReport, SalesReport } from '../../types'

type Tab = 'overview' | 'items' | 'taxes' | 'day-end'
const TABS: { id: Tab; label: string }[] = [
  { id: 'overview', label: 'Sales overview' }, { id: 'items', label: 'Items & categories' }, { id: 'taxes', label: 'Tax summary' },
  { id: 'day-end', label: 'Day-end reconciliation' },
]

const shift = (iso: string, days: number) => {
  const d = new Date(`${iso}T00:00:00Z`)
  d.setUTCDate(d.getUTCDate() + days)
  return d.toISOString().slice(0, 10)
}
const sourceLabel: Record<string, string> = { Pos: 'POS', Qr: 'QR table ordering', Aggregator: 'Aggregator' }

export function ReportsPage() {
  const outletId = useOutletId()
  const { outlet } = useSession()
  const [tab, setTab] = useState<Tab>('overview')
  const t = useBusinessToday(outletId)
  const [picked, setRange] = useState<{ from: string; to: string } | null>(null)
  const range = picked ?? { from: t, to: t }
  const presets = [
    { label: 'Today', from: t, to: t },
    { label: 'Yesterday', from: shift(t, -1), to: shift(t, -1) },
    { label: 'Last 7 days', from: shift(t, -6), to: t },
    { label: 'This month', from: `${t.slice(0, 8)}01`, to: t },
  ]
  const sales = useQuery({
    queryKey: ['reports', outletId, 'sales', range.from, range.to],
    queryFn: () => api<SalesReport>(`/api/outlets/${outletId}/reports/sales?from=${range.from}&to=${range.to}`),
    enabled: tab !== 'day-end' && range.from <= range.to,
  })
  const r = sales.data
  const fileSuffix = `${outlet?.code ?? 'outlet'}-${range.from}${range.from === range.to ? '' : `-to-${range.to}`}`

  return (
    <div className="space-y-6">
      <PageHeader title="Reports" description={`Figures for ${outlet?.name ?? 'this outlet'} from settled bills. Net sales exclude tax; collected includes tax and round-off.`} />
      <div role="tablist" aria-label="Report" className="flex flex-wrap gap-1 border-b border-slate-200">
        {TABS.map((t) => (
          <button key={t.id} role="tab" aria-selected={tab === t.id} onClick={() => setTab(t.id)}
            className={clsx('-mb-px border-b-2 px-4 py-2 text-sm font-medium',
              tab === t.id ? 'border-brand-700 text-brand-800' : 'border-transparent text-slate-500 hover:text-slate-800')}>
            {t.label}
          </button>
        ))}
      </div>

      {tab === 'day-end' ? <DayEnd outletId={outletId} /> : (
        <>
          <div className="flex flex-wrap items-end gap-3">
            <Input label="From" type="date" value={range.from} max={range.to} onChange={(e) => setRange({ ...range, from: e.target.value })} className="w-44" />
            <Input label="To" type="date" value={range.to} min={range.from} onChange={(e) => setRange({ ...range, to: e.target.value })} className="w-44" />
            <div className="flex flex-wrap gap-2" role="group" aria-label="Date presets">
              {presets.map((p) => (
                <Button key={p.label} size="sm" variant={range.from === p.from && range.to === p.to ? 'primary' : 'secondary'}
                  onClick={() => setRange({ from: p.from, to: p.to })}>{p.label}</Button>
              ))}
            </div>
          </div>
          <ErrorBanner error={sales.error} />
          {sales.isLoading || !r ? (sales.error ? null : <Spinner />) : tab === 'overview' ? <Overview r={r} /> : tab === 'items' ? (
            <Items r={r} fileSuffix={fileSuffix} />
          ) : <Taxes r={r} fileSuffix={fileSuffix} />}
        </>
      )}
    </div>
  )
}

function Kpi({ label, value, sub, testId }: { label: string; value: string; sub?: string; testId?: string }) {
  return (
    <div className="rounded-xl bg-white p-4 shadow-sm ring-1 ring-slate-200">
      <p className="text-sm text-slate-500">{label}</p>
      <p className="mt-1 text-2xl font-semibold text-slate-900" data-testid={testId}>{value}</p>
      {sub && <p className="mt-0.5 text-xs text-slate-500">{sub}</p>}
    </div>
  )
}

function Bars({ rows, label }: { rows: { key: string; value: number; caption: string }[]; label: string }) {
  const max = Math.max(...rows.map((r) => r.value), 0)
  if (rows.length === 0 || max === 0) return <p className="p-4 text-sm text-slate-500">No sales in this period.</p>
  return (
    <ul className="space-y-2 p-4" aria-label={label}>
      {rows.map((r) => (
        <li key={r.key} className="grid grid-cols-[6rem_1fr_7rem] items-center gap-3 text-sm">
          <span className="truncate text-slate-600">{r.key}</span>
          <span className="h-3 rounded bg-slate-100" aria-hidden>
            <span className="block h-3 rounded bg-brand-600" style={{ width: `${(r.value / max) * 100}%` }} />
          </span>
          <span className="text-right font-medium">{r.caption}</span>
        </li>
      ))}
    </ul>
  )
}

function Overview({ r }: { r: SalesReport }) {
  const s = r.summary
  if (s.orders === 0 && s.cancelledOrders === 0) return <EmptyState title="No settled bills in this period" description="Pick another date range." />
  return (
    <div className="space-y-6">
      <div className="grid gap-4 sm:grid-cols-2 xl:grid-cols-4">
        <Kpi label="Net sales" value={money(s.netSales)} sub={`Gross ${money(s.grossSales)} − discounts ${money(s.discounts)}`} testId="net-sales" />
        <Kpi label="Collected" value={money(s.totalCollected)} sub={`Tax ${money(s.tax)} · round-off ${money(s.roundOff)}`} testId="total-collected" />
        <Kpi label="Bills" value={String(s.orders)} sub={`${s.guests} guests`} testId="bill-count" />
        <Kpi label="Average bill" value={money(s.averageOrderValue)} />
      </div>
      <div className="grid gap-6 lg:grid-cols-2">
        <Card title="Payments">
          <Table head={['Method', 'Count', 'Amount']} empty={r.payments.length === 0}>
            {r.payments.map((p) => (
              <tr key={p.method}><Td>{paymentLabel[p.method]}</Td><Td>{p.count}</Td><Td className="font-medium">{money(p.amount)}</Td></tr>
            ))}
          </Table>
        </Card>
        <Card title="Order types and channels">
          <Table head={['', 'Bills', 'Net sales', 'Collected']}>
            {r.byType.map((g) => (
              <tr key={`t-${g.key}`}><Td>{orderTypeLabel[g.key as keyof typeof orderTypeLabel] ?? g.key}</Td><Td>{g.orders}</Td><Td>{money(g.netSales)}</Td><Td>{money(g.total)}</Td></tr>
            ))}
            {r.bySource.map((g) => (
              <tr key={`s-${g.key}`} className="bg-slate-50/60"><Td>{sourceLabel[g.key] ?? g.key}</Td><Td>{g.orders}</Td><Td>{money(g.netSales)}</Td><Td>{money(g.total)}</Td></tr>
            ))}
          </Table>
        </Card>
        <Card title="Daily collections">
          <Bars label="Daily collections" rows={r.daily.map((d) => ({ key: d.date.slice(5), value: d.total, caption: money(d.total) }))} />
        </Card>
        <Card title="Bills by hour">
          <Bars label="Bills by hour" rows={r.hourly.map((h) => ({
            key: `${String(h.hour).padStart(2, '0')}:00`, value: h.total, caption: `${h.orders} · ${money(h.total)}`,
          }))} />
        </Card>
      </div>
      <Card title="Leakage">
        <dl className="grid gap-4 p-4 sm:grid-cols-2">
          <div><dt className="text-sm text-slate-500">Cancelled orders</dt><dd className="text-lg font-semibold">{s.cancelledOrders} · {money(s.cancelledValue)}</dd></div>
          <div><dt className="text-sm text-slate-500">Voided items on settled bills</dt><dd className="text-lg font-semibold">{s.voidedItems} · {money(s.voidedValue)}</dd></div>
        </dl>
      </Card>
    </div>
  )
}

function Items({ r, fileSuffix }: { r: SalesReport; fileSuffix: string }) {
  if (r.items.length === 0) return <EmptyState title="No items sold in this period" />
  return (
    <div className="grid gap-6 xl:grid-cols-[2fr_1fr]">
      <Card title="Item sales" actions={
        <Button size="sm" variant="secondary" onClick={() => downloadCsv(`item-sales-${fileSuffix}.csv`, ['Item', 'Category', 'Quantity', 'Net sales', 'Total incl. tax'],
          r.items.map((i) => [i.name, i.category, i.quantity, i.netSales.toFixed(2), i.total.toFixed(2)]))}>
          <Download className="size-4" /> CSV
        </Button>}>
        <Table head={['Item', 'Category', 'Qty', 'Net sales', 'Incl. tax']}>
          {r.items.map((i) => (
            <tr key={i.menuItemId}>
              <Td className="font-medium text-slate-900">{i.name}</Td><Td>{i.category}</Td><Td>{qty(i.quantity)}</Td>
              <Td>{money(i.netSales)}</Td><Td>{money(i.total)}</Td>
            </tr>
          ))}
        </Table>
      </Card>
      <Card title="Categories">
        <Bars label="Category sales" rows={r.categories.map((c) => ({ key: c.category, value: c.netSales, caption: money(c.netSales) }))} />
      </Card>
    </div>
  )
}

function Taxes({ r, fileSuffix }: { r: SalesReport; fileSuffix: string }) {
  const total = r.taxes.reduce((s, t) => s + t.amount, 0)
  return (
    <Card title="Tax collected" actions={r.taxes.length > 0 && (
      <Button size="sm" variant="secondary" onClick={() => downloadCsv(`tax-summary-${fileSuffix}.csv`, ['Tax', 'Rate %', 'Taxable value', 'Tax amount'],
        r.taxes.map((t) => [t.code, t.ratePercent, t.taxable.toFixed(2), t.amount.toFixed(2)]))}>
        <Download className="size-4" /> CSV
      </Button>)}>
      {r.taxes.length === 0 ? <EmptyState title="No tax collected in this period" /> : (
        <Table head={['Tax', 'Rate', 'Taxable value', 'Tax amount']}>
          {r.taxes.map((t) => (
            <tr key={`${t.code}-${t.ratePercent}`}>
              <Td className="font-medium">{t.code}</Td><Td>{t.ratePercent}%</Td><Td>{money(t.taxable)}</Td><Td>{money(t.amount)}</Td>
            </tr>
          ))}
          <tr className="bg-slate-50 font-semibold"><Td>Total</Td><Td>{''}</Td><Td>{''}</Td><Td>{money(total)}</Td></tr>
        </Table>
      )}
    </Card>
  )
}

function DayEnd({ outletId }: { outletId: string }) {
  const businessToday = useBusinessToday(outletId)
  const [picked, setDate] = useState<string | null>(null)
  const date = picked ?? businessToday
  const report = useQuery({
    queryKey: ['reports', outletId, 'day-end', date],
    queryFn: () => api<DayEndReport>(`/api/outlets/${outletId}/reports/day-end?date=${date}`),
    refetchInterval: 60_000,
  })
  const d = report.data
  const checks: { ok: boolean; text: ReactNode }[] = d ? [
    { ok: d.unreconciled === 0, text: d.unreconciled === 0 ? 'Payments match settled bills.' : `Payments differ from settled bills by ${money(d.unreconciled)}.` },
    { ok: d.runningOrders === 0, text: d.runningOrders === 0 ? 'No running orders.' : `${d.runningOrders} order(s) are still running.` },
    { ok: d.openCashSessions === 0, text: d.openCashSessions === 0 ? 'All cash sessions are closed.' : `${d.openCashSessions} cash session(s) still open.` },
    { ok: d.cashVariance === 0, text: d.cashVariance === 0 ? 'No cash variance.' : `Cash variance of ${money(d.cashVariance)}.` },
    ...(d.cashOutsideSessions > 0 ? [{ ok: false, text: `${money(d.cashOutsideSessions)} cash was taken without an open session.` }] : []),
  ] : []
  return (
    <div className="space-y-6">
      <Input label="Business date" type="date" value={date} max={businessToday} onChange={(e) => setDate(e.target.value)} className="w-48" />
      <ErrorBanner error={report.error} />
      {!d ? (report.error ? null : <Spinner />) : (
        <>
          <div className="grid gap-4 sm:grid-cols-3">
            <Kpi label="Settled bills" value={String(d.settledOrders)} />
            <Kpi label="Sales" value={money(d.salesTotal)} testId="day-sales" />
            <Kpi label="Payments received" value={money(d.paymentsTotal)} testId="day-payments" />
          </div>
          <Card title="Closing checklist">
            <ul className="divide-y divide-slate-100" data-testid="day-end-checks">
              {checks.map((c, i) => (
                <li key={i} className="flex items-center gap-2 px-4 py-2.5 text-sm">
                  {c.ok ? <CheckCircle2 className="size-4 text-emerald-600" aria-label="OK" /> : <AlertTriangle className="size-4 text-amber-600" aria-label="Needs attention" />}
                  {c.text}
                </li>
              ))}
            </ul>
          </Card>
          <div className="grid gap-6 lg:grid-cols-2">
            <Card title="Payments by method">
              <Table head={['Method', 'Count', 'Amount']} empty={d.payments.length === 0}>
                {d.payments.map((p) => <tr key={p.method}><Td>{paymentLabel[p.method]}</Td><Td>{p.count}</Td><Td>{money(p.amount)}</Td></tr>)}
              </Table>
            </Card>
            <Card title="Cash sessions">
              {d.cashSessions.length === 0 ? <EmptyState title="No cash sessions on this date" /> : (
                <Table head={['Opened', 'By', 'Expected', 'Counted', 'Variance']}>
                  {d.cashSessions.map((s) => (
                    <tr key={s.id}>
                      <Td>{dateTime(s.openedAt)}</Td>
                      <Td>{s.openedBy}</Td>
                      <Td>{money(s.expectedCash)}</Td>
                      <Td>{s.countedCash != null ? money(s.countedCash) : <Badge tone="amber">Open</Badge>}</Td>
                      <Td className={s.variance ? 'font-medium text-rose-700' : ''}>{s.variance != null ? money(s.variance) : '—'}</Td>
                    </tr>
                  ))}
                </Table>
              )}
            </Card>
          </div>
        </>
      )}
    </div>
  )
}
