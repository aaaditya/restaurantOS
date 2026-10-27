import { expect, test, type Page } from '@playwright/test'
import { signup } from './helpers'

async function stockOf(page: Page, name: string) {
  return page.getByRole('row').filter({ hasText: name }).getByRole('cell').nth(1)
}

test('ingredients, recipe, purchase receipt and a sale keep stock correct', async ({ page }) => {
  await signup(page)
  const nav = page.getByRole('navigation', { name: 'Main' })
  await nav.getByRole('link', { name: 'Inventory' }).click()

  await page.getByRole('tab', { name: 'Ingredients' }).click()
  await page.getByRole('button', { name: 'New ingredient' }).click()
  let dialog = page.getByRole('dialog', { name: 'New ingredient' })
  await dialog.getByLabel('Name').fill('Spring roll sheets')
  await dialog.getByLabel('Unit', { exact: true }).selectOption('Piece')
  await dialog.getByLabel('Reorder level').fill('10')
  await dialog.getByLabel('Cost per unit (₹)').fill('4')
  await dialog.getByRole('button', { name: 'Save' }).click()
  await expect(page.getByRole('cell', { name: 'Spring roll sheets', exact: true })).toBeVisible()

  await page.getByRole('tab', { name: 'Recipes' }).click()
  await page.getByRole('row').filter({ hasText: 'Veg Spring Roll' }).getByRole('button', { name: 'Add recipe' }).click()
  dialog = page.getByRole('dialog', { name: /Recipe · Veg Spring Roll/ })
  await dialog.getByLabel('Ingredient', { exact: true }).selectOption({ label: 'Spring roll sheets' })
  await dialog.getByLabel(/Quantity per unit sold/).fill('3')
  await dialog.getByRole('button', { name: 'Save recipe' }).click()
  await expect(page.getByRole('row').filter({ hasText: 'Veg Spring Roll' })).toContainText('Spring roll sheets 3 pc')

  await page.getByRole('tab', { name: 'Stock' }).click()
  await page.getByRole('button', { name: 'Add stock' }).click()
  dialog = page.getByRole('dialog', { name: 'Add stock' })
  await dialog.getByLabel('Ingredient', { exact: true }).selectOption({ label: 'Spring roll sheets' })
  await dialog.getByLabel(/Quantity/).fill('12')
  await dialog.getByRole('button', { name: 'Add stock' }).click()
  await expect(await stockOf(page, 'Spring roll sheets')).toHaveText('12 pc')

  await nav.getByRole('link', { name: 'Purchasing' }).click()
  await page.getByRole('tab', { name: 'Suppliers' }).click()
  await page.getByRole('button', { name: 'New supplier' }).click()
  dialog = page.getByRole('dialog', { name: 'New supplier' })
  await dialog.getByLabel('Name').fill('Wrap Traders')
  await dialog.getByRole('button', { name: 'Save' }).click()
  await expect(page.getByRole('cell', { name: 'Wrap Traders', exact: true })).toBeVisible()

  await page.getByRole('tab', { name: 'Purchase orders' }).click()
  await page.getByRole('button', { name: 'New purchase order' }).click()
  dialog = page.getByRole('dialog', { name: 'New purchase order' })
  await dialog.getByLabel('Supplier', { exact: true }).selectOption({ label: 'Wrap Traders' })
  await dialog.getByLabel('Ingredient', { exact: true }).selectOption({ label: 'Spring roll sheets' })
  await dialog.getByLabel(/Quantity/).fill('50')
  await expect(dialog.getByLabel('Unit cost (₹)')).toHaveValue('4')
  await dialog.getByRole('button', { name: 'Create draft' }).click()

  dialog = page.getByRole('dialog', { name: 'PO-1 · Wrap Traders' })
  await expect(dialog).toContainText('₹200.00')
  // While the status change is in flight, other actions are disabled so its completion cannot reset them.
  let release!: () => void
  const held = new Promise<void>((r) => { release = r })
  await page.route('**/purchase-orders/*/order', async (route) => { await held; await route.continue() })
  await dialog.getByRole('button', { name: 'Mark as ordered' }).click()
  await expect(dialog.getByRole('button', { name: 'Receive goods' })).toBeDisabled()
  release()
  await expect(dialog.getByRole('button', { name: 'Mark as ordered' })).toHaveCount(0)
  await page.unroute('**/purchase-orders/*/order')
  await expect(dialog).toContainText('Ordered')
  await dialog.getByRole('button', { name: 'Receive goods' }).click()
  await dialog.getByLabel('Received quantity for Spring roll sheets').fill('48')
  await dialog.getByLabel('Unit cost for Spring roll sheets').fill('4.5')
  await dialog.getByRole('button', { name: 'Confirm receipt' }).click()
  await expect(dialog).toContainText('Received')
  await expect(dialog).toContainText('₹216.00')
  await dialog.getByRole('button', { name: 'Close' }).click()

  await nav.getByRole('link', { name: 'POS' }).click()
  await page.getByRole('button', { name: 'Takeaway' }).click()
  const menu = page.getByRole('region', { name: 'Menu items' })
  await menu.getByRole('button', { name: /Veg Spring Roll/ }).click()
  await menu.getByRole('button', { name: /Veg Spring Roll/ }).click()
  await page.getByRole('button', { name: 'Send KOT' }).click()
  await page.getByRole('button', { name: /^Settle ₹/ }).click()
  const settle = page.getByRole('dialog', { name: /Settle order/ })
  await settle.getByRole('button', { name: /^Settle ₹/ }).click()
  await expect(page.getByTestId('invoice-number')).toBeVisible()

  // 12 opening + 48 received - 2 rolls x 3 sheets = 54, valued at the latest cost 4.50.
  await page.goto('/inventory')
  await expect(await stockOf(page, 'Spring roll sheets')).toHaveText('54 pc')
  await expect(page.getByTestId('stock-value')).toHaveText('₹243.00')
  await page.getByRole('tab', { name: 'Movements' }).click()
  await expect(page.getByRole('row').filter({ hasText: 'Sale' })).toContainText('-6 pc')
  await expect(page.getByRole('row').filter({ hasText: 'Purchase' })).toContainText('+48 pc')
})
