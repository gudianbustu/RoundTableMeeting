import { render, screen } from '@testing-library/react'
import type { ReactElement } from 'react'
import { vi } from 'vitest'
import type { ChatMessage, MessageAuthor } from '@/features/meetingWorkspace/types/realtimeChat'
import type { MeetingListItem, Participant } from '@/features/meetingWorkspace/types/meetingWorkspace'
import { AuthenticationContext } from '@/features/authentication/context/AuthenticationContext'
import type { AuthenticationContextValue } from '@/features/authentication/types/authentication'
import { ConfigProvider } from 'antd'
import { MemoryRouter, Route, Routes } from 'react-router-dom'
import { appearanceTheme } from '@/app/theme/appearance'
import { colors } from '@/app/theme/appearanceColors'
import '@/shared/styles/global.css'
import '@/features/meetingWorkspace/pages/MeetingWorkspaceShell.css'

/**
 * 会议工作区浏览器测试工具。
 *
 * 渲染生产组件树（AuthenticatedLayout + MeetingWorkspacePage，antd 生产主题 + --rt-* 变量注入），
 * 仅 mock 数据层。模块 mock（vi.mock）必须声明在「测试文件」里才会被 vitest 提升，
 * 工厂通过 mockStateRef 读取这里的 vi.fn 状态；用 mockStateRef.apply(overrides) 驱动不同界面状态。
 *
 * 注意：组件不卸载（无 cleanup），跑完后窗口保留真实渲染结果供观察。
 */

// ---- 主题变量注入（与 AppProviders.useLayoutEffect 一致：palette=paper, mode=light）----
for (const [name, value] of Object.entries(colors.paper.light)) {
  document.documentElement.style.setProperty('--rt-' + name, value)
}
document.documentElement.dataset.appearanceMode = 'light'

// 被测组件延迟到运行时加载，确保测试文件里的 vi.mock 已注册
let AuthenticatedLayout: (props: { children?: ReactElement }) => ReactElement
let MeetingWorkspacePage: () => ReactElement

// ---- 默认模拟数据 ----

export const participants: Participant[] = [
  { participantId: '00000000-0000-4000-8000-000000000001', displayName: 'alice', type: 'human', isCurrentUser: true, isCreator: true, membershipState: 'joined' },
  { participantId: '00000000-0000-4000-8000-000000000002', displayName: 'bob', type: 'human', isCurrentUser: false, isCreator: false, membershipState: 'joined' },
  { participantId: '00000000-0000-4000-8000-000000000003', displayName: 'carol', type: 'human', isCurrentUser: false, isCreator: false, membershipState: 'joined' },
  { participantId: '00000000-0000-4000-8000-000000000004', displayName: '纪要助手', type: 'agent', isCurrentUser: false, isCreator: false, membershipState: 'joined' },
]

export const meetingList: MeetingListItem[] = Array.from({ length: 60 }, (_, index) => ({
  meetingId: `00000000-0000-4000-8000-${String(index + 1).padStart(12, '0')}`,
  name: `模拟会议 ${String(index + 1).padStart(2, '0')} 号`,
  myDisplayName: 'alice',
  isCreator: index === 0,
}))

/** 按 getMeetingDetails 返回给 mapper 的 DTO 形状提供数据（permissions 决定 canSend/canDelete 等） */
export const detailsDto = {
  meetingId: meetingList[0].meetingId,
  name: '模拟会议 01 号',
  description: '整体布局预览用模拟会议',
  currentParticipant: participants[0],
  membershipState: 'joined',
  freeDiscussion: false,
  permissions: { canLeave: true, canSend: true, canDelete: true, canInvite: true },
  participants,
}

export const authorOf = (participant: Participant, kind: 'human' | 'agent' = 'human'): MessageAuthor =>
  ({ participantId: participant.participantId, displayName: participant.displayName, kind })

export const makeMessage = (sequence: number, author: MessageAuthor, content: string): ChatMessage => ({
  messageId: `00000000-0000-4000-8000-0000000001${String(sequence).padStart(2, '0')}`,
  meetingId: detailsDto.meetingId,
  sequence,
  author,
  content,
  mentions: [],
  createdAt: new Date(2026, 0, 1, 9, sequence),
})

export const defaultMessages: ChatMessage[] = [
  makeMessage(1, authorOf(participants[0]), '大家好，今天讨论客户端整体布局的视觉效果。'),
  makeMessage(2, authorOf(participants[1]), '侧栏会议列表、话题头和聊天区都在这一屏里。'),
  makeMessage(3, authorOf(participants[3], 'agent'), '已就绪，我会记录本次讨论的会议纪要。'),
  makeMessage(4, authorOf(participants[2]), '输入框和消息气泡的间距看起来正常吗？'),
  makeMessage(5, authorOf(participants[0]), '正常，1280×720 视口下整体可读性不错。'),
]

export const mockImageUrls: Record<string, string> = {
  'file-1': 'data:text/plain;charset=utf-8,' + encodeURIComponent('# Agent 接入体验报告\n\n让人和 AI Agent 坐在同一间会议室里开会。你在浏览器里发言，Agent 通过 MCP 接入，支持公屏点名、自由讨论与附件协作。'),
  'file-3': 'data:text/plain;charset=utf-8,' + encodeURIComponent('{"tools": ["join_meeting", "send_message", "wait_messages"], "version": "0.1.0", "transport": "stdio"}'),
  'file-4': 'data:text/plain;charset=utf-8,' + encodeURIComponent('2026-11-27 09:04:01 INFO 并发 50 路 SignalR 连接稳定，平均推送延迟 18ms，P99 42ms，零断线重连。'),
  'img-1': 'data:image/svg+xml;utf8,' + encodeURIComponent('<svg xmlns="http://www.w3.org/2000/svg" width="320" height="240"><rect width="320" height="240" fill="#2563eb"/><text x="50%" y="52%" fill="#fff" font-size="22" font-family="sans-serif" text-anchor="middle">架构草图.png</text></svg>'),
  'img-2': 'data:image/svg+xml;utf8,' + encodeURIComponent('<svg xmlns="http://www.w3.org/2000/svg" width="320" height="240"><rect width="320" height="240" fill="#0d9488"/><text x="50%" y="52%" fill="#fff" font-size="22" font-family="sans-serif" text-anchor="middle">交互稿-01.png</text></svg>'),
  'img-3': 'data:image/svg+xml;utf8,' + encodeURIComponent('<svg xmlns="http://www.w3.org/2000/svg" width="320" height="240"><rect width="320" height="240" fill="#d97706"/><text x="50%" y="52%" fill="#fff" font-size="22" font-family="sans-serif" text-anchor="middle">性能监控.webp</text></svg>'),
  'img-4': 'data:image/svg+xml;utf8,' + encodeURIComponent('<svg xmlns="http://www.w3.org/2000/svg" width="320" height="240"><rect width="320" height="240" fill="#7c3aed"/><text x="50%" y="52%" fill="#fff" font-size="22" font-family="sans-serif" text-anchor="middle">白板记录.jpg</text></svg>'),
  'img-5': 'data:image/svg+xml;utf8,' + encodeURIComponent('<svg xmlns="http://www.w3.org/2000/svg" width="320" height="240"><rect width="320" height="240" fill="#e11d48"/><text x="50%" y="52%" fill="#fff" font-size="22" font-family="sans-serif" text-anchor="middle">告警截图.png</text></svg>'),
  'img-6': 'data:image/svg+xml;utf8,' + encodeURIComponent('<svg xmlns="http://www.w3.org/2000/svg" width="320" height="240"><rect width="320" height="240" fill="#16a34a"/><text x="50%" y="52%" fill="#fff" font-size="22" font-family="sans-serif" text-anchor="middle">验收通过.png</text></svg>'),
}

export const defaultAttachments = [
  { attachment: { attachmentId: 'file-1', name: 'Agent接入体验报告.md', sizeBytes: 18420, mediaType: 'text/markdown', state: 'saved' }, messageId: defaultMessages[2].messageId, sequence: 3, author: { displayName: '纪要助手' }, createdAt: new Date(2026, 0, 1, 9, 3).toISOString() },
  { attachment: { attachmentId: 'file-2', name: '圆桌会议界面优化方案-v2.pdf', sizeBytes: 2457600, mediaType: 'application/pdf', state: 'saved' }, messageId: defaultMessages[0].messageId, sequence: 1, author: { displayName: 'alice' }, createdAt: new Date(2026, 0, 1, 9, 1).toISOString() },
  { attachment: { attachmentId: 'file-3', name: 'mcp-tools-schema.json', sizeBytes: 6340, mediaType: 'application/json', state: 'saved' }, messageId: defaultMessages[1].messageId, sequence: 2, author: { displayName: 'bob' }, createdAt: new Date(2026, 0, 1, 9, 2).toISOString() },
  { attachment: { attachmentId: 'file-4', name: '压测日志-2026-11-27.txt', sizeBytes: 94210, mediaType: 'text/plain', state: 'saved' }, messageId: defaultMessages[3].messageId, sequence: 4, author: { displayName: 'carol' }, createdAt: new Date(2026, 0, 1, 9, 4).toISOString() },
  { attachment: { attachmentId: 'img-1', name: '架构草图.png', sizeBytes: 215040, mediaType: 'image/png', state: 'saved' }, messageId: defaultMessages[0].messageId, sequence: 1, author: { displayName: 'alice' }, createdAt: new Date(2026, 0, 1, 9, 1).toISOString() },
  { attachment: { attachmentId: 'img-2', name: '交互稿-01.png', sizeBytes: 184320, mediaType: 'image/png', state: 'saved' }, messageId: defaultMessages[1].messageId, sequence: 2, author: { displayName: 'bob' }, createdAt: new Date(2026, 0, 1, 9, 2).toISOString() },
  { attachment: { attachmentId: 'img-3', name: '性能监控.webp', sizeBytes: 92160, mediaType: 'image/webp', state: 'saved' }, messageId: defaultMessages[2].messageId, sequence: 3, author: { displayName: '纪要助手' }, createdAt: new Date(2026, 0, 1, 9, 3).toISOString() },
  { attachment: { attachmentId: 'img-4', name: '白板记录.jpg', sizeBytes: 312000, mediaType: 'image/jpeg', state: 'saved' }, messageId: defaultMessages[3].messageId, sequence: 4, author: { displayName: 'carol' }, createdAt: new Date(2026, 0, 1, 9, 4).toISOString() },
  { attachment: { attachmentId: 'img-5', name: '告警截图.png', sizeBytes: 128000, mediaType: 'image/png', state: 'saved' }, messageId: defaultMessages[4].messageId, sequence: 5, author: { displayName: 'alice' }, createdAt: new Date(2026, 0, 1, 9, 5).toISOString() },
  { attachment: { attachmentId: 'img-6', name: '验收通过.png', sizeBytes: 156000, mediaType: 'image/png', state: 'saved' }, messageId: defaultMessages[4].messageId, sequence: 5, author: { displayName: 'bob' }, createdAt: new Date(2026, 0, 1, 9, 5).toISOString() },
]

export const defaultSubscription = {
  onlineParticipantIds: participants.map(p => p.participantId),
  highWatermarkSequence: defaultMessages.length,
  agentActivities: [],
}

const authValue: AuthenticationContextValue = {
  state: { status: 'authenticated', user: { userId: participants[0].participantId, username: 'alice', nickname: 'alice', isAdmin: false } },
} as AuthenticationContextValue

// ---- 配置覆盖 ----

export type WorkspaceOverrides = {
  /** 覆盖会议详情 DTO（例如 permissions 全 false 驱动只读界面） */
  details?: Partial<typeof detailsDto>
  /** 覆盖会议列表 */
  meetings?: MeetingListItem[]
  /** 覆盖历史消息 */
  messages?: ChatMessage[]
  /** 让详情请求失败（驱动「操作未能完成」条幅），值为抛出的错误 */
  detailsError?: unknown
  /** 让消息历史请求失败（驱动「消息暂时不可用」条幅） */
  messagesError?: unknown
  /** 让 capabilities 请求失败（驱动「无法读取附件大小限制」提示） */
  capabilitiesError?: unknown
  /** 让邀请码请求失败 */
  invitationError?: unknown
  /** 让聊天连接失败（驱动「连接已断开」条幅） */
  connectionError?: unknown
  /** 让聊天连接永远 pending（停留在「正在连接会议」状态，用于观察该状态样式） */
  hangConnection?: boolean
  /** 覆盖连接订阅快照（在线成员等） */
  subscription?: Partial<typeof defaultSubscription>
  /** 路由初始地址，默认进入会议 01 */
  initialEntry?: string
}

type MessagesPage = { items: ChatMessage[]; hasMoreBefore: boolean; hasMoreAfter: boolean; firstSequence: number; lastSequence: number }

/**
 * mock 状态与配置入口。
 *
 * state 里的 vi.fn 被测试文件中的 vi.mock 工厂引用；
 * apply(overrides) 每次重设全部 API 返回，defaults 覆盖未指定的字段。
 */
export const mockStateRef = {
  state: {
    meetings: {
      list: vi.fn(),
      details: vi.fn(),
      messages: vi.fn(),
      attachments: vi.fn(),
      invitation: vi.fn(),
      capabilities: vi.fn(),
    },
    connection: { start: vi.fn() },
    messagesPage: undefined as MessagesPage | undefined,
  },
  apply(overrides: WorkspaceOverrides = {}) {
    const meetings = overrides.meetings ?? meetingList
    const details = { ...detailsDto, ...overrides.details }
    const messages = overrides.messages ?? defaultMessages
    const s = this.state
    s.meetings.list.mockImplementation(async () => meetings)
    s.meetings.details.mockImplementation(async () => {
      if (overrides.detailsError) throw overrides.detailsError
      return details
    })
    s.meetings.messages.mockImplementation(async () => {
      if (overrides.messagesError) throw overrides.messagesError
      return { items: messages, hasMoreBefore: false, hasMoreAfter: false, firstSequence: 1, lastSequence: messages.length }
    })
    s.meetings.attachments.mockImplementation(async () => ({ items: defaultAttachments, nextBeforeSequence: undefined }))
    s.meetings.invitation.mockImplementation(async () => {
      if (overrides.invitationError) throw overrides.invitationError
      return { meetingId: details.meetingId, meetingCode: 'ABC123' }
    })
    s.meetings.capabilities.mockImplementation(async () => {
      if (overrides.capabilitiesError) throw overrides.capabilitiesError
      return { maxFileBytes: 25 * 1024 * 1024 }
    })
    s.connection.start.mockImplementation((): Promise<unknown> => {
      if (overrides.connectionError) return Promise.reject(overrides.connectionError)
      // 永远挂起：连接停在 connecting，观察「正在连接会议」常驻样式
      if (overrides.hangConnection) return new Promise(() => {})
      return Promise.resolve({ ...defaultSubscription, ...overrides.subscription })
    })
    s.messagesPage = { items: messages, hasMoreBefore: false, hasMoreAfter: false, firstSequence: 1, lastSequence: messages.length }
  },
}

/**
 * 渲染生产路由结构：AuthenticatedLayout + MeetingWorkspacePage，主题与样式全为原版。
 * 每次调用先清空 body（上一用例画面不保留）并应用 overrides。
 * 组件动态加载，保证测试文件的 vi.mock 已注册。
 */
export async function renderWorkspace(overrides: WorkspaceOverrides = {}) {
  if (!MeetingWorkspacePage) {
    const [layoutModule, pageModule] = await Promise.all([
      import('@/app/layouts/AuthenticatedLayout'),
      import('@/features/meetingWorkspace/pages/MeetingWorkspacePage'),
    ])
    AuthenticatedLayout = layoutModule.AuthenticatedLayout
    MeetingWorkspacePage = pageModule.MeetingWorkspacePage
  }
  // 清掉上一用例的画面，避免 DOM 叠加导致查询命中旧元素
  document.body.textContent = ''
  mockStateRef.apply(overrides)
  const theme = appearanceTheme(colors.paper.light, 'light')
  const entry = overrides.initialEntry ?? `/meetings/${detailsDto.meetingId}`
  return render(
    <ConfigProvider theme={theme}>
      <AuthenticationContext.Provider value={authValue}>
        <MemoryRouter initialEntries={[entry]}>
          <Routes>
            <Route element={<AuthenticatedLayout />}>
              <Route path="/meetings/:meetingId" element={<MeetingWorkspacePage />} />
            </Route>
          </Routes>
        </MemoryRouter>
      </AuthenticationContext.Provider>
    </ConfigProvider>,
  )
}

/** 等待历史消息加载完成：首条消息气泡出现（超时 5s） */
export async function waitForFirstMessage(content = defaultMessages[0].content) {
  try {
    return await screen.findByText(content, undefined, { timeout: 5000 })
  } catch (error) {
    const main = document.querySelector('.meeting-main')
    const text = main ? main.textContent?.replace(/\s+/g, ' ').slice(0, 300) : 'missing'
    const alerts = [...document.querySelectorAll('.ant-alert')].map(a => a.textContent?.replace(/\s+/g, ' ').slice(0, 120))
    throw new Error(`消息未出现。main 文本: ${text} | alerts: ${JSON.stringify(alerts)}`, { cause: error })
  }
}

/** 常用布局结构选择器，供断言「哪些区域就位」 */
export const workspaceSelectors = [
  '.app-shell.authenticated-shell',
  '.meeting-workspace',
  '.meeting-navigation',
  '.meeting-main',
  '.workspace-topic-header',
  '.realtime-chat-region',
  '.message-timeline',
  '.composer-mentions',
] as const
