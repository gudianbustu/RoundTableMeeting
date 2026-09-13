# 会议工作台主题测试方案

## 1. 文档定位

本文定义 004 会议工作台主题的 Domain、Application、Repository.Sqlite、Architecture、HTTP、OpenAPI/Kiota、客户端、浏览器和发布测试。实施顺序见[主题实现方案](impl.md)，服务端见[Server/impl.md](Server/impl.md)，客户端见[Client/impl.md](Client/impl.md)。

本方案只验证会议空间、参与者静态身份、会议码、创建/加入/切换/删除和消息区插槽，不验证主题四消息、SignalR、MCP、AI 任务或在线状态。

## 2. 测试目标

必须证明：

1. 会议、参与者和创建回执领域不变量稳定。
2. 多会议数据隔离，随机 GUID 会议码不可通过常规枚举获得，也不能绕过成员授权。
3. 创建、加入、重复恢复、名称唯一和删除屏障在并发下正确。
4. `IMeetingTransactionRunner` 与同一 Scoped DbContext/连接绑定，失败可回滚。
5. 未认证、非成员、普通成员和创建者的错误与权限边界稳定。
6. 认证后限流、Origin、JSON 正文、禁缓存和 Retry-After 契约正确。
7. OpenAPI/Kiota DTO、错误、字符串枚举和 200/201/202 状态一致。
8. 客户端工作台在刷新、深链接、切换、404、删除和网络失败下不串会、不显示假数据。
9. 主题二和系统框架回归保持通过。

## 3. 环境与隔离

- 每个测试使用独立临时 SQLite 数据库，不读取 `data/round-table-meeting.db`。
- IntegrationTests 使用两个或三个测试账号、独立 CookieContainer、显式 AllowedOrigins。
- 并发请求各自创建 DI scope 和 DbContext，不共享 Scoped 实例。
- 测试注入 `ManualTimeProvider`；会议码使用测试内明确构造的 Guid 或真实 `Guid.NewGuid()`，测试结束清理临时数据库。
- 浏览器使用独立 Chrome Profile 和仅绑定 `127.0.0.1` 的 CDP。
- 客户端测试使用 Vitest/jsdom/Testing Library；不整体 mock Ant Design 或公共 Kiota runtime。
- 不把会议码、密码、密钥、Cookie 或完整请求体写入测试输出、快照或日志。

## 4. 优先级和完成规则

- P0：错误授权、凭证泄露、跨会议串线、删除屏障失效、重复创建、数据丢失或构建阻断。
- P1：核心正常流程、契约、并发、恢复和客户端状态。
- P2：视觉、诊断和维护性检查。

所有 P0/P1 失败阻止主题完成。没有真实消息或 AI 实现不能作为主题三失败。

## 5. Domain 测试

项目：`Server/Tests/RoundTableMeeting.Domain.Tests`。

| 编号 | 优先级 | 场景 | 预期 |
| --- | --- | --- | --- |
| MW-D001 | P0 | 创建合法 Meeting | ID 非空，状态 Active，名称/说明规范化 |
| MW-D002 | P0 | 空白或超长会议名称 | 拒绝创建，不产生可持久化实体 |
| MW-D003 | P1 | 说明 Trim + NFC | 空说明保存为 null，长度边界正确 |
| MW-D004 | P0 | 会议码为空 Guid | Meeting 工厂拒绝 `Guid.Empty`，合法 Guid 可持久化 |
| MW-D005 | P0 | BeginDeleting | Active 只能进入 Deleting，不能恢复 |
| MW-D006 | P1 | 重复 BeginDeleting | 幂等或返回已删除状态，不产生非法第三状态 |
| MW-D007 | P0 | Human Participant | UserId 非空，显示名称 Trim + NFC 后 1 至 64 |
| MW-D008 | P0 | Agent 预留 Participant | UserId 为空约束可表达，名称规则不分类型 |
| MW-D009 | P0 | 显示名称大小写/NFC 等价 | NormalizedDisplayName 使用统一比较值 |
| MW-D010 | P1 | 显示名称比较值超 128 | 拒绝创建，不依赖 SQLite TEXT 长度 |
| MW-D011 | P0 | MeetingCreationReceipt 状态 | Guid ID 非空；Active/Deleted 转换和 MeetingId 清空规则正确 |
| MW-D012 | P1 | 创建回执内容摘要 | 只保存幂等所需安全字段，不保存名称、背景或邀请码 |

## 6. Application 测试

项目：`Server/Tests/RoundTableMeeting.Application.Tests`。使用 fake 当前用户、账号仓储、会议仓储、参与者仓储、事务 runner 和 UoW；会议仓储的 fake 同时承载创建回执查询与状态转换，不建立第二套回执事实来源。

| 编号 | 优先级 | 场景 | 预期 |
| --- | --- | --- | --- |
| MW-A001 | P0 | 匿名创建/列表/详情/预检/加入/删除 | `IsAuthenticated=false` 或 UserId 空/Empty 时统一 Unauthenticated |
| MW-A002 | P0 | 创建会议 | 读取账号昵称，原子创建 Meeting、Human Participant、Active Receipt |
| MW-A003 | P0 | 创建回执一致重放 | 返回 Replayed，不创建第二场会议，复用原邀请码 |
| MW-A004 | P0 | 创建回执内容不一致 | 返回 CreationRequestMismatch，不写新数据 |
| MW-A005 | P0 | 创建回执指向 Deleting/缺失会议 | 返回 CreationRequestConsumed，不返回旧详情或邀请码 |
| MW-A006 | P1 | Deleted 回执重放 | 返回 CreationRequestConsumed |
| MW-A007 | P0 | 会议列表 | 只返回 Active 成员会议，按 JoinedAt DESC、MeetingId ASC |
| MW-A008 | P0 | 非成员详情 | 返回 MeetingNotFound，不加载参与者或邀请码 |
| MW-A009 | P0 | 成员详情 | 返回不含会议码的详情，参与者按 JoinedAt ASC、ParticipantId ASC |
| MW-A010 | P0 | 人类成员邀请信息 | 已入会人类成员读取并返回标准小写 GUID `D` 格式邀请码，非成员视为 NotFound |
| MW-A011 | P0 | 预检有效会议码 | 返回 meetingId、名称、背景、默认昵称，不创建参与者 |
| MW-A012 | P0 | 预检无效/Deleting 会议码 | 统一 MeetingNotFound，不泄露存在性 |
| MW-A013 | P0 | 首次加入 | 事务内复查 Active，创建 Human Participant |
| MW-A014 | P0 | 重复加入同一 UserId | 返回原参与者，不修改显示名称 |
| MW-A015 | P0 | 名称已占用 | 返回 DisplayNameTaken，不注册新增 |
| MW-A016 | P1 | 并发 JoinConflict | 当前 scope 废弃；新请求最多重试一次，分别收敛为恢复身份或名称冲突 |
| MW-A017 | P0 | 删除确认名称错误 | 返回 validation_failed，不改变状态 |
| MW-A018 | P0 | 成员但非创建者删除 | 返回 CreatorRequired |
| MW-A019 | P0 | 非成员删除 | 返回 MeetingNotFound |
| MW-A020 | P0 | 创建者删除 | 原子 Active -> Deleting，返回 Accepted，安排清理 |
| MW-A021 | P0 | 加入与删除并发 | 后提交者看到不可访问，不能留下新参与者 |
| MW-A022 | P1 | 数据库异常 | 上抛安全异常，不返回会议码或内部细节 |
| MW-A023 | P1 | TimeProvider | CreatedAt/JoinedAt 使用注入 UTC 时间，取消令牌传播 |

## 7. TransactionRunner 和 Repository.Sqlite 测试

项目：`Server/Tests/RoundTableMeeting.Repository.Sqlite.Tests`。

| 编号 | 优先级 | 场景 | 预期 |
| --- | --- | --- | --- |
| MW-R001 | P0 | 空库 Migration | 创建 Meetings、MeetingParticipants、MeetingCreationReceipts 和索引 |
| MW-R002 | P0 | MeetingCode Guid 列与唯一索引 | 数据库直接保存 Guid；重复 MeetingCode 被唯一索引拒绝，不产生第二场会议 |
| MW-R003 | P0 | 创建回执主键和业务唯一键 | ID 为 Guid 主键；同 CreatorUserId + CreationRequestId 拒绝重复 |
| MW-R004 | P0 | Human UserId 过滤唯一索引 | 同会议同账号只能一条 Human 参与者 |
| MW-R005 | P0 | 名称无过滤唯一索引 | Human 与 Agent 不能占用同一规范化名称 |
| MW-R006 | P0 | Restrict 外键 | 删除前未清理参与者或回执时数据库拒绝，协调器先清理 |
| MW-R007 | P1 | SQLite 长度越界 | Domain/Application 拒绝，不能依赖 HasMaxLength |
| MW-R008 | P0 | Runner 同一 DbContext/连接 | runner、仓储、UoW 使用同实例和同事务 |
| MW-R009 | P0 | Runner 提交 | Application 委托不 Commit；runner 在委托成功后只调用一次 CommitAsync，并只提交一次 EF IDbContextTransaction |
| MW-R010 | P0 | Runner 回滚 | 委托抛异常、取消或 SaveChanges 失败时全部回滚 |
| MW-R011 | P0 | Runner 嵌套 | 明确编程错误，不开启第二事务 |
| MW-R012 | P1 | BEGIN IMMEDIATE 写锁 | 加入/删除串行化，后到者重新读取状态并失败 |
| MW-R013 | P0 | Deleting 数据库原子清理 | 参与者删除、回执 Deleted 转换和 Meeting 删除在同一事务全部提交或全部回滚；重复执行可收敛 |
| MW-R014 | P1 | Deleted 回执过期清理 | ExpiresAtUtc 到期删除，Active 回执不被清理 |
| MW-R015 | P1 | 查询 NoTracking | 列表/详情/参与者投影不污染跟踪状态 |
| MW-R016 | P0 | 枚举与 CHECK 约束 | Lifecycle/Participant/Receipt 状态按字符串保存；非法状态或 ParticipantType/UserId、ReceiptState/MeetingId 组合被 SQLite 拒绝 |
| MW-R017 | P0 | 唯一约束异常转换 | 仅参与者账号/名称唯一冲突转为 MeetingJoinConflictException；其他 DbUpdateException 原样上抛 |

## 8. Architecture 和 DI 测试

项目：`RoundTableMeeting.ArchitectureTests` 与 IntegrationTests。

| 编号 | 优先级 | 场景 | 预期 |
| --- | --- | --- | --- |
| MW-C001 | P0 | Application 引用扫描 | 不引用 Web、EF Core、Repository.Sqlite、IConfiguration 或 IOptions |
| MW-C002 | P0 | Abstraction 契约扫描 | 不暴露 DbContext、SqliteConnection、领域实体或密钥类型 |
| MW-C003 | P0 | Runner 契约扫描 | 只使用 Func<CancellationToken, Task<TResult>>，不引用 EF 类型 |
| MW-C004 | P0 | DI scope | Runner、会议仓储、UoW、DbContext 同 scope；TimeProvider singleton |
| MW-C005 | P1 | 自动注册 | 只扫描 Application 和 Repository.Sqlite，不重复注册会议服务 |
| MW-C006 | P0 | 消息/AI 不提前出现 | 主题三程序集无 Message、SignalR、MCP、AgentTask 实现 |
| MW-C007 | P1 | Sensitive headers | 会议 API 使用统一 `SensitiveResponseHeadersMiddleware`，不保留重复 auth-only 规则 |
| MW-C008 | P1 | 状态枚举 | MeetingOperationStatus 包含 Accepted/Mismatch/Consumed；Controller 不按文案分支 |

## 9. HTTP IntegrationTests

使用 `WebApplicationFactory`、真实 Cookie、临时 SQLite 和 `ManualTimeProvider`。

| 编号 | 优先级 | 场景 | 预期 |
| --- | --- | --- | --- |
| MW-H001 | P0 | 未登录访问会议 API | `401 unauthenticated`，不查询会议码、不消耗限流 |
| MW-H002 | P0 | Origin 缺失/null/外部 | `403 origin_not_allowed`，Application 不执行 |
| MW-H003 | P0 | 非 JSON 创建/预检/加入/删除 | `415 unsupported_media_type` |
| MW-H004 | P0 | 无正文邀请信息 | 允许 Origin，不要求 Content-Type |
| MW-H005 | P0 | 认证后限流 | 正常请求计数，超限 `429` 并含 `Retry-After` |
| MW-H006A | P0 | 限流执行阶段 | Resource filter 在模型绑定和 Application 之前拒绝超限请求；无效大正文不绕过配额 |
| MW-H006 | P0 | 未认证与限流顺序 | 未认证总是 401，不消耗账号/来源地址配额 |
| MW-H007 | P1 | 限流重启 | 单实例计数清零，Options 配置重新校验 |
| MW-H007B | P1 | 限流来源地址 | 可信代理配置下使用处理后的 RemoteIpAddress；非可信转发头被忽略；地址为空时仅账号维度计数 |
| MW-H008 | P0 | 创建会议 | `201`, Location、详情和一次邀请码；响应 no-store |
| MW-H009 | P0 | 创建网络重试 | 相同 CreationRequestId 返回 `200`，不重复创建 |
| MW-H010 | P0 | 创建 mismatch/consumed | `409` 固定 code，不返回旧邀请码 |
| MW-H011 | P0 | 普通成员详情 | `200` 但无 meetingCode |
| MW-H012 | P0 | 普通成员邀请信息 | `200 MeetingInvitationDto`，与创建者会议码一致且 no-store |
| MW-H013 | P0 | 人类成员邀请信息 | `200 MeetingInvitationDto`，含会议码且 no-store |
| MW-H014 | P0 | 非成员详情/删除 | `404 meeting_not_found` |
| MW-H015 | P0 | 成员非创建者删除 | `403 meeting_creator_required` |
| MW-H016 | P0 | 预检成功 | 返回 meetingId、名称、背景、默认名称，无参与者和会议码 |
| MW-H017 | P0 | 首次加入 | `200 MeetingDetailsDto`，列表新增，名称规范化 |
| MW-H018 | P0 | 重复加入 | `200` 原参与者，显示名称不变 |
| MW-H019 | P0 | 同名并发加入 | 一个成功，另一个 JoinConflict/DisplayNameTaken，最终只有一个名称 |
| MW-H020 | P0 | 创建者删除 | `202`, 后续列表/详情/预检/加入均不可访问 |
| MW-H021 | P1 | 删除重入 | 已屏障或已删除统一 404，不返回第二个成功状态 |
| MW-H022 | P0 | Deleting 服务重启 | 重启后恢复器继续外部幂等步骤和数据库原子清理，会议不恢复 Active |
| MW-H023 | P1 | 过期 Deleted 回执 | 时间推进超过 30 天后回执清理，会议数据不复活 |
| MW-H024 | P0 | 响应缓存头 | 所有会议成功和错误响应含 no-store/no-cache |
| MW-H025 | P0 | 枚举 JSON | ParticipantDto.type 为 `human`/`agent` 字符串，拒绝整数输入 |
| MW-H026 | P1 | 时间 | CreatedAt/JoinedAt 为 UTC DateTimeOffset，使用 ManualTimeProvider |
| MW-H026A | P0 | GUID 会议码输入契约 | Trim 首尾空白后严格接受标准 `D` 格式且大小写不敏感；缺失连字符、内部空白、大括号、其他 Guid 格式或非法值统一拒绝；响应统一为小写 `D` 格式 |
| MW-H027 | P0 | 敏感日志扫描 | 不含会议码、Cookie、完整会议名称/背景或请求体 |
| MW-H028 | P1 | 未知会议路由 | Guid 不存在或非成员返回 ProblemDetails 404，不进入 SPA HTML |

## 10. OpenAPI 和 Kiota 测试

| 编号 | 优先级 | 场景 | 预期 |
| --- | --- | --- | --- |
| MW-K001 | P0 | `pnpm generate:api` | 生成会议列表、创建、详情、邀请、预检、加入、删除请求器 |
| MW-K002 | P0 | 创建响应 | operation 接受 `201` 和 `200` 两种成功 DTO |
| MW-K003 | P0 | 错误映射 | 400/401/403/404/409/415/429/500 code 可识别 |
| MW-K004 | P1 | 429 header | Retry-After 出现在 OpenAPI 与生成响应 headers |
| MW-K005A | P1 | OpenAPI transformer 范围 | 只有预检/加入 429 声明 Retry-After，其他 operation 不被污染 |
| MW-K005 | P0 | ParticipantType | Kiota 类型为 `human | agent`，不为数字枚举 |
| MW-K006 | P1 | no-store 契约 | OpenAPI operation 和真实响应都不生成缓存示例 |
| MW-K007 | P1 | 生成目录 | UTF-8 无 BOM、CRLF、无绝对开发地址和诊断文件 |

## 11. 客户端模块和组件 Vitest

测试与 `features/meetingWorkspace` colocate。

| 编号 | 优先级 | 场景 | 预期 |
| --- | --- | --- | --- |
| MW-V001 | P0 | 首次 `/` 加载空列表 | 显示创建/加入入口，无模拟会议 |
| MW-V002 | P0 | 首次 `/` 有会议 | 自动进入服务端第一条会议 |
| MW-V003 | P0 | 详情 404 | 通过一次性 Router state 回 `/`，页面消费并清除 state，显示页面级提示且不自动进入其他会议 |
| MW-V004 | P0 | 删除成功 202 | 移除列表/缓存/邀请码，回 `/` 无选择 |
| MW-V005 | P1 | 删除返回 404 | 按已不可访问处理，清理本地详情/邀请码并回 `/` |
| MW-V006 | P0 | 快速切换会议 | 旧响应不能覆盖新 meetingId，不能串参与者 |
| MW-V007 | P1 | 焦点/手动刷新 | 只刷新当前详情，不建立实时订阅 |
| MW-V008 | P0 | 邀请信息显式加载 | 普通成员不显示按钮，创建者按需加载 |
| MW-V009 | P1 | 隐藏/切换/卸载邀请码 | 内存状态立即清除，不写存储 |
| MW-V010 | P0 | 创建 201 | 关闭对话框、刷新列表、导航新会议、显示邀请码 |
| MW-V011 | P0 | 创建重放 200 | 同样视为成功，不重复创建 |
| MW-V012 | P0 | 创建 mismatch/consumed | 停止提交、刷新列表、提示检查结果，不换 ID 重提 |
| MW-V013 | P0 | 创建输入编辑后重试 | 复用原 ID，不产生第二场隐式会议 |
| MW-V014 | P0 | GUID 会议码输入 | 只 Trim 首尾空白；标准 `D` 格式大小写均可并统一小写展示；内部空白、缺失连字符、大括号和其他格式不自动修复 |
| MW-V015 | P0 | 预检成功 | 显示名称/背景/默认昵称，不显示参与者或会议码 |
| MW-V016 | P0 | alreadyJoined | 使用 preview.meetingId 直接打开，名称不可修改 |
| MW-V017 | P0 | display_name_taken | 保留输入，定位字段，不误报网络错误 |
| MW-V018 | P1 | JoinConflict | 自动重试一次，第二次失败停止重试 |
| MW-V019 | P1 | Retry-After | 从会议 feature 错误元数据 retryAfterSeconds 显示可再次尝试时间，不修改共享 ApiError、不显示剩余次数 |
| MW-V020 | P0 | 删除确认 | 页面挂载对话框，完整名称、Trim+NFC、Ordinal 大小写敏感 |
| MW-V021 | P1 | 删除后果提示 | 列出参与者记录和会议码失效，保留后续消息/AI/连接扩展位 |
| MW-V022 | P0 | 普通成员界面 | 渲染邀请码与邀请入口，不渲染删除操作 |
| MW-V023 | P1 | `human | agent` 渲染 | 文字类型标记，不只依赖颜色 |
| MW-V024 | P1 | 网络错误 | 保留当前确认详情，显示重试，不显示假数据 |
| MW-V025 | P1 | 移动端关键断点 | 导航折叠、按钮和长名称不溢出 |
| MW-V026 | P0 | sibling route remount | `/meetings/:id -> /` 页面 remount 后仍保留 suppress 语义；刷新后标记已清除，不长期抑制自动选择 |

## 12. 浏览器验收

开发使用 Vite 5173 + 服务端 API 代理；每个场景使用真实服务端，不使用 mock 数据。

| 编号 | 优先级 | 场景 | 预期 |
| --- | --- | --- | --- |
| MW-B001 | P0 | 账号 A 登录后进入 `/` | 会议列表和空状态来自真实 API |
| MW-B002 | P0 | A 创建会议 | 立即进入工作台，名称、参与者和邀请码可见 |
| MW-B003 | P0 | A 复制邀请码 | Clipboard 成功或失败均有明确反馈，码不进入 URL |
| MW-B004 | P0 | 账号 B 预检并加入 | 预览正确，B 可改显示名称并进入会议 |
| MW-B005 | P0 | A 刷新当前会议 | 参与者列表出现 B；无实时连接也可手动更新 |
| MW-B006 | P0 | A/B 切换多个会议 | 各自列表、详情和参与者不串线 |
| MW-B007 | P0 | B 查看详情 | B 看不到邀请码和删除入口 |
| MW-B008 | P0 | A 重新打开会议 | 通过列表/深链接恢复成员身份，不需重新输入会议码 |
| MW-B009 | P0 | A 删除会议 | 输入完整名称确认，返回无选择工作台，列表移除 |
| MW-B010 | P0 | B 仍在旧会议页面 | 下次刷新/操作收到不可访问提示，回无选择，不自动跳其他会议 |
| MW-B011 | P1 | 服务端重启后 Deleting | 会议不恢复，后台继续清理 |
| MW-B012 | P1 | 宽屏/窄屏截图 | 导航、头部、参与者和空插槽无重叠溢出 |
| MW-B013 | P0 | Console/Network 扫描 | 无未处理异常、无会议码 URL、请求头、日志或缓存 |

## 13. 构建、发布和回归

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
| MW-BLD001 | P0 | 服务端全量测试 | 002/003 既有测试和 004 新增测试全部通过 |
| MW-BLD002 | P0 | 客户端测试与构建 | Vitest、tsc、Vite 全部成功 |
| MW-BLD003 | P0 | Kiota 生成 | 服务启动后生成成功，失败不破坏旧目录 |
| MW-BLD004 | P0 | 发布构建 | 发布目录包含客户端产物和服务端会议能力 |
| MW-BLD005 | P1 | 发布 SPA 路由 | `/`、`/meetings/{id}` 刷新返回应用，未知 `/api` 返回 JSON 404 |
| MW-BLD006 | P1 | 旧主题回归 | 系统状态、认证、Origin、ProblemDetails、Scalar/OpenAPI 继续通过 |
| MW-BLD007 | P1 | 主题范围扫描 | 无消息、SignalR、MCP、AI 任务、模拟会议或会议码秘密泄露 |

## 14. 完成门

- 所有 P0/P1 用例通过。
- 真实浏览器完成创建、加入、切换、刷新、邀请码、删除和恢复路径。
- 不同会议隔离，删除屏障和回执幂等行为正确。
- 002/003 回归、OpenAPI/Kiota、构建和发布通过。
- 主题三没有提前交付消息、实时或 AI 功能。
