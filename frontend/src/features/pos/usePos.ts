import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { api } from '../../app/api'
import type { Order, OrderSummary, PosMenu, Table } from '../../types'

export const posKeys = {
  menu: (outletId: string) => ['menu', outletId] as const,
  tables: (outletId: string) => ['tables', outletId] as const,
  orders: (outletId: string, view: string) => ['orders', outletId, view] as const,
  order: (outletId: string, id: string) => ['order', outletId, id] as const,
}

export function usePosMenu(outletId: string) {
  return useQuery({ queryKey: posKeys.menu(outletId), queryFn: () => api<PosMenu>(`/api/outlets/${outletId}/menu`), staleTime: 30_000 })
}

export function useTables(outletId: string) {
  return useQuery({ queryKey: posKeys.tables(outletId), queryFn: () => api<Table[]>(`/api/outlets/${outletId}/tables`), refetchInterval: 30_000 })
}

export function useRunningOrders(outletId: string) {
  return useQuery({
    queryKey: posKeys.orders(outletId, 'running'),
    queryFn: () => api<OrderSummary[]>(`/api/outlets/${outletId}/orders?view=running`),
    refetchInterval: 30_000,
  })
}

export function useOrder(outletId: string, orderId: string | undefined) {
  return useQuery({
    queryKey: posKeys.order(outletId, orderId ?? ''),
    queryFn: () => api<Order>(`/api/outlets/${outletId}/orders/${orderId}`),
    enabled: !!orderId,
  })
}

/** Runs an order mutation and pushes the returned order into the cache. */
export function useOrderAction<TVars>(outletId: string, fn: (vars: TVars) => Promise<Order>) {
  const qc = useQueryClient()
  return useMutation({
    mutationFn: fn,
    onSuccess: (order) => {
      qc.setQueryData(posKeys.order(outletId, order.id), order)
      void qc.invalidateQueries({ queryKey: ['orders', outletId] })
      void qc.invalidateQueries({ queryKey: posKeys.tables(outletId) })
    },
    onError: () => {
      // Stale versions or concurrent edits: refetch so the screen shows the latest state.
      void qc.invalidateQueries({ queryKey: ['order', outletId] })
    },
  })
}

export const orderUrl = (outletId: string, orderId?: string, suffix = '') =>
  `/api/outlets/${outletId}/orders${orderId ? `/${orderId}` : ''}${suffix}`
