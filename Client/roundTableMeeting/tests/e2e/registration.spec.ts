import { expect, test, type Page } from '@playwright/test'

type MockUser = {
  userId: string
  username: string
  nickname: string
  isAdmin: boolean
}

type MockRegistrationOptions = {
  existingUsernames?: string[]
  invitePreview?: {
    meetingId: string
    meetingName: string
    description?: string
    defaultDisplayName: string
    alreadyJoined?: boolean
  }
}

async function setupRegistrationMocks(page: Page, options: MockRegistrationOptions = {}) {
  let currentUser: MockUser | null = null
  const requestedMeetingDetails: string[] = []
  const existing = new Set((options.existingUsernames ?? []).map(name => name.toLowerCase()))

  await page.route(/^https?:\/\/[^/]+\/hubs\//, route => route.abort())

  await page.route(/^https?:\/\/[^/]+\/api\/auth\/me(?:\?.*)?$/, async route => {
    if (!currentUser) {
      await route.fulfill({
        status: 401,
        contentType: 'application/problem+json',
        body: JSON.stringify({
          status: 401,
          code: 'unauthenticated',
          title: 'Authentication required',
          detail: 'Authentication is required.',
        }),
      })
      return
    }
    await route.fulfill({
      status: 200,
      contentType: 'application/json',
      body: JSON.stringify(currentUser),
    })
  })

  await page.route(/^https?:\/\/[^/]+\/api\/auth\/register(?:\?.*)?$/, async route => {
    const payload = route.request().postDataJSON() as { username?: string; password?: string } | null
    const username = payload?.username?.trim() ?? ''
    if (existing.has(username.toLowerCase())) {
      await route.fulfill({
        status: 409,
        contentType: 'application/problem+json',
        body: JSON.stringify({
          status: 409,
          code: 'username_conflict',
          title: 'Username conflict',
          detail: 'The username is already in use.',
        }),
      })
      return
    }
    currentUser = {
      userId: '22222222-2222-2222-2222-222222222222',
      username,
      nickname: username,
      isAdmin: false,
    }
    await route.fulfill({
      status: 200,
      contentType: 'application/json',
      body: JSON.stringify(currentUser),
    })
  })

  await page.route(/^https?:\/\/[^/]+\/api\/meetings(?:\/[^?]*)?(?:\?.*)?$/, async route => {
    const url = new URL(route.request().url())
    if (url.pathname === '/api/meetings') {
      await route.fulfill({
        status: 200,
        contentType: 'application/json',
        body: JSON.stringify([]),
      })
      return
    }
    if (url.pathname === '/api/meetings/join/preview') {
      if (!options.invitePreview) {
        await route.fulfill({
          status: 404,
          contentType: 'application/problem+json',
          body: JSON.stringify({
            status: 404,
            code: 'meeting_not_found',
            title: 'meeting_not_found',
            detail: 'The request could not be completed.',
          }),
        })
        return
      }
      await route.fulfill({
        status: 200,
        contentType: 'application/json',
        body: JSON.stringify(options.invitePreview),
      })
      return
    }
    requestedMeetingDetails.push(url.pathname)
    await route.fulfill({
      status: 404,
      contentType: 'application/problem+json',
      body: JSON.stringify({
        status: 404,
        code: 'meeting_not_found',
        title: 'meeting_not_found',
        detail: 'The request could not be completed.',
      }),
    })
  })

  return { requestedMeetingDetails }
}

test.describe('注册流程（Mock API）', () => {
  test('从欢迎页注册成功后正常进入会议首页且无报错', async ({ page }) => {
    const pageErrors: Error[] = []
    page.on('pageerror', error => pageErrors.push(error))
    await setupRegistrationMocks(page)

    await page.goto('/')
    await expect(page).toHaveURL(/\/welcome$/)
    await page.getByRole('button', { name: '注册' }).click()
    await expect(page).toHaveURL(/\/register$/)
    await expect(page.getByRole('heading', { name: '创建账号' })).toBeVisible()

    await page.getByRole('textbox', { name: '用户名' }).fill('newuser')
    await page.getByLabel('密码').fill('Pass123456')
    await page.getByRole('button', { name: '注册并开始使用' }).click()

    await expect(page).toHaveURL(/\/$/)
    await expect(page.locator('.app-brand')).toHaveText(/圆桌会议/)
    await expect(page.getByRole('button', { name: 'newuser' })).toBeVisible()
    await expect(page.getByText('还没有会议')).toBeVisible()
    await expect(page.locator('.workspace-home-errors')).toHaveCount(0)
    expect(pageErrors).toEqual([])
  })

  test('从受保护会议地址跳转登录再切换注册，成功后回到首页不触发 404 报错', async ({ page }) => {
    const pageErrors: Error[] = []
    page.on('pageerror', error => pageErrors.push(error))
    const { requestedMeetingDetails } = await setupRegistrationMocks(page)

    await page.goto('/meetings/11111111-1111-1111-1111-111111111111')
    await expect(page).toHaveURL(/\/login$/)
    await page.getByRole('link', { name: '立即注册' }).click()
    await expect(page).toHaveURL(/\/register$/)

    await page.getByRole('textbox', { name: '用户名' }).fill('freshuser')
    await page.getByLabel('密码').fill('Pass123456')
    await page.getByRole('button', { name: '注册并开始使用' }).click()

    await expect(page).toHaveURL(/\/$/)
    await expect(page.getByRole('button', { name: 'freshuser' })).toBeVisible()
    await expect(page.getByText('还没有会议')).toBeVisible()
    await expect(page.locator('.workspace-home-errors')).toHaveCount(0)
    expect(requestedMeetingDetails).toEqual([])
    expect(pageErrors).toEqual([])
  })

  test('通过邀请链接进入注册流程，成功后保留邀请码并打开加入会议弹窗', async ({ page }) => {
    const pageErrors: Error[] = []
    page.on('pageerror', error => pageErrors.push(error))
    await setupRegistrationMocks(page, {
      invitePreview: {
        meetingId: '33333333-3333-3333-3333-333333333333',
        meetingName: '产品架构讨论',
        description: '讨论新版本方案',
        defaultDisplayName: 'invitee',
        alreadyJoined: false,
      },
    })

    const inviteCode = '44444444-4444-4444-4444-444444444444'
    await page.goto(`/#join=${inviteCode}`)
    await expect(page).toHaveURL(/\/login$/)
    await page.getByRole('link', { name: '立即注册' }).click()
    await expect(page).toHaveURL(/\/register$/)

    await page.getByRole('textbox', { name: '用户名' }).fill('invitee')
    await page.getByLabel('密码').fill('Pass123456')
    await page.getByRole('button', { name: '注册并开始使用' }).click()

    await expect(page).toHaveURL(new RegExp(`/#join=${inviteCode}$`))
    await expect(page.getByRole('dialog', { name: '加入会议' })).toBeVisible()
    await expect(page.getByText('产品架构讨论')).toBeVisible()
    await expect(page.locator('.workspace-home-errors')).toHaveCount(0)
    expect(pageErrors).toEqual([])
  })

  test('注册用户名已存在时展示冲突提示且停留在注册页', async ({ page }) => {
    await setupRegistrationMocks(page, { existingUsernames: ['alice'] })

    await page.goto('/register')
    await page.getByRole('textbox', { name: '用户名' }).fill('alice')
    await page.getByLabel('密码').fill('Pass123456')
    await page.getByRole('button', { name: '注册并开始使用' }).click()

    await expect(page).toHaveURL(/\/register$/)
    await expect(page.getByRole('alert')).toHaveText('用户名已被使用，请换一个。')
  })
})
