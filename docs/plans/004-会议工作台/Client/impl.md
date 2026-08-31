# 会议工作台客户端实现方案

## 1. 文档定位

本文将[客户端设计](design.md)具体化为 feature 目录、API 适配、路由、工作台 Hook、响应式布局、错误恢复和构建步骤。模块、页面和组件细节见本目录下的对应 `impl.md`，测试编号见[测试方案](../testPlan.md)。

本主题不新增状态管理库，不实现聊天、实时订阅、在线状态、MCP 或 AI。

## 2. 目录和文件

~~~text
Client/roundTableMeeting/src/features/meetingWorkspace/
  api/listMeetings.ts
  api/getMeetingDetails.ts
  api/getMeetingInvitation.ts
  api/createMeeting.ts
  api/previewJoinMeeting.ts
  api/joinMeeting.ts
  api/deleteMeeting.ts
  components/MeetingNavigation.tsx
  components/CreateMeetingDialog.tsx
  components/JoinMeetingDialog.tsx
  components/MeetingHeader.tsx
  components/ParticipantList.tsx
  hooks/useMeetingWorkspace.ts
  pages/MeetingWorkspacePage.tsx
  types/meetingWorkspace.ts
  index.ts
~~~

`DeleteMeetingDialog` 和 `ConversationRegionPlaceholder` 是 `MeetingWorkspacePage.tsx` 内的局部无复用 UI 块，不创建独立文件，也不增加新的组件文档；出现真实跨页面复用后再提取。

沿用公共 `src/api/client/apiClient.ts`、`src/api/errors/toApiError.ts` 和现有 Ant Design/React Router/Vitest 版本。页面和组件不得 import `api/generated`。

## 3. API 适配

每个 API 文件只负责调用公共 Kiota Client、组装请求 DTO、转换生成模型和抛出会议 feature 的 `MeetingWorkspaceApiError`。该类型扩展现有公共 `ApiError`，只额外增加可选 `retryAfterSeconds`；不修改 002 公共错误契约。

- `listMeetings()` -> `MeetingListItem[]`。
- `getMeetingDetails(meetingId)` -> 不含会议码的 `MeetingDetails`。
- `getMeetingInvitation(meetingId)` -> `MeetingInvitation`，只在创建者按需调用。
- `createMeeting(input)` -> `CreateMeetingResult`；适配层接受 201 Created 和 200 Replayed 两种 2xx。
- `previewJoinMeeting(input)` -> `JoinPreview`。
- `joinMeeting(input)` -> `MeetingDetails`。
- `deleteMeeting(meetingId, confirmationName)` -> 202/void。

适配层从 Kiota 错误的稳定 `code`、HTTP status 和 response headers 转换错误，不解析 detail 文案。`MeetingWorkspaceApiError = ApiError & { retryAfterSeconds?: number }` 只在本 feature 的 `api/` 内定义；429 的 `Retry-After` 从 Kiota 错误对象的 `responseHeaders` 读取，支持非负秒数和 HTTP-date 两种标准格式，再把结果附加到公共 `toApiError` 输出上。现有 002 公共 `ApiError` 类型不修改，其他 feature 不感知会议加入限流细节。

## 4. 路由和 App 接入

将现有认证路由的 `/` 页面替换为 `MeetingWorkspacePage`，新增 `/meetings/:meetingId`：

~~~tsx
{
  element: <AuthGate />,
  children: [{
    element: <AuthenticatedLayout />,
    children: [
      { index: true, element: <MeetingWorkspacePage /> },
      { path: '/meetings/:meetingId', element: <MeetingWorkspacePage /> },
    ],
  }],
}
~~~

保留 `/login` 和公开 `/system/status`。会议码不进入路由参数、查询字符串、history state 或 localStorage。

## 5. useMeetingWorkspace

Hook 使用 `useReducer` 或等价状态组合，维护：

- meetings、selectedMeetingId、detailsById；
- invitationState 和仅当前 meetingId 的 invitation 内存值；
- create、joinPreview、join、delete 操作状态；
- `suppressAutoSelect` 内存布尔标记，以及一次性 Router `location.state.suppressMeetingAutoSelect` 导航标记；
- 每个 meetingId 的 detail request version 和 AbortController。

初始化流程：

1. 读取路由 meetingId。
2. 加载列表。
3. 页面先读取 `location.state.suppressMeetingAutoSelect` 到 Hook 内存，并立即使用 replace 清除 location state；只有无 meetingId、无 suppressAutoSelect 且属于首次正常进入时，列表非空才导航第一条。
4. 有 meetingId 时只加载该会议详情；旧响应必须检查 request version 和当前路由。
5. 详情 404 或删除后使用 `navigate('/', { replace: true, state: { suppressMeetingAutoSelect: true } })`，清空详情/邀请码；目标页面消费一次性标记后保持无选择。
6. 用户主动选择会议、创建/加入成功或 Hook 卸载时清除 suppressAutoSelect。

页面重新获得焦点、点击刷新或切换目标会议时，只重取当前详情，不触发实时订阅；邀请信息不会随详情刷新自动取得。

## 6. 缓存和凭证生命周期

- 详情缓存只保存在 Hook 内存 Map，按 meetingId 隔离，不作为权限事实。
- 邀请信息不进入详情缓存；创建成功后可暂存于当前新会议内存状态，重新进入会议需点击“显示邀请码”。
- 隐藏邀请码、切换会议、删除会议、认证转匿名、组件卸载时清空邀请信息。
- 会议码不写入 localStorage、sessionStorage、URL、日志、埋点或错误对象。
- 所有会议 API 由服务端 `no-store/no-cache`，客户端不主动持久化响应。

## 7. 创建、加入和删除

### 7.1 创建

对话框打开时生成 `crypto.randomUUID()` 的 creationRequestId。同一打开周期内所有提交和网络重试复用该 ID。201/200 都更新列表、详情和创建者邀请信息；mismatch/consumed 停止提交并刷新列表，不能自动换 ID。

### 7.2 加入

会议码输入或粘贴后只 Trim 首尾空白，严格解析 GUID `D` 格式并以小写 `D` 格式展示，大小写不敏感；不自动修复内部空白、缺失连字符、大括号或其他 Guid 格式。预检成功后允许修改显示名称；最终加入由服务端再次校验。JoinConflict 最多自动重试一次，第二次冲突停止重试。

### 7.3 删除

页面持有删除确认对话框状态，提交完整 confirmationName。202 表示删除屏障刚建立；404 表示会议已不可访问或删除屏障已存在。两种结果都清除当前数据、详情缓存、邀请信息和一次性路由状态并导航 `/`；其他错误保留会议并允许重试。

## 8. 样式和可访问性

- 复用 Ant Design Button、Modal、Form、Input、List、Typography、Skeleton、Alert 和 Tooltip。
- 工作台桌面为固定导航列 + 主内容列；720px 以下导航折叠到顶部。
- 图标按钮提供 tooltip 和 `aria-label`；删除使用危险样式，复制使用 Copy 图标，刷新使用 Reload 图标。
- 会议码使用等宽样式、可完整选择，长名称和显示名称允许换行。
- 不使用整页嵌套卡片，不显示实现说明或假消息。

## 9. 构建和检查

~~~powershell
Set-Location Client/roundTableMeeting
pnpm install --frozen-lockfile
pnpm generate:api
pnpm test
pnpm build
~~~

实现前按 `MW-V*`、`MW-B*` 和 `MW-K*` 执行客户端、浏览器和生成契约测试。
