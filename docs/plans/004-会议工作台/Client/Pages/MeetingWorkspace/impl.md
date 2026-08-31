# 会议工作台页面实现方案

## 1. 文件和组件树

文件为 `features/meetingWorkspace/pages/MeetingWorkspacePage.tsx`。页面调用 `useMeetingWorkspace`，组合：

~~~text
MeetingWorkspacePage
  MeetingNavigation
  main
    MeetingHeader
    ParticipantList
    ConversationRegionPlaceholder
  CreateMeetingDialog
  JoinMeetingDialog
  DeleteMeetingDialog
~~~

页面拥有三个对话框的 open、confirm 和 error 状态；`MeetingHeader` 只通过回调请求打开删除确认。

`DeleteMeetingDialog` 与 `ConversationRegionPlaceholder` 作为本页面文件内的私有函数组件实现，不从 feature 公共出口导出。

## 2. 加载状态

- 列表 loading：导航区域显示固定宽度 Skeleton，主区域显示工作台加载状态。
- 列表 error：显示可重试错误，不渲染空列表。
- 无选择：显示创建/加入空状态；错误恢复后的无选择状态不自动选会议。
- 详情 loading：只替换主区域，导航保持可操作。
- 详情 404：清除主区域，携带一次性 `suppressMeetingAutoSelect` history state 回 `/`，显示页面级不可访问提示并刷新列表；页面消费后立即清除 history state。
- 详情 network error：保留当前会议已确认详情并显示重试。

## 3. 页面交互

首次正常进入 `/` 时，列表有数据自动打开第一场；用户从导航点击会议时导航到 `/meetings/:meetingId`。创建/加入成功后刷新列表并导航目标会议。删除成功或已不可访问时回 `/`，不自动选择剩余会议。

页面监听 `visibilitychange` 或 focus 事件，当前有会议时调用模块刷新详情；不会自动加载邀请码。

## 4. 空插槽

`ConversationRegionPlaceholder` 只显示后续主题接入的空状态。不得渲染输入框、发送按钮、消息计数、假消息或实时连接指示。

## 5. 验收映射

实现对应 `MW-V001`–`MW-V026` 和 `MW-B001`–`MW-B013`。页面测试必须确认不同 meetingId 不串详情、参与者和邀请码，并确认 location state 消费后被 replace 清除。
