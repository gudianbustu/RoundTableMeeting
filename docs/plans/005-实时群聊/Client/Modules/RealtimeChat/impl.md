# 实时群聊客户端模块实现方案

## 1. 状态和 reducer

以 `createInitialChatState(meetingId, generation)` 创建状态。Reducer action 固定携带 meetingId/generation，包含 `historyPageMerged`、`messageEventReceived`、`presenceChanged`、`connectionStateChanged`、`sendStarted`、`sendSucceeded`、`sendFailed`、`meetingDeleted` 和 `reset`。

Reducer 不执行副作用；fetch、SignalR 和滚动由区域 Hook 协调。

## 2. MessageStore 算法

`mergePage` 和 `mergeEvent` 共用 `upsertMessage`：

1. messageId 已存在则比较 sequence/meetingId，重复直接忽略。
2. 新消息插入 Map 和有序 ID 列表。
3. 若 sequence 小于当前最早值，更新 earliest；空历史把 liveBaseline/lastApplied 初始化为 0，首次非空最新页以 firstSequence-1 初始化 liveBaseline；若实时段 sequence 大于 lastApplied+1，直接由有序 Map 的序号差表达缺口。
4. 缺口填补后从 liveBaseline 开始按连续序列推进 lastApplied；liveBaseline 之前的更早历史不参与实时缺口判断。
5. meetingId 不匹配直接丢弃。

组件使用 selector 得到稳定数组，不在渲染阶段排序原始 state。

## 3. useRealtimeChat

Hook 内持有 AbortController、generation ref、连接 Dispose 函数和当前提交对象。effect 依赖 meetingId；清理函数先递增 generation，再关闭旧连接和取消请求。

连接流程：create -> start/buffer -> subscribe -> dispatch snapshot -> load through/after -> drain buffer -> caughtUp。自动重连期间保留已确认消息和草稿，不把断线误报为消息丢失。

## 4. Mention candidates

候选根据当前 `ParticipantSummary[]` 过滤名称，不把 Agent 视为不可提及。插入文本后以 NFC+Trim 后正文重新计算 offset/length；组件卸载或正文编辑会清除过期候选。

## 5. 错误处理

- 401 通知认证模块，不由聊天模块自行导航登录页。
- 404 触发页面回退 `/` 并清除当前 meeting 状态。
- 429 读取 Retry-After，保留草稿和 ID，显示可重试时间。
- Hub `meeting_not_found` 进入 rejected 并触发工作台回退；自动重连耗尽进入 disconnected。
- 网络错误显示重连/发送重试；AbortError 静默处理。

## 6. 模块出口

只导出 `RealtimeChatRegion`、只读消息/作者/mention/presence 类型和区域所需 Hook；生成模型、HubConnection、reducer action 和 API 错误细节保持内部。
