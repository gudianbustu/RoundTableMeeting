import { expect, test } from '@playwright/test'

// 登录冒烟：welcome 重定向 → 登录 → 进入 meetings 首页
// 依赖后端 Development 环境播种的账号（见 Server/RoundTableMeeting/appsettings.Development.json）。
test('未登录访问首页跳转 welcome，登录后进入会议首页', async ({ page }) => {
  // 未登录访问根路径应被 AuthGate 重定向到 welcome 页
  await page.goto('/')
  await expect(page).toHaveURL(/\/welcome$/)
  await expect(page.getByRole('heading', { name: '开始一场圆桌讨论' })).toBeVisible()

  // 进入登录页，使用种子账号登录
  await page.goto('/login')
  await page.getByRole('textbox', { name: '用户名' }).fill('alice')
  await page.getByRole('textbox', { name: '密码' }).fill('123456')
  await page.getByRole('button', { name: '登 录' }).click()

  // 登录成功后跳转到 meetings 首页，Header 品牌与侧栏会议列表可见
  await expect(page).toHaveURL(/\/$/)
  await expect(page.locator('.app-brand')).toHaveText(/圆桌会议/)
  await expect(page.getByRole('textbox', { name: '搜索会议' })).toBeVisible()
})
