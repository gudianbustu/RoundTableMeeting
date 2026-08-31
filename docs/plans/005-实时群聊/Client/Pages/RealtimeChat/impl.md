# 实时群聊页面区域实现方案

## 1. 组合结构

`RealtimeChatRegion` 组合 `ChatConnectionStatus`、`MessageTimeline`、历史加载按钮、新消息提示和 `MessageComposer`。它接收会议工作台提供的 meetingId、参与者摘要和 `onMeetingUnavailable` 回调。

## 2. 加载和滚动

- 首次历史加载显示骨架；空历史显示空状态。
- 用户位于底部附近时自动跟随新消息，否则显示新消息提示。
- 加载更早历史保持滚动锚点，避免顶部插入导致视口跳动。
- 会议删除或 404 清空消息并只触发 `onMeetingUnavailable`；`MeetingWorkspacePage` 是唯一执行导航、列表刷新和 suppressAutoSelect 的所有者，避免双份回退。

## 3. 页面状态文本

连接中、重连中、已断开、正在补齐和发送失败均使用用户可理解的状态文案；不显示 EventId、Sequence 缺口、租约或数据库诊断。

## 4. 完成门

页面区域没有独立路由、没有聊天专用认证、没有本地持久化消息；切换、卸载和删除都会释放连接和请求。
