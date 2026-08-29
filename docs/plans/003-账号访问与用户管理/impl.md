# 账号访问与用户管理主题实现方案

## 1. 文档定位

本文把[主题总体设计](design.md)拆解为可执行的实施顺序、跨端交付物和阶段验收门。服务端工程与认证管道见[服务端实现方案](Server/impl.md)，账号访问模块内部实现见[服务端账号访问模块实现方案](Server/Modules/AccountAccess/impl.md)，客户端工程接入见[客户端实现方案](Client/impl.md)，认证状态、页面和组件实现见对应子目录的 `impl.md`。完整测试编号和浏览器验收见[测试方案](testPlan.md)。

本文不记录实施日志，也不扩展主题设计已经明确延期的用户管理、安全治理或高级会话能力。

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

主题完成后，新增账号的唯一途径仍是更新受保护部署配置并重启应用；在线添加用户、密码重置和密码找回均未提供。这是用户明确接受的阶段性运营限制，主题三不得假设存在动态建号入口。所有部署至少需要一个有效预置账号，只有验收环境要求至少两个账号以完成多人场景验证。

不交付添加用户、用户列表、资料修改、密码修改或重置、角色权限、审计、找回密码、多因素认证、持久化会话或会议业务。

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

## 5. 文档分工

- [Server/impl.md](Server/impl.md)：包、文件、实体、配置、DI、Cookie、Controller、Migration 和运行命令。
- [Server/Modules/AccountAccess/impl.md](Server/Modules/AccountAccess/impl.md)：账号访问用例、结果类型、规范化、种子和失败映射。
- [Client/impl.md](Client/impl.md)：目录、公共 Client、路由、Provider 组装和构建。
- [Client/Modules/Authentication/impl.md](Client/Modules/Authentication/impl.md)：认证状态机、API 适配、竞态控制和 401 信号。
- 页面和组件目录下的 `impl.md`：具体 JSX、输入输出、状态和样式落点。
- [testPlan.md](testPlan.md)：证明每个阶段行为的测试编号和环境。

## 6. 完成定义

003 主题只有在以下条件全部满足时才完成：

1. 两个验收账号可通过配置幂等初始化。
2. 登录、me、logout 的 HTTP、Cookie、错误和 OpenAPI 契约稳定。
3. 页面刷新保持登录，退出后受保护入口不可访问。
4. Application、Web、Repository 和客户端职责符合设计分层。
5. 密码正文、密码哈希、Cookie、配置秘密不进入响应、日志、OpenAPI 示例或客户端状态。
6. 002 的构建、系统状态、ProblemDetails、Kiota 和发布回归继续通过。
7. 没有实现添加用户、改密、重置密码、角色、审计或其他延期能力。
