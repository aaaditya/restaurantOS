import { expect, test } from '@playwright/test'
import { signup } from './helpers'

type MenuResponse = { items: { id: string; name: string }[] }

test('owner upgrades, connects a partner by API key, accepts its order and settles it by payment link', async ({ page, context }) => {
  await signup(page)
  const nav = page.getByRole('navigation', { name: 'Main' })

  await nav.getByRole('link', { name: 'Integrations' }).click()
  await expect(page.getByRole('note')).toContainText('available on the Growth and Scale plans')
  await page.getByRole('link', { name: 'Change plan' }).click()

  await expect(page.getByTestId('current-plan')).toHaveText('Starter')
  await page.getByRole('region', { name: 'Growth plan' }).getByRole('button', { name: 'Switch to Growth' }).click()
  await page.getByRole('dialog', { name: 'Subscribe to Growth' }).getByRole('button', { name: 'Confirm' }).click()
  await expect(page.getByTestId('current-plan')).toHaveText('Growth')
  await expect(page.getByRole('region', { name: 'Growth plan' }).getByText('Current plan', { exact: true }).first()).toBeVisible()

  await nav.getByRole('link', { name: 'Integrations' }).click()
  await page.getByRole('button', { name: 'Create API key' }).click()
  const create = page.getByRole('dialog', { name: 'Create API key' })
  await create.getByLabel('Name').fill('SwiftEats')
  await create.getByRole('button', { name: 'Create key' }).click()
  const key = await page.getByTestId('new-api-key').inputValue()
  expect(key).toMatch(/^ros_[0-9a-f]{10}\./)
  await page.getByRole('dialog', { name: 'Your new API key' }).getByRole('button', { name: 'Done' }).click()
  await expect(page.getByRole('row').filter({ hasText: 'SwiftEats' })).toContainText('Active')
  const outletId = await page.locator('#outlet-switcher').inputValue()

  // The partner reads the menu and pushes an order; a retry of the same external id must not duplicate it.
  const headers = { 'X-Api-Key': key }
  const menu = await (await page.request.get(`/api/v1/outlets/${outletId}/menu`, { headers })).json() as MenuResponse
  const roll = menu.items.find((i) => i.name === 'Veg Spring Roll')!
  const payload = {
    provider: 'swifteats', externalId: 'SE-1001', type: 'Delivery', prepaid: false, expectedTotal: 378,
    customer: { name: 'Neha', phone: '9822001100', address: '4 Park Street' },
    lines: [{ menuItemId: roll.id, quantity: 2 }],
  }
  const first = await page.request.post(`/api/v1/outlets/${outletId}/orders`, { headers, data: payload })
  expect(first.status()).toBe(201)
  const replay = await page.request.post(`/api/v1/outlets/${outletId}/orders`, { headers, data: payload })
  expect(replay.status()).toBe(200)
  const created = await first.json() as { orderId: string; totalMatches: boolean }
  expect(created.totalMatches).toBe(true)
  expect((await replay.json() as { orderId: string }).orderId).toBe(created.orderId)

  await page.getByRole('button', { name: 'Send test order' }).click()
  await expect(page.getByTestId('simulated-order')).toContainText('SIM-')

  await nav.getByRole('link', { name: 'POS' }).click()
  const pending = page.getByRole('region', { name: /Awaiting acceptance \(2\)/ })
  await expect(pending).toBeVisible()
  const partnerOrder = pending.getByRole('listitem').filter({ hasText: 'swifteats · Delivery · SE-1001' })
  await expect(partnerOrder).toContainText('1 item · ₹378.00 · Neha')
  await partnerOrder.getByRole('button', { name: 'Accept' }).click()
  await expect(page.getByText(/accepted and sent to kitchen/)).toBeVisible()
  await expect(page.getByRole('region', { name: /Awaiting acceptance \(1\)/ })).toBeVisible()

  await page.getByRole('region', { name: 'Takeaway & delivery' }).getByRole('link').filter({ hasText: 'Neha' }).click()
  await page.getByRole('button', { name: 'Settle ₹378.00' }).click()
  const settle = page.getByRole('dialog', { name: /Settle order/ })
  await settle.getByLabel('Payment 1 method').selectOption('Online')
  await expect(settle.getByRole('button', { name: 'Settle ₹378.00' })).toBeDisabled()
  await settle.getByRole('button', { name: 'Create payment link for ₹378.00' }).click()
  const link = settle.getByTestId('online-payment')
  await expect(link).toContainText('Awaiting payment')

  const [checkout] = await Promise.all([context.waitForEvent('page'), link.getByRole('link', { name: 'Open test checkout' }).click()])
  await expect(checkout.getByTestId('checkout-amount')).toHaveText('₹378.00')
  await checkout.getByRole('button', { name: 'Pay ₹378.00' }).click()
  await expect(checkout.getByRole('status')).toHaveText('Payment successful')
  await checkout.close()

  await expect(link).toContainText('Paid')
  await settle.getByRole('button', { name: 'Settle ₹378.00' }).click()
  await expect(page.getByTestId('invoice-number')).toBeVisible()
  await page.getByRole('dialog', { name: 'Order settled' }).getByRole('button', { name: 'New order' }).click()

  const status = await page.request.get(`/api/v1/outlets/${outletId}/orders/swifteats/SE-1001`, { headers })
  expect((await status.json() as { state: string }).state).toBe('completed')

  await nav.getByRole('link', { name: 'Integrations' }).click()
  await page.getByRole('button', { name: 'Revoke SwiftEats' }).click()
  await page.getByRole('dialog', { name: 'Revoke SwiftEats?' }).getByRole('button', { name: 'Revoke' }).click()
  await expect(page.getByRole('row').filter({ hasText: 'SwiftEats' })).toContainText('Revoked')
  expect((await page.request.get(`/api/v1/outlets/${outletId}/menu`, { headers })).status()).toBe(401)

  await nav.getByRole('link', { name: 'Billing' }).click()
  await page.getByRole('button', { name: 'Cancel subscription' }).click()
  await page.getByRole('dialog', { name: 'Cancel subscription?' }).getByRole('button', { name: 'Cancel subscription' }).click()
  await expect(page.getByRole('alert')).toContainText('Your subscription is inactive')
  await page.getByRole('region', { name: 'Growth plan' }).getByRole('button', { name: 'Subscribe to Growth' }).click()
  await page.getByRole('dialog', { name: 'Subscribe to Growth' }).getByRole('button', { name: 'Confirm' }).click()
  await expect(page.getByRole('alert')).toHaveCount(0)
})
