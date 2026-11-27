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
  localStorage.removeItem('rtm-reader-panel-width-v1')
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

  it('连续消息合并：同人 5 分钟内连发合并，超过 5 分钟重新分组', async () => {
    const baseTime = new Date(2026, 0, 1, 10, 0, 0).getTime()
    const at = (minutes: number) => new Date(baseTime + minutes * 60_000)
    const groupedMessages = [
      { ...makeMessage(1, authorOf(participants[0]), '第一句：我们先把接口字段对齐一下。'), createdAt: at(0) },
      { ...makeMessage(2, authorOf(participants[0]), '第二句：附件列表和图片网格已经合并到右侧分栏了。'), createdAt: at(1) },
      { ...makeMessage(3, authorOf(participants[0]), '第三句：连续发短消息时只保留第一条头像，hover 左侧可看时间。'), createdAt: at(3) },
      { ...makeMessage(4, authorOf(participants[3], 'agent'), '收到，我先整理当前接口契约。'), createdAt: at(4) },
      { ...makeMessage(5, authorOf(participants[3], 'agent'), '另外补充一点：历史会议展开滚动也已验证通过。'), createdAt: at(5) },
      { ...makeMessage(6, authorOf(participants[3], 'agent'), '超过 5 分钟后再次发言，这里应当重新显示头像与消息头。'), createdAt: at(12) },
    ]
    await renderWorkspace({ messages: groupedMessages })
    await waitForFirstMessage(groupedMessages[0].content)

    const rows = document.querySelectorAll<HTMLElement>('.message-item[data-message-id]')
    expect(rows).toHaveLength(6)
    expect(rows[0].classList.contains('message-grouped')).toBe(false)
    expect(rows[1].classList.contains('message-grouped')).toBe(true)
    expect(rows[2].classList.contains('message-grouped')).toBe(true)
    expect(rows[3].classList.contains('message-grouped')).toBe(false)
    expect(rows[4].classList.contains('message-grouped')).toBe(true)
    expect(rows[5].classList.contains('message-grouped')).toBe(false)
  })

  it('回复消息渲染：展示引用块、长文本截断与输入框回复上下文条', async () => {
    const msg1 = makeMessage(1, authorOf(participants[0]), '大家先看一下这版方案：我们打算把会议信息、成员、文件和图片统一收进右侧可拖拽分栏，底部保留极简单行输入条。')
    const msg2 = { ...makeMessage(2, authorOf(participants[1]), '这个方向挺好的，右侧栏默认宽度 330px 看起来很舒展。'), replyToMessageId: msg1.messageId }
    const msg3 = { ...makeMessage(3, authorOf(participants[3], 'agent'), '已记录该决议，并同步更新到会议纪要与后续排期。'), replyToMessageId: msg2.messageId }
    const msg4 = { ...makeMessage(4, authorOf(participants[3], 'agent'), '补充确认：引用回复的消息不会与上一条同人消息合并。'), replyToMessageId: msg1.messageId }
    await renderWorkspace({ messages: [msg1, msg2, msg3, msg4] })
    await waitForFirstMessage(msg1.content)

    const quotes = document.querySelectorAll<HTMLButtonElement>('.message-quote')
    expect(quotes).toHaveLength(3)
    expect(quotes[0].textContent).toContain('回复 alice')
    expect(quotes[1].textContent).toContain('回复 bob')

    // 点击消息快捷操作里的「回复」，在底部输入框上方展示待回复上下文条
    const replyButtons = screen.getAllByRole('button', { name: '回复' })
    fireEvent.click(replyButtons[1])
    expect(document.querySelector('.message-reply-context')).not.toBeNull()
    expect(screen.getByText('回复 bob')).toBeTruthy()
  })

  it('Markdown 排版基准：AI 大标题、多段落间距、列表与表格渲染观察', async () => {
    const markdownReport = [
      '# 一级大标题：多 Agent 协作体验评估报告',
      '',
      '这是第一段正文说明。AI 回复时经常自带 `#`、`##`、`###` 多级标题，在聊天信息流里如果沿用文档默认字号，标题会显得过大、撑开整个屏幕。',
      '',
      '这是第二段正文说明。多个连续段落之间的 `margin` 如果偏大，会让本来紧凑的聊天气泡/信息流被拉得很长，降低同屏可读信息量。',
      '',
      '## 二级标题：核心问题清单',
      '',
      '1. **标题层级过大**：聊天流内 `h1`/`h2` 建议压接近正文字号，通过字重和颜色区分层次。',
      '2. **段落间距偏松**：`p`、列表和引用块的上下间距可以进一步收紧。',
      '3. **表格与引用**：需要保持紧凑边框和清晰对比度。',
      '',
      '### 三级标题：指标对比',
      '',
      '| 项目 | 当前状态 | 目标建议 |',
      '| --- | --- | --- |',
      '| 一级标题字号 | 默认 2em | 15.5px 加粗 + 细分割线 |',
      '| 段落间距 | 0.6em | 0.35em |',
      '',
      '## 二级标题：列表排版基准',
      '',
      '1. **有序列表（松散）**：序号与正文之间带空行时，正文不应另起一行、序号不应独占高度。',
      '',
      '   这是第一项展开的第二段说明，仍然停留在同一个列表项内部。',
      '',
      '2. **有序列表（紧凑）**：序号与正文同行显示。',
      '3. **嵌套有序**：',
      '   1. 子项一',
      '   2. 子项二',
      '',
      '- **无序列表**：圆点与正文同行，段落间距收紧。',
      '',
      '  无序列表的松散第二段同样不增加额外高度。',
      '',
      '- 嵌套无序：',
      '  - 子项 A',
      '  - 子项 B',
      '',
      '## 二级标题：任务清单基准',
      '',
      '- [ ] 未完成任务：复选框与首行文字对齐，不使用原生控件外观。',
      '- [x] 已完成任务：复选框展示选中态，文字保持正常排版。',
      '',
      '  - [ ] 嵌套任务也可以对齐缩进层级。',
      '',
      '### 代码片段示例',
      '',
      '```typescript',
      'export function formatMeetingTitle(name: string, freeDiscussion: boolean): string {',
      '  return `${name} · ${freeDiscussion ? "自由讨论" : "点名讨论"}`',
      '}',
      '```',
      '',
      '```bash',
      'pnpm test:browser && pnpm build',
      '```',
      '',
      '> 引用说明：以上样本用于直观观察 Markdown 在当前消息流中的排版密度与代码块混排效果。',
      '',
      '## 附录：详细验收记录',
      '',
      ...Array.from({ length: 8 }, (_, index) => `- 第 ${index + 1} 项验收结论：确认聊天消息流在多段落、多级标题和长报告场景下保持紧凑可读，超过阈值自动折叠并支持在右侧栏阅读完整文档。`),
    ].join('\n')
    const msgs = [
      makeMessage(1, authorOf(participants[0]), '@纪要助手 请把刚才讨论的 Markdown 排版问题整理一份报告发在群里。'),
      makeMessage(2, authorOf(participants[3], 'agent'), markdownReport),
    ]
    await renderWorkspace({ messages: msgs })
    await waitForFirstMessage(msgs[0].content)

    expect(document.querySelector('.message-body h1')).not.toBeNull()
    expect(document.querySelector('.message-body h2')).not.toBeNull()
    expect(document.querySelector('.message-body h3')).not.toBeNull()
    expect(document.querySelector('.message-body table')).not.toBeNull()
    expect(document.querySelector('.message-body .hljs-keyword')).not.toBeNull()

    // 列表排版基准：有序/无序/任务列表都渲染，且松散列表项首段与列表项顶边对齐、无序/任务列表样式符合预期
    const orderedItems = document.querySelectorAll<HTMLLIElement>('.message-body ol > li')
    const unorderedItems = document.querySelectorAll<HTMLLIElement>('.message-body ul > li')
    expect(orderedItems.length).toBeGreaterThanOrEqual(3)
    expect(unorderedItems.length).toBeGreaterThanOrEqual(3)
    const looseLi = document.querySelector<HTMLLIElement>('.message-body ol > li:has(> p)')
    const looseFirstP = looseLi?.querySelector<HTMLParagraphElement>(':scope > p')
    expect(looseLi).not.toBeNull()
    expect(looseFirstP).not.toBeNull()
    expect(getComputedStyle(looseLi!).whiteSpace).toBe('normal')
    expect(Math.abs(looseFirstP!.getBoundingClientRect().top - looseLi!.getBoundingClientRect().top)).toBeLessThanOrEqual(2)

    const checkboxes = document.querySelectorAll<HTMLInputElement>('.message-body input[type="checkbox"]')
    expect(checkboxes.length).toBe(3)
    expect(Array.from(checkboxes).filter(box => box.checked).length).toBe(1)
    for (const box of checkboxes) {
      expect(box.disabled).toBe(true)
      expect(getComputedStyle(box).appearance).toBe('none')
      const taskLi = box.closest<HTMLLIElement>('li.task-list-item')
      expect(taskLi).not.toBeNull()
      expect(getComputedStyle(taskLi!).listStyleType).toBe('none')
    }
    expect(checkboxes[2].getBoundingClientRect().left).toBeGreaterThan(checkboxes[0].getBoundingClientRect().left + 10)
    const offsetOf = (box: HTMLInputElement) => box.getBoundingClientRect().top - box.closest('li')!.getBoundingClientRect().top
    expect(Math.abs(offsetOf(checkboxes[1]) - offsetOf(checkboxes[0]))).toBeLessThanOrEqual(1)
    expect(Math.abs(offsetOf(checkboxes[2]) - offsetOf(checkboxes[0]))).toBeLessThanOrEqual(1)

    const expandBtn = await screen.findByRole('button', { name: '展开全部' }, { timeout: 5000 })
    expect(expandBtn).toBeTruthy()
    // 长消息底部折叠条支持一键「放大阅读」直达宽屏弹窗
    fireEvent.click(screen.getByRole('button', { name: '放大阅读' }))
    expect(document.querySelector('.workspace-reader-modal .message-body h1')).not.toBeNull()
    fireEvent.click(screen.getByRole('button', { name: '退出放大阅读' }))
    await vi.waitFor(() => expect(document.querySelector('.workspace-reader-modal .message-body')).toBeNull(), { timeout: 3000 })

    const readerBtn = screen.getByRole('button', { name: '在右侧栏阅读' })
    fireEvent.click(readerBtn)
    expect(await screen.findByText('文档阅读', undefined, { timeout: 5000 })).toBeTruthy()

    // 方案 C：文档阅读拥有独立宽度与动态上限（可超出普通侧栏 520px），且支持弹窗放大阅读；返回侧栏自动恢复原宽
    const resizer = screen.getByRole('separator', { name: '调整右侧栏宽度' })
    const readerDefaultWidth = Number(resizer.getAttribute('aria-valuenow'))
    expect(readerDefaultWidth).toBeGreaterThanOrEqual(520)
    expect(Number(resizer.getAttribute('aria-valuemax'))).toBeGreaterThan(520)
    fireEvent.keyDown(resizer, { key: 'End' })
    expect(Number(resizer.getAttribute('aria-valuenow'))).toBeGreaterThan(520)

    const panelExpandButtons = screen.getAllByRole('button', { name: '放大阅读' })
    fireEvent.click(panelExpandButtons[panelExpandButtons.length - 1])
    expect(document.querySelector('.workspace-reader-modal .message-body h1')).not.toBeNull()
    fireEvent.click(screen.getByRole('button', { name: '退出放大阅读' }))
    await vi.waitFor(() => expect(document.querySelector('.workspace-reader-modal .message-body')).toBeNull(), { timeout: 3000 })

    fireEvent.click(screen.getByRole('button', { name: '返回侧边栏' }))
    expect(Number(resizer.getAttribute('aria-valuenow'))).toBe(330)
  })

  it('普通消息中的行内代码与无语言代码块：保持干净行内胶囊，不误触发语法高亮', async () => {
    const msgs = [
      makeMessage(1, authorOf(participants[0]), '普通对话里提到的 `MeetingWorkspacePage.tsx`、`pnpm test:browser` 和 `return false` 应该只作为行内代码胶囊展示，不进行语法染色。'),
      makeMessage(2, authorOf(participants[1]), '未标明语言的围栏代码块也保持纯文本展示：\n\n```\nGET /api/meetings/01/messages?limit=50\nHTTP/1.1 200 OK\n```'),
    ]
    await renderWorkspace({ messages: msgs })
    await screen.findByText('MeetingWorkspacePage.tsx', undefined, { timeout: 5000 })

    const inlineCode = screen.getByText('MeetingWorkspacePage.tsx')
    expect(inlineCode.tagName).toBe('CODE')
    expect(inlineCode.classList.contains('hljs')).toBe(false)
    expect(document.querySelector('.message-body .hljs-keyword')).toBeNull()
    expect(screen.getByRole('button', { name: '复制代码' })).toBeTruthy()
  })

  it('Markdown 附件统一预览：聊天消息与右侧文件列表中的 .md 文件均在右侧文档阅读视图打开', async () => {
    const withMd = [
      makeMessage(1, authorOf(participants[3], 'agent'), '已将本次讨论纪要导出为 Markdown 附件，点击即可在右侧栏阅读：'),
    ]
    withMd[0].attachments = [{ attachmentId: 'md-msg-1', name: '会议纪要-2026-11-27.md', sizeBytes: 1280, mediaType: 'application/octet-stream', state: 'saved' }]
    await renderWorkspace({ messages: withMd })
    await waitForFirstMessage(withMd[0].content)

    // 1. 点击聊天消息里直接发送的 .md 附件卡片 → 右侧栏打开文档阅读
    fireEvent.click(screen.getByRole('button', { name: '预览 会议纪要-2026-11-27.md' }))
    expect(await screen.findByText('文档阅读', undefined, { timeout: 5000 })).toBeTruthy()
    expect(await screen.findByText('会议纪要：界面密度与侧栏阅读统一', undefined, { timeout: 5000 })).toBeTruthy()
    expect(document.querySelector('.workspace-reader-doc .hljs-keyword')).not.toBeNull()

    // 2. 返回侧边栏并切到「文件」列表，点击其中的 .md 文件 → 同样在右侧栏打开文档阅读，且返回回到「文件」Tab
    fireEvent.click(screen.getByRole('button', { name: '返回侧边栏' }))
    fireEvent.click(screen.getByRole('tab', { name: '文件' }))
    const reportRow = await screen.findByText('Agent接入体验报告.md', undefined, { timeout: 5000 })
    fireEvent.click(reportRow)
    expect(await screen.findByText('接入配置示例', undefined, { timeout: 5000 })).toBeTruthy()
    fireEvent.click(screen.getByRole('button', { name: '返回侧边栏' }))
    expect(screen.getByRole('tab', { name: '文件' }).getAttribute('aria-selected')).toBe('true')
  })

  it('代码块渲染基准：多语言代码块与行内代码高亮观察', async () => {
    const codeMessage = [
      '以下是后续接入语法高亮的示例样本，包含行内代码 `MessageBody.tsx` 和多语言围栏代码块：',
      '',
      '```typescript',
      'export function isGroupedWithPrevious(prev: ChatMessage | undefined, curr: ChatMessage): boolean {',
      '  if (!prev || curr.replyToMessageId) return false',
      '  const delta = curr.createdAt.getTime() - prev.createdAt.getTime()',
      '  return prev.author.participantId === curr.author.participantId && delta <= 5 * 60 * 1000',
      '}',
      '```',
      '',
      '```csharp',
      'public sealed record SendMessageCommand(Guid MeetingId, string Content, Guid? ReplyToMessageId);',
      '```',
      '',
      '```json',
      '{',
      '  "mode": "directed",',
      '  "maxFileBytes": 26214400,',
      '  "features": ["grouping", "syntax-highlight"]',
      '}',
      '```',
    ].join('\n')
    const msgs = [
      makeMessage(1, authorOf(participants[1]), '发一段 TypeScript / C# / JSON 代码看看现在的代码块显示效果。'),
      makeMessage(2, authorOf(participants[3], 'agent'), codeMessage),
    ]
    await renderWorkspace({ messages: msgs })
    await waitForFirstMessage(msgs[0].content)

    const blocks = document.querySelectorAll('.message-body pre code')
    expect(blocks.length).toBeGreaterThanOrEqual(3)
    expect(document.querySelector('.message-body .hljs-keyword')).not.toBeNull()
    expect(screen.getAllByRole('button', { name: '复制代码' })).toHaveLength(3)
    expect(screen.getByText('MessageBody.tsx')).toBeTruthy()
  })
})
