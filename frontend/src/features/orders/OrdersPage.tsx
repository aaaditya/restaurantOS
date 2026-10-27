import { useQuery } from '@tanstack/react-query'
import { useState } from 'react'
import { Link } from 'react-router'
import { api } from '../../app/api'
import { useOutletId } from '../../app/session'
import { Card, ErrorBanner, PageHeader, Select, Spinner, Table, Td } from '../../components/ui'
import { dateTime, money, orderTypeLabel, today } from '../../lib/format'
import type { OrderSummary } from '../../types'
import { StatusBadge } from '../pos/OrderScreen'

export function OrdersPage() {
  const outletId = useOutletId()
  const [view, setView] = useState('all')
  const [date, setDate] = useState(today())
  const q = useQuery({
    queryKey: ['orders', outletId, view, date],
    queryFn: () => api<OrderSummary[]>(`/api/outlets/${outletId}/orders?view=${view}${view === 'running' ? '' : `&date=${date}`}`),
  })

  return (
    <div>
      <PageHeader title="Orders" description="Every order for the selected business day." />
      <Card>
        <div className="flex flex-wrap gap-3 border-b border-slate-100 p-4">
          <Select label="Show" value={view} onChange={(e) => setView(e.target.value)} className="w-44">
            <option value="all">All</option>
            <option value="running">Running</option>
            <option value="settled">Settled</option>
            <option value="cancelled">Cancelled</option>
          </Select>
          {view !== 'running' && (
            <label className="text-sm">
              <span className="mb-1 block font-medium text-slate-700">Business day</span>
              <input type="date" value={date} onChange={(e) => setDate(e.target.value)} className="rounded-lg border-slate-300 text-sm" />
            </label>
          )}
        </div>
        {q.isLoading && <Spinner />}
        <ErrorBanner error={q.error} />
        {q.data && (
          <Table head={['Order', 'Type', 'Customer', 'Status', 'Invoice', 'Total', 'Time']} empty={q.data.length === 0}>
            {q.data.map((o) => (
              <tr key={o.id} className="hover:bg-slate-50">
                <Td><Link to={o.status === 'Settled' || o.status === 'Cancelled' ? `/orders/${o.id}/invoice` : `/pos/orders/${o.id}`}
                  className="font-medium text-brand-700 hover:underline">#{o.orderNumber}</Link></Td>
                <Td>{orderTypeLabel[o.type]}{o.tableName ? ` · ${o.tableName}` : ''}{o.source !== 'Pos' ? ` · ${o.source}` : ''}</Td>
                <Td>{o.customerName ?? '—'}</Td>
                <Td><StatusBadge status={o.status} /></Td>
                <Td>{o.invoiceNumber ?? '—'}</Td>
                <Td className="font-medium">{money(o.grandTotal)}</Td>
                <Td>{dateTime(o.settledAt ?? o.createdAt)}</Td>
              </tr>
            ))}
          </Table>
        )}
      </Card>
    </div>
  )
}
