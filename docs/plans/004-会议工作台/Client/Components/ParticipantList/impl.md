# 参与者列表组件实现方案

## 1. 文件和输入

文件为 `features/meetingWorkspace/components/ParticipantList.tsx`。组件接收只读 `ParticipantSummary[]`，不调用 API、不订阅实时状态。

## 2. 渲染

严格按服务端返回顺序显示显示名称、类型文字、创建者标记、当前用户标记和加入时间。`human` 显示人类文字，`agent` 预留 AI 文字；类型不能只用颜色表达。当前主题不伪造 agent 项。

## 3. 边界和测试

不提供踢出、禁言、退出、改名、私聊或消息提及。覆盖 `MW-V023`、`MW-B005` 和长名称/窄屏布局场景。
