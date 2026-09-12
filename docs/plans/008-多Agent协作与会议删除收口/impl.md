# 多 Agent 协作与会议删除收口实现计划

## 1. 依据与实施边界

本计划落实[主题设计](design.md)，验收见[测试计划](testPlan.md)。目录 008 对应总体计划主题七；前置主题六的身份、确认、重试、接管和租约语义保持有效。

当前各阶段均待实施，本文不记录实施日志，不将前置测试通过视为本主题已完成。新增代码放在现有 Server、Client 和 Demo 项目中，docs/plans 只保存文档。

交付能力为 Agent 相互提及、独立任务与来源追踪、升级兼容，以及会议删除最终收口。平台不判断完成、冻结或业务循环，不设置协作轮数/深度/总任务预算，不增加暂停或停止 Agent 入口。人类在外部工具停止执行；网站按真实连接状态与原恢复规则处理。

## 2. 改动落点与依赖

以下路径相对仓库根目录；现有文件优先扩展，新增模型及测试文件在所属现有项目内建立。

| 层次 | 落点 | 工作内容 |
| --- | --- | --- |
| Domain | Server/RoundTableMeeting.Domain/Entities/AgentReplyTask.cs 及相关 partial 文件 | ChainId、ParentTaskId、创建来源校验；保留任务状态与恢复字段 |
| Domain / Repository 抽象 | AgentCollaborationBoundary 新实体、IAgentCollaborationRepository | 会议启用边界、按回复消息查询唯一父任务 |
| SQLite | AgentCollaborationConfigurations、DbContext、仓储与 Migrations | 映射、复合归属约束、唯一索引、删除例外触发器及迁移回填 |
| Application | Meetings/MeetingWorkspaceService.cs | 新会议与启用边界同事务建立，重放创建收据不重置边界 |
| Application | AgentCollaboration/AgentMentionTaskConsumer.cs | Human/Agent 提及统一补扫，来源继承，独立 FIFO 与幂等 |
| Application | AgentCollaborationService、AgentReliableTaskService、AgentActivityTransactions | 两种模式的回复原子性与来源投影，沿用 activity 比较机制 |
| Contracts / MCP | AgentCollaborationContracts、AgentRecoveryContracts、AgentToolSchemas、McpAgentTools | collaboration 可选字段、能力声明、真实 SDK schema 及既有工具兼容 |
| 删除 | AgentCollaborationCleanupService、MeetingDeletionRecoveryService | Deleting 内解除父引用与全部从属资源清理；保留异步 202 语义 |
| Client | Client/roundTableMeeting/src/features/meetingWorkspace | 复用消息、提及、参与者状态及删除处理；以回归验证为主 |
| 参考宿主 | Demo/AI参会联调客户端/reliable.mjs、README.md 及现有测试 | 通用来源字段兼容、双 Agent 联调、宿主停止与独立续租验证 |

阶段依赖：I01 → I02 → I03 → I04 → I05 → I06 → I07。每阶段完成对应测试再继续；数据库约束与删除适配必须随迁移交付，不能在可运行版本中只加入新外键而遗漏删除路径。

代码实施前按仓库约定对实际修改符号执行 GitNexus impact，UNKNOWN 继续核对接口调用和真实入口，不当作低风险。现有 DI 分发路径包括 MessageEventDispatcherService → IMessageEventConsumer → AgentMentionTaskConsumer。文档计划不是后续代码修改的影响分析替代品。

## 3. I01：领域与仓储契约

- [ ] 为 AgentReplyTask 增加不可变来源：ChainId 取根 Human 消息 ID；ParentTaskId 根任务为空，Agent 派生任务为来源任务 ID。不能由外部请求指定链路身份。
- [ ] 调整任务创建入口，让 Human 与 Agent 创建都明确提供经服务端解析的来源；现有任务状态、幂等键工厂和候选范围保持不变。禁止 System 创建任务。
- [ ] 为 AgentCollaborationBoundary 建模：每会议唯一、AgentTriggerStartSequence 非负且遵守现有序号安全上界；边界建立后不随重启、读取或新连接改变。
- [ ] 增加按 MeetingId + ReplyMessageId 读取唯一来源任务的仓储方法，以及边界读取/建立方法。查询始终带会议归属；不返回任取第一条的歧义结果。
- [ ] 完成不依赖数据库的领域创建、来源不可变和状态转换断言；数据库映射、约束及生产清理入口统一在 I02 验证。

退出条件：领域模型与仓储契约可编译，不依赖数据库的来源规则及状态转换单元测试通过。此阶段只记录领域部分证据，不将 MA-D01～MA-D07 整项标为通过；其数据库约束与清理入口验收在 I02 完成。

## 4. I02：数据库升级、新会议与删除适配

### 4.1 迁移与启用边界

- [ ] 按 design §4.1 替换 IX_AgentReplyTasks_ReplyMessageId_MeetingId 为部分唯一索引；扩展 MeetingTransactionRunner.IsAgentCollaborationConflict 对具体索引/列的识别及稳定错误映射，保留原触发去重键。
- [ ] 建立 ChainId 与同会议根消息、ParentTaskId 与同会议同链父任务的归属约束；核对根消息作者 Human、父回复等于当前触发消息、父触发序号早于父回复。
- [ ] 用 SQLite 触发器明确 INSERT/UPDATE 边界：Active 根任务/派生任务完整校验；来源不可改写；Deleting 禁止新增任务或替换/恢复父引用，仅允许原父引用不变或在清理时置空。非空复合外键始终保留。
- [ ] ParentTaskId 物理可空；不增加会阻止删除清理的无条件 Agent 非空 CHECK。清空父引用不改变 SourceAuthorKind、ChainId 或任务身份。
- [ ] 通过 Domain 与真实 SQLite 测试证明业务校验和数据库约束一致；直接 SQL 也不能绕过 Active 约束。

- [ ] 从主题六已存在的真实 schema 建立迁移，先校验原任务来源、ReplyMessageId 唯一性和会议归属，再回填来源及建立约束。
- [ ] 同一迁移事务为每会议记录当前最大消息 Sequence，空会议取 0；已有 Human 任务 ChainId=TriggerMessageId，ParentTaskId=null。不得改变原 TaskId、尝试、幂等键、候选范围、确认或重试事实。
- [ ] 保留旧事件的 Pending/Leased/Suspended/Completed 状态。旧 Agent 事件及补扫候选受边界过滤；旧 Human 消息继续按原规则补扫，不能按新边界跳过。
- [ ] 迁移失败整体回滚，诊断只包含必要标识，不输出消息正文或凭据；校验失败不静默修补或任取父任务。
- [ ] 新会议在 MeetingWorkspaceService.CreateAsync 的同一事务建立边界 0；创建失败无孤立边界，创建收据重放返回原会议且不重设边界。
- [ ] Active 会议边界缺失视为数据异常并保留待处理事件，不能默认取 0 而追溯触发旧消息；正常迁移及创建路径保证该行存在。Deleting 清理允许边界不存在，确保恢复可重入。
- [ ] 迁移先于业务入口与事件分发启动；重复启动仅读取既有边界，不重新计算最大序号。

- [ ] 按 design §4.1 定案实体主键、唯一会议外键、整数范围、可空边界查询、父复合外键及同链触发器；INSERT 同时检查边界存在和 Agent 序号。重建后验证六个旧触发器定义及行为。
- [ ] 按 design §8 执行停旧实例升级，包含全部 Deleting 会议；审计回复作者、Replied 回复存在性、提及所属消息和目标，保留未消费 Human 与全部投递状态。边界取已持久化消息最大 Sequence。
- [ ] 在 SqliteConnectionInterceptor 同步/异步连接打开路径及 SqliteDatabaseInitializer 迁移前后验证 foreign_keys=1；不满足时明确失败。

### 4.2 删除适配

- [ ] 清理服务要求已存在删除事务，并在事务内确认会议为 Deleting。拒绝 Active 会议借清理入口解除来源。
- [ ] 沿用取消任务、清空原恢复指针顺序，增加清空全部 ParentTaskId，包括 Replied 任务；引用清空和任务删除同事务，故障整体回滚。
- [ ] 删除收据、初始投递、尝试、任务、游标和连接后，删除 AgentCollaborationBoundary，再清原消息事件、提及、消息、参与者和会议。
- [ ] 保持已存在的创建收据处理和平台账号保留规则；不要把所有数据库表归零当作删除目标。
- [ ] 保持 Deleting 屏障提交即返回 202 Accepted；再次删除 Deleting/已删除会议沿用 404。后台清理失败继续重试，不能改成接口等待物理清理完成。
- [ ] 启动恢复覆盖新引用和边界表，恢复器不派生业务任务；旧凭据、幂等回复和协作状态查询不能绕过删除屏障。

- [ ] 本阶段先搭建测试程序集中的后台清理暂缓开关、引用清空后故障点、清理提交前后进程切点；复用已有屏障切点。通过真实 DI 清理实现及 EF 命令/事务诊断观测，覆盖无事务、Active 和 ExecuteUpdate 批量行为，不能以替身清理算法代替。恢复器事务内新增 Deleting 检查。I06 复用这些设施，不推迟到 I06 才建立。

退出条件：MA-D01～MA-D10、MA-M01～MA-M03、MA-M07～MA-M08、MA-M11、MA-M13、MA-X01～MA-X05、MA-X10、MA-B05～MA-B06 通过，且原主题六删除核心用例保持通过。来源清理测试可用合法持久化样例构造父子任务；依赖新消费者的 MA-M04～MA-M06、MA-M09～MA-M10 在 I03 完成。此阶段可暂不开放 Agent 触发，但迁移后的系统须能正常启动和删除会议。

## 5. I03：提及任务与消息原子性

- [ ] 扩展消费者的入口和候选过滤，两处都允许 Human 与边界之后的 Agent；System 始终跳过。保留原消费者注册名，避免旧事件失去消费入口。
- [ ] 使用持久化提及按目标去重。针对事件涉及的每个 AI，从 AllocatedThroughSequence 按 Sequence 补扫到事件位置，补齐期间全部有效提及；不按来源链另建队列。
- [ ] Human 直接确定根 ChainId；Agent 先校验启用边界，再通过 ReplyMessageId 读取唯一父任务，继承父任务 ChainId。处理不完整来源时回滚，不猜测新根链。
- [ ] 用现有唯一键读取已存在任务；新任务固定候选范围、入队并推进分配游标。所有受影响目标的本次分配和可见 activity 变化同事务提交；不引入按目标分别提交造成的半成功。
- [ ] 保持初始快照与新任务边界，新加入 AI 不补发入会前任务；离线 AI 可继续排队，自我提及排在原任务后。
- [ ] 沿用当前事务内顺序补扫；需要分页读取时不能提前确认事件完成，也不能用页面进度代替任务游标。分批提交若改变全目标原子性，须先修订设计，不能在实现中隐式改变。
- [ ] 可靠与 legacy 回复路径均验证：消息、提及、事件、Replied 和 ReplyMessageId 原子提交；反查仅看到已提交父任务。回复重放无新事件，消费者重放无新任务。
- [ ] 保留 AgentActivityTransactions 的提交内 payload 比较，后排入队不制造无变化 revision；唤醒在提交后执行，丢信号不丢任务。
- [ ] 异常交给既有事件重试与 Suspended/ResumeAsync 恢复机制；来源损坏不能标为成功。该技术恢复不增加业务暂停界面或协议。

- [ ] 落实 design §5.2 的无目标短路、根/派生来源分类校验、多目标损坏耦合和可信本地 DI 事件恢复路径；恢复不是新增产品端点。
- [x] activity 前后比较只读取当前连接与未终结队首，候选/提及批量查询；任务序号范围索引与未终结队首部分索引已接入迁移，MA-Q11 实测见 testPlan。
- [ ] 验证同尝试重放和新旧尝试竞争的错误形状，不扩大异常捕获范围。

退出条件：MA-A01～MA-A12、MA-Q01～MA-Q11、MA-R01～MA-R07，以及 MA-M04～MA-M06、MA-M09～MA-M10、MA-M12 通过。独立验证真实 MCP 双 Agent 正常路径，而非仅直接调用消费者。

## 6. I04：MCP 返回与参考宿主

- [ ] 统一来源 DTO，collaboration 包含 chainId、parentTaskId、sourceAuthorKind、sourceParticipantId；沿用现有 Guid/作者类型序列化惯例，禁止泄露恢复密钥和绑定。
- [ ] AgentMentionResultDto 与 AgentTaskSummaryDto 增加可选 collaboration。复用任务摘要的位置均同步映射，包括 queueHead、requestedTask、resume 的相关摘要及已有错误摘要；仍先按原规则校验所有权。
- [ ] 更新成功 DTO 映射、AgentToolSchemas 及真实 SDK 输出 schema，使实际 JSON 与工具发现一致；根 parentTaskId=null、派生非空均可验证。
- [ ] 在现有能力声明中增加 multi_agent_collaboration_v1，只有迁移和生产消费者已接入时才宣告；可靠/legacy 的既有必填字段、kind、nextAction 和可靠模式门槛不改变。
- [ ] 更新通用驻留指令与参考客户端说明，告知 Agent 可通过有效 @ 与其他参与者交流，不加入具体业务流程或固定结束条件。
- [ ] 参考宿主兼容新增字段，继续串行业务处理与独立续租。测试用脚本自身控制有限轮次或外部退出，不把测试截止条件写入服务器。
- [ ] reply 继续表示消息已接受，不新增下游完成承诺或 pending/suppressed 决策协议；客户端不能因下游尚未回复而重新提交新的回复消息。

- [ ] 逐一更新 AgentCollaborationService 的 mention/legacy summary、AgentReliableWaitService 的 mention、AgentReliableResumeService 的 summary 构造；覆盖 queueHead、requestedTask、lastRepliedTask 和 AgentRecoveryErrorData.taskSummary。可空 collaboration 省略，内部根 parentTaskId 保留 null，不增加显示名。
- [ ] capabilities 仅可靠 join/resume 声明；模板分别升级为 resident-instruction-v3-legacy / resident-instruction-v3-reliable，更新真实契约断言及 Demo README，旧服务夹具保留 v1/v2。

退出条件：MA-C01～MA-C09 及参考宿主自动化测试通过，工具总数 9（4 个既有工具加 5 个可靠专属工具）保持兼容；真实双宿主和模型用例在 I05 验收。

## 7. I05：浏览器与通用协作联调

- [ ] 使用现有 MessageTimeline、MessageComposer 和 ParticipantList 验证 AI 消息提及、Human 提及、多参与者显示与消息去重。无需新增页面、暂停按钮或完成状态。
- [ ] 验证 AI @Human 仍为可见提及；客户端不把特定正文解释成冻结或业务完成，不增加未读数量或站外通知。
- [ ] 检查 Agent activity revision、Human presence、切会订阅代数、重连历史补齐和删除后迟到事件；只修复本次协作接入引起的实际问题，不重构无关 UI。
- [ ] 运行有限多轮与多分支脚本以及两个真实模型参与者。脚本用于可控断言，真实模型用于验证上下文理解和实际相互提及，两者证据分开。
- [ ] 分别停止外部模型、关闭传输、停止续租，观察真实状态。只停模型但续租仍在时不要求立即离线；恢复后原任务身份保持。

退出条件：MA-V01～MA-V08 通过；网页验证默认使用 browser-debugging-and-control 技能和对应 Chrome DevTools 工具。本文不规定浏览器工具之外的新测试框架。

## 8. I06：全资源删除、故障与回归

- [ ] 在同一会议构造多分支、已回复父任务、未完成子任务、耗尽队首、多代连接、恢复收据与待分发事件；验证父引用解除及真实外键清理顺序。
- [ ] 复用 I02 已建立的测试程序集探针，在屏障提交前后、引用解除后、物理清理事务提交前后注入失败和进程终止。故障工具不能进入生产程序集。
- [ ] 删除与 join/resume/takeover/wait/confirm/reply/renew/retry/status/消费者分别交错，两个顺序都验证；另一个会议同时聊天。
- [ ] 区分 202、逻辑不可访问、数据库清理完成三个观察点；不以网络返回或页面消失替代逐表检查。
- [ ] 回归账号、会议、Human 群聊、可靠恢复及旧版协议。前置测试中“Agent 不触发”仅作为旧边界样例保留；启用边界后的断言应改为本主题行为，不删除既有 Human/System 隔离覆盖。

退出条件：MA-X 全部用例、MA-B01～MA-B03 通过；MA-B04 的发布包验证在 I07 完成。

## 9. I07：发布验证与完成门

使用当前项目锁定的 SDK/依赖版本，不为本主题升级无关工具链。常规命令见 testPlan.md §10；发布到仓库 artifacts 下的隔离目录，使用独立 SQLite 和端口，不操作日常数据。

- [ ] 完成 MA-B04：发布包使用空库和主题六数据库副本分别启动，证明迁移、/mcp、SPA、SignalR、身份和删除恢复均正常。
- [ ] 将每条测试用例的当前状态、测试文件/场景及实际证据更新到 testPlan.md；尚未执行、失败和环境阻塞不能写成通过，不在 docs 新建执行日志。
- [ ] 所有 P0/P1 用例及前置核心回归通过，真实双 Agent 和浏览器验收完成，清理全部临时测试会议及测试宿主。
- [ ] 核对只存在通用消息交流能力，没有残留协作预算、业务循环判定或暂停入口；文档、工具描述和发布行为一致。
- [ ] 依仓库约定检查 CRLF、链接和 diff；需要提交时另获用户确认，并先完成 GitNexus detect_changes。本文不授权自动提交或建分支。

## 10. 前置文档同步清单

本主题已进入实施验证；006/007 的当前触发边界、模板版本、能力及删除说明已同步。新增场景仍以本主题测试证据为准，不能把旧验收结果改写为新功能已通过。

- I03：006 design 的任务触发、impl 的消费者过滤、testPlan 的 AI-E007；007 design 的触发/队列说明、impl 的过滤与回归条目、testPlan §20 的“Agent 不触发”断言，统一区分旧边界内不追溯与启用后有效 Agent 派生。
- I04：006 design/impl/testPlan 的 legacy 驻留版本，007 design §9.2/§9.3、impl/testPlan 的可靠模板及能力断言，指向本主题新版本和仅可靠能力发现；校正工具计数为总数 9、既有 4、可靠专属 5。
- I06：006/007 的删除和恢复验收说明引用本主题来源清理、边界与异步 202 验收；旧测试证据保持原适用范围。同步检索六份文件，消除无适用范围的 Human-only 断言。

AgentCollaborationBoundary 是新增业务实体；既有 ArchitectureTests.AgentCollaborationBoundaryTests 是分层架构测试，不要求为此改名。Domain 用例领域部分在 I01 留证，涉及 SQL 的整项只在 I02 完成后标记通过。
