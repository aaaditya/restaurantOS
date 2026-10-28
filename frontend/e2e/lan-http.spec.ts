import { expect, test } from '@playwright/test'
import { signup } from './helpers'

// Devices on the LAN open the app over plain HTTP (for example http://192.168.1.6:5173), which is not a
// secure context, so browsers do not provide crypto.randomUUID there.
test.beforeEach(async ({ page }) => {
  await page.addInitScript(() => {
    Object.defineProperty(Crypto.prototype, 'randomUUID', { value: undefined, configurable: true })
  })
})

test('POS works without crypto.randomUUID (plain-HTTP LAN access)', async ({ page }) => {
  expect(await page.evaluate(() => typeof crypto.randomUUID)).toBe('undefined')
  await signup(page)

  await page.getByRole('button', { name: 'Takeaway' }).click()
  const menu = page.getByRole('region', { name: 'Menu items' })
  await menu.getByRole('button', { name: /Chicken Tikka/ }).click()
  const configure = page.getByRole('dialog', { name: 'Chicken Tikka' })
  await configure.getByLabel('Medium').check()
  await configure.getByRole('button', { name: 'Add · ₹320.00' }).click()
  await expect(configure).toHaveCount(0)
  await menu.getByRole('button', { name: /Veg Spring Roll/ }).click()
  await page.getByRole('button', { name: 'Send KOT' }).click()
  await expect(page).toHaveURL(/\/pos\/orders\/[0-9a-f-]+$/)

  await page.getByRole('button', { name: /^Settle ₹/ }).click()
  const settle = page.getByRole('dialog', { name: /Settle order/ })
  await settle.getByRole('button', { name: /^Settle ₹/ }).click()
  await expect(page.getByTestId('invoice-number')).toBeVisible()
  await page.getByRole('dialog', { name: 'Order settled' }).getByRole('button', { name: 'New order' }).click()
  await expect(page).toHaveURL(/\/pos$/)
})
