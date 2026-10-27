import { expect, test } from '@playwright/test'
import { signup } from './helpers'

test('guest orders from a table QR code, staff accept it live and the guest tracks it to completion', async ({ page, browser }) => {
  await signup(page)
  const nav = page.getByRole('navigation', { name: 'Main' })

  await nav.getByRole('link', { name: 'Tables & QR' }).click()
  await page.getByRole('button', { name: 'New table' }).click()
  const tableDialog = page.getByRole('dialog', { name: 'New table' })
  await tableDialog.getByLabel('Name').fill('T1')
  await tableDialog.getByRole('button', { name: 'Save' }).click()
  await page.getByRole('button', { name: 'QR link' }).click()
  const qrUrl = (await page.getByTestId('qr-url').textContent())!.trim()
  expect(qrUrl).toMatch(/\/qr\/[\w-]+$/)
  await page.getByRole('dialog', { name: /QR ordering/ }).getByRole('button', { name: 'Close' }).click()
  await nav.getByRole('link', { name: 'POS' }).click()

  // The guest is anonymous: a separate browser context with no session.
  const guestContext = await browser.newContext()
  const guest = await guestContext.newPage()
  await guest.goto(qrUrl)
  await expect(guest.getByText('Table T1')).toBeVisible()
  await guest.getByRole('button', { name: 'Add Veg Spring Roll' }).click()
  await guest.getByRole('button', { name: 'Add Veg Spring Roll' }).click()
  await guest.getByRole('button', { name: 'Beverages' }).click()
  await guest.getByRole('button', { name: 'Add Masala Chaas' }).click()
  await guest.getByRole('button', { name: /Review order · ₹430\.00/ }).click()
  const checkout = guest.getByRole('dialog', { name: 'Your order' })
  await checkout.getByLabel('Your name (optional)').fill('Neha')
  await checkout.getByLabel('Phone (optional)').fill('98765 00000')
  await checkout.getByRole('button', { name: 'Place order · ₹430.00' }).click()
  const tracker = guest.getByTestId('guest-order').first()
  await expect(tracker).toContainText('Waiting for the restaurant to confirm')
  await expect(tracker).toContainText('2 × Veg Spring Roll, 1 × Masala Chaas')

  // Staff see the order arrive without reloading and accept it.
  const pending = page.getByRole('region', { name: 'Awaiting acceptance (1)' })
  await expect(pending).toBeVisible()
  await pending.getByRole('button', { name: 'Accept' }).click()
  await expect(page.getByText(/accepted and sent to kitchen/)).toBeVisible()
  await expect(page.getByRole('button', { name: /^Table T1/ })).toContainText('₹452.00')

  await guest.reload()
  await expect(guest.getByTestId('guest-order').first()).toContainText('Confirmed — being prepared')
  await expect(guest.getByTestId('guest-order').first()).toContainText('Total ₹452.00')

  await page.getByRole('button', { name: /^Table T1/ }).click()
  await page.getByRole('button', { name: 'Settle ₹452.00' }).click()
  const settle = page.getByRole('dialog', { name: /Settle order/ })
  await expect(settle.getByTestId('loyalty-balance')).toContainText('Neha: 0 points')
  await settle.getByRole('button', { name: 'Settle ₹452.00' }).click()
  await expect(page.getByTestId('invoice-number')).toBeVisible()
  await page.getByRole('dialog', { name: 'Order settled' }).getByRole('button', { name: 'New order' }).click()

  await guest.reload()
  await expect(guest.getByTestId('guest-order').first()).toContainText('Completed')

  // floor(452 / 100 x 5) = 22 points earned on the guest's phone number.
  await nav.getByRole('link', { name: 'Customers' }).click()
  await expect(page.getByRole('row').filter({ hasText: 'Neha' })).toContainText('22 pts')
  await guestContext.close()
})

test('an invalid QR token shows a helpful message', async ({ page }) => {
  await page.goto('/qr/not-a-real-token')
  await expect(page.getByRole('heading', { name: 'This QR code is no longer valid' })).toBeVisible()
})
