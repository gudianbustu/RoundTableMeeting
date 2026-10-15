# 实时群聊与附件服务端设计

## 1. 范围与归属

依据FR-001、FR-003、FR-008、FR-011、FR-013。本文维护统一消息、提及、分页、附件和发送结果契约；权限读取[004服务端](../../004-会议工作台/Server/design.md)，内部Agent身份由[007](../../007-AI任务连续性与接管/design.md)验证，CLI输出分页由[006](../../006-AI参会/design.md)负责。

## 2. 统一消息与事务

Human、Agent、System使用同一消息表和序号。MeetingMessages保存ID、MeetingId、Sequence、作者参与者引用/类型/名称快照、Content、CreatedAtUtc、ClientSubmissionId和nullable ReplyToMessageId。System作者参与者为空，系统消息同样进入历史与CLI同步；系统消息不解析人工提及。

消息原文保留传入正文，不以NFC或Trim改写保存内容；判空可使用Trim。提及比较只规范化候选名称，原文偏移始终指向保存文本。允许仅附件消息；正文和附件都空拒绝。CLI100000输出预算不是存储上限，服务端不能套用为正文截断规则。部署请求体保护须显式返回大小错误，不静默裁剪已接受原文。

唯一键(MeetingId,Sequence)、(MeetingId,ParticipantId,ClientSubmissionId)保证顺序与提交幂等；System使用内部稳定发生键。相同提交ID与相同正文、附件列表、replyTo返回原消息；不同载荷409 submission_mismatch。不是按正文去重。ReplyTo仅可引用同会议有权读取的消息，不要求被提及或领取任务。

追加核心在004短事务内验证活跃收发、查幂等、校验附件和引用、分配序号、写消息/提及/附件绑定/持久化追加事件。并发唯一冲突回滚后新作用域核对；不得无限重试或复用污染跟踪状态。返回成功只表示服务端已保存，不表示接收方已读或工作完成。关闭/退出后结果查询仍可读取已保存提交，新增写入仍拒绝。

## 3. 提及表示

MessageMentions保存MessageId、目标ParticipantId、startOffset、length；偏移采用UTF-16代码单元以沿用Web字符串切片，必须落在完整Unicode字符边界。DTO明确offsetUnit=utf16；CLI字符容量为Unicode标量，与提及偏移不混算。

候选从同会议参与者目录获得，名称比较复用004 Trim/NFC/ToUpperInvariant。显式候选必须与原文@显示名称和目标匹配；无候选时服务端按名称最长匹配及词边界解析。未知名称保留普通正文，不猜测替代对象；同目标重复出现保留范围，筛选按目标ID去重。@只提供关注定位，不生成任务、强制回复或遗漏提醒。

## 4. 历史与发送HTTP契约

浏览器Cookie身份与Agent受信身份共用应用服务；以下为Web路由，Agent映射见006。

| 方法与路径 | 输入 | 输出 |
| --- | --- | --- |
| GET /api/meetings/{id}/messages | beforeSequence或afterSequence（二选一）、throughSequence?、limit=100（1—200） | items按序升序、first/lastSequence、hasMoreBefore/After、nextBefore/AfterSequence、visibleThroughSequence |
| POST /api/meetings/{id}/messages | clientSubmissionId、content、mentionCandidates?、attachmentIds?、replyToMessageId? | 201首次/200重放：message、replayed |
| GET /api/meetings/{id}/submissions/{clientSubmissionId} | 当前发送者身份 | 200：saved及message，或not_found；无权限会议404 |
| GET /api/meetings/{id}/messages/{messageId} | 无 | 原消息及稳定定位；越界404 |

MessageDto含messageId、meetingId、sequence、author{participantId?,kind,displayName}、content、mentions、attachments、replyToMessageId?、createdAt。JSON序号保持JavaScript安全整数范围，分配到上限时明确失败，不溢出。

无方向取最新页；before不包含边界，after不包含边界；through包含边界且不得超过服务端授权上界，实际范围取交集。无效游标400；授权空历史200空items。服务端按索引边界查询，不做全量Count。单条完整消息可由HTTP返回，CLI输出分片不改变服务器原文。

历史访问不要求在线或会议开放。left和closed按004上界；结果查询not_found只表示该身份该提交未保存，网络错误不能伪装成not_found。客户端超时先保持pending_confirmation，核对后才重发原ID。

## 5. 附件传输、存储与访问

| 方法与路径 | 输入 | 输出 |
| --- | --- | --- |
| POST /api/meetings/{id}/attachments | multipart单文件、uploadRequestId | attachmentId、name、sizeBytes、mediaType、staged |
| GET /api/meetings/{id}/attachments | beforeSequence?、limit=100（1—200） | 授权已绑定附件，文件名/发送者/发送时间/messageId/sequence及继续位置 |
| GET /api/meetings/{id}/attachments/{attachmentId}/content | download或preview | 授权二进制，按Range返回完整/部分内容 |

默认100 MB明确定义为100000000字节，配置Attachment.MaxFileBytes，在服务启动时读取并校验正数；变更重启生效。Web/CLI通过能力响应取得实际bytes与展示单位，超限413 attachment_too_large并返回实际limitBytes。不能只相信Content-Length，流式累计强制上限。任意扩展名均可上传，文件名只作为显示与下载建议，存储使用随机ID键，禁止路径拼接穿越。

二进制保存到配置的数据目录（不置于公开wwwroot），元数据保存Attachment表：ID、MeetingId、UploaderParticipantId、UploadRequestId、OriginalName、SizeBytes、MediaType、StorageKey、State、MessageId(nullable)。唯一(MeetingId,UploaderParticipantId,UploadRequestId)；消息绑定时确认同会议、本人上传且可用。写临时文件、校验完成原子改名后才标staged；提交消息事务绑定为attached。失败重试可查询同上传ID，内容不一致409 upload_mismatch。

未绑定临时上传不属于会议已发送附件，设计保留24小时供重试；后台仅清理超时未绑定对象，并在同一状态条件下与绑定互斥。已绑定附件随消息长期保存，不自动过期。磁盘文件/数据库失败返回明确错误，不能创建指向未完成文件的成功消息。

下载、预览和列表均从所属消息执行004历史权限，不以知道attachmentId或被@作为授权。禁止公开永久文件URL绕过权限。Content-Disposition安全编码；默认attachment及nosniff。安全光栅图片可内嵌，PDF采用隔离预览，HTML/SVG/可执行及无法安全预览类型一律下载。无OCR、文档解析、文件夹、版本或必读状态。CLI下载到明确本地路径，已存在文件要求显式覆盖参数。

## 6. 实时、在线与事件

SignalR沿用/api/meetings/chat，同源认证。SubscribeMeeting返回授权、高水位和onlineParticipantIds；活跃订阅只允许active+joined。消息事件携带meetingId、messageId、sequence，客户端按ID去重，丢失后通过HTTP补齐。meetingChanged、meetingClosed、membershipLeft携带revision；关闭/退出终止对应活跃订阅，客户端转历史模式而非删除历史。

浏览器多个有效连接聚合为online，无连接为offline；Agent在线由007心跳/探测提供。不展示最后活跃时间、思考、任务状态或已读。Web连接重建先缓冲事件、订阅获高水位、补齐历史再合并；旧会议和旧generation回调丢弃。

持久化消息事件和消费者投递与消息同事务；Dispatcher每次独立DI scope，重试可重复，消费者幂等。消息事件用于通知，历史原文是事实。CLI后台对全部类型消息按序同步，不能只投递@。关闭后停止活跃通知但仍保留历史；不生成AI回复任务、协作链或任务派生器。

## 7. 错误、安全与迁移

参数错误400 validation_failed，提交内容冲突409 submission_mismatch，生命周期错误按004，未授权资源404。服务错误不暴露堆栈、路径、正文、邀请码或令牌。消息与附件原文不进入普通日志。写入沿用Origin和实际部署协议配置；HTTP可用不表示跳过身份/成员授权。

迁移保留既有消息ID、顺序、作者与提及事实，新增附件和可选回复关联；旧已存规范化正文无法还原成未保存原文，不能宣称恢复。停用物理删除协调和AI任务消费者，既有消息事件保留处理事实。真实SQLite约束、文件故障和旧库副本迁移在后续实施验证；不以文档说明替代执行。
