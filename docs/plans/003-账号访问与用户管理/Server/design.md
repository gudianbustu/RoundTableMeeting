# 账号访问与用户管理服务端设计

## 1. 文档定位

本文定义账号访问主题在服务端的工程分层、认证方式、账号持久化、初始账号初始化、HTTP 入口、当前用户上下文和最小访问控制。主题范围与跨端契约以[主题总体设计](../design.md)为准；服务端账号访问模块的用例和内部契约以[账号访问模块设计](Modules/AccountAccess/design.md)为准。

本文只为后续主题提供最小可用的人类身份，不建设完整 ASP.NET Core Identity 用户中心，也不实现需求文档中记录但本次延期的用户管理和账号安全能力。

## 2. 服务端目标

服务端完成后应能够：

1. 从配置读取并幂等初始化预置账号。
2. 验证用户名和密码并建立 Cookie 会话。
3. 从会话解析当前 `UserId`，并通过统一抽象提供当前用户资料。
4. 支持当前浏览器退出登录。
5. 对后续业务入口提供统一认证和授权边界。
6. 在错误响应和结构化日志中保护密码、Cookie 和内部实现细节。

## 3. 分层职责

### 3.1 Domain

`RoundTableMeeting.Domain` 定义 `UserAccount` 人类账号实体及其不依赖 ASP.NET Core 的基础约束。实体至少具有 `Guid` 类型的稳定主键、用户名、昵称和密码哈希。领域实体不持有 Cookie、`ClaimsPrincipal`、HTTP 上下文或 EF Core 类型。

本主题不在 Domain 中加入角色、组织、账号状态机、密码策略、审计字段或会话实体。密码校验由 Application 使用密码哈希器完成，Domain 只保存不可逆凭证摘要。

### 3.2 Abstraction

`RoundTableMeeting.Abstraction` 提供跨项目稳定契约：

- 当前用户上下文接口；
- 登录、当前用户查询和退出所需的 DTO 与应用服务接口；
- `IAccountSeeder` 账号种子用例契约。

Abstraction 不暴露 Domain 实体、EF Core 查询类型、Cookie 选项或 ASP.NET Core 内部认证对象。

### 3.3 Application

`RoundTableMeeting.Application` 负责：

- 根据用户名查询账号；
- 调用 `IPasswordHasher<UserAccount>` 验证密码；
- 将账号实体转换为当前用户 DTO；
- 判断登录成功或无效凭证；
- 实现 `IAccountSeeder`，编排账号种子校验、查重、密码哈希和新增提交。

Application 不直接访问 `HttpContext`，不写入认证 Cookie，不返回 `ClaimsPrincipal`，不引用 Repository 或 EF Core。

### 3.4 Repository.Sqlite

`RoundTableMeeting.Repository.Sqlite` 负责：

- `UserAccount` 的 EF Core 映射和 Migration；
- 按稳定用户名查询账号；
- 账号新增和初始化所需的持久化操作；
- 用户名唯一约束；
- 与现有 `RoundTableMeetingDbContext` 和 `IUnitOfWork` 集成。

Repository 不比较密码、不解析 Cookie、不决定 HTTP 状态码，也不向客户端暴露实体。

### 3.5 Web 宿主

`RoundTableMeeting` 宿主负责：

- 注册 ASP.NET Core Cookie Authentication；
- 注册授权和当前用户访问器；
- 注册认证 Controller；
- 把登录成功的 `UserId` 写入最小认证 Claim；
- 按统一错误边界返回 `401`、验证错误和无效凭证错误；
- 在启动阶段触发数据库迁移及账号种子初始化；
- 将登录状态应用到后续 Controller、实时入口和页面托管。

Controller 只做请求绑定、调用应用服务、建立或清除 Cookie 和返回 DTO，不直接查询数据库。

## 4. 认证技术方案

### 4.1 Cookie Authentication

使用 ASP.NET Core `AddAuthentication().AddCookie()`，不引入完整 ASP.NET Core Identity，不自行实现 Token 格式。浏览器通过同源 HTTP 请求自动携带 Cookie；客户端不读取、解码或持久化 Cookie。

Cookie 至少使用以下约束：

- `HttpOnly = true`；
- `SameSite = Lax`；
- 生产环境 `SecurePolicy = Always`；
- 明确的认证 Cookie 名称和根路径；
- 本主题采用会话级 Cookie，不设置长期持久化登录作为产品承诺。

Cookie 只包含识别账号所需的最小 Claim，至少包含 `UserId`。请求级当前用户上下文只暴露 `IsAuthenticated` 和 `UserId`；用户名和昵称由当前用户查询从服务端读取，不依赖 Cookie Claim 或客户端缓存作为资料事实来源。

### 4.2 认证事件

认证 Cookie 的验证由 ASP.NET Core 中间件完成。`ICurrentUserContext` 接口位于 Abstraction，实现位于 Web 的 `Authentication/` 边界；实现读取已经验证的 `ClaimsPrincipal`，将 `UserId` 转换为稳定的 `Guid`。Application 只消费该接口，不访问 `HttpContext`、`ClaimsPrincipal` 或 Cookie 类型。如果 Claim 缺失或格式错误，则按未认证处理，不从请求参数回退推断身份；账号是否仍存在由当前用户查询用例验证。

本主题不配置滑动续期、跨设备会话、服务端会话撤销表或强制失效所有设备。后续增加这些能力时必须重新设计会话存储和失效语义。

## 5. 账号持久化与初始化

### 5.1 账号概念

服务端持久化一个人类账号聚合，概念字段包括：

- `UserId`：`Guid` 主键；
- `Username`：唯一登录名称；
- `Nickname`：后续首次加入会议时使用的默认名称；
- `PasswordHash`：密码哈希器输出；
- 本主题账号表只包含上述身份和凭证字段，不预留创建时间、审计、状态或扩展字段。

本主题不增加角色、权限集合、密码重置标志、首次登录标志、禁用标志或完整审计字段。若后续业务需要账号状态，必须另行设计，不以空字段预留的方式扩大当前模型。

用户名在本主题采用最小稳定比较语义：显示用户名先去除首尾空格并转换为 Unicode NFC，规范化后为空则拒绝；比较值 `NormalizedUsername` 在此基础上执行 `ToUpperInvariant()`。数据库对 `NormalizedUsername` 建立普通精确唯一索引，配置内查重和仓储查询也使用同一比较值，不依赖 SQLite `NOCASE` 或不同运行时的默认字符串比较。登录和当前用户接口返回保存的显示用户名，不返回比较值。

`UserAccount.ID` 继承自 `BaseEntity`，它就是平台和跨端契约中的 `UserId`；实体不得再声明第二个 `UserId` 属性。`CurrentUserDto.UserId` 直接映射 `UserAccount.ID`，后续会议创建者、参与者和消息发送者同样引用该 Guid。密码不执行 Trim、Unicode 规范化或大小写转换，配置和登录请求中的原始字符串直接交给密码哈希器。

### 5.2 配置结构

固定使用独立的 `Authentication` 配置节，包含允许浏览器源和初始账号集合。每个账号必须提供用户名、昵称和初始密码。配置绑定对象只在服务端使用，不能被 OpenAPI、客户端构建或状态接口返回。

Development 使用 user-secrets 提供非生产凭证，Testing 使用测试内存配置，生产使用环境变量或部署 Secret Provider；密码不得提交到仓库。

### 5.3 幂等种子

数据库 Migration 完成后执行账号种子：

1. 校验配置账号集合非空且配置内用户名不重复。
2. 按用户名查找已存在账号。
3. 不存在时使用密码哈希器生成摘要并新增账号。
4. 已存在时不覆盖昵称、密码或其他已保存资料。
5. 再次启动不重复新增，也不重置用户密码。
6. 任何需要提供人类登录的部署环境中，配置缺失、无有效账号或种子执行失败时都终止启动，不开放一个无法登录的应用。

Web 宿主拥有 Options 绑定和 `ValidateOnStart`，将验证通过的账号配置映射为 Abstraction 中的种子输入 DTO。宿主在同一个启动作用域内先调用现有 `IDatabaseInitializer` 完成 Migration，再把种子输入传给 Application 实现的 `IAccountSeeder`。Application 不引用 Web Options 或配置 Provider；种子用例通过领域仓储和 `IUnitOfWork` 持久化。Repository 不读取配置，也不拥有种子编排。种子失败与 Migration 失败使用同一启动异常边界并向上抛出，宿主不继续监听业务请求。

## 6. HTTP API 设计

### 6.1 路由总览

| 方法 | 路径 | 认证 | 作用 |
| --- | --- | --- | --- |
| `POST` | `/api/auth/login` | 匿名 | 验证凭证并建立当前浏览器会话 |
| `GET` | `/api/auth/me` | 必须认证 | 返回当前账号的最小资料 |
| `POST` | `/api/auth/logout` | 可匿名 | 清除当前浏览器会话，保持幂等 |

授权配置固定设置 `FallbackPolicy = RequireAuthenticatedUser()`，使没有显式授权元数据的业务端点默认要求认证。登录、退出、系统状态、OpenAPI、Scalar、API 404 fallback 和 SPA 页面 fallback 显式标记 `AllowAnonymous`；静态文件由静态文件中间件直接提供。会议、参与者、消息、实时连接和其他人类业务入口不得使用 `AllowAnonymous`。

无效登录凭证固定返回 `401`，并使用 `code = "invalid_credentials"`；请求字段校验失败固定返回 `400`、`code = "validation_failed"` 和传输字段 `errors`；不支持的请求媒体类型返回 `415`、`code = "unsupported_media_type"`。`GET /api/auth/me` 没有有效会话时返回 `401`、`code = "unauthenticated"`。通用已认证但权限不足错误固定为 `403`、`code = "forbidden"`。三个认证 operation 必须在 OpenAPI 中声明对应的类型化错误响应，供 Kiota 生成错误映射。

Cookie Authentication 的重定向事件必须针对 `/api/*` 覆盖默认行为：未认证 Challenge 直接返回 `401` ProblemDetails，禁止重定向到 HTML 登录页；当前主题没有业务权限不足场景，后续出现已认证但无权限时固定使用 `403` ProblemDetails。

### 6.2 登录

请求只包含用户名和密码。服务端先做基本输入验证，再按用户名查询账号并调用密码哈希器验证。成功时建立 Cookie，并返回 `UserId`、用户名和昵称；失败时统一返回无效凭证，不区分用户名不存在和密码不匹配。

用户名不存在时仍必须执行与正常密码验证同类的 dummy hash 验证，再返回统一失败，降低通过响应耗时枚举有效用户名的风险。自动化测试验证 dummy 验证被调用，不对不稳定的绝对耗时设阈值。

登录 Controller 不把密码传给日志、异常或下游响应。应用服务不返回密码哈希给 Web 以外的调用方。

### 6.3 当前用户

`GET /api/auth/me` 只从已验证会话读取当前 `UserId`，再查询账号资料。返回字段固定为当前跨端契约所需的最小集合。没有有效会话、Claim 无法解析或账号已不存在时返回 `401`。

### 6.4 退出

`POST /api/auth/logout` 清除当前 Cookie。即使请求没有有效会话，也不把它当作服务器错误；接口保持幂等并返回成功或统一的空结果。退出后同一浏览器再次访问受保护接口必须得到 `401`。

### 6.5 Cookie 写请求保护

浏览器客户端和 API 在生产环境必须保持同源，服务端不开放允许携带凭证的跨源 CORS。使用 Cookie 改变状态的浏览器端点通过明确的 `RequireBrowserOrigin` 端点元数据登记；登录和退出必须登记，后续业务写端点引入时必须同步登记。校验中间件只处理匹配到该元数据的端点，未知 API 和未标记端点继续进入原有授权或 404 边界。带请求体的登记端点只接受预期 HTTP 方法和 `application/json` 请求；无请求体的退出接口只接受 `POST`，不以不存在的 Content-Type 作为失败条件。所有登记端点都必须携带 `Origin`；生产同源部署时该值必须与配置的浏览器源一致，开发环境通过明确配置允许 Vite 浏览器源（例如 `http://localhost:5173`），不把代理转发后的 API Host 当作浏览器源；缺失、`null` 或未配置的外部 Origin 一律拒绝。`SameSite=Lax`、同源或受控开发源限制和 Origin 校验共同构成本主题的最小跨站请求保护；未来若开放跨源客户端、嵌入式页面或第三方集成，必须重新评估并引入 ASP.NET Core Antiforgery 或等价双重提交令牌方案。

## 7. 授权和公开入口

### 7.1 中间件顺序

宿主在现有异常、请求日志和静态文件管道基础上加入：

1. `UseAuthentication()` 解析 Cookie；
2. `UseAuthorization()` 执行 `[Authorize]` 或等价策略；
3. Controller 和其他业务入口使用当前用户上下文。

认证中间件不能放在静态文件和 SPA 回退之后导致 API 保护失效。未知 `/api/*` 仍返回 ProblemDetails 404，不被 SPA fallback 转成 HTML。

### 7.2 系统状态

`GET /api/system/status` 可以继续匿名访问，作为框架诊断入口。该接口只能返回非敏感服务状态，不返回账号数量、用户名、数据库路径、连接字符串、Cookie 或环境秘密。是否在未来改为内部健康接口不属于本主题。

### 7.3 后续业务保护

后续会议主题在认证通过后，还必须执行自己的会议成员或创建者权限判断：

- 已登录不等于属于任意会议；
- 会议码不能替代人类账号认证；
- Controller、实时 Hub 或其他入口不能自行解析 Cookie；
- 业务写入和读取均从统一当前用户上下文取得 `UserId`。

## 8. 错误与日志

- 无效凭证返回统一业务错误，不能通过状态、文案或响应字段泄露账号是否存在。
- 未认证访问受保护 API 返回 `401`，不使用 HTML 登录重定向掩盖 API 错误。
- 输入校验复用现有 ProblemDetails 传输字段 `errors`；客户端公共错误层再将其转换为 `fieldErrors`，服务端不得输出 `fieldErrors`。
- 认证日志只记录成功、失败或退出的结果、请求关联 `traceId` 和必要的非敏感摘要。
- 禁止记录密码正文、密码哈希、认证 Cookie、Claim 原文、完整请求体、Authorization 头或配置秘密。
- 异常响应不返回内部堆栈、数据库路径、连接字符串或密码比较细节。

本主题不建立登录审计表、密码重置历史或管理员操作日志；“有请求日志”不等于“实现业务审计”。

首版明确不提供登录失败限流、账号锁定、密码复杂度和最小长度策略；配置密码只要求非空且不超过 256 个字符。这是为验证核心业务做出的有意简化，不构成长期安全承诺，后续账号安全主题必须重新评估。

## 9. 服务端扩展点

后续主题只依赖以下稳定入口：

- 当前用户上下文；
- 账号 `UserId` 与昵称查询；
- 已配置的认证和授权中间件；
- 认证 API 的 Cookie 会话语义。

后续主题不得依赖本主题的具体 Cookie 名称、Claim 字符串、Repository 类型或账号数据库表名。若未来更换认证实现，应保持当前用户上下文和 HTTP 业务契约兼容，或在主题设计中明确迁移方案。

## 10. 服务端非目标

本主题服务端不实现：

- 用户创建、用户资料修改和用户列表；
- 密码修改、密码重置、找回密码和密码过期；
- 账号状态管理、角色和组织权限；
- 登录风控、多因素认证、第三方身份源；
- 持久化会话、设备管理和全局会话撤销；
- 账号审计和安全运营后台。
