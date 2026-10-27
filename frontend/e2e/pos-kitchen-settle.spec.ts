import { expect, test } from '@playwright/test'
import { signup } from './helpers'

test('takeaway order flows from POS to the kitchen display, is settled with split payment and invoiced', async ({ page, context }) => {
  await signup(page)

  const kitchen = await context.newPage()
  await kitchen.goto('/kitchen')
  await expect(kitchen.getByRole('heading', { name: 'Kitchen display' })).toBeVisible()
  await expect(kitchen.getByTestId('kot-card')).toHaveCount(0)

  await page.getByRole('button', { name: 'Takeaway' }).click()
  await expect(page).toHaveURL(/\/pos\/new\?type=Takeaway/)
  const menu = page.getByRole('region', { name: 'Menu items' })
  await menu.getByRole('button', { name: /Veg Spring Roll/ }).click()
  await menu.getByRole('button', { name: /Veg Spring Roll/ }).click()
  await menu.getByRole('button', { name: /Masala Chaas/ }).click()
  await page.getByRole('button', { name: 'Send KOT' }).click()

  await expect(page).toHaveURL(/\/pos\/orders\/[0-9a-f-]+$/)
  // 2 x 180 + 70 = 430, GST 5% = 21.50, rounded to the nearest rupee.
  await expect(page.getByTestId('order-total')).toHaveText('₹452.00')

  // One KOT per station (Main and Bar) arrives on the already-open kitchen display without a reload.
  await expect(kitchen.getByTestId('kot-card')).toHaveCount(2)
  const mainKot = kitchen.getByTestId('kot-card').filter({ hasText: 'Veg Spring Roll' })
  await expect(mainKot).toContainText('Main')
  await mainKot.getByRole('button', { name: 'Start' }).click()
  await expect(kitchen.getByRole('region', { name: 'Preparing' }).getByTestId('kot-card')).toHaveCount(1)
  await kitchen.getByRole('region', { name: 'Preparing' }).getByRole('button', { name: 'Ready' }).click()
  await expect(kitchen.getByRole('region', { name: 'Ready to serve' }).getByTestId('kot-card')).toHaveCount(1)

  await page.getByRole('button', { name: 'Settle ₹452.00' }).click()
  const dialog = page.getByRole('dialog', { name: /Settle order/ })
  await expect(dialog.getByTestId('amount-due')).toHaveText('₹452.00')
  await dialog.getByLabel('Payment 1 amount').fill('200')
  await dialog.getByRole('button', { name: 'Split payment' }).click()
  await dialog.getByLabel('Payment 2 method').selectOption('Upi')
  await dialog.getByLabel('Payment 2 amount').fill('252')
  await dialog.getByRole('button', { name: 'Settle ₹452.00' }).click()

  const invoiceNumber = page.getByTestId('invoice-number')
  await expect(invoiceNumber).toHaveText(/^MAIN\/\d{4}-\d{2}\/000001$/)
  const number = await invoiceNumber.textContent()

  const orderUrl = page.url()
  await page.goto(`${new URL(orderUrl).pathname.replace('/pos/orders/', '/orders/')}/invoice`)
  await expect(page.getByText(number!)).toBeVisible()
  await expect(page.getByText('E2E Bistro')).toBeVisible()
  await expect(page.getByText('₹452.00').first()).toBeVisible()
  await expect(page.getByText('UPI')).toBeVisible()
})
