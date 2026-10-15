# 群聊客户端实现计划

1. 按[客户端设计](design.md)扩展既有listMessages/sendMessage/meetingChatConnection适配，接入真实Kiota，增加发送结果、附件上传/清单/下载和消息定位。
2. 按[模块计划](Modules/RealtimeChat/impl.md)维护按ID去重、序号补齐、generation、权限上界及待确认发送，不把超时直接标失败。
3. 更新[区域页面](Pages/RealtimeChat/impl.md)、[时间线](Components/MessageTimeline/impl.md)与[编辑器](Components/MessageComposer/impl.md)，接入阅读锚点、回到最新、附件和纯文本提及。
4. 退出保留有权历史及附件，暂停双方私聊；删除移出入口并清理公开/私聊缓存与草稿；普通网络恢复不join。
5. 执行根C02/C03/C07/C08/C10组件和真实浏览器验收，重点检查新消息、旧页插入、图片尺寸变化及切会；本轮仅计划。

6. 按Client第7节接入私聊选择、范围隔离的草稿/滚动、默认回复与显式公开发送，执行C11—C14；服务端及网络事件不得向第三方提供私聊发生信息。
