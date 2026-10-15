# 账号访问与用户管理服务端设计

## 1. 文档定位

本文定义账号访问主题在服务端的工程分层、认证方式、账号持久化、初始账号初始化、HTTP 入口、当前用户上下文和最小用户管理。主题范围与跨端契约以[主题总体设计](../design.md)为准；登录和会话用例以[账号访问模块设计](Modules/AccountAccess/design.md)为准，用户列表与管理操作以[用户管理模块设计](Modules/UserManagement/design.md)为准。

本文提供人类身份、管理员授权和个人设置；不建设完整 ASP.NET Core Identity 用户中心、复杂角色体系、审计或账号生命周期治理。

当前服务端源码已经提供 `/api/users` 用户列表、添加账号、昵称修改和固定密码重置接口；本设计继续作为后续维护与回归依据。

公共 `OriginValidationMiddleware` 已由提交 `4abd58e` 升级完成：HTTP 方法合法性由 endpoint routing 和 action 特性决定，中间件不硬编码 POST；带 `RequireBrowserOrigin` 元数据的 POST、PATCH、PUT、DELETE 均可复用 Origin 校验，并按 `RequireJsonBody` 决定是否要求 `application/json`。用户管理不再有框架前置阻断。

## 2. 服务端目标

服务端完成后应能够：

1. 从配置读取并幂等初始化预置账号。
2. 验证用户名和密码并建立 Cookie 会话。
3. 从会话解析当前 `UserId`，并通过统一抽象提供当前用户资料。
4. 支持当前浏览器退出登录。
5. 对后续业务入口提供统一认证和授权边界。
6. 在错误响应和结构化日志中保护密码、Cookie 和内部实现细节。
7. 仅允许管理员查看用户列表、添加账号、修改昵称、固定重置密码和调整管理员身份。
8. 所有用户可修改本人昵称和密码；至少保留一名管理员。

## 3. 分层职责

### 3.1 Domain

`RoundTableMeeting.Domain` 定义 `UserAccount` 人类账号实体及其不依赖 ASP.NET Core 的基础约束。实体至少具有 `Guid` 类型的稳定主键、用户名、昵称、管理员标志和密码哈希。领域实体不持有 Cookie、`ClaimsPrincipal`、HTTP 上下文或 EF Core 类型。

Domain 增加 IsAdmin 布尔标志，不加入复杂角色、组织、账号状态机、审计字段或会话实体。密码校验由 Application 使用密码哈希器完成，Domain 保存账号资料和不可逆凭证摘要。

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
- 实现用户管理和个人设置服务，编排管理员授权、身份修改、昵称与密码写入及事务内校验。

Application 不直接访问 `HttpContext`，不写入认证 Cookie，不返回 `ClaimsPrincipal`，不引用 Repository 或 EF Core。

### 3.4 Repository.Sqlite

`RoundTableMeeting.Repository.Sqlite` 负责：

- `UserAccount` 的 EF Core 映射和 Migration；
- 按稳定用户名查询账号；
- 账号新增和初始化所需的持久化操作；
- 用户名唯一约束；
- 与现有 `RoundTableMeetingDbContext` 和 `IUnitOfWork` 集成。
- 提供小规模账号列表读取。

Repository 不比较密码、不解析 Cookie、不决定 HTTP 状态码，也不向客户端暴露实体。

### 3.5 Web 宿主

`RoundTableMeeting` 宿主负责：

- 注册 ASP.NET Core Cookie Authentication；
- 注册授权和当前用户访问器；
- 注册认证 Controller；
- 注册用户管理、个人设置 Controller 和 AdminOnly 策略；
- 把登录成功的 `UserId` 写入最小认证 Claim；
- 按统一错误边界返回 `401`、验证错误和无效凭证错误；
- 在启动阶段触发数据库迁移及账号种子初始化；
- 将登录状态应用到后续 Controller、实时入口和页面托管。
- 对用户管理写接口复用 Origin 元数据和认证响应安全约束。

Controller 只做请求绑定、调用应用服务、建立或清除 Cookie 和返回 DTO，不直接查询数据库。

## 4. 认证技术方案

### 4.1 Cookie Authentication

使用 ASP.NET Core `AddAuthentication().AddCookie()`，不引入完整 ASP.NET Core Identity，不自行实现 Token 格式。浏览器通过同源 HTTP 请求自动携带 Cookie；客户端不读取、解码或持久化 Cookie。

Cookie 至少使用以下约束：

- `HttpOnly = true`；
- `SameSite = Lax`；
- Cookie `SecurePolicy = SameAsRequest`，依据实际公开请求协议决定 Secure；使用可信代理终止 TLS 时须在认证前处理受信转发协议，不能仅按 Production 环境强制 Secure；
- 明确的认证 Cookie 名称和根路径；
- 本主题采用会话级 Cookie，不设置长期持久化登录作为产品承诺。

Cookie 只包含识别账号所需的最小 Claim，至少包含 `UserId`。请求级当前用户上下文只暴露 `IsAuthenticated` 和 `UserId`；用户名、昵称和 IsAdmin 由当前用户查询从服务端读取，不依赖 Cookie Claim 或客户端缓存作为资料事实来源。

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
- `IsAdmin`：非空布尔值，默认 false；不预留创建时间、审计或账号状态字段。

本主题不增加角色表、权限集合、密码重置标志、首次登录标志、禁用标志或完整审计字段。若后续业务需要账号状态，必须另行设计，不以空字段预留的方式扩大当前模型。

账号实体增加明确的昵称修改和密码哈希替换行为。昵称修改复用 Trim + NFC 和 64 字符上限；密码重置由 Application 生成新哈希后替换旧哈希，不允许 Controller 或 Repository 直接写入明文密码。

用户名在本主题采用最小稳定比较语义：显示用户名先去除首尾空格并转换为 Unicode NFC，规范化后为空则拒绝；比较值 `NormalizedUsername` 在此基础上执行 `ToUpperInvariant()`。数据库对 `NormalizedUsername` 建立普通精确唯一索引，配置内查重和仓储查询也使用同一比较值，不依赖 SQLite `NOCASE` 或不同运行时的默认字符串比较。登录和当前用户接口返回保存的显示用户名，不返回比较值。

`UserAccount.ID` 继承自 `BaseEntity`，它就是平台和跨端契约中的 `UserId`；实体不得再声明第二个 `UserId` 属性。`CurrentUserDto.UserId` 直接映射 `UserAccount.ID`，后续会议创建者、参与者和消息发送者同样引用该 Guid。密码不执行 Trim、Unicode 规范化或大小写转换，配置和登录请求中的原始字符串直接交给密码哈希器。

### 5.2 配置结构

固定使用独立的 `Authentication` 配置节，包含允许浏览器源和初始账号集合。每个账号必须提供用户名、昵称和初始密码。配置绑定对象只在服务端使用，不能被 OpenAPI、客户端构建或状态接口返回。

Development 使用 user-secrets 提供非生产凭证，Testing 使用测试内存配置，生产使用环境变量或部署 Secret Provider；密码不得提交到仓库。

### 5.3 幂等种子

数据库 Migration 完成后执行账号种子和初始管理员初始化，具体顺序如下：

1. 按现有规则验证非空 SeedAccounts 集合、规范化用户名唯一及密码输入；开启写事务并取得写锁，再查询和创建缺失账号，新账号默认 IsAdmin=false。
2. 增加独立配置 `Authentication:InitialAdminUsername`，仅在数据库没有任何管理员时使用。空库和旧库升级时，该值必须显式配置，并按同一用户名规范化规则匹配种子执行后的一个账号。
3. 若数据库没有管理员，在同一写事务中重新检查管理员数量，并仅把指定账号提升为管理员；目标不存在、配置缺失或无效时启动失败，事务回滚，不开放业务请求。不默认选择第一条记录，也不按用户名猜测管理员。
4. 若数据库已有管理员，不应用 InitialAdminUsername；已存在账号的昵称、密码和管理员身份均不覆盖。后续新建的种子账号仍默认为普通用户。这样正常重启不会恢复已撤销的身份。
5. 提交前确认至少存在一个管理员。并行启动应串行化初始化写入，等待后重新检查，不能产生重复账号或覆盖在线管理结果。

本规则在首次升级前要求部署者明确提供 InitialAdminUsername；当前设计不指定任何真实账号。初始管理员配置不作为运行期授权来源。没有管理员时显式配置可以完成恢复，不引入额外初始化标志或自动提升所有旧账号。

Web 绑定并验证 Options，将 SeedAccounts 与可选 InitialAdminUsername 映射到 Abstraction 的种子契约；只有数据库无管理员时才强制要求后者。Application 编排事务和校验，不引用 Web Options；Repository 负责写事务和按列更新。失败沿现有启动异常边界终止启动，不记录配置值或秘密。

## 6. HTTP API 设计

### 6.1 路由总览

| 方法 | 路径 | 认证 | 作用 |
| --- | --- | --- | --- |
| `POST` | `/api/auth/login` | 匿名 | 验证凭证并建立当前浏览器会话 |
| `GET` | `/api/auth/me` | 必须认证 | 返回当前账号的最小资料 |
| `POST` | `/api/auth/logout` | 可匿名 | 清除当前浏览器会话，保持幂等 |
| `GET` | `/api/users` | 管理员 | 返回全部账号的最小列表 |
| `POST` | `/api/users` | 管理员 | 添加账号 |
| `PATCH` | `/api/users/{userId}/nickname` | 管理员 | 修改账号昵称 |
| `POST` | `/api/users/{userId}/reset-password` | 管理员 | 将密码重置为 `123456` |

授权配置固定设置 `FallbackPolicy = RequireAuthenticatedUser()`，使没有显式授权元数据的业务端点默认要求认证。登录、退出、系统状态、OpenAPI、Scalar、API 404 fallback 和 SPA 页面 fallback 显式标记 `AllowAnonymous`；静态文件由静态文件中间件直接提供。会议、参与者、消息、实时连接和其他人类业务入口不得使用 `AllowAnonymous`。

无效登录凭证固定返回 `401`，并使用 `code = "invalid_credentials"`；请求字段校验失败固定返回 `400`、`code = "validation_failed"` 和传输字段 `errors`；不支持的请求媒体类型返回 `415`、`code = "unsupported_media_type"`。`GET /api/auth/me` 没有有效会话时返回 `401`、`code = "unauthenticated"`。通用已认证但权限不足错误固定为 `403`、`code = "forbidden"`。用户管理的重复用户名固定返回 `409`、`code = "username_conflict"`；目标账号不存在固定返回 `404`、`code = "user_not_found"`。认证和用户管理 operation 都必须在 OpenAPI 中声明实际可能返回的类型化错误响应，供 Kiota 生成错误映射。

Cookie Authentication 的重定向事件必须针对 `/api/*` 覆盖默认行为：未认证 Challenge 直接返回 `401` ProblemDetails，禁止重定向到 HTML 登录页；普通用户调用管理接口时使用 `403 forbidden` ProblemDetails。`username_conflict` 和 `user_not_found` 只属于用户管理业务错误，不代表当前引入角色权限。

UsersController 统一使用 AdminOnly 策略，由服务端依据数据库当前 IsAdmin 授权；普通用户不能查询账号目录或管理他人。管理员身份修改与个人设置按下文契约实施。

### 6.2 登录

请求只包含用户名和密码。服务端先做基本输入验证，再按用户名查询账号并调用密码哈希器验证。成功时建立 Cookie，并返回 `UserId`、用户名、昵称和 IsAdmin；失败时统一返回无效凭证，不区分用户名不存在和密码不匹配。

用户名不存在时仍必须执行与正常密码验证同类的 dummy hash 验证，再返回统一失败，降低通过响应耗时枚举有效用户名的风险。自动化测试验证 dummy 验证被调用，不对不稳定的绝对耗时设阈值。

登录 Controller 不把密码传给日志、异常或下游响应。应用服务不返回密码哈希给 Web 以外的调用方。

### 6.3 当前用户

`GET /api/auth/me` 只从已验证会话读取当前 `UserId`，再查询账号资料。返回字段固定为当前跨端契约所需的最小集合。没有有效会话、Claim 无法解析或账号已不存在时返回 `401`。

### 6.4 退出

`POST /api/auth/logout` 清除当前 Cookie。即使请求没有有效会话，也不把它当作服务器错误；接口保持幂等并返回成功或统一的空结果。退出后同一浏览器再次访问受保护接口必须得到 `401`。

### 6.5 Cookie 写请求保护

浏览器客户端和 API 在生产环境必须保持同源，服务端不开放允许携带凭证的跨源 CORS。使用 Cookie 改变状态的浏览器端点通过明确的 `RequireBrowserOrigin` 端点元数据登记；登录、退出、添加账号、昵称修改和密码重置必须登记，后续业务写端点引入时必须同步登记。校验中间件只处理匹配到该元数据的端点，未知 API 和未标记端点继续进入原有授权或 404 边界。POST、PATCH、PUT、DELETE 是否允许由 endpoint routing 决定；中间件不能把非 POST 请求统一误判为 `415`。带 JSON 正文的登记端点要求 `application/json`，无正文端点不因缺少 Content-Type 失败。所有登记端点都必须携带 `Origin`；生产同源部署时该值必须与配置的浏览器源一致，开发环境通过明确配置允许 Vite 浏览器源（例如 `http://localhost:5173`），不把代理转发后的 API Host 当作浏览器源；缺失、`null` 或未配置的外部 Origin 一律拒绝。`SameSite=Lax`、同源或受控开发源限制和 Origin 校验共同构成本主题的最小跨站请求保护；未来若开放跨源客户端、嵌入式页面或第三方集成，必须重新评估并引入 ASP.NET Core Antiforgery 或等价双重提交令牌方案。

添加账号和昵称修改带 JSON 正文，使用 `RequireBrowserOrigin(true)`；添加使用 POST，昵称修改使用 PATCH。密码重置无请求正文，使用 POST 和默认 `RequireBrowserOrigin`。用户列表是只读 GET，不需要 Origin 元数据。框架回归必须另外覆盖带正文 PUT 与无正文 DELETE 的标记端点，证明中间件没有方法硬编码。

系统框架升级完成门固定为：

| 场景 | 预期 |
| --- | --- |
| 标记的 POST + JSON + 允许 Origin | 进入 endpoint |
| 标记的 PATCH + JSON + 允许 Origin | 进入 endpoint |
| 标记的 PUT + JSON + 允许 Origin | 进入 endpoint |
| 标记的 DELETE + 无正文 + 允许 Origin | 进入 endpoint |
| 上述任一方法缺失或使用外部 Origin | `403 origin_not_allowed` |
| `RequireJsonBody=true` 且媒体类型不是 `application/json` | `415 unsupported_media_type` |
| 方法与路由不匹配 | 由 routing 返回 404/405，不由 Origin 中间件改写为 415 |
| 未标记的 API | 不受 Origin 中间件拦截，保持原授权或 404 语义 |

### 6.6 用户列表与添加

`GET /api/users` 返回按规范化用户名稳定排序的完整数组，元素只包含 `userId`、`username`、`nickname`、`isAdmin`。面向小规模账号管理，不增加分页、搜索或总数元数据。

`POST /api/users` 接收用户名、昵称和初始密码。用户名冲突返回 `409`、`code = username_conflict`；输入错误返回 `400 validation_failed`。成功返回 `201` 和新账号最小 DTO。新账号立即可以登录，创建过程不修改配置种子。

`username_conflict` 必须登记到统一 `ProblemDetailsFactory`，通过 `ApiProblemDetailsWriter` 输出安全固定标题、detail 和 traceId；Controller 不自行构造 ProblemDetails。OpenAPI operation 必须声明 `400/401/403/409/415/500`，Kiota 生成结果必须包含对应错误映射。

### 6.7 昵称修改

`PATCH /api/users/{userId}/nickname` 只接收 nickname，成功返回更新后的最小 DTO。账号不存在返回 `404 user_not_found`。昵称修改只更新账号资料；会议内显示名称规则由后续会议主题定义。若修改的是当前账号，客户端在操作成功后重新读取当前用户资料。

`user_not_found` 同样登记到统一 `ProblemDetailsFactory`，并由昵称修改和密码重置 operation 声明类型化 `404` 错误响应，不能通过异常消息或局部 JSON 返回。

### 6.8 密码重置

`POST /api/users/{userId}/reset-password` 不接收请求正文，固定将密码替换为 `123456` 的哈希并返回 `204`。账号不存在返回 `404 user_not_found`。响应和日志不返回固定密码、旧哈希或新哈希；客户端在确认对话框中依据冻结契约展示固定值。

密码重置不撤销已有 Cookie，不建立首次登录改密标志，也不记录重置人、时间或历史。这些能力与后续权限、审计和会话治理一起设计。

五个 `/api/users` operation 的成功和错误响应统一设置 `Cache-Control: no-store` 和 `Pragma: no-cache`。用户列表虽然是 GET，也不能被浏览器或中间缓存保存；实现时扩展现有认证响应头中间件的路径/元数据覆盖范围，不能只覆盖 `/api/auth/*`。

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

用户管理操作同样不记录业务审计，不记录操作人和目标账号关联历史。请求日志只能记录路由、状态和 traceId，不记录初始密码、固定重置密码、密码哈希或完整请求体。

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

- 用户搜索、分页、删除、禁用、批量导入和公开注册；
- 用户名修改、自助找回密码和密码过期；
- 账号状态管理、复杂角色和组织权限；
- 登录风控、多因素认证、第三方身份源；
- 持久化会话、设备管理和全局会话撤销；
- 账号审计和安全运营后台。

## 11. 管理员与个人设置接口

| 方法 | 路径 | 授权 | 请求 | 成功 |
| --- | --- | --- | --- | --- |
| PATCH | `/api/users/{userId}/admin` | 管理员 | `{ isAdmin: boolean }`，必填 | 200 UserSummaryDto |
| PATCH | `/api/account/nickname` | 已登录且账号存在 | `{ nickname: string }` | 200 CurrentUserDto |
| POST | `/api/account/change-password` | 已登录且账号存在 | `{ currentPassword: string, newPassword: string }` | 204 |

个人设置通过现有 GET `/api/auth/me` 读取 UserId、Username、Nickname、IsAdmin，无需单独资料 GET。本人写接口只从 ICurrentUserContext.UserId 取目标，不接受客户端指定目标 UserId 或 IsAdmin；管理员也不能用该接口修改他人。

所有新增写接口使用 RequireBrowserOrigin(true)，所有响应使用 NoStoreResponse。个人接口未登录或当前账号不存在返回 401 unauthenticated；管理员 API 未登录返回 401，普通用户或无有效管理员账号返回 403 forbidden。权限判断先于目标账号查询，普通用户不能利用 404 枚举账号。

管理员身份设置：目标不存在为 404 user_not_found；取消最后一名管理员为 409 last_admin_required；目标已是请求状态则返回当前摘要，保持幂等。本人降级成功仍返回 200，下一次管理请求被拒绝。

自行改密：currentPassword 按登录相同规则验证，不 Trim 或规范化，长度上限 256；错误返回 400 validation_failed，errors.currentPassword 指明当前密码不正确，不用 401 以免误清登录状态。newPassword 至少包含一个非空白字符且最长 256，原字符串哈希；不要求与旧密码不同。confirmNewPassword 仅为客户端输入，不进入 HTTP 契约。任何校验失败不写库，成功只替换 PasswordHash，不返回密码或哈希。

接口按实际声明 400/401/403/404/409/415/500：个人写入声明 400/401/403（Origin）/415/500；管理员设置声明全部七类；用户列表补充 403。沿用统一 ProblemDetails 的 errors、code 与 traceId，Kiota 同步生成。

## 12. 管理员授权与并发写入

Cookie 保持只含 UserId；AdminOnly 授权处理器调用应用层管理员查询契约，从数据库加载当前账号 IsAdmin，不依赖客户端字段或登录时快照，不引入跨请求权限缓存。请求级共享读取可以复用，但写事务中必须重新判断操作者。

管理员变更事务先取得 SQLite 写锁，再重新读取操作者、目标与管理员数量；确认操作者仍是管理员后才更新。取消管理员时必须保证更新后至少一名管理员，包括两个管理员互相取消、并发自我取消和重复请求。使用数据库事务串行化，不用进程内锁或事务外 Count 代替。撤销提交之后开始的管理请求必须被拒绝；已进入事务的操作按事务提交顺序判定，不承诺撤回已完成操作。

新增用户、昵称和密码管理写入也在写事务内重新验证管理员；个人改密在同一事务内读取最新哈希、验证旧密码并写入新哈希。串行提交的两个改密请求中，第二个必须基于最新密码验证。

账号更新必须只写对应列：昵称只写 Nickname，密码只写 PasswordHash，管理员只写 IsAdmin。不能继续以旧 NoTracking 实体的整行 Modified 覆盖数据库中其他字段，尤其不能在管理员被撤销后被并发改昵称恢复权限。Repository 提供意图明确的按列更新和事务能力，Application 不依赖 EF/SQLite 类型。
