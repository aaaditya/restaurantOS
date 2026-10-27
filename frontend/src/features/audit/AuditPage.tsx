import { useInfiniteQuery } from '@tanstack/react-query'
import { useState } from 'react'
import { api } from '../../app/api'
import { useSession } from '../../app/session'
import { Button, Card, EmptyState, ErrorBanner, PageHeader, Select, Spinner, Table, Td } from '../../components/ui'
import { dateTime } from '../../lib/format'
import type { AuditPage as AuditPageData } from '../../types'

const AREAS = [
  { value: '', label: 'All activity' }, { value: 'order.', label: 'Orders & billing' }, { value: 'cash_session.', label: 'Cash sessions' },
  { value: 'menu.', label: 'Menu' }, { value: 'table.', label: 'Tables' }, { value: 'inventory.', label: 'Inventory' },
  { value: 'purchase_order.', label: 'Purchase orders' }, { value: 'supplier.', label: 'Suppliers' }, { value: 'customer.', label: 'Customers' },
  { value: 'staff.', label: 'Staff' }, { value: 'user.', label: 'Account' }, { value: 'outlet.', label: 'Outlets' },
]

function summarize(data: string | null) {
  if (!data) return ''
  try {
    const obj = JSON.parse(data) as Record<string, unknown>
    return Object.entries(obj).filter(([, v]) => v !== null && typeof v !== 'object')
      .map(([k, v]) => `${k}: ${String(v)}`).join(' · ')
  } catch {
    return data
  }
}

export function AuditPage() {
  const { me } = useSession()
  const [area, setArea] = useState('')
  const [outletId, setOutletId] = useState('')
  const outletName = (id: string | null) => (id ? me?.outlets.find((o) => o.id === id)?.name ?? 'Other outlet' : 'All outlets')
  const log = useInfiniteQuery({
    queryKey: ['audit', area, outletId],
    initialPageParam: null as string | null,
    queryFn: ({ pageParam }) => {
      const q = new URLSearchParams({ limit: '50' })
      if (area) q.set('action', area)
      if (outletId) q.set('outletId', outletId)
      if (pageParam) q.set('before', pageParam)
      return api<AuditPageData>(`/api/audit?${q}`)
    },
    getNextPageParam: (last) => last.nextBefore,
  })
  const rows = log.data?.pages.flatMap((p) => p.items) ?? []

  return (
    <div className="space-y-6">
      <PageHeader title="Audit log" description="Every sensitive change — voids, discounts, cancellations, settlements, stock and staff changes — with who made it and when." />
      <div className="flex flex-wrap gap-3">
        <Select label="Area" value={area} onChange={(e) => setArea(e.target.value)} className="w-52">
          {AREAS.map((a) => <option key={a.value} value={a.value}>{a.label}</option>)}
        </Select>
        <Select label="Outlet" value={outletId} onChange={(e) => setOutletId(e.target.value)} className="w-52">
          <option value="">All outlets</option>
          {me?.outlets.map((o) => <option key={o.id} value={o.id}>{o.name}</option>)}
        </Select>
      </div>
      <Card>
        <ErrorBanner error={log.error} />
        {log.isLoading ? <Spinner /> : rows.length === 0 ? <EmptyState title="No matching activity" /> : (
          <>
            <Table head={['When', 'Who', 'Action', 'Outlet', 'Details']}>
              {rows.map((e) => (
                <tr key={e.id}>
                  <Td className="whitespace-nowrap">{dateTime(e.createdAt)}</Td>
                  <Td>{e.userName ?? <span className="text-slate-400">System / guest</span>}</Td>
                  <Td><code className="rounded bg-slate-100 px-1.5 py-0.5 text-xs">{e.action}</code></Td>
                  <Td>{outletName(e.outletId)}</Td>
                  <Td className="max-w-md truncate text-xs text-slate-600"><span title={e.data ?? ''}>{summarize(e.data)}</span></Td>
                </tr>
              ))}
            </Table>
            {log.hasNextPage && (
              <div className="flex justify-center p-4">
                <Button variant="secondary" loading={log.isFetchingNextPage} onClick={() => void log.fetchNextPage()}>Load more</Button>
              </div>
            )}
          </>
        )}
      </Card>
    </div>
  )
}
