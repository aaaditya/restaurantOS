import { useQuery, useQueryClient } from '@tanstack/react-query'
import { createContext, useCallback, useContext, useEffect, useMemo, useState, type ReactNode } from 'react'
import type { Me, Permission } from '../types'
import { api, getTokens, onTokensChange, setTokens, type Tokens } from './api'

type Session = {
  me: Me | undefined
  isLoading: boolean
  isAuthenticated: boolean
  outletId: string | null
  outlet: Me['outlets'][number] | undefined
  selectOutlet: (id: string) => void
  can: (permission: Permission) => boolean
  canTenant: (permission: Permission) => boolean
  signIn: (tokens: Tokens) => void
  signOut: () => Promise<void>
}

const SessionContext = createContext<Session | null>(null)
const OUTLET_KEY = 'ros.outlet'

export function SessionProvider({ children }: { children: ReactNode }) {
  const queryClient = useQueryClient()
  const [hasTokens, setHasTokens] = useState(() => getTokens() !== null)
  const [outletId, setOutletId] = useState<string | null>(() => localStorage.getItem(OUTLET_KEY))

  useEffect(() => onTokensChange((t) => {
    setHasTokens(t !== null)
    if (!t) queryClient.clear()
  }), [queryClient])

  const meQuery = useQuery({ queryKey: ['me'], queryFn: () => api<Me>('/api/me'), enabled: hasTokens, staleTime: 60_000 })
  const me = meQuery.data

  const outlet = me?.outlets.find((o) => o.id === outletId) ?? me?.outlets[0]

  const selectOutlet = useCallback((id: string) => {
    setOutletId(id)
    localStorage.setItem(OUTLET_KEY, id)
  }, [])

  const signIn = useCallback((tokens: Tokens) => {
    setTokens(tokens)
    void queryClient.invalidateQueries({ queryKey: ['me'] })
  }, [queryClient])

  const signOut = useCallback(async () => {
    const t = getTokens()
    if (t) await api('/api/auth/logout', { method: 'POST', body: { refreshToken: t.refreshToken }, auth: false }).catch(() => undefined)
    setTokens(null)
  }, [])

  const value = useMemo<Session>(() => ({
    me,
    isLoading: hasTokens && meQuery.isLoading,
    isAuthenticated: hasTokens && !!me,
    outletId: outlet?.id ?? null,
    outlet,
    selectOutlet,
    can: (p) => !!outlet?.permissions.includes(p),
    canTenant: (p) => !!me?.tenantPermissions.includes(p),
    signIn,
    signOut,
  }), [me, hasTokens, meQuery.isLoading, outlet, selectOutlet, signIn, signOut])

  return <SessionContext.Provider value={value}>{children}</SessionContext.Provider>
}

export function useSession() {
  const ctx = useContext(SessionContext)
  if (!ctx) throw new Error('useSession must be used inside SessionProvider')
  return ctx
}

/** Current outlet id; only call inside routes guarded by RequireAuth (an outlet is always selected there). */
export function useOutletId(): string {
  const { outletId } = useSession()
  if (!outletId) throw new Error('No outlet selected')
  return outletId
}
