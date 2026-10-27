import { defineConfig, devices } from '@playwright/test'

const apiPort = Number(process.env.E2E_API_PORT ?? 5080)
const webPort = Number(process.env.E2E_WEB_PORT ?? 5173)
const connection = process.env.E2E_DB
  ?? 'Host=localhost;Port=5432;Database=restaurantos_e2e;Username=restaurantos;Password=restaurantos'

export default defineConfig({
  testDir: './e2e',
  timeout: 60_000,
  expect: { timeout: 10_000 },
  fullyParallel: false,
  workers: 1,
  forbidOnly: !!process.env.CI,
  retries: process.env.CI ? 1 : 0,
  reporter: process.env.CI ? [['list'], ['html', { open: 'never' }]] : 'list',
  use: {
    baseURL: `http://localhost:${webPort}`,
    trace: 'retain-on-failure',
    screenshot: 'only-on-failure',
  },
  projects: [{ name: 'chromium', use: { ...devices['Desktop Chrome'], viewport: { width: 1440, height: 900 } } }],
  webServer: [
    {
      command: `dotnet run --no-launch-profile --project ../backend/src/RestaurantOS.Api --urls http://localhost:${apiPort}`,
      url: `http://localhost:${apiPort}/health`,
      timeout: 180_000,
      reuseExistingServer: !process.env.CI,
      env: {
        ASPNETCORE_ENVIRONMENT: 'Development',
        ConnectionStrings__Default: connection,
        RateLimiting__AuthPermitPerMinute: '1000',
        RateLimiting__PublicPermitPerMinute: '1000',
      },
    },
    {
      command: `npm run dev -- --port ${webPort} --strictPort`,
      url: `http://localhost:${webPort}`,
      timeout: 60_000,
      reuseExistingServer: !process.env.CI,
      env: { VITE_API_PROXY: `http://localhost:${apiPort}` },
    },
  ],
})
