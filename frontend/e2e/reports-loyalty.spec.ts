import { expect, test, type Page } from '@playwright/test'
import { signup } from './helpers'

async function takeaway(page: Page, phone: string, name: string, rolls: number) {
  await page.getByRole('navigation', { name: 'Main' }).getByRole('link', { name: 'POS' }).click()
  await page.getByRole('button', { name: 'Takeaway' }).click()
  await page.getByLabel('Customer phone').fill(phone)
  await page.getByLabel('Name', { exact: true }).fill(name)
  const menu = page.getByRole('region', { name: 'Menu items' })
  for (let i = 0; i < rolls; i++) await menu.getByRole('button', { name: /Veg Spring Roll/ }).click()
  await page.getByRole('button', { name: 'Send KOT' }).click()
  await expect(page).toHaveURL(/\/pos\/orders\/[0-9a-f-]+$/)
}

test('loyalty is earned and redeemed at settlement, and reports reconcile the day', async ({ page }) => {
  await signup(page)
  const nav = page.getByRole('navigation', { name: 'Main' })

  // 4 x 189 = 756 paid in cash earns floor(756 / 100 x 5) = 37 points.
  await takeaway(page, '9811122233', 'Kiran', 4)
  await page.getByRole('button', { name: 'Settle ₹756.00' }).click()
  let settle = page.getByRole('dialog', { name: /Settle order/ })
  await settle.getByRole('button', { name: 'Settle ₹756.00' }).click()
  await expect(page.getByTestId('invoice-number')).toBeVisible()
  await page.getByRole('dialog', { name: 'Order settled' }).getByRole('button', { name: 'New order' }).click()

  // Redeem ₹9 (36 points at ₹0.25) and pay ₹180 by UPI, earning floor(180 / 100 x 5) = 9 points.
  await takeaway(page, '9811122233', 'Kiran', 1)
  await page.getByRole('button', { name: 'Settle ₹189.00' }).click()
  settle = page.getByRole('dialog', { name: /Settle order/ })
  await expect(settle.getByTestId('loyalty-balance')).toContainText('Kiran: 37 points (worth ₹9.25)')
  await settle.getByLabel('Payment 1 method').selectOption('Loyalty')
  await settle.getByLabel('Payment 1 amount').fill('10')
  await expect(settle.getByText('Loyalty payment exceeds the ₹9.25 available.')).toBeVisible()
  await settle.getByLabel('Payment 1 amount').fill('9')
  await settle.getByRole('button', { name: 'Split payment' }).click()
  await settle.getByLabel('Payment 2 amount').fill('180')
  await settle.getByRole('button', { name: 'Settle ₹189.00' }).click()
  await expect(page.getByTestId('invoice-number')).toBeVisible()
  await page.getByRole('dialog', { name: 'Order settled' }).getByRole('button', { name: 'New order' }).click()

  await nav.getByRole('link', { name: 'Customers' }).click()
  await expect(page.getByRole('row').filter({ hasText: 'Kiran' })).toContainText('10 pts')
  await page.getByRole('button', { name: 'View Kiran' }).click()
  await expect(page.getByTestId('customer-points')).toHaveText('10')
  await page.getByRole('button', { name: 'Adjust points' }).click()
  const adjust = page.getByRole('dialog', { name: 'Adjust points' })
  await adjust.getByLabel('Points to add or remove').fill('5')
  await adjust.getByLabel('Reason').fill('Birthday bonus')
  await adjust.getByRole('button', { name: 'Apply' }).click()
  await expect(page.getByTestId('customer-points')).toHaveText('15')
  await page.getByRole('dialog', { name: 'Kiran' }).getByRole('button', { name: 'Close' }).click()

  await nav.getByRole('link', { name: 'Reports' }).click()
  await expect(page.getByTestId('net-sales')).toHaveText('₹900.00')
  await expect(page.getByTestId('total-collected')).toHaveText('₹945.00')
  await expect(page.getByTestId('bill-count')).toHaveText('2')
  const payments = page.getByRole('region', { name: 'Payments', exact: true })
  await expect(payments.getByRole('row').filter({ hasText: 'Cash' })).toContainText('₹756.00')
  await expect(payments.getByRole('row').filter({ hasText: 'UPI' })).toContainText('₹180.00')
  await expect(payments.getByRole('row').filter({ hasText: 'Loyalty points' })).toContainText('₹9.00')

  await page.getByRole('tab', { name: 'Items & categories' }).click()
  await expect(page.getByRole('row').filter({ hasText: 'Veg Spring Roll' })).toContainText('₹900.00')
  await page.getByRole('tab', { name: 'Tax summary' }).click()
  await expect(page.getByRole('row').filter({ hasText: 'CGST' })).toContainText('₹22.50')
  await page.getByRole('tab', { name: 'Day-end reconciliation' }).click()
  await expect(page.getByTestId('day-sales')).toHaveText('₹945.00')
  await expect(page.getByTestId('day-payments')).toHaveText('₹945.00')
  await expect(page.getByTestId('day-end-checks')).toContainText('Payments match settled bills.')

  await nav.getByRole('link', { name: 'Dashboard' }).click()
  await expect(page.getByTestId('dash-collected')).toHaveText('₹945.00')
  await expect(page.getByTestId('dash-orders')).toHaveText('2')

  await nav.getByRole('link', { name: 'Audit log' }).click()
  await page.getByLabel('Area').selectOption({ label: 'Customers' })
  await expect(page.getByRole('row').filter({ hasText: 'customer.loyalty_adjusted' })).toContainText('Birthday bonus')
})
