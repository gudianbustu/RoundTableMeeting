# 会议工作台客户端设计

## 1. 文档定位

本文定义主题三在 React 客户端的 feature 接入、路由、工作台布局、数据加载、选择恢复、响应式行为和错误适配。认证边界继续使用主题二的 `AuthGate`、`AuthenticatedLayout`、Cookie 会话和当前用户 Context。

本主题只交付会议空间与静态参与者工作台，不提前实现聊天、实时状态、AI 或未读提示。

## 2. 工程接入

在 `Client/roundTableMeeting/src/features/` 新增：

```text
meetingWorkspace/
  api/
  components/
  hooks/
  pages/
  types/
  index.ts
```

路由级 `AuthenticatedLayout` 保持在 `app/layouts/`。主题三的工作台布局、会议导航和业务组件全部归属于 `meetingWorkspace` feature，不把会议状态放进 `shared` 或认证 Provider。

## 3. 路由

| 路径 | 页面 | 说明 |
| --- | --- | --- |
| `/` | `MeetingWorkspacePage` | 登录后默认工作台；无会议选择时显示空状态 |
| `/meetings/:meetingId` | `MeetingWorkspacePage` | 当前会议深链接，必须是当前用户成员 |
| `/system/status` | 既有系统状态页 | 继续按框架约定公开 |

历史认证占位页由主题三替换为 `MeetingWorkspacePage`，但保留主题二的认证门和认证后布局。未知、删除中或无权访问的会议深链接不展示会议内容；页面导航到 `/` 的无选择状态，刷新会议列表并显示页面级不可访问提示。列表即使非空也不自动进入其他会议。

当前选择只由 `:meetingId` 表达，不把会议码放在 URL、浏览器历史或本地存储。用户从列表切换会议时使用 `navigate('/meetings/' + meetingId)`；无选择状态使用 `/`。

## 4. 客户端状态

`useMeetingWorkspace` 负责协调：

- `listState`：会议列表请求中的 Loading、Success、Error；
- `selectedMeetingId`：来自路由参数的选择；
- `detailState`：当前会议详情 Loading、Success、NotFound、Error；
- `createState`、`joinPreviewState`、`joinState`、`deleteState`：对话框操作状态；
- `invitationState`：人类成员邀请信息的 Idle、Loading、Success、Error；
- `lastActionError`：最近一次可展示的操作错误。

服务端是唯一事实来源。列表和详情成功后写入当前 feature 内存状态；路由切换时只保留缓存用于避免闪烁，任何刷新、深链接或重连都必须重新请求服务端。首版不引入 TanStack Query、Zustand 或跨页面全局会议 Store。

## 5. API 适配

`features/meetingWorkspace/api/` 提供：

- `listMeetings()`；
- `getMeetingDetails(meetingId)`；
- `getMeetingInvitation(meetingId)`；
- `createMeeting(input)`；
- `previewJoinMeeting(input)`；
- `joinMeeting(input)`；
- `deleteMeeting(meetingId, confirmationName)`。

`getMeetingInvitation()` 使用 POST 且只在创建者主动选择“显示邀请码”或创建成功后调用；详情刷新、焦点刷新和会议切换不得自动调用。其失败只影响邀请区域，不清空会议详情。`createMeeting()` 将所有 `2xx` 视为成功，同时接受首次创建的 `201 Created` 与幂等重放的 `200 OK`，二者都写入会议详情和邀请信息。`deleteMeeting()` 实际提交 `confirmationName` 到删除命令端点。

适配层调用公共 Kiota Client，将生成模型转换为 feature 自己的 `MeetingListItem`、`MeetingDetails`、`MeetingInvitation`、`ParticipantSummary` 和 `JoinPreview`。页面和组件不得直接依赖 `api/generated/`，也不得自行拼接会议码 URL。

标准错误按稳定 `code` 分支：`validation_failed` 映射字段错误，`meeting_not_found` 映射不可见或不存在，`display_name_taken` 映射名称冲突，`meeting_join_conflict` 触发一次新请求重试，`creation_request_mismatch` 和 `creation_request_consumed` 都先停止提交、刷新会议列表并提示“之前的创建可能已经成功，请先检查会议列表”；客户端不能自动生成新 ID 并再次创建，只有用户确认需要另一场会议并重新打开创建对话框时才生成新 ID。`meeting_creator_required` 映射无删除权限，`meeting_join_rate_limited` 显示 `Retry-After` 对应的可重试时间，不显示剩余次数。网络错误显示服务不可访问，不清空当前同一会议已经确认的详情。

## 6. 页面结构

`MeetingWorkspacePage` 组合以下区域：

1. `MeetingNavigation`：会议列表、创建按钮、加入按钮和当前选择。
2. 当前会议区域：无选择时显示空状态；有选择时显示 `MeetingHeader`、参与者区域和消息区插槽。
3. `ParticipantList`：显示全部静态参与者，区分人类与预留的 AI 类型。
4. 创建、加入和删除对话框通过组件状态挂载，不嵌套页面卡片。

主题二的 `AuthenticatedLayout` 继续提供顶部当前用户栏和 Outlet；工作台页面不重复显示账号身份或退出按钮。

## 7. 工作台布局

### 7.1 桌面端

使用两栏主布局：左侧固定宽度会议导航，右侧当前会议内容。右侧再使用顶部会议头部、参与者摘要区和消息区插槽的垂直结构。会议导航不使用浮动卡片堆叠，当前会议内容是页面主区域。

左侧至少展示会议名称、创建者标记和当前用户首次加入时间；不展示未读数、最后消息、会议码或 AI 状态。当前会议头部展示名称、可选背景、创建时间、当前用户的会议内显示名称；所有已入会人类成员看到会议码和邀请入口，仅创建者看到删除操作。

### 7.2 移动端

在 720px 以下将左侧导航折叠为顶部可展开的会议选择区；当前会议内容占满宽度。创建、加入、人类成员复制会议码和删除按钮保持可触达，按钮文字不得因窄屏溢出。参与者列表改为全宽分组列表。

## 8. 数据加载和切换

1. 页面进入时先加载会议列表。
2. 有 URL 会议 ID 时并行或随后加载详情；列表加载失败时不能伪造空列表。
3. 首次登录进入 `/` 且没有错误恢复上下文时，列表非空则自动导航到服务端排序后的第一条会议；由详情 404 或删除流程返回 `/` 时保持无选择状态，不再自动跳转。
4. 切换 meetingId 时立即清空上一会议的可见详情和邀请信息；若目标会议已有详情缓存，可以显示该目标缓存并标记正在刷新，否则显示局部加载状态。任何时候都不能在新路由下继续显示上一会议的数据。
5. 详情返回 `404` 时清理无效 URL 选择、导航 `/`、保留并刷新列表并显示页面级提示；返回 `401` 时交给认证门；删除成功时移除列表项并导航 `/`，不自动选择剩余会议。

客户端不依赖浏览器标签页同时打开会议。不同会议的详情、参与者和后续消息插槽始终按当前 meetingId 隔离。用户点击刷新图标或页面从后台重新获得焦点时，客户端重取当前会议详情，使主题三在没有实时连接的情况下也能看到新加入的参与者；不轮询、不建立实时订阅。

## 9. 创建流程

`CreateMeetingDialog` 打开时通过 `crypto.randomUUID()` 生成一次 `creationRequestId`，提供会议名称和可选讨论主题/背景说明。同一对话框内的网络重试始终复用该 ID，成功或取消关闭后清除；用户编辑已经提交过的字段后再次提交仍复用原 ID，服务端 mismatch 时客户端先刷新列表并停止自动创建，不换 ID 重提。没有人数上限、密码、有效期、审批或预约字段。提交时按钮进入 loading，成功后关闭对话框、刷新列表并导航到新会议；失败保留用户输入和请求 ID，并显示字段错误或通用错误。

创建成功后，响应中的会议码邀请信息在新会议头部立即展示；之后重新打开会议时，创建者需要主动选择“显示邀请码”。复制失败应显示手动复制提示，不把码写入 URL、日志、本地存储或详情缓存。隐藏邀请码、切换会议、删除会议、退出登录或组件卸载时立即清除邀请信息内存状态。

## 10. 加入流程

`JoinMeetingDialog` 分为输入会议码、预览确认和提交三步：

1. 用户输入或粘贴会议码，客户端只去除首尾空白，按标准 GUID `D` 格式解析并以小写 `D` 格式展示；大小写不敏感。内部空白、缺失连字符、其他 Guid 格式或非法字符不自动修复，服务端仍执行最终校验。
2. 调用预检，显示会议名称、背景和默认显示名称。
3. 用户确认或修改会议内显示名称。
4. 调用最终加入；服务端再次验证会议码和名称。
5. 成功后刷新列表并导航到会议。

若已加入，预检显示原显示名称并进入可打开状态，不允许借此修改名称。名称冲突时保留用户输入，提示更换后重试。最终加入返回 `meeting_join_conflict` 时，客户端在新请求中自动重试一次；再次冲突才显示通用重试提示。无效码、会议不可用和限流使用稳定错误状态，不回显敏感服务端细节。

## 11. 删除流程

`MeetingHeader` 仅向创建者显示删除图标按钮和 tooltip。点击后由 `MeetingWorkspacePage` 挂载的删除确认对话框打开；Header 只触发回调，不持有对话框状态。对话框显示会议名称，明确列出当前主题已有参与者记录和会议码将永久不可访问，并预留后续主题把消息、AI 任务和连接终止后果追加到同一提示；要求输入完整会议名称才能提交。比较为 Trim + NFC 后区分大小写的 Ordinal 精确匹配，客户端提示大小写敏感且不提供自动填充。`confirmationName` 随删除命令发送到服务端再次校验，对话框明确没有恢复操作。

删除提交期间禁用重复提交和当前会议操作。服务端返回 `202 Accepted` 后，客户端立即清理列表、详情缓存、邀请信息缓存并导航 `/`；后台物理清理不阻塞界面。只有删除屏障未成功建立时才保留会议并显示失败；服务端若返回 `meeting_not_found`，按已经不可访问处理并导航 `/`。两种成功收敛都不自动进入其他会议。

## 12. 页面状态与可访问性

- 首次加载使用页面级加载状态；局部切换使用区域级 Skeleton 或 Spin。
- 会议列表为空显示创建或加入入口，不显示模拟会议。
- 会议不存在、已删除或非成员显示统一不可访问状态和返回会议列表操作。
- 网络错误只保留当前 meetingId 已有的成功数据并标记可能过期，提供重试。
- 对话框操作支持键盘提交、Escape 取消、焦点回收和明确的 `aria-label`。
- 人类成员的会议码复制按钮必须有可理解的 tooltip；删除使用危险操作语义和二次确认。
- 参与者类型使用文字和视觉标记同时表达，不只依赖颜色。

## 13. 与后续主题的插槽契约

工作台为主题四预留 `conversationRegion` 插槽：

- 当前主题渲染“消息功能将在后续主题接入”的空状态；
- 不提供发送框、假消息、未读数或实时连接占位；
- 插槽接收当前 `meetingId` 和成员上下文，不通过全局单例读取；
- 主题四替换插槽后，会议导航、会议头部、参与者授权和删除屏障继续复用。

后续 AI 主题可向 `ParticipantList` 增加类型和状态字段，但主题三的 Human 参与者显示和名称唯一语义不能改变。

## 14. 测试边界

客户端测试覆盖：首次进入自动选择与 404/删除后保持无选择；页面级不可访问提示；创建 200/201 双成功、mismatch/consumed 先刷新列表且不自动重提；会议码首尾空白 Trim、标准 GUID `D` 格式解析、大小写不敏感和非法格式拒绝；预检 meetingId 的 alreadyJoined 导航；JoinConflict 自动重试一次并分别收敛为恢复身份或名称冲突；Retry-After 提示；邀请信息显式加载与内存清除；详情切换不串会；删除确认大小写敏感、后果提示和页面级对话框挂载；普通人类成员可见并复制邀请码；`human | agent` 类型渲染；移动端关键断点和无模拟数据。具体测试矩阵在 `testPlan.md` 中编写。

## 15. 非目标

本客户端设计不添加聊天消息、SignalR、在线状态、AI MCP、未读状态、通知、会议设置页或用户资料页。
