import { fireEvent, screen } from '@testing-library/react'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import { RealtimeChatApiError } from '@/features/meetingWorkspace/api/listMessages'
import {
  authorOf,
  defaultMessages,
  detailsDto,
  makeMessage,
  mockImageUrls,
  mockStateRef,
  participants,
  renderWorkspace,
  waitForFirstMessage,
  workspaceSelectors,
} from './workspaceHarness'

// ---- 模块 mock：必须声明在测试文件里才会被 vitest 提升生效 ----
// mockStateRef.state 由 workspaceHarness 延迟填充（绕开 vi.mock 工厂与数据模块的循环依赖）

vi.mock('@/api/client/apiClient', () => ({
  getApiClient: () => ({
    api: {
      meetings: {
        get: () => mockStateRef.state.meetings.list(),
        byMeetingId: () => ({
          get: () => mockStateRef.state.meetings.details(),
          messages: { get: (request: Record<string, unknown>) => mockStateRef.state.meetings.messages(request) },
          attachments: { get: () => mockStateRef.state.meetings.attachments() },
          invitation: { post: () => mockStateRef.state.meetings.invitation() },
          discussionMode: { put: () => Promise.resolve() },
        }),
      },
      system: {
        capabilities: { get: () => mockStateRef.state.meetings.capabilities() },
      },
    },
  }),
}))

vi.mock('@/features/meetingWorkspace/api/meetingChatConnection', () => ({
  MeetingChatConnection: class {
    callbacks: { onState?: (state: string) => void }
    constructor(callbacks: { onState?: (state: string) => void }) {
      this.callbacks = callbacks
    }
    async start() {
      const result = await mockStateRef.state.connection.start()
      this.callbacks.onState?.('connected')
      return result
    }
    async stop() {}
  },
}))

vi.mock('@/features/meetingWorkspace/api/accountActivityConnection', () => ({
  subscribeAccountActivity: () => () => {},
}))

// listMessages 模块含严格 DTO 校验的 mapper，mock 直接返回领域消息
vi.mock('@/features/meetingWorkspace/api/listMessages', () => ({
  RealtimeChatApiError: class extends Error {
    code: string
    constructor(code: string) { super(code); this.code = code }
  },
  mapMessage: (value: unknown) => value,
  mapAttachment: (value: unknown) => value,
  listMessages: vi.fn(async () => mockStateRef.state.messagesPage),
}))

vi.mock('@/features/meetingWorkspace/api/messageResources', async importOriginal => {
  const actual = await importOriginal<typeof import('@/features/meetingWorkspace/api/messageResources')>()
  return {
    ...actual,
    attachmentUrl: (meetingId: string, attachmentId: string, preview = false) =>
      mockImageUrls[attachmentId] ?? actual.attachmentUrl(meetingId, attachmentId, preview),
  }
})

beforeEach(() => {
  // 每条用例默认干净状态；renderWorkspace 的 overrides 再按需覆盖
  localStorage.removeItem('rtm-right-panel-width-v1')
  mockStateRef.apply({})
})

describe('会议工作区完整布局', () => {
  it('正常状态：侧栏、话题头、聊天时间线与输入框按生产组件渲染', async () => {
    await renderWorkspace()

    // 等历史消息异步加载完成：首条消息气泡出现
    await waitForFirstMessage()

    // 结构完整：壳、工作区、侧栏、话题头、聊天区、时间线、输入框全部就位
    const missing = workspaceSelectors.filter(sel => !document.querySelector(sel))
    expect(missing).toEqual([])

    // 五条消息全部渲染
    const bubbles = document.querySelectorAll('.message-item')
    expect(bubbles.length).toBeGreaterThanOrEqual(defaultMessages.length)

    // 无错误条幅、无连接提示（mock 完整时的干净状态）
    expect(document.querySelector('.ant-alert-error')).toBeNull()
    expect(screen.queryByText('正在连接会议')).toBeNull()
    expect(screen.queryByText('操作未能完成')).toBeNull()
  })

  it('附件能力接口失败：输入框工具区出现大小限制读取失败提示', async () => {
    await renderWorkspace({ capabilitiesError: new RealtimeChatApiError('invalid_capabilities') })

    await waitForFirstMessage()
    // AttachmentUpload 挂载即请求 capabilities，失败后展示红色提示
    const notice = await screen.findByText('无法读取附件大小限制，请重新打开此交流区域后重试。', undefined, { timeout: 5000 })
    expect(notice).toBeTruthy()
  })

  it('会议详情加载失败：主区展示「操作未能完成」错误条幅', async () => {
    // TypeError 被 toApiError 映射为网络错误文案，贴近真实断网场景
    await renderWorkspace({ detailsError: new TypeError('Failed to fetch') })

    // 详情失败后 workspace.error 被置位，主区出现错误条幅并回退空状态
    const alert = await screen.findByText('操作未能完成', undefined, { timeout: 5000 })
    expect(alert).toBeTruthy()
    // 描述来自 toApiError 的网络错误映射
    expect(screen.getByText('服务暂时不可访问，请稍后重试。')).toBeTruthy()
    // 页面回退到「开始一场圆桌会议」空状态
    expect(screen.getByText('开始一场圆桌会议')).toBeTruthy()
  })

  it('聊天连接失败：展示「连接已断开」警告并提供重新连接按钮', async () => {
    await renderWorkspace({ connectionError: new Error('hub unavailable') })

    // 连接失败后 ChatConnectionStatus 展示断开警告
    const alert = await screen.findByText('连接已断开', undefined, { timeout: 5000 })
    expect(alert).toBeTruthy()
    expect(screen.getByRole('button', { name: '重新连接' })).toBeTruthy()
  })

  it('连接挂起：停在「正在连接会议」状态供观察样式', async () => {
    // hangConnection 让 start() 永远 pending，连接状态停在 connecting 不再流转
    await renderWorkspace({ hangConnection: true })

    // ConnectingNotice 有 300ms 防闪烁延迟，等它出现
    const alert = await screen.findByText('正在连接会议', undefined, { timeout: 5000 })
    expect(alert).toBeTruthy()
  })

  it('权限受限成员视角：canSend=false 时输入框替换为历史浏览工具栏', async () => {
    await renderWorkspace({
      details: {
        permissions: { canLeave: true, canSend: false, canDelete: false, canInvite: false },
        currentParticipant: { ...participants[1], isCurrentUser: true, isCreator: false },
      },
    })

    await waitForFirstMessage()
    // 只读模式：无输入框，出现历史记录工具栏
    expect(document.querySelector('.composer-mentions')).toBeNull()
    expect(screen.getByText(/历史记录/)).toBeTruthy()
  })

  it('历史会议视角：membershipState=left 时展示历史头部与重新入会', async () => {
    await renderWorkspace({ details: { membershipState: 'left' } })

    await waitForFirstMessage()
    expect(screen.getByText('已退出')).toBeTruthy()
    expect(screen.getByRole('button', { name: '重新入会' })).toBeTruthy()
  })

  it('空会议：无消息时时间线展示空状态', async () => {
    await renderWorkspace({ messages: [] })

    // 空消息列表下输入框仍在，等待布局稳定
    await screen.findByTitle(detailsDto.name)
    expect(document.querySelector('.composer-mentions')).not.toBeNull()
    expect(document.querySelectorAll('.message-item')).toHaveLength(0)
  })

  it('带附件消息：展示附件卡片', async () => {
    const withAttachment = [
      makeMessage(1, authorOf(participants[0]), '这是带附件的消息'),
    ]
    withAttachment[0].attachments = [{ attachmentId: 'att-1', name: '设计稿.pdf', sizeBytes: 1024, mediaType: 'application/pdf', state: 'saved' }]
    await renderWorkspace({ messages: withAttachment })

    await screen.findByText('这是带附件的消息', undefined, { timeout: 5000 })
    expect(screen.getByText('设计稿.pdf')).toBeTruthy()
  })

  it('右侧上下文分栏：支持一级切换成员/文件/图片，并可拖拽调整宽度', async () => {
    await renderWorkspace({
      subscription: { onlineParticipantIds: [participants[0].participantId, participants[1].participantId] },
    })
    await waitForFirstMessage()

    const infoTab = screen.getByRole('tab', { name: '信息' })
    const filesTab = screen.getByRole('tab', { name: '文件' })
    const imagesTab = screen.getByRole('tab', { name: '图片' })
    const resizer = screen.getByRole('separator', { name: '调整右侧栏宽度' })

    const initialWidth = Number(resizer.getAttribute('aria-valuenow'))
    fireEvent.keyDown(resizer, { key: 'ArrowLeft' })
    expect(Number(resizer.getAttribute('aria-valuenow'))).toBeGreaterThan(initialWidth)

    fireEvent.click(infoTab)
    expect(await screen.findByText('整体布局预览用模拟会议', undefined, { timeout: 5000 })).toBeTruthy()
    expect(screen.getByText('讨论模式')).toBeTruthy()

    fireEvent.click(filesTab)
    expect(await screen.findByText('Agent接入体验报告.md', undefined, { timeout: 5000 })).toBeTruthy()
    expect(await screen.findByText(/让人和 AI Agent 坐在同一间会议室里开会/, undefined, { timeout: 5000 })).toBeTruthy()
    expect(screen.getByText('圆桌会议界面优化方案-v2.pdf')).toBeTruthy()

    fireEvent.click(imagesTab)
    expect(await screen.findByRole('button', { name: '预览 架构草图.png' }, { timeout: 5000 })).toBeTruthy()
    expect(document.querySelectorAll('.meeting-image-tile')).toHaveLength(6)

    // 历史会议展开后在限定高度内可正常滚动
    fireEvent.click(screen.getByRole('button', { name: '历史会议' }))
    await vi.waitFor(() => {
      const historyScroll = document.querySelector<HTMLElement>('.meeting-navigation-history .meeting-navigation-scroll')
      expect(historyScroll).not.toBeNull()
      expect(historyScroll!.scrollHeight).toBeGreaterThan(historyScroll!.clientHeight)
      historyScroll!.scrollTop = 120
      expect(historyScroll!.scrollTop).toBeGreaterThan(0)
    }, { timeout: 2000 })
  })
})
