# 账号访问与用户管理主题实现方案

## 1. 文档定位

本文把[主题总体设计](design.md)拆解为可执行的实施顺序、跨端交付物和阶段验收门。服务端工程与认证管道见[服务端实现方案](Server/impl.md)，账号访问模块内部实现见[服务端账号访问模块实现方案](Server/Modules/AccountAccess/impl.md)，客户端工程接入见[客户端实现方案](Client/impl.md)，认证状态、页面和组件实现见对应子目录的 `impl.md`。完整测试编号和浏览器验收见[测试方案](testPlan.md)。

本文不记录实施日志。首轮最小登录切片已经完成；本轮在同一主题内增加内部测试所需的最小用户管理，不扩展角色权限、审计、账号生命周期或高级会话能力。

## 2. 实施前提

### 2.1 已有工程基线

实施直接扩展 002 系统框架已经落地的正式工程：

- 服务端：.NET 10、ASP.NET Core Controller、Application/Domain/Abstraction/Repository.Sqlite 分层、EF Core SQLite、工作单元、ProblemDetails、Serilog、OpenAPI 和 Scalar。
- 客户端：React 19、TypeScript 5.9、Vite 8、Ant Design 6、React Router 7、Kiota、公共 API Client、统一 `ApiError` 和 Vitest。
- 开发地址：服务端 `http://localhost:5043`，客户端 `http://localhost:5173`。
- 生产形态：ASP.NET Core 同源托管客户端静态文件。

不新建横向 Infrastructure 项目，不替换现有 DI、工作单元、ProblemDetails、Kiota 或发布链路。

### 2.2 本主题交付范围

本主题只交付：

- 配置预置账号与幂等种子。
- 用户名密码登录、当前用户查询和退出。
- ASP.NET Core Cookie Authentication、最小 Claim、同源 Origin 校验和统一授权边界。
- `UserId` 请求级当前用户上下文。
- 登录页、认证 Provider、认证门、认证后布局、登录后占位页和当前用户栏。
- OpenAPI/Kiota 认证契约、自动化测试和真实浏览器验收。
- 所有已登录用户可使用的用户列表、添加账号、昵称修改和固定密码重置。
- `/users` 用户管理页、菜单入口和独立 userManagement feature。

配置种子继续保证部署至少存在一个可登录账号；应用运行后，任意已登录用户可以通过用户管理页添加测试账号。在线添加不修改配置种子，后续重启也不会覆盖已有账号。

不交付用户搜索、分页、删除、禁用、用户名修改、用户自行改密、角色权限、审计、找回密码、多因素认证、持久化会话或会议业务。

## 3. 实施阶段总览

| 阶段 | 目标 | 主要交付物 | 验收门 |
| --- | --- | --- | --- |
| 0 | 冻结契约和配置 | DTO、错误 code、配置键、用户名规范、文件清单 | 设计与实现文档一致，无延期需求混入 |
| 1 | 建立账号领域与持久化 | `UserAccount`、仓储、映射、Migration | 空库迁移后具备账号表和唯一索引 |
| 2 | 建立应用服务和账号种子 | 认证服务、当前用户查询、`IAccountSeeder` | 两个测试账号可幂等种入且不覆盖已有资料 |
| 3 | 建立 Web 认证管道 | Cookie、当前用户上下文、Origin 校验、Controller、ProblemDetails | 登录、me、logout 和 `401/403` 契约通过真实 HTTP |
| 4 | 更新 OpenAPI 与 Kiota | 认证路径、成功 DTO、错误映射、generated Client | `pnpm generate:api` 成功且生成结果可构建 |
| 5 | 建立客户端认证模块 | Provider、API 适配、全局 401 信号、状态机 | 刷新初始化、登录、退出和失败语义通过 Vitest |
| 6 | 建立页面与组件 | 登录页、认证门、认证后布局、首页、用户栏 | 用户可从浏览器完成登录、刷新保持和退出 |
| 7 | 完成回归与发布验证 | xUnit、Vitest、Kiota、publish、CDP | 所有 P0/P1 通过，002 核心回归保持可用 |
| 8 | 建立用户管理应用服务 | DTO、IUserManagementService、实体修改行为、列表查询 | 添加、改昵称、固定重置的 Domain/Application/Repository 测试通过 |
| 9 | 建立用户管理 HTTP 契约 | UsersController、Origin 元数据、ProblemDetails、OpenAPI | GET/POST/PATCH/重置接口通过真实 Cookie 集成测试 |
| 10 | 建立用户管理客户端 | userManagement feature、/users、布局入口、弹窗组件 | 列表和三项管理操作通过 Vitest 与浏览器验收 |
| 11 | 用户管理回归收口 | Kiota、构建、publish、CDP、框架 Origin 回归 | 所有新增 P0/P1 和 002/003 既有回归通过 |

## 4. 分阶段实施步骤

### 4.1 阶段 0：固定契约和配置

1. 固定认证 API：`POST /api/auth/login`、`GET /api/auth/me`、`POST /api/auth/logout`。
2. 固定成功 DTO：`userId`、`username`、`nickname`。
3. 固定错误：`invalid_credentials`、`unauthenticated`、`origin_not_allowed`、`unsupported_media_type`、`validation_failed` 和通用权限错误 `forbidden`。
4. 固定配置节 `Authentication`，包含允许浏览器源和初始账号集合。
5. 固定用户名处理：显示值执行 Trim + Unicode NFC，比较值再执行 `ToUpperInvariant()`。
6. 固定 Cookie 为会话级、HttpOnly、SameSite=Lax，生产 Secure。
7. 固定 Application 不访问 `HttpContext`、Cookie、Options 或配置 Provider。

验收门：设计文档、本文、Server/Client 实现文档使用相同字段、状态码、目录和职责。

### 4.2 阶段 1：账号领域与 SQLite 持久化

1. 在 Domain 新增 `UserAccount` 和 `IUserAccountRepository`。
2. 在实体中集中实现显示用户名与 `NormalizedUsername` 的规范化；昵称只做 Trim + NFC，不要求唯一。
3. 在 Repository.Sqlite 增加实体映射、仓储实现和 DbSet。
4. 为 `NormalizedUsername` 建立普通精确唯一索引。
5. 新增 `AddUserAccounts` Migration 并更新 Snapshot。
6. 使用真实临时 SQLite 验证映射、唯一约束、查询和 NoTracking。

验收门：空数据库自动迁移后可写入和查询账号；等价用户名不能创建两条记录。

### 4.3 阶段 2：应用服务与账号种子

1. 在 Abstraction 新增认证 DTO、`IAuthenticationService`、`ICurrentUserContext`、`IAccountSeeder` 和种子输入 DTO。
2. 在 Application 引用 `Microsoft.Extensions.Identity.Core`，使用 `IPasswordHasher<UserAccount>`，不引入完整 ASP.NET Core Identity。
3. 实现凭证验证和当前用户查询。
4. 实现 `AccountSeeder`：一次校验全部输入，查询缺失账号，生成哈希，统一注册并提交。
5. 已存在账号保持原昵称和密码，不在启动时覆盖。
6. Web 负责 Options 绑定和配置到种子 DTO 的映射。

验收门：Application 单元测试覆盖有效登录、无效凭证、当前用户、重复种子、配置重复和取消传播。

### 4.4 阶段 3：Web 认证管道和 API

1. 注册 Cookie Authentication、FallbackPolicy Authorization、`HttpCurrentUserContext`、认证响应头中间件和基于 `RequireBrowserOrigin` 元数据的 Origin 校验中间件。
2. 覆盖 Cookie 默认跳转事件，使 API 返回 ProblemDetails `401/403`。
3. 在启动 scope 中先执行 `IDatabaseInitializer`，再执行 `IAccountSeeder`。
4. 增加 `AuthenticationController` 三个端点。
5. 登录和退出应用 Origin 校验；系统状态继续匿名。
6. 登录成功只写 `UserId` Claim；me 每次按 `UserId` 查询账号资料。
7. 认证日志只记录结果和 traceId，不记录用户名、密码、Cookie 或 Claim 原文。

验收门：IntegrationTests 通过真实 CookieContainer 完成登录、刷新查询和退出；配置 Origin 允许，外部、`null`、缺失 Origin 均拒绝，默认 Cookie 重定向被覆盖。

### 4.5 阶段 4：OpenAPI 与 Kiota

1. 为三个认证 operation 声明成功和错误响应。
2. 验证 OpenAPI 包含认证路径、DTO、`errors` 和稳定 code。
3. 启动服务端并执行 `pnpm generate:api`。
4. 审查 generated 差异，确认只有一个 `/api` 前缀且没有开发绝对地址。
5. 运行客户端类型检查和构建。

验收门：生成链路可重复执行；服务端停止或契约非法时原 generated 目录不受损。

### 4.6 阶段 5：客户端认证模块

1. 新增 authentication feature 的 API 适配和视图模型。
2. 在公共 Kiota 中间件增加 `401` 通知信号，不在页面重复监听 HTTP 细节。
3. 在 `features/authentication/providers/` 增加 `AuthenticationProvider`，由 `AppProviders` 通过 feature 公共出口组合，实现 `checking/authenticated/anonymous/error` 状态机。
4. 使用 AbortController 和递增请求版本处理 StrictMode、刷新、登录和退出之间的竞态。
5. 退出网络失败时保留当前认证状态。
6. `AppProviders` 组合 Ant Design 和认证 Provider。

验收门：Provider 和 API 适配的 P0/P1 Vitest 全部通过。

### 4.7 阶段 6：路由、页面和组件

1. 将 `/login` 设为匿名页面。
2. 用 `AuthGate -> AuthenticatedLayout -> AuthenticatedHomePage` 保护 `/`。
3. 保留公开 `/system/status` 和现有框架验收路径。
4. 实现 `LoginForm`、`CurrentUserBar`、`AuthGate` 和 `AuthenticatedLayout`。
5. 安全保存并校验站内返回路径，过滤外部 URL。
6. 登录失败保留用户名并清空密码；退出失败保留当前用户。
7. 登录后首页只显示身份确认内容，不重复渲染用户栏或模拟会议数据。

验收门：浏览器可完成两个账号分别登录、刷新保持、退出和受保护路由拒绝。

### 4.8 阶段 7：回归、发布与浏览器验收

1. 执行服务端全部测试和客户端全部测试。
2. 执行 OpenAPI/Kiota 生成回归。
3. 执行 `pnpm build` 和 `dotnet publish`。
4. 发布环境通过 HTTPS 验证 Secure Cookie、SPA 子路径和同源 API。
5. 使用独立 Chrome CDP 检查登录前、登录后、刷新后和退出后的 DOM、Console、Network 和截图。
6. 回归 `/api/system/status`、未知 API、ProblemDetails 和静态托管。

验收门：testPlan 中全部 P0/P1 通过，且没有出现用户管理延期能力。

### 4.9 阶段 8：用户管理领域与应用服务

1. 在 Abstraction 增加 UserSummaryDto、CreateUserRequest、UpdateNicknameRequest、结果类型和 IUserManagementService。
2. 在 UserAccount 增加 ChangeNickname 和 ReplacePasswordHash；首次 InitializePasswordHash 约束保持不变。
3. 在 IUserAccountRepository 增加稳定排序的 ListAllAsync。
4. 实现 UserManagementService，复用 PasswordHasher、IUserAccountRepository 和 IUnitOfWork。
5. 添加账号执行应用层规范化冲突检查；数据库唯一索引冲突映射为同一 UsernameConflict。
6. 密码重置只使用固定常量 `123456` 生成新哈希，不接受传入密码。

验收门：列表稳定排序；新账号立即可登录；昵称修改不改变用户名；固定重置后只有 `123456` 对新登录有效；已有 Cookie 继续有效。

### 4.10 阶段 9：用户管理 HTTP、OpenAPI 与 Kiota

1. 新增 UsersController，Controller 级 `[Authorize]`，不加角色或权限 Policy。
2. 固定接口：GET/POST `/api/users`、PATCH `/api/users/{userId}/nickname`、POST `/api/users/{userId}/reset-password`。
3. POST 创建和 PATCH 昵称使用 `[RequireBrowserOrigin(true)]`；密码重置使用 `[RequireBrowserOrigin]`；GET 列表不标记。
4. 用户管理四条接口的全部响应统一 no-store/no-cache；采用独立用户管理响应头中间件或通用敏感响应 metadata，不扩大 auth-only 中间件后留下名称错配。
5. 在统一 ProblemDetailsFactory 增加 username_conflict 和 user_not_found 的安全 title/detail。
6. 为各 operation 显式声明成功和错误响应，重新生成 Kiota，并审查 PATCH、404、409 和 415 映射。
7. 未认证请求由 Cookie Challenge 返回 401；所有已登录用户都能执行管理操作。

验收门：WebApplicationFactory 使用真实 Cookie 完成列表、添加、改昵称和重置闭环；框架 Origin 的 POST/PATCH/PUT/DELETE 回归继续通过。

### 4.11 阶段 10：用户管理客户端

1. 新增 features/userManagement 的 API 适配、类型、页面和四个组件。
2. AuthenticationProvider 增加 refreshCurrentUser，刷新资料时保持当前页面，不进入整页 checking。
3. `/users` 放入 AuthGate -> AuthenticatedLayout，布局增加对全部登录用户可见的用户管理入口。
4. 页面进入时加载完整列表；操作成功后重新加载列表。
5. 当前账号昵称被修改时调用 refreshCurrentUser，使用户栏同步更新。
6. 添加失败和昵称失败绑定字段错误；重置密码使用确认对话框。
7. user_not_found 时关闭目标操作并刷新列表；全局业务 401 继续由公共客户端转为匿名。

验收门：任意登录账号可以从菜单进入 `/users`，添加第二个可登录账号、修改昵称并固定重置密码。

### 4.12 阶段 11：用户管理验证与收口

1. 执行新增 Domain/Application/Repository/Architecture/Integration/Vitest。
2. 执行 002 Origin I-018 至 I-026 回归，尤其 PATCH、PUT、DELETE 和 404/405。
3. 启动服务端执行 pnpm generate:api，验证用户 DTO、请求 DTO 和错误映射。
4. 执行 pnpm build 和 dotnet publish。
5. 使用两个账号完成 CDP：账号 A 添加 B、B 登录、A 修改 B 昵称、重置 B、旧密码失败、123456 成功。
6. 检查 Network、Console、DOM 和宽窄屏，不泄露初始密码或哈希。

验收门：新增测试全绿，003 既有基线及 002 框架回归不退化，发布环境用户管理闭环可见。

## 5. 文档分工

- [Server/impl.md](Server/impl.md)：包、文件、实体、配置、DI、Cookie、Controller、Migration 和运行命令。
- [Server/Modules/AccountAccess/impl.md](Server/Modules/AccountAccess/impl.md)：账号访问用例、结果类型、规范化、种子和失败映射。
- [Server/Modules/UserManagement/impl.md](Server/Modules/UserManagement/impl.md)：列表、添加、昵称修改、固定密码重置和并发冲突映射。
- [Client/impl.md](Client/impl.md)：目录、公共 Client、路由、Provider 组装和构建。
- [Client/Modules/Authentication/impl.md](Client/Modules/Authentication/impl.md)：认证状态机、API 适配、竞态控制和 401 信号。
- [Client/Modules/UserManagement/impl.md](Client/Modules/UserManagement/impl.md)：用户管理状态、API 适配和认证资料刷新协作。
- 页面和组件目录下的 `impl.md`：具体 JSX、输入输出、状态和样式落点。
- [testPlan.md](testPlan.md)：证明每个阶段行为的测试编号和环境。

## 6. 完成定义

003 主题只有在以下条件全部满足时才完成：

1. 两个验收账号可通过配置幂等初始化。
2. 登录、me、logout 的 HTTP、Cookie、错误和 OpenAPI 契约稳定。
3. 页面刷新保持登录，退出后受保护入口不可访问。
4. Application、Web、Repository 和客户端职责符合设计分层。
5. 密码正文、密码哈希、Cookie、配置秘密不进入响应、日志、OpenAPI 示例或客户端长期状态；表单中的密码只在当前输入与请求生命周期内短暂存在。
6. 002 的构建、系统状态、ProblemDetails、Kiota 和发布回归继续通过。
7. 已实现用户列表、添加账号、昵称修改和固定密码重置。
8. 没有实现搜索、分页、删除、禁用、用户自行改密、角色、权限或审计。
9. 用户管理所有写接口使用方法级 Origin metadata，并保留 PATCH 与 routing 404/405 语义。
10. 用户管理成功和错误响应全部 no-store/no-cache，密码和账号目录不进入持久化客户端缓存。
