# 系统框架服务端实现方案

## 1. 文档定位

本文把[服务端设计](design.md)具体化为工程创建命令、项目引用、包版本、目录、配置、组合根、数据访问、HTTP 管道、发布集成和运行命令。总体实施顺序以[系统框架实现方案](../impl.md)为准，验证方式以[测试方案](../testPlan.md)为准。

本文中的版本是 002 主题实施基线。实现时如官方包存在阻断性兼容问题，只能在验证后调整到明确版本，并同步更新本文、集中包版本文件和测试方案，不使用浮动版本规避问题。

## 2. 工具链和版本基线

### 2.1 .NET SDK

仓库根目录建立 `global.json`：

```json
{
  "sdk": {
    "version": "10.0.302",
    "rollForward": "latestPatch",
    "allowPrerelease": false
  }
}
```

所有生产项目目标框架统一为 `net10.0`，开启 nullable 和隐式 using。SDK 缺失时直接失败，不自动回退到 .NET 9。

### 2.2 NuGet 版本

在 `Server/Directory.Packages.props` 启用集中包版本管理。首版固定以下直接依赖：

| 包 | 版本 | 使用项目 |
| --- | --- | --- |
| `Materal.TTA.Common` | `1.4.7` | Domain |
| `Materal.TTA.EFRepository` | `1.4.7` | Repository.Sqlite |
| `Materal.TTA.SqliteEFRepository` | `1.4.7` | Repository.Sqlite |
| `Materal.Extensions.DependencyInjection` | `1.4.4` | Application、Repository.Sqlite |
| `Materal.Extensions.DependencyInjection.AspNetCore` | `1.4.4` | Web |
| `Microsoft.EntityFrameworkCore.Sqlite` | `10.0.11` | Repository.Sqlite |
| `Microsoft.EntityFrameworkCore.Design` | `10.0.11` | Repository.Sqlite |
| `Microsoft.AspNetCore.OpenApi` | `10.0.11` | Web |
| `Scalar.AspNetCore` | `2.17.1` | Web |
| `Serilog.AspNetCore` | `10.0.0` | Web |
| `Serilog.Settings.Configuration` | `10.0.1` | Web |
| `Serilog.Sinks.Console` | `6.1.1` | Web |
| `Serilog.Formatting.Compact` | `3.0.0` | Web |
| `xunit` | `2.9.3` | 所有 xUnit 测试项目 |
| `xunit.runner.visualstudio` | `4.0.0` | 所有 xUnit 测试项目 |
| `Microsoft.NET.Test.Sdk` | `18.9.0` | 所有测试项目 |
| `Microsoft.AspNetCore.Mvc.Testing` | `10.0.11` | IntegrationTests |
| `NetArchTest.Rules` | `1.3.2` | ArchitectureTests |

Materal TTA `1.4.7` 在 `net10.0` 下依赖 EF Core `10.0.8`；Repository.Sqlite 对 EF Core `10.0.11` 的直接引用目标是把同一依赖图统一到当前实施基线。完成实现后必须通过还原和集成测试确认兼容性，并检查没有同时加载多个 EF Core 补丁版本。

`Microsoft.EntityFrameworkCore.Design`、测试 runner 等只在需要的项目中设置 `PrivateAssets=all`，不让设计时和测试依赖进入发布产物。

### 2.3 .NET Tool Manifest

仓库根目录建立 `.config/dotnet-tools.json`，固定：

- `dotnet-ef`：`10.0.11`。
- `Microsoft.OpenApi.Kiota`：`1.34.1`。

统一使用以下命令恢复工具：

```powershell
dotnet tool restore
```

不得要求其他开发者预先安装全局 `dotnet-ef` 或 Kiota。

## 3. 创建解决方案和项目

### 3.1 创建命令

从仓库根目录执行：

```powershell
dotnet new sln --format slnx --name RoundTableMeeting --output Server

dotnet new webapi --use-controllers --no-openapi --framework net10.0 --name RoundTableMeeting --output Server/RoundTableMeeting
dotnet new classlib --framework net10.0 --name RoundTableMeeting.Application --output Server/RoundTableMeeting.Application
dotnet new classlib --framework net10.0 --name RoundTableMeeting.Domain --output Server/RoundTableMeeting.Domain
dotnet new classlib --framework net10.0 --name RoundTableMeeting.Abstraction --output Server/RoundTableMeeting.Abstraction
dotnet new classlib --framework net10.0 --name RoundTableMeeting.Repository.Sqlite --output Server/RoundTableMeeting.Repository.Sqlite

dotnet new xunit --framework net10.0 --name RoundTableMeeting.Domain.Tests --output Server/Tests/RoundTableMeeting.Domain.Tests
dotnet new xunit --framework net10.0 --name RoundTableMeeting.Application.Tests --output Server/Tests/RoundTableMeeting.Application.Tests
dotnet new xunit --framework net10.0 --name RoundTableMeeting.Repository.Sqlite.Tests --output Server/Tests/RoundTableMeeting.Repository.Sqlite.Tests
dotnet new xunit --framework net10.0 --name RoundTableMeeting.IntegrationTests --output Server/Tests/RoundTableMeeting.IntegrationTests
dotnet new xunit --framework net10.0 --name RoundTableMeeting.ArchitectureTests --output Server/Tests/RoundTableMeeting.ArchitectureTests
```

删除 Web API 模板自带的天气接口和示例模型，不保留与本主题无关的模板代码。

### 3.2 加入解决方案

```powershell
dotnet sln Server/RoundTableMeeting.slnx add Server/RoundTableMeeting/RoundTableMeeting.csproj
dotnet sln Server/RoundTableMeeting.slnx add Server/RoundTableMeeting.Application/RoundTableMeeting.Application.csproj
dotnet sln Server/RoundTableMeeting.slnx add Server/RoundTableMeeting.Domain/RoundTableMeeting.Domain.csproj
dotnet sln Server/RoundTableMeeting.slnx add Server/RoundTableMeeting.Abstraction/RoundTableMeeting.Abstraction.csproj
dotnet sln Server/RoundTableMeeting.slnx add Server/RoundTableMeeting.Repository.Sqlite/RoundTableMeeting.Repository.Sqlite.csproj
dotnet sln Server/RoundTableMeeting.slnx add Server/Tests/RoundTableMeeting.Domain.Tests/RoundTableMeeting.Domain.Tests.csproj
dotnet sln Server/RoundTableMeeting.slnx add Server/Tests/RoundTableMeeting.Application.Tests/RoundTableMeeting.Application.Tests.csproj
dotnet sln Server/RoundTableMeeting.slnx add Server/Tests/RoundTableMeeting.Repository.Sqlite.Tests/RoundTableMeeting.Repository.Sqlite.Tests.csproj
dotnet sln Server/RoundTableMeeting.slnx add Server/Tests/RoundTableMeeting.IntegrationTests/RoundTableMeeting.IntegrationTests.csproj
dotnet sln Server/RoundTableMeeting.slnx add Server/Tests/RoundTableMeeting.ArchitectureTests/RoundTableMeeting.ArchitectureTests.csproj
```

### 3.3 项目引用

生产项目引用固定为：

```powershell
dotnet add Server/RoundTableMeeting/RoundTableMeeting.csproj reference Server/RoundTableMeeting.Application/RoundTableMeeting.Application.csproj
dotnet add Server/RoundTableMeeting/RoundTableMeeting.csproj reference Server/RoundTableMeeting.Abstraction/RoundTableMeeting.Abstraction.csproj
dotnet add Server/RoundTableMeeting/RoundTableMeeting.csproj reference Server/RoundTableMeeting.Repository.Sqlite/RoundTableMeeting.Repository.Sqlite.csproj

dotnet add Server/RoundTableMeeting.Application/RoundTableMeeting.Application.csproj reference Server/RoundTableMeeting.Domain/RoundTableMeeting.Domain.csproj
dotnet add Server/RoundTableMeeting.Application/RoundTableMeeting.Application.csproj reference Server/RoundTableMeeting.Abstraction/RoundTableMeeting.Abstraction.csproj

dotnet add Server/RoundTableMeeting.Repository.Sqlite/RoundTableMeeting.Repository.Sqlite.csproj reference Server/RoundTableMeeting.Domain/RoundTableMeeting.Domain.csproj
dotnet add Server/RoundTableMeeting.Repository.Sqlite/RoundTableMeeting.Repository.Sqlite.csproj reference Server/RoundTableMeeting.Abstraction/RoundTableMeeting.Abstraction.csproj
```

测试项目引用：

- Domain.Tests 只引用 Domain。
- Application.Tests 引用 Application、Domain 和 Abstraction。
- Repository.Sqlite.Tests 引用 Repository.Sqlite、Domain 和 Abstraction。
- IntegrationTests 引用 Web，并通过 Web 的传递依赖访问实际组合根；只在编写替身时增加必要项目引用。
- ArchitectureTests 引用全部生产项目，用于加载和检查程序集。

Application 不得添加 EF Core、Repository.Sqlite 或 Web 的 ProjectReference。Domain 和 Abstraction 不得引用其他本项目生产程序集。

## 4. 服务端目录结构

框架主题实施后的有效目录建议为：

```text
Server/
  Directory.Build.props
  Directory.Packages.props
  RoundTableMeeting.slnx
  RoundTableMeeting/
    Controllers/
    Errors/
    Hosting/
    OpenApi/
    Services/
    Program.cs
    appsettings.json
    appsettings.Development.json
  RoundTableMeeting.Application/
    AssemblyMarker.cs
    SystemStatus/
  RoundTableMeeting.Domain/
    Entities/
    Exceptions/
    Repositories/
    UnitOfWork/
  RoundTableMeeting.Abstraction/
    Contracts/
    Persistence/
    Runtime/
    Services/
  RoundTableMeeting.Repository.Sqlite/
    Configuration/
    Database/
      RoundTableMeetingDbContextFactory.cs
    Migrations/
    Repositories/
    UnitOfWork/
    AssemblyMarker.cs
  Tests/
```

只创建有文件的目录。框架主题不创建 User、Meeting、Message 或 Agent 等占位实体。

`Server/Directory.Build.props` 至少统一：`TargetFramework`、`Nullable=enable`、`ImplicitUsings=enable`、`TreatWarningsAsErrors` 的项目策略和可重复构建属性。若第三方包在实施期产生不可控警告，应精确抑制对应编号，不关闭全部警告。

## 5. Domain 和 Abstraction 实现

### 5.1 BaseEntity

`RoundTableMeeting.Domain/Entities/BaseEntity.cs` 实现：

```csharp
public abstract class BaseEntity : IEntity<Guid>
{
    public Guid ID { get; protected set; } = Guid.NewGuid();
}
```

构造时生成非空 Guid，ID 不提供公开 setter。测试可通过专用派生类验证 ID；生产代码不得为了测试开放主键修改入口。

### 5.2 领域仓储

当后续主题引入聚合时，在 `Domain/Repositories/` 建立：

```csharp
public interface IXxxRepository : IRepository<Xxx, Guid>
{
}
```

框架主题只建立约定，不创建假的 `IXxxRepository`。Application 构造函数只能依赖具体领域仓储接口，禁止注入 `IRepository<TEntity, Guid>`、`DbContext`、`DbSet<T>` 或 `IQueryable<T>`。

### 5.3 IUnitOfWork

`Domain/UnitOfWork/IUnitOfWork.cs` 固定为：

```csharp
public interface IUnitOfWork
{
    void RegisterAdd(IEntity<Guid> entity);

    void RegisterEdit(IEntity<Guid> entity);

    void RegisterDelete(IEntity<Guid> entity);

    Task<int> CommitAsync(CancellationToken cancellationToken = default);
}
```

Domain 同时定义不依赖 EF Core 的 `ConcurrencyConflictException`。Repository.Sqlite 捕获 `DbUpdateConcurrencyException` 后转换为该异常并保留原异常为 InnerException；Web 将它映射为 `409` 和 `code = "concurrency_conflict"`。

### 5.4 Abstraction 契约

框架主题需要以下最小契约：

- `Persistence/IDatabaseInitializer`：启动时初始化当前数据库。
- `Persistence/IDatabaseAvailabilityChecker`：执行运行期真实轻量数据库检查，返回可用或不可用，不暴露 Provider 类型。
- `Runtime/IRuntimeEnvironment`：提供当前环境名，由 Web 宿主实现。
- `Contracts/SystemStatusDto` 和 `DatabaseStatusDto`：保存状态接口的传输模型。
- `Services/ISystemStatusService`：由 Application 实现，组合数据库状态和运行环境。

Abstraction 不能引用 Domain；这些接口和 DTO 不得出现 `DbContext`、`SqliteConnection`、领域实体或 Materal Repository 类型。

## 6. SQLite Repository 实现

### 6.1 数据库配置

`appsettings.json` 固定配置键：

```json
{
  "Database": {
    "ConnectionString": "Data Source=data/round-table-meeting.db;Mode=ReadWriteCreate;Default Timeout=5;Foreign Keys=True;Pooling=True"
  }
}
```

`SqliteDatabaseOptions` 位于 Repository.Sqlite 的 `Configuration/`，SectionName 固定为 `Database`。注册时执行 `ValidateOnStart`，至少验证：

- ConnectionString 非空。
- Provider 能用 `SqliteConnectionStringBuilder` 解析。
- DataSource 非空且不是内存数据库。
- Default Timeout 大于 0。

默认相对 DataSource 由单一纯函数按 `AppContext.BaseDirectory` 转换为绝对路径后再交给 `UseSqlite`。配置覆盖顺序使用 ASP.NET Core 默认顺序：`appsettings.json`、`appsettings.{Environment}.json`、Development user-secrets、环境变量、命令行。测试必须通过环境变量或测试配置 Provider 显式覆盖为随机绝对临时路径，不允许继承默认开发库；发布目录默认需要对 `data/` 具有创建和写入权限，若部署目录只读则必须用 `Database__ConnectionString` 指向可写持久化目录。日志、状态响应和异常响应不得输出归一化后的绝对路径或完整连接字符串。

环境覆盖使用 `Database__ConnectionString`。开发和测试可以覆盖数据库路径，生产秘密不得提交到 appsettings。并行测试为每个工厂/集合生成唯一目录，teardown 释放所有 scope 和连接、清理 SQLite 连接池后再删除目录。

### 6.2 DbContext

`RoundTableMeetingDbContext` 位于 `Database/`，继承 EF Core `DbContext`。注册方式使用显式 `AddDbContext<RoundTableMeetingDbContext>`，不能调用 `AddTTARepository` 或 `AddTTASqliteEFRepository`。

该类型声明为 sealed，只承载生产模型和 Migration。Repository.Sqlite.Tests 使用独立的 `TestDbContext` 与测试实体验证通用状态逻辑，不通过继承生产上下文向生产模型注入测试映射。`TestDbContext` 必须复用生产的 Options 配置构建函数，至少保持全局 NoTracking 和同一个 SQLite 连接拦截器；测试类型不进入生产 Migration。

配置至少包含：

```csharp
options.UseSqlite(normalizedConnectionString);
options.UseQueryTrackingBehavior(QueryTrackingBehavior.NoTracking);
options.AddInterceptors(sqliteConnectionInterceptor);
options.EnableDetailedErrors(environment.IsDevelopment());
options.EnableSensitiveDataLogging(false);
```

所有环境都关闭 SensitiveDataLogging。普通仓储查询依赖全局 NoTracking；只有后续业务设计明确说明时才允许局部 `AsTracking()`。

`SqliteConnectionInterceptor` 继承 `DbConnectionInterceptor`，在每次同步或异步打开 SQLite 物理连接后执行 `PRAGMA busy_timeout = 5000;`。拦截器不持有 DbContext 或作用域状态，按 Singleton 注册并显式加入 DbContext Options。这样 busy timeout 对连接池中的每次连接使用都可验证，不依赖启动初始化连接。`Default Timeout=5` 是 ADO.NET 命令超时（秒），`busy_timeout=5000` 是 SQLite 锁等待超时（毫秒），二者不是同一语义；保留前者是为了限制命令最长执行时间，具体锁等待行为由后者控制。

`OnModelCreating` 使用当前 Repository.Sqlite 程序集中的 `IEntityTypeConfiguration<T>` 自动应用映射。Migration 程序集固定为 Repository.Sqlite。

### 6.3 Repository 基类

Repository.Sqlite 建立项目自己的通用基类：

```csharp
public abstract class SqliteRepository<TEntity>(RoundTableMeetingDbContext dbContext)
    : SqliteEFRepositoryImpl<TEntity, Guid, RoundTableMeetingDbContext>(dbContext)
    where TEntity : class, IEntity<Guid>, new()
{
}
```

后续具体实现采用：

```csharp
public sealed class XxxRepository(RoundTableMeetingDbContext dbContext)
    : SqliteRepository<Xxx>(dbContext), IXxxRepository, IScopedDependency<IXxxRepository>
{
}
```

该基类只复用 TTA 查询能力。不得向领域仓储添加 Add、Update、Delete 或自动保存方法，也不得把 `RoundTableMeetingDbContext` 暴露给 Application。

TTA 的 SQLite Repository 基类带有 `new()` 约束，因此后续由该基类承载的具体领域实体必须提供 public 无参构造函数。该构造函数可以只用于 ORM/Repository 创建，领域正常创建仍通过有语义的构造方法或工厂完成；不得为了满足约束开放实体属性写入口。

### 6.4 工作单元状态语义

工作单元实现命名为 `UnitOfWorkImpl`，实现 `IUnitOfWork` 和 `IScopedDependency<IUnitOfWork>`，构造函数只注入当前 Scoped `RoundTableMeetingDbContext`。不依赖具体业务模型的状态逻辑放入 internal 泛型核心 `EfUnitOfWorkCore<TDbContext>`；生产 `UnitOfWorkImpl` 组合该核心，Repository.Sqlite.Tests 通过 `InternalsVisibleTo` 使用测试专用 `TestDbContext` 和测试实体验证同一套逻辑。生产对外只注册 `IUnitOfWork`，测试类型不进入生产 Migration。

三个 Register 方法均先执行：

1. 参数为空时抛出 `ArgumentNullException`。
2. 按运行时实体类型和 Guid 主键检查 ChangeTracker.Local；同主键已有不同对象实例时抛出 `InvalidOperationException`，不自动覆盖或合并。
3. 根据当前 EntityState 执行下表，不调用 `SaveChanges`。

| 当前状态 | RegisterAdd | RegisterEdit | RegisterDelete |
| --- | --- | --- | --- |
| Detached | 设为 Added | Attach 后设为 Modified | Attach 后设为 Deleted |
| Added | 幂等，不改变 | 保持 Added，新实体修改随插入提交 | 取消新增并设为 Detached |
| Unchanged | 抛出冲突异常 | 设为 Modified | 设为 Deleted |
| Modified | 抛出冲突异常 | 幂等，不改变 | 设为 Deleted |
| Deleted | 抛出冲突异常 | 抛出冲突异常 | 幂等，不改变 |

`RegisterEdit` 对 NoTracking 查询得到的实体采用 `Detached -> Modified`，默认整行更新。后续需要选择性更新时另行设计，首版不推断属性差异。

`CommitAsync` 语义：

1. 只调用一次 `SaveChangesAsync(cancellationToken)`，依赖 EF Core 对单次 SaveChanges 的默认事务边界。
2. 返回受影响记录数；无待保存实体时返回 0。
3. 不捕获并吞掉取消或普通 `DbUpdateException`。
4. 将 `DbUpdateConcurrencyException` 转换为 `ConcurrencyConflictException`。
5. 无论提交成功、普通失败、并发失败或取消，Commit 尝试完成后都在 `finally` 中调用 `ChangeTracker.Clear()`，避免当前 scope 留下半失败状态。失败或取消仍然原样上抛；调用方不得在同一 scope 内重试或继续使用该工作单元/DbContext，必须废弃当前 scope 并创建新的 scope。这样清理是失败后的 scope 终止语义，不表示提交成功。
6. 不允许同一工作单元被并发调用，不在内部使用锁掩盖 DbContext 的错误用法。

一次应用用例通常只调用一次 Commit。需要多阶段事务的业务出现时再扩展接口，不在框架主题加入显式 BeginTransaction。

### 6.5 Migration 和初始化器

Migration 创建命令：

```powershell
dotnet tool restore
dotnet tool run dotnet-ef migrations add InitialCreate --project Server/RoundTableMeeting.Repository.Sqlite/RoundTableMeeting.Repository.Sqlite.csproj --startup-project Server/RoundTableMeeting/RoundTableMeeting.csproj --context RoundTableMeetingDbContext --output-dir Migrations
```

首条 Migration 即使没有业务表，也要建立可验证的 Migration 历史和初始化链路。不得使用 `EnsureCreated`。

Repository.Sqlite 实现 `IDesignTimeDbContextFactory<RoundTableMeetingDbContext>`。设计时工厂复用相同的 SQLite Provider、Migration Assembly 和 NoTracking 配置，但不构建 Web Host、不解析 Application 服务，也不调用 `IDatabaseInitializer`。工厂默认使用不含秘密的设计时连接字符串；允许通过 `ROUND_TABLE_MEETING_DESIGN_TIME_CONNECTION` 覆盖。这样 `dotnet ef migrations add/remove` 不会意外启动服务、写入开发数据库或执行运行时自动迁移。

`SqliteDatabaseInitializer` 与 SQLite 连接配置的顺序固定为：

1. 从规范化连接字符串取得数据库文件父目录并创建目录。
2. 打开 `RoundTableMeetingDbContext` 当前使用的 SQLite 连接，在同一个连接上先执行 `PRAGMA journal_mode = WAL;` 并确认返回值为 `wal`，再调用该 DbContext 的 `Database.MigrateAsync(cancellationToken)`；WAL 是数据库文件级模式，`busy_timeout` 仍由连接拦截器在每次物理连接打开时设置。
3. 在迁移连接上查询 `PRAGMA busy_timeout;`，确认连接拦截器已设置为 5000 毫秒。
4. 关闭迁移连接并调用 `SqliteConnection.ClearAllPools()`，使后续连接重新按拦截器配置打开；再用独立新连接复核 WAL 和 busy timeout。
5. 记录不含路径和连接字符串的成功日志。

任何一步失败都向外抛出。Web 在开始监听请求前创建 scope 并调用初始化器；失败时 bootstrap logger 记录一次关键错误，进程返回非零。初始化器和数据库可用性检查分别实现 `IScopedDependency<IDatabaseInitializer>`、`IScopedDependency<IDatabaseAvailabilityChecker>`，由 Materal 标记接口自动注册为 Scoped；组合根不得再次显式注册同一实现。

### 6.6 运行期数据库检查

`SqliteDatabaseAvailabilityChecker` 实现 `IDatabaseAvailabilityChecker`。每次检查使用当前请求 scope 的 DbContext 连接执行 `SELECT 1`，设置短取消超时，并在连接打开后确认命令成功。

调用方取消令牌触发的 `OperationCanceledException` 继续上抛；其他数据库访问失败记录 Warning 级结构化日志并返回不可用。日志和结果不得返回异常正文、数据库路径或连接字符串。状态检查不执行 Migration、不修改 schema，也不把 `GetPendingMigrationsAsync` 当作可用性检查。

## 7. Application 和依赖注入

### 7.1 系统状态应用服务

`SystemStatusService` 位于 Application，实现 `ISystemStatusService` 和 `IScopedDependency<ISystemStatusService>`，注入 `IDatabaseAvailabilityChecker` 和 `IRuntimeEnvironment`。

映射规则固定为：

- 数据库可用：`status = "available"`，`database.status = "available"`。
- 数据库不可用：`status = "degraded"`，`database.status = "unavailable"`。
- `environment` 原样使用宿主 `IWebHostEnvironment.EnvironmentName`，不得由客户端传入。

### 7.2 组合根

Web 中建立扩展方法，例如 `AddRoundTableMeetingServices`。核心注册顺序：

```csharp
builder.AddMateralServiceProvider();

builder.Services.AddAutoService(
    typeof(ApplicationAssemblyMarker).Assembly,
    typeof(SqliteRepositoryAssemblyMarker).Assembly);

builder.Services.AddRoundTableMeetingSqlite(
    builder.Configuration,
    builder.Environment);

builder.Services.AddSingleton<IRuntimeEnvironment>(
    new RuntimeEnvironment(builder.Environment.EnvironmentName));
```

`AddRoundTableMeetingSqlite` 是当前数据库提供程序的唯一技术配置扩展，内部一次性完成 `SqliteDatabaseOptions` 的 `Configure/ValidateOnStart`、Singleton `SqliteConnectionInterceptor` 和 Scoped `RoundTableMeetingDbContext` 注册。`UnitOfWorkImpl`、`SqliteDatabaseInitializer`、`SqliteDatabaseAvailabilityChecker` 以及后续具体 Repository 通过各自的 `IScopedDependency<T>` 标记由上方一次 `AddAutoService` 扫描注册；扩展方法不重复调用 `AddAutoService`，也不显式重复注册这些服务或测试类型。测试替换通过 `WebApplicationFactory` 的测试服务配置完成。

必须显式传入 Application 和当前 SQLite Repository 程序集。Domain、Abstraction 和 Web 不加入自动扫描列表；Web 技术服务手工注册。不得调用 Materal 的默认全程序集构建扫描，也不得调用 TTA 的 Repository 扫描扩展。

容器测试必须验证：

- `ISystemStatusService`、`IUnitOfWork`、`IDatabaseInitializer` 和 `IDatabaseAvailabilityChecker` 可解析且为 Scoped。
- 同一 scope 内工作单元、数据库初始化器和可用性检查服务取得同一个 DbContext 实例；后续每个具体 Repository 也必须满足该约束。
- 不同 scope 取得不同 DbContext。
- `IRepository<TEntity, Guid>` 不会作为宽泛服务被 Application 解析。

## 8. ProblemDetails 和异常处理

### 8.1 API 错误模型

Web 定义用于运行时和 OpenAPI 的 `ApiProblemDetails : ProblemDetails`，增加：

```csharp
public required string Code { get; init; }

public required string TraceId { get; init; }

public IReadOnlyDictionary<string, string[]>? Errors { get; init; }
```

JSON 使用 camelCase。`errors` 为空时省略。`type` 使用稳定相对 URI `/problems/{code}`，仅供诊断展示，客户端不得依赖或解析该 URI；客户端分支使用 `code` 或 HTTP 状态。`instance` 使用请求 Path，不包含 QueryString。

首版固定错误映射：

| 场景 | HTTP | code |
| --- | --- | --- |
| 模型或字段校验失败 | 400 | `validation_failed` |
| 未匹配的 API 路由 | 404 | `route_not_found` |
| EF 并发冲突 | 409 | `concurrency_conflict` |
| 未处理异常 | 500 | `internal_error` |

后续业务错误由对应主题增加稳定 code，不以中文或英文错误文案作为客户端判断条件。

### 8.2 字段路径归一化

模型校验错误在输出前按以下规则归一化：

1. 空或纯空白 key 转为 `$`。
2. 去掉一个开头的 `$.`。
3. 第一个属性段使用当前 JSON camelCase 命名策略转换；后续点号路径和数组下标保持原结构。
4. 相同归一化 key 的消息合并、去重并保持原顺序。
5. 空错误消息替换为安全的通用校验文案，不输出异常正文。

例如 `Request.Items[0].Name` 转为 `request.Items[0].Name`；后续如果需要完整 JSON 路径映射，由引入复杂请求模型的业务主题扩展并补测试。

### 8.3 traceId 和集中异常

进程启动最早阶段设置：

```csharp
Activity.DefaultIdFormat = ActivityIdFormat.W3C;
Activity.ForceDefaultIdFormat = true;
```

traceId 获取顺序如下。该逻辑提取为独立的 `TraceIdResolver` 静态方法或等价无状态服务，以便在没有 `Activity.Current` 的单元测试中直接验证：

1. `Activity.Current?.TraceId.ToString()`。
2. `HttpContext.TraceIdentifier`。

使用 ASP.NET Core `IExceptionHandler` 或等价集中组件处理异常。只有集中异常处理记录未处理异常；Controller、Application 和 Repository 在继续抛出同一异常时不得重复写 Error 日志。

生产环境 `internal_error` 的 detail 使用固定安全文案。开发环境也不直接把堆栈写入响应，堆栈只进入受控日志。

## 9. Serilog 实现

### 9.1 包和配置

Web 使用 Serilog bootstrap logger 捕获 Host 构建、配置校验和 Migration 失败。Host 建立后使用 `ReadFrom.Configuration` 替换 bootstrap 配置；业务类只注入 `ILogger<T>`，禁止直接调用静态 `Log`。

`appsettings.json` 至少包含：

```json
{
  "Serilog": {
    "Using": [
      "Serilog.Sinks.Console",
      "Serilog.Formatting.Compact"
    ],
    "MinimumLevel": {
      "Default": "Information",
      "Override": {
        "Microsoft.AspNetCore": "Warning",
        "Microsoft.EntityFrameworkCore.Database.Command": "Warning"
      }
    },
    "Enrich": [
      "FromLogContext"
    ],
    "WriteTo": [
      {
        "Name": "Console",
        "Args": {
          "formatter": "Serilog.Formatting.Compact.RenderedCompactJsonFormatter, Serilog.Formatting.Compact"
        }
      }
    ]
  }
}
```

Development 可把部分框架类别提高到 Information，但不得开启 EF SensitiveDataLogging。

### 9.2 请求日志

使用 `UseSerilogRequestLogging`，每个 HTTP 请求产生一条完成日志。EnrichDiagnosticContext 至少补充：

- `traceId`。
- `requestMethod`。
- `requestPath`，不包含 QueryString。
- `routeName` 或 endpoint display name，无法解析时省略。
- `statusCode`。
- `elapsedMilliseconds`。

请求体、响应体、Cookie、Authorization、密码、Token、连接字符串、完整聊天消息、AI Prompt 和文件内容不进入日志。未来业务只能记录消息 ID、任务 ID、会议 ID 等必要标识，不能记录完整内容。

## 10. Controller、OpenAPI 和状态接口

### 10.1 Controller

`SystemController` 使用 `[ApiController]` 和 `[Route("api/system")]`，只注入 `ISystemStatusService`。固定端点：

```csharp
[HttpGet("status")]
[ProducesResponseType(typeof(SystemStatusDto), StatusCodes.Status200OK)]
[ProducesResponseType(typeof(ApiProblemDetails), StatusCodes.Status500InternalServerError, "application/problem+json")]
public async Task<ActionResult<SystemStatusDto>> GetStatusAsync(
    CancellationToken cancellationToken)
```

成功和数据库降级都返回 200；服务未启动或网络断开由客户端识别为不可访问。Controller 不访问 DbContext，也不捕获普通异常生成自定义 JSON。

### 10.2 OpenAPI

注册文档名 `v1`，HTTP 地址固定为：

- `http://localhost:5043/openapi/v1.json`。
- HTTPS 等价地址 `https://localhost:7043/openapi/v1.json`。

OpenAPI `OpenApiVersion` 显式设置为 `OpenApi3_0`，并省略 `servers` 或仅使用相对 `/`，不得把本机开发地址写入文档。Scalar 路径固定为 `/scalar/v1`，读取同一 `/openapi/v1.json`。

为 `ApiProblemDetails`、字段错误字典和状态 DTO 建立可生成的 schema。每个 operation 用 attribute、convention 或 operation transformer 显式声明可能的错误响应；不能只依赖全局 schema 却不把响应挂到 operation。

IntegrationTests 需要解析 JSON 并验证：

- 根字段 `openapi` 以 `3.0` 开头。
- `/api/system/status` 存在。
- 错误响应引用包含 `code`、`traceId` 和 `errors` 的 schema。
- 文档中不出现开发机绝对路径或连接字符串。

### 10.3 未知 API 和 SPA fallback

Controller、OpenAPI 和 Scalar 映射后，增加专用的 `/api/{**path}` fallback，返回 `ApiProblemDetails` 404 和 `route_not_found`。随后才添加 SPA fallback。

SPA fallback 只服务 GET/HEAD 且接受 HTML 的非服务端路径。以下前缀永远不允许回退到 `index.html`：

- `/api`。
- `/openapi`。
- `/scalar`。

非 HTML 请求和非 GET/HEAD 请求使用普通 404 ProblemDetails 或空 404，不返回 React 页面。

## 11. Program 启动顺序

`Program.cs` 按以下顺序组织：

1. 设置 W3C Activity 格式并创建 Serilog bootstrap logger。
2. 创建 `WebApplicationBuilder`。
3. 调用 `builder.AddMateralServiceProvider()`。
4. 用配置接入 Serilog。
5. 注册 Controller、ProblemDetails、异常处理、OpenAPI、Application、SQLite Repository 和 RuntimeEnvironment。
6. 构建 WebApplication。
7. 创建启动 scope，解析 `IDatabaseInitializer` 并等待 `InitializeAsync`；失败时记录 Critical 并终止进程。
8. 配置集中异常处理和 Serilog 请求日志。
9. 非 Development 环境启用 HTTPS 重定向和生产安全策略；localhost Development 允许 HTTP 工具链访问。
10. 配置静态文件、Controller、OpenAPI、Scalar、API fallback 和 SPA fallback。
11. 调用 `RunAsync`。

为了支持 `WebApplicationFactory<Program>`，在文件末尾声明 `public partial class Program;` 或等价可见入口。阶段 0/1 必须先完成一个最小测试宿主探针，验证 Materal/AspectCore 服务提供程序下 `ConfigureTestServices` 能替换临时数据库配置和故障服务、同一 scope 的 Scoped 实例保持一致、不同 scope 相互隔离，并且测试可以捕获 Serilog 事件。若该探针失败，不得继续依赖未经验证的服务替换方案，需先调整组合根或测试宿主接入方式并同步本文。

I-005/I-006 使用 IntegrationTests 中派生的 `WebApplicationFactory`，在 `ConfigureTestServices` 中把测试程序集的 Controller 作为 MVC ApplicationPart 注入。测试 Controller 使用 `[ApiController]`、`[ApiExplorerSettings(IgnoreApi = true)]`：`GET /__test/throw` 确定性抛出未处理异常，`POST /__test/validation` 绑定测试程序集内带 `[Required]` 字段的请求模型。它们复用生产的 `MapControllers`、模型校验、异常处理和 ProblemDetails 管道，不替换整个应用管道。临时端点不得写入生产 `Program.cs`，不得被生产程序集引用，不进入生产发布产物或任何 OpenAPI 文档。

`launchSettings.json` 固定 Development 地址：

- HTTP：`http://localhost:5043`。
- HTTPS：`https://localhost:7043`。

Vite 代理和 Kiota 生成默认使用 HTTP 5043，避免本地开发证书成为自动化前提；浏览器直接访问服务端时可以使用 HTTPS 7043。生产部署只允许 HTTPS。

## 12. ASP.NET Core 托管客户端

### 12.1 MSBuild 属性

Web 项目定义：

```xml
<PropertyGroup>
  <SpaRoot>$(MSBuildProjectDirectory)\..\..\Client\roundTableMeeting\</SpaRoot>
  <SpaDist>$(SpaRoot)dist\</SpaDist>
</PropertyGroup>
```

### 12.2 Publish Target

在计算发布文件前完成客户端安装和构建，再把产物纳入发布文件列表。实际 MSBuild Target 以 P-001/P-007 的干净目录、旧 dist、重复 publish 和失败回滚探针为准；若 SDK 行为证明 `BeforeTargets=ComputeFilesToPublish` 仍不能稳定纳入当前列表，必须改为验证通过的 `DependsOnTargets` 或专用 publish target。当前示例是待探针确认的首选实现：

```xml
<Target Name="BuildClient" BeforeTargets="ComputeFilesToPublish">
  <Exec WorkingDirectory="$(SpaRoot)" Command="pnpm install --frozen-lockfile" />
  <Exec WorkingDirectory="$(SpaRoot)" Command="pnpm build" />
  <ItemGroup>
    <ClientDistFiles Include="$(SpaDist)**\*" />
    <ResolvedFileToPublish Include="@(ClientDistFiles->'%(FullPath)')" Exclude="@(ResolvedFileToPublish)">
      <RelativePath>wwwroot\%(RecursiveDir)%(Filename)%(Extension)</RelativePath>
      <CopyToPublishDirectory>PreserveNewest</CopyToPublishDirectory>
    </ResolvedFileToPublish>
  </ItemGroup>
</Target>
```

最终实现必须确认 glob 能包含嵌套资源且不会把源码或 `node_modules` 复制到发布目录。Client 目录、pnpm、锁文件或构建任一缺失都使 publish 失败，不设置静默跳过条件。

Web 源项目不提交客户端 `dist/` 到自身 `wwwroot`；publish 时动态收集，开发环境仍由 Vite 独立运行。

## 13. 配置和秘密

配置层级：

1. `appsettings.json` 保存非秘密默认值。
2. `appsettings.{Environment}.json` 保存环境差异。
3. Development user-secrets 保存开发秘密。
4. 生产使用环境变量或部署系统的 Secret Provider。

首版至少校验 Database 配置。未来认证、MCP 或外部服务配置由首次使用它们的业务主题增加。任何 secret 都不能出现在日志、ProblemDetails、OpenAPI example、测试快照或客户端构建变量中。

## 14. 运行和维护命令

### 14.1 还原和构建

```powershell
dotnet tool restore
dotnet restore Server/RoundTableMeeting.slnx
dotnet build Server/RoundTableMeeting.slnx --configuration Debug --no-restore
```

### 14.2 启动服务端

```powershell
dotnet run --project Server/RoundTableMeeting/RoundTableMeeting.csproj --launch-profile http
```

验证地址：

- `http://localhost:5043/api/system/status`。
- `http://localhost:5043/openapi/v1.json`。
- `http://localhost:5043/scalar/v1`。

### 14.3 Migration

新增 Migration 必须显式指定 Repository 和 startup project：

```powershell
dotnet tool run dotnet-ef migrations add <MigrationName> --project Server/RoundTableMeeting.Repository.Sqlite/RoundTableMeeting.Repository.Sqlite.csproj --startup-project Server/RoundTableMeeting/RoundTableMeeting.csproj --context RoundTableMeetingDbContext --output-dir Migrations
```

删除最近未发布 Migration 时同样显式指定项目。正常运行不要求执行 `database update`，应用启动会自动迁移。

### 14.4 测试

```powershell
dotnet test Server/RoundTableMeeting.slnx --configuration Debug --no-build
```

需要隔离定位时直接运行对应测试项目，不使用开发数据库。

### 14.5 生产发布

```powershell
dotnet publish Server/RoundTableMeeting/RoundTableMeeting.csproj --configuration Release --output artifacts/publish
```

发布验证从 `artifacts/publish` 启动 `RoundTableMeeting`，不要依赖 Vite 开发服务器。应用自身 TLS 与反向代理终止 TLS 是两种部署方式：本主题验收使用测试 HTTPS 证书和独立 HTTPS 端口；生产若由反向代理终止 TLS，则由部署配置负责只允许 HTTPS 并正确转发协议头。不得把开发证书信任作为发布验收前提。

## 15. 服务端完成标准

服务端实现完成时必须证明：

1. 解决方案全部项目能还原、构建和测试。
2. 项目引用和程序集依赖符合分层约束。
3. Materal 显式程序集扫描在 .NET 10 下正确注册 Scoped 服务，未暴露宽泛 Repository。
4. SQLite 空库能自动迁移，默认 NoTracking、WAL、busy timeout 和工作单元语义通过真实 SQLite 测试。
5. Migration 失败使服务启动失败；运行期数据库失败使状态接口返回降级结果。
6. Serilog 输出结构化请求日志，traceId 与 ProblemDetails 可关联且不泄露敏感信息。
7. OpenAPI 3.0、Scalar、ProblemDetails schema 和显式错误响应可通过 HTTP 获取。
8. 未知 API 返回 JSON 404，SPA fallback 不掩盖服务端端点。
9. `dotnet publish` 自动构建并包含客户端资源，前端失败会使发布失败。
10. 框架服务端没有账号、会议、消息或 AI 的占位业务实现。
