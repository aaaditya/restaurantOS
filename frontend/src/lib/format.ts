const inr = new Intl.NumberFormat('en-IN', { style: 'currency', currency: 'INR', minimumFractionDigits: 2 })
const plain = new Intl.NumberFormat('en-IN', { maximumFractionDigits: 3 })

export const money = (value: number | null | undefined) => inr.format(value ?? 0)
export const qty = (value: number) => plain.format(value)

export function time(iso: string | null | undefined) {
  if (!iso) return ''
  return new Date(iso).toLocaleTimeString('en-IN', { hour: '2-digit', minute: '2-digit' })
}

export function dateTime(iso: string | null | undefined) {
  if (!iso) return ''
  return new Date(iso).toLocaleString('en-IN', { day: '2-digit', month: 'short', hour: '2-digit', minute: '2-digit' })
}

export function minutesSince(iso: string, now = Date.now()) {
  return Math.max(0, Math.floor((now - new Date(iso).getTime()) / 60000))
}

/** Local calendar date (yyyy-mm-dd) in the browser's time zone. */
export function today() {
  const d = new Date()
  return new Date(d.getTime() - d.getTimezoneOffset() * 60000).toISOString().slice(0, 10)
}

export const orderTypeLabel = { DineIn: 'Dine-in', Takeaway: 'Takeaway', Delivery: 'Delivery' } as const

export const paymentLabel: Record<string, string> = {
  Cash: 'Cash', Card: 'Card', Upi: 'UPI', Wallet: 'Wallet', Online: 'Online', Loyalty: 'Loyalty points', Aggregator: 'Aggregator',
}
