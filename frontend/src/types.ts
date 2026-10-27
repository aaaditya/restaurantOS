export type Permission =
  | 'OutletView' | 'OutletManage' | 'StaffManage' | 'MenuView' | 'MenuManage' | 'TablesManage' | 'OrderCreate'
  | 'OrderDiscount' | 'OrderVoidItem' | 'OrderCancel' | 'OrderSettle' | 'KitchenView' | 'KitchenUpdate'
  | 'CashSessionManage' | 'InventoryView' | 'InventoryManage' | 'PurchaseManage' | 'CustomerView' | 'CustomerManage'
  | 'ReportsView' | 'AuditView' | 'BillingManage' | 'IntegrationsManage'

export type Role = 'Owner' | 'Manager' | 'Cashier' | 'Waiter' | 'Kitchen'
export const ROLES: Role[] = ['Owner', 'Manager', 'Cashier', 'Waiter', 'Kitchen']

export type Me = {
  user: { id: string; name: string; email: string }
  tenant: { id: string; name: string; planCode: string; subscriptionStatus: string; trialEndsAt: string | null; subscriptionUsable: boolean }
  tenantRoles: Role[]
  tenantPermissions: Permission[]
  outlets: { id: string; name: string; code: string; roles: Role[]; permissions: Permission[] }[]
}

export type Outlet = {
  id: string; name: string; code: string; address: string | null; phone: string | null; gstin: string | null; currency: string
  timeZone: string; pricesIncludeTax: boolean; rounding: 'None' | 'NearestRupee'; invoicePrefix: string
  loyaltyPointsPer100: number; loyaltyPointValue: number; qrOrderingEnabled: boolean; isActive: boolean
}

export type FoodType = 'Veg' | 'NonVeg' | 'Egg' | 'NotApplicable'

export type PosMenu = {
  categories: { id: string; name: string }[]
  items: PosMenuItem[]
}

export type PosMenuItem = {
  id: string; categoryId: string; name: string; description: string | null; foodType: FoodType; station: string; price: number
  basePrice: number; priceOverride: number | null; isAvailable: boolean; taxRate: number
  variants: { id: string; name: string; price: number }[]
  modifierGroups: { id: string; name: string; minSelect: number; maxSelect: number; options: { id: string; name: string; price: number }[] }[]
}

export type OrderType = 'DineIn' | 'Takeaway' | 'Delivery'
export type OrderStatus = 'AwaitingAcceptance' | 'Open' | 'Billed' | 'Settled' | 'Cancelled'
export type LineStatus = 'Pending' | 'Sent' | 'Voided'
export type PaymentMethod = 'Cash' | 'Card' | 'Upi' | 'Wallet' | 'Online' | 'Loyalty' | 'Aggregator'

export type OrderLine = {
  id: string; menuItemId: string; variantId: string | null; itemName: string; variantName: string | null; station: string
  unitPrice: number; quantity: number; modifiers: { optionId: string; groupName: string; name: string; price: number }[]
  notes: string | null; status: LineStatus; voidReason: string | null; gross: number; discount: number; taxable: number
  taxTotal: number; total: number
}

export type Order = {
  id: string; outletId: string; orderNumber: number; type: OrderType; source: 'Pos' | 'Qr' | 'Aggregator'; status: OrderStatus
  tableId: string | null; tableName: string | null; customerId: string | null; customerName: string | null; customerPhone: string | null
  deliveryAddress: string | null; guests: number; notes: string | null; externalRef: string | null; pricesIncludeTax: boolean
  discountType: 'Percent' | 'Flat' | null; discountValue: number; discountReason: string | null
  subtotal: number; discountTotal: number; taxTotal: number; roundOff: number; grandTotal: number; paidTotal: number
  taxBreakdown: { code: string; ratePercent: number; amount: number }[]
  invoiceNumber: string | null; businessDate: string | null; createdAt: string; billedAt: string | null; settledAt: string | null
  cancelledAt: string | null; cancelReason: string | null; version: number; lines: OrderLine[]
}

export type OrderSummary = Pick<Order, 'id' | 'orderNumber' | 'type' | 'source' | 'status' | 'tableId' | 'tableName' | 'customerName'
  | 'customerPhone' | 'grandTotal' | 'invoiceNumber' | 'createdAt' | 'settledAt' | 'version'> & { itemCount: number; pendingItems: number }

export type Table = {
  id: string; name: string; area: string; capacity: number; sortOrder: number; isActive: boolean; qrToken: string
  status: 'free' | 'occupied' | 'billed'
  currentOrder: { id: string; status: OrderStatus; grandTotal: number; createdAt: string; orderNumber: number } | null
}

export type KotStatus = 'New' | 'Preparing' | 'Ready' | 'Served' | 'Cancelled'
export type Kot = {
  id: string; orderId: string; orderNumber: number; kotNumber: string; kind: 'Normal' | 'Cancellation'; station: string
  status: KotStatus; tableName: string | null; orderTypeLabel: string; createdAt: string; startedAt: string | null; readyAt: string | null
  items: { id: string; name: string; variantName: string | null; modifiersText: string | null; quantity: number; notes: string | null }[]
}

export type SettlementResult = { settlementId: string; orderId: string; invoiceNumber: string; amount: number; changeDue: number; replayed: boolean }

export type Invoice = {
  business: { name: string; outlet: string; address: string | null; phone: string | null; gstin: string | null }
  order: Order
  payments: { method: PaymentMethod; amount: number; tendered: number | null; reference: string | null }[]
}

export type TaxGroup = { id: string; name: string; isActive: boolean; components: { id: string; code: string; ratePercent: number }[] }
export type Category = { id: string; name: string; sortOrder: number; isActive: boolean }
export type ModifierGroup = {
  id: string; name: string; minSelect: number; maxSelect: number; isActive: boolean
  options: { id: string; name: string; price: number; isActive: boolean; sortOrder: number }[]
}
export type MenuItem = {
  id: string; categoryId: string; name: string; description: string | null; sku: string | null; price: number; taxGroupId: string | null
  foodType: FoodType; station: string; sortOrder: number; isActive: boolean
  variants: { id: string; name: string; price: number; isActive: boolean }[]
  modifierGroupIds: string[]
}

export type Staff = { id: string; name: string; email: string; isActive: boolean; lastLoginAt: string | null; roles: { role: Role; outletId: string | null }[] }

export type CashSession = {
  id: string; status: 'Open' | 'Closed'; openingFloat: number; expectedCash: number; countedCash: number | null; variance: number | null
  openedAt: string; closedAt: string | null; notes: string | null
}

export type StockUnit = 'Gram' | 'Kilogram' | 'Millilitre' | 'Litre' | 'Piece'
export const STOCK_UNITS: { value: StockUnit; label: string; short: string }[] = [
  { value: 'Kilogram', label: 'Kilogram', short: 'kg' }, { value: 'Gram', label: 'Gram', short: 'g' },
  { value: 'Litre', label: 'Litre', short: 'L' }, { value: 'Millilitre', label: 'Millilitre', short: 'ml' },
  { value: 'Piece', label: 'Piece', short: 'pc' },
]

export type InventoryItem = { id: string; name: string; sku: string | null; unit: StockUnit; reorderLevel: number; costPerUnit: number; isActive: boolean }

export type StockRow = {
  inventoryItemId: string; name: string; sku: string | null; unit: StockUnit; reorderLevel: number; costPerUnit: number
  quantity: number; value: number; isLow: boolean; updatedAt: string | null
}
export type StockReport = { items: StockRow[]; totalValue: number; lowCount: number }

export type MovementType = 'Opening' | 'Purchase' | 'Consumption' | 'Wastage' | 'Adjustment' | 'ConsumptionReversal'
export type LedgerEntry = {
  id: string; inventoryItemId: string; itemName: string; unit: StockUnit; type: MovementType; quantity: number; unitCost: number
  balanceAfter: number; sourceType: string; sourceId: string; notes: string | null; createdAt: string
}

export type Recipe = { id: string; menuItemId: string; variantId: string | null; lines: { id: string; inventoryItemId: string; quantity: number }[] }

export type Supplier = {
  id: string; name: string; contactName: string | null; phone: string | null; email: string | null; gstin: string | null
  address: string | null; isActive: boolean
}

export type PoStatus = 'Draft' | 'Ordered' | 'Received' | 'Cancelled'
export type PurchaseOrderSummary = {
  id: string; number: number; status: PoStatus; supplierId: string; supplierName: string; total: number; createdAt: string
  orderedAt: string | null; receivedAt: string | null; lineCount: number
}
export type PurchaseOrder = {
  id: string; outletId: string; number: number; status: PoStatus; supplierId: string; supplierName: string | null; notes: string | null
  total: number; createdAt: string; orderedAt: string | null; receivedAt: string | null
  lines: { id: string; inventoryItemId: string; itemName: string | null; unit: StockUnit | null; quantity: number; unitCost: number; receivedQuantity: number | null; amount: number }[]
}
