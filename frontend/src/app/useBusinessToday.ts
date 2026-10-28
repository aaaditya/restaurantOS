import { useQuery } from '@tanstack/react-query'
import { today } from '../lib/format'
import type { Outlet } from '../types'
import { api } from './api'

/**
 * Today's business date in the outlet's time zone. Orders and reports are filed by outlet-local date,
 * so a browser in another zone (or past midnight UTC) must not pick its own calendar day.
 */
export function useBusinessToday(outletId: string) {
  const outlet = useQuery({ queryKey: ['outlet', outletId], queryFn: () => api<Outlet>(`/api/outlets/${outletId}`), staleTime: 60_000 })
  return today(outlet.data?.timeZone)
}
