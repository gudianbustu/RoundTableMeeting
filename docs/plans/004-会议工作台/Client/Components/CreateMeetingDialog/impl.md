# 创建会议对话框实现方案

## 1. 文件和输入

文件为 `features/meetingWorkspace/components/CreateMeetingDialog.tsx`。组件接收 `open`、`submitting`、`fieldErrors`、`error`、`onSubmit` 和 `onCancel`，不直接调用 API。

## 2. 表单和幂等

每次从 closed 变为 open 时生成 `creationRequestId = crypto.randomUUID()`。表单包含 name 和可选 description；服务端最终执行 Trim + NFC 和长度验证，客户端设置 maxLength 只改善体验。

同一次打开期间提交和网络重试复用 ID。收到 `creation_request_mismatch` 或 `creation_request_consumed` 后停止提交、通知父模块刷新列表和提示用户检查结果；不自动换 ID。用户关闭后重新打开才生成新 ID。

## 3. 状态

提交时禁用重复提交；字段错误保留输入，网络错误显示重试。成功回调交给页面关闭、刷新列表、导航和显示邀请码。取消时清理表单、错误和 requestId。

## 4. 测试

覆盖 `MW-V010`–`MW-V013`、`MW-B002`，确认 201/200、断网重试、mismatch/consumed 和无模拟数据。
