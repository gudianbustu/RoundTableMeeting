# 实时群聊客户端实现方案

## 1. 文档定位

本文把[客户端设计](design.md)具体化为依赖、API/SignalR 适配、消息 store、Reducer、页面接入、组件实现和构建步骤。模块、页面和组件内部细节见本目录下的对应 `impl.md`，测试编号见[测试方案](../testPlan.md)。

本主题扩展 004 会议工作台，不修改认证、会议导航、参与者身份或删除权限。客户端只实现浏览器发送和通用消息渲染，不能创建 AI 专用状态或通道。

## 2. 依赖和目录

1. 在 `package.json` 增加精确版本 `@microsoft/signalr`，同步锁文件。
2. 在 `vite.config.ts` 的 proxy 增加 `/hubs`，设置 `ws: true`、`changeOrigin: false`。
3. 仅 `api/meetingChatConnection.ts` 可 import `@microsoft/signalr`。
4. API、Reducer、组件和页面均放在 `features/meetingWorkspace`，不新增全局 chat store。

~~~text
src/features/meetingWorkspace/
  api/listMessages.ts
  api/sendMessage.ts
  api/meetingChatConnection.ts
  chat/messageReducer.ts
  chat/messageSelectors.ts
  chat/mentionCandidates.ts
  components/MessageTimeline.tsx
  components/MessageComposer.tsx
  components/ChatConnectionStatus.tsx
  pages/RealtimeChatRegion.tsx
  types/realtimeChat.ts
~~~

## 3. API 实现

### 3.1 listMessages

构造 `/api/meetings/{meetingId}/messages` 查询参数，保留 `beforeSequence`、`afterSequence`、`throughSequence` 和 `limit`。映射生成模型时校验 Guid、正整数 sequence、作者 kind、mentions 范围和 UTC 时间；不把正文塞入异常。

### 3.2 sendMessage

提交 `clientSubmissionId`、正文和候选范围。接受 201/200；返回 `{ message, replayed }`。稳定错误 code 转为 feature 内 `RealtimeChatApiError`，不修改公共 `ApiError` 类型。

### 3.3 meetingChatConnection

封装 `HubConnectionBuilder`、Cookie 同源 credentials、自动 reconnect 策略、事件注册和 Dispose。连接回调只向模块派发结构化事件；组件不接触 HubConnection。

连接不得把 meetingCode、Cookie 或正文写入日志。Origin 由浏览器自动发送，客户端不手工设置。

## 4. Reducer 和 MessageStore

### 4.1 State

状态包含 meetingId、generation、messagesById、orderedMessageIds、earliestLoadedSequence、liveBaselineSequence、lastAppliedSequence、highWatermarkSequence、hasEarlierMessages、connection、history、presence、draft、send 和 failedSubmission。乱序消息直接保存在 messagesById 并通过序号差计算缺口，不维护重复的 pendingSequences 集合。

### 4.2 合并

1. 丢弃 meetingId/generation 不匹配的 action。
2. 以 messageId 去重；内容冲突保留首次服务端消息。
3. 按 Sequence ASC 排序。
4. 只在序号连续时推进 lastAppliedSequence；存在缺口则触发 afterSequence 补齐。
5. 维护 earliestLoadedSequence 和 hasEarlierMessages，不把本地 Map 当作权限事实。

## 5. 连接和历史恢复

1. region 挂载递增 generation，创建连接并先缓冲事件。
2. SubscribeMeeting 返回 presence 和 highWatermarkSequence。
3. 首次以 throughSequence=highWatermarkSequence 读取最新页；空页将 liveBaseline/lastApplied 设为 0，非空页从 first/last 初始化并用 HasMoreBefore 设置 hasEarlierMessages；重连以 afterSequence=lastAppliedSequence 同一高水位补齐。
4. 合并历史和高水位后事件；首次最新页以 `firstSequence - 1` 初始化 liveBaselineSequence，以该页最后序号/高水位初始化 lastAppliedSequence，缺口不推进实时游标。
5. 追平后进入 caughtUp/connected；更早历史由 beforeSequence 按需加载。
6. 路由切换、删除、注销、卸载时取消 fetch、解绑 Hub、关闭连接和清理旧状态。

## 6. 发送和重试

- 发送前生成一次 UUID；网络错误重试复用同一 ID。
- 不做乐观消息；服务端响应或广播到达后才插入时间线。
- 201/200 都合并 MessageDto；广播重复按 messageId 去重。
- 明确校验/权限/限流错误不自动重试；`message_append_conflict` 保留正文和同一 ID，允许一次受控重试或手动重试；编辑正文后生成新 ID。
- 失败状态保留正文和候选范围，用户可以手动重试。

## 7. 页面接入

在 `MeetingWorkspacePage` 的消息区插槽渲染 `RealtimeChatRegion`。页面继续负责 meetingId、删除回退、列表刷新和主题级错误；RealtimeChatRegion 只负责消息和实时状态。

`ParticipantList` 接收 presence Map，显示文字状态；不改变 `ParticipantSummary` 的稳定身份。

## 8. 构建命令

~~~powershell
Set-Location Client/roundTableMeeting
pnpm install --frozen-lockfile
pnpm generate:api
pnpm test
pnpm build
Set-Location ../..
~~~

## 9. 完成门

- 只有一个 SignalR 适配文件；组件和页面不接触 HubConnection 或 generated Client。
- 历史高水位、断线补齐、序号缺口和 messageId 去重可自动化验证。
- 发送失败保留草稿和幂等 ID；不显示假发送消息。
- 多会议切换不串消息、presence、连接或迟到响应。
- 客户端构建、Vitest、真实浏览器发送/接收和删除回退通过。
