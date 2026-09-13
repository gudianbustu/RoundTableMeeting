# 会议工作台服务端设计

## 1. 文档定位

本文定义主题三在服务端的项目分层、领域实体、持久化、会议码、应用用例、HTTP API、授权和删除访问屏障。主题边界以[主题总体设计](../design.md)为准；会议工作台模块的内部契约以[模块设计](Modules/MeetingWorkspace/design.md)为准。

本文只设计人类会议空间和静态参与关系，不实现消息、实时通信、MCP 或 AI 任务。

## 2. 分层职责

### 2.1 Domain

`RoundTableMeeting.Domain` 定义 `Meeting`、`MeetingParticipant` 和会议状态值。`Meeting` 是会议聚合根，拥有会议名称、背景、创建者、会议码和删除状态；`MeetingParticipant` 是会议从属实体，表示一个账号在该会议中的稳定人类参与身份。

领域对象负责名称和输入值的规范化、Active/Deleting 状态转换以及不变量检查，不依赖 ASP.NET Core、EF Core、Cookie 或 HTTP。会议码不是密码或密钥，领域对象允许保存用于持久化的 Guid 值；日志和错误边界仍不得输出该值。

### 2.2 Abstraction

`RoundTableMeeting.Abstraction` 提供会议列表、详情、创建、加入预检、加入确认、删除和当前成员查询所需的 DTO、枚举和应用服务接口。契约只使用 Guid、字符串、时间和只读集合，不暴露领域实体、EF 查询类型或非成员无权查看的会议码。

### 2.3 Application

Application 负责：

- 从当前用户上下文取得 `UserId`；
- 编排创建会议和创建者参与者；
- 校验会议码、会议状态和成员权限；
- 执行名称规范化和重复加入幂等；
- 生成会议码并持久化 Meeting；
- 将实体映射为工作台读模型；
- 发起删除状态转换和本主题数据清理。

Application 不读取 `HttpContext`，不直接使用 DbContext，不把会议码写入日志。

### 2.4 Repository.Sqlite

Repository 负责会议和参与者的 EF Core 映射、Migration、专用查询、唯一索引、条件状态更新和事务实现。普通读取返回 DTO 投影或未跟踪实体；仓储不判断当前用户权限，不决定 HTTP 状态码，不生成会议码。

### 2.5 Web 宿主

Web 负责 Controller、请求绑定、认证授权元数据、Origin 校验、认证后的加入限流、ProblemDetails 映射、敏感响应头和删除后台恢复触发。Controller 不自行拼接会议列表，也不从请求参数推断创建者。

## 3. 领域模型

### 3.1 Meeting

概念属性：

- `ID`：继承 `BaseEntity` 的 `Guid`。
- `Name`：规范化后 1 至 128 个字符。
- `Description`：可选，规范化后最多 2000 个字符。
- `CreatorUserId`：创建账号的 `Guid`。
- `MeetingCode`：创建时生成的随机 Guid，数据库明文持久化并建立唯一索引。
- `LifecycleState`：`Active` 或 `Deleting`。
- `CreatedAtUtc`：Application 通过注入的 `TimeProvider.GetUtcNow()` 取得的 `DateTimeOffset`。

`Meeting` 只允许由工厂创建 Active 会议；`BeginDeleting()` 是唯一进入 Deleting 的领域操作，重复调用保持幂等或返回已删除状态，由应用服务统一处理。

Web 组合根固定注册 `TimeProvider.System` 单例；Application 只依赖 `TimeProvider` 抽象。测试通过替换该单例提供可控时间，不在领域实体中直接调用 `DateTimeOffset.UtcNow`。

### 3.2 MeetingParticipant

概念属性：

- `ID`：稳定参与者标识。
- `MeetingId`：所属会议。
- `UserId`：人类账号标识；数据库字段为可空以兼容后续 Agent，但 Human 类型必须非空。
- `ParticipantType`：本主题固定 `Human`。
- `DisplayName`：保存的会议内名称，1 至 64 个字符。
- `NormalizedDisplayName`：去除首尾空格、Unicode NFC 后转不区分大小写比较值。
- `JoinedAtUtc`：Application 通过同一 `TimeProvider.GetUtcNow()` 取得的 `DateTimeOffset`；创建者与会议创建使用同一个时间值。

参与者没有退出、禁言、踢出或名称修改状态。后续 Agent 参与者必须复用相同名称唯一索引和 `ParticipantType` 契约。

### 3.3 聚合与外键

主题三使用 Meeting 作为删除边界。参与者以 `MeetingId` 关联会议，不把会议实体加载图作为跨层 DTO。会议从属数据由删除协调器显式、幂等清理，外键使用 Restrict 阻止遗漏清理步骤被静默掩盖；后续消息、连接和任务必须在各自主题设计中登记删除策略并接入统一删除协调器。

## 4. 规范化规则

### 4.1 会议名称和说明

会议名称和说明先 Trim，再做 Unicode NFC；会议名称为空拒绝，说明规范化后为空按 null 保存。名称不要求平台范围唯一，不同会议可以同名。

### 4.2 会议内显示名称

显示名称先 Trim，再做 Unicode NFC；规范化后为空拒绝，规范化后的长度上限为 64。比较值使用 `ToUpperInvariant()`，领域工厂和 Application 必须再次验证比较值不超过 128；数据库列长度 128 只表达映射和索引预期，SQLite 不负责强制字符串长度。账号昵称直接作为创建者默认显示名称；这是已确认的产品决策，创建者在创建时不单独自定义显示名称，加入后首版也不可修改。

会议名称 Trim + NFC 后长度 1 至 128、说明 Trim + NFC 后长度不超过 2000 的规则同样由 Domain/Application 强制；EF Core 的 `HasMaxLength` 不能代替 SQLite 下的业务校验。客户端 `maxLength` 只改善输入体验，服务端仍是最终校验边界。

### 4.3 会议码

会议码由 Application 在创建事务内调用 `Guid.NewGuid()` 生成。输入规范化由会议模块纯函数完成：Trim 后按标准 GUID `D` 格式解析，并统一格式化为小写 `D` 形式。首版只依赖 Guid 生成、解析和数据库唯一索引。

## 5. 应用服务契约

在 `Abstraction/Services/IMeetingWorkspaceService.cs` 固定提供：

- `CreateAsync(CreateMeetingRequest, CancellationToken)`；
- `ListMineAsync(CancellationToken)`；
- `GetDetailsAsync(Guid meetingId, CancellationToken)`；
- `GetInvitationAsync(Guid meetingId, CancellationToken)`；
- `PreviewJoinAsync(PreviewJoinRequest, CancellationToken)`；
- `JoinAsync(JoinMeetingRequest, CancellationToken)`；
- `DeleteAsync(Guid meetingId, DeleteMeetingRequest request, CancellationToken)`。

`CreateMeetingRequest` 包含 `CreationRequestId`、`Name` 和可选 `Description`。`PreviewJoinRequest` 只有 `MeetingCode`。`JoinMeetingRequest` 包含 `MeetingCode` 和 `DisplayName`；客户端传入默认昵称或自定义名称，服务端始终重新规范化。`DeleteMeetingRequest` 包含 `ConfirmationName`，服务端按会议名称的 Trim + NFC 规则规范化后，使用 `StringComparison.Ordinal` 执行区分大小写的精确比较。

字段验证错误键固定为 `creationRequestId`、`name`、`description`、`meetingCode`、`displayName` 和 `confirmationName`。`CreationRequestId` 不能是空 Guid。会议码请求字符串最长 64 个字符；Trim 后必须能按标准 GUID `D` 格式解析。

结果状态至少包括：

- `Success`；
- `Created`；
- `Replayed`；
- `ValidationFailed`；
- `MeetingNotFound`；
- `AlreadyMember`（映射为成功并返回既有参与身份）；
- `DisplayNameTaken`；
- `JoinConflict`；
- `CreatorRequired`；
- `CreationRequestMismatch`；
- `CreationRequestConsumed`；
- `Accepted`。

`Created` 与 `Replayed` 只用于创建结果，使 Controller 分别返回 `201` 和 `200`；删除屏障成功使用 `Accepted` 并映射 `202`；其他查询或命令使用 Success。加入限流属于 Web 入口结果，不进入 Application 的 `MeetingOperationStatus`。

结果类型不暴露领域实体、数据库异常或非成员无权查看的会议码。

## 6. 用例语义

### 6.1 创建

应用服务先验证当前用户和字段；`IsAuthenticated = false`、`UserId` 为空或空 Guid 都按未认证处理。随后通过既有 `IUserAccountRepository.FindByIdAsync(UserId)` 读取账号昵称，再进入显式短事务。事务内先按 `CreatorUserId + CreationRequestId` 查询创建回执：Active 回执只有在关联 Meeting 仍存在且为 Active 时才允许重放；保存内容一致则返回原会议及人类成员邀请信息，内容不一致则返回 `409 creation_request_mismatch`。Active 回执指向缺失、Deleting 或其他不可访问会议，以及 Deleted 回执，统一返回 `409 creation_request_consumed`，不能重新创建会议。不存在回执时生成会议码，原子创建 Meeting、MeetingParticipant 和 Active 回执。账号记录不存在时按未认证处理，不从客户端资料或 Cookie Claim 回退昵称。会议码以 Guid 明文持久化，但只在已入会人类成员有权获得的 DTO 和短生命周期内存状态中对外出现；提交失败时废弃当前作用域，不重用同一 DbContext。

若 MeetingCode 唯一索引发生随机碰撞，本次创建按内部错误失败并废弃当前作用域；不在失败后的 DbContext 上重试。用户重新提交创建请求时生成全新会议和会议码。

### 6.2 会议列表

按当前 `UserId` 查询参与关系，返回 Active 会议的列表项。排序固定为当前用户 `JoinedAtUtc` 倒序、MeetingId 升序作为稳定次序；创建者的 JoinedAtUtc 与 CreatedAtUtc 相同。列表返回 `joinedAt` 而不是创建时间，不包含会议码、背景全文、参与者详情、消息摘要、未读数量或在线状态。

### 6.3 详情

先检查会议为 Active，再检查当前账号参与关系。成功后返回基本信息、当前账号参与者信息和全部静态参与者，不返回会议码。参与者固定按 `JoinedAtUtc` 升序、ParticipantId 升序返回。已入会人类成员需要会议码时调用单独的邀请信息用例；该用例先检查 Active 状态和当前账号成员关系，再读取持久化的 Guid 并格式化为标准 `D` 字符串。服务端日志和请求追踪上下文始终不包含会议码。

### 6.4 加入预检

预检按 Guid MeetingCode 定位会议，检查 Active 状态，并返回会议名称、说明和当前账号昵称作为默认显示名称。预检不创建参与者、不返回参与者列表、不承诺名称可用。若当前账号已经是成员，预检返回已加入标志和既有显示名称。

### 6.5 加入确认

加入确认再次验证会议码和 Active 状态。若 `MeetingId + UserId` 已存在，返回既有参与者并忽略本次名称修改。否则检查统一名称唯一约束，在同一事务中创建参与者。任一并发唯一约束冲突先返回 `409 meeting_join_conflict`，当前作用域立即废弃，禁止在失败后的 DbContext 上继续查询。客户端自动发起至多一次新请求：若第一次冲突来自同账号重复加入，新作用域恢复既有参与者；若来自另一参与者占用了相同名称，新作用域稳定返回 `display_name_taken`；再次遇到 `meeting_join_conflict` 时停止自动重试并提示用户重试。SQLite 异常和索引名称不泄露给客户端。

### 6.6 删除

删除服务先确认当前账号是该会议成员；非成员和不可见会议返回 MeetingNotFound，成员但非创建者返回 CreatorRequired。随后校验 `ConfirmationName` 与保存的会议名称一致；不一致返回字段验证错误。校验通过后以条件更新将 Meeting 从 Active 改为 Deleting，条件包含 MeetingId、CreatorUserId 和当前状态。只有一行受影响才接受删除；零行时统一映射为不可访问。状态屏障提交成功后，所有其他用例必须拒绝会议访问，HTTP 接口返回 `202 Accepted`，不等待物理清理完成。

本主题后台清理器清理参与者和会议记录。清理失败保持 Deleting，由启动恢复器和运行期协调器扫描重试；不得恢复 Active，不得让客户端继续读取。屏障提交失败才是删除请求失败；屏障成功后的后台清理失败不反转已经返回的接受结果。

## 7. HTTP API

### 7.1 路由

| 方法 | 路径 | 认证 | 作用 |
| --- | --- | --- | --- |
| `GET` | `/api/meetings` | 必须认证 | `200 OK`，当前用户参与的会议列表 |
| `POST` | `/api/meetings` | 必须认证 | 首次创建返回 `201 Created`；相同 CreationRequestId 重放返回 `200 OK`，响应均为详情和人类成员邀请信息 |
| `GET` | `/api/meetings/{meetingId}` | 必须认证 | `200 OK`，读取当前成员可见的详情 |
| `POST` | `/api/meetings/{meetingId}/invitation` | 必须认证 | `200 OK`，已入会人类成员取得会议码邀请信息 |
| `POST` | `/api/meetings/join/preview` | 必须认证 | `200 OK`，会议码预检 |
| `POST` | `/api/meetings/join` | 必须认证 | `200 OK`，确认加入或恢复既有身份 |
| `POST` | `/api/meetings/{meetingId}/deletion` | 必须认证 | 校验确认名称并建立删除屏障，成功返回 `202 Accepted` |

所有 POST 端点登记主题二的 `RequireBrowserOrigin` 元数据。元数据固定为：创建、加入预检、加入确认和删除使用 `RequireBrowserOrigin(true)` 并要求 `application/json`；无正文的邀请信息使用默认 `RequireBrowserOrigin(false)`。详情、邀请信息和删除端点的 `meetingId` 使用 Guid 路由约束，不能把会议码放入 URL。创建成功响应设置 `Location: /api/meetings/{meetingId}`。

会议 API 的 `Cache-Control: no-store` 和 `Pragma: no-cache` 由 Web 宿主统一使用 003 已冻结的 `SensitiveResponseHeadersMiddleware` 设置，覆盖 `/api/auth/*`、`/api/users*` 和 `/api/meetings*`。实现替换主题二现有的 `AuthenticationResponseHeadersMiddleware`，不同时注册两个重复中间件；非敏感 API 不自动添加该头。

对外 DTO 不直接序列化 Domain 或 Application 枚举。`ParticipantDto.type` 使用 Abstraction 中的稳定传输枚举，Controller JSON 配置固定加入 `JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: false)`，OpenAPI 线上值固定为 `human | agent`；Kiota 生成后必须回验该联合值。`MeetingLifecycleState` 和 `MeetingOperationStatus` 只在服务端内部使用，不进入成功 DTO；若未来对外暴露枚举，必须采用同一 camelCase 字符串约定，禁止默认整数序列化。

### 7.2 错误映射

| 场景 | HTTP | `code` |
| --- | --- | --- |
| 字段无效 | `400` | `validation_failed` |
| 非成员读取、无效/已删除会议码 | `404` | `meeting_not_found` |
| 显示名称已占用 | `409` | `display_name_taken` |
| 并发加入需要新作用域重试 | `409` | `meeting_join_conflict` |
| 相同创建幂等键但内容不一致 | `409` | `creation_request_mismatch` |
| 已删除会议的创建幂等键被重放 | `409` | `creation_request_consumed` |
| 已认证但非创建者删除 | `403` | `meeting_creator_required` |
| 加入请求超限 | `429` | `meeting_join_rate_limited` |
| 数据库故障 | `500` | `internal_error` |

对非成员和无效会议码统一 `meeting_not_found`，不泄露会议存在性。会议码缺失或超过请求长度上限返回 `validation_failed`；通过基础校验后的格式错误、查无会议和删除中会议也统一 `meeting_not_found`。Deleting 是内部恢复状态，不作为公开错误分支，避免物理清理时序改变相同请求的业务语义。

## 8. 授权与删除访问屏障

会议访问必须经过两层检查：

1. 主题二认证边界确认当前 `UserId`。
2. 会议应用服务确认 Active 状态和成员/创建者关系。

所有针对既有会议的读取、加入和删除入口都必须调用统一的 `IMeetingAccessGuard` 或等价领域服务；创建会议只经过认证和创建输入校验。后续消息、实时 Hub、MCP 和后台任务也必须先调用同一删除屏障；不能只在 Controller 增加判断。

删除状态转换和业务写入共享短事务和明确的写锁顺序。SQLite 实现使用等价于 `BEGIN IMMEDIATE` 的写事务：加入在事务内重新确认 Meeting 为 Active 后插入参与者，删除在同类事务内先提交 Active -> Deleting 再安排清理；两者只能有一个先提交，后到者按 `meeting_not_found` 失败，不能在失败的 DbContext 上继续查询。后续消息和任务写入必须建立等价保护。

为在不向 Application 暴露 EF Core、也不扩大通用 `IUnitOfWork` 接口影响面的前提下建立该事务，本主题在 Abstraction 新增 `IMeetingTransactionRunner.ExecuteAsync<TResult>(Func<CancellationToken, Task<TResult>> operation, CancellationToken cancellationToken)` 技术契约。Runner、会议仓储、既有 `IUnitOfWork` 与 `RoundTableMeetingDbContext` 必须是同一 DI scope，并共享同一个 Scoped DbContext、物理连接和 EF Core 当前事务；Runner 在该 DbContext 上创建并绑定 `BEGIN IMMEDIATE` 等价事务。Application 委托只允许查询和调用 `RegisterAdd/Edit/Delete`，不得调用 `CommitAsync`；委托成功返回后由 Runner 统一执行本次事务唯一一次 `IUnitOfWork.CommitAsync`，随后提交数据库事务。任何查询、SaveChanges、取消或事务提交失败都回滚并要求废弃当前作用域。Runner 检测到已有活动事务或嵌套调用时必须抛出明确的编程错误，不支持嵌套事务。既有 `IUnitOfWork` 接口和普通用例行为保持不变。

## 9. 持久化与索引

持久化表固定为：

```text
Meetings
  ID
  Name
  Description
  CreatorUserId
  MeetingCode
  LifecycleState
  CreatedAtUtc

MeetingCreationReceipts
  ID
  CreatorUserId
  CreationRequestId
  MeetingId nullable
  State Active | Deleted
  CreatedAtUtc
  ExpiresAtUtc nullable

MeetingParticipants
  ID
  MeetingId
  UserId nullable
  ParticipantType
  DisplayName
  NormalizedDisplayName
  JoinedAtUtc
```

必须建立：

- `Meetings.MeetingCode` 使用 Guid 列并建立唯一索引；
- `MeetingCreationReceipts.ID` 使用继承自 `BaseEntity` 的 Guid 主键，以兼容现有 `IEntity<Guid>` 和 `IUnitOfWork.Register*`；
- `MeetingCreationReceipts(CreatorUserId, CreationRequestId)` 建立业务唯一索引；
- `MeetingCreationReceipts.CreatorUserId` 指向 `UserAccounts` 的 Restrict 外键；
- Active 回执的 `MeetingId` 指向 Meetings；删除清理在同一事务中先把回执改为 Deleted 并将 MeetingId 置 null，再物理删除会议；
- Deleted 是统一的“创建意图已消费”终态，本主题只有会议删除会产生该状态，不建立审计原因枚举。Deleted 回执不保存会议名称、背景、邀请码或参与者数据，默认保留 30 天，`ExpiresAtUtc` 固定为转入 Deleted 时加 30 天；后台回执清理器可删除已到期的 Deleted 回执。Active 回执随会议存在，不按时间清理。到期后极旧请求 ID 理论上可再次使用，这是有限存储与断网重试窗口之间的明确取舍；客户端 UUID 不会持久化 30 天，首版接受该风险。
- `MeetingParticipants(MeetingId, UserId)` 使用 `UserId IS NOT NULL` 的过滤唯一索引；
- `MeetingParticipants(MeetingId, NormalizedDisplayName)` 无过滤唯一索引，对 Human 与未来 Agent 使用同一名称空间；不得按 ParticipantType 增加过滤条件；
- `MeetingParticipants.MeetingId` 使用 Restrict 外键；删除协调器显式、幂等删除参与者后再删除 Meeting，数据库不承担级联清理。
- `MeetingParticipants.UserId` 是指向 `UserAccounts` 的可空 Restrict 外键；
- `Meetings.CreatorUserId` 指向账号的 Restrict 外键；
- Human 参与者要求 UserId 非空，未来 Agent 参与者要求 UserId 为空的检查约束。

创建、加入和删除都使用 `IMeetingTransactionRunner` 包装同一作用域内的重新查询、既有 `IUnitOfWork.Register*` 和一次 `CommitAsync`。创建借此原子写入 Meeting、创建者参与者和创建回执；加入和删除借此保证 Active 屏障顺序。NoTracking 查询返回列表项和详情投影；删除状态修改通过局部跟踪实体或明确条件更新实现，但不能绕过事务 runner 和工作单元提交边界。

## 10. 安全、限流与日志

- 会议码使用 `Guid.NewGuid()` 生成，并按标准 GUID `D` 格式展示和解析。
- 会议码只在创建成功响应和人类成员邀请信息响应中返回，禁止出现在普通成员详情、URL、日志、指标、异常和客户端持久化存储。
- 加入预检和加入确认使用 Web 层认证后的 action filter/resource filter 执行，不放在 `UseAuthentication` 之前。请求先经过既有 Origin 边界：缺失或不允许的 Origin 仍由 `OriginValidationMiddleware` 以 `403 origin_not_allowed` 拒绝；通过 Origin 校验后，Cookie 认证先于限流执行，未认证请求固定返回 `401 unauthenticated` 且不消耗加入配额；已认证请求在进入 Controller/Application、查询会议码之前执行限流。预检和确认共享同一账号配额和同一来源地址配额，默认每账号每 5 分钟 30 次、每来源地址每 5 分钟 100 次；窗口和次数通过 `MeetingJoinRateLimit` Options 配置，必须为正数并在启动时校验。任一层超限即拒绝并返回 `429 meeting_join_rate_limited` 与 `Retry-After`。来源地址只在请求来自配置的可信代理时采用转发头。计数器首版使用单实例进程内存储，服务重启会清零，不引入分布式缓存；这是首版单实例部署的明确取舍。限流器不把完整会议码作为 key。
- 会议码输入只接受 JSON；服务端去除首尾空白后使用 `Guid.TryParseExact(value, "D")` 或等价严格 D 格式解析，大小写不敏感，错误响应不回显会议码。
- 请求完成日志只记录路由模板、状态、耗时、用户标识摘要和会议 ID；会议码、名称全文、背景全文和消息内容不进入日志。
- 会议名称和背景属于用户内容，默认不写入日志和指标。

## 11. 测试边界

服务端测试应覆盖：领域规范化与长度边界；应用创建、预检、加入幂等、名称冲突和成员授权；`CreationRequestMismatch/Consumed`、Active 回执指向 Deleting 会议、Deleted 回执 30 天清理；真实 SQLite Guid 列、唯一索引、Restrict 外键和 Migration；事务 runner 与同一 Scoped DbContext/连接绑定、禁止嵌套、取消、提交、回滚和失败后作用域终止；加入与删除并发的两种先后顺序以及 JoinConflict 重试的“恢复身份/名称占用”两种结局；会议码 Guid 生成、标准 `D` 格式解析、大小写不敏感和碰撞处理；Origin 元数据的有正文/无正文配置；未认证 `401` 与认证后限流 `429` 顺序、共享配额、`Retry-After` 和进程重启清零；敏感 API no-store/no-cache；`ParticipantDto.type` 的 JSON/OpenAPI/Kiota `human | agent` 契约；删除屏障和清理失败后的不可访问；HTTP ProblemDetails、未知成员 404、邀请入口 404、删除入口 403 和认证边界。具体矩阵进入本主题 `testPlan.md`。

## 12. 非目标

本服务端设计不添加消息表、SignalR Hub、MCP endpoint、AI 连接表、任务队列、未读游标、会议结束状态或管理员会议权限。
