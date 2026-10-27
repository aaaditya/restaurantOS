import { expect, type Page } from '@playwright/test'

export const PASSWORD = 'e2e-password-123'

export function uniqueEmail(prefix: string) {
  return `${prefix}-${Date.now()}-${Math.random().toString(36).slice(2, 8)}@example.test`
}

/** Signs up a fresh tenant with the sample menu and outlet "Main Outlet" (invoice prefix MAIN). */
export async function signup(page: Page, email = uniqueEmail('owner')) {
  await page.goto('/signup')
  await page.getByLabel('Restaurant / brand name').fill('E2E Bistro')
  await page.getByLabel('First outlet name').fill('Main Outlet')
  await page.getByLabel('Your name').fill('Asha Owner')
  await page.getByLabel('Work email').fill(email)
  await page.getByLabel('Password').fill(PASSWORD)
  await page.getByRole('button', { name: 'Create account' }).click()
  // Owners land on the dashboard; specs start from the POS.
  await expect(page).toHaveURL(/\/dashboard$/)
  await expect(page.getByTestId('dash-orders')).toHaveText('0')
  await page.getByRole('navigation', { name: 'Main' }).getByRole('link', { name: 'POS' }).click()
  await expect(page).toHaveURL(/\/pos$/)
  return email
}

export async function login(page: Page, email: string, password = PASSWORD) {
  await page.goto('/login')
  await page.getByLabel('Email').fill(email)
  await page.getByLabel('Password').fill(password)
  await page.getByRole('button', { name: 'Sign in' }).click()
}
