# 消息时间线组件实现方案

## 1. 组件接口

Props 至少包括 `messages`、`loading`、`hasEarlierMessages`、`onLoadEarlier`、`onAtBottomChange` 和 `onNewMessageNoticeClick`。组件不拥有消息 state 或 SignalR 连接。

## 2. 文本渲染

实现纯函数 `splitMentionRanges(content, mentions)`：过滤越界/重叠范围，按 startOffset 排序，切分普通文本和 mention 文本；所有结果作为 React 文本节点输出，不使用 HTML 注入。

## 3. 视觉语义

Human、Agent、System 显示文字类型标签；作者、时间、序号和正文可被屏幕阅读器读取。长正文换行，列表 item 使用 messageId key。
