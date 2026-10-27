import { HubConnectionBuilder, HubConnectionState, LogLevel, type HubConnection } from '@microsoft/signalr'
import { useQueryClient } from '@tanstack/react-query'
import { useEffect, useRef, useState } from 'react'
import { accessToken } from './api'

export type RealtimeEvent = 'kotChanged' | 'orderChanged' | 'qrOrderPlaced' | 'stockChanged' | 'externalOrderPlaced' | 'paymentIntentChanged'

/**
 * Joins the outlet's SignalR group and invalidates the given query keys when events arrive.
 * Returns the connection state so screens can show a live/offline indicator.
 */
export function useOutletRealtime(
  outletId: string | null,
  invalidate: Partial<Record<RealtimeEvent, ReadonlyArray<readonly unknown[]>>>,
  onEvent?: (event: RealtimeEvent, payload: unknown) => void,
) {
  const queryClient = useQueryClient()
  const [state, setState] = useState<HubConnectionState>(HubConnectionState.Disconnected)
  const invalidateRef = useRef(invalidate)
  const onEventRef = useRef(onEvent)
  useEffect(() => {
    invalidateRef.current = invalidate
    onEventRef.current = onEvent
  })

  useEffect(() => {
    if (!outletId) return
    let disposed = false
    const connection: HubConnection = new HubConnectionBuilder()
      .withUrl('/hubs/outlet', { accessTokenFactory: async () => (await accessToken()) ?? '' })
      .withAutomaticReconnect([0, 2000, 5000, 10000, 30000])
      .configureLogging(LogLevel.Warning)
      .build()

    const events: RealtimeEvent[] = ['kotChanged', 'orderChanged', 'qrOrderPlaced', 'stockChanged', 'externalOrderPlaced', 'paymentIntentChanged']
    for (const event of events) {
      connection.on(event, (payload: unknown) => {
        for (const key of invalidateRef.current[event] ?? []) void queryClient.invalidateQueries({ queryKey: key })
        onEventRef.current?.(event, payload)
      })
    }

    const join = () => connection.invoke('JoinOutlet', outletId).catch(() => undefined)
    connection.onreconnecting(() => setState(HubConnectionState.Reconnecting))
    connection.onreconnected(() => {
      setState(HubConnectionState.Connected)
      void join()
      // Events may have been missed while offline.
      for (const keys of Object.values(invalidateRef.current)) keys?.forEach((k) => void queryClient.invalidateQueries({ queryKey: k }))
    })
    connection.onclose(() => setState(HubConnectionState.Disconnected))

    const started = connection.start()
      .then(async () => {
        if (disposed) return
        await join()
        setState(HubConnectionState.Connected)
      })
      .catch(() => setState(HubConnectionState.Disconnected))

    return () => {
      disposed = true
      // Stopping mid-negotiation logs a spurious error, so wait for the start attempt to settle.
      void started.finally(() => connection.stop())
    }
  }, [outletId, queryClient])

  return state
}

export { HubConnectionState }
