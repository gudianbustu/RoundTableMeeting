# 实时群聊客户端设计

## 1. 文档定位

本文定义实时群聊主题在 React 客户端中的工程接入、API/SignalR 适配、消息状态、工作台数据流和生命周期。主题边界与统一消息契约以[主题总体设计](../design.md)为准；消息模块、页面和组件分别由子文档负责。

本主题扩展 004 会议工作台的消息区插槽，不重复实现登录、会议选择、参与者身份或删除授权。客户端只实现浏览器人类参与者的发送入口与通用消息渲染；消息模型、作者类型和事件处理保持对未来 Agent 兼容。

## 2. 工程目录

在现有 `features/meetingWorkspace` 基础上增加：

~~~text
src/features/meetingWorkspace/
  api/listMessages.ts
  api/sendMessage.ts
  api/meetingChatConnection.ts
  chat/
    messageReducer.ts
    messageSelectors.ts
    mentionCandidates.ts
  components/
    MessageTimeline.tsx
    MessageComposer.tsx
    ChatConnectionStatus.tsx
  pages/
    RealtimeChatRegion.tsx
  types/
    realtimeChat.ts
~~~

`MeetingWorkspacePage` 仍由 004 主题拥有；它组合 `RealtimeChatRegion`，不直接调用生成 Client 或 SignalR 原始 API。API 和连接适配集中在 feature 内，组件只消费视图模型和回调。

## 3. API 适配

### 3.1 历史消息

`listMessages(meetingId, request, signal)` 调用 `GET /api/meetings/{meetingId}/messages`，将生成模型映射为：

- `messageId: string`；
- `meetingId: string`；
- `sequence: number`，运行时校验为安全整数且不小于 1；
- `author: { participantId?: string; kind: 'human' | 'agent' | 'system'; displayName: string }`；
- `content: string`；
- `mentions`；
- `createdAt: Date`。

请求只支持 `beforeSequence`、`afterSequence`、`throughSequence` 和 `limit` 中的合法组合。适配层不把消息正文写入错误对象、日志或 URL。

### 3.2 发送消息

`sendMessage(meetingId, input, signal)` 调用 `POST`，请求由模块生成一次 `clientSubmissionId = crypto.randomUUID()` 并携带内容与可选提及候选。响应 201/200 都映射为同一 `SendMessageResult`，其中 `replayed` 只用于诊断，不改变时间线行为。

客户端不提交作者 participantId、author kind、sequence、createdAt 或 messageId；服务端从当前 Cookie 和会议参与关系解析作者。

### 3.3 SignalR 适配

`meetingChatConnection.ts` 是唯一允许 import `@microsoft/signalr` 的文件，提供：

- `connect(meetingId, callbacks)`；
- `disconnect()`；
- `subscribeMeeting(meetingId)`；
- `onMessageAppended`、`onPresenceChanged`、`onMeetingDeleted`。

实施时新增并固定 SignalR JavaScript 客户端版本，锁文件变更纳入本主题。组件和 Hook 不接触 `HubConnection` 实例。

## 4. RealtimeChatRegion 状态

区域状态按 meetingId 隔离，至少包含：

~~~text
connection: idle | connecting | connected | reconnecting | disconnected | rejected
history: idle | loading | appending | caughtUp | error
messagesById: Map<MessageId, Message>
orderedMessageIds: MessageId[]
earliestLoadedSequence: number | undefined
liveBaselineSequence: number
lastAppliedSequence: number
highWatermarkSequence: number
hasEarlierMessages: boolean
draft: string
send: idle | sending | failed
failedSubmission: { clientSubmissionId, content, candidates } | undefined
presenceByParticipantId: Map<ParticipantId, online | offline>
~~~

Reducer 的每个 action 都携带 meetingId 和 connection generation；旧会议或旧连接代次的 action 直接丢弃。消息合并以 messageId 去重，以 sequence 升序排列；同一 messageId 收到不同内容时保留首次服务端消息并记录安全诊断，不用客户端覆盖事实。

## 5. 进入、历史和实时恢复

1. `RealtimeChatRegion` 挂载时读取当前 meetingId，清空上一个会议的消息和 presence。
2. 先创建 SignalR 连接并开始缓冲事件，再订阅会议组；订阅响应返回服务端 highWatermarkSequence 和 presence 快照。
3. 首次进入读取 highWatermarkSequence 以内的最新一页；空页时 `liveBaselineSequence = 0`、`lastAppliedSequence = 0`。非空页用 `firstSequence - 1` 初始化 liveBaseline、用最后序号初始化 lastApplied，并以 `hasMoreBefore` 初始化 `hasEarlierMessages`。
4. 合并历史和缓冲事件。连续性检查从 liveBaselineSequence 开始；只有实时段内部存在缺口时才继续调用历史接口，不让更早未加载历史被误判为缺失，也不让更晚事件掩盖缺口。
5. 只有 `hasMoreAfter` 为真时才继续读取直到高水位；`hasMoreBefore` 只控制更早历史按钮和 beforeSequence 按需加载。追平后 connection 显示 connected。
6. 切换路由、删除会议、注销或组件卸载时，先增加 generation，再取消历史请求、解绑回调和关闭连接。

连接重建重复执行“订阅 -> afterSequence 补齐”，不依赖事件恰好一次。若 Hub 订阅返回 `meeting_not_found`，连接进入 `rejected` 并交给工作台回退；自动重连耗尽或网络不可达进入 `disconnected`。若历史接口 404 或收到 meetingDeleted，同样清空区域并回退 `/`。

## 6. 发送与幂等重试

- 用户按发送时先做轻量 trim 检查，正文为空不发请求；服务端仍是最终校验者。
- 创建一个 `clientSubmissionId` 并在该消息得到明确成功或最终放弃前复用。
- `sending` 状态不把本地草稿渲染成已发送气泡；可以显示独立的发送中占位，但不得伪造服务端 sequence。
- 201/200 成功后以响应 MessageDto 合并到时间线并清空草稿。
- 网络错误保留草稿和幂等标识，显示重试按钮；重试不生成新 ID。
- validation、meeting_not_found、rate_limited 等确定性错误停止自动重试并保留可编辑正文；用户修改正文后才生成新的 submissionId。
- 发送期间收到同一消息的 SignalR 广播，按 messageId 去重，不重复显示。

## 7. @ 提及选择与显示

`mentionCandidates.ts` 只做输入辅助：根据当前参与者列表提供名称候选，插入原始 `@显示名称` 文本并记录范围。发送时模块先得到 `content.normalize('NFC').trim()` 的实际请求正文，再针对该字符串重新计算非重叠候选范围；它不自行决定服务端目标，也不阻止未知 `@` 文本发送。

服务端返回的 mentions 是唯一高亮事实。`MessageTimeline` 使用 startOffset/length 切分普通文本、mention 文本和尾部文本，React 文本节点渲染，不使用 HTML 注入。多个目标和同一目标的多个合法出现范围都按服务端数组完整展示。

## 8. Presence 与连接状态

- `participantPresenceChanged` 只更新当前 meetingId 的 presence Map；旧会议事件丢弃。
- 人类参与者在 `online`/`offline` 状态旁显示文字标签；不从消息时间或浏览器焦点推断在线。
- 连接状态在消息区域顶部使用 Alert/Status 展示，包含重连中、已断开和重试操作。
- Presence 丢失不影响消息历史；重连时用 Hub 返回的在线 ParticipantId 列表重置，列表中缺席的会议参与者视为 offline，再接收增量事件。
- Agent 类型可以先显示“未连接”或不显示处理状态，不能由客户端猜测 AI 正在思考。

## 9. 多会议隔离

消息缓存、连接、presence、草稿和失败提交都以 meetingId 作为拥有边界。客户端主路径是切换会议时关闭旧连接并新建连接；服务端同连接切换只作为防御性能力。路由从 `/meetings/A` 切换到 `/meetings/B` 时：

1. 关闭 A 连接并递增 generation；
2. 清空 A 的可见消息和 presence；
3. 建立 B 连接并读取 B 历史；
4. A 的迟到历史响应、广播或重连回调全部丢弃。

不把消息写入 localStorage、sessionStorage、URL、全局认证 Context 或跨标签页 BroadcastChannel。

## 10. 客户端安全和可访问性

- 消息正文按纯文本渲染，链接不自动转换为不可信 HTML。
- 发送、重试、重连和关闭按钮使用熟悉图标并提供 tooltip/aria-label；发送命令仍保留清晰文字。
- 消息时间线使用语义列表或可访问日志结构，自动滚动只在用户位于底部时启用；用户阅读旧消息时不强制跳到底部。
- 长正文和长显示名称换行，不以缩小字体解决溢出；消息列表和编辑器保持稳定尺寸。
- 不在界面上显示实现说明、快捷键说明或技术诊断文本；错误提示只描述用户可采取的操作。

## 11. 客户端非目标

不实现未读数、已读位置、消息编辑删除、全文搜索、附件、AI 上下文、MCP、Agent 任务或跨会议消息聚合。
