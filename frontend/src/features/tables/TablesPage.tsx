import { useMutation, useQueryClient } from '@tanstack/react-query'
import { Download, Pencil, Plus, QrCode, RefreshCw } from 'lucide-react'
import QRCode from 'qrcode'
import { useEffect, useState } from 'react'
import { api } from '../../app/api'
import { useOutletId } from '../../app/session'
import { Badge, Button, Card, Checkbox, EmptyState, ErrorBanner, Input, Modal, PageHeader, Spinner, Table as DataTable, Td, useToast } from '../../components/ui'
import type { Table } from '../../types'
import { posKeys, useTables } from '../pos/usePos'

const qrUrl = (token: string) => `${window.location.origin}/qr/${token}`

export function TablesPage() {
  const outletId = useOutletId()
  const tables = useTables(outletId)
  const [editing, setEditing] = useState<Table | 'new' | null>(null)
  const [qr, setQr] = useState<Table | null>(null)

  return (
    <div>
      <PageHeader title="Tables & QR" description="Dining tables for this outlet and their QR ordering links."
        actions={<Button onClick={() => setEditing('new')}><Plus className="size-4" aria-hidden /> New table</Button>} />
      <Card>
        <ErrorBanner error={tables.error} />
        {tables.isLoading ? <Spinner /> : !tables.data?.length ? (
          <EmptyState title="No tables yet" description="Add tables to take dine-in orders and enable QR ordering."
            action={<Button onClick={() => setEditing('new')}>Add a table</Button>} />
        ) : (
          <DataTable head={['Table', 'Area', 'Seats', 'Status', '']}>
            {tables.data.map((t) => (
              <tr key={t.id} className={t.isActive ? undefined : 'opacity-60'}>
                <Td className="font-medium text-slate-900">{t.name}</Td>
                <Td>{t.area || '—'}</Td>
                <Td>{t.capacity}</Td>
                <Td>
                  {!t.isActive ? <Badge>Inactive</Badge>
                    : t.status === 'free' ? <Badge tone="green">Free</Badge>
                      : t.status === 'billed' ? <Badge tone="amber">Bill printed</Badge> : <Badge tone="blue">Occupied</Badge>}
                </Td>
                <Td className="text-right whitespace-nowrap">
                  <Button variant="ghost" size="sm" onClick={() => setQr(t)}><QrCode className="size-4" aria-hidden /> QR link</Button>
                  <Button variant="ghost" size="sm" onClick={() => setEditing(t)} aria-label={`Edit ${t.name}`}><Pencil className="size-4" aria-hidden /></Button>
                </Td>
              </tr>
            ))}
          </DataTable>
        )}
      </Card>
      {editing && <TableEditor table={editing === 'new' ? null : editing} onClose={() => setEditing(null)} />}
      {qr && <QrDialog table={qr} onClose={() => setQr(null)} />}
    </div>
  )
}

function TableEditor({ table, onClose }: { table: Table | null; onClose: () => void }) {
  const outletId = useOutletId()
  const qc = useQueryClient()
  const toast = useToast()
  const [form, setForm] = useState({
    name: table?.name ?? '', area: table?.area ?? '', capacity: table?.capacity.toString() ?? '4',
    sortOrder: table?.sortOrder.toString() ?? '0', isActive: table?.isActive ?? true,
  })
  const save = useMutation({
    mutationFn: () => api(`/api/outlets/${outletId}/tables${table ? `/${table.id}` : ''}`, {
      method: table ? 'PUT' : 'POST',
      body: { name: form.name, area: form.area || null, capacity: Number(form.capacity), sortOrder: Number(form.sortOrder || 0), isActive: form.isActive },
    }),
    onSuccess: () => {
      void qc.invalidateQueries({ queryKey: posKeys.tables(outletId) })
      toast('Table saved')
      onClose()
    },
  })
  return (
    <Modal open onClose={onClose} title={table ? `Edit ${table.name}` : 'New table'} size="sm"
      footer={<>
        <Button variant="secondary" onClick={onClose}>Cancel</Button>
        <Button type="submit" form="table-form" loading={save.isPending}>Save</Button>
      </>}>
      <form id="table-form" className="space-y-3" onSubmit={(e) => { e.preventDefault(); save.mutate() }}>
        <Input label="Name" required maxLength={40} value={form.name} onChange={(e) => setForm({ ...form, name: e.target.value })} />
        <Input label="Area" placeholder="e.g. Terrace" value={form.area} onChange={(e) => setForm({ ...form, area: e.target.value })} />
        <div className="grid grid-cols-2 gap-3">
          <Input label="Seats" type="number" min={1} max={50} required value={form.capacity} onChange={(e) => setForm({ ...form, capacity: e.target.value })} />
          <Input label="Sort order" type="number" value={form.sortOrder} onChange={(e) => setForm({ ...form, sortOrder: e.target.value })} />
        </div>
        <Checkbox label="Active" checked={form.isActive} onChange={(v) => setForm({ ...form, isActive: v })} />
        <ErrorBanner error={save.error} />
      </form>
    </Modal>
  )
}

function QrDialog({ table, onClose }: { table: Table; onClose: () => void }) {
  const outletId = useOutletId()
  const qc = useQueryClient()
  const toast = useToast()
  const [token, setToken] = useState(table.qrToken)
  const url = qrUrl(token)
  const svg = useQrSvg(url)
  const regenerate = useMutation({
    mutationFn: () => api<Table>(`/api/outlets/${outletId}/tables/${table.id}/regenerate-qr`, { method: 'POST' }),
    onSuccess: (t) => {
      setToken(t.qrToken)
      void qc.invalidateQueries({ queryKey: posKeys.tables(outletId) })
      toast('New QR link generated; the old link no longer works')
    },
  })
  const copy = async () => {
    try {
      await navigator.clipboard.writeText(url)
      toast('Link copied')
    } catch {
      toast('Copy failed; select the link and copy it manually', 'error')
    }
  }
  return (
    <Modal open onClose={onClose} title={`QR ordering · ${table.name}`}
      footer={<>
        <Button variant="secondary" onClick={() => regenerate.mutate()} loading={regenerate.isPending}>
          <RefreshCw className="size-4" aria-hidden /> Regenerate link
        </Button>
        <Button onClick={() => void copy()}>Copy link</Button>
      </>}>
      <p className="text-sm text-slate-600">
        Print this code for the table. Guests scan it to browse the menu and place orders, which staff accept on the POS.
        QR ordering must be enabled in outlet settings.
      </p>
      <div className="mt-4 flex flex-col items-center gap-3">
        {svg ? <img src={svg} alt={`QR code for table ${table.name}`} className="size-56 rounded-lg ring-1 ring-slate-200" data-testid="qr-image" />
          : <div className="size-56 rounded-lg motion-safe:animate-pulse bg-slate-100" aria-hidden />}
        {svg && (
          <a href={svg} download={`table-${table.name.replace(/[^\w-]+/g, '_')}-qr.svg`}
            className="inline-flex items-center gap-1 rounded-lg px-2 py-1 text-sm font-medium text-brand-700 hover:bg-brand-50">
            <Download className="size-4" aria-hidden /> Download for printing (SVG)
          </a>
        )}
      </div>
      <p className="mt-3 break-all rounded-lg bg-slate-50 p-3 font-mono text-sm text-slate-800 ring-1 ring-slate-200" data-testid="qr-url">{url}</p>
      <ErrorBanner error={regenerate.error} />
    </Modal>
  )
}

function useQrSvg(text: string) {
  const [result, setResult] = useState<{ text: string; svg: string } | null>(null)
  useEffect(() => {
    let active = true
    void QRCode.toString(text, { type: 'svg', errorCorrectionLevel: 'M', margin: 2 }).then((svg) => {
      if (active) setResult({ text, svg: `data:image/svg+xml;charset=utf-8,${encodeURIComponent(svg)}` })
    })
    return () => { active = false }
  }, [text])
  return result?.text === text ? result.svg : null
}
