# 消息时间线组件设计

## 1. 组件职责

`MessageTimeline` 展示统一 `Message` 读模型，不关心消息来自 HTTP、SignalR、MCP 还是后台处理器。它只根据 author.kind、mentions、sequence 和 createdAt 渲染可访问时间线。

## 2. 渲染规则

- Human、Agent、System 使用文字类型标签和不同布局语义，不能只依赖颜色。
- 作者显示名称来自服务端快照；系统消息使用固定系统标签。
- mentions 依据服务端范围切分为高亮文本节点；非法范围降级为普通文本，不抛异常。
- sequence 可作为稳定 DOM key 的诊断信息，但 React key 使用 messageId。
- 空消息历史显示明确空状态，不创建占位气泡。

## 3. 交互

组件不负责发送、重试、滚动策略或连接建立，只发出“到达底部”“请求更早历史”和“新消息提示被点击”等回调。
