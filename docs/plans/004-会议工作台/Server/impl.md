# 会议工作台服务端实现方案

## 1. 文档定位

本文将[服务端设计](design.md)具体化为项目文件、包与配置、EF Core 模型、Migration、会议码生成与解析、事务 runner、Controller、响应头、限流和删除恢复。主题阶段顺序见[主题实现方案](../impl.md)，会议用例内部实现见[会议工作台模块实现方案](Modules/MeetingWorkspace/impl.md)，测试编号见[测试方案](../testPlan.md)。

本文不实现消息、SignalR、MCP、Agent 连接、AI 任务或未读状态。

## 2. 项目和引用

沿用 002 建立的五个生产项目，不新增横向 Infrastructure 项目：

| 项目 | 本主题新增内容 |
| --- | --- |
| `RoundTableMeeting.Domain` | Meeting、MeetingParticipant、MeetingCreationReceipt、状态枚举、仓储契约、名称/会议码规则 |
| `RoundTableMeeting.Abstraction` | 会议 DTO、结果类型、应用服务、访问守卫、事务 runner |
| `RoundTableMeeting.Application` | MeetingWorkspaceService、MeetingAccessGuard、事务用例编排 |
| `RoundTableMeeting.Repository.Sqlite` | DbSet、配置、Migration、会议/参与者/回执仓储、同连接事务 runner |
| `RoundTableMeeting` | MeetingsController、限流 filter、敏感响应头中间件、删除恢复 HostedService、组合根注册 |

不修改通用 `IUnitOfWork` 接口，不把 EF Core、SQLite 或 ASP.NET Core 类型放入 Domain/Application/Abstraction 契约。

## 3. 文件落点

~~~text
Server/RoundTableMeeting.Domain/
  Entities/Meeting.cs
  Entities/MeetingParticipant.cs
  Entities/MeetingCreationReceipt.cs
  Enums/MeetingLifecycleState.cs
  Enums/ParticipantType.cs
  Exceptions/MeetingJoinConflictException.cs
  Repositories/IMeetingRepository.cs
  Repositories/IMeetingParticipantRepository.cs

Server/RoundTableMeeting.Abstraction/
  Contracts/Meetings/CreateMeetingRequest.cs
  Contracts/Meetings/CreateMeetingResultDto.cs
  Contracts/Meetings/DeleteMeetingRequest.cs
  Contracts/Meetings/PreviewJoinRequest.cs
  Contracts/Meetings/JoinMeetingRequest.cs
  Contracts/Meetings/MeetingDetailsDto.cs
  Contracts/Meetings/MeetingInvitationDto.cs
  Contracts/Meetings/MeetingListItemDto.cs
  Contracts/Meetings/ParticipantDto.cs
  Contracts/Meetings/JoinPreviewDto.cs
  Contracts/Meetings/MeetingOperationResult.cs
  Enums/MeetingOperationStatus.cs
  Enums/ParticipantKind.cs
  Services/IMeetingWorkspaceService.cs
  Services/IMeetingAccessGuard.cs
  Services/IMeetingTransactionRunner.cs

Server/RoundTableMeeting.Application/Meetings/
  MeetingWorkspaceService.cs
  MeetingAccessGuard.cs

Server/RoundTableMeeting.Repository.Sqlite/
  Database/Configurations/MeetingConfiguration.cs
  Database/Configurations/MeetingParticipantConfiguration.cs
  Database/Configurations/MeetingCreationReceiptConfiguration.cs
  Repositories/MeetingRepository.cs
  Repositories/MeetingParticipantRepository.cs
  Transactions/MeetingTransactionRunner.cs
  Migrations/<timestamp>_AddMeetings.cs

Server/RoundTableMeeting/
  Controllers/MeetingsController.cs
  Meetings/MeetingJoinRateLimitOptions.cs
  Meetings/MeetingJoinRateLimiter.cs
  Meetings/MeetingJoinRateLimitFilter.cs
  Meetings/MeetingDeletionSignal.cs
  Meetings/MeetingDeletionRecoveryService.cs
  OpenApi/MeetingOpenApiOperationTransformer.cs
  Authentication/NoStoreResponseAttribute.cs
  Authentication/SensitiveResponseHeadersMiddleware.cs
~~~

文件命名和目录作为实施基线。`NoStoreResponseAttribute` 与 `SensitiveResponseHeadersMiddleware` 已在 003 用户管理实现文档中规划但当前源码尚未落地；004 实施时若 003 增量尚未完成，则按同一文件名和契约落地一次，并同步替换旧 `AuthenticationResponseHeadersMiddleware`，不得创建第二套同名或同职责类型。实现过程中如需合并其他技术辅助类，只能在同一职责目录内合并，不得把领域规则移入 Controller 或把会议状态放入认证模块。

## 4. 配置

固定配置节：

~~~json
{
  "MeetingJoinRateLimit": {
    "WindowSeconds": 300,
    "PerUserLimit": 30,
    "PerSourceLimit": 100
  }
}
~~~

- `MeetingJoinRateLimit` 的窗口和次数必须为正数，Options 使用 `ValidateOnStart`。
- 会议码不属于密码或密钥，使用 `Guid.NewGuid()` 生成并以 Guid 列明文持久化；不配置会议码专用保护设施或秘密。
- 会议码唯一性由 `Meetings.MeetingCode` 唯一索引保证；随机碰撞按内部错误失败并废弃当前事务作用域，不在失败的 DbContext 上重试。

## 5. EF Core 模型和 Migration

### 5.1 Meeting

`Meetings` 表字段：`ID`、`Name`、`Description`、`CreatorUserId`、`MeetingCode`、`LifecycleState`、`CreatedAtUtc`。

- `MeetingCode` 映射为 Guid 列并建立唯一索引，数据库直接保存会议码值。
- `LifecycleState` 使用 EF Core `HasConversion<string>()` 存储为 `Active | Deleting`，列长 16，并增加只允许两个值的 SQLite CHECK 约束；它不进入成功 DTO。
- 会议名称和说明的长度由 Domain/Application 校验，不能依赖 SQLite `TEXT` 长度。
- `CreatorUserId` 使用 Restrict 外键指向 `UserAccounts`。

### 5.2 MeetingParticipants

字段：`ID`、`MeetingId`、`UserId` nullable、`ParticipantType`、`DisplayName`、`NormalizedDisplayName`、`JoinedAtUtc`。

- `MeetingId` 使用 Restrict 外键；删除协调器负责显式清理。
- `UserId` 使用可空 Restrict 外键；Human 必须非空，未来 Agent 必须为空。
- `ParticipantType` 使用 `HasConversion<string>()` 存储为 `Human | Agent`，并增加枚举值与 UserId 空值关系的 CHECK 约束。
- `(MeetingId, UserId)` 建立 `UserId IS NOT NULL` 过滤唯一索引。
- `(MeetingId, NormalizedDisplayName)` 建立无过滤唯一索引，Human/Agent 共享名称空间。
- 64/128 长度和 Human/Agent 空值检查在 Domain/Application 及可用的 SQLite CHECK 约束中双重表达。

### 5.3 MeetingCreationReceipts

`MeetingCreationReceipt` 继承 `BaseEntity`。字段：`ID`、`CreatorUserId`、`CreationRequestId`、`MeetingId` nullable、`State`、`CreatedAtUtc`、`ExpiresAtUtc` nullable。

- `ID` 为 Guid 主键；`(CreatorUserId, CreationRequestId)` 建立业务唯一索引。
- `CreatorUserId` 使用 Restrict 外键。
- Active 回执的 MeetingId 指向 Active Meeting；删除清理先将回执改为 Deleted 并清空 MeetingId，再删除会议。
- Deleted 回执不保存会议名称、背景或邀请码，ExpiresAtUtc 为 Deleted 时间加 30 天。
- Receipt State 使用 `HasConversion<string>()` 存储为 `Active | Deleted`，并增加 Active 必须有 MeetingId、Deleted 必须无 MeetingId 的 CHECK 约束。

Migration 命名固定为 `AddMeetings`，显式指定 Repository.Sqlite 项目和 Web startup project。Migration 不写种子会议或邀请码。

## 6. 会议码生成与解析

会议创建事务内直接调用 `Guid.NewGuid()` 生成 `MeetingCode`，保存到 `Meetings.MeetingCode`。邀请信息用例仅在确认当前用户为创建者后读取该 Guid，并用标准 `D` 格式字符串返回；普通成员详情永不包含该字段。

预检和加入请求只接受字符串输入：先 Trim 首尾空白，再使用 `Guid.TryParseExact(value, "D", out var meetingCode)` 或等价严格解析；解析大小写不敏感，缺少连字符、内部空白、其他 Guid 格式和非法字符均拒绝。格式化统一使用小写 `D`。会议码不进入 URL、查询参数、日志、指标、异常、客户端持久化或限流 key。

## 7. 事务 runner

`MeetingTransactionRunner` 必须：

- 注册为 Scoped，并与 `RoundTableMeetingDbContext`、会议仓储、`IUnitOfWork` 共享同一 scope。
- 调用 `dbContext.Database.OpenConnectionAsync(cancellationToken)`，取得同一 `SqliteConnection`，执行 `BeginTransaction(deferred: false)` 建立 IMMEDIATE 写事务。
- 通过 `dbContext.Database.UseTransaction(transaction)` 把该 `SqliteTransaction` 绑定为 EF Core 当前事务；禁止另开独立连接或让 `CommitAsync` 自动创建第二事务。Runner 持有 `IDbContextTransaction` 的提交和释放责任，不能同时调用底层 `SqliteTransaction.Commit()` 与 EF 包装事务的 Commit；底层连接由 runner 在完成解绑后关闭。
- 调用 `Func<CancellationToken, Task<TResult>>`，传递调用方取消令牌。
- 检测活动事务或嵌套 runner 并抛出明确编程错误。
- Application 委托只查询和注册变化，不调用 `CommitAsync`；委托成功后由 runner 调用本次事务唯一一次 `IUnitOfWork.CommitAsync`，再提交 EF `IDbContextTransaction` 一次，随后解除 EF 事务绑定并按所有权关闭连接，才返回 TResult。
- 任何查询、保存、取消或提交失败都回滚并要求调用方废弃当前 scope。

Runner 捕获 `DbUpdateException` 后只识别会议参与者两个已冻结唯一约束；命中时转换为 Domain `MeetingJoinConflictException`，其他约束或数据库错误原样上抛。异常不包含索引名、SQL、会议码或用户输入。Application 将该异常转换为 JoinConflict；Web 不直接解析 SQLite 异常。

Repository.Sqlite 测试必须验证 runner、仓储、UoW 和 DbContext 的引用相同；不能只验证数据库最终结果。

## 8. Web 注册和管道

组合根注册顺序：

1. `MeetingJoinRateLimit`、`TimeProvider.System`。
2. Meeting/Participant 仓储、`IMeetingTransactionRunner`、Singleton `MeetingJoinRateLimiter`、Singleton `MeetingDeletionSignal` 和删除恢复器；创建回执查询与写入归入 `IMeetingRepository`。
3. MVC JSON 字符串枚举转换器：`JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: false)`。Domain `ParticipantType` 在 Application 映射为 Abstraction `ParticipantKind`，线上值固定为 `human | agent`。
4. `SensitiveResponseHeadersMiddleware`，替换主题二的 auth-only 响应头中间件，覆盖 `/api/auth/*`、`/api/users*`、`/api/meetings*`。
5. Scoped `MeetingJoinRateLimitFilter`（实现 `IAsyncResourceFilter`）、`MeetingOpenApiOperationTransformer` 和 `OriginValidationMiddleware`。

管道固定为：异常处理、Serilog、HTTPS、`UseRouting`、默认文件/静态文件、敏感响应头中间件、Origin 校验、`UseAuthentication`、`UseAuthorization`、OpenAPI/Scalar、Controller 和 API/SPA fallback。

## 9. Controller 和端点

`MeetingsController` 使用 `[ApiController]`、`[Route("api/meetings")]` 和 `[Authorize]`。成功/错误响应固定：

| 方法 | 路径 | 成功 | Origin 正文 |
| --- | --- | --- | --- |
| GET | `/api/meetings` | 200 列表 | 无 |
| POST | `/api/meetings` | 201 首次、200 重放 | `RequireBrowserOrigin(true)` |
| GET | `/api/meetings/{meetingId}` | 200 详情 | 无 |
| POST | `/api/meetings/{meetingId}/invitation` | 200 邀请信息 | `RequireBrowserOrigin(false)` |
| POST | `/api/meetings/join/preview` | 200 预检 | `RequireBrowserOrigin(true)` |
| POST | `/api/meetings/join` | 200 加入/恢复 | `RequireBrowserOrigin(true)` |
| POST | `/api/meetings/{meetingId}/deletion` | 202 已建立屏障 | `RequireBrowserOrigin(true)` |

所有 Controller action 显式声明 ProblemDetails 400/401/403/404/409/415/429/500；创建 action 额外声明 `creation_request_mismatch`、`creation_request_consumed`。`MeetingOpenApiOperationTransformer` 只为两个限流 action 的 429 响应声明 `Retry-After` 字符串响应头，并保持同一 ProblemDetails schema。

## 10. 限流和响应头

`MeetingJoinRateLimitFilter` 实现 `IAsyncResourceFilter`，只挂载预检和确认加入两个 action，确保在模型绑定和 Controller/Application 之前执行；内部调用 Singleton `MeetingJoinRateLimiter`：

- Origin 校验先行；认证成功后 filter 执行。
- 未认证请求得到 401，不消耗配额。
- `MeetingJoinRateLimiter` 使用 `ConcurrentDictionary<string, FixedWindowCounter>` 分别保存 `user:{UserId}` 与 `source:{address}`；每个 counter 内部使用锁或等价原子操作同步 windowStart/count，惰性移除过期 entry，避免并发丢计数和无界增长。
- 来源地址优先使用 `HttpContext.Connection.RemoteIpAddress`；只有请求确实来自配置的 KnownProxies/KnownNetworks 且 `ForwardedHeadersMiddleware` 已先执行时，才使用处理后的客户端地址。地址缺失时只执行账号维度限流，不创建空地址共享桶。
- 任一维度超限返回 429、稳定 code 和 `Retry-After`；值为当前窗口剩余秒数向上取整，最小 1。不查会议码。
- 服务重启计数清零，首版不引入分布式缓存。

`SensitiveResponseHeadersMiddleware` 在 `UseRouting` 后读取 `[NoStoreResponse]` metadata，在 `OnStarting` 追加 `Cache-Control: no-store` 和 `Pragma: no-cache`。会议 Controller 和后续认证/用户 Controller 都标记该 metadata。

## 11. 删除恢复

`MeetingDeletionRecoveryService` 为 HostedService；`MeetingDeletionSignal` 使用容量 1 的 Channel 或等价 AsyncAutoResetEvent 合并唤醒，不丢失持久化状态：

- 启动后立即扫描 Deleting 会议；平时等待周期 timer 或 `MeetingDeletionSignal`，不阻塞 Active 请求。删除 Application 在屏障提交成功后调用 signal；即使进程在 signal 前退出，重启扫描仍能恢复。
- 每次清理创建独立 DI scope。
- 先执行后续主题登记的连接/任务终止等外部清理步骤；它们必须幂等，失败时保持 Deleting 并稍后重试。
- 外部步骤完成后，在一个新的 `IMeetingTransactionRunner` 短事务中原子删除 MeetingParticipants、把 Active 回执改为 Deleted/清空 MeetingId/设置 ExpiresAtUtc，并删除 Meeting。
- 本主题三张表的数据库收敛要么全部提交，要么全部回滚；重复执行时已不存在的会议视为完成。
- 同一 HostedService 周期清理已过 `ExpiresAtUtc` 的 Deleted 回执。
- 失败只记录 meetingId、步骤、异常类型和 traceId，不记录会议码或用户内容。

## 12. 命令和验证

~~~powershell
dotnet tool restore
dotnet restore Server/RoundTableMeeting.slnx
dotnet tool run dotnet-ef migrations add AddMeetings --project Server/RoundTableMeeting.Repository.Sqlite/RoundTableMeeting.Repository.Sqlite.csproj --startup-project Server/RoundTableMeeting/RoundTableMeeting.csproj --context RoundTableMeetingDbContext --output-dir Migrations
dotnet build Server/RoundTableMeeting.slnx --configuration Debug
dotnet test Server/RoundTableMeeting.slnx --configuration Debug
~~~

实现完成前必须按[测试方案](../testPlan.md)执行 `MW-D*`、`MW-A*`、`MW-R*`、`MW-C*`、`MW-H*`、`MW-K*` 和发布/浏览器场景。
