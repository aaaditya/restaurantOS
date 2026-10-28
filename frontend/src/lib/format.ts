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

/** Calendar date (yyyy-mm-dd) in the given IANA time zone, or the browser's when omitted. */
export function today(timeZone?: string) {
  // en-CA formats dates as yyyy-mm-dd.
  return new Intl.DateTimeFormat('en-CA', { timeZone, year: 'numeric', month: '2-digit', day: '2-digit' }).format(new Date())
}

export const orderTypeLabel = { DineIn: 'Dine-in', Takeaway: 'Takeaway', Delivery: 'Delivery' } as const

export const paymentLabel: Record<string, string> = {
  Cash: 'Cash', Card: 'Card', Upi: 'UPI', Wallet: 'Wallet', Online: 'Online', Loyalty: 'Loyalty points', Aggregator: 'Aggregator',
}
