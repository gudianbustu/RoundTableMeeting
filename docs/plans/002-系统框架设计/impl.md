# 系统框架实现方案

## 1. 文档定位

本文把[系统框架设计](design.md)拆解为可执行的实施顺序、交付物和验收门。它只描述如何把框架设计落地，不记录实际执行日志，也不提前实现账号、会议、消息或 AI 协作业务。

服务端的命令、项目引用、EF Core、SQLite、Serilog、OpenAPI 和发布集成见[服务端实现方案](Server/impl.md)；客户端初始化、目录、Kiota、路由、Vitest、Vite 代理和生产构建见[客户端实现方案](Client/impl.md)。测试编号、测试数据隔离和手工浏览器验收见[testPlan.md](testPlan.md)。

## 2. 实施前提

### 2.1 目录和工作范围

所有命令从仓库根目录 `E:\Project\GDB\RoundTableMeeting` 执行，服务端代码位于 `Server/`，客户端代码位于 `Client/roundTableMeeting/`，文档位于 `docs/`。当前仓库有设计文档和 `Demo/` 预研源码；实施开始后允许创建解决方案和正式源码，但不得把源码放入 `docs/plans/`。

本主题只交付：

- 可启动的 ASP.NET Core 服务端。
- 可启动的 Vite/React 客户端。
- SQLite 自动迁移和真实系统状态检查。
- OpenAPI 3.0 HTTP 文档和可重复的 Kiota 生成链路。
- ProblemDetails、Serilog、统一配置和基础日志关联。
- 系统状态页面、基础路由和生产静态文件托管。
- 服务端、客户端、集成、架构和浏览器冒烟测试基础。

本主题不交付任何模拟账号、会议、消息或 AI 数据。状态页面的示例内容只能反映真实的服务端和数据库检查结果。

### 2.2 工具链

实施环境必须具备：

- .NET 10 SDK。使用 `global.json` 锁定已验证的小版本，当前实施机可用 `10.0.302`。
- Node.js 24 LTS。通过 Node 版本文件和 `package.json` 的 `engines` 约束版本范围，不以当前机器的 Node 26 作为项目基线。
- pnpm。项目通过 `packageManager` 固定实施验证版本，当前基线为 `pnpm@11.24.0`。
- Git。
- 可访问官方 NuGet 和 npm registry。
- 可选的独立开发模式 Chrome，以及只绑定 `127.0.0.1` 的 CDP 端口，用于最终手工冒烟验收。

Kiota 通过仓库内 .NET Tool Manifest 使用 `Microsoft.OpenApi.Kiota` `1.34.1`。不要求开发者依赖全局 Kiota；全局安装只作为本机便利工具，不能成为构建前提。实施开始时必须执行一次 `kiota info -l TypeScript`，以真实生成器输出确认 TypeScript runtime 配套版本。

### 2.3 实施原则

1. 先建立可编译的工程边界，再逐层增加运行能力。
2. 每个阶段都必须有一个可以执行的验收门，未通过不得进入依赖它的下一阶段。
3. 服务端和客户端各自可以独立构建，但最终必须通过真实 HTTP 和真实 SQLite 联调。
4. 设计文档中的包版本、路由、配置键和目录一旦在实现阶段确认，必须同步回对应实现文档，不把关键约定留在代码注释或聊天记录中。
5. 只修改完成框架所必需的文件，不顺手重构 Materal 包或后续业务结构。
6. 所有异步入口传递取消令牌；任何长生命周期后台入口未来接入时都必须自行创建 DI scope。

## 3. 实施阶段总览

| 阶段 | 目标 | 主要交付物 | 验收门 |
| --- | --- | --- | --- |
| 0 | 固定工具链和仓库基线 | `global.json`、包版本策略、工具清单、Node/pnpm 版本约束 | 工具版本可检查，未产生未声明的业务代码 |
| 1 | 建立服务端工程骨架 | `slnx`、Web、Application、Domain、Abstraction、Repository.Sqlite 和测试项目 | 全部项目可还原、编译，引用方向符合设计 |
| 2 | 建立服务端持久化与启动基础 | `DbContext`、Repository、`IUnitOfWork`、SQLite Migration、初始化流程 | 空数据库可启动并自动迁移；NoTracking/UoW 测试通过 |
| 3 | 建立服务端 HTTP 基础 | Controller、状态接口、ProblemDetails、Serilog、OpenAPI/Scalar | 真实 HTTP 可访问，错误和日志字段符合契约 |
| 4 | 建立客户端工程、生成初始 Client 和状态 feature | Vite、React、Ant Design、Router、初始 generated Client、公共 Client、`systemStatus` 页面 | Kiota 初始生成完成，Vite 页面通过真实 API 显示状态和失败态 |
| 5 | 固化可重复的 OpenAPI 到 Kiota 链路 | 生成脚本、staging 校验、提交的 generated Client、feature API 适配 | 服务端启动后可通过 HTTP 重新生成，失败返回非零且不破坏旧结果 |
| 6 | 建立前后端开发联调 | Vite proxy、本地证书边界、脚本和运行说明 | 浏览器通过 Vite 页面访问真实 API，无跨域或绝对地址依赖 |
| 7 | 建立生产发布链路 | Vite 构建、ASP.NET Core 静态文件托管、SPA fallback、publish target | `dotnet publish` 自动构建并托管前端，未知 API 不返回 HTML |
| 8 | 完成自动化和浏览器验收 | xUnit、Vitest、集成测试、架构测试、CDP 冒烟记录 | 测试矩阵通过，页面 DOM/控制台/网络/截图检查通过 |

## 4. 分阶段实施步骤

### 4.1 阶段 0：固定工具链和仓库基线

输入：三份设计文档、仓库约束、当前机器工具链。

实施内容：

1. 在仓库根目录建立 `global.json`，固定已通过验证的 .NET 10 SDK 小版本，并设置缺失 SDK 时的失败策略。
2. 建立根目录 `.config/dotnet-tools.json`，登记 `Microsoft.OpenApi.Kiota` `1.34.1` 和仓储 Migration 所需的 `dotnet-ef` 精确版本。
3. 建立服务端集中包版本文件或等价的单一版本管理位置。Materal 包固定为 `1.4.7` 或 `1.4.4`，EF Core、ASP.NET Core OpenAPI、Serilog 和测试包采用同一 .NET 10 兼容版本。
4. 建立客户端 Node 24 LTS 版本提示、`packageManager`、`engines` 和依赖锁文件策略。
5. 检查 `.gitignore` 至少忽略 `bin/`、`obj/`、`node_modules/`、`dist/`、`artifacts/`、SQLite 数据库及伴随文件、测试临时数据库和本地日志；不按 `data/` 或 `packages/` 目录名宽泛忽略未来可能提交的业务资产。
6. 执行最小工具链探针：确认 .NET 10、OpenAPI 3.0、Scalar、Kiota `1.34.1`、TypeScript、Vite 和 Vitest 能完成“服务端输出 OpenAPI -> Kiota 生成 -> TypeScript 类型检查”的最小链路。若 TypeScript 5.9.3 与 Kiota preview.103 生成代码存在阻断性兼容问题，项目固定使用已验证的 TypeScript `5.9.3`，并同步 `Client/impl.md`、锁文件和本阶段验收记录；不得通过浮动版本规避问题。

输出：工具约束、包版本约束和仓库忽略规则。

验收门：

- `dotnet --version`、`dotnet tool restore`、`node --version` 和 `pnpm --version` 能按文档检查。
- 包版本没有使用浮动范围或 `latest`。
- `.gitignore` 不会把 SQLite 运行数据和生成构建产物提交到仓库。
- 最小版本探针通过，且 Kiota `info` 输出的 TypeScript runtime 版本已同步到客户端依赖。

### 4.2 阶段 1：建立服务端工程骨架

输入：服务端设计文档中的项目职责和引用方向。

实施内容：

1. 创建 `Server/RoundTableMeeting.slnx`。
2. 创建 `RoundTableMeeting`、`RoundTableMeeting.Application`、`RoundTableMeeting.Domain`、`RoundTableMeeting.Abstraction`、`RoundTableMeeting.Repository.Sqlite` 五个生产项目。
3. 创建 Domain、Application、Repository.Sqlite、Integration 和 Architecture 测试项目，并按测试目标决定是否创建独立的测试项目引用。
4. 添加项目引用：Web 引用 Application、Abstraction、Repository.Sqlite；Application 引用 Domain、Abstraction；Repository.Sqlite 引用 Domain、Abstraction；Domain 和 Abstraction 不引用其他本项目生产项目。
5. 加入统一的 nullable、隐式 using、分析器和测试构建属性。测试项目不能通过项目引用把 EF Core 或 Web 依赖传入 Domain 测试目标。
6. 建立各项目的程序集标记类型，供 Materal `AddAutoService` 显式扫描 Application 与当前 Repository.Sqlite 程序集中的标记服务；Web 使用 ASP.NET Core 默认 DI，不调用 `AddMateralServiceProvider`。

输出：可还原的服务端解决方案和空的分层项目。

验收门：

- `dotnet restore Server/RoundTableMeeting.slnx` 成功。
- `dotnet build Server/RoundTableMeeting.slnx --configuration Debug` 成功。
- 架构测试能读取生产程序集并验证禁止引用方向。
- Web 项目是唯一 ASP.NET Core 启动项目，名称为 `RoundTableMeeting`。

### 4.3 阶段 2：建立服务端持久化与启动基础

输入：Domain 的 `BaseEntity`、仓储契约、`IUnitOfWork` 和 Abstraction 的 `IDatabaseInitializer` 设计。

实施内容：

1. 在 Domain 建立项目级 `BaseEntity : IEntity<Guid>`，主要实体统一使用 Guid 主键。首版不向基础实体添加未经确认的审计、软删除或并发字段。
2. 在每个实际聚合出现时建立空的 `IXxxRepository : IRepository<TEntity, Guid>`。框架阶段可以不创建业务实体和业务仓储，只验证基类和未来的扩展位置。
3. 在 Domain 固定实现 `IUnitOfWork` 的接口形状；Application 只依赖该接口和领域仓储，不引用 EF Core。
4. 在 Repository.Sqlite 实现 `RoundTableMeetingDbContext`、通用 SQLite Repository 基类和工作单元。工作单元组合 DbContext，不继承 TTA 的工作单元实现。
5. 全局设置 `QueryTrackingBehavior.NoTracking`。`RegisterEdit` 对 detached 实体明确设置为 Modified，普通查询不能偷偷恢复跟踪。
6. 在 Abstraction 放置 `IDatabaseInitializer`，在 Repository.Sqlite 实现 SQLite 初始化器。初始化器确保目录存在后调用 `Database.MigrateAsync`，迁移异常必须向宿主抛出。
7. 建立第一条空 Migration，配置默认路径 `data/round-table-meeting.db`，并归一化为基于 `AppContext.BaseDirectory` 的稳定路径。
8. 配置 WAL 和 5 秒级 busy timeout，并用真实 SQLite 测试确认配置生效或在连接级别可观察。

输出：可以使用空数据库启动并完成迁移的持久化基础。

验收门：

- 删除数据库文件后启动服务，数据库目录、文件和 Migration 历史自动创建。
- 人为制造 Migration 失败时，服务启动失败并记录可诊断日志。
- Repository 查询默认没有跟踪实体；成功提交后工作单元清理跟踪状态。
- 工作单元重复注册、冲突注册、保存失败和取消都具有测试覆盖。

### 4.4 阶段 3：建立服务端 HTTP 基础

输入：服务端错误、日志、状态和 OpenAPI 设计。

实施内容：

1. 配置 ASP.NET Core Controller、JSON、模型校验和路由约定。
2. 接入 `Serilog.AspNetCore`，默认使用 Console Sink，配置结构化输出和请求完成日志。应用代码只使用 `ILogger<T>`。
3. 配置 W3C Trace Context；集中异常处理只记录一次未处理异常，并使用 `Activity.Current.TraceId`，无 Activity 时回退 `HttpContext.TraceIdentifier`。
4. 接入统一 ProblemDetails。固定标准字段、`code`、`traceId` 和字段校验 `errors`，明确公开错误不得包含堆栈、连接字符串、绝对路径或秘密。
5. 在 Web 层冻结可供后续 Cookie 写端点复用的 `RequireBrowserOrigin` 元数据、Origin 校验中间件和 `origin_not_allowed`/`unsupported_media_type` 错误契约；中间件不判断 POST/PATCH/PUT/DELETE 白名单，方法合法性由 endpoint routing/action 决定。
6. 创建匿名 `GET /api/system/status` Controller。运行期用真实轻量数据库检查，数据库故障返回 `200` 的 `degraded/unavailable` 结果。
7. 配置 ASP.NET Core OpenAPI 输出 OpenAPI 3.0，并提供 Scalar 交互页面。ProblemDetails schema 和各 operation 的错误响应必须进入文档；Web/OpenAPI/Scalar 使用默认 ASP.NET Core DI，避免 Materal keyed service 兼容性边界。

输出：可通过 HTTP 观察的服务端框架接口。

验收门：

- `GET /api/system/status` 的成功、降级和服务不可访问场景与设计一致。
- Origin 框架测试证明方法级 endpoint metadata、Origin 单值校验、JSON Content-Type 约束和 `UseRouting -> Origin -> Authentication -> Authorization` 顺序。
- 通过 IntegrationTests 测试宿主注入的 `GET /__test/throw` 触发一个故意异常，只产出一条集中异常日志并返回 `internal_error` ProblemDetails；该端点不属于生产 API。
- OpenAPI 文档可以由 HTTP 获取，版本为 3.0，包含状态接口和错误 schema。
- Scalar 能读取同一个 OpenAPI 地址，未维护第二份手工契约。

### 4.5 阶段 4：建立客户端工程、初始生成 Client 和系统状态 feature

输入：客户端设计文档中的目录、路由、公共 API 层和状态页面需求。

实施内容：

1. 在 `Client/roundTableMeeting/` 使用 Vite React TypeScript 模板初始化项目，删除模板演示内容。
2. 安装精确版本的 React、Ant Design、React Router、Kiota runtime、Vitest、jsdom 和 Testing Library 依赖。
3. 建立 `app/`、`api/`、`features/systemStatus/`、`shared/` 和 `test/` 的实际目录；只创建首版真正使用的目录，不创建空的未来业务目录。
4. 建立 `@/` 指向 `src/` 的 TypeScript、Vite 和 Vitest 配置。
5. 配置根组件、Ant Design `ConfigProvider`、Router、路由级错误边界和未匹配路由。
6. 配置 `/api` 到 `http://localhost:5043` 的 Vite 开发代理，使客户端请求保持同源路径。
7. 先建立能等待 OpenAPI、调用仓库 Tool Manifest 中 Kiota 并输出到临时目录的最小 `generate:api` 脚本；启动阶段 3 的服务端并执行一次，产生初始 generated Client，再建立公共 Kiota Client 单例和公共错误适配层。
8. 创建 `systemStatus` feature 的 API 适配、页面和状态组件。页面展示请求中、可用、降级、数据库不可用和网络失败状态，数据全部来自真实 API。

输出：可在 Vite 开发服务器中打开的最小客户端外壳。

验收门：

- `pnpm install --frozen-lockfile` 和 `pnpm build` 成功。
- Vite proxy 已配置后，系统状态页面能显示服务端真实状态；阶段 4 不使用手写 API Client 或假成功数据。
- API 失败时页面有明确失败态，不把降级状态误报为网络异常。
- feature 页面不直接依赖 generated Client，公共 API 单例不会被重复创建。

### 4.6 阶段 5：固化 OpenAPI 到 Kiota 链路

输入：已运行的服务端、OpenAPI HTTP 地址和客户端生成目录。

实施内容：

1. 在阶段 4 最小脚本基础上固化统一的 API 生成脚本，默认请求 `http://localhost:5043/openapi/v1.json` 或当前启动配置对应的 OpenAPI 地址；工具命令必须从仓库 Tool Manifest 解析，不能依赖全局 Kiota。
2. 脚本先轮询 OpenAPI 地址，使用短间隔和总超时；服务未就绪、返回非成功状态或内容不是有效 OpenAPI 时以非零状态退出。
3. 脚本调用仓库本地 `dotnet tool run kiota generate`，先输出到临时 staging 目录，使用 TypeScript、清理输出和精确版本运行时。
4. staging 通过 UTF-8 无 BOM、CRLF、目录完整性和 TypeScript 结构检查后，才原子替换 `src/api/generated/`；失败时恢复原目录并返回非零。
5. 生成目录提交到仓库但禁止手工修改。业务代码通过 `api/client` 和 feature API 适配使用生成结果。

输出：可重复执行的 `generate:api` 脚本和当前生成结果。

验收门：

- 服务端启动后，在独立终端执行 `pnpm generate:api` 可以通过 HTTP 重新生成。
- 停止服务端后执行脚本会在超时后返回非零，原 generated 目录保持不变。
- OpenAPI 发生契约变化时，重新生成产生可审查的 generated 目录差异。

### 4.7 阶段 6：建立前后端开发联调

输入：阶段 3 的服务端 HTTP 端点和阶段 4 的 Vite 客户端。

实施内容：

1. ASP.NET Core 使用固定开发地址 `http://localhost:5043`；Vite 使用默认 `http://localhost:5173`，如端口被占用必须在启动说明中明确替代端口。开发代理使用同源路径，不额外配置 CORS。
2. Vite 只代理 `/api` 到 ASP.NET Core；默认使用 HTTP 5043，不把开发证书信任作为自动化前提。
3. 客户端公共 Client 始终从当前页面 origin 发起请求，生成路径模板负责追加一次 `/api`，不拼接环境专属绝对地址。
4. 通过浏览器检查 API 请求、状态码、响应内容和控制台；不以仅能在 curl 中返回成功作为联调完成。

输出：开发环境的真实跨进程联调路径。

验收门：

- 浏览器从 Vite 页面发起的 `/api/system/status` 请求被正确代理。
- 客户端不出现 CORS 错误、混合内容错误或错误的绝对 API 地址。
- 服务端停止后页面进入可理解的不可访问状态，重新启动后能够重新加载。

### 4.8 阶段 7：建立生产发布链路

输入：客户端 `dist/` 构建产物和服务端 Web 项目。

实施内容：

1. 在 Web 项目定义客户端目录、构建目录和发布目录的 MSBuild 属性。
2. `dotnet publish` 前执行 `pnpm run check:toolchain` 精确校验 Node `24.20.0` 和 pnpm `11.24.0`，再执行客户端依赖安装和 `pnpm build`，统一使用 `pnpm install --frozen-lockfile`。
3. 将 `dist/` 复制到 ASP.NET Core 发布输出的 `wwwroot/`，前端安装或构建失败时终止 publish。
4. 配置静态文件、Controller、OpenAPI、Scalar 和 SPA fallback 的路由顺序。
5. fallback 只处理页面导航请求，明确排除 `/api`、`/openapi`、`/scalar` 和其他服务端端点。未知 `/api/*` 返回 ProblemDetails 404。
6. 在发布目录按实际 HTTP 或 HTTPS 配置启动服务，确认 React Router 子路径刷新、同源 API、Origin 与 Cookie 行为。

输出：可以由 ASP.NET Core 单独运行的发布目录。

验收门：

- 干净环境执行 `dotnet publish` 时会自动安装并构建客户端。
- 发布服务打开首页和 Router 子路径，静态资源正确加载。
- 页面通过同源 `/api` 获取状态。
- 未知 `/api/not-found` 返回 JSON ProblemDetails 404，而不是 `index.html` 200。
- 前端构建故意失败时 `dotnet publish` 返回非零。

### 4.9 阶段 8：完成自动化和浏览器验收

输入：前面各阶段的源码、脚本和 testPlan。

实施内容：

1. 按 testPlan 建立 Domain、Application、Repository.Sqlite、Integration 和 Architecture 的 xUnit 测试；验证默认 ASP.NET Core DI 下的显式 Materal 标记扫描、Scoped 生命周期、临时配置替换、测试 ApplicationPart、Serilog 和 OpenAPI；测试宿主专用异常和模型校验端点只由 IntegrationTests 注入，不进入生产项目、发布产物或生产 OpenAPI。
2. 建立 Vitest 的 jsdom 测试环境和系统状态页面组件测试。
3. 增加构建、OpenAPI/Kiota、publish 和未知 API 404 的自动化验证。
4. 启动独立开发模式 Chrome，通过 CDP 检查 endpoint、目标页面、DOM、控制台、网络和截图；不得把端口可访问当作调试完成。
5. 按 testPlan 固定 CDP 命令、页面地址、选择器、网络断言和截图位置；执行结果保留在代码审查、提交说明或外部 CI/PR 记录中，不在 `docs/plans/` 追加实施日志文件。

输出：通过的自动化测试和浏览器验收结果。

验收门：

- testPlan 中的阻断级用例全部通过。
- 服务端和客户端生产构建通过。
- CDP 能读取正确页面的 DOM、控制台和网络结果，并且截图显示页面非空、布局无明显遮挡。
- 框架页面中不存在模拟业务数据。

## 5. 文档之间的关系

### 5.1 总体文档与服务端文档

本文只规定阶段顺序、跨端依赖和验收门。服务端实现文档负责给出可以直接执行的 `dotnet` 命令、项目引用、包清单、配置键、启动管道和服务端测试命令。服务端文档不能通过新增横向项目改变总体设计中已经确认的分层边界；其中测试专用端点、测试替身和故障注入仅存在于测试程序集/测试宿主。

### 5.2 总体文档与客户端文档

本文规定客户端必须与服务端通过真实 `/api` 联调，并由 ASP.NET Core 托管生产构建结果。客户端实现文档负责初始化命令、包版本、feature 目录、Router、Kiota、Vitest、Vite proxy 和发布脚本。客户端文档不能引入 Axios 或绕过公共 Client 直接依赖 generated 目录。

### 5.3 实现文档与测试方案

每一个实施阶段的验收门必须能在[testPlan.md](testPlan.md)中找到对应测试编号或手工场景。实现文档定义行为，测试方案定义如何证明行为；如果实现过程中发现设计无法直接测试，先更新对应实现文档和测试方案，再修改代码。测试专用端点不改变生产 OpenAPI 契约，也不进入发布验收目标。

## 6. 完成定义

002 主题只有在以下条件全部满足时才算完成：

1. 服务端和客户端项目结构、引用方向和版本约束已经落地。
2. 空 SQLite 数据库能够在 ASP.NET Core 启动时自动迁移，迁移失败不会开放半初始化服务。
3. `GET /api/system/status` 通过真实数据库检查返回约定状态。
4. Serilog、ProblemDetails、W3C traceId 和 OpenAPI 3.0 通过自动化测试。
5. Kiota 能从已启动服务端的 HTTP OpenAPI JSON 重新生成 TypeScript Client。
6. Vite 开发代理和 ASP.NET Core 生产托管都能完成页面到 API 的真实调用。
7. 系统状态页面覆盖加载中、成功、降级、数据库不可用和网络失败状态。
8. 未知 API 不会被 SPA fallback 伪装成 HTML 成功响应。
9. 服务端、客户端、集成、架构和构建测试通过，测试没有依赖开发者本机已有数据库文件。
10. 独立 Chrome CDP 冒烟检查能够读取目标页面的 DOM、控制台、网络结果和截图。
11. Cookie 写请求 Origin 元数据、中间件顺序、方法不匹配旁路、403/415 错误契约和统一 ProblemDetails 写入已通过 I-018 至 I-026；首个 Cookie 消费方直接复用该冻结形态，并在自身主题中完成业务 operation 的 OpenAPI 与认证回归。

账号、会议、群聊、宿主原生 MCP Agent 和通信恢复必须留给总体设计中后续业务主题实现，不能为了让框架页面显得完整而提前加入模拟实现。
