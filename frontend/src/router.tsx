import type { ComponentType } from 'react'
import type { RouteObject } from 'react-router'
import { Spinner } from './components/ui'
import { LoginPage, SignupPage } from './features/auth/AuthPages'
import { AppLayout, HomeRedirect, RequireAuth } from './features/layout/AppLayout'
import { NotFound } from './features/layout/NotFound'
import { RouteError } from './features/layout/RouteError'

/** Loads a page module on first visit so the initial bundle only carries the shell. */
const page = <M,>(load: () => Promise<M>, pick: (m: M) => ComponentType) => async () => ({ Component: pick(await load()) })

const fallback = <Spinner label="Loading…" />
const errorElement = <RouteError />

export const router: RouteObject[] = [
  { path: '/login', element: <LoginPage />, errorElement },
  { path: '/signup', element: <SignupPage />, errorElement },
  { path: '/qr/:token', hydrateFallbackElement: fallback, errorElement, lazy: page(() => import('./features/qr/GuestOrderPage'), (m) => m.GuestOrderPage) },
  { path: '/pay/mock/:providerRef', hydrateFallbackElement: fallback, errorElement, lazy: page(() => import('./features/payments/MockCheckoutPage'), (m) => m.MockCheckoutPage) },
  {
    element: <RequireAuth />,
    hydrateFallbackElement: fallback,
    errorElement,
    children: [
      { path: '/orders/:orderId/invoice', lazy: page(() => import('./features/orders/InvoicePage'), (m) => m.InvoicePage) },
      {
        element: <AppLayout />,
        children: [
          { errorElement, children: [
            { index: true, element: <HomeRedirect /> },
            { path: '/pos', lazy: page(() => import('./features/pos/PosHome'), (m) => m.PosHome) },
            { path: '/pos/new', lazy: page(() => import('./features/pos/OrderScreen'), (m) => m.OrderScreen) },
            { path: '/pos/orders/:orderId', lazy: page(() => import('./features/pos/OrderScreen'), (m) => m.OrderScreen) },
            { path: '/orders', lazy: page(() => import('./features/orders/OrdersPage'), (m) => m.OrdersPage) },
            { path: '/kitchen', lazy: page(() => import('./features/kitchen/KitchenPage'), (m) => m.KitchenPage) },
            { path: '/cash', lazy: page(() => import('./features/cash/CashPage'), (m) => m.CashPage) },
            { path: '/menu', lazy: page(() => import('./features/menu/MenuPage'), (m) => m.MenuPage) },
            { path: '/tables', lazy: page(() => import('./features/tables/TablesPage'), (m) => m.TablesPage) },
            { path: '/staff', lazy: page(() => import('./features/staff/StaffPage'), (m) => m.StaffPage) },
            { path: '/inventory', lazy: page(() => import('./features/inventory/InventoryPage'), (m) => m.InventoryPage) },
            { path: '/purchasing', lazy: page(() => import('./features/purchasing/PurchasingPage'), (m) => m.PurchasingPage) },
            { path: '/settings', lazy: page(() => import('./features/settings/SettingsPage'), (m) => m.SettingsPage) },
            { path: '/dashboard', lazy: page(() => import('./features/dashboard/DashboardPage'), (m) => m.DashboardPage) },
            { path: '/customers', lazy: page(() => import('./features/customers/CustomersPage'), (m) => m.CustomersPage) },
            { path: '/reports', lazy: page(() => import('./features/reports/ReportsPage'), (m) => m.ReportsPage) },
            { path: '/audit', lazy: page(() => import('./features/audit/AuditPage'), (m) => m.AuditPage) },
            { path: '/billing', lazy: page(() => import('./features/billing/BillingPage'), (m) => m.BillingPage) },
            { path: '/integrations', lazy: page(() => import('./features/integrations/IntegrationsPage'), (m) => m.IntegrationsPage) },
            { path: '*', element: <NotFound /> },
          ] },
        ],
      },
    ],
  },
]
