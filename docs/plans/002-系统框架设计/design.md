# 系统框架设计

## 1. 主题定位

本主题建立圆桌会议网站后续业务主题共同依赖的工程基础。目标不是提前实现账号、会议、消息或 AI 协作，而是形成一套可启动、可联调、可测试、可扩展的客户端、服务端和数据库框架。

本文所说的“最小应用外壳”仅指框架主题提供的客户端加载、基础路由和系统状态验收入口，不包含账号认证、权限或业务工作台。账号主题后续建立的认证外壳属于业务主题范围，可以替换当前首页，但必须保留等价的框架集成验证路径。

主题完成后，开发者应能够独立验证以下闭环：

1. 启动 ASP.NET Core 服务端和 Vite 客户端。
2. 浏览器打开最小应用外壳。
3. 客户端通过真实 API 获取服务端和数据库状态。
4. SQLite 数据库在应用启动时自动完成 EF Core Migration。
5. 生产构建由 ASP.NET Core 托管 Vite 构建后的静态文件。
6. 服务端、客户端和测试项目能够统一构建并通过基础测试。
7. 使用独立开发模式 Chrome 和 CDP 检查页面 DOM、控制台、网络与截图。

## 2. 文档组成

本主题按职责拆分为三份设计文档：

- 本文：保存系统框架的总体方向、跨端契约、运行方式、主题边界和可见验收结果。
- [服务端设计](Server/design.md)：保存 .NET 解决方案、项目分层、依赖方向、领域持久化抽象、SQLite Repository、依赖注入、OpenAPI、错误响应、数据库迁移和服务端测试设计。
- [客户端设计](Client/design.md)：保存 React/Vite 工程、feature 目录、共享组件、路由、Kiota Client、错误适配、系统状态页面、前端测试和构建设计。

三份文档共同构成本主题的有效设计。具体实现步骤和验收用例后续分别写入 `impl.md` 和 `testPlan.md`，不在设计文档中保存实施日志。

## 3. 总体技术基线

### 3.1 服务端

- 使用最新稳定版 .NET 10 和 ASP.NET Core。
- ASP.NET Core 启动项目命名为 `RoundTableMeeting`，不添加 `.Web` 后缀。
- 采用启动项目、Application、Domain、Abstraction、具体数据库 Repository 和测试项目组成的模块化单体结构。
- 正式 HTTP API 使用 Controller。
- Application 使用普通应用服务，不引入 MediatR、消息总线或形式化 CQRS Handler 框架。
- 数据访问使用 Entity Framework Core，第一版数据库使用 SQLite。
- EF Core `DbContext` 默认全局使用 `QueryTrackingBehavior.NoTracking`；普通查询不跟踪实体，局部恢复跟踪必须有明确写入或并发理由。
- 使用 `Materal.Extensions.DependencyInjection` 的标记接口自动注册应用服务和仓储实现。
- API 错误统一使用 ASP.NET Core `ProblemDetails`。
- 使用 ASP.NET Core OpenAPI 生成 OpenAPI JSON，并提供 Scalar 或同类 OpenAPI UI。
- 服务端测试使用 xUnit。
- 使用 Serilog 作为日志实现，并通过 ASP.NET Core 日志集成提供结构化日志、请求日志和基础请求关联；应用代码统一依赖 `ILogger<T>`，不直接依赖 Serilog 静态 API。
- 使用 appsettings.json、环境专属配置、开发机 user-secrets 和生产环境变量管理配置，并对关键 Options 执行启动时校验。

### 3.2 客户端

- 使用 React、TypeScript、Vite、Ant Design 和 React Router。
- 使用 pnpm 管理依赖并提交锁文件。
- Node.js 项目基线使用 Node 24 LTS。
- 客户端测试使用 Vitest、jsdom、`@testing-library/react`、`@testing-library/user-event` 和 `@testing-library/jest-dom`。
- 不使用 Axios 编写手工 API Client。
- 使用 Kiota 根据服务端 OpenAPI JSON 生成类型安全的 TypeScript Client。
- Kiota CLI 和运行时 npm 包均使用精确版本，并保持版本配套。
- 框架阶段不引入 TanStack Query、Zustand 或其他额外全局状态管理库。

## 4. 总体工程结构

```text
RoundTableMeeting/
  Server/
    RoundTableMeeting.slnx
    RoundTableMeeting/
    RoundTableMeeting.Application/
    RoundTableMeeting.Domain/
    RoundTableMeeting.Abstraction/
    RoundTableMeeting.Repository.Sqlite/
    Tests/
  Client/
    roundTableMeeting/
```

服务端项目使用 C# 常见的帕斯卡命名。客户端项目和业务目录使用 JavaScript/TypeScript 常见的小驼峰命名。

首版不创建没有明确职责的 `RoundTableMeeting.Infrastructure`。数据库技术实现归属具体数据库 Repository，ASP.NET Core 宿主技术归属启动项目；只有后续出现文件系统、外部服务、系统时钟、后台调度等具有独立适配边界的能力时，才新增 Infrastructure 或按能力命名的项目。

## 5. 跨端契约

### 5.1 OpenAPI 与 Kiota

服务端生成的 OpenAPI JSON 是客户端 API 契约的唯一生成来源。首版固定生成 OpenAPI 3.0 文档，以降低 .NET 10 默认 OpenAPI 3.1 与 Kiota 目标版本之间的兼容风险。Scalar、Swagger UI 或其他交互页面只用于查看和调用接口，不维护第二份接口定义。

ProblemDetails 扩展字段必须进入 OpenAPI schema，并由各个 API operation 显式声明对应的错误响应。错误 schema 至少包含标准字段和 `code`、`traceId`、`errors` 扩展字段，使 Kiota 能够生成可使用的类型化错误模型。

Kiota 生成代码固定放在 `Client/roundTableMeeting/src/api/generated/` 并提交到仓库。业务代码不直接修改生成目录，也不让页面或业务组件直接依赖生成代码；认证配置、公共请求选项、错误转换和 feature 业务适配均放在生成目录之外。

Kiota 版本通过仓库内的 .NET Tool Manifest 固定。重新生成客户端时先启动服务端，统一脚本等待 OpenAPI HTTP 地址就绪，再通过 HTTP 获取当前 OpenAPI JSON 并直接交给 Kiota；仓库不额外维护一份手工同步的 OpenAPI JSON。日常客户端构建使用已经提交的生成代码，不要求服务端正在运行，也不要求开发机安装全局 Kiota。生成脚本失败或服务端未就绪时必须返回明确的非零结果。

生成脚本在写入 `src/api/generated/` 后统一转换为 CRLF，遵守项目文件行尾约定；生成目录不得通过 `.gitattributes` 豁免为 LF。

客户端所有环境都使用同源请求。服务端 OpenAPI path 固定包含一次 `/api` 前缀，OpenAPI `servers` 省略或使用相对 `/`；Kiota RequestAdapter 的技术 base URL 使用当前页面 origin，不设置为 `/api`，由生成请求模板追加一次 `/api`。开发环境由 Vite 代理到 ASP.NET Core，生产环境由同源 ASP.NET Core 直接提供。认证等公共请求配置只能在公共 Client 层接入。

### 5.2 错误响应

服务端 API 统一使用 `ProblemDetails`。跨端错误契约固定使用标准字段 `type`、`title`、`status`、`detail`、`instance`，并扩展稳定业务错误标识 `code`、请求关联标识 `traceId`；字段校验错误额外使用 `errors`，类型为字段名到错误消息数组的映射。客户端公共错误适配层将这些字段转换为页面可处理的错误对象。

生产响应和系统状态接口不得返回堆栈、连接字符串、绝对路径、环境变量或其他敏感内部信息。

### 5.3 日志与基础可观测性

首版固定使用 Serilog，并通过 `Serilog.AspNetCore` 接入 ASP.NET Core 日志管道。应用代码继续使用 `ILogger<T>`，由 Serilog 作为底层 Provider；默认使用 Console Sink 输出结构化日志，日志级别、类别、输出格式和覆盖规则通过配置管理。HTTP 请求使用 Serilog 请求日志能力统一记录，应用关键失败和数据库初始化过程记录足够的诊断信息，但不得记录密码、连接字符串、令牌、完整请求敏感字段或其他秘密。

请求关联使用 W3C Trace Context。错误响应中的 `Activity.Current?.TraceId` 优先作为 traceId，没有 Activity 时回退到 `HttpContext.TraceIdentifier`。集中异常处理只负责统一记录一次未处理异常并生成 ProblemDetails，Controller 或应用服务不得重复记录同一个异常。

### 5.4 系统状态

框架主题提供 `GET /api/system/status` 和临时系统状态页面。成功响应固定为以下形状：

```json
{
  "status": "available",
  "database": {
    "status": "available"
  },
  "environment": "Development"
}
```

其中 `status` 取 `available` 或 `degraded`，`database.status` 取 `available` 或 `unavailable`。状态接口不返回恒为真的 `migrationCompleted` 字段；迁移成功是服务能够启动的前置条件。数据库检查必须执行真实的轻量连接或查询检查，不能只检查是否存在待执行 Migration。

页面至少展示：

- 客户端已经加载。
- 服务端 API 是否可访问。
- 数据库是否可访问。
- 当前运行环境。
- 请求中、成功和失败状态。

状态数据必须来自真实服务端和数据库检查，不使用前端模拟数据。数据库迁移在启动阶段失败时，服务端直接启动失败且不开放状态 API，前端只能显示服务端不可访问，具体原因由服务端启动日志给出；服务端成功启动后发生数据库访问异常时，状态 API 仍可调用并以 `200` 返回 `degraded` 和 `unavailable`，页面展示对应结果。当前运行环境取自 ASP.NET Core 的 `IWebHostEnvironment.EnvironmentName`。状态接口首版不要求账号认证，账号主题落地时重新评估其暴露范围。该页面是框架集成的验收入口，后续账号主题可以将它替换为登录页面。

## 6. 开发与生产运行

### 6.1 开发环境

- ASP.NET Core 服务端和 Vite 开发服务器分别启动。
- Vite 将 `/api` 请求代理到 ASP.NET Core。
- OpenAPI JSON 和 Scalar 页面由 ASP.NET Core 提供。
- 客户端开发不要求 ASP.NET Core 托管前端文件。

### 6.2 生产环境

- Vite 生成客户端静态构建产物。
- ASP.NET Core 托管该构建产物，浏览器页面和 API 使用同一源站。
- React Router 路由由服务端提供 SPA 回退。API、OpenAPI 和其他服务端端点不能回退到 `index.html`。
- 未匹配的 `/api/*` 必须返回 ProblemDetails 404；OpenAPI、Scalar 和其他服务端端点也不能被 SPA 回退掩盖。
- `dotnet publish` 必须自动包含客户端生产构建结果，不要求开发者手工复制文件。发布环境必须提供 Node 24 LTS 和 pnpm，并使用 `pnpm install --frozen-lockfile`；前端安装或构建失败必须使发布失败，不能静默跳过。

## 7. 测试与调试方向

- 服务端使用 xUnit 覆盖 Domain、Application、SQLite Repository、HTTP 集成和架构约束。
- SQLite 数据访问测试使用真实 SQLite 数据库或等价隔离环境，不使用 EF Core InMemory 替代关系型行为测试。
- 客户端使用 Vitest、jsdom 和 React Testing Library 覆盖系统状态页面、错误状态、基础路由和公共适配。
- 服务端构建、客户端生产构建和自动化测试必须通过。
- 框架主题暂不默认引入 Playwright。
- 浏览器冒烟调试使用独立 Profile 的开发模式 Chrome，并通过只绑定 `127.0.0.1` 的 CDP endpoint 检查 DOM、控制台、网络和截图。
- 生产部署必须使用 HTTPS；开发环境的 localhost HTTP 仅用于本地调试。

不能把“CDP 端口已启动”等同于“页面已经完成调试”。验收必须确认已连接正确页面并成功获取所需调试信息。

## 8. 主题边界

本主题不实现：

- 用户账号、登录、认证 Cookie 和权限。
- 会议创建、会议码、参与者和会议列表。
- 群聊消息、提及和浏览器实时消息。
- MCP Agent 接入和 AI 回复任务。
- 正式业务页面。

未来的 SignalR、MCP 端点和 HostedService 可以先落在 `RoundTableMeeting` 宿主项目，业务编排仍由 Application 承担。只有当某类技术入口形成独立适配边界时，才新增能力项目；每条后台任务或消息处理都必须创建独立 DI scope，不能长期持有 Scoped DbContext。

这些能力由后续业务主题在本框架上垂直实现。框架主题不能用占位业务实体、模拟账号或模拟会议冒充业务完成。

## 9. 完成标准

本主题完成时必须同时满足：

1. 开发者可以按文档启动 Server 和 Client。
2. 浏览器能够打开系统状态页面。
3. 页面能够通过真实 API 显示服务端和数据库状态。
4. 服务端不可访问时页面停止加载并显示不可访问；数据库迁移失败时服务端启动失败并在启动日志中给出明确原因；服务启动后的数据库异常能够由状态 API 和页面展示。
5. SQLite Migration 能够在应用启动时自动执行。
6. `GET /api/system/status` 能够返回约定的状态模型，并能展示运行期数据库降级。
7. 服务端和客户端生产构建成功。
8. 服务端、客户端和架构约束测试通过。
9. ASP.NET Core 发布结果能够直接打开 React 应用并调用同源 API；未知 API 不会返回 HTML。
10. Chrome CDP 冒烟调试能够读取页面 DOM、控制台和网络结果，并完成截图检查。
