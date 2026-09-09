# 邀请入会对话框实现方案

## 1. 文件和输入

文件为 `features/meetingWorkspace/components/InviteMeetingDialog.tsx`，提示词构建为 `features/meetingWorkspace/invitePrompts.ts`。组件接收 `open`、可选 `MeetingInvitation`、`loading`、`onCancel`，以及可覆盖的 `siteUrl`（默认 `window.location.origin`）和 `mcpUrl`（默认 `origin + /mcp`），覆盖参数仅供测试注入。

## 2. 渲染

内部状态为步骤 `select | human | agent` 和 `agentName`，`open` 变化时重置。选择步骤渲染两个带图标和说明的整行按钮；人类步骤用 `Typography` copyable 展示网站地址和会议码；AI 步骤渲染 `aria-label` 为“Agent 显示名称”的输入框，名称非空后渲染 `code` 样式的提示词段落，并在底部右侧渲染显式“复制提示词”按钮（名称为空时禁用），复制经 Clipboard API 写入完整提示词，成功或失败用 `message.useMessage` 反馈。无会议码时按 `loading` 渲染加载中或失败文案。

## 3. 页面接入

工作台页面持有 `inviteOpen` 状态；“邀请入会”入口在打开对话框时若内存中无邀请信息则先调用 `revealInvitation()`，`loading` 取 `无邀请信息 && operationLoading`。会议码加载失败时页面级 Alert 展示错误详情，对话框内只保留失败引导。

## 4. 开发代理

`vite.config.ts` 增加 `/mcp` 到 `http://localhost:5043` 的代理，使开发态与生产态的同源 MCP 地址 `origin + /mcp` 都成立。

## 5. 测试

覆盖：普通成员不渲染邀请入口；创建者头部以可访问名称渲染全部图标按钮；人类步骤展示网站地址与会议码；AI 步骤名称为空不出现提示词、输入名称后生成包含 MCP 地址、显示名称和会议代码的提示词且“复制提示词”按钮可用；`buildAgentInvitePrompt` 纯函数断言关键指令行。
