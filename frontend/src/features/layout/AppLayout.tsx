import clsx from 'clsx'
import {
  BarChart3, Boxes, ChefHat, ClipboardList, CreditCard, LayoutDashboard, LogOut, Menu as MenuIcon, NotebookTabs, Settings,
  ShieldCheck, ShoppingCart, Truck, Users, UtensilsCrossed, Wallet, X, type LucideIcon,
} from 'lucide-react'
import { useState } from 'react'
import { Navigate, NavLink, Outlet, useLocation } from 'react-router'
import { useSession } from '../../app/session'
import { Spinner } from '../../components/ui'
import type { Permission } from '../../types'

type NavItem = { to: string; label: string; icon: LucideIcon; permission: Permission; tenant?: boolean }

export const NAV: NavItem[] = [
  { to: '/dashboard', label: 'Dashboard', icon: LayoutDashboard, permission: 'ReportsView' },
  { to: '/pos', label: 'POS', icon: ShoppingCart, permission: 'OrderCreate' },
  { to: '/orders', label: 'Orders', icon: ClipboardList, permission: 'OrderCreate' },
  { to: '/kitchen', label: 'Kitchen display', icon: ChefHat, permission: 'KitchenView' },
  { to: '/cash', label: 'Cash register', icon: Wallet, permission: 'CashSessionManage' },
  { to: '/menu', label: 'Menu', icon: UtensilsCrossed, permission: 'MenuView' },
  { to: '/tables', label: 'Tables & QR', icon: NotebookTabs, permission: 'TablesManage' },
  { to: '/inventory', label: 'Inventory', icon: Boxes, permission: 'InventoryView' },
  { to: '/purchasing', label: 'Purchasing', icon: Truck, permission: 'PurchaseManage' },
  { to: '/customers', label: 'Customers', icon: Users, permission: 'CustomerView' },
  { to: '/reports', label: 'Reports', icon: BarChart3, permission: 'ReportsView' },
  { to: '/staff', label: 'Staff', icon: ShieldCheck, permission: 'StaffManage', tenant: true },
  { to: '/billing', label: 'Billing', icon: CreditCard, permission: 'BillingManage', tenant: true },
  { to: '/settings', label: 'Settings', icon: Settings, permission: 'OutletManage' },
]

export function useNavItems() {
  const { can, canTenant } = useSession()
  return NAV.filter((n) => (n.tenant ? canTenant(n.permission) : can(n.permission)) && ENABLED_ROUTES.has(n.to))
}

/** Routes that are implemented; extended as modules ship. */
export const ENABLED_ROUTES = new Set<string>(['/pos', '/orders', '/kitchen', '/cash', '/menu', '/tables', '/staff', '/settings'])

export function RequireAuth() {
  const { isAuthenticated, isLoading, me } = useSession()
  const location = useLocation()
  if (isLoading) return <Spinner label="Loading your workspace…" />
  if (!isAuthenticated) return <Navigate to="/login" replace state={{ from: location.pathname }} />
  if (me && me.outlets.length === 0) {
    return <div className="p-8 text-center text-slate-600">Your account has no outlet access yet. Ask an owner to assign you a role.</div>
  }
  return <Outlet />
}

export function HomeRedirect() {
  const items = useNavItems()
  return <Navigate to={items[0]?.to ?? '/pos'} replace />
}

export function AppLayout() {
  const { me, outlet, selectOutlet, signOut } = useSession()
  const items = useNavItems()
  const [mobileOpen, setMobileOpen] = useState(false)
  const trialEnds = me?.tenant.subscriptionStatus === 'Trialing' && me.tenant.trialEndsAt ? new Date(me.tenant.trialEndsAt) : null

  const nav = (
    <nav aria-label="Main" className="flex flex-1 flex-col gap-1 overflow-y-auto px-3 py-4">
      {items.map((item) => (
        <NavLink key={item.to} to={item.to} onClick={() => setMobileOpen(false)}
          className={({ isActive }) => clsx('flex items-center gap-3 rounded-lg px-3 py-2 text-sm font-medium transition-colors',
            isActive ? 'bg-brand-700 text-white' : 'text-slate-300 hover:bg-slate-800 hover:text-white')}>
          <item.icon className="size-4" aria-hidden /> {item.label}
        </NavLink>
      ))}
    </nav>
  )

  return (
    <div className="flex min-h-screen">
      <aside className={clsx('no-print fixed inset-y-0 left-0 z-40 flex w-64 flex-col bg-slate-900 transition-transform lg:translate-x-0',
        mobileOpen ? 'translate-x-0' : '-translate-x-full')}>
        <div className="flex items-center justify-between px-5 py-4">
          <span className="text-lg font-semibold text-white">RestaurantOS</span>
          <button className="text-slate-400 lg:hidden" onClick={() => setMobileOpen(false)} aria-label="Close menu"><X className="size-5" /></button>
        </div>
        <div className="px-4">
          <label htmlFor="outlet-switcher" className="mb-1 block text-xs font-medium uppercase tracking-wide text-slate-400">Outlet</label>
          <select id="outlet-switcher" value={outlet?.id ?? ''} onChange={(e) => selectOutlet(e.target.value)}
            className="w-full rounded-lg border-0 bg-slate-800 px-3 py-2 text-sm text-white ring-1 ring-slate-700 focus:ring-2 focus:ring-brand-500">
            {me?.outlets.map((o) => <option key={o.id} value={o.id}>{o.name}</option>)}
          </select>
        </div>
        {nav}
        <div className="border-t border-slate-800 px-4 py-4">
          <p className="truncate text-sm font-medium text-white">{me?.user.name}</p>
          <p className="truncate text-xs text-slate-400">{me?.tenant.name} · {outlet?.roles.join(', ')}</p>
          <button onClick={() => void signOut()} className="mt-3 flex items-center gap-2 text-sm text-slate-300 hover:text-white">
            <LogOut className="size-4" aria-hidden /> Sign out
          </button>
        </div>
      </aside>
      {mobileOpen && <div className="fixed inset-0 z-30 bg-slate-900/50 lg:hidden" onClick={() => setMobileOpen(false)} aria-hidden />}

      <div className="flex min-w-0 flex-1 flex-col lg:pl-64">
        <header className="no-print sticky top-0 z-20 flex items-center gap-3 border-b border-slate-200 bg-white/90 px-4 py-2 backdrop-blur lg:hidden">
          <button onClick={() => setMobileOpen(true)} aria-label="Open menu" className="rounded-md p-1.5 hover:bg-slate-100"><MenuIcon className="size-5" /></button>
          <span className="font-semibold">{outlet?.name}</span>
        </header>
        {me && !me.tenant.subscriptionUsable && (
          <div className="no-print bg-rose-600 px-4 py-2 text-center text-sm text-white" role="alert">
            Your subscription is inactive. The workspace is read-only until it is renewed.
          </div>
        )}
        {trialEnds && me?.tenant.subscriptionUsable && (
          <div className="no-print bg-amber-50 px-4 py-1.5 text-center text-xs text-amber-800">
            Free trial ends {trialEnds.toLocaleDateString('en-IN', { day: 'numeric', month: 'short' })}
          </div>
        )}
        <main className="flex-1 p-4 sm:p-6 lg:p-8"><Outlet /></main>
      </div>
    </div>
  )
}
