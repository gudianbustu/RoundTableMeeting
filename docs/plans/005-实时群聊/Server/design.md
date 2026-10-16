# 实时群聊与附件服务端设计

## 1. 范围与归属

依据FR-001、FR-003、FR-008、FR-011、FR-013、FR-017。本文维护统一消息、提及、分页、附件和发送结果契约；权限读取[004服务端](../../004-会议工作台/Server/design.md)，内部Agent身份由[007](../../007-AI任务连续性与接管/design.md)验证，CLI输出分页由[006](../../006-AI参会/design.md)负责。

## 2. 统一消息与事务

Human、Agent、System使用同一消息表，公开与私聊不另建平行正文表。MeetingMessages保存ID、MeetingId、InternalOrdinal、作者参与者引用/类型/名称快照、Content、CreatedAtUtc、ClientSubmissionId、ReplyToMessageId?、Visibility(public/private)、RecipientParticipantId?。System只产生公开系统消息；private必须有同会议另一位人类或Agent接收者，拒绝自身/跨会/多接收者。公开消息recipient为空。私聊双方由稳定ParticipantId确定，会议创建者与管理员均无额外权限。

InternalOrdinal为设计语义；为避免重建既有消息表，当前SQLite物理列沿用Sequence名称。该内部值仅服务端用于排序/事务，永不出现在HTTP、SignalR、CLI游标或诊断。新增技术投影ParticipantMessageViews(MeetingId,ParticipantId,Sequence,MessageId)，唯一(ParticipantId,Sequence)及(ParticipantId,MessageId)，另有每参与者序号计数器；对外sequence指该参与者自己的连续可见序号，不能跨身份比较。公开消息写入会议所有现存成员（含left）的投影，left读取仍受退出上界限制；私聊仅写双方投影及计数器。第三方的高水位、计数和游标不会因私聊推进，因此不能通过序号空洞推断私聊。

SQLite通过message_projection_insert与participant_projection_insert触发器在原写入事务内分配计数器和写入投影，唯一键及安全整数检查约束防止重复/溢出；消息范围与正文通过插入校验及不可变触发器保护。首次加入在004加入事务内按内部顺序回填已有公开消息投影，再开放参与；已有身份重入保留投影，只解除退出上界。退出期间公开投影仅在服务器保存，不能向left传输其范围或数量；双方任一left时不得新增私聊。接管复用原身份投影，不产生第三方副本。投影是可重建读模型，不是阅读状态；规模与回填耗时须在实施时实测，不在这里承诺无限规模。

消息原文保留传入正文，不以NFC或Trim改写保存内容；判空可使用Trim。提及比较只规范化候选名称，原文偏移始终指向保存文本。允许仅附件消息；正文和附件都空拒绝。CLI100000输出预算不是存储上限，服务端不能套用为正文截断规则。部署请求体保护须显式返回大小错误，不静默裁剪已接受原文。当前RealtimeChat.MaxContentLength默认1000000个UTF-16代码单元，超过配置返回400 validation_failed；该保护上限独立于CLI输出预算。

唯一键(MeetingId,InternalOrdinal)、(MeetingId,ParticipantId,ClientSubmissionId)保证顺序与提交幂等；System使用内部稳定发生键。相同提交ID与相同正文、附件ID集合、replyTo、visibility、recipient及显式mentionCandidates返回原消息；不同载荷409 message_submission_mismatch。指纹包含候选提及，重试必须保留原候选；附件ID排序后参与指纹。不是按正文去重。ReplyTo仅可引用同会议同一交流范围内有权读取的消息，不要求被提及或领取任务。回复私聊默认由原消息解析双方；若显式目标冲突返回409 reply_scope_mismatch。选择公开发送时应建立新的public发送意图并移除私密replyTo，不自动携带原私聊正文、附件或预览；已有私聊权限不变。公开正文中的@不能改变visibility，私聊中的第三人@仅保留文本，不建立第三人提及事件。

追加核心在004短事务内验证活跃收发、查幂等、校验附件和引用、分配内部顺序与各可见参与者序号、写消息/提及/附件绑定/投影/持久化追加事件。并发唯一冲突回滚后新作用域核对；不得无限重试或复用污染跟踪状态。返回成功只表示服务端已保存，不表示接收方已读或工作完成。退出后结果查询仍可读取自己的已保存提交，新增写入拒绝；deleting/deleted时全部内容查询及结果返回均拒绝，不能重发。私聊追加须事务内复查双方均joined且会议active；offline不影响留言。

## 3. 提及表示

MessageMentions保存MessageId、目标ParticipantId、startOffset、length；偏移采用UTF-16代码单元以沿用Web字符串切片，必须落在完整Unicode字符边界。DTO明确offsetUnit=utf16；CLI字符容量为Unicode标量，与提及偏移不混算。

候选从同会议参与者目录获得，名称比较复用004 Trim/NFC/ToUpperInvariant。显式候选必须与原文@显示名称和目标匹配；无候选时服务端按名称最长匹配及词边界解析。未知名称保留普通正文，不猜测替代对象；同目标重复出现保留范围，筛选按目标ID去重。私聊只解析双方范围内提及，不因@第三人向其发通知；接收者无需额外@即符合CLI等待条件。@只提供关注定位，不生成任务、强制回复或遗漏提醒。

## 4. 历史与发送HTTP契约

浏览器Cookie身份与Agent受信身份共用应用服务；以下为Web路由，Agent映射见006。

| 方法与路径 | 输入 | 输出 |
| --- | --- | --- |
| GET /api/meetings/{id}/messages | scope=all/public/private、peerParticipantId?、beforeSequence或afterSequence（二选一）、throughSequence?、limit=100（1—200） | items按序升序、first/lastSequence、hasMoreBefore/After、nextBefore/AfterSequence、visibleThroughSequence |
| POST /api/meetings/{id}/messages | clientSubmissionId、content、visibility?、recipientParticipantId?、mentionCandidates?、attachmentIds?、replyToMessageId? | 201首次/200重放：message、replayed |
| GET /api/meetings/{id}/submissions/{clientSubmissionId} | 当前发送者身份 | 200：saved及message，或not_found；无权限会议404 |
| GET /api/meetings/{id}/messages/{messageId} | 无 | 原消息及稳定定位；越界404 |

MessageDto含messageId、meetingId、sequence、author{participantId?,kind,displayName}、content、visibility、recipient{participantId,displayName}?、mentions、attachments、replyToMessageId?、createdAt。sequence来自当前受信参与者投影，public不携带私聊对象；MessageId使用不反映消息数量或时间顺序的随机Guid。JSON序号保持JavaScript安全整数范围，分配到上限时明确失败，不溢出。

visibility默认public仅兼容无replyTo的旧发送；存在replyTo且省略visibility时继承原范围和私聊对方。scope默认all表示本人有权消息合集，private+peer限定该双方，peer必须同会成员；scope过滤后分页，不返回被过滤总量或他人高水位。

无方向取最新页；before不包含边界，after不包含边界；through包含边界且不得超过服务端授权上界，实际范围取交集。无效游标400；授权空历史200空items。服务端按参与者投影与权限过滤后计算hasMore及游标，不做全量Count，也不从会议全局末条消息计算时间、数量或高水位。单条完整消息可由HTTP返回，CLI输出分片不改变服务器原文。

历史访问不要求在线，但会议必须active。left按004上界，私聊另验双方身份；deleting/deleted一律拒绝；结果查询not_found只表示该身份该提交未保存，网络错误不能伪装成not_found。客户端超时先保持pending_confirmation，核对后才重发原ID。

## 5. 附件传输、存储与访问

| 方法与路径 | 输入 | 输出 |
| --- | --- | --- |
| POST /api/meetings/{id}/attachments | multipart单文件、uploadRequestId | attachmentId、name、sizeBytes、mediaType、staged |
| GET /api/meetings/{id}/attachments | scope=all/public/private、peerParticipantId?、beforeSequence?、limit=100（1—200） | 授权已绑定附件，文件名/发送者/发送时间/messageId/sequence及继续位置 |
| GET /api/meetings/{id}/attachments/{attachmentId}/content | download或preview | 授权二进制，按Range返回完整/部分内容 |

默认100 MB明确定义为100000000字节，配置Attachment.MaxFileBytes，在服务启动时读取并校验正数；变更重启生效。Web/CLI通过能力响应取得实际bytes与展示单位，超限413 attachment_too_large并返回实际limitBytes。不能只相信Content-Length，流式累计强制上限。任意扩展名均可上传，文件名只作为显示与下载建议，存储使用随机ID键，禁止路径拼接穿越。

二进制保存到配置的数据目录（不置于公开wwwroot），元数据保存Attachment表：ID、MeetingId、UploaderParticipantId、UploadRequestId、OriginalName、SizeBytes、MediaType、StorageKey、State、MessageId(nullable)。唯一(MeetingId,UploaderParticipantId,UploadRequestId)；消息绑定时确认同会议、本人上传且可用。写临时文件、校验完成原子改名后才标staged；提交消息事务绑定为attached。失败重试可查询同上传ID，内容不一致409 upload_mismatch。

未绑定临时上传不属于会议已发送附件，设计保留24小时供重试；后台仅清理超时未绑定对象，并在同一状态条件下与绑定互斥。会议存续时已绑定附件随消息长期保存，不自动过期；删除会议时暂存和已绑定附件均进入004清理集合。上传开始/最终登记检查会议屏障，晚到文件由上传清理或恢复扫描收敛。磁盘文件/数据库失败返回明确错误，不能创建指向未完成文件的成功消息。

已绑定附件的下载、预览和列表均从所属消息执行004历史权限，还须执行所属消息public/private和双方权限，不以知道attachmentId或被@作为授权。未绑定附件仅上传者可查询/重试，不能通过内容接口读取，不能进入任何会议附件清单；绑定后不能复用同一attachmentId向另一范围发消息。私聊附件需公开分享时由用户明确另行上传与发送，不自动扩大原附件权限。禁止公开永久文件URL绕过权限。Content-Disposition安全编码；默认attachment及nosniff。安全光栅图片可内嵌，PDF采用隔离预览，HTML/SVG/可执行及无法安全预览类型一律下载。无OCR、文档解析、文件夹、版本或必读状态。CLI下载到明确本地路径，已存在文件要求显式覆盖参数。

## 6. 实时、在线与事件

SignalR沿用/hubs/meeting-chat，同源认证。SubscribeMeeting返回该参与者授权、高水位和onlineParticipantIds；活跃订阅仅active+joined。连接与参与者绑定由服务器身份确定，消息分发通过注册表选择该参与者的有效连接ID；客户端不能自行选择他人连接。公开消息通知所有有权活跃参与者；私聊只通知双方，各自携带meetingId、messageId、本人sequence及visibility，不向会议公共组先发再由前端过滤。私聊不产生公共“正在私聊”、数量、时间或对象事件；会议revision及presence不因私聊更新。

浏览器多个有效连接聚合为online，无连接为offline；Agent在线由007提供。不展示最后活跃、任务或已读。重连先缓冲、取得本人高水位，再补齐合并；按messageId去重，旧meeting/generation回调丢弃。membershipLeft终止本人活跃订阅但保留授权历史，并使其私聊对方的发送资格重新核对；该双方暂停状态只通知有关双方，不广播私聊关系。meetingDeleting阻止全部读取并移出入口，meetingDeleted清空本会议页面缓存、草稿、待发内容和附件ObjectURL；普通断线不触发删除。

持久化追加事件和正文同事务，事件只存随机消息引用与必要路由事实，Dispatcher独立DI scope、重复消费幂等。分发前再次检查会议状态、接收身份和连接代次；删除中的排队事件不能复活数据或携带私聊到公共通道。CLI同步全部有权消息类型，过滤和ACK使用本人投影序号；非本人私聊不会产生空消息事件或增加同步高水位。

下载/预览开始检查会议及消息权限，流式期间持有可取消的会议文件租约；删除屏障取消租约，拒绝后续Range请求，删除清理等待释放文件句柄后完成。已发送字节不可追回，不能把这一点写成可继续提供服务器历史访问。

## 7. 错误、安全与迁移

参数错误400 validation_failed，提交内容冲突409 submission_mismatch，生命周期错误按004，未授权资源404。服务错误不暴露堆栈、路径、正文、邀请码或令牌。消息与附件原文不进入普通日志。写入沿用Origin和实际部署协议配置；HTTP可用不表示跳过身份/成员授权。

迁移保留既有消息ID、顺序、作者与提及事实，为既有消息标记public，并按内部顺序建立成员投影；新增附件、私聊范围及可选回复关联；旧已存规范化正文无法还原成未保存原文，不能宣称恢复。停用AI任务消费者；整会删除复用004协调并覆盖私聊、投影、消息事件、回执和全部附件。存续会议迁移保留消息事实；已提交删除按删除恢复规则处理。真实SQLite约束、旧库迁移、默认文件边界与中断/清理测试已执行，具体范围见测试计划。升级使用原生新增列和新表，避免EF重建消息表破坏既有跨表触发器；退出上界从旧内部序号转换为成员投影位置。存在新指纹、私聊、回复或附件事实时拒绝降级。

私聊对象不可用、非同会议或发送资格不足返回统一 recipient_unavailable，不泄露对方的私聊活动；第三人访问私聊、引用、附件或猜测ID均404。正文、私聊关系及查询内容不得写入普通日志、指标标签或公共诊断。

序号语义变更必须提升协议能力版本：旧全局序号/ACK不得直接当作本人投影位置。服务端迁移保留消息ID并重建投影；006在握手识别旧游标版本后重建同步范围，按messageId核对已有原文后回补，不以较大的旧游标跳过新投影。当前能力接口GET /api/system/capabilities返回protocol=participant-sequence-v2。旧宿主/mcp及子路径统一返回426 protocol_upgrade_required；旧任务消费者、启动恢复服务停用，Dispatcher不领取旧任务消费者的历史待处理项，保留旧事实供整会清理。官方CLI内部通信入口归第3批，在其完成前不提供Agent产品通信链路，不向旧客户端降级广播私聊。

纯文本预览仅在存储类型为application/octet-stream且扩展名属于txt/md/log/csv/tsv/json/xml/yaml/yml/ini/conf时返回inline与text/plain，保留nosniff、sandbox、no-store及同一消息可见性检查；不按上传者声明的MIME启用HTML/SVG。前端按Range读取最多前1 MiB。

音视频预览：对于历史或新上传的octet-stream附件，仅在preview模式按扩展名白名单设置媒体Content-Type和inline：MP3 audio/mpeg、WAV audio/wav、OGG/OGA audio/ogg、M4A audio/mp4、AAC audio/aac、FLAC audio/flac、MP4/M4V video/mp4、WebM video/webm、OGV video/ogg、MOV video/quicktime。扩展名仅选择原生解码器，不表示内容校验通过；不执行文件内容。不改存储元数据与下载响应，继续nosniff、sandbox、no-store及Range；绑定前和无权限访问仍404。
