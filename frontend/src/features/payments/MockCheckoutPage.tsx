import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { CheckCircle2, FlaskConical, XCircle } from 'lucide-react'
import { useParams } from 'react-router'
import { api } from '../../app/api'
import { Button, ErrorBanner, Spinner } from '../../components/ui'
import { money } from '../../lib/format'

type MockIntent = { providerRef: string; amount: number; status: 'Pending' | 'Succeeded' | 'Failed'; merchant: string }

/** Stand-in for a hosted payment page; only served when mock simulators are enabled on the API. */
export function MockCheckoutPage() {
  const { providerRef = '' } = useParams()
  const qc = useQueryClient()
  const key = ['mock-payment', providerRef]
  const intent = useQuery({ queryKey: key, queryFn: () => api<MockIntent>(`/api/dev/mock-payments/${encodeURIComponent(providerRef)}`, { auth: false }), retry: false })
  const pay = useMutation({
    mutationFn: (outcome: 'succeeded' | 'failed') =>
      api(`/api/dev/mock-payments/${encodeURIComponent(providerRef)}/${outcome}`, { method: 'POST', auth: false }),
    onSuccess: () => qc.invalidateQueries({ queryKey: key }),
  })

  return (
    <main className="flex min-h-screen items-center justify-center bg-slate-100 p-4">
      <div className="w-full max-w-sm rounded-2xl bg-white p-6 shadow-lg">
        <p className="mb-4 flex items-center gap-2 rounded-lg bg-amber-50 px-3 py-2 text-xs font-medium text-amber-800">
          <FlaskConical className="size-4" aria-hidden /> Test checkout. No real money is charged.
        </p>
        {intent.isLoading && <Spinner />}
        {intent.isError && <p className="text-center text-slate-600">This payment link is invalid or has expired.</p>}
        {intent.data && (
          <div className="space-y-4 text-center">
            <div>
              <p className="text-sm text-slate-500">Pay {intent.data.merchant}</p>
              <p className="text-3xl font-semibold" data-testid="checkout-amount">{money(intent.data.amount)}</p>
            </div>
            <ErrorBanner error={pay.error} />
            {intent.data.status === 'Pending' ? (
              <div className="grid gap-2">
                <Button variant="success" loading={pay.isPending && pay.variables === 'succeeded'} disabled={pay.isPending}
                  onClick={() => pay.mutate('succeeded')}>Pay {money(intent.data.amount)}</Button>
                <Button variant="secondary" loading={pay.isPending && pay.variables === 'failed'} disabled={pay.isPending}
                  onClick={() => pay.mutate('failed')}>Simulate a declined payment</Button>
              </div>
            ) : intent.data.status === 'Succeeded' ? (
              <p className="flex items-center justify-center gap-2 font-medium text-emerald-700" role="status">
                <CheckCircle2 className="size-5" aria-hidden /> Payment successful
              </p>
            ) : (
              <p className="flex items-center justify-center gap-2 font-medium text-rose-700" role="status">
                <XCircle className="size-5" aria-hidden /> Payment declined
              </p>
            )}
          </div>
        )}
      </div>
    </main>
  )
}
