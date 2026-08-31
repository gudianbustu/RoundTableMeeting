# 消息编辑器组件实现方案

## 1. 组件接口

Props 至少包括 `participants`、`draft`、`sendState`、`failedSubmission`、`onDraftChange`、`onSubmit` 和 `onRetry`。组件不调用 API、不生成服务器 Sequence。

## 2. 输入和提及

文本域维护原始正文和光标位置；候选选择插入 `@displayName`。提交时父模块规范化正文并重新计算候选范围。未知 `@` 不阻止发送。

## 3. 状态

发送中禁用重复提交但保留正文；失败显示重试并复用原 ClientSubmissionId；成功由父模块清空草稿。按钮、重试和连接动作提供 aria-label/tooltip。
