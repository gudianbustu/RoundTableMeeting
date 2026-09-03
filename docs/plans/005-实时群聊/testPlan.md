# 实时群聊主题测试方案

## 1. 文档定位

本文定义 005 实时群聊主题的 Domain、Application、Repository.Sqlite、Architecture、HTTP、SignalR、事件分发、OpenAPI/Kiota、客户端、浏览器和发布回归测试。实施顺序见[主题实现方案](impl.md)，服务端见[Server/impl.md](Server/impl.md)，服务端模块见[Server/Modules/RealtimeChat/impl.md](Server/Modules/RealtimeChat/impl.md)，客户端见[Client/impl.md](Client/impl.md)。

本方案验证统一消息核心和当前浏览器人类参与者闭环，同时验证未来 Agent 消息能够复用相同的消息、序号、提及、事件和广播契约。本主题不验证 MCP 入会、AI 回复任务、上下文裁剪、接管、循环保护、未读状态或消息编辑删除；这些属于后续主题，但必须通过架构/范围测试确认没有被提前实现为第二套消息路径。

## 2. 测试目标

必须证明：

1. Human、Agent 预留和 System 作者最终都能映射到同一 `MeetingMessage`/`MessageDto`/Sequence 模型。
2. 消息、提及、追加事件和实时广播 delivery 在同一事务中可靠登记；事务失败不会留下半条事实。
3. 同一会议消息序号连续、唯一、按服务端提交顺序分配；并发发送不会覆盖或丢失消息。
4. 同一会议、同一作者和同一 `ClientSubmissionId` 的重试幂等，不重复创建消息、提及、序号或追加事件。
5. `@参与者` 解析遵守 NFC、不区分大小写、最长匹配、边界和重复去重规则，目标保存为稳定 ParticipantId。
6. 非成员、未认证、Deleting 会议和跨会议请求均经过统一会议访问屏障，不泄露会议存在性。
7. 历史接口的 before/after/through 游标不漏消息、不重复消息，不依赖 offset，也不在活跃会议补齐时漂移。
8. SignalR 订阅、presence、事件重复、事件丢失、断线重连和高水位补齐行为正确。
9. 持久化事件 Dispatcher 在广播失败、租约过期、服务重启和删除并发下可重试且不产生删除后的外部广播。
10. 客户端多会议隔离、消息去重、序号缺口处理、发送失败重试、删除回退和移动端布局正确。
11. 会议删除覆盖消息、mentions、events、deliveries、presence 和连接终止；删除失败或服务重启不会重新开放会议。
12. 目录 002/003/004 已有框架、认证、Origin、ProblemDetails、Cookie、SPA 和会议工作台回归继续通过。

## 3. 优先级和完成规则

- P0：消息事实丢失/重复、跨会议泄露、未授权读取/发送、删除屏障失效、删除后迟到广播、幂等失效、序号错乱、XSS、构建阻断。
- P1：正常业务流程、分页/补齐、断线恢复、presence、多连接、事件重试、OpenAPI/Kiota 和客户端状态。
- P2：视觉、诊断、性能基线和维护性检查。

所有 P0/P1 失败阻止主题完成。单元测试通过但真实 SQLite、真实 Cookie/SignalR 或浏览器流程失败，仍视为主题失败。

## 4. 环境与隔离

- 每个服务端测试使用独立临时 SQLite 数据库；不读取或修改 `data/round-table-meeting.db`。
- IntegrationTests 使用 WebApplicationFactory、真实 Cookie、两个或三个种子账号、独立 CookieContainer、显式 `AllowedOrigins` 和可替换 `ManualTimeProvider`。
- 并发 HTTP 请求各自创建 DI scope 和 DbContext；测试不得共享 Scoped DbContext、UnitOfWork 或事务 Runner。
- 事件 Dispatcher、presence registry 和 gate 测试使用可控时钟、可注入消费者和可注入 Signal/Channel，避免以固定 sleep 作为唯一同步手段。
- SignalR 测试优先使用 TestServer 可连接的真实 Hub；若底层 transport 在单元环境不可用，必须保留至少一组真实进程 WebSocket/长轮询验收。
- 客户端测试使用 Vitest/jsdom/Testing Library/user-event；不整体 mock Ant Design，不 mock 所有 Kiota runtime，只 mock feature 边界的 API/连接适配。
- 浏览器使用独立 Chrome Profile 和仅绑定 `127.0.0.1` 的 CDP；每个测试结束关闭页面和连接。
- 不把会议码、密码、Cookie、消息正文全集、参与者名称全集或完整请求体写入日志、快照、失败输出或测试报告。

## 5. 测试数据约定

- 固定会议 A、会议 B、账号 Alice、Bob、Carol；每个测试重新生成 Guid，除非需要验证同一幂等键。
- 人类参与者显示名使用 `Alice`、`Bob`；Agent 预留使用 `分析师`、`Reviewer`；系统作者名称由服务端固定。
- 使用组合字符和预组字符验证 NFC，例如 `e\u0301` 与 `é`；使用大小写、首尾空白、内部空白和标点验证名称边界。
- 预生成正常消息序列 1、2、3；另在客户端 reducer 测试中直接注入已收到序列 10 的事件，模拟实时投递缺口。数据库正常追加仍保证严格递增，不把测试夹具解释为服务端允许断号。
- 所有内容长度边界同时验证 UTF-16 code units 与浏览器 JS `.length` 语义。

## 6. Domain 测试

项目：`Server/Tests/RoundTableMeeting.Domain.Tests`。

| 编号 | 优先级 | 场景 | 预期 |
| --- | --- | --- | --- |
| RC-D001 | P0 | 创建合法 Human 消息 | MessageId/MeetingId/AuthorParticipantId 非空，AuthorKind=Human，Sequence 正数 |
| RC-D002 | P0 | 创建合法 Agent 消息 | 与 Human 使用同一实体工厂/约束，AuthorKind=Agent，目标 ParticipantId 稳定 |
| RC-D003 | P0 | 创建合法 System 消息 | AuthorKind=System，AuthorParticipantId=null，固定系统作者快照 |
| RC-D004 | P0 | 空 MeetingId/MessageId/Sequence | 工厂拒绝，不产生可持久化实体；Sequence 超过 Number.MAX_SAFE_INTEGER 同样拒绝 |
| RC-D005 | P0 | Participant 作者与 null ParticipantId | Human/Agent 作者拒绝 null ParticipantId |
| RC-D006 | P0 | System 作者与非 null ParticipantId | 拒绝系统作者伪装为会议参与者 |
| RC-D007 | P0 | 内容 Trim + NFC | 首尾空白去除，组合字符归一化，内部空格/换行保留 |
| RC-D008 | P0 | 空内容 | null、空串和全空白拒绝 |
| RC-D009 | P0 | 内容长度边界 | 1 和 MaxContentLength 通过，超过上限拒绝 |
| RC-D010 | P1 | UTF-16 长度语义 | 代理对、组合字符长度与约定一致，不以字节数误判 |
| RC-D011 | P0 | ClientSubmissionId | 所有消息持久化非空 Guid；浏览器/Agent 使用外部 ID，System 使用 code+occurrence key 派生确定性键；Guid.Empty 被拒绝 |
| RC-D012 | P0 | Mention 基本范围 | StartOffset>=0、Length>1、范围落在正文内 |
| RC-D013 | P0 | Mention 越界/负值/重叠 | 拒绝非法对象，不产生越界高亮数据 |
| RC-D014 | P0 | Mention 目标 | TargetParticipantId 非空，TargetDisplayName 为快照值 |
| RC-D015 | P0 | MessageMention 出现键 | 同一 MessageId + StartOffset 只能一条；同一 TargetParticipantId 可有多个不重叠范围 |
| RC-D016 | P0 | MessageAppendedEvent | EventId/MessageId/MeetingId/Sequence 非空，事件不可修改关键引用 |
| RC-D017 | P0 | Delivery 状态机 | Pending -> Leased -> Completed 合法；失败达到阈值转 Suspended；Completed 不回退；租约过期可回 Pending |
| RC-D018 | P1 | Delivery 尝试次数/时间 | AttemptCount 非负，Available/Lease/Completed 时间关系正确 |
| RC-D019 | P0 | AuthorKind 字符串值 | Domain 只存 Human/Agent/System，Abstraction JSON 只输出 human/agent/system，映射不接受未知值 |
| RC-D020 | P1 | System 幂等键 | 相同 code+occurrence key 生成稳定 ID；不同参与者/业务发生键生成不同 ID，不错误合并同类事实 |

## 7. MentionParser Application 测试

项目：`Server/Tests/RoundTableMeeting.Application.Tests/MessageMentionParserTests.cs`。

| 编号 | 优先级 | 场景 | 预期 |
| --- | --- | --- | --- |
| RC-M001 | P0 | 精确 `@Alice` | 解析 Alice ParticipantId，范围含 `@` 和名称 |
| RC-M002 | P0 | 大小写等价 | `@alice`、`@ALICE` 命中同一目标 |
| RC-M003 | P0 | NFC 等价 | 组合字符和预组字符命中同一名称 |
| RC-M004 | P0 | 名称后空白/标点/末尾 | 合法边界产生提及 |
| RC-M005 | P0 | 名称后普通字母 | 不产生提及，正文原样保留 |
| RC-M006 | P0 | 最长匹配 | `@AI` 与 `@AI Reviewer` 同时存在时选择最长合法名称 |
| RC-M007 | P0 | 同消息重复目标 | 同一 TargetParticipantId 的每个合法不重叠出现都返回，后续任务目标 Distinct 后只有一个 |
| RC-M008 | P0 | 多目标 | 一条消息对 Alice/Bob/Agent 分别返回记录 |
| RC-M009 | P0 | 未知名称 | 未知 `@` 作为普通正文，不使发送失败 |
| RC-M010 | P0 | System 消息 | 解析器跳过 mentions |
| RC-M011 | P0 | Human/Agent 相同规则 | 作者类型不同不改变名称匹配和范围算法 |
| RC-M012 | P0 | 候选 ParticipantId 属于其他会议 | 忽略候选并回退服务端解析，不跨会议引用 |
| RC-M013 | P0 | 候选范围与正文不一致 | 忽略候选，不信任 ID，普通正文仍可发送 |
| RC-M014 | P0 | 候选范围重叠 | 重叠候选不写入重复 mention |
| RC-M015 | P1 | NFC+Trim 偏移 | 规范化后范围与 UTF-16 代码单元索引一致 |
| RC-M016 | P1 | 边界 Unicode 标点 | 中文标点、括号、换行后的提及边界稳定 |
| RC-M017 | P1 | 非 ASCII 大小写 | 正文片段按 NFC+ToUpperInvariant 后与 NormalizedDisplayName Ordinal 比较，ß/SS、ı/I 等边界与目录 004 规则一致 |

## 8. Application 服务测试

项目：`Server/Tests/RoundTableMeeting.IntegrationTests/RealtimeChatContractTests.cs`；Application 纯规则测试位于 `Server/Tests/RoundTableMeeting.Application.Tests/MessageMentionParserTests.cs`，Repository 状态测试位于 `Server/Tests/RoundTableMeeting.Repository.Sqlite.Tests/RealtimeChatDeliveryPersistenceTests.cs`。使用 fake current user、会议/参与者/消息/事件仓储、事务 Runner、TimeProvider、Options、gate 和 dispatcher signal。

| 编号 | 优先级 | 场景 | 预期 |
| --- | --- | --- | --- |
| RC-A001 | P0 | 未认证历史 | 返回 Unauthenticated，不查询消息、不消耗限流 |
| RC-A002 | P0 | 未认证发送 | 返回 Unauthenticated，不创建消息/序号/event |
| RC-A003 | P0 | 非成员历史 | 返回 MeetingNotFound，不泄露会议或消息 |
| RC-A004 | P0 | 非成员发送 | 返回 MeetingNotFound，不写入消息 |
| RC-A005 | P0 | Deleting 会议历史/发送 | 统一 MeetingNotFound，不返回 Deleting 状态细节 |
| RC-A006 | P0 | Human 作者解析 | 只按当前 UserId 找会议内 Human Participant，忽略客户端作者字段 |
| RC-A007 | P0 | Agent 公共入口范围 | 本主题 Abstraction 不公开可伪造 participantId 的 Agent 提交 DTO；内部 AppendResolvedAsync 不可由 Web 直接调用 |
| RC-A008 | P0 | System 作者 | 受信系统用例可写入，普通浏览器上下文不能伪造 |
| RC-A009 | P0 | 首次 Human 发送 | 同一事务注册 Message、Mentions、Event 和 broadcast Delivery |
| RC-A010 | P0 | 同作者同会议同 ID 重放 | 返回 Replayed 和原 MessageDto，不分配新 Sequence、不写新 Event |
| RC-A011 | P0 | 同 ID 正文不一致 | 返回 SubmissionMismatch/`message_submission_mismatch`，不覆盖原消息 |
| RC-A011A | P1 | 瞬时追加冲突 | 无法在本次请求安全收敛时返回 Conflict；同一 ClientSubmissionId 重试可收敛为 Created/Replayed |
| RC-A012 | P0 | MessageId/Sequence 服务端生成 | 客户端提供伪造值不会进入实体 |
| RC-A013 | P0 | Event/delivery 原子性 | 任一写入失败全部回滚，不留下孤儿 message/mention/event |
| RC-A014 | P0 | Dispatcher signal | 提交成功后唤醒 dispatcher；事务失败不唤醒成功路径 |
| RC-A015 | P0 | Sequence 分配 | 空会议从 1 开始，连续发送得到 1/2/3 |
| RC-A016 | P0 | 并发追加 | 两个独立 scope 都成功时得到不同序号，消息不覆盖 |
| RC-A017 | P0 | Append 与 Delete 先后 | Delete 先提交则 Append NotFound；Append 先提交则消息归删除清理 |
| RC-A018 | P0 | 追加持有实时 gate | gate 关闭后不执行外部广播/后处理 |
| RC-A019 | P0 | 事件消费者按作者类型 | Human/Agent 消息保留 mentions；System 不产生可触发 AI 的 mention |
| RC-A020 | P1 | TimeProvider | CreatedAtUtc 使用注入 UTC 时间，取消令牌完整传递 |
| RC-A021 | P0 | 历史无方向游标 | 返回高水位以内最新一页，items ASC |
| RC-A022 | P0 | beforeSequence | 返回更早页，边界不重复，下一 before 可继续 |
| RC-A023 | P0 | after + through | 只返回 after 之后且不超过 through 的消息 |
| RC-A024 | P0 | 游标非法 | before+after、before+through、负数、through<after、limit 超界返回 ValidationFailed |
| RC-A025 | P1 | 空历史 | 返回成功空页，不误报 MeetingNotFound |
| RC-A026 | P1 | MessageDto 投影 | 作者快照、mentions、时间和 sequence 映射完整，不暴露领域实体 |
| RC-A027 | P0 | 多消费者 delivery | Catalog 有 broadcast/audit 两个消费者时，同一事件同事务创建两条独立 delivery；一方状态不覆盖另一方 |
| RC-A028 | P0 | Consumer catalog 校验 | 空名称、重复名称、未版本化名称使启动/目录构建失败，运行期目录不可变化 |
| RC-A029 | P1 | System 契约不隐式触发 | 受信 AppendSystem 可用，但会议创建/加入用例不会在事务后隐式补写系统消息 |
| RC-A030 | P1 | Consumer 移除/重命名 | 存在未完成 delivery 时，缺失 ConsumerName 不能静默完成；部署迁移必须先完成、迁移或清理旧 delivery |
| RC-A031 | P0 | 幂等规范化顺序 | 已有 ID 重发时首尾空白/NFC 等价正文返回 Replayed；超长正文仅在幂等未命中后返回 ValidationFailed |
| RC-A032 | P1 | 追加冲突收敛落点 | 冲突由 Runner/Repository 转换后，Service 在新 Runner 事务只重查一次幂等记录，不复用污染状态或无限重试 |
| RC-A033 | P0 | Hub 订阅授权用例 | Active+Human 成员返回 ParticipantId/HighWatermark；空历史为 0，非成员/Deleting 返回 MeetingNotFound |

## 9. Repository.Sqlite 与 Migration 测试

项目：`Server/Tests/RoundTableMeeting.Repository.Sqlite.Tests`。

| 编号 | 优先级 | 场景 | 预期 |
| --- | --- | --- | --- |
| RC-R001 | P0 | 空库 Migration | 创建四张消息/事件表、索引、外键和 CHECK |
| RC-R002 | P0 | 004 数据库升级 | 既有 Meetings/Participants 数据保持不变，可读取新表 |
| RC-R003 | P0 | MeetingId + Sequence 唯一 | 同会议重复序号被拒绝；不同会议可使用相同序号 |
| RC-R004 | P0 | MessageId 唯一 | 重复消息 ID 被拒绝 |
| RC-R005 | P0 | 作者/会议 Restrict 外键 | 不存在会议或参与者无法写入；删除前必须清理消息 |
| RC-R005A | P0 | 作者跨会议复合外键 | 会议 A 消息引用会议 B ParticipantId 时数据库拒绝，即使两个单独 ID 都存在 |
| RC-R006 | P0 | ClientSubmissionId 唯一 | 参与者消息按 MeetingId+AuthorParticipantId+ID 唯一，System 按 MeetingId+System+ID 唯一；不同作者可使用同 ID |
| RC-R007 | P0 | MessageMention 唯一 | 同 MessageId + StartOffset 重复写入被拒绝；同目标不同范围允许保存 |
| RC-R008 | P0 | Mention 外键 | 消息/目标参与者不存在时拒绝；会议清理按约定顺序成功 |
| RC-R008A | P0 | Mention 跨会议复合外键 | Mention.MeetingId、MessageId、TargetParticipantId 不属于同一会议时数据库拒绝 |
| RC-R009 | P0 | Event 唯一引用 | 同一 MessageId 不能创建两个追加事件 |
| RC-R009A | P0 | Event 复合一致性 | Event 的 MeetingId 或 Sequence 与 Message 不一致时数据库拒绝 |
| RC-R010 | P0 | Delivery 唯一 | 同 EventId + ConsumerName 不能重复注册 |
| RC-R011 | P0 | 枚举 CHECK | 非法 AuthorKind/DeliveryState 被 SQLite 拒绝 |
| RC-R012 | P0 | System 作者 CHECK | System+非空 AuthorParticipantId、Human/Agent+空值均被拒绝 |
| RC-R013 | P0 | Sequence 范围 CHECK | 0、负值和超过 `9,007,199,254,740,991` 被拒绝 |
| RC-R014 | P1 | 内容列长度 | Domain/Application 负责越界拒绝，不依赖 SQLite TEXT 长度 |
| RC-R015 | P0 | GetNextSequence 同事务 | 并发独立连接不会分配相同序号 |
| RC-R016 | P0 | 历史 NoTracking 投影 | 查询不污染 ChangeTracker，不返回 EF 实体引用 |
| RC-R017 | P0 | 游标索引 | before/after/through 查询使用 MeetingId+Sequence 范围，不做 offset 分页 |
| RC-R018 | P0 | 删除原子清理 | deliveries/events/mentions/messages 全部删除或全部回滚 |
| RC-R019 | P1 | Delivery 租取 | 仅 Pending 且 AvailableAt<=now 或 Leased 且 LeaseExpiresAt<=now 可租取，退避中的 Pending 和 Suspended 不可被 worker 抢占 |
| RC-R020 | P0 | LeaseId 条件完成 | 旧 worker 不能完成新租约；正确 LeaseId 才能 Completed |
| RC-R021 | P1 | 退避时间 | 失败增加 AttemptCount 并计算 AvailableAtUtc，超时可再次租取 |
| RC-R022 | P1 | 数据库异常分类 | 已知幂等/序号冲突转稳定业务错误，其他 DbUpdateException 不泄露 SQL |
| RC-R023 | P1 | Lease 完成 0 行 | 删除清理后匹配 0 行视为完成，LeaseId 被替换时旧 worker 不能覆盖新租约 |
| RC-R025 | P1 | Delivery 重试上限 | 达到告警阈值转 Suspended，不无限热重试；显式 Resume 可恢复 Pending |
| RC-R024 | P1 | 既有参与者表重建 | AddRealtimeChat 升级保留 004 外键、Human UserId 过滤唯一索引、名称唯一索引和原有数据 |

## 10. Architecture 和 DI 测试

项目：`Server/Tests/RoundTableMeeting.ArchitectureTests` 与 IntegrationTests 源码边界测试。

| 编号 | 优先级 | 场景 | 预期 |
| --- | --- | --- | --- |
| RC-C001 | P0 | Application 引用扫描 | 不引用 Web、SignalR、EF Core、Repository.Sqlite、IConfiguration、IHubContext |
| RC-C002 | P0 | Abstraction 契约扫描 | 不暴露 DbContext、SqliteConnection、HubConnection、领域实体或 Cookie 类型 |
| RC-C003 | P0 | Domain 引用扫描 | 不引用 ASP.NET Core、SignalR、EF Core、JSON transport 类型 |
| RC-C004 | P0 | 单一追加边界 | Controller、Hub、MCP 预留代码只能依赖 IMessageAppendService，不能直接 RegisterAdd/DbContext |
| RC-C005 | P0 | 单一消息模型 | 不存在 AgentMessage/AI 专用消息表/AI 专用历史 Controller |
| RC-C006 | P0 | 作者类型契约 | ParticipantKind 只允许 human/agent；MessageAuthorKind 允许 human/agent/system，system 不进入参与者枚举 |
| RC-C007 | P0 | 事件消费者边界 | 广播消费者和未来处理器通过 IMessageEventConsumer/Dispatcher，不在追加请求内同步调用；ConsumerName 只来自注册描述符 |
| RC-C008 | P0 | 删除资源登记 | MeetingDeletionRecoveryService 明确包含 deliveries/events/mentions/messages 清理步骤 |
| RC-C009 | P0 | DI scope | Message repositories、UnitOfWork、TransactionRunner、DbContext 同 scope；Dispatcher/presence/gate Singleton/HostedService 依赖不跨持有 Scoped |
| RC-C010 | P0 | Dispatcher scope | Catalog/HostedService 不持有消费者实例；每条或每批消费使用独立 scope 解析消费者，不跨 await 保存 Scoped DbContext |
| RC-C011 | P0 | SignalR 入口 | Hub 只负责连接/组/事件，不直接修改消息实体 |
| RC-C012 | P1 | 客户端依赖边界 | 只有 meetingChatConnection.ts import @microsoft/signalr；组件不 import generated Client |
| RC-C013 | P0 | 消息范围扫描 | 本主题不提前出现 MCP、AI Task、上下文裁剪、接管和循环保护实现 |
| RC-C014 | P1 | 事件消费者版本 | ConsumerName 符合 `name-vN` 格式，新增 AI consumer 不复用 broadcast 名称；每条消息为目录全部消费者创建 delivery |
| RC-C015 | P0 | Agent 入口不可伪造 | Abstraction 无 AppendAgentAsync/ParticipantMessageSubmission 公共契约，Web 无 Agent message endpoint |
| RC-C016 | P0 | Hub endpoint metadata | MapHub 明确附加 RequireBrowserOriginAttribute 和 NoStoreResponseAttribute，Origin/缓存中间件能从 negotiate/transport endpoint 读取 metadata |
| RC-C017 | P0 | 删除用例 gate 接线 | MeetingWorkspaceService.DeleteAsync 依赖 IMeetingRealtimeGate，并存在失败释放/成功 CommitClosed 路径 |
| RC-C018 | P0 | 发送响应 DTO | OpenAPI/Controller 使用 SubmitMessageResultDto，不直接把 Application Result 暴露为传输模型 |
| RC-C019 | P1 | OpenAPI transformer 共存 | Meeting 与 Message transformer 同时注册且只修改各自 operation，不覆盖对方响应/header |
| RC-C020 | P1 | 实体工作单元兼容 | 四个新领域实体继承 BaseEntity/IEntity<Guid>，可由现有 IUnitOfWork 登记 |

## 11. HTTP IntegrationTests

使用 WebApplicationFactory、真实 Cookie、临时 SQLite、ManualTimeProvider 和真实 ProblemDetails。

| 编号 | 优先级 | 场景 | 预期 |
| --- | --- | --- | --- |
| RC-H001 | P0 | 未登录 GET 历史 | 401 `unauthenticated`，不查询消息或消耗发送配额 |
| RC-H002 | P0 | 未登录 POST 消息 | 401，不创建消息、event、delivery 或 sequence |
| RC-H003 | P0 | Origin 缺失/null/外部 | 403 `origin_not_allowed`，Application 不执行 |
| RC-H004 | P0 | 非 JSON POST | 415 `unsupported_media_type` |
| RC-H005 | P0 | 认证后消息限流 | 超限 429 `message_send_rate_limited`，含 Retry-After，不写消息 |
| RC-H006 | P0 | 限流执行阶段 | Resource filter 在模型绑定/Application 前拒绝超限；恶意大正文不绕过配额 |
| RC-H007 | P1 | 限流来源地址 | 可信代理使用处理后地址，非可信转发头忽略，地址为空只按参与者维度 |
| RC-H007A | P1 | 限流账号维度 | 同一账号在多个会议的不同 ParticipantId 发送时共享 PerUserLimit，参与者/来源维度仍独立计数 |
| RC-H008 | P0 | 首次发送 | 201，返回 MessageDto、Sequence=1，响应 no-store/no-cache |
| RC-H009 | P0 | 发送重放 | 相同 ID 返回 200 Replayed，MessageId/Sequence 不变 |
| RC-H010 | P0 | 发送 mismatch | 409 `message_submission_mismatch`，不返回内部索引或原正文 |
| RC-H010A | P1 | 发送瞬时冲突 | 409 `message_append_conflict`，客户端可用同一 ID 重试，不创建重复消息 |
| RC-H011 | P0 | 伪造作者字段 | 请求中的 participantId/kind/sequence/messageId/createdAt 被忽略或拒绝，响应使用服务端事实 |
| RC-H012 | P0 | Human mentions | 单/多目标提及返回稳定 participantId、displayName 快照和范围 |
| RC-H013 | P0 | System message | 受信内部用例消息在历史和实时 DTO 中为 system，浏览器不能伪造 |
| RC-H014 | P0 | 非成员历史/发送 | 404 `meeting_not_found`，不泄露会议/参与者/消息 |
| RC-H015 | P0 | Deleting 会议 | 历史/增量/发送统一 404；不返回 Deleting 或清理进度 |
| RC-H016 | P0 | 跨会议 ID | A 的账号/消息不能读取或写入 B；URL 路由不串线 |
| RC-H017 | P0 | 空历史 | 200 空 items、hasMoreBefore=false、hasMoreAfter=false |
| RC-H018 | P0 | 最新页 | 无方向游标返回高水位内最新页，items ASC |
| RC-H019 | P0 | before 页 | 返回更早消息，边界不重复，连续请求可读完 |
| RC-H020 | P0 | after 页 | 返回 after 之后消息，items ASC |
| RC-H021 | P0 | through 高水位 | 追平期间新增消息不改变本次 through 范围 |
| RC-H022 | P0 | 非法游标 | before+after、before+through、负数、through<after、limit 越界均 400 validation_failed |
| RC-H023 | P0 | 正确缓存头 | 所有消息成功和错误响应 no-store/no-cache，正文不进入缓存 |
| RC-H024 | P0 | 时间和序号 JSON | createdAt 为 UTC DateTimeOffset，sequence 为 JSON number 且不丢精度 |
| RC-H025 | P0 | 作者枚举 JSON | human/agent/system 字符串；整数枚举输入被拒绝 |
| RC-H026 | P0 | 发送正文 XSS | `<script>` 等内容按纯文本返回和存储，不执行、不转 HTML |
| RC-H027 | P1 | 请求取消 | 客户端取消不会留下半条消息；取消响应不产生未处理异常日志 |
| RC-H028 | P0 | 敏感日志扫描 | 不含消息正文全集、Cookie、会议码、Origin 值或完整请求体 |
| RC-H029 | P1 | 首次发送 Location | 当前无单消息 GET 端点，201 不返回指向不存在路由的 Location |
| RC-H030 | P1 | 幂等重发限流取舍 | 已成功消息的同 ID 重试仍在 Web 限流前计数并可能 429；响应和文档明确该单实例取舍 |

## 12. SignalR IntegrationTests

| 编号 | 优先级 | 场景 | 预期 |
| --- | --- | --- | --- |
| RC-S001 | P0 | 匿名 negotiate/connect | 401，不建立会议组或 presence |
| RC-S002 | P0 | 外部 Origin negotiate/WebSocket | 403 `origin_not_allowed`，不建立连接 |
| RC-S002A | P0 | Hub endpoint metadata | negotiate、WebSocket、SSE、长轮询 endpoint 均携带 Origin/no-store metadata；缺失/null/多值 Origin 均拒绝，协商响应不可缓存 |
| RC-S002B | P0 | Subscribe 租约生命周期 | Subscribe 共享租约只覆盖单次方法调用，连接存续期不持有；Closing 后新订阅立即拒绝 |
| RC-S003 | P0 | 非成员 SubscribeMeeting | HubException 只含 `meeting_not_found`，不返回会议存在性或参与者列表 |
| RC-S004 | P0 | Deleting 会议订阅 | HubException 只含 `meeting_not_found`，不加入 group |
| RC-S005 | P0 | 合法订阅 | 加入服务端生成 group，返回在线 ParticipantId 列表和 highWatermarkSequence；空历史高水位为 0 |
| RC-S006 | P0 | 客户端自选 group/participantId | Hub 不接受自选值，服务端按上下文绑定 |
| RC-S007 | P0 | 一个连接切换会议 | 先移除旧组/presence，再绑定新会议；旧事件不再到达 |
| RC-S008 | P1 | 双浏览器消息广播 | 已提交消息广播给同会议在线成员，不广播给其他会议 |
| RC-S009 | P0 | 广播包含完整 DTO | messageId/meetingId/sequence/author/content/mentions/createdAt 完整 |
| RC-S010 | P0 | 事件重复 | 客户端可收到重复 messageAppended，服务端不承诺 exactly-once |
| RC-S011 | P0 | 事件乱序 | 客户端按 sequence 排序，不按到达顺序覆盖事实 |
| RC-S012 | P0 | 多连接 presence | 同 ParticipantId 两标签页只广播一次 online；一页断开不广播 offline |
| RC-S013 | P0 | 最后一连接断开 | 宽限期后广播 offline，参与者关系和历史不删除 |
| RC-S014 | P1 | 断线重连 | 新连接重新订阅、返回 presence/highWatermark，并可历史补齐 |
| RC-S015 | P0 | meetingDeleted 事件 | 所有连接收到删除通知，关闭/移除组；旧连接再次操作被拒绝 |
| RC-S016 | P0 | 删除与广播并发 | gate 保证屏障提交后无迟到 messageAppended |
| RC-S017 | P1 | Hub 取消/异常 | 注册表幂等释放，异常不泄露内部堆栈 |
| RC-S018 | P1 | 服务端重启 presence | 重启后旧 presence 不被伪造恢复，重新连接后重新广播 online |

## 13. Dispatcher、Gate 和删除恢复测试

项目：`Server/Tests/RoundTableMeeting.IntegrationTests`，必要时新增独立 `MessageEventDispatcherTests.cs`、`MeetingRealtimeGateTests.cs`。

| 编号 | 优先级 | 场景 | 预期 |
| --- | --- | --- | --- |
| RC-E001 | P0 | 消息事务创建 delivery | Message/Event/Delivery 一起提交，任一失败全部回滚 |
| RC-E002 | P0 | Dispatcher 租取 | 只租取 Pending/过期 Leased，设置 LeaseId 和过期时间 |
| RC-E003 | P0 | 广播成功 | 查询 MessageDto、广播、按 LeaseId 完成 delivery |
| RC-E004 | P0 | 广播失败 | delivery 回 Pending、AttemptCount+1、退避后重试，不影响消息读取 |
| RC-E004A | P1 | 退避扫描边界 | AvailableAtUtc 未到时不重复租取；达到时间后才可重试，达到告警阈值后降低频率并记录告警 |
| RC-E004B | P1 | Suspended 恢复 | Suspended 不被自动扫描；显式 Resume 后可重新租取，AttemptCount 保留 |
| RC-E005 | P0 | 进程重启租约 | 旧租约过期后新 Dispatcher 可重取并广播 |
| RC-E006 | P0 | 旧 LeaseId 完成 | 旧 worker 不能覆盖新租约状态 |
| RC-E007 | P0 | 无在线成员 | delivery 可以完成；未来客户端通过历史读取消息 |
| RC-E008 | P0 | 处理前会议删除 | consumer/gate 复查 Active，Deleting 不广播、不创建后续任务 |
| RC-E009 | P0 | 共享 gate 与独占关闭 | 已持有共享租约完成后删除取得独占权；独占后拒绝新共享租约 |
| RC-E009A | P0 | Writer priority | 删除进入 Closing 后持续到达的新广播/订阅不能取得共享租约，存量归零后独占必然成立 |
| RC-E010 | P0 | 删除预检/事务失败 | 权限、确认名称或事务失败时独占租约 Dispose 释放，Active 会议重新允许共享租约，不进入半关闭状态 |
| RC-E010A | P0 | 删除独占超时 | 存量共享未在上限内释放时返回 meeting_delete_busy、释放 Closing、会议仍 Active，可再次删除 |
| RC-E010B | P0 | Gate 与 SQLite 时序 | 删除等待共享归零期间未开启写事务；独占成立后才开启短写事务，不与消费者 DB 读取反向等待 |
| RC-E011 | P0 | 删除消息资源顺序 | deliveries/events/mentions/messages 清理完成后再清理会议资源 |
| RC-E012 | P0 | 删除幂等重入 | 已不存在资源视为完成，不重复广播、不返回第二个成功结果 |
| RC-E013 | P0 | 删除服务重启 | 启动扫描 Deleting，继续清理；会议不恢复 Active |
| RC-E014 | P0 | 删除后追加事件 | 屏障建立后不能新增 message/event/delivery |
| RC-E015 | P1 | 删除与 dispatcher 并发 | 不产生删除后外部行为，delivery 最终 Completed 或被清理 |
| RC-E016 | P1 | 过期 delivery/receipt 混合 | 聊天 delivery 清理不误删目录 004 Deleted receipt，两个协调器边界清晰 |
| RC-E017 | P1 | 安全日志 | Dispatcher/gate/recovery 失败日志只含 ID、步骤、异常类型和 traceId |
| RC-E018 | P0 | DeleteAsync 成功提交 gate | 真实 MeetingWorkspaceService.DeleteAsync 成功后调用 CommitClosed，通知恢复器，后续共享租约被拒绝 |
| RC-E019 | P1 | 物理清理 Forget | 删除完成后 Forget 移除内存 gate 项；重复 Forget 幂等，不导致字典增长 |
| RC-E020 | P1 | 恢复器删除通知 | 清理轮次可重发 meetingDeleted；服务重启后客户端以 404 兜底，不恢复 Active |

## 14. OpenAPI 和 Kiota 测试

| 编号 | 优先级 | 场景 | 预期 |
| --- | --- | --- | --- |
| RC-K001 | P0 | `pnpm generate:api` | 生成消息历史、发送请求器和 DTO |
| RC-K002 | P0 | 发送双成功 | operation 接受 201 Created 和 200 Replayed，二者 schema 均为 SubmitMessageResultDto `{message,replayed}` |
| RC-K003 | P0 | 错误映射 | 400/401/404/409/415/429/500 code 可识别 |
| RC-K004 | P1 | Retry-After | 429 响应声明并生成 Retry-After header |
| RC-K005 | P0 | 作者枚举 | `human | agent | system` 字符串，不生成数字枚举 |
| RC-K006 | P0 | Sequence 类型 | Kiota 映射为 number，客户端运行时验证 `Number.isSafeInteger` 且 >=1；服务端契约限制在 MAX_SAFE_INTEGER 内 |
| RC-K007 | P0 | Mention DTO | participantId、displayName、startOffset、length 全部生成 |
| RC-K008 | P1 | MessagePage 游标 | before/after/through、hasMoreBefore/hasMoreAfter 和两个 next cursor 字段完整 |
| RC-K009 | P1 | no-store/OpenAPI | operation 文档不生成缓存示例或绝对开发地址 |
| RC-K010 | P1 | 生成目录格式 | UTF-8 无 BOM、CRLF、无诊断/临时文件、只保留一个 `/api` 前缀 |

## 15. 客户端 MessageStore 和 Reducer Vitest

测试与 `Client/roundTableMeeting/src/features/meetingWorkspace` colocate；当前 reducer 测试位于 `chat/messageReducer.test.ts`，生成器归一化测试位于 `scripts/normalize-generated-output.test.mjs`。

| 编号 | 优先级 | 场景 | 预期 |
| --- | --- | --- | --- |
| RC-V001 | P0 | 首次历史页 | 空历史显示空状态，有消息按 sequence ASC 展示 |
| RC-V002 | P0 | messageId 去重 | HTTP 与 SignalR 同一消息只显示一次 |
| RC-V003 | P0 | sequence 排序 | 乱序事件最终按服务端 sequence 排列 |
| RC-V004 | P0 | sequence 缺口 | 不推进 lastAppliedSequence，触发 afterSequence 补齐 |
| RC-V005 | P0 | highWatermark | throughSequence 固定，追平期间新增事件暂存后合并 |
| RC-V006 | P1 | beforeSequence | 加载更早消息保持滚动锚点，hasEarlierMessages 正确 |
| RC-V006A | P0 | 首屏超过一页历史 | 高水位 1000、首屏 901-1000 时 liveBaseline=900、lastApplied=1000、hasMoreBefore=true、hasMoreAfter=false，不自动请求 1-900 |
| RC-V006B | P0 | 空历史基线 | highWatermark=0、空 items 时 liveBaseline=0、lastApplied=0、hasMoreBefore=false、hasMoreAfter=false |
| RC-V007 | P0 | 旧 meetingId action | 旧会议历史/事件/presence 不修改当前会议状态 |
| RC-V008 | P0 | 旧 generation action | 迟到连接回调和请求响应被丢弃 |
| RC-V009 | P0 | messageId 内容冲突 | 保留首次服务端事实，不由客户端覆盖 |
| RC-V010 | P1 | presence 快照+事件 | 快照初始化，后续 online/offline 更新只影响当前会议 |
| RC-V010A | P1 | rejected 状态 | Hub meeting_not_found 进入 rejected 并交给页面回退；自动重连耗尽进入 disconnected，不混用两者 |
| RC-V011 | P0 | meetingDeleted | 清空消息/presence，关闭连接并触发工作台回退 |
| RC-V011A | P0 | 单一回退所有者 | RealtimeChatRegion 只发 onMeetingUnavailable；MeetingWorkspacePage 只导航一次并保持 suppressAutoSelect 语义 |
| RC-V012 | P1 | 重连状态 | connecting/reconnecting/disconnected/connected 文案和按钮正确 |
| RC-V013 | P0 | 发送中 | 不插入乐观已发送气泡，不伪造 sequence |
| RC-V014 | P0 | 发送成功 | 201/200 都合并服务端 MessageDto，清除草稿 |
| RC-V015 | P0 | 网络失败重试 | 保留正文和同一 ClientSubmissionId，重试不生成新 ID |
| RC-V016 | P0 | 明确错误 | 400/404/429 不自动重复提交；正文编辑后才生成新 ID |
| RC-V017 | P1 | AbortError | 路由切换/卸载取消请求不显示错误 |
| RC-V018 | P1 | 提及候选偏移 | 编辑正文后重新按 NFC+Trim 计算，旧范围不复用 |
| RC-V018A | P1 | 缺口状态最小化 | 不维护 pendingSequences 冗余集合；缺口只由有序消息 Map、liveBaseline 和 lastApplied 的序号差计算 |

## 16. 客户端 API、连接和组件测试

| 编号 | 优先级 | 场景 | 预期 |
| --- | --- | --- | --- |
| RC-V019 | P0 | listMessages mapping | 生成模型映射完整，非法 sequence/kind/日期拒绝 |
| RC-V020 | P0 | sendMessage mapping | 只提交 clientSubmissionId/content/candidates，不提交作者字段 |
| RC-V021 | P0 | SignalR 单一适配 | 只有适配文件创建 HubConnection，组件不接触实例 |
| RC-V022 | P0 | Connect lifecycle | connect/subscribe/reconnect/disconnect/Dispose 成对调用 |
| RC-V023 | P0 | MessageTimeline mention split | 合法范围高亮，非法/重叠范围降级普通文本，不注入 HTML |
| RC-V024 | P1 | MessageTimeline author | Human/Agent/System 都有文字类型标记 |
| RC-V025 | P0 | MessageComposer send | 空正文不调用 API，合法正文调用一次并进入 sending |
| RC-V026 | P0 | MessageComposer retry | 失败重试复用同一 ID，编辑正文清除旧失败对象 |
| RC-V027 | P1 | Mention selector | 候选来自当前会议全部参与者，未知 @ 仍可发送 |
| RC-V028 | P1 | scroll behavior | 底部自动跟随，阅读旧消息时显示新消息提示不强制跳转 |
| RC-V029 | P0 | XSS rendering | 正文 `<img onerror>` 等作为文本节点，不执行 |
| RC-V030 | P1 | responsive layout | 长正文/名称/状态按钮不溢出，窄屏时间线可用 |

## 17. 浏览器验收

开发使用 Vite 5174 + 服务端 API/Hub 代理；所有场景使用真实服务端，不使用 mock 数据。SignalR 传输验收分别强制 WebSocket、ServerSentEvents 和 LongPolling，并在 Network 中确认实际请求类型；同源 SSE/LongPolling GET 缺少 Origin 属于浏览器规范行为，negotiate/写请求仍必须执行 AllowedOrigins 校验。

| 编号 | 优先级 | 场景 | 预期 |
| --- | --- | --- | --- |
| RC-B001 | P0 | Alice/Bob 登录并进入同一会议 | 两个浏览器显示同一会议工作台；若尚无人发送消息则为空，不因加入流程隐式生成系统消息 |
| RC-B002 | P0 | Alice 发送普通文本 | Alice/Bob 实时看到同一 MessageDto、作者和服务端时间 |
| RC-B003 | P0 | Bob 连续发送多条 | 两端 sequence 一致且按服务端顺序显示 |
| RC-B004 | P0 | 并发发送 | 两端最终看到全部消息，无覆盖、重复或客户端临时序号 |
| RC-B005 | P0 | Alice @Bob | 发送后历史和实时消息显示提及高亮，服务端保存 Bob ParticipantId |
| RC-B006 | P0 | 多目标/重复提及 | 多目标和同一目标的每次合法出现均高亮；后续业务目标按 ParticipantId 去重 |
| RC-B007 | P1 | 长正文/换行 | 正文完整保存、换行保留、时间线可滚动，无横向溢出 |
| RC-B008 | P0 | 刷新当前会议 | 重新订阅并通过高水位/afterSequence 补齐，不重复显示消息 |
| RC-B009 | P0 | 短暂断网重连 | 页面显示重连状态，恢复后补齐断网期间消息和 presence |
| RC-B010 | P0 | 模拟事件丢失 | 历史接口补回缺口，lastAppliedSequence 只在连续时推进 |
| RC-B010A | P0 | 大历史首次进入 | 预置超过一页消息后首次进入只载入最新页且连接追平，不把未加载旧历史当作断线缺口 |
| RC-B011 | P0 | 会议 A/B 切换 | 消息、presence、草稿、连接和迟到事件不串会 |
| RC-B012 | P0 | 非成员深链接 | 详情/历史均不可读，返回工作台无选择和安全提示 |
| RC-B013 | P0 | 多连接 presence | 同一账号双标签页在线状态不抖动，全部关闭后显示离线 |
| RC-B014 | P0 | 发送失败 | 不显示成已发送，保留正文和重试入口 |
| RC-B015 | P0 | 删除会议 | 创建者删除后两端收到删除通知，消息区清空并回无选择工作台 |
| RC-B016 | P0 | 删除后旧页面操作 | 历史、发送和 Hub 操作均不可继续，不能恢复会议 |
| RC-B017 | P1 | 服务端重启 | Active 会议消息可读取；presence 需重新连接；Deleting 会议继续清理 |
| RC-B018 | P1 | 发布 SPA/Hub | `/meetings/{id}` 刷新返回应用，`/hubs/meeting-chat` 握手不被 SPA fallback 截获 |
| RC-B019 | P0 | Console/Network | 无未处理异常、无正文写入 URL、无 Cookie/会议码泄露、无 XSS |
| RC-B020 | P1 | 宽屏/窄屏截图 | 消息、状态、参与者、编辑器无重叠和溢出 |

## 18. 构建、发布和回归

~~~powershell
dotnet restore Server/RoundTableMeeting.slnx
dotnet build Server/RoundTableMeeting.slnx --configuration Debug
dotnet test Server/RoundTableMeeting.slnx --configuration Debug --no-build

Set-Location Client/roundTableMeeting
pnpm install --frozen-lockfile
pnpm generate:api
pnpm test
pnpm build
Set-Location ../..

dotnet publish Server/RoundTableMeeting/RoundTableMeeting.csproj --configuration Release --output artifacts/publish
~~~

| 编号 | 优先级 | 场景 | 预期 |
| --- | --- | --- | --- |
| RC-BLD001 | P0 | 服务端全量测试 | 002/003/004 既有测试和 005 新增测试全部通过 |
| RC-BLD002 | P0 | 客户端测试与构建 | Vitest、tsc、Vite 全部成功 |
| RC-BLD003 | P0 | Migration | 空库和 004 数据库均可升级，启动恢复正常 |
| RC-BLD004 | P0 | Kiota 生成 | 生成成功，失败不破坏旧 generated 目录 |
| RC-BLD005 | P0 | 发布构建 | 发布目录包含客户端消息区域、Hub 和服务端事件能力 |
| RC-BLD006 | P1 | API/Hub 路由 | 消息 API、negotiate、WebSocket 和 SPA fallback 路由不互相截获；未知 `/hubs/*` 不返回 SPA HTML |
| RC-BLD007 | P1 | 旧目录回归 | 002/003/004 的认证、Origin、ProblemDetails、会议创建/加入/删除、Scalar/OpenAPI 继续通过 |
| RC-BLD008 | P0 | 主题范围扫描 | 无 AI 专用消息模型、MCP 入会、任务状态机、未读或模拟消息提前实现 |
| RC-BLD009 | P1 | 文本格式 | 新增/修改文档、源代码和生成目录符合 UTF-8 无 BOM、CRLF 约定 |
| RC-BLD010 | P1 | 单实例部署 | 发布配置确认仅单实例运行；多实例部署阻止发布并要求重新设计 backplane/presence/gate |

## 19. 完成门

- 所有 P0/P1 用例通过；失败用例有明确豁免和用户确认。
- Human、Agent 预留、System 的统一消息模型和单一追加边界通过 Domain/Application/Architecture 测试。
- SQLite Migration、Sequence、幂等、mentions、event/delivery、租约和删除清理通过。
- HTTP、SignalR、Origin、Cookie、成员授权、缓存头、ProblemDetails 和日志脱敏通过。
- 高水位历史补齐、事件重复/丢失、断线重连、多连接 presence 和多会议隔离通过真实浏览器验收。
- 删除 gate 建立后无迟到广播、无新消息/事件/任务，服务重启后 Deleting 清理继续。
- OpenAPI/Kiota、Vitest、客户端构建、服务端全量测试和发布构建通过。
- 目录 005 没有提前实现后续 AI 主题的 MCP、AI 回复任务、上下文裁剪、接管、循环保护、未读或消息编辑删除。
