# 客户端会议工作台模块设计

## 1. 模块职责

客户端会议工作台模块负责会议列表、当前会议选择、详情加载、创建、加入预检、加入确认和删除操作的状态协调。它向路由页面暴露稳定 Hook 与视图模型，不管理登录身份，不实现聊天或实时连接。

## 2. 公共出口

`features/meetingWorkspace/index.ts` 只导出：

- `MeetingWorkspacePage`；
- 页面组合确实需要的公共类型；
- 后续主题需要复用时经审阅确认的参与者摘要类型。

API 函数、内部 reducer、对话框状态和生成模型不从 feature 公共出口暴露。

## 3. 视图模型

### 3.1 MeetingListItem

- `meetingId`；
- `name`；
- `myDisplayName`；
- `isCreator`；
- `joinedAt`。

### 3.2 MeetingDetails

- `meetingId`、`name`、`description`、`createdAt`；
- `currentParticipant`；
- `canDelete`、`canManageInvitation`；
- `participants`。

`canDelete` 和 `canManageInvitation` 是当前用户命令权限的服务端权威值；`ParticipantSummary.isCreator` 只用于列表标记，模块不得由参与者字段反推删除或邀请权限。

`MeetingInvitation` 只在创建成功响应或创建者主动打开邀请信息时短暂保存在内存中，不进入详情缓存。隐藏邀请码、切换 meetingId、删除会议、认证转为匿名或 Provider 卸载时立即清除；详情刷新和焦点刷新不自动重新获取。

### 3.3 ParticipantSummary

- `participantId`；
- `displayName`；
- `type: 'human' | 'agent'`；
- `isCurrentUser`；
- `isCreator`；
- `joinedAt`。

主题三实际数据只有 human，但类型联合为后续 Agent 扩展保留兼容入口。组件不得假造 agent 项。

## 4. 状态模型

模块内部使用 reducer 或等价 Hook 状态，至少表达：

```text
list: idle | loading | success | error
detail: idle | loading | success | notFound | error
create: idle | submitting | error
joinPreview: idle | loading | success | notFound | rateLimited | error
join: idle | submitting | conflict | error
delete: idle | submitting | error
invitation: idle | loading | success | error
```

每次异步调用绑定请求序号或 AbortController。快速切换会议、焦点刷新和手动刷新共享每个 meetingId 的最新请求版本，旧响应不得覆盖新结果；组件卸载或用户取消对话框时取消请求不显示网络错误。

## 5. 初始化和路由同步

1. `useParams()` 读取 meetingId。
2. 初始化加载会议列表。
3. 首次进入 `/` 且没有详情 404、删除或显式无选择恢复标记时，列表非空则导航到服务端顺序中的第一条会议；错误或删除流程返回 `/` 时保持无选择状态。
4. meetingId 变化时先清除上一会议的可见详情和邀请信息，再加载目标详情；只允许展示目标 meetingId 自己的缓存。
5. 详情成功后确认返回的 meetingId 与当前路由一致再更新。
6. 详情 404 时清除当前详情和邀请信息、导航 `/`、记录一次页面级提示并刷新列表；即使列表非空也保持无选择状态。提示由工作台页面承载，不因路由切换丢失。
7. 登录失效由公共 API 错误层通知认证模块，会议模块不自行清理 Cookie。

模块不将当前 meetingId 写入 localStorage。URL 是刷新与分享站内深链接的唯一客户端选择来源；分享该 URL 不授予非成员访问权限。

“显式无选择恢复标记”通过一次性 Router `location.state` 跨 `/meetings/:meetingId -> /` 导航传递，页面挂载后立即读取到 Hook 内存并用 replace 清除 history state。这样即使 sibling route 导致页面 remount，删除/404 后仍不会自动选择第一场会议。禁止写入 localStorage、sessionStorage、URL 查询参数或跨标签页状态，也不得在 history state 中放会议码或会议内容。

## 6. 缓存策略

首版只在 Hook 生命周期内用 `Map<meetingId, MeetingDetails>` 保存最近成功详情，用于切换时减少空白。邀请信息不进入详情缓存。任何写操作成功后：

- 创建：插入或刷新列表，写入新详情；若保存的邀请信息属于新路由目标则保留，否则切换时清除。
- 加入：插入或刷新列表，写入新详情。
- 删除：移除列表项和详情缓存。

缓存不是权限事实。每次页面刷新、直接深链、窗口重新获得焦点和删除后重新访问都必须请求服务端。用户也可以显式刷新当前会议。没有定时轮询、实时同步或跨标签页广播。

## 7. 操作协调

### 7.1 创建

成功顺序固定为更新不含会议码的数据、短暂保存人类成员邀请信息、关闭对话框、导航新会议。邀请信息只用于当前工作台复制，不进入详情缓存；仍需允许后续手动刷新以服务端为准。

### 7.2 加入

预检成功后保存本次会议码仅在对话框内存状态中，关闭对话框立即清除。最终加入请求使用该状态，不写入 URL、localStorage 或全局 Context。

服务端返回 alreadyJoined 时模块使用 `JoinPreview.meetingId` 导航到既有会议，不用本次输入覆盖显示名称；未加入时该 meetingId 仅用于校验最终加入响应和导航，不能在成功加入前读取详情。会议码只在加入对话框的短生命周期内存中保留，模块关闭对话框或完成加入后立即清除。

### 7.3 删除

删除提交时冻结当前 meetingId，并提交确认名称。收到 `202 Accepted` 后从数据、详情缓存和邀请信息缓存中移除并导航 `/`，保持无选择状态。若服务端返回 `meeting_not_found`，按已不可访问处理、移除本地数据并导航 `/`。其他失败说明删除屏障没有确认建立，保留列表项并允许重试。

## 8. 错误适配

模块只依据错误 code 和状态码分支：

- `validation_failed` -> 表单字段；
- `meeting_not_found` -> 预检无效或详情不可访问；
- `display_name_taken` -> 显示名称字段；
- `meeting_join_conflict` -> 自动重试一次，仍失败时显示可重试错误；
- `creation_request_mismatch`、`creation_request_consumed` -> 停止提交并刷新会议列表，提示用户核对之前的创建结果；不自动换 ID 重提；
- `meeting_creator_required` -> 删除权限提示并刷新详情；
- `meeting_join_rate_limited` -> 保留会议码输入，显示稍后重试；
- 网络/未知 -> 通用重试状态。

错误对象、埋点和调试输出不得包含 meetingCode、description 或参与者名称全集。

## 9. 模块非目标

模块不持有消息、连接、未读、通知、AI 任务、账号资料编辑或全局路由状态。
