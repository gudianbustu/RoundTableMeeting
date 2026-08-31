# 密码重置确认对话框实现方案

## 1. 文件和 Props

文件固定为 `features/userManagement/components/ResetPasswordDialog.tsx`。Props：user、open、loading、error、onConfirm、onCancel。

## 2. Modal

使用 Modal，title=“重置密码”，确认按钮文案为“重置为 123456”，加载中禁用取消、关闭和重复确认。

正文显示目标用户名/昵称、固定密码 `123456` 和“已有登录会话不会立即失效”。不使用额外密码输入框。

## 3. 错误与测试

user_not_found 由页面关闭；origin/network/unknown 显示安全 Alert，失败时保持对话框并允许重试。覆盖目标文案、确认/取消、loading 禁止关闭和失败保留。
