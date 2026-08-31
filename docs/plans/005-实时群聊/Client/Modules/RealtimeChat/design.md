# 实时群聊客户端模块设计

## 1. 模块职责

模块负责当前会议的消息历史、SignalR 连接、增量合并、发送幂等、presence 和连接错误状态。会议选择和登录身份由既有 feature 负责。

## 2. 状态所有权

模块以 `meetingId` 为状态边界。每个挂载实例只拥有一个当前会议；缓存可以在内存 Map 中按 meetingId 保存，但路由切换时必须先终止旧连接和请求。模块不把消息事实保存到浏览器持久化存储。

## 3. MessageStore

内部 `MessageStore` 提供：

- `mergePage(meetingId, page)`；
- `mergeEvent(meetingId, event, generation)`；
- `removeMeeting(meetingId)`；
- `getOrderedMessages(meetingId)`；
- `getLastSequence(meetingId)`。

合并算法先按 messageId 去重，再按 sequence 排序；模块分别维护 earliestLoadedSequence、连续的 lastAppliedSequence 和暂存的更晚事件。sequence 不连续时保留缺口状态并触发带 throughSequence 高水位的 afterSequence 补齐，而不是跳过缺口或填充假消息。

## 4. 请求与连接协调

历史请求和 SignalR 连接共享 generation。每次路由 meetingId、删除、注销或重连都会递增 generation；响应和回调必须同时匹配 meetingId 与 generation 才能进入 reducer。

连接流程固定为建立连接并缓冲事件、订阅会议、取得 presence 与 highWatermarkSequence、读取高水位内最新页或请求 afterSequence、循环补齐、合并高水位后事件、进入 caughtUp。断开后使用退避重连；退避期间保留当前历史和草稿，但把连接状态标为 reconnecting。

## 5. 发送协调

`sendMessage` 状态持有 `{ clientSubmissionId, content, candidates }`。成功返回后只合并服务端消息；确定性错误不重试，网络错误可以用同一对象重试。用户编辑内容时清除旧失败对象并创建新幂等标识。

## 6. 模块出口

公共出口只暴露 `RealtimeChatRegion`、消息/作者/提及/presence 只读类型和渲染所需 Hook。生成 Client、Hub 类型、reducer action 和错误转换保持模块内部。
