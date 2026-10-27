import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { useState } from 'react'
import { api } from '../../app/api'
import { useOutletId } from '../../app/session'
import { Badge, Button, Card, EmptyState, ErrorBanner, Input, Modal, PageHeader, Spinner, Table, Td, Textarea, useToast } from '../../components/ui'
import { dateTime, money } from '../../lib/format'
import type { CashSession } from '../../types'

type Current = { session: CashSession | null; cashPayments?: number; expectedCash?: number }

export function CashPage() {
  const outletId = useOutletId()
  const qc = useQueryClient()
  const toast = useToast()
  const base = `/api/outlets/${outletId}/cash-sessions`
  const current = useQuery({ queryKey: ['cash', outletId, 'current'], queryFn: () => api<Current>(`${base}/current`), refetchInterval: 30_000 })
  const history = useQuery({ queryKey: ['cash', outletId, 'list'], queryFn: () => api<CashSession[]>(base) })
  const [float, setFloat] = useState('0')
  const [closing, setClosing] = useState(false)

  const refresh = () => void qc.invalidateQueries({ queryKey: ['cash', outletId] })
  const open = useMutation({
    mutationFn: () => api(base, { method: 'POST', body: { openingFloat: Number(float || 0) } }),
    onSuccess: () => { refresh(); toast('Cash session opened') },
  })

  const session = current.data?.session
  return (
    <div className="space-y-6">
      <PageHeader title="Cash register" description="Open a session with the opening float, then close it with a counted drawer to record any variance." />
      {current.isLoading ? <Spinner /> : session ? (
        <Card title="Current session" actions={<Button variant="danger" onClick={() => setClosing(true)}>Close session</Button>}>
          <dl className="grid gap-4 p-4 sm:grid-cols-4">
            <Stat label="Opened" value={dateTime(session.openedAt)} />
            <Stat label="Opening float" value={money(session.openingFloat)} />
            <Stat label="Cash sales" value={money(current.data?.cashPayments)} />
            <Stat label="Expected in drawer" value={money(current.data?.expectedCash)} testId="expected-cash" />
          </dl>
        </Card>
      ) : (
        <Card title="No open session">
          <form className="flex flex-wrap items-end gap-3 p-4" onSubmit={(e) => { e.preventDefault(); open.mutate() }}>
            <Input label="Opening float (₹)" type="number" min={0} step="0.01" value={float} onChange={(e) => setFloat(e.target.value)} className="w-48" />
            <Button type="submit" loading={open.isPending}>Open session</Button>
            <p className="w-full text-sm text-slate-500">Cash payments are linked to the open session. Without one, cash is still accepted but not reconciled to a drawer.</p>
            <div className="w-full"><ErrorBanner error={open.error} /></div>
          </form>
        </Card>
      )}

      <Card title="History">
        <ErrorBanner error={history.error} />
        {history.isLoading ? <Spinner /> : !history.data?.length ? <EmptyState title="No sessions yet" /> : (
          <Table head={['Opened', 'Closed', 'Float', 'Expected', 'Counted', 'Variance', 'Notes']}>
            {history.data.map((s) => (
              <tr key={s.id}>
                <Td>{dateTime(s.openedAt)}</Td>
                <Td>{s.closedAt ? dateTime(s.closedAt) : <Badge tone="green">Open</Badge>}</Td>
                <Td>{money(s.openingFloat)}</Td>
                <Td>{s.status === 'Closed' ? money(s.expectedCash) : '—'}</Td>
                <Td>{s.countedCash != null ? money(s.countedCash) : '—'}</Td>
                <Td>
                  {s.variance == null ? '—' : (
                    <span className={s.variance === 0 ? 'text-emerald-700' : 'font-medium text-rose-700'}>{money(s.variance)}</span>
                  )}
                </Td>
                <Td className="max-w-xs truncate">{s.notes}</Td>
              </tr>
            ))}
          </Table>
        )}
      </Card>
      {closing && session && (
        <CloseDialog sessionId={session.id} expected={current.data?.expectedCash ?? 0} onClose={() => setClosing(false)}
          onDone={() => { setClosing(false); refresh() }} />
      )}
    </div>
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

function CloseDialog({ sessionId, expected, onClose, onDone }: { sessionId: string; expected: number; onClose: () => void; onDone: () => void }) {
  const outletId = useOutletId()
  const toast = useToast()
  const [counted, setCounted] = useState('')
  const [notes, setNotes] = useState('')
  const variance = counted === '' ? null : Math.round((Number(counted) - expected) * 100) / 100
  const close = useMutation({
    mutationFn: () => api<CashSession>(`/api/outlets/${outletId}/cash-sessions/${sessionId}/close`, {
      method: 'POST', body: { countedCash: Number(counted), notes: notes || null },
    }),
    onSuccess: (s) => {
      toast(s.variance === 0 ? 'Session closed; drawer balanced' : `Session closed with variance ${money(s.variance)}`, s.variance === 0 ? 'success' : 'info')
      onDone()
    },
  })
  return (
    <Modal open onClose={onClose} title="Close cash session" size="sm"
      footer={<>
        <Button variant="secondary" onClick={onClose}>Cancel</Button>
        <Button type="submit" form="close-form" variant="danger" loading={close.isPending} disabled={counted === ''}>Close session</Button>
      </>}>
      <form id="close-form" className="space-y-3" onSubmit={(e) => { e.preventDefault(); close.mutate() }}>
        <p className="text-sm text-slate-600">Expected in drawer: <strong>{money(expected)}</strong></p>
        <Input label="Counted cash (₹)" type="number" min={0} step="0.01" required autoFocus value={counted} onChange={(e) => setCounted(e.target.value)} />
        {variance != null && (
          <p className={variance === 0 ? 'text-sm text-emerald-700' : 'text-sm font-medium text-rose-700'}>
            {variance === 0 ? 'Drawer balances.' : `Variance: ${money(variance)} (${variance > 0 ? 'excess' : 'short'})`}
          </p>
        )}
        <Textarea label="Notes" value={notes} onChange={(e) => setNotes(e.target.value)} />
        <ErrorBanner error={close.error} />
      </form>
    </Modal>
  )
}
