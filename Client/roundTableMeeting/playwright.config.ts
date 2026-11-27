import { defineConfig, devices } from '@playwright/test'

// e2e 测试运行真实后端（dotnet, 5043）与前端 dev 服务器（vite, 5173）。
// 本地已手动启动的服务会被复用，不会重复拉起；CI 环境始终全新启动。
export default defineConfig({
  testDir: './tests/e2e',
  fullyParallel: true,
  forbidOnly: !!process.env.CI,
  retries: process.env.CI ? 2 : 0,
  reporter: [['list'], ['html', { open: 'never' }]],
  use: {
    baseURL: 'http://localhost:5173',
    trace: 'on-first-retry',
  },
  projects: [
    { name: 'chromium', use: { ...devices['Desktop Chrome'] } },
  ],
  webServer: [
    {
      command: 'dotnet run --project ../../Server/RoundTableMeeting/RoundTableMeeting.csproj --no-launch-profile',
      url: 'http://localhost:5043/openapi/v1.json',
      reuseExistingServer: !process.env.CI,
      timeout: 120_000,
      env: {
        ASPNETCORE_ENVIRONMENT: 'Development',
        ASPNETCORE_URLS: 'http://localhost:5043',
      },
    },
    {
      command: 'pnpm dev',
      url: 'http://localhost:5173',
      reuseExistingServer: !process.env.CI,
      timeout: 120_000,
    },
  ],
})
