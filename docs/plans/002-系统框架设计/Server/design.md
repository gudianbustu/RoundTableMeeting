# 系统框架服务端设计

## 1. 文档定位

本文定义系统框架主题的服务端工程结构、项目职责、依赖方向、领域持久化抽象、SQLite 数据访问、依赖注入、OpenAPI、错误处理、静态文件托管和测试边界。跨端运行方式和总体完成标准以[系统框架设计](../design.md)为准。

## 2. 技术基线

- 使用最新稳定版 .NET 10 和 ASP.NET Core。
- 使用 `global.json` 锁定实施验证通过的 .NET 10 SDK 小版本，NuGet 包使用精确版本。
- 启动项目命名为 `RoundTableMeeting`，不添加 `.Web` 后缀。
- HTTP API 使用 Controller。
- Application 使用普通应用服务，不引入 MediatR、消息总线或形式化 CQRS Handler 框架。
- 数据访问使用 Entity Framework Core，第一版使用 SQLite。
- 使用 `Materal.Extensions.DependencyInjection` 的标记接口自动注册应用服务和仓储实现。
- 使用 ASP.NET Core OpenAPI 生成 OpenAPI JSON，并提供 Scalar 或同类 UI。
- API 错误统一使用 `ProblemDetails`。
- 使用 Serilog 作为日志实现，通过 `Serilog.AspNetCore` 接入 ASP.NET Core，使用 Console Sink 输出结构化日志，并使用 W3C Trace Context 关联请求与错误。
- 测试使用 xUnit。

## 3. 解决方案结构

```text
Server/
  RoundTableMeeting.slnx
  RoundTableMeeting/
  RoundTableMeeting.Application/
  RoundTableMeeting.Domain/
  RoundTableMeeting.Abstraction/
  RoundTableMeeting.Repository.Sqlite/
  Tests/
    RoundTableMeeting.Domain.Tests/
    RoundTableMeeting.Application.Tests/
    RoundTableMeeting.Repository.Sqlite.Tests/
    RoundTableMeeting.IntegrationTests/
    RoundTableMeeting.ArchitectureTests/
```

### 3.1 项目职责

- `RoundTableMeeting`：ASP.NET Core 宿主，负责应用启动、Controller、HTTP 管道、依赖装配、OpenAPI、数据库初始化调用和生产环境静态文件托管。
- `RoundTableMeeting.Application`：应用编排、普通应用服务和业务用例。通过领域仓储访问数据，通过 `IUnitOfWork` 提交变更，不直接使用 EF Core。
- `RoundTableMeeting.Domain`：领域实体、领域规则、项目级基础实体、每个聚合的领域仓储契约和 `IUnitOfWork`。
- `RoundTableMeeting.Abstraction`：跨项目 DTO、枚举、应用服务接口和非领域技术契约，包括 `IDatabaseInitializer`。
- `RoundTableMeeting.Repository.Sqlite`：SQLite 数据库实现，持有 `DbContext`、实体映射、Migration、领域仓储实现、工作单元实现和数据库初始化实现。
- 测试项目：分别验证领域规则、应用服务、SQLite 数据访问、HTTP 集成和项目依赖约束。

首版不创建 `RoundTableMeeting.Infrastructure`。出现无法自然归属于宿主、Application、Domain、Abstraction 或具体数据库 Repository 的独立技术适配能力时，再新增 Infrastructure 或按能力命名的项目。

## 4. 项目依赖方向

- `RoundTableMeeting.Abstraction` 不引用其他服务端业务项目。
- `RoundTableMeeting.Domain` 不引用其他服务端业务项目；允许引用 `Materal.TTA.Common`。
- `RoundTableMeeting.Application` 引用 `RoundTableMeeting.Domain` 和 `RoundTableMeeting.Abstraction`。
- `RoundTableMeeting.Repository.Sqlite` 引用 `RoundTableMeeting.Domain` 和 `RoundTableMeeting.Abstraction`。
- `RoundTableMeeting` 引用 `RoundTableMeeting.Application`、`RoundTableMeeting.Abstraction` 和 `RoundTableMeeting.Repository.Sqlite`。
- 测试项目只引用其测试目标所需的项目，不为生产项目制造反向依赖。

Application 不引用 Repository、TTA EF 实现或 EF Core。Domain 不引用 TTA 的 EF Core、SQLite 或其他数据库实现。Application 只使用 Abstraction 中的 DTO、枚举、基础类型和应用服务接口；应用服务接口不得暴露 Domain 实体、领域仓储或值对象，实体到 DTO 的映射由 Application 完成。Repository.Sqlite 引用 Abstraction 仅用于实现 `IDatabaseInitializer` 等技术契约，不消费应用 DTO 或应用服务接口。项目引用方向由 `.csproj` 和架构约束测试共同保证。

## 5. Domain 设计

### 5.1 基础实体

第一版主要领域实体统一使用 `Guid` 主键。Domain 提供项目级基础实体：

```csharp
public abstract class BaseEntity : IEntity<Guid>
{
    public Guid ID { get; protected set; } = Guid.NewGuid();
}
```

`ID` 使用与 TTA 契约一致的属性名，并且不提供公开修改入口。创建时间、更新时间、软删除、并发版本和审计字段不是所有实体已经确认的共同属性，首版不放入 `BaseEntity`。

### 5.2 领域仓储

每个聚合在 Domain 中定义独立的 `IXxxRepository`，直接继承 TTA 的 `IRepository<TEntity, Guid>`：

```csharp
public interface IUserAccountRepository : IRepository<UserAccount, Guid>
{
}
```

即使首版没有聚合专用查询，也保留空接口作为 Application 的稳定依赖契约。后续专用查询直接添加到对应仓储接口，不要求批量修改应用服务的构造函数类型。

Application 只注入 `IXxxRepository`，不直接注入 `IRepository<TEntity, Guid>`。Repository 实现不向 Application 暴露 `DbContext`、`DbSet<T>`、`IQueryable<T>` 或 EF Core 实体状态类型。

TTA 的 `IRepository<TEntity, Guid>` 在本项目中只作为查询契约使用，不提供新增、修改和删除方法。禁止在 `IXxxRepository` 上扩展 Add、Update、Delete 等写入方法；所有持久化写入必须且只能通过 `IUnitOfWork.RegisterAdd`、`RegisterEdit`、`RegisterDelete` 和 `CommitAsync` 完成。TTA 查询契约带来的表达式和查询模型依赖是本项目明确接受的框架耦合；新增聚合专用查询时优先使用明确的领域语义和基元参数。

### 5.3 工作单元

`IUnitOfWork` 位于 Domain，首版接口固定为：

```csharp
public interface IUnitOfWork
{
    void RegisterAdd(IEntity<Guid> entity);

    void RegisterEdit(IEntity<Guid> entity);

    void RegisterDelete(IEntity<Guid> entity);

    Task<int> CommitAsync(CancellationToken cancellationToken = default);
}
```

工作单元保持显式注册新增、修改和删除的模式，但不复制 TTA 工作单元中的 `TryRegister*`、同步 `Commit`、`IServiceProvider` 和 `GetRepository<T>`。常规仓储通过构造函数注入，不通过工作单元动态获取。

工作单元必须满足：

- 与领域仓储实现共享同一个 Scoped `DbContext`。
- 一次 `CommitAsync` 对应一次 `SaveChangesAsync`。
- 接受并传递 `CancellationToken`。
- 提交成功返回受影响记录数。
- 提交失败不能返回成功结果。
- `DbUpdateConcurrencyException` 不能通过重新加载实体静默吞掉。
- Commit 尝试结束后清理当前作用域内的实体跟踪状态；失败或取消仍然上抛，并要求调用方废弃当前 scope，不在同一工作单元上重试。
- 同一作用域内的工作单元和 `DbContext` 不允许被多个并发执行流同时使用；每个 HTTP 请求、后台任务或消息处理都使用独立 DI scope。
- 不在工作单元内部自动串行化并发调用；并发复用同一个 `DbContext` 属于编程错误，应通过 EF Core 线程安全检查和测试尽早暴露。

本项目工作单元必须完全自研并组合 `DbContext`，不得继承 TTA 的 `IUnitOfWork`、`EFUnitOfWorkImpl` 或 SQLite 工作单元基类。原因是 TTA 工作单元会在并发异常时重新加载实体并使提交失败不可见，且其内部同步等待不符合本项目的提交边界。未来若需要多次保存、跨数据库或外部资源协调，再独立设计显式事务能力；首版不提前扩展。

## 6. Abstraction 设计

Abstraction 保存跨项目使用但不属于领域模型的契约，建议按职责组织：

```text
RoundTableMeeting.Abstraction/
  Contracts/
  Enums/
  Services/
  Persistence/
    IDatabaseInitializer.cs
```

具体目录按实际内容创建，不预建空目录。领域实体、领域规则、领域仓储和 `IUnitOfWork` 不放入 Abstraction。Abstraction 中的应用服务接口只能使用本程序集中的 DTO、枚举、基础类型和其他稳定契约，不能引用 Domain 类型。

`IDatabaseInitializer` 固定放在 `Persistence/`：

```csharp
public interface IDatabaseInitializer
{
    Task InitializeAsync(CancellationToken cancellationToken = default);
}
```

该接口是宿主启动流程和当前数据库实现之间的技术契约，不暴露 `DbContext`、Migration、SQLite、SQL Server 或其他数据库类型。Application 业务用例不调用它。

## 7. SQLite Repository 设计

`RoundTableMeeting.Repository.Sqlite` 持有：

- EF Core `DbContext`。
- SQLite 实体映射。
- SQLite Migration。
- 基于 TTA EF Core/SQLite 能力的领域仓储实现。
- 本项目 `IUnitOfWork` 的实现。
- `IDatabaseInitializer` 的 SQLite 实现。
- 数据库目录创建和 SQLite 连接配置。

领域仓储实现与工作单元必须注入同一个 Scoped `DbContext`。EF Core `DbContext` 全局配置 `QueryTrackingBehavior.NoTracking`，所有普通查询默认不跟踪实体；仓储不得在普通查询中隐式恢复跟踪，只有确有明确写入或并发需求时才允许在局部查询中显式使用 `AsTracking()`，并且必须由对应业务设计说明原因。工作单元负责设置新增、修改、删除状态并统一提交，仓储自身不隐式提交。由于 NoTracking 查询返回的实体未被当前上下文跟踪，`RegisterEdit` 对该实体按 `Detached -> Modified` 处理，默认产生整行更新；需要选择性更新时，必须由后续业务设计明确增加对应能力。

首版项目直接命名为 `RoundTableMeeting.Repository.Sqlite`。未来需要同时保留其他数据库实现时，平行增加：

```text
RoundTableMeeting.Repository.Sqlite/
RoundTableMeeting.Repository.SqlServer/
RoundTableMeeting.Repository.PostgreSql/
```

每个提供程序项目分别持有自己的 EF Core Provider、Migration 和 `IDatabaseInitializer` 实现。首版采用编译期选择：`RoundTableMeeting` 只引用并装配 `RoundTableMeeting.Repository.Sqlite`。未来切换数据库时，替换启动项目引用并生成对应部署包；需要同时支持多种数据库的运行时动态选择时，必须另行设计，首版不同时引用和扫描多套 Repository。只有多个提供程序之间出现真实可复用代码时，才提取共享 Repository 核心项目。

## 8. 数据库初始化

服务端配置使用 `appsettings.json` 和 `appsettings.{Environment}.json` 分层；Serilog 的最低级别、分类覆盖、Sink 和输出格式也通过配置管理。开发机秘密使用 user-secrets，生产秘密使用环境变量，秘密不得提交仓库。连接字符串和其他关键配置映射为 Options 并使用 `ValidateOnStart` 在启动时校验。运行环境统一取 `IWebHostEnvironment.EnvironmentName`。

第一版使用 EF Core Migration，不使用 `EnsureCreated` 替代迁移。

ASP.NET Core 启动时：

1. 创建应用服务作用域。
2. 解析 `IDatabaseInitializer`。
3. SQLite 实现确保数据库文件父目录存在。
4. 直接调用 `DbContext.Database.MigrateAsync(cancellationToken)` 应用未执行的 Migration；不得调用会吞掉异常的迁移辅助方法。
5. 初始化成功后才开放业务请求。

初始化失败直接终止服务启动并记录必要诊断信息，不能在数据库状态不确定时继续运行。日志不得记录密码、连接字符串或其他敏感信息。

默认数据库文件为 `data/round-table-meeting.db`，相对路径按 `AppContext.BaseDirectory` 归一化为稳定路径；数据库目录加入 `.gitignore`。连接字符串写入 `appsettings.json`，允许通过 `appsettings.{Environment}.json`、开发机 user-secrets 和生产环境变量覆盖。SQLite 启用 WAL 和 busy timeout，具体连接参数在实施文档中固定。首版以单服务实例运行作为自动迁移前提；未来多实例部署时重新评估迁移协调策略。

## 9. 依赖注入

- Application 中的应用服务使用 `IScopedDependency<T>`。
- SQLite Repository 中的领域仓储实现使用 `IScopedDependency<T>`。
- 工作单元实现使用 `IScopedDependency<IUnitOfWork>`。
- 数据库初始化实现使用适合启动作用域的 Scoped 生命周期。
- `DbContext`、数据库 Provider 和连接字符串由启动项目或 Repository 注册扩展显式配置。
- 自动注册只扫描 `RoundTableMeeting.Application` 和当前数据库 Repository 程序集；数据库技术配置（Options、拦截器和 DbContext）由当前数据库组合根显式注册，带 `IScopedDependency<T>` 标记的应用服务、初始化器、可用性检查器、工作单元和具体 Repository 由一次扫描注册。
- Domain 和 Abstraction 不参与实现类型扫描。

`IScopedDependency<T>` 的泛型参数 `T` 是容器注册时对外暴露的服务类型，例如实现类使用 `IScopedDependency<IUserService>` 时注册为 `IUserService -> 实现类`。

Materal 的 `AddAutoService` 必须接收明确的程序集清单，不依赖默认的全进程程序集扫描。ASP.NET Core 宿主调用 `AddMateralServiceProvider()`，由其内部使用 `MateralServiceContextProviderFactory` 接入 Materal/AspectCore 服务提供程序；不调用 TTA 的 `AddTTARepository` 或 `AddTTASqliteEFRepository` 扫描扩展，避免将宽泛的 `IRepository<TEntity, Guid>` 注册为 Application 可直接解析的服务。实现类同时注册多个服务时使用 Materal 的多泛型参数标记接口，不使用多个单参数标记接口。容器构建集成测试需要隔离服务集合和服务提供程序，验证 .NET 10 下的解析结果。

未来存在多套数据库 Repository 源码时，每个发布构建的 Web 组合根只引用并注册其中一套，避免同一个领域仓储或 `IDatabaseInitializer` 出现多个实现冲突。

## 10. Materal 能力复用

本项目只通过官方 NuGet 源引用已经发布的 Materal 包，不引用本地源码路径，也不复制源码。

- `Materal.TTA.Common`：提供 `IEntity<Guid>` 和 `IRepository<TEntity, Guid>`。
- `Materal.TTA.EFRepository`：提供 EF Core 通用仓储基础实现。
- `Materal.TTA.SqliteEFRepository`：提供 SQLite EF Core 仓储能力。
- `Materal.Extensions.DependencyInjection`：提供标记接口自动注册。
- `Materal.Extensions.DependencyInjection.AspNetCore`：提供 `AddMateralServiceProvider()` 和 ASP.NET Core 宿主容器集成。
- Materal.Utils 仅在代码出现明确工具需求时直接引用；由 TTA 传递引入不视为项目主动采用其全部能力。

Materal 包首版基线为：`Materal.TTA.Common`、`Materal.TTA.EFRepository`、`Materal.TTA.SqliteEFRepository` 使用 `1.4.7`，`Materal.Extensions.DependencyInjection` 和 `Materal.Extensions.DependencyInjection.AspNetCore` 使用 `1.4.4`。实现时通过 NuGet 还原和 .NET 10 集成测试确认后写入集中包版本配置；本主题不引入没有已确认用途的 MergeBlock。

自动注册集成方式以 Materal 源码中的 `AddMateralServiceProvider()` 和显式 `AddAutoService(assemblies)` 为准；前者内部使用 `MateralServiceContextProviderFactory` 和 AspectCore 服务提供程序，因此必须通过容器集成测试验证作用域、服务解析和 .NET 10 兼容性。不通过默认构建路径隐式扫描已加载程序集。

本项目不直接使用 TTA 的 `IUnitOfWork` 和 `EFUnitOfWorkImpl`。工作单元改进成熟后可以反向沉淀到 TTA，再评估是否切回 TTA 的新实现。

## 11. API、OpenAPI 与错误处理

- Controller 只负责请求绑定、调用应用服务和转换 HTTP 响应。
- Controller 不直接操作 `DbContext` 或实现业务规则。
- OpenAPI JSON 是 Kiota 的生成来源，首版固定输出 OpenAPI 3.0。
- Scalar 或同类 UI 只用于接口查看和交互。
- 所有 API 错误使用 `ProblemDetails`。
- 业务错误提供稳定的 HTTP 状态和业务错误标识。
- 字段校验错误提供客户端可定位的字段信息。
- 开发环境诊断信息受控开启，生产响应不得包含内部敏感信息。

跨端 JSON 错误契约固定为：

- `type`：标准 Problem Details 类型标识。
- `title`：错误类型的简短标题。
- `status`：HTTP 状态码。
- `detail`：可安全展示或记录的错误说明。
- `instance`：当前请求实例标识或路径。
- `code`：稳定的业务错误标识，使用字符串表达，不以本地化文案作为判断依据。
- `traceId`：请求关联标识，便于结合服务端日志定位。
- `errors`：仅字段校验错误使用，类型为 `Dictionary<string, string[]>`；其他错误省略该字段。

应用生成的错误响应必须提供 `code`。`detail`、`errors` 和 `traceId` 不得包含密码、连接字符串、绝对路径、堆栈或其他敏感内部信息。

服务端需要为扩展 ProblemDetails 建立明确的 OpenAPI schema，并在各个 operation 上声明可能返回的 4xx/5xx 错误响应，确保 Kiota 可以生成类型化错误模型。OpenAPI `servers` 省略或使用相对 `/`，不得把本机开发地址写入生成契约。未处理异常固定使用 `code = "internal_error"`。字段校验错误在输出前归一化模型级空键和嵌套字段路径，准确规则在实施文档中固定。

请求关联使用 W3C Trace Context。`traceId` 优先取 `Activity.Current?.TraceId`，无 Activity 时回退到 `HttpContext.TraceIdentifier`。未处理异常只由集中异常处理组件记录一次结构化日志；日志和响应均不得泄露敏感信息。

Serilog 使用 ASP.NET Core 请求日志中间件将单次 HTTP 请求压缩为一条结构化完成日志，并补充 `traceId`、请求方法、路由或路径、状态码和耗时。应用服务、Repository 和后台任务通过 `ILogger<T>` 写日志，不直接调用 Serilog 的静态 `Log` API；启动阶段允许使用 Serilog bootstrap logger 捕获宿主构建和数据库初始化失败。

并发冲突需要转换为明确的项目级异常，再由 Web 层映射为对应的 `ProblemDetails`。该约定只在业务实体明确配置 EF Core 并发令牌，或 EF Core 因更新行数为零检测到并发冲突时生效；不会把并发字段提前加入 `BaseEntity`。准确错误标识在实施设计中固定。

## 12. 系统状态接口

框架主题固定提供 `GET /api/system/status`。成功响应为：

```json
{
  "status": "available",
  "database": {
    "status": "available"
  },
  "environment": "Development"
}
```

`status` 取 `available | degraded`，`database.status` 取 `available | unavailable`，`environment` 取自 `IWebHostEnvironment.EnvironmentName`。运行期数据库故障时接口以 `200` 返回 `degraded`，数据库检查必须执行真实轻量查询；不返回恒为真的迁移完成字段。

接口不返回连接字符串、数据库绝对路径、环境变量、堆栈或机器账号。数据库 Migration 在启动阶段失败时，服务端立即退出且不开放状态接口，失败原因通过启动日志诊断。状态接口首版匿名访问，账号主题落地时重新评估鉴权；后续可以保留为内部健康信息，也可以由正式健康检查替代。

## 13. 客户端静态文件托管

生产环境由 `RoundTableMeeting` 托管 Vite 构建产物，并提供 React Router SPA 回退。SPA fallback 必须显式排除 `/api`、OpenAPI、Scalar 和其他服务端端点；未匹配的 `/api/*` 返回 ProblemDetails 404，不能使用默认 fallback 将未知 API 变成 HTML 200。

`dotnet publish` 必须自动触发或集成客户端生产构建，并将产物放入发布目录。发布环境必须安装 Node 24 LTS 与 pnpm，执行 `pnpm install --frozen-lockfile`；依赖安装或前端构建失败必须使 publish 失败，不能静默跳过。具体 MSBuild Target 在 `impl.md` 中确定。生产环境必须使用 HTTPS；localhost 开发环境允许 HTTP。

## 14. 服务端测试

- `RoundTableMeeting.Domain.Tests`：基础实体、领域规则和纯领域行为。
- `RoundTableMeeting.Application.Tests`：应用服务编排、仓储交互和工作单元调用。
- `RoundTableMeeting.Repository.Sqlite.Tests`：真实 SQLite 查询、状态注册、事务、映射、Migration 和数据库初始化。
- `RoundTableMeeting.IntegrationTests`：HTTP 管道、Materal 服务提供程序、OpenAPI、ProblemDetails、状态接口、未知 API 404 和静态文件托管；为 `WebApplicationFactory` 提供明确的宿主入口可见性。
- `RoundTableMeeting.ArchitectureTests`：项目引用、程序集依赖、Application 不注入宽泛 TTA Repository、Abstraction 不暴露 Domain 类型等禁止依赖规则。

Repository 测试不能使用开发数据库文件。每次测试使用独立 SQLite 数据库或等价隔离方式，不使用 EF Core InMemory 替代关系型行为测试。

Repository.Sqlite 使用仓库级 .NET Tool Manifest 固定 `dotnet-ef`。项目引用 `Microsoft.EntityFrameworkCore.Design`，Migration 放在 `RoundTableMeeting.Repository.Sqlite/Migrations/` 并提交仓库；生成命令必须显式指定 `--project` 为 Repository.Sqlite、`--startup-project` 为 RoundTableMeeting。测试覆盖 `QueryTrackingBehavior.NoTracking` 全局配置、普通查询不跟踪、显式 `AsTracking()` 的边界、整行更新、重复 Register 语义、成功提交后跟踪清理、迁移失败上抛、WAL/busy timeout 和独立数据库并发隔离。

## 15. 待在实施文档中固定

- `global.json` 锁定的 .NET 10 SDK 小版本、具体 NuGet 版本和兼容性验证结果。
- Abstraction 中 DTO、应用服务接口和其他技术契约的实际目录。
- `MateralServiceContextProviderFactory`、`AddAutoService` 的组合根代码和精确程序集清单。
- TTA 仓储基类、`DbContext` 和工作单元共享实例的具体注册代码。
- `Register*` 对重复注册、同主键实体和已跟踪实体的幂等或报错语义。
- 并发冲突的项目级异常类型和 `ProblemDetails` 错误标识。
- Options 类型、配置键、`ValidateOnStart` 规则，以及 Serilog 最低级别、分类覆盖、Console Sink、输出格式和请求日志补充字段。
- SQLite WAL、busy timeout 和真实轻量状态查询的具体实现。
- 服务端 OpenAPI HTTP 地址、服务就绪等待算法和 Kiota 生成脚本。
- `dotnet publish` 集成前端构建的 MSBuild 方式和工具链失败语义。
