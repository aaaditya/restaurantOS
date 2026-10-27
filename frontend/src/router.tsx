import type { RouteObject } from 'react-router'
import { LoginPage, SignupPage } from './features/auth/AuthPages'
import { KitchenPage } from './features/kitchen/KitchenPage'
import { AppLayout, HomeRedirect, RequireAuth } from './features/layout/AppLayout'
import { InvoicePage } from './features/orders/InvoicePage'
import { OrdersPage } from './features/orders/OrdersPage'
import { OrderScreen } from './features/pos/OrderScreen'
import { PosHome } from './features/pos/PosHome'
import { NotFound } from './features/layout/NotFound'
import { CashPage } from './features/cash/CashPage'
import { MenuPage } from './features/menu/MenuPage'
import { SettingsPage } from './features/settings/SettingsPage'
import { StaffPage } from './features/staff/StaffPage'
import { TablesPage } from './features/tables/TablesPage'

export const router: RouteObject[] = [
  { path: '/login', element: <LoginPage /> },
  { path: '/signup', element: <SignupPage /> },
  {
    element: <RequireAuth />,
    children: [
      { path: '/orders/:orderId/invoice', element: <InvoicePage /> },
      {
        element: <AppLayout />,
        children: [
          { index: true, element: <HomeRedirect /> },
          { path: '/pos', element: <PosHome /> },
          { path: '/pos/new', element: <OrderScreen /> },
          { path: '/pos/orders/:orderId', element: <OrderScreen /> },
          { path: '/orders', element: <OrdersPage /> },
          { path: '/kitchen', element: <KitchenPage /> },
          { path: '/cash', element: <CashPage /> },
          { path: '/menu', element: <MenuPage /> },
          { path: '/tables', element: <TablesPage /> },
          { path: '/staff', element: <StaffPage /> },
          { path: '/settings', element: <SettingsPage /> },
          { path: '*', element: <NotFound /> },
        ],
      },
    ],
  },
]
