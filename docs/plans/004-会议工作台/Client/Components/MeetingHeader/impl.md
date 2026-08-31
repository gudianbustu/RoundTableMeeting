# 会议头部组件实现方案

## 1. 文件和输入

文件为 `features/meetingWorkspace/components/MeetingHeader.tsx`。组件接收 `MeetingDetails`、可选 `MeetingInvitation`、`invitationState`、`onLoadInvitation`、`onClearInvitation`、`onRefresh` 和 `onDeleteRequested`。

## 2. 渲染和权限

显示会议名称、背景、创建时间和当前显示名称。`canManageInvitation` 为 true 时显示“显示邀请码”，邀请码加载成功后显示等宽会议码、复制和隐藏按钮；普通成员不渲染邀请码区域。`canDelete` 为 true 时显示危险删除图标按钮。权限只来自详情字段，不从 participants 推导。

## 3. 操作

复制调用 Clipboard API，成功/失败显示反馈；隐藏通知父模块清除内存邀请码。刷新只请求详情，不请求邀请码。删除只触发 `onDeleteRequested()`，不在 Header 内挂载对话框。

## 4. 测试

覆盖 `MW-V008`、`MW-V009`、`MW-V020`、`MW-B003`、`MW-B007`，确认普通成员看不到邀请码和删除按钮。
