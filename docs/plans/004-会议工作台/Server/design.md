# 会议工作台服务端设计

## 1. 范围与契约归属

依据基础需求及 FR-005、FR-007、FR-012、FR-015、FR-017，本文维护会议生命周期、参与权限、元数据和删除契约。[模块设计](Modules/MeetingWorkspace/design.md)负责应用编排，[005](../../005-实时群聊/Server/design.md)负责消息可见性与附件。全部内容为目标设计，未执行实现或迁移。

## 2. 分层与模型

沿用 Domain、Abstraction、Application、Repository.Sqlite 与 Web；Domain 校验不变量，Application 编排，Repository 条件写，Controller 适配 HTTP，不返回 EF 实体。共享模型见[领域图](../../../uml/圆桌会议协作模型.puml)。

Meeting 保存 Id、Name、Description、CreatorUserId、MeetingCode、LifecycleState(active/deleting)、Revision。active 可交流；deleting 是立即禁止任何内容访问的技术清理阶段，不是只读归档。清理完成删除业务行，以最小删除回执表示 deleted，不保留 ClosedThroughSequence 或可浏览历史会议。

MeetingParticipant 保存 Id、MeetingId、UserId?、ParticipantType(human/agent)、DisplayName、NormalizedDisplayName、MembershipState(joined/left)、LeftThroughSequence?。退出上界是005为该参与者定义的可见消息序号，不能用其他人的序号比较。账号与参会身份分离；同名不能合并不同人类。私聊双方由稳定 ParticipantId 确定，Agent 接管继承同一身份有权私聊和附件，不改变参与状态。

## 3. 规范化与存储

Name Trim+NFC，1—128；Description Trim+NFC，空白为null，最长2000；DisplayName Trim+NFC，1—64，比较值 ToUpperInvariant，最长128。SQLite 长度声明不替代输入校验。会议名称不要求唯一，参会显示名首版加入后不改。

MeetingCode 为随机 Guid 的 D 格式，Trim 后严格解析，仅用于加入/接管，不代替读取授权。Meetings.MeetingCode 唯一；成员 (MeetingId,UserId) 在人类非空时唯一，(MeetingId,NormalizedDisplayName) 对两种类型统一唯一。退出不释放身份与名称。保留 Restrict 外键，由删除协调显式按依赖顺序清理，不能以关系解除意外级联。

创建回执 (CreatorUserId,CreationRequestId) 唯一；会议删除后仅保留请求指纹、不可复用事实和目标ID，不保存名称、背景或完整请求正文；旧创建意图不能重放生成新会议。删除协调记录只保存 operationId、目标ID、发起者验证信息、阶段及剩余随机存储键，完成后清除文件键与成员映射，不保留消息或私聊元数据。

## 4. 授权与列表

授权结果包含 canReadHistory、canSend、canSubscribe、canInvite、canEdit、canDelete、canLeave、visibleThroughSequence 和 membershipRevision；其中 canSend 只表示会议基础资格，私聊双方与目标检查由005追加核心执行。

- active+joined：可读完整有权公开历史及本身份私聊，可活跃收发；人类可邀请；仅创建者可编辑/删除，仅非创建者可退出。
- active+left：只读退出前有权公开历史与已有本人私聊/附件，不能发送、订阅、邀请或编辑；任何一方退出均暂停该双方私聊发送。
- deleting/deleted：所有人均无消息、附件、检索、加入、接管或收发资格，已退出者与创建者无例外。
- 非成员或无效会议码：404 meeting_not_found，不泄露资源；管理员不额外取得会议或私聊权限。

活跃列表为 active+joined；历史列表仅含仍存在的 active+left。删除会议从两类列表移除。列表按 joinedAt 降序及稳定ID排序；详情不带会议码，不展示私聊关系、数量、时间或活动。普通详情、浏览历史、reconnect/takeover 不改变成员状态，只有明确 join 可重入。

## 5. HTTP目标契约

浏览器沿用003 Cookie认证、写请求 Origin 校验及 ProblemDetails；响应 no-store。以下是设计目标，真实 OpenAPI 待实现生成。

| 方法与路径 | 请求 | 响应 |
| --- | --- | --- |
| GET /api/meetings?view=active或history | 默认active | items：id、name、description摘要、membershipState、lifecycleState、joinedAt、permissions、revision |
| POST /api/meetings | creationRequestId、name、description? | 201首次/200重放；详情和邀请；已删除创建意图410 |
| GET /api/meetings/{id} | 无 | 基本信息、成员权限、本人可见高水位、参与者摘要、revision |
| PATCH /api/meetings/{id} | name、description?、expectedRevision | 200；仅创建者且active |
| POST /api/meetings/{id}/invitation | 无正文 | 会议码与同版本CLI邀请；仅已加入人类 |
| POST /api/meetings/join/preview | meetingCode | 名称背景、默认或既有显示名、参与状态 |
| POST /api/meetings/join | meetingCode、displayName | 参与身份；left明确恢复joined |
| POST /api/meetings/{id}/deletion | confirmationName、operationId? | 204清理完成；202表示已封锁且仍清理，返回operationId、status=deleting及状态地址 |
| GET /api/meetings/{id}/deletion/{operationId} | 创建者账号 | 仅返回deleting/deleted、可重试清理错误；不返回已删除内容 |
| POST /api/meetings/{id}/leave | operationId | 200状态及本人退出上界；重复不移动边界 |
| POST /api/agent/v1/lifecycle-status | meetingId、lifecycleProof | 经删除状态专用凭据验证后返回active/deleting/deleted；不返回内容或新身份 |

沿用现有 /deletion 和再次输入会议名称的确认方式，提示删除公开消息、私聊及全部附件，所有人不能再查看；不增加确认弹窗或新的确认流程。confirmationName 按 Trim+NFC 后 Ordinal 比较；输入不符400 validation_failed。operationId 可由新版客户端提供，旧客户端缺省时服务端以已有会议删除操作收敛，不能把重复调用当作新删除。此前设计中的 /close 不作为目标接口；若实施时发现已部署兼容端点，必须指向同一删除用例，禁止保留归档语义。

非创建者编辑/删除403 meeting_creator_required；创建者leave为409 creator_cannot_leave；revision冲突409 meeting_revision_conflict；left写入409 membership_left。已验证原成员访问正在删除会议返回410 meeting_deleting，确认已删返回410 meeting_deleted；无权请求统一404。普通404、401、网络失败或连接接管不得推断为已删除并清理本地副本。

## 6. 删除事务、文件清理与恢复

所有消息追加、附件绑定、加入、退出、元数据修改与删除屏障共用 IMeetingTransactionRunner 的单scope短事务（BEGIN IMMEDIATE等价写锁），一次工作单元提交，失败废弃作用域，不嵌套、不持锁等待网络或大文件IO。

1. 验证创建者和名称；事务内将active改deleting，记录可恢复删除操作、待清理文件集合及仅含会议ID的控制通知。提交前失败则保持原状态；提交后不可回到active。屏障提交即禁止所有内容访问和新业务写入。
2. 提交后取消本会议订阅、上传与下载租约，向原连接发 meetingDeleting；已发出的网络字节无法收回。005上传在最终登记前复查状态，屏障后产生的临时文件由该上传清理或清理器扫描本会议受管目录收敛。
3. 分批、幂等地删除该会议的暂存和已绑定附件二进制，再在短事务内清除公开/私聊消息、提及、附件元数据、可见投影、正文事件、发送回执、连接与成员、会议业务行。旧业务任务残留也按实际依赖清除。只依据服务器生成的受管存储键清理，禁止用户文件名决定路径；不影响其他会议。
4. 文件或数据库失败保持deleting、记录阶段并重试，状态不可读，不能提前返回删除完成。后台恢复服务延续同一操作，不重复生成业务消息。文件、业务数据及带正文的派生记录均清理完才标deleted，发 meetingDeleted 并返回完成；失败诊断不含正文、私聊关系或路径。

删除与发送的先后由事务确定：发送先提交仍被随后整会清理；删除先提交则发送拒绝。删除与退出并发时删除优先终止全部历史资格。结果核对在删除后不能返回已清除消息，也不再重发。普通退出只固定该参与者的序号上界，不清理任何业务内容；重新加入active会议恢复原身份与有权公开历史，两方均joined才恢复私聊。

## 7. 离线客户端删除确认

Agent首次成功绑定会议时生成并私有保存随机 lifecycleProof，服务端只保留不可逆验证值；它独立于活跃连接与历史读取凭据，仅用于查询删除状态。旧会话被接管、退出或断开后仍可用其已有proof确认本会议是否删除，不能用proof读取内容、续接或获取新凭据。服务端删除完成后保留最小墓碑（meetingId、proof验证值、deleted状态及必要幂等标识），不保留会议名称、消息、附件或私聊对象/时间/数量；它不是历史入口。

删除通知必须来自已验证服务连接，通知丢失则后台在联网恢复时先查生命周期；会话已被接管也可显式运行此只读检查，不恢复参与。状态为deleted才触发006受管副本清理；deleting先禁止本地继续查询并等待确认。网络失败、未知meetingId或proof验证失败显示无法确认，不猜测删除。墓碑/proof索引不按普通会话过期自动删除，避免长期离线客户端无法确认；备份恢复及升级需保持删除事实，不能将旧备份直接恢复为对外可访问会议。

## 8. 安全与迁移

邀请码不进入URL、日志或指标。沿用认证加入限流：每账号5分钟30次、每来源5分钟100次，可部署配置；429含Retry-After，可信代理才接受转发来源。HTTP/HTTPS按003实际协议配置。

后续实施先用隔离旧库核对现有 Active/Deleting、删除恢复服务、外键及创建回执。已有Deleting按真实已提交删除意图接续清理，不转为保留历史的closed；若实际存在旧设计产生的closed数据，需核实其来源与用户意图，不能仅凭字段名批量删除。未删除的既有消息按005迁移为公开消息；已物理删除资料不补造。此处只定义迁移检查，不执行运行库操作。
