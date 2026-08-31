# 客户端会议工作台模块实现方案

## 1. 文件和出口

模块文件为 `features/meetingWorkspace/hooks/useMeetingWorkspace.ts`、`types/meetingWorkspace.ts` 和 `api/` 下七个适配器。`index.ts` 只导出 `MeetingWorkspacePage`、必要视图模型和经审阅的 Hook 类型，不导出生成模型或 Context。

## 2. 类型

~~~ts
type MeetingListItem = {
  meetingId: string
  name: string
  myDisplayName: string
  isCreator: boolean
  joinedAt: string
}

type ParticipantSummary = {
  participantId: string
  displayName: string
  type: 'human' | 'agent'
  isCurrentUser: boolean
  isCreator: boolean
  joinedAt: string
}

type MeetingDetails = {
  meetingId: string
  name: string
  description: string | null
  createdAt: string
  currentParticipant: ParticipantSummary
  canDelete: boolean
  canManageInvitation: boolean
  participants: ParticipantSummary[]
}

type MeetingInvitation = { meetingId: string; meetingCode: string }
~~~

`canDelete` 和 `canManageInvitation` 是服务端命令权限权威值；`isCreator` 只用于列表标记和参与者展示。

## 3. 状态和并发

每个异步操作有 AbortController 和递增 request version。详情响应只有在 version、meetingId 和当前路由都匹配时才写入 state。切换会议先清理可见详情和邀请信息，再读取目标。

详情 404、删除 202/404 时通过一次性 `location.state.suppressMeetingAutoSelect` 导航到 `/`。目标页面将标记读入 Hook 的 `suppressAutoSelect` 内存后立即 replace 清除 history state；用户主动选会、创建/加入成功或卸载时清除 Hook 标记。禁止写入 localStorage、sessionStorage、URL 或跨标签页状态；history state 中也不能放会议码或会议详情。

## 4. 操作实现

- 创建：打开时生成 creationRequestId；成功接受 201/200；mismatch/consumed 先刷新列表，不自动换 ID。
- 预检：只保存对话框内 meetingCode；返回 meetingId，alreadyJoined 时用于直接导航。
- 加入：发送会议码和显示名称；JoinConflict 自动重试一次，DisplayNameTaken 定位字段。
- 邀请：仅当 `canManageInvitation` 为 true 且用户点击显示邀请码时请求；结果只存内存。
- 删除：发送 confirmationName；202 或 404 清除数据、详情缓存、邀请信息并回 `/`，失败保留列表。
- 限流错误：由 feature API 适配器从 Kiota `responseHeaders` 转换为 `MeetingWorkspaceApiError.retryAfterSeconds`，不修改共享 `ApiError`；页面只消费该 feature 扩展字段。

## 5. 测试映射

模块测试覆盖 `MW-V001`–`MW-V026`；API 适配器覆盖 `MW-K001`–`MW-K007` 的生成模型和错误转换。模块不得创建消息、实时订阅或模拟会议。
