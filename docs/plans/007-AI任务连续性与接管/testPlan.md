# AI 任务连续性与接管测试方案

## 1. 定位与完成规则

本方案验收[主题设计](design.md)及[实现步骤](impl.md)，所有用例均为待实施、待执行的要求，不是测试结果。

P0 为身份串用、消息/任务丢失或重复、越序、旧连接写入、删除复活、迁移破坏和构建阻断；P1 为恢复协议、状态收敛、兼容、参考 Agent 和真实浏览器业务路径；P2 为视觉细节和诊断可读性。所有 P0/P1 通过后才能宣布主题完成。环境阻塞不能按通过计；若需要豁免，必须明确影响并由用户确认。

每个服务端用例使用独立临时 SQLite，跨请求并发使用独立 DI scope/DbContext/事务。真实 MCP 用固定 SDK 和真实 Streamable HTTP，不仅直接调用 Application。涉及提交与响应丢失的场景必须明确故障点并查询数据库事实；涉及进程崩溃必须重启真实服务进程。使用可控时间和同步屏障，不用固定 sleep 作为唯一证据。

## 2. 数据和观察约定

会议 A/B；Human Alice/Bob；AI Reviewer/分析师；每个测试生成独立 ID。准备首个入会快照 S=10、任务 A=(10,15]、B=(15,20]、接管快照 S2=25，并在区间穿插 Human/Agent/System 消息。只有 Human 有效提及创建本主题任务。

覆盖四类断点：数据库事务提交前、提交后响应前、客户端收到后确认前、确认提交后回复前。断点必须分别断言数据库任务、当前连接、尝试、两个游标、消息数量及客户端观察，不能只看 HTTP 状态码。

日志和测试失败输出不得包含会议码、recoveryKey、MCP 会话令牌、完整上下文或回复正文；使用 ID 摘要、状态、序号、字符计数和数据库计数。测试凭据来自隔离环境，不能写入仓库。

## 3. 连接、恢复与操作幂等

| 编号 | 级别 | 场景 | 预期 |
| --- | --- | --- | --- |
| CT-C01 | P0 | 可靠首次 join | 身份、连接、当前指针、初始快照、收据同事务创建；返回下一步确认 |
| CT-C02 | P0 | Human 同名、大小写及 Unicode 变体 | Human 不可接管；统一 NFC/Trim/大小写唯一规则，无重复参与者 |
| CT-C03 | P0 | 普通 create 遇在线 AI | 返回 agent_connection_already_active，不改变当前连接 |
| CT-C04 | P0 | 显式 takeover 在线 AI | 新业务 ID/新代数/新预算，原参与者 ID 和消息作者不变；旧连接立即失去读写资格 |
| CT-C05 | P0 | 两个并发 takeover | 每次提交有确定顺序，只有最终当前连接可继续；不出现两个 Connected |
| CT-C06 | P0 | resume 到新 MCP 会话 | 保持原业务 ID、预算和初始快照，增加绑定版本；旧绑定不能继续调用 |
| CT-C07 | P0 | 恢复密钥错误、跨会议或已接管 | 拒绝，不泄露会议/任务；密钥不能恢复 Superseded |
| CT-C08 | P0 | join 已提交但响应完全丢失 | 同 operationId/参数/密钥找回原结果；新 MCP 会话上重放只返回 recovery_required，无绑定副作用；再用新 operationId resume，无第二个身份/连接 |
| CT-C09 | P0 | 旧 takeover 收据在后续接管后重放 | connection_superseded，不重新执行抢占 |
| CT-C10 | P0 | 同 operationId 不同参数/目标绑定 | operation_conflict，不覆盖旧收据 |
| CT-C11 | P0 | resume 响应丢失后重放 | 同目标绑定返回同一版本，不反复增加 BindingVersion |
| CT-C12 | P0 | 接管提交后新注册失败 | 旧连接不复活，新连接条件离线；Activating 经原操作同版本最终化或条件补偿为 Disconnected；离线后需新 resume，旧连接不复活 |
| CT-C13 | P0 | 旧 MCP 关闭回调晚于 resume | 回调因旧 BindingVersion 被拒绝，不断开恢复后的连接 |
| CT-C14 | P1 | 密钥丢失后的接管 | 凭会议码与名称显式 takeover 可用，无额外外部 Agent 身份验证 |
| CT-C15 | P1 | 主动关闭与原会话短断 | 关闭后 Disconnected 可 resume；仅取消一次请求不立即离线 |
| CT-C16 | P0 | resume B2 后重放旧 B1 操作 | operation_superseded，不返回 B1 成功，不改变 B2 或失效其任务 |
| CT-C17 | P0 | join 重放发生于后续 resume 后 | 最多返回 recovery_required 与原业务 ID，不直接重新绑定或踢掉当前传输 |
| CT-C18 | P0 | 同 MCP 会话并发加入两个 AI/会议 | 传输预留只允许一个绑定；失败者不创建连接、不接管目标 |
| CT-C19 | P0 | Activating 注册前、注册后最终化前分别断开 | 从未返回可用连接；按绑定版本最终化失败或补偿，旧连接不复活 |
| CT-C20 | P0 | 原 Activating 操作重放与新 takeover 竞争 | 只可完成仍匹配的版本；已替换收据不能把旧版本转 Connected |
| CT-C21 | P0 | 同 operationId 从 create 改为 takeover | 指纹冲突，无隐式接管；joinMode 不形成绕开幂等的新作用域 |
| CT-C22 | P1 | create 遇离线 AI、takeover 拼错名称 | 分别返回 agent_takeover_required/agent_not_found，不隐式接管或新建 |
| CT-C23 | P0 | Connected 租约已过期但扫描未执行时 create | 先条件收敛离线，返回 agent_takeover_required，不误报活动连接；不自动接管；另验证可靠 create 遇未升级且 Connected+有效注册的 legacy 时返回 agent_connection_already_active，不因无租约误判离线 |
| CT-C24 | P0 | 当前指针与代数配对、resume/并发接管 | 同事务创建并匹配复合外键；resume 不递增代数，错代指针写入拒绝 |
| CT-C25 | P1 | 原密钥齐全、join 响应丢失、只有名称会议码三类恢复 | 分别 resume、收据查询后 resume、显式 takeover；不靠“离线”二字决定新建连接 |
| CT-C26 | P0 | 可靠 Activating 失败后 legacy 重入 | RequiredReliabilityMode 保留，返回 reliability_mode_required，不能因离线或激活失败降级 |
| CT-C27 | P0 | operation_pending 后补偿完成的完整客户端链路 | 原 join 重放 recovery_required、原 resume 重放 operation_superseded；原已占用会话新 resume 拒绝，新 MCP 会话用新操作成功 kind=resumed |

## 4. 上下文、确认和 FIFO

| 编号 | 级别 | 场景 | 预期 |
| --- | --- | --- | --- |
| CT-X01 | P0 | 可靠 join 未确认时新消息与提及到达 | 快照 S 稳定，新任务从 S 后分配；确认前 wait 不领取 |
| CT-X02 | P0 | 初始 confirm 响应丢失 | 相同 InitialDeliveryId 重放幂等，确认位置只推进一次；初始投递超过任务确认时限仍可在有效连接下确认 |
| CT-X03 | P0 | 初次快照未确认即接管 | 新历史可确认，完成原首次边界确认；边界之后旧任务全部保留 |
| CT-X04 | P0 | A 已确认未回复、B 排队、快照到 25 | 接管初始确认不跳到 25；A/B 原区间不变，依次恢复 |
| CT-X05 | P0 | 接管时无未完成任务但有普通新消息 | 下次提及从既有分配位置取连续范围，不能因新快照漏掉间隔 |
| CT-X06 | P0 | 可靠 wait 提交后响应丢失 | 任务 Delivered，游标未提前推进；截止前重复 wait 返回同 ID/同窗口 |
| CT-X07 | P0 | 确认事务提交后响应丢失 | 任务 Processing、尝试 Confirmed、游标原子一致，confirm 可安全重放 |
| CT-X08 | P0 | confirm 与确认截止竞争 | 以事务内取得锁后条件写时刻判断首次确认，截止前成功或截止后拒绝，不能既确认又创建有效替代尝试 |
| CT-X09 | P0 | 旧尝试迟到确认/回复 | delivery_attempt_stale 或 confirmation_expired，无游标/消息修改 |
| CT-X10 | P0 | 已确认未回复任务恢复重投 | 新 DeliveryAttemptId，稳定 TaskId/幂等键/范围；确认位置不回退 |
| CT-X11 | P0 | 同一 AI 连续 3 次提及 | 最早未回复队首阻塞后续；不同 AI 与 Human 不受阻 |
| CT-X12 | P0 | Processing 期间重复 wait | task_in_progress，不启动第二个任务或新尝试 |
| CT-X13 | P0 | 离线时乱序消息事件、消费者并发 | 按 Sequence 补齐独立任务，任务唯一键有效，无遗漏或倒置区间 |
| CT-X14 | P0 | 接管改小/改大字符预算 | 初始历史和新尝试按新预算裁剪；已持久化任务区间不变 |
| CT-X15 | P0 | Unicode、系统/AI 消息、超长触发消息 | UTF-16 计数一致，保留所有来源与完整触发消息，元数据准确 |
| CT-X16 | P1 | 同次响应重放时服务配置变化 | 使用尝试保存的窗口和格式版本，正文/范围/字符元数据一致 |
| CT-X17 | P0 | 可靠模式未确认直接 reply | context_confirmation_required，不隐式确认或追加消息 |
| CT-X18 | P1 | 接管快照与任务上下文重叠 | 标明 contextPurpose/replayReason，同 messageId 不产生新任务 |
| CT-X19 | P0 | confirm 已提交，过原截止后重放 | 当前已确认且租约有效则成功，无超时失败或重投 |
| CT-X20 | P0 | 任务已回复后重复 confirm | 返回确认事实及 wait 下一步，不再次要求回复 |
| CT-X21 | P0 | 初始空历史、首次边界 0、确认前接管 | 空上下文合法，初始确认不生成非法区间；后续提及仍完整分配 |
| CT-X22 | P0 | 上下文构造失败的两类原因 | 数据不变量错误保留失败阻塞任务且无空尝试；短暂数据库故障回滚，不伪造永久失败 |
| CT-X23 | P0 | 确认截止已过，wait 先于超时扫描执行 | wait 共用条件超时转换后返回 retry_pending/exhausted 或新尝试，绝不重放过期窗口；扫描随后无重复副作用 |
| CT-X24 | P1 | status 查询可靠 Delivered/过期未扫描/Failed 耗尽 | reason、FailureCode、nextAction 符合模式表；查询不推进状态或确认，客户端不凭 ID 盲确认 |

## 5. 租约、失败与显式重试

| 编号 | 级别 | 场景 | 预期 |
| --- | --- | --- | --- |
| CT-L01 | P0 | now 等于租约截止，扫描尚未执行 | 读写/确认/续租已拒绝，必须 resume；不能依赖注册表判在线 |
| CT-L02 | P1 | 默认 30 秒 wait，60 秒客户端请求上限 | 跨多个 idle 持续等待，工具不超宿主时限，会话复用 |
| CT-L03 | P1 | 请求长 wait 或无效 wait 参数 | 基础租约可以小于长 wait；取 max(原租约,now+基础,wait截止+余量)，越界参数拒绝 |
| CT-L04 | P0 | 请求取消、进程静默消失 | 取消不立即离线；没有后续有效操作时租约到期，离线最终可见 |
| CT-L05 | P0 | 模型处理时间超过多个租约周期 | 独立续租有效则保持 Processing，不因处理慢自动重试 |
| CT-L06 | P1 | 同 MCP 会话 wait 与 renew 并发 | 支持续租，不误把业务串行循环当成所有请求串行 |
| CT-L07 | P0 | 旧绑定续租与接管竞争 | 不能延长新连接租约或使旧连接复活 |
| CT-L08 | P1 | status 反复查询、非法请求 | 不续租；查询本身不令离线连接恢复 |
| CT-R01 | P0 | 确认超时后再次领取 | 保留原任务，旧尝试失效；建立替代时同事务 Abandoned+ReplacementAttemptId |
| CT-R02 | P0 | 扫描器与请求重复处理同一失败 | 条件更新只生效一次，不重复计数或延期 |
| CT-R03 | P0 | 离线 100 次扫描 | 不创建替代尝试、不耗尽计数，恢复后最早任务继续 |
| CT-R04 | P0 | 连续达到 3 次自动替代投递后再次失败 | RetryExhausted=true，第四次替代不会自动发生，队列保持阻塞 |
| CT-R05 | P0 | 耗尽后 resume/takeover | 不重置周期和次数，不绕过失败队首 |
| CT-R06 | P0 | 当前 Agent 显式 retry_task | 重试周期增加、计数归零，本周期首次投递不占自动名额、之后最多自动替代 3 次；原 TaskId/幂等键不变，后续任务不越序 |
| CT-R07 | P0 | retry_task 响应丢失重放 | 同 operationId 只增加一次周期；旧周期收据不重新激活任务 |
| CT-R08 | P0 | Processing/非耗尽/跨 Agent 的 retry | 分别返回 task_in_progress/retry_not_required/拒绝，不破坏正常任务 |
| CT-R09 | P1 | 临时失败、永久失败和未知错误码 | 临时失败退避；永久失败阻塞；无任意敏感文本写入失败码 |
| CT-R10 | P1 | 服务重启发生在退避期间 | NextRetryAt 和次数持久化，重启不清零、不提前越序 |
| CT-L09 | P0 | 300 秒 wait 后并发执行 120 秒基础续租 | LeaseExpiresAtUtc 不缩短；旧续租响应乱序也不改变服务端事实 |
| CT-L10 | P0 | 续租请求在截止前排队、截止后才取得锁 | 使用锁后时间拒绝，不用请求到达时间复活租约 |
| CT-R11 | P0 | 新尝试产生后重放上一尝试失败报告 | 同码返回原决定和当前摘要，新尝试不失败；改码冲突 |
| CT-R12 | P0 | 已 Failed 的任务经历多次关闭/恢复/接管 | NextRetryAt、周期、耗尽和失败决定不被重置或不断后延 |
| CT-R13 | P1 | 非法外部 failureCode | failure_code_invalid，当前 Processing 不受影响，不误写永久失败 |
| CT-R14 | P1 | 最大自动重试为 0、退避数组较短 | 首次失败即阻塞；配置为多次重试时退避索引饱和至最后一项，不越界 |
| CT-L11 | P0 | 跨字段配置非法 | 覆盖默认 wait 超上限、wait+余量不小于租约、renew+余量不小于租约、负重试、空/非正退避数组、时间计算溢出，启动拒绝 |
| CT-L12 | P1 | 确认时限大于基础租约且独立续租有效 | 配置允许，确认时限内仍可成功；停止续租后按租约失效，不能伪造长期连接 |
| CT-L13 | P1 | join/takeover 共用限流和 resume 多维限流 | 换操作/会话不能清零来源等计数；密钥错误触发失败额度；超限前不改所有权，可靠 data 含 retryAfterSeconds |
| CT-L14 | P1 | 限流桶容量与到期清理 | 达到 RecoveryRateLimitMaximumBuckets 后拒绝新桶，到期释放；不记录原始会议码/密钥 |
| CT-R15 | P1 | 固定 FailureCode/stopReason 和未知旧原因 | 单一判定函数交叉覆盖全部原因与剩余额度：可恢复原因有额度为 false/null，无额度为 true/attempts_exhausted；永久原因无论额度均为 true/permanent_failure；耗尽保留最近原因，未知旧原因转 internal_failure 阻塞；显式重试清除停止标志，原收据决定不变 |
| CT-R16 | P1 | 退避期间 wait → retry_pending → renew → wait | 根据 nextRetryAt 调度，保持租约；到期只建立一次替代投递 |

## 6. 回复、崩溃与删除

| 编号 | 级别 | 场景 | 预期 |
| --- | --- | --- | --- |
| CT-P01 | P0 | 同任务并发相同回复 | 只追加一条消息，一组 mentions/event/deliveries，另一请求重放 |
| CT-P02 | P0 | 幂等键相同正文不同 | reply_idempotency_mismatch，不覆盖既有消息 |
| CT-P03 | P0 | 回复提交后响应丢失并 resume | 查询已完成摘要和原消息 ID，同成功尝试/正文重放原结果，不重新回复 |
| CT-P04 | P0 | 已接管的旧连接请求幂等重放 | 先拒绝身份，不能以幂等键读取旧结果或写消息 |
| CT-P05 | P0 | reply 与 takeover 并发 | 先提交回复则保留唯一消息；接管先提交则旧 reply 无任何写入 |
| CT-P06 | P0 | 消息追加后任务标记前事务失败 | 消息、任务和事件全部回滚，无半提交 |
| CT-S01 | P0 | join 提交前/后分别杀进程 | 未提交无残留；已提交 Activating/Connected 都可收敛，收据查询后发起 resume，无同名占死 |
| CT-S02 | P0 | Delivered/Processing/Failed/Replied 状态分别重启 | 未完成任务可恢复，已回复不重投；旧传输全部失效 |
| CT-S03 | P0 | 启动扫描与新 join/resume | 启动门生效，旧扫描不能把新代数断开；初始化未完成不开放 Agent 接入 |
| CT-S04 | P1 | 信号/状态通知丢失 | 数据库轮询、状态快照能收敛；任务不依赖内存信号保存 |
| CT-D01 | P0 | 删除与 join/resume/takeover/confirm/reply/renew 竞争 | 屏障后不得成功读写，旧会议码与密钥都不能恢复 |
| CT-D02 | P0 | 删除含初始投递、收据、耗尽任务和租约的会议 | 所有新增资源清理，当前指针及外键顺序正确 |
| CT-D03 | P0 | 删除中进程崩溃重启 | Deleting 不回 Active，清理可重入，无恢复扫描复活 |
| CT-D04 | P0 | 删除一个会议期间其他会议运行 | 其他会议 Human 和 AI 正常工作，无全局串行阻塞 |
| CT-D05 | P0 | 多代连接、多次替代尝试的删除 | 先清 CurrentConnectionId/CurrentAttemptId，再清 SupersededByConnectionId/ReplacementAttemptId，最后删关联行，真实 SQLite 外键检查通过 |
| CT-P07 | P0 | Replied 后 CurrentAttemptId 生命周期 | 保留成功尝试并可重放原回复，不能替换或清空；仅会议删除清理时清空 |
| CT-D06 | P0 | 传输预留/会议 gate/参与者锁/数据库锁交错 | join/resume/reply/delete/scan 各路径有界完成，取消释放持有资源，无反向等待；其他会议可继续 |

## 7. 兼容、迁移与约束

| 编号 | 级别 | 场景 | 预期 |
| --- | --- | --- | --- |
| CT-M01 | P0 | 空库迁移 | 所有表、复合外键、唯一索引和 CHECK 生效 |
| CT-M02 | P0 | 主题五数据库副本升级 | 参与者/消息/任务/幂等键/候选范围保持，不伪造客户端确认 |
| CT-M03 | P0 | 旧未完成任务首次可靠接管 | 回填初始投递、CurrentAttemptId 与周期；旧尝试失效后可重投，游标已推进也不能吞任务 |
| CT-M04 | P0 | 非法跨会议连接/尝试/指针写入 | Domain 或 SQLite 约束拒绝，数据不串会 |
| CT-M05 | P0 | 多条旧 Connected 或非法旧状态 | 明确迁移诊断，不静默选择一条覆盖事实 |
| CT-M06 | P1 | 老客户端省略 reliabilityMode | 原四工具正常闭环，既有字段/nextAction 语义兼容 |
| CT-M07 | P0 | 新 Agent 可靠接管 legacy 连接 | 合法升级；之后尝试降级返回 reliability_mode_required |
| CT-M08 | P1 | 工具发现与能力协商 | 工具/schema/版本一致；不支持能力时不冒充可恢复 |
| CT-M09 | P0 | 可靠新工具误用于 legacy 连接 | 明确 capability/模式错误，不能半执行可靠状态机 |
| CT-M10 | P1 | 敏感字段与错误输出扫描 | 密钥、会议码、绑定及正文不进入日志、网页或一般错误响应 |
| CT-M11 | P0 | 两个模式的初始状态迁移与确认 | 初始投递表是唯一事实，旧 DTO 投影一致，不残留双写确认状态 |
| CT-M12 | P1 | 可靠 retry_pending 等失败序列化 | succeeded=false，data 含准确 nextAction/时间；legacy 失败仍 Data=null；未授权不泄漏摘要 |
| CT-M13 | P0 | 激活状态唯一索引 | Activating+Connected 组合不能同时拥有两个连接；枚举升级保留旧值语义 |
| CT-M14 | P1 | tools/list、DTO、成功 kind、错误注册表一致性 | schema.properties 中可识别 reliabilityMode；逐一用真实工具 JSON 序列化验证全部映射 DTO、schema 与 kind/错误可重试性/data，外层投递元数据不进入消息 DTO |
| CT-M15 | P0 | 两种 wait 投递与初始确认分支 | legacy 仍隐式确认并最终化；可靠未显式确认不能推进游标，两个模式不可串用 finalize |
| CT-M16 | P0 | 迁移歧义或外键错误中断 | 诊断 ID 摘要，事务回滚且原库仍可读取；不任取最新连接/尝试，源确认事实只保留一份 |
| CT-M17 | P1 | 旧 legacy 连接被可靠 Agent 接管后的 wait | connection_superseded 且 Data=null，旧客户端停止；未升级离线 legacy join 仍为 agent_recovery_not_supported |
| CT-M18 | P1 | legacy 携带 waitTimeoutSeconds/taskId 新参数 | 非空合法新参数明确 reliability_mode_required，不静默忽略；提示移除参数或可靠接入，与已升级参与者的必须可靠接入提示区分；两者仍 data=null，客户端不解析提示文案分支；原输入/DTO 兼容 |
| CT-M19 | P0 | 同会议 legacy AI 与可靠 AI 并行 | 各自任务、确认、错误形状及队列独立；一方阻塞不影响另一方 |
| CT-M20 | P1 | join 新参数非法与模式错误分流 | 非法 reliabilityMode/joinMode、operationId/密钥格式、可靠模式缺少必填参数均为 agent_request_invalid；未知模式 data=null，已知可靠但未认证只给 nextAction=stop；合法参数不兼容才为 reliability_mode_required；分别验证 SDK 协议拒绝与业务适配器错误 |

## 8. 网页、参考客户端与真实模型

| 编号 | 级别 | 场景 | 预期 |
| --- | --- | --- | --- |
| CT-V01 | P1 | 在线接管 | 列表保持一个 AI，名称和历史作者不变，有接管提示 |
| CT-V02 | P1 | 确认等待、退避、耗尽、过期、恢复 | 各状态中文文案与服务端一致；presence 与 activity 不矛盾 |
| CT-V03 | P0 | 状态事件乱序/重复、刷新与重连 | revision 拒绝旧状态；快照在扫描器执行前遇已过期租约也能先收敛并增加 revision，无同版本不同状态或倒退 |
| CT-V04 | P0 | 会议切换与删除后迟到事件 | 不串会，不让已删除会议重新出现 |
| CT-V05 | P1 | 长名称、窄屏与多参与者 | 状态不遮挡操作，不新增无授权的接管/重试管理页面 |
| CT-V06 | P0 | HTTP 历史和实时消息重复 | AI 回复仍按 messageId/Sequence 去重，统一消息流 |
| CT-G01 | P1 | 参考客户端长驻留至少 10 分钟 | 跨多次 idle 及续租，两个提及依次回复后继续等待 |
| CT-G02 | P0 | 参考客户端丢 wait/confirm/reply 响应 | 按错误与状态恢复，不重复模型执行同一个有效尝试，不重复写消息 |
| CT-G03 | P1 | 新 MCP 会话 resume / 新 Agent takeover | 原恢复保留 ID/预算，新接管更换 ID/预算，旧循环停止 |
| CT-G04 | P1 | 模型实际处理超过一个租约周期 | 宿主独立续租有效，模型完成后正常提交，不伪造支持 |
| CT-G05 | P1 | 两轮内容不同的真实模型问题 | 每轮按上下文作答并继续等待；固定回复客户端不得代替 |
| CT-G06 | P1 | 重试耗尽及显式重试 | 客户端先报告阻塞，明确重试后恢复，无自动重置循环 |
| CT-G07 | P1 | 明确离开与远程关闭失败 | 成功关闭可观察离线；失败报告实际状态，不靠本地退出推断 |
| CT-V07 | P0 | Activating、Connected 与普通续租快照 | 每次可见改变有新 revision；普通续租不改变 activity.changedAt，同 revision 的 payload 一致 |
| CT-V08 | P0 | 接管/离线后到达旧无版本 presence 事件 | Agent presence 不被旧增量覆盖，Human presence 保持原流程 |
| CT-V09 | P0 | 注册表缺失但持久化补偿尚未完成 | 先收敛产生新 revision 再发布离线，不在相同 revision 下产生不同状态 |
| CT-V10 | P0 | 混合 Human/Agent 的订阅快照合并 | onlineParticipantIds 仅 Human，Agent 在线来自 agentActivities，不能互相补齐或覆盖 |
| CT-V11 | P0 | revision 全变化点与无变化点矩阵 | design §11 每个入口逐项验证；相同过期状态重复读/扫描不空转，后排入队或普通续租不误改版本 |
| CT-V12 | P1 | agentActivityChangedV1 增量兼容 | 新 DTO/快照/map/reducer 同步；旧客户端忽略新增字段，新客户端按 revision，无未声明 V2 依赖 |
| CT-G08 | P1 | 耗尽等待明确重试超过多个租约周期 | 业务循环暂停、renew 循环持续；显式 retry 后继续；主动离开才停止续租 |
| CT-G09 | P1 | 阶段 0 支持性与两种兼容运行路径 | 原 SDK 或短 wait/专用 SDK 宿主路径实际维持处理期租约才 go；都失败则 no-go，不用无绑定心跳假通过 |
| CT-G10 | P1 | 实际 RunSessionHandler 关闭路径与接入说明 | finally 清理/补偿有版本隔离；参考客户端说明接管凭据与恢复密钥区别，以及 Activating 事务提交后即使激活失败也不允许 legacy 回退（未提交不升级）；固定回复不计为真实模型验收 |

CT-G02 中客户端可通过本地保存 TaskId/DeliveryAttemptId 和已生成正文避免同一有效尝试重复生成；进程丢失本地状态后，新尝试可能再次执行模型或外部工具。本系统只保证回复消息幂等，不声称外部副作用 exactly-once。

## 9. 构建和回归基线

服务端在仓库根目录执行：

```powershell
dotnet tool restore
dotnet restore Server/RoundTableMeeting.slnx
dotnet build Server/RoundTableMeeting.slnx --configuration Debug
dotnet test Server/RoundTableMeeting.slnx --configuration Debug
```

客户端在 Client/roundTableMeeting 执行：

```powershell
pnpm install --frozen-lockfile
pnpm generate:api
pnpm test
pnpm build
```

发布验证从现有 Server/RoundTableMeeting/RoundTableMeeting.csproj 输出到隔离的 artifacts 子目录，使用独立 SQLite。验证 /mcp 工具发现、应用启动、SPA、SignalR、数据库迁移与删除恢复。HTTP OpenAPI/Kiota 与 SignalR 镜像契约分开验证，不把 MCP DTO 误当 REST 自动生成能力。

必须回归账号认证和授权、会议创建/加入/切换/删除、Human 消息发送与历史补齐、presence、乱序广播去重、事件分发器重试，以及主题五 Human @AI 正常参会路径。可靠模式仍不能让 Agent/System 消息触发主题七任务。

## 10. 完成门

- 连接恢复和接管具有不同身份/预算语义，主动在线接管符合已确认规则，旧绑定不再有权限。
- 入会响应丢失可恢复；未确认上下文不丢失；接管快照不覆盖原任务或分配边界。
- 重复确认、回复、失败报告、resume 和 retry 收据幂等，真实 SQLite 并发验证通过。
- 连接租约与模型处理解耦；自动重试有界且可恢复；耗尽失败队首不被自动跳过。
- 服务进程崩溃后未完成任务继续；已完成任务不重复；删除后全部新增资源不可恢复。
- 旧客户端兼容与可靠模式升级分别通过，UI 状态和真实模型驻留可见。
- P0/P1、发布与前置主题核心回归通过，未执行项明确列出，不以文档检查代替业务验收。
