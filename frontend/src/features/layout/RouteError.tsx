import { isRouteErrorResponse, useRouteError } from 'react-router'
import { Button } from '../../components/ui'

export function RouteError() {
  const error = useRouteError()
  const detail = isRouteErrorResponse(error) ? `${error.status} ${error.statusText}` : error instanceof Error ? error.message : null
  return (
    <div className="flex min-h-[60vh] items-center justify-center p-6">
      <div className="max-w-md space-y-4 text-center" role="alert">
        <h1 className="text-xl font-semibold text-slate-900">Something went wrong</h1>
        <p className="text-sm text-slate-600">This screen hit an unexpected error. Your saved orders are not affected.</p>
        {detail && <p className="break-words rounded-lg bg-slate-100 px-3 py-2 font-mono text-xs text-slate-600">{detail}</p>}
        <div className="flex justify-center gap-2">
          <Button variant="secondary" onClick={() => window.history.back()}>Go back</Button>
          <Button onClick={() => window.location.reload()}>Reload</Button>
        </div>
      </div>
    </div>
  )
}
