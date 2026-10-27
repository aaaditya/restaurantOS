import { useQuery } from '@tanstack/react-query'
import { ArrowLeft, Printer } from 'lucide-react'
import { useEffect } from 'react'
import { Link, useParams, useSearchParams } from 'react-router'
import { api } from '../../app/api'
import { useOutletId } from '../../app/session'
import { Button, ErrorBanner, Spinner } from '../../components/ui'
import { dateTime, money, orderTypeLabel, paymentLabel, qty } from '../../lib/format'
import type { Invoice } from '../../types'

export function InvoicePage() {
  const outletId = useOutletId()
  const { orderId } = useParams()
  const [params] = useSearchParams()
  const q = useQuery({ queryKey: ['invoice', outletId, orderId], queryFn: () => api<Invoice>(`/api/outlets/${outletId}/orders/${orderId}/invoice`) })

  useEffect(() => {
    if (q.data && params.get('print') === '1') setTimeout(() => window.print(), 300)
  }, [q.data, params])

  if (q.isLoading) return <Spinner />
  if (q.error || !q.data) return <ErrorBanner error={q.error} />
  const { business, order, payments } = q.data
  const isTaxInvoice = order.status === 'Settled'
  const lines = order.lines.filter((l) => l.status !== 'Voided')

  return (
    <div className="mx-auto max-w-md">
      <div className="no-print mb-4 flex justify-between">
        <Link to="/orders"><Button variant="ghost"><ArrowLeft className="size-4" /> Orders</Button></Link>
        <Button onClick={() => window.print()}><Printer className="size-4" /> Print</Button>
      </div>
      <article className="bg-white p-6 font-mono text-[13px] leading-relaxed shadow-sm ring-1 ring-slate-200 print:p-0 print:shadow-none print:ring-0" aria-label="Invoice">
        <header className="text-center">
          <h1 className="text-base font-bold">{business.name}</h1>
          <p>{business.outlet}</p>
          {business.address && <p>{business.address}</p>}
          {business.phone && <p>Ph: {business.phone}</p>}
          {business.gstin && <p>GSTIN: {business.gstin}</p>}
          <p className="mt-2 font-bold">{isTaxInvoice ? 'TAX INVOICE' : order.status === 'Cancelled' ? 'CANCELLED ORDER' : 'BILL (NOT PAID)'}</p>
        </header>
        <hr className="my-2 border-dashed border-slate-400" />
        <dl className="grid grid-cols-2 gap-x-2">
          {order.invoiceNumber && <><dt>Invoice</dt><dd className="text-right">{order.invoiceNumber}</dd></>}
          <dt>Order</dt><dd className="text-right">#{order.orderNumber} · {orderTypeLabel[order.type]}</dd>
          {order.tableName && <><dt>Table</dt><dd className="text-right">{order.tableName}</dd></>}
          <dt>Date</dt><dd className="text-right">{dateTime(order.settledAt ?? order.billedAt ?? order.createdAt)}</dd>
          {order.customerName && <><dt>Customer</dt><dd className="text-right">{order.customerName}</dd></>}
        </dl>
        <hr className="my-2 border-dashed border-slate-400" />
        <table className="w-full">
          <thead><tr><th className="text-left">Item</th><th className="text-right">Qty</th><th className="text-right">Rate</th><th className="text-right">Amt</th></tr></thead>
          <tbody>
            {lines.map((l) => (
              <tr key={l.id} className="align-top">
                <td>{l.itemName}{l.variantName ? ` (${l.variantName})` : ''}{l.modifiers.length > 0 && <div className="text-[11px]">+ {l.modifiers.map((m) => m.name).join(', ')}</div>}</td>
                <td className="text-right">{qty(l.quantity)}</td>
                <td className="text-right">{l.unitPrice.toFixed(2)}</td>
                <td className="text-right">{l.gross.toFixed(2)}</td>
              </tr>
            ))}
          </tbody>
        </table>
        <hr className="my-2 border-dashed border-slate-400" />
        <dl className="grid grid-cols-2 gap-x-2">
          <dt>Subtotal</dt><dd className="text-right">{order.subtotal.toFixed(2)}</dd>
          {order.discountTotal > 0 && <><dt>Discount</dt><dd className="text-right">-{order.discountTotal.toFixed(2)}</dd></>}
          {order.taxBreakdown.map((t) => (
            <div key={`${t.code}${t.ratePercent}`} className="contents"><dt>{t.code} @{t.ratePercent}%</dt><dd className="text-right">{t.amount.toFixed(2)}</dd></div>
          ))}
          {order.roundOff !== 0 && <><dt>Round off</dt><dd className="text-right">{order.roundOff.toFixed(2)}</dd></>}
          <dt className="text-base font-bold">TOTAL</dt><dd className="text-right text-base font-bold">{money(order.grandTotal)}</dd>
        </dl>
        {order.pricesIncludeTax && <p className="mt-1 text-[11px]">Prices are inclusive of taxes.</p>}
        {payments.length > 0 && <>
          <hr className="my-2 border-dashed border-slate-400" />
          <dl className="grid grid-cols-2 gap-x-2">
            {payments.map((p, i) => <div key={i} className="contents"><dt>Paid · {paymentLabel[p.method]}</dt><dd className="text-right">{p.amount.toFixed(2)}</dd></div>)}
          </dl>
        </>}
        <p className="mt-4 text-center">Thank you! Visit again.</p>
      </article>
    </div>
  )
}
