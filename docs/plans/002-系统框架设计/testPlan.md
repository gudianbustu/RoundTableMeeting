# 系统框架测试方案

## 1. 文档定位

本文定义 002 系统框架主题如何被自动化测试、契约测试、构建验证和浏览器手工验收。实现步骤见[总体实现方案](impl.md)，服务端细节见[Server/impl.md](Server/impl.md)，客户端细节见[Client/impl.md](Client/impl.md)。

测试方案只验证框架能力和系统状态页面，不验证账号、会议、群聊、宿主原生 MCP Agent 或通信恢复。后续主题必须复用本方案中的构建、路由、错误和数据库回归基础，并为新增业务增加自己的测试文档。

## 2. 测试目标

002 主题测试需要证明：

1. 服务端项目能按设计分层并正确构建。
2. Application、Domain、Abstraction 不被 EF Core、SQLite 或 Web 入口污染。
3. SQLite Repository、NoTracking、工作单元和启动 Migration 语义可靠。
4. Serilog、ProblemDetails、traceId、OpenAPI 3.0 和状态接口形成可观察契约。
5. Kiota 能从已启动服务端的 HTTP OpenAPI JSON 重新生成客户端。
6. 客户端 Router、系统状态 feature、统一错误适配和 Vite proxy 可用。
7. dotnet publish 能自动构建和托管 Vite 产物，未知 API 不会被 SPA fallback 隐藏。
8. 独立 Chrome CDP 能检查实际页面，而不是只检查端口存在。

## 3. 测试环境和隔离

### 3.1 工具版本

- .NET SDK 使用仓库 global.json 锁定的 .NET 10 版本。
- .NET 工具使用 .config/dotnet-tools.json 中固定的 dotnet-ef 和 Kiota。
- Node.js 使用 Node 24 LTS，pnpm 使用 packageManager 固定版本。
- 浏览器冒烟使用独立 Profile 的开发模式 Chrome，CDP 仅绑定 127.0.0.1。

### 3.2 数据库隔离

1. Domain 和 Application 测试不依赖文件数据库。
2. Repository.Sqlite 测试每个测试类或测试集合使用独立临时 SQLite 文件；文件路径随机、测试结束清理。使用连接池时 teardown 必须先释放 scope/connection，再调用 `SqliteConnection.ClearAllPools()`，最后删除数据库文件和目录。
3. 不使用 EF Core InMemory 替代 SQLite 关系行为测试。测试专用 `TestDbContext` 必须复用生产的 NoTracking 配置和同一个 SQLite 连接拦截器/配置构建函数；所有依赖 NoTracking、WAL 或 busy timeout 的断言都标明这是对该配置路径的验证。
4. IntegrationTests 使用独立临时数据库配置，并在 WebApplicationFactory 生命周期结束后删除文件和目录。
5. 测试不能读取或修改开发默认 data/round-table-meeting.db。
6. 并发测试不得共享同一个 Scoped DbContext；每个模拟请求或任务创建自己的 scope。

### 3.3 日志和网络隔离

- 测试可以注入内存日志 sink 或捕获 Console，但生产日志配置仍使用 Serilog Console Sink。
- 日志断言关注结构化属性和数量，不依赖完整文本格式或本地化文案。
- HTTP IntegrationTests 使用随机可用端口或 WebApplicationFactory TestServer，不依赖开发服务器已在运行。
- Kiota 生成链路测试另行启动真实服务端；客户端单元测试不要求服务端进程存在。

## 4. 测试项目和职责

| 项目 | 主要范围 | 禁止依赖 |
| --- | --- | --- |
| RoundTableMeeting.Domain.Tests | 基础实体、领域异常和纯领域规则 | EF Core、Web、SQLite |
| RoundTableMeeting.Application.Tests | 应用服务编排、DTO 映射、仓储/UoW 调用顺序 | 真实 Web Host、开发数据库 |
| RoundTableMeeting.Repository.Sqlite.Tests | DbContext、映射、Repository、NoTracking、UoW、Migration、SQLite 参数 | 开发数据库 |
| RoundTableMeeting.IntegrationTests | HTTP 管道、DI、状态接口、OpenAPI、ProblemDetails、静态托管 | 外部真实账号或业务数据 |
| RoundTableMeeting.ArchitectureTests | 项目引用和程序集边界 | 运行时网络、数据库 |

客户端测试位于 Client/roundTableMeeting/src/，使用 Vitest、jsdom 和 React Testing Library；构建和生成脚本验证使用 pnpm 命令。

## 5. 测试编号和通过规则

优先级定义：

- P0：阻断框架使用或会造成错误数据/错误路由的测试，必须通过。
- P1：核心框架契约和常见失败路径，主题完成前必须通过。
- P2：辅助可维护性和诊断能力，可以在首轮失败后修复再通过，不得长期忽略。

所有 P0/P1 失败都阻止 002 主题完成。测试必须可重复运行，不能依赖测试执行顺序、开发者本地文件或外部账号。

## 6. Domain 单元测试

测试项目：Server/Tests/RoundTableMeeting.Domain.Tests/。

| 编号 | 优先级 | 场景 | 预期 |
| --- | --- | --- | --- |
| D-001 | P0 | 创建 BaseEntity 派生对象 | ID 非空且自动生成 |
| D-002 | P0 | 连续创建两个实体 | ID 不重复 |
| D-003 | P1 | 尝试从生产 API 修改 ID | 编译层面无公开 setter；测试通过反射只验证保护边界 |
| D-004 | P0 | 检查 IUnitOfWork 方法签名 | 四个方法名称、参数和异步返回类型符合冻结接口 |
| D-005 | P1 | 构造 ConcurrencyConflictException | 异常不依赖 EF Core 类型，InnerException 可选保留 |

若框架阶段没有实际聚合实体，不能创建假的业务实体来凑测试；BaseEntity 和接口契约测试足够。

## 7. Application 测试

测试项目：Server/Tests/RoundTableMeeting.Application.Tests/。

使用手写 fake 或 mock 的 Abstraction/Domain 接口，不引用 Repository.Sqlite 实现。

| 编号 | 优先级 | 场景 | 预期 |
| --- | --- | --- | --- |
| A-001 | P0 | 数据库检查返回 available | SystemStatusService 返回 available/available |
| A-002 | P0 | 数据库检查返回 unavailable | SystemStatusService 返回 degraded/unavailable |
| A-003 | P0 | RuntimeEnvironment 提供环境名 | DTO 使用服务端提供的环境名，不接受客户端输入 |
| A-004 | P1 | 状态 DTO 映射 | 不包含 migrationCompleted、连接字符串、绝对路径或其他设计外字段 |
| A-005 | P1 | 状态检查被取消 | 取消令牌传递到数据库可用性检查，服务不伪造成功结果 |

Application 测试用编译检查和架构测试保证它没有引用 EF Core、Repository.Sqlite 或 Web。

## 8. Repository.Sqlite 测试

测试项目：Server/Tests/RoundTableMeeting.Repository.Sqlite.Tests/。

### 8.1 配置、映射和迁移

| 编号 | 优先级 | 场景 | 预期 |
| --- | --- | --- | --- |
| R-001 | P0 | 默认连接字符串解析 | DataSource 为 data/round-table-meeting.db，不是内存库 |
| R-002 | P0 | 相对路径归一化 | 数据库路径位于 AppContext.BaseDirectory 下的 data 目录 |
| R-003 | P0 | 空临时库执行 InitializeAsync | 自动创建父目录、SQLite 文件和 Migration 历史 |
| R-004 | P0 | 重复执行 InitializeAsync | 幂等成功，不重复破坏已有数据 |
| R-005 | P1 | 构造无效连接配置 | Options 校验在启动时失败 |
| R-006 | P0 | Migration 或数据库打开失败 | 异常向宿主抛出，不返回“初始化成功” |
| R-007 | P1 | 初始化后查询 PRAGMA journal_mode | journal mode 为 WAL；迁移连接关闭并清理连接池后，至少一个独立新连接仍为 WAL |
| R-008 | P1 | 初始化后在多个新连接查询 PRAGMA busy_timeout | 释放旧连接并清理连接池后，每个独立新连接的 busy timeout 均为 5000 |
| R-008A | P1 | 设计时 DbContextFactory 创建上下文 | 不构建 Web Host、不调用数据库初始化器、不写入开发数据库 |
| R-008B | P1 | 配置覆盖与路径解析 | 环境变量/测试配置覆盖 appsettings；相对路径只由单一函数按 AppContext.BaseDirectory 解析；测试和发布可显式指向可写绝对目录 |

`R-004` 在第二次初始化前写入独立探针表，重复初始化后验证探针数据和唯一 Migration 历史仍保留。`R-006` 使用指向目录的 SQLite DataSource 触发真实数据库打开失败并断言异常上抛；宿主级终止语义由 `I-017` 的独立真实进程测试证明，不为测试向生产初始化器增加故障注入接口。`R-008A` 直接实例化设计时工厂，验证 Provider、NoTracking、覆盖后的 DataSource，并确认创建上下文不会创建数据库文件；该调用路径不构建 Web Host，也不解析或调用 `IDatabaseInitializer`。

### 8.2 NoTracking

| 编号 | 优先级 | 场景 | 预期 |
| --- | --- | --- | --- |
| R-009 | P0 | 查询实体后检查 ChangeTracker | 普通查询实体为 Detached，不被跟踪 |
| R-010 | P0 | 普通查询不使用 AsTracking | 仓储没有隐式恢复跟踪 |
| R-011 | P1 | AsTracking 源码约定检查 | 框架阶段没有业务时不出现 `AsTracking()`；后续若引入必须同步设计说明，不把该项伪装成运行时行为测试 |
| R-012 | P0 | 修改 NoTracking 查询结果并 RegisterEdit | Detached -> Modified，默认整行更新 |
| R-013 | P1 | 修改后提交并重新查询 | 修改字段持久化，未修改字段保持原值 |

### 8.3 工作单元

使用测试专用 TestEntity : BaseEntity 和最小映射验证状态机。

| 编号 | 优先级 | 场景 | 预期 |
| --- | --- | --- | --- |
| R-014 | P0 | Detached 实体 RegisterAdd | 状态为 Added |
| R-015 | P0 | Detached 实体 RegisterEdit | Attach 后状态为 Modified |
| R-016 | P0 | Detached 实体 RegisterDelete | Attach 后状态为 Deleted |
| R-017 | P1 | Added 实体再次 RegisterAdd | 幂等，不重复添加 |
| R-018 | P1 | Added 实体 RegisterDelete | 取消新增并转为 Detached |
| R-019 | P1 | Unchanged 实体 RegisterEdit/Delete | 转为 Modified/Deleted |
| R-020 | P1 | 已跟踪同主键不同实例 | 抛出 InvalidOperationException，不自动合并 |
| R-021 | P1 | null 作为 Register 参数 | 抛出 ArgumentNullException |
| R-022 | P0 | 一次 Commit 有变更 | 只调用一次 SaveChangesAsync，返回受影响行数 |
| R-023 | P0 | 无变更 Commit | 返回 0 |
| R-024 | P0 | Commit 成功 | ChangeTracker.Clear，后续没有残留跟踪实体 |
| R-025 | P0 | SaveChangesAsync 普通失败 | 异常上抛，不能返回成功；跟踪状态按失败清理语义清空，调用方必须废弃当前 scope，不得在同一 scope 重试 |
| R-026 | P0 | SaveChangesAsync 并发失败 | 转换为 ConcurrencyConflictException，原异常保留为 InnerException；跟踪状态清空，调用方必须废弃当前 scope。本项是异常适配单元测试，不要求框架阶段通过真实业务实体自然制造竞态 |
| R-027 | P1 | Commit 取消 | OperationCanceledException 上抛，清理跟踪状态；调用方必须废弃当前 scope |
| R-028 | P1 | 同一 UoW 并发 Commit | 不加锁掩盖错误；允许 EF Core 线程安全检查或确定范围内的并发异常，断言无伪成功结果且数据库状态未被破坏，不做依赖时序的单一异常断言 |

### 8.4 Scope 生命周期

| 编号 | 优先级 | 场景 | 预期 |
| --- | --- | --- | --- |
| R-029 | P0 | 同一 scope 解析 UoW、数据库初始化器和可用性检查 | 共享同一 DbContext 实例；后续具体 Repository 沿用该约束 |
| R-030 | P0 | 两个独立 scope 解析 DbContext | 实例不同 |
| R-031 | P1 | 请求 scope 结束 | DbContext 和相关资源释放 |

## 9. DI、架构和包约束测试

测试项目：RoundTableMeeting.ArchitectureTests。

| 编号 | 优先级 | 场景 | 预期 |
| --- | --- | --- | --- |
| C-001 | P0 | Domain 项目引用扫描 | 不引用 Web、Application、Repository.Sqlite、EF Core 或 SQLite |
| C-002 | P0 | Abstraction 项目引用扫描 | 不引用 Domain、EF Core、Repository.Sqlite 或 Web |
| C-003 | P0 | Application 项目引用扫描 | 不引用 EF Core、Repository.Sqlite 或 Web |
| C-004 | P0 | Repository.Sqlite 项目引用扫描 | 只通过 Domain/Abstraction 与业务契约连接，EF Core 只停留在实现侧 |
| C-005 | P0 | Application 类型依赖扫描 | 不出现 DbContext、DbSet、IQueryable 或 EF EntityState |
| C-006 | P0 | 应用服务构造函数扫描 | 不注入宽泛 IRepository<TEntity, Guid>、DbContext 或生成 Client |
| C-007 | P0 | 领域仓储接口扫描 | 每个业务仓储是空壳扩展或明确查询契约，不包含 Add/Update/Delete |
| C-008 | P0 | Materal 注册标记扫描 | 应用服务和 Repository 实现使用明确泛型 IScopedDependency |
| C-009 | P0 | 组合根源码约定检查 | 对组合根源文件做文本断言：包含 `AddAutoService` 与 Application/Sqlite 两个 AssemblyMarker，使用 ASP.NET Core 默认 DI，不包含 `AddMateralServiceProvider`、`AddTTARepository`、`AddTTASqliteEFRepository` 或默认全程序集扫描调用；该项属于源码检查，不是纯反射行为测试 |
| C-010 | P1 | 显式标记服务解析 | 默认 ASP.NET Core DI 下，ISystemStatusService、IUnitOfWork、IDatabaseInitializer、IDatabaseAvailabilityChecker 可由显式 `AddAutoService` 按 Scoped 解析 |
| C-011 | P1 | 包版本扫描 | 生产和测试项目无浮动版本，Materal 版本符合实施文档 |
| C-012 | P1 | generated 目录扫描 | 客户端业务文件不得直接 import api/generated |

## 10. HTTP IntegrationTests

测试项目：Server/Tests/RoundTableMeeting.IntegrationTests/。

使用真实 WebApplicationFactory<Program>、默认 ASP.NET Core DI、真实 ProblemDetails 管道和测试临时 SQLite 配置。IntegrationTests 在 `ConfigureWebHost`/`ConfigureServices` 中把测试程序集 Controller 作为 MVC ApplicationPart 注入：`GET /__test/throw` 用于 I-005，`POST /__test/validation` 用于 I-006；后者绑定带 `[Required]` 字段的测试请求模型。Origin 框架测试在同一测试程序集提供标记 POST/PATCH/PUT/DELETE 和未标记 POST 端点。所有测试 Controller 使用 `[ApiController]` 和 `[ApiExplorerSettings(IgnoreApi = true)]`，复用生产 `UseRouting`、Origin、Authentication/Authorization、`MapControllers`、模型校验、异常处理和 ProblemDetails 管道，不进入 OpenAPI。这些端点不写入生产 Program，不被生产项目引用，也不进入生产发布产物。测试工厂必须验证显式 Materal `AddAutoService` 标记服务解析、Scoped 生命周期、同 scope DbContext 共享、跨 scope 隔离、临时配置替换、测试 ApplicationPart 和 Serilog 日志捕获。`AddMateralServiceProvider` 和 AspectCore 容器集成不属于最终方案；C-009 明确禁止其调用。

| 编号 | 优先级 | 场景 | 预期 |
| --- | --- | --- | --- |
| I-001 | P0 | 启动测试宿主 | 配置校验、DI、Migration 和 HTTP 管道成功 |
| I-002 | P0 | GET /api/system/status 数据库可用 | 200，返回 available/available/environment |
| I-003 | P0 | 运行期数据库检查失败 | 200，返回 degraded/unavailable，不返回连接细节 |
| I-004 | P0 | 请求不存在的 API | 404，Content-Type 为 JSON ProblemDetails，code 为 route_not_found |
| I-005 | P0 | Controller/中间件抛出未处理异常 | 500，code 为 internal_error，包含 traceId，不包含堆栈 |
| I-006 | P1 | 传入非法模型 | 400，code 为 validation_failed，errors 为字段到消息数组 |
| I-007 | P0 | 请求带 W3C traceparent | 响应 traceId 与 Activity TraceId 一致 |
| I-008 | P1 | 无 Activity 的回退单元测试 | 直接调用独立 TraceIdResolver，在 `Activity.Current` 为空时回退 `HttpContext.TraceIdentifier`；不要求常规 HTTP 宿主制造无 Activity 请求 |
| I-009 | P1 | 检查错误响应敏感信息 | 不包含密码、Token、连接字符串、绝对路径或内部堆栈 |
| I-010 | P1 | OpenAPI HTTP 文档 | 200，根版本为 OpenAPI 3.0，包含 status endpoint |
| I-011 | P1 | OpenAPI 错误 schema | 包含 code、traceId、errors 和 operation 错误响应声明 |
| I-012 | P1 | Scalar 页面 | 返回可加载页面并指向同一个 OpenAPI JSON |
| I-013 | P1 | 请求日志 | IntegrationTests 解析真实 Serilog LogEvent，断言 traceId、requestMethod、requestPath、statusCode、elapsedMilliseconds 字段和安全边界 |
| I-014 | P1 | 相同异常日志次数 | 未处理异常只记录一次集中 Error/Critical，不由 Controller 重复记录 |
| I-015 | P1 | 调用方取消状态请求 | 不把取消转换为 500 ProblemDetails；宿主允许请求自然终止，服务端不记录重复 Error |
| I-016 | P1 | 数据库降级日志关联 | 产生一条 Warning 和一条请求完成日志，二者可通过 traceId 关联且不含连接信息 |
| I-017 | P0 | 真实进程启动初始化失败 | 进程返回非零，端口未开始监听，启动日志包含安全诊断；原始、JSON 转义和正斜杠形式均不包含数据库绝对路径或连接字符串 |
| I-018 | P0 | 标记 POST/PATCH/PUT + JSON + 允许 Origin | 三种方法都成功进入各自 endpoint，中间件不硬编码 POST |
| I-019 | P0 | 标记 DELETE、RequireJsonBody=false、允许 Origin且无正文/Content-Type | 成功进入 endpoint，不因缺少 Content-Type 返回 415 |
| I-020 | P0 | 标记端点的 Origin 矩阵 | 允许 Origin 通过；缺失、重复多值、`null`、外部 Origin 均返回 403 origin_not_allowed，且 endpoint 未执行 |
| I-021 | P0 | RequireJsonBody=true 的媒体类型与正文边界 | `application/json` 与带 charset 的 JSON 通过媒体类型检查；缺失、无效、其他 media type 和 `application/*+json` 返回 415；合法 media type 下的空/非法正文继续由模型绑定返回 400 |
| I-022 | P1 | RequireJsonBody=false 的标记 POST/DELETE，携带任意或缺失 Content-Type | Origin 允许后均不由 Origin 中间件拒绝，正文约束留给 endpoint 自身 |
| I-023 | P0 | 对标记路由发送错误 HTTP 方法，省略 Origin 和 Content-Type | 中间件通过 IHttpMethodMetadata 识别方法不匹配并放行；最终由 routing/fallback 返回项目当前的 404 或框架 405，不得改写为 403/415 |
| I-024 | P0 | 未标记 API 与未知 `/api/*`，分别携带缺失/外部 Origin | 未标记 endpoint 不校验 Origin并保持原授权/执行语义；未知 API 仍为 404 route_not_found |
| I-025 | P0 | Endpoint metadata 与管道顺序 | `UseRouting` 位于 Origin 中间件之前；Origin 位于 Authentication/Authorization 之前，运行时能够读取标记元数据 |
| I-026 | P1 | Origin/认证/模型校验 ProblemDetails 写入路径 | 403、415 与既有 400/401/403 使用 Errors 边界统一工厂/写入器；不得各自构造 ApiProblemDetails 或复制 Results.Json 流程 |

`I-003` 使用测试替身验证 HTTP 状态映射，替身在真实请求内记录一条 Warning；真实 `SqliteDatabaseAvailabilityChecker` 的超时、调用方取消和异常降级语义由 Repository.Sqlite.Tests 覆盖。`I-008` 与 `I-015` 直接测试无状态 traceId 解析器和集中异常处理器；`I-014` 通过真实 Serilog sink 断言同一未处理异常只有一条 Error/Fatal；`I-016` 使用固定 W3C traceparent，断言降级 Warning 与请求完成日志具有相同 traceId，且日志不包含连接信息。`I-017` 启动独立 `RoundTableMeeting.dll` 进程并注入不可打开的 SQLite DataSource，验证非零退出、端口未监听和安全 bootstrap 日志。

I-018 至 I-026 使用测试专用 `OriginTestController` 或等价 endpoint，端点固定为：要求 JSON 的标记 POST/PATCH/PUT、`RequireJsonBody=false` 的标记 POST/DELETE、未标记 POST。测试 AllowedOrigins 使用纯 Origin 值，例如 `http://localhost:5173`。测试先验证 endpoint 成功标识，再验证拒绝响应的 `status`、`code`、`traceId` 和安全固定文案；明确使用不允许/缺失 Origin 与缺失/错误 Content-Type 的组合请求证明 403 必须先于 415，Origin 不允许时不继续检查媒体类型。重复 Origin Header 必须通过显式单值判断返回 403；AttributeUsage 通过反射验证为方法级、不可重复且可继承。

管道顺序同时使用运行时行为和源码/架构约定检查：请求日志必须覆盖 Origin 的 403/415，显式 `UseRouting()` 必须早于 Origin 中间件，Origin 必须早于 `UseAuthentication()`/`UseAuthorization()`，默认文件/静态文件顺序与实际 Program 保持一致。不得通过把测试 endpoint 映射到独立替代管道来绕过生产中间件顺序。

## 11. OpenAPI 和 Kiota 契约测试

### 11.1 OpenAPI 文档检查

脚本或 IntegrationTests 解析 OpenAPI 文档：脚本验证使用真实服务端 `http://localhost:5043/openapi/v1.json`，IntegrationTests 使用 WebApplicationFactory 的 TestServer 根地址，不依赖开发服务器已在运行。

1. JSON 可解析，openapi 以 3.0 开头。
2. paths 包含 /api/system/status。
3. components.schemas 包含成功状态 DTO 和扩展 ProblemDetails。
4. 错误响应明确绑定到对应 operation。
5. 不包含绝对开发机路径、连接字符串或 secret。

### 11.2 Kiota 生成链路

| 编号 | 优先级 | 场景 | 预期 |
| --- | --- | --- | --- |
| K-001 | P0 | 启动真实服务端后执行 pnpm generate:api | 生成成功，输出到 src/api/generated/；默认端口和自定义 5055 端口都可归一化 |
| K-002 | P0 | 检查生成代码 | TypeScript 可被客户端构建，文件为 UTF-8 无 BOM 且行尾为 CRLF；generated 不包含 `.kiota.log` 等运行诊断文件 |
| K-003 | P1 | 停止服务端后执行生成脚本 | 快速失败或超时，总耗时不超过 30 秒并返回非零，原 generated 目录保持不变 |
| K-004 | P1 | OpenAPI 返回非 JSON | 脚本返回非零并给出安全诊断 |
| K-005 | P1 | OpenAPI 版本非 3.0 | 脚本拒绝生成并返回非零 |
| K-006 | P1 | 修改服务端契约后重新生成 | generated 目录出现可审查差异 |
| K-007 | P1 | 业务代码依赖检查 | 页面只通过公共 Client/feature API 使用生成代码 |
| K-008 | P1 | staging 校验或目录替换失败 | 在备份前、备份后和切换失败点均恢复原 generated 目录，不留下半生成结果、替换目录或备份目录 |
| K-009 | P0 | 最终请求 URL 契约 | OpenAPI path 为 `/api/system/status`，adapter base URL 为当前 origin，最终 URL 恰好包含一次 `/api`，生成代码和构建产物不含 `http://localhost:5043` |
| K-010 | P1 | 生成错误模型契约 | 生成结果能够读取 `code`、`traceId`、`errors`；空 body、非 JSON、无 code 的 JSON 和取消请求按客户端错误适配规则处理 |

Kiota CLI 和 runtime 的版本必须由脚本或包清单读取，不能依赖全局安装版本。

框架阶段不为 `409 concurrency_conflict` 增加生产占位端点或假业务实体。R-026 只验证 Repository.Sqlite 的异常转换；首个明确配置并发令牌或更新行数冲突的业务主题负责增加 409 ProblemDetails 的端到端集成测试。

002 的 Origin 框架能力已经被首个生产 Cookie 消费方 login/logout 实际使用，并已完成 403/415 错误响应、OpenAPI 声明和认证回归验证；后续业务主题继续在各自 operation 上声明 `RequireBrowserOrigin` 元数据和对应错误响应，并运行 I-018 至 I-026 的相关回归，不得只验证全局 schema 存在。

## 12. 客户端 Vitest 测试

测试位置：Client/roundTableMeeting/src/**/*.test.ts(x)。

测试环境固定为 jsdom，使用 renderWithProviders 按需包装 MemoryRouter、Ant Design ConfigProvider 和目标 Provider。

| 编号 | 优先级 | 场景 | 预期 |
| --- | --- | --- | --- |
| V-001 | P0 | 首次渲染 SystemStatusPage | 显示加载状态，未显示假成功数据 |
| V-002 | P0 | API 返回 available | 显示客户端、服务端、数据库和 environment 状态 |
| V-003 | P0 | API 返回 degraded/unavailable | 显示降级和数据库不可用，不能显示为网络错误 |
| V-004 | P0 | Fetch 网络失败 | 显示服务不可访问和重试入口 |
| V-005 | P1 | ProblemDetails 失败 | 显示安全消息，使用 code/status 分支，不显示原始 JSON |
| V-006 | P1 | 点击重试 | 重新请求一次并更新页面状态 |
| V-007 | P1 | /system/status 路由 | 能够通过 MemoryRouter 渲染状态页 |
| V-008 | P1 | 未匹配客户端路由 | 显示 Not Found 页面并能返回状态页 |
| V-009 | P1 | 路由渲染异常 | 显示安全错误边界，不显示堆栈 |
| V-010 | P1 | 公共 Client 获取两次 | 返回同一模块级实例，不重复创建 adapter |
| V-011 | P1 | 生成模型转换 | feature ViewModel 不暴露 generated 模型细节 |
| V-012 | P2 | 小屏和长环境名 | 组件测试验证长环境名完整渲染且样式允许任意断词；窄屏 CDP 截图验证文本不溢出、不遮挡状态内容，布局保持稳定 |
| V-013 | P1 | 请求在组件卸载或重试前被取消 | 不显示网络失败或未知错误，不在卸载组件后更新状态 |

不整体 mock Ant Design；只 mock API 适配或公共 Client 的边界。组件测试按用户可见行为断言，不依赖实现细节。

## 13. 构建和发布测试

### 13.1 服务端构建

~~~powershell
dotnet tool restore
dotnet restore Server/RoundTableMeeting.slnx
dotnet build Server/RoundTableMeeting.slnx --configuration Debug
dotnet test Server/RoundTableMeeting.slnx --configuration Debug --no-build
~~~

预期：还原、编译和测试返回 0；不存在未处理编译警告或版本漂移警告。ArchitectureTests 使用反射、程序集引用检查和少量源码约定断言，不引入未实际使用的第三方架构测试包。

### 13.2 客户端构建

~~~powershell
Set-Location Client/roundTableMeeting
pnpm install --frozen-lockfile
pnpm test
pnpm build
~~~

预期：Vitest 和 TypeScript/Vite 构建返回 0；dist/ 包含入口 HTML 和静态资源，不包含源代码或 secret。

### 13.3 发布

| 编号 | 优先级 | 场景 | 预期 |
| --- | --- | --- | --- |
| P-001 | P0 | 干净目录执行 dotnet publish | 先精确校验 Node 24.20.0/pnpm 11.24.0，再自动执行 frozen install 和客户端 build，publish 文件清单包含本次构建的全部嵌套 dist 资源 |
| P-002 | P0 | 启动 publish 输出 | 首页和 /system/status 可打开，静态资源成功加载 |
| P-003 | P0 | 发布页面调用 API | 使用同源 /api/system/status 成功 |
| P-004 | P0 | 发布后刷新 Router 子路径 | 返回 index.html 并由 React Router 渲染 |
| P-005 | P0 | 请求未知 /api/not-found | 返回 JSON 404 ProblemDetails，不返回 index.html |
| P-006 | P1 | 请求 /openapi/v1.json 和 /scalar/v1 | 服务端端点正常，不被 SPA fallback 覆盖 |
| P-007 | P1 | 故意让 pnpm build 失败 | dotnet publish 返回非零，不复制旧 dist |
| P-008 | P1 | 非 GET/HEAD 页面请求 | 不返回 index.html |
| P-009 | P1 | 已有旧 dist 后重复 publish | 发布目录只包含本次构建资源，不残留已删除或改名的旧文件 |
| P-010 | P1 | 检查发布目录安全边界 | 不包含 `node_modules`、客户端源码、测试文件、`.env`、lockfile 或生成脚本 |
| P-011 | P1 | SPA fallback 的 Accept/方法边界 | 仅 GET/HEAD 且接受 HTML 的页面导航返回 index.html；JSON Accept、静态资源缺失和其他方法不回退 |

发布验证至少覆盖干净输出目录、已有旧 dist、重复 publish 和客户端构建失败。P-007 失败后必须检查发布目录没有复制旧 dist；P-001/P-009 必须检查实际发布文件清单，而不只断言命令退出码。

## 14. CDP 浏览器冒烟测试

浏览器方案遵循独立开发模式 Chrome 和 CDP 技能约束。该项不是 Playwright 自动化替代品，而是对真实开发或发布页面的连接、观察和诊断验收。

### 14.1 启动检查

1. 按 `E:\Project\Materal\MateralSkills\skills\browser-debugging-and-control\SKILL.md` 启动只绑定 `127.0.0.1` 的独立 Chrome CDP 实例和隔离 Profile。
2. 请求 http://127.0.0.1:9222/json/version，确认返回浏览器版本和 WebSocket 调试地址。
3. 请求 http://127.0.0.1:9222/json/list，确认目标 Vite 或发布页面出现在页面列表。
4. 使用当前环境实际可调用的 CDP 客户端连接目标页面；仅有端点响应不能算完成调试。若当前会话没有 CDP/MCP 客户端，只记录 `/json/version`、`/json/list` 可访问及无法附加的原因，不宣称已经完成 DOM、Console、Network 或点击检查。

### 14.2 开发环境冒烟

| 编号 | 优先级 | 检查 | 预期 |
| --- | --- | --- | --- |
| B-001 | P0 | 打开 http://localhost:5173/ | 页面非空，系统状态页面可见 |
| B-002 | P0 | 读取 DOM | 能找到页面标题、服务端状态、数据库状态和环境字段 |
| B-003 | P0 | 读取网络 | /api/system/status 通过 Vite proxy 返回预期 200 |
| B-004 | P1 | 读取 Console | 无未处理异常或资源加载错误；本应用无 SSR，不检查 hydration |
| B-005 | P1 | 停止服务端后刷新/重试 | 页面显示不可访问错误，不崩溃或显示假成功 |
| B-006 | P1 | 恢复服务端并重试 | 页面恢复为真实 available 状态 |
| B-007 | P2 | 宽屏截图 | 布局完整、没有明显重叠或溢出 |
| B-008 | P2 | 窄屏截图 | 状态文本和操作不被遮挡，页面仍可读 |
| B-009 | P1 | 键盘操作与语义 | 重试按钮可聚焦并由键盘触发；加载、降级和错误状态具有可读语义，错误信息与操作关联 |

### 14.3 发布环境冒烟

重复 B-001 到 B-004 和 B-009，页面地址替换为发布服务地址，并额外执行 P-004、P-005。截图和 DOM 结果必须来自发布服务，而不是 Vite 开发服务器。截图输出到仓库忽略的 `artifacts/cdp/002/`，按 `development-wide.png`、`development-narrow.png`、`publish-wide.png`、`publish-narrow.png` 命名；命令和检查摘要记录在代码审查、提交说明或外部 CI/PR 记录中，不回写为计划历史日志。

## 15. 失败场景矩阵

| 场景 | 服务端预期 | 客户端预期 | 级别 |
| --- | --- | --- | --- |
| 服务端未启动 | 无 HTTP 响应 | 显示网络不可访问和重试 | P0 |
| 启动 Migration 失败 | 进程启动失败，记录诊断日志 | 无法访问服务，不能显示假状态 | P0 |
| 运行期数据库不可用 | 200 degraded/unavailable | 显示降级，不误报网络失败 | P0 |
| 未知 API | JSON 404 ProblemDetails | 不渲染 HTML 页面作为 API 响应 | P0 |
| 标记写端点缺失/null/外部 Origin | 403 origin_not_allowed，endpoint 不执行 | 保持当前页面并显示安全错误 | P0 |
| 标记 JSON 写端点媒体类型错误 | 允许 Origin 后返回 415 unsupported_media_type | 显示请求格式不受支持 | P0 |
| 标记路由使用错误 HTTP 方法 | routing 404/405，不改写为 415 | 按 API 错误适配处理 | P0 |
| 未知 API 携带缺失或恶意 Origin | 404 route_not_found，不进入 Origin 校验 | 不渲染 HTML 页面作为 API 响应 | P0 |
| OpenAPI 不可用 | Kiota 脚本超时非零 | 不生成伪 Client | P0 |
| Kiota 生成失败 | 脚本安全报错 | 客户端原有生成结果不被半成品覆盖 | P1 |
| ProblemDetails 无法解析 | 安全未知错误响应 | 显示通用错误，不展示原始响应 | P1 |
| 发布前端构建失败 | dotnet publish 非零 | 不发布陈旧 dist | P0 |
| W3C traceparent 存在 | 使用 Activity TraceId | 错误反馈可提供 traceId | P1 |
| W3C traceparent 不存在 | 使用 TraceIdentifier 回退 | 错误仍有可关联 ID | P1 |
| Console 日志输出 | 结构化请求日志 | 不影响页面 | P1 |

## 16. 执行顺序和完成门

### 16.1 本地开发验证

~~~powershell
dotnet tool restore
dotnet restore Server/RoundTableMeeting.slnx
dotnet build Server/RoundTableMeeting.slnx --configuration Debug
dotnet test Server/RoundTableMeeting.slnx --configuration Debug

Set-Location Client/roundTableMeeting
pnpm install --frozen-lockfile
pnpm test
pnpm build
~~~

然后启动服务端和 Vite，执行 K-001 至 K-010 及 B-001 至 B-009。

### 16.2 发布验证

~~~powershell
dotnet publish Server/RoundTableMeeting/RoundTableMeeting.csproj --configuration Release --output artifacts/publish
~~~

启动 publish 输出，执行 P-002 至 P-011 和发布环境 CDP 冒烟。发布验收覆盖无证书 HTTP 与显式 HTTPS 配置：HTTP 不被强制跳转，HTTPS 使用测试证书或可信代理并核对转发协议；不把反向代理作为必需前置。

### 16.3 002 完成门

只有以下条件全部满足，系统框架主题才可以冻结：

- 所有 P0 测试通过。
- 所有 P1 测试通过，或有明确的阻断原因和用户批准的延期决定。
- dotnet build、dotnet test、pnpm test、pnpm build 和 dotnet publish 全部成功。
- OpenAPI/Kiota HTTP 生成链路成功和失败场景都验证过。
- 开发和发布页面的 CDP DOM、Console、Network 和截图检查完成。
- 变更没有引入业务主题的模拟实现或越过分层边界的临时依赖。

测试结果应在代码审查或提交说明中提供命令和摘要，不在本测试方案中追加历史执行日志。
