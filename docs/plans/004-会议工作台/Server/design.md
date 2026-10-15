# 会议工作台服务端设计

## 1. 范围与契约归属

依据基础需求及FR-005、FR-007、FR-012、FR-015，本文是会议生命周期、成员权限、元数据与HTTP契约的唯一维护位置。[模块设计](Modules/MeetingWorkspace/design.md)展开应用编排。[005](../../005-实时群聊/Server/design.md)消费授权结果，不能自行用Active状态替代历史权限。

## 2. 分层与模型

沿用Domain、Abstraction、Application、Repository.Sqlite与Web宿主。Domain负责输入约束和状态不变量；Application解析当前身份、组织短事务；Repository映射及条件写；Controller只适配HTTP。接口不返回EF实体。

Meeting拥有ID、Name、Description、CreatorUserId、MeetingCode、LifecycleState(active/closed)、ClosedThroughSequence(nullable)和Revision。会议创建即开放；closed是保留数据的终态，不恢复交流。MeetingParticipant拥有ID、MeetingId、UserId(nullable)、ParticipantType(human/agent)、DisplayName、NormalizedDisplayName、MembershipState(joined/left)、LeftThroughSequence(nullable)。账号和参与者身份分离，同名不是任意合并人类身份的依据。

会议与成员共同受会议状态约束；消息是005独立追加对象，通过短事务协调，不加载无限消息实体集合。共享业务模型见[领域图](../../../uml/圆桌会议协作模型.puml)。

## 3. 规范化与持久化

保留既有规则：Name Trim+NFC，1—128；Description Trim+NFC，空白为null，最长2000；DisplayName Trim+NFC，1—64；比较值ToUpperInvariant，最长128。名称规范化由Domain/Application执行，SQLite HasMaxLength不能替代校验。会议名称不要求平台唯一，参与者名称首版加入后不改。

MeetingCode使用随机Guid，以标准D格式展示，输入Trim后严格D格式解析；只用于加入/接管，不代替成员读取授权。Meetings.MeetingCode唯一；MeetingParticipants(MeetingId,UserId)在人类非空时唯一，(MeetingId,NormalizedDisplayName)对两种类型统一唯一。退出仍保留身份及名称，不释放给另一个主体。外键Restrict，禁止通过级联删除历史。

既有Meetings、MeetingParticipants增加上述状态、边界及revision字段；创建回执继续按(CreatorUserId,CreationRequestId)唯一，保存原请求及会议引用。关闭后相同创建请求返回原会议关闭结果，不重新创建，不删除回执使旧请求复活。技术时间字段沿用UTC，不展示为参与者最后活跃。

## 4. 授权与列表

授权服务输出canReadHistory、canSend、canSubscribe、canInvite、canEdit、canClose、canLeave、visibleThroughSequence和membershipRevision。

- joined且active：完整当前历史、发送/订阅；已加入人类成员可取邀请；只有创建者可编辑和关闭，只有非创建者可退出。
- left：历史上界为LeftThroughSequence，无活跃收发/邀请/编辑；开放会议允许明确再加入。
- closed且joined：历史上界为ClosedThroughSequence；无新加入、活跃收发或编辑。
- closed且left：历史上界为min(LeftThroughSequence,ClosedThroughSequence)，关闭不扩大退出者权限。
- 非成员：404 meeting_not_found；平台管理员无额外会议权限。

活跃列表为active+joined，历史列表为closed或left。历史入口不改变成员状态。列表保持joinedAt降序、meetingId稳定次序；详情不返回会议码。参与者标识、名称和human/agent类型供展示，不提供邀请人/所属人/最后活跃/工作状态/已读字段。连接状态由005/007提供，不能靠成员状态推测在线。

## 5. HTTP目标契约

所有浏览器入口沿用003 Cookie认证，JSON写请求使用RequireBrowserOrigin(true)，无正文请求按既有元数据处理；响应no-store，错误ProblemDetails。以下为设计目标，实施后从真实OpenAPI生成Kiota。

| 方法与路径 | 请求 | 成功响应 |
| --- | --- | --- |
| GET /api/meetings?view=active或history | 默认active | items：id、name、description摘要、membershipState、lifecycleState、joinedAt、permissions、revision |
| POST /api/meetings | creationRequestId、name、description? | 201首次或200重放；详情和邀请信息 |
| GET /api/meetings/{id} | 无 | 基本信息、当前成员、权限、可见历史上界、参与者摘要、revision |
| PATCH /api/meetings/{id} | name、description?、expectedRevision | 200更新详情；仅创建者且会议开放 |
| POST /api/meetings/{id}/invitation | 无正文 | 200会议码和同版本CLI邀请说明；已加入人类成员 |
| POST /api/meetings/join/preview | meetingCode | 会议名、背景、默认/既有名称、成员状态 |
| POST /api/meetings/join | meetingCode、displayName | 200参与身份；已退出者明确再加入 |
| POST /api/meetings/{id}/close | confirmationName、operationId | 200关闭详情及最终历史上界；创建者重复关闭返回原状态 |
| POST /api/meetings/{id}/leave | operationId | 200成员状态及退出上界；重复退出不移动上界 |

统一DTO枚举为camelCase字符串，不输出整数。confirmationName按Trim+NFC后Ordinal比较；修改使用revision防止旧表单覆盖，冲突409 meeting_revision_conflict并要求重载。close确认名不一致400 validation_failed；成员非创建者编辑/关闭403 meeting_creator_required；创建者leave为409 creator_cannot_leave；closed上写入409 meeting_closed；left上收发409 membership_left。无权或无效会议码统一404，不泄露资源。

原/deletion写端点在目标版停用，返回410 operation_retired，提示使用关闭；不能让旧客户端继续物理清理。此为版本切换设计，须实施时验证旧客户端错误展示。旧已物理删除数据无法恢复。

## 6. 事务与并发

保留IMeetingTransactionRunner、单scope DbContext/连接、BEGIN IMMEDIATE等价短事务；委托只登记变更，Runner执行一次工作单元提交与数据库提交，失败废弃作用域，禁止嵌套。所有发送、附件绑定、加入、退出、关闭、元数据修改在事务内复查状态和权限。网络通知在提交后进行，不持锁等外部连接。

关闭事务读取当前最大消息序号（空会议0），保存closed及ClosedThroughSequence、增加revision并写状态事件，保留全部内容。退出事务读取同一高水位，保存left及LeftThroughSequence；创建者请求在写入前拒绝。发送先提交则包含在边界内，关闭/退出先提交则发送被拒绝。重复close/leave返回原边界不重新扩大。

重新join开放会议时恢复原ParticipantId及显示名，将成员状态改joined、清除退出上界；完整历史开放。普通详情、历史读取、reconnect和takeover不得执行这一状态变更。并发加入靠唯一索引和一次新作用域重查收敛，不能继续使用失败DbContext。

状态事件通知Web和CLI停止活跃订阅；通知丢失不影响服务端逐次授权。关闭不删除数据库行、消息事件、附件或创建回执，不启用物理删除恢复器。

## 7. 安全及迁移

邀请码仅在创建和授权邀请响应/私有CLI输入中出现，不放URL、日志、指标。沿用认证后的加入限流：每账号5分钟30次、每来源地址5分钟100次，部署可配置；超额429及Retry-After，单实例重启清零。可信代理配置才接受转发来源。允许HTTP/HTTPS部署，按003实际协议配置Cookie与Origin。

迁移先备份与核验旧库：Active映射active，现存成员初始joined；旧Deleting不得继续清理，检查存留数据后按已提交关闭意图映射closed并固定现有历史上界。已经缺失的数据列为不可恢复缺口，不能补造。旧创建回执保留已消费事实，禁止把缺失会议引用重放成新会议。迁移只在后续授权实施阶段执行。
