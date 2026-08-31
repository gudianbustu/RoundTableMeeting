# 会议导航组件实现方案

## 1. 文件和输入

文件为 `features/meetingWorkspace/components/MeetingNavigation.tsx`。组件接收 `meetings`、`selectedMeetingId`、`listState`、`onSelectMeeting`、`onCreate` 和 `onJoin`，不调用 API。

## 2. 渲染

列表项显示会议名称、当前用户显示名称、首次加入时间和创建者文字标记。当前项使用 `aria-current="page"`。不显示会议码、未读、消息摘要、参与者数量或在线状态。

## 3. 响应式和操作

桌面固定侧栏宽度，移动端折叠为可展开选择区。创建和加入使用明确文字按钮；列表选择使用链接/按钮语义。长名称换行，不改变列宽。

## 4. 测试

覆盖空列表、loading、error、选中项、创建/加入回调、移动断点和长名称，对应 `MW-V001`、`MW-V002`、`MW-V025`。
