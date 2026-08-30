# 003 账号访问与用户管理 Codex 修复报告

## 1. 报告范围

本报告依据以下两份独立审查报告，并记录随后源码实施和监管收口结果，对 docs/plans/003-账号访问与用户管理/ 下的设计、实现和测试文档进行复核与修订：

- [Dsh审查报告](Dsh审查报告.md)
- [ZCode审查报告](ZCode审查报告.md)

此前文档冻结阶段只修改设计、实现和测试文档；随后源码实施阶段按本报告的冻结约束完成了服务端、客户端、数据库迁移、生成代码和自动化测试。产品需求文档保持不变。

## 2. 总体处理结论

- 两份报告指出的高优先级契约矛盾和主要中优先级实施风险已经修复。
- 用户已经明确指定“需求文档保留完整账号需求，本主题当前只实施极简账号接入”，因此没有修改需求分析文档，也没有把完整用户管理能力重新塞回 003。
- 登录限流、复杂密码策略、账号找回、Data Protection 密钥持久化等能力被明确保留为后续安全或账号治理主题，不作为当前冻结阻断项。
- 修复后，设计、实现和测试文档已经具备冻结并进入源码实现的条件。

## 3. 已修复项目

### 3.1 跨文档契约

1. **认证服务签名统一**
   - 统一为 AuthenticateAsync(LoginRequest, CancellationToken)。
   - GetCurrentUserAsync(CancellationToken) 同步固定。
   - 对应 ZCode A1。

2. **ValidationFailed 字段错误载荷补齐**
   - AuthenticateResult 的 ValidationFailed 携带 Errors 字典。
   - 字段键固定为 username 和 password。
   - 自动模型验证与 Application 防御性验证统一输出 validation_failed + errors。
   - 对应 ZCode A5、C7。

3. **UserId 契约明确**
   - UserAccount.ID 即跨端 UserId，不增加第二个 UserId 属性。
   - CurrentUserDto.UserId 直接映射实体 ID。
   - 主题验收占位页显示 UserId、用户名和昵称；主题三替换页面后不要求继续展示 Guid。
   - 增加实体主键架构测试。
   - 对应 Dsh 3.1、ZCode A2。

4. **logout 契约统一**
   - 服务端保持匿名幂等 204。
   - 客户端删除不可达的 logout 401 unauthenticated 分支。
   - logout 的 403 origin_not_allowed 和网络错误保留当前认证状态。
   - 对应 ZCode A3。

5. **昵称非空规则统一**
   - Options、实体和种子输入均要求昵称 Trim + NFC 后非空。
   - CurrentUserBar 的空昵称仅为防御性回退，不是正常业务分支。
   - 对应 ZCode A4。

6. **PasswordHasher 注册职责统一**
   - Web 组合根注册 IPasswordHasher<UserAccount>。
   - Application 只消费，不承担 DI 注册。
   - 对应 ZCode A6。

7. **AuthGate 统一为 Outlet**
   - AuthGate 不接收 children，authenticated 状态只渲染 Router Outlet。
   - 测试改为断言子路由渲染。
   - 对应 Dsh 2.1、ZCode A7。

### 3.2 服务端安全和授权边界

1. **登录时序枚举缓解**
   - 增加 PasswordVerificationService 和启动时 dummy hash。
   - 用户不存在时仍执行同类 VerifyHashedPassword。
   - 自动化测试验证 dummy 验证被调用，不使用不稳定的绝对耗时断言。
   - 对应 ZCode B1。

2. **默认拒绝策略落地**
   - Authorization FallbackPolicy 固定要求认证。
   - login、logout、system/status、OpenAPI、Scalar、API 404 和 SPA fallback 显式 AllowAnonymous。
   - 增加无标注测试业务端点默认 401 用例。
   - 对应 ZCode B2。

3. **Origin 校验改为端点元数据驱动**
   - 固定 RequireBrowserOriginAttribute。
   - Origin 中间件仅处理显式标记端点，未知 API 保持 route_not_found。
   - 缺失、null、外部 Origin 均拒绝。
   - 显式要求 UseRouting 位于 Origin 中间件之前。
   - 对应 Dsh 4.2、4.3。

4. **认证错误契约补齐**
   - 固定 400 validation_failed、401 invalid_credentials、401 unauthenticated、403 origin_not_allowed、403 forbidden、415 unsupported_media_type 和 500 internal_error。
   - OpenAPI/Kiota 测试覆盖 415。
   - 对应 ZCode B4。

5. **认证响应禁缓存**
   - 增加 AuthenticationResponseHeadersMiddleware。
   - login/me/logout 的成功和 400/401/403/415/500 最终响应统一 Cache-Control: no-store、Pragma: no-cache。
   - 对应 ZCode B3。

6. **密码原样验证**
   - 密码不 Trim、不 NFC、不转换大小写。
   - 增加首尾空格密码测试。
   - 对应 ZCode B6。

7. **会话语义冻结**
   - 非持久化 Cookie，不写 Expires。
   - 票据有效期固定 8 小时、不滑动续期。
   - SignIn 显式 IsPersistent=false。
   - 设计、实现、测试三层同步。
   - 对应 Dsh 4.4、ZCode B8。

8. **Forbidden 契约定位明确**
   - 当前没有正式业务权限场景。
   - 403 forbidden 是为后续主题冻结的通用契约，只通过测试专用拒绝策略端点验证。
   - 对应 Dsh 3.2。

### 3.3 配置、启动和持久化

1. **配置来源可执行化**
   - Development Origin 固定在 appsettings.Development.json。
   - Development 账号通过 user-secrets 提供，并补充 init/set 命令。
   - Testing 使用 InMemoryCollection。
   - 生产使用完整环境变量键或 Secret Provider。
   - 对应 ZCode B7、C4、D5。

2. **生产 AllowedOrigins 校验**
   - 缺失或格式错误时启动失败。
   - 安全摘要可以指出配置键，但不打印 Origin 值。
   - 对应 Dsh 4.2。

3. **真实进程测试防假绿**
   - 002 StartupFailureTests 必须注入有效认证配置。
   - 失败类型明确断言为 Microsoft.Data.Sqlite.SqliteException，不能只排除 OptionsValidationException。
   - 对应 Dsh 4.1。

4. **首个实体迁移说明**
   - 明确 AddUserAccounts 是 002 空模型后的首个真实实体 Migration。
   - 对应 Dsh 5.4。

5. **TTA new() 约束处理**
   - UserAccount 保留 public 无参构造用于物化。
   - 业务创建使用工厂，并通过一次性 InitializePasswordHash 完成凭证初始化。
   - 禁止未初始化 hash 的账号进入持久化。

6. **种子配置运行期边界**
   - 种子完成后业务不得再次解析 SeedAccounts。
   - 验收账号密码保存在受保护的本地秘密中。
   - 明确当前没有密码找回或重置路径。

### 3.4 客户端和测试可执行性

1. **Provider 依赖方向修复**
   - AuthenticationProvider 放回 authentication feature。
   - AppProviders 通过 feature 公共出口组合，保持 app -> features -> api/shared。

2. **路由树冻结**
   - LoginPage 无认证布局。
   - 根路由使用 AuthGate -> AuthenticatedLayout -> AuthenticatedHomePage。
   - SystemStatusPage 保留 AppLayout。
   - NotFoundPage 独立。
   - 对应 ZCode B5。

3. **认证状态机统一**
   - 登录期间保持 anonymous + isLoggingIn。
   - Provider 不负责导航；LoginPage 使用唯一 Navigate 分支。
   - 公开 system/status 不被认证初始化错误阻断。

4. **returnTo 结构化并保留 query/hash**
   - 使用 pathname、search、hash 结构化对象并逐段校验。
   - 不接受外部 URL、双斜杠或反斜杠。
   - 对应 Dsh 5.3、ZCode D8。

5. **客户端长度和错误分支固定**
   - username 原始输入 maxLength=128，password maxLength=256；服务端规范化后再按显示值和比较值限制。
   - 415 显示“请求格式不受支持”。
   - LoginPage 初始化 error 固定使用 PageState。
   - 对应 ZCode B9、C6。

6. **文件清单和命令补齐**
   - 增加结果类型、PasswordVerificationService、Origin 元数据、响应头中间件、认证 ProblemDetails 扩展和 authenticationTestUtils。
   - 构建块增加 pnpm generate:api 和 dotnet publish。
   - 对应 ZCode D5、D6。

7. **测试矩阵补全**
   - 增加默认授权、dummy hash、密码原样、UserId 主键、路由回归、缓存头、Origin 元数据、AllowedOrigins 配置、公开状态页、returnTo query/hash、415 页面文案等测试。
   - 测试编号保持唯一。

## 4. 用户指定项目

以下不是 Codex 自行裁剪，而是用户已经明确指定的实施策略：

1. **需求文档保持不变**
   - 产品需求中添加用户、修改密码、重置密码等内容继续保留。
   - 当前 003 只实施最小身份接入，完整账号管理在核心会议和 AI 协作验证完成后重新设计。
   - 因此没有按 Dsh 1.1 建议修改需求分析文档。

2. **当前主题保持极简接入**
   - 不把用户列表、动态建号、资料维护、密码重置、角色、审计等重新纳入 003。
   - 账号增长只能通过受保护配置和重启完成，这是用户接受的阶段性运营限制。

3. **文档拆分方式**
   - 采用主题总体、Server、Client、模块、页面、组件的多级文档结构。
   - 每份 design.md 对应 impl.md，主题根目录统一 testPlan.md。

## 5. 拒绝修复项目及原因

以下审查建议本身有价值，但不应在当前主题实施，已明确记录为后续事项：

1. **完整登录限流和账号锁定**
   - 拒绝纳入当前主题。
   - 原因：001 和 003 已把高级限流、风险识别和账号安全治理延期；当前通过统一错误、dummy hash、Origin 和 Cookie 边界满足最小安全底线。

2. **提高密码最小长度或复杂度**
   - 拒绝在当前主题强制。
   - 原因：账号来自受保护部署配置，没有在线改密或自助注册；复杂度策略需要在后续账号治理主题统一确定，避免现在形成长期产品承诺。

3. **Data Protection 密钥持久化与多实例共享**
   - 拒绝纳入当前主题。
   - 原因：主题明确不承诺服务端重启后的会话恢复，也不支持多实例部署；文档已记录未来容器/多实例必须另行设计密钥存储。

4. **账号找回、重置和在线新增**
   - 拒绝纳入当前主题。
   - 原因：这是用户明确延期的完整账号管理能力。当前只记录运营限制和秘密保管要求。

5. **运行期清空 IOptions 中的种子密码**
   - 拒绝承诺“从进程内存消失”。
   - 原因：标准配置 Provider 可能在进程生命周期持有秘密，强行清空 Options 不能保证所有 Provider 副本被销毁，容易形成虚假安全承诺。当前改为限制运行期业务不得再次读取 SeedAccounts，并禁止秘密进入实体、数据库、日志和响应。

6. **Development HTTPS 7043 直接登录**
   - 拒绝作为 003 验收路径。
   - 原因：正式客户端开发链路是 Vite 5173 代理；7043 仍可用于 API/OpenAPI 调试，但不作为带客户端 Cookie Origin 的登录入口。

## 6. 未发现需要用户再次确认的项目

本轮没有新的产品决策需要向用户追问。所有需要取舍的事项都已经被此前的“极简账号接入、需求文档不改、完整安全与用户管理延期”决定覆盖。

## 7. 冻结结论

**结论：可以冻结 003 账号访问与用户管理文档，并进入源码实现。**

冻结依据：

- 两份审查报告中的高优先级契约矛盾已全部处理。
- 主要中优先级安全、管道、配置、路由和测试风险已经落到 design/impl/testPlan。
- 剩余未实施项均属于用户指定延期或明确拒绝纳入当前主题的能力，不构成当前实现阻断。
- 实现阶段仍必须以 testPlan 的全部 P0/P1 为完成门，不得因为文档冻结跳过测试。

## 8. 第二轮复审后的补充修复

最新复审报告指出的唯一冻结阻断是 FallbackPolicy 与 002 既有测试 TestController 的冲突。文档处置已完成，源码处置将在 003 实现阶段按下列要求落地：

- 服务端实现文档已要求现有 `Controllers/TestController.cs` 的 `/__test/application-part`、`/__test/throw`、`/__test/validation` 测试夹具使用 AllowAnonymous，继续承担 002 的 ApplicationPart、ProblemDetails、字段校验、traceId 和请求日志回归。
- 服务端实现文档已要求新增独立 DefaultAuthorizationTestController 验证无授权标记端点在 FallbackPolicy 下返回 401。
- 服务端实现文档已要求新增独立 ForbiddenTestController 验证始终失败策略返回 403 forbidden。
- testPlan 的 AI-030、AI-032、AI-034 已分别覆盖公开回归、默认拒绝和 002 夹具回归。

同时处理第二轮报告提出的低风险项：

- 清理 CurrentUserBar 中不可达的 logout 401/已未认证措辞。
- 将模块设计中的密码非空规则改为“原字符串非空且 1–256”，不误称密码做规范化。
- Client API 适配明确 logout 的 403 行为。
- AU-003/AU-006 明确验收页展示 UserId、用户名和昵称。
- AP-019 覆盖 returnTo 的 pathname、search、hash 保留和非法分段回退。
- Client 路由树明确 NotFoundPage 独立渲染且自身提供返回 `/system/status` 链接，保持 002 V-008 语义。
- AI 测试编号补齐 AI-030，并保持全表编号唯一。

第二轮复审结论为“解决 2.1 后即可冻结”；2.1 的文档处置已经完成，源码改动属于下一阶段实现任务，不再阻止文档冻结。

需要区分历史文档冻结与当前源码状态：上文第 8 节保留的是当时的阶段记录；当前源码实施已经完成 `TestController` 的 AllowAnonymous 属性、两个独立测试控制器、认证管道和对应 P0/P1 自动化测试。

## 9. 本轮最终实施与监管收口

### 9.1 已修复

1. **validation_failed 字段错误绑定**
   - LoginForm 读取 ApiError.fieldErrors，将 username 和 password 错误绑定到对应 Form.Item。
   - 每次错误变化都显式设置 username/password 两个字段；缺失字段使用空错误数组，清除旧字段错误；忽略其他后端字段。
   - 表单仍显示安全的通用提示；validation_failed、未知 ProblemDetails 和内部后端 detail 不直接回显。
   - 新增客户端测试验证两个字段错误可见、原始后端细节不可见，以及错误清空或切换后旧字段错误消失。

2. **Origin 正文策略改为显式元数据**
   - RequireBrowserOriginAttribute 增加 RequireJsonBody 参数。
   - login 使用 RequireBrowserOrigin(true)，logout 使用默认参数。
   - OriginValidationMiddleware 不再通过 URL 的 login 后缀猜测请求体类型；只有显式要求 JSON 的端点才校验解析后的 media type。
   - 新增架构测试锁定 login/logout 元数据和管道顺序，HTTP 测试验证 logout 携带 text/plain 仍返回 204。

3. **实现文档对齐**
   - Server/impl.md 与 Program.cs 的实际静态文件位置一致，未为文档措辞无必要重排生产管道。
   - testPlan.md 的 AD-009 改为显示用户名上限和规范化长度防御；明确 .NET ToUpperInvariant 当前没有稳定多字符扩展样例，防御性分支保留但不伪造不可执行测试。
   - AccountAccess 模块设计明确初始密码是原字符串非空且 1–256，不做 Trim 或 Unicode 规范化。
   - LoginForm 实现文档补充 fieldErrors 到 Form.Item 的绑定规则。
   - AI/AP/AU 编号和承诺与现有实现及测试保持一致；CurrentUserBar 文档保留真实的 204 成功和失败保留认证状态语义。

4. **浏览器空白问题记录**
   - 独立 Chrome CDP 复现的 /login 空白根因是旧 Vite 依赖预优化缓存返回 504 Outdated Optimize Dep，涉及 react、react-dom/client 和 JSX runtime。
   - 清理 Client/roundTableMeeting/node_modules/.vite 并以 pnpm dev -- --force 重建预优化缓存后页面恢复。后端未启动时显示服务不可访问；注入测试账号并启动后登录表单正常。
   - browser-client 插件因缺失 browser-service.mjs 无法附加；但 raw Chrome CDP 已成功完成截图、DOM、Console 和 Network 读取。
   - 该处理是本机开发缓存恢复，不是业务代码修复；本报告不把它计入生产逻辑变更，也不声称 pnpm build、publish 或完整 CDP 验收通过。

### 9.2 用户指定项目

- 保持当前 master 工作树直接实现，不创建工作树、分支或提交。
- 不修改 docs/需求分析/圆桌会议网站需求.md 或其他需求分析文档。
- 保持 003 为极简账号接入，不新增在线用户管理、改密、重置、找回、角色或审计能力。
- 按用户要求使用浏览器调试链路实际检查 `/login`，不能只依赖单元测试或截图推测。

工程约束方面，依赖声明、`package.json` 和 `pnpm-lock.yaml` 均保持不变；为恢复 Vite 的 `504 Outdated Optimize Dep`，只删除并重建了可再生的 `node_modules/.vite` 预优化缓存，没有修改依赖包内容。所有新增和修改文件保持 CRLF、UTF-8 无 BOM。

### 9.3 拒绝修复及原因

以下建议不属于本轮必要业务修复，继续延期：

1. **登录限流和账号锁定**：属于后续账号安全治理，需要统一定义风险策略和运维边界。
2. **密码最小长度和复杂度**：当前只做原字符串非空且不超过 256；复杂度策略需在后续账号主题统一确定。
3. **Data Protection 密钥持久化和多实例共享**：当前主题不承诺重启后会话恢复，也不支持跨实例会话；后续部署主题另行设计。
4. **完整用户管理**：用户列表、在线新增、资料修改、密码修改、重置、找回、角色和审计不属于当前实施切片。
5. **非 POST 标记端点统一改为 405**：当前 RequireBrowserOrigin 元数据端点按现有 HTTP 方法契约处理；logout 明确要求 POST，未来端点应按自身路由契约测试，不用 Origin 中间件代替 MVC 方法匹配。
6. **无害 DI 注册冗余**：不为风格性冗余扩大组合根改动；现有 DI 解析和生命周期已有自动化证明。

### 9.4 当前验证和冻结结论

- 本轮新增测试覆盖 LoginForm 字段错误、login/logout Origin 正文元数据、Origin 管道顺序和文档声明。
- 服务端最终结果：129 个测试通过，其中 Domain 13、Application 22、Repository 34、Architecture 14、Integration 46。
- 客户端最终结果：10 个测试文件、63 个测试通过；pnpm exec tsc --noEmit 通过。
- 使用 volta run --node 24.20.0 --pnpm 11.24.0 pnpm build 实际通过；Vite 仅有 chunk size warning，没有构建错误。
- raw CDP 已成功截图并读取 DOM、Console、Network；browser-client 插件因缺失 browser-service.mjs 无法附加，因此不将其表述为插件完成的完整浏览器验收。
- raw CDP 已实际验证 alice 登录 200 后进入首页，刷新调用 me 返回 200 并保持会话，退出 logout 返回 204 后回到 /login。
- publish 和完整 CDP 验收仍未完成；browser-client 插件限制仍保留，不将 raw CDP 局部流程描述为完整发布验收。
- 结论：在用户指定的极简范围内，字段错误清理、Origin RequireJsonBody 元数据、文档对齐、服务端/客户端测试、tsc、客户端构建和 raw CDP 核心登录流程均已完成；发布与完整 CDP 验收仍是明确未完成项。
