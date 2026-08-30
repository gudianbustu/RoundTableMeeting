# 003 账号访问与用户管理 实现审查报告（第三轮）

- 审查对象：003 主题的源码实现（`Server/` 服务端五个生产项目与五个测试项目、`Client/roundTableMeeting/src` 客户端实现与测试），对照已冻结的 `docs/plans/003-账号访问与用户管理/` 全部设计/实现/测试文档。
- 审查方式：只读审查 + 实际运行验证（构建、全部测试）。审查中为恢复本机依赖做过一次环境修复（见第四节 E3，未改动项目源码）。审查日期：2026-08-30。
- 报告性质：覆盖替换本目录 `ZCode审查报告.md`（第二轮为文档冻结复审，本轮为实现审查）。

## 一、总体结论

**003 实现质量高，与冻结文档高度一致。文档 §11“完成检查”与 testPlan 全部 P0/P1 所要求的服务端契约、客户端状态机、测试覆盖均已真实落地；`dotnet build`（0 警告 0 错误）与全部 128 个服务端测试（Domain 13 + Architecture 14 + Application 22 + Repository.Sqlite 34 + Integration 45）、61 个客户端 Vitest 测试全部通过，`pnpm build` 成功。**

发现的 6 个问题中无一属于 P0 功能或契约错误：1 个中优先级实现与文档偏差（`SuccessRehashNeeded` 场景的 fake 覆盖方式）、5 个低优先级瑕疵与文档遗留项。**结论：实现可以进入浏览器 CDP 验收与发布验证（testPlan §14/AB 系列），完成上述验收后 003 即可关闭。**

## 二、实现与文档契约逐项核实（已落地项）

### 2.1 服务端契约与安全边界

| 文档承诺 | 实现核实 |
| --- | --- |
| `UserAccount` 规范化（Trim+NFC、显示 64、规范化 128、昵称非空） | `UserAccount.cs`：`Create` 工厂 + 一次性 `InitializePasswordHash`（拒绝二次调用/空 hash）；`NormalizeDisplayUsername/NormalizeUsername/NormalizeNickname` 与文档 §5.1 完全一致 |
| `UserAccount.ID` 即跨端 `UserId`，不声明第二个 UserId 属性 | 实体仅继承 `BaseEntity.ID`；`CurrentUserDto` 直接映射 `account.ID`；AD-010 架构测试锁定 |
| 密码原样验证（不 Trim/不 NFC/不转大小写） | `AuthenticationService` 直接传 `request.Password!` 给哈希器；AA-017 覆盖首尾空格密码 |
| dummy hash 时序防护 | `PasswordVerificationService` 构造时生成 dummy hash；账号不存在时执行等价 `VerifyHashedPassword`；AA-016 用记录调用次数的 fake 断言被调用 |
| FallbackPolicy 默认拒绝 + 显式豁免 | `ServiceCollectionExtensions`：`FallbackPolicy = RequireAuthenticatedUser`；login/logout/me 及 OpenAPI、Scalar、API 404 fallback、SPA fallback 均 `AllowAnonymous`；AC-009（`/__test/default-authorization` 无标注 → 401）、AI-031 回归通过 |
| Origin 校验（元数据驱动、UseRouting 之后、缺失/null/外部一律 403、415） | `OriginValidationMiddleware` 读取 `GetEndpoint()` 元数据；`Program.cs` 管道顺序为 `UseRouting → ResponseHeaders → Origin → Authentication → Authorization`，与 Server impl §9 一致；AI 系列覆盖四种 Origin 场景 |
| Cookie 契约 | `RoundTableMeeting.Auth`、Path=/、HttpOnly、SameSite=Lax、生产 Always；`ExpireTimeSpan=8h`、`SlidingExpiration=false`、`SignInAsync` 显式 `IsPersistent=false`；AI-002 断言无 `expires=`，AI-026 用 `ManualTimeProvider` 推进 8 小时后 me 401 |
| Challenge/Forbidden 返回 JSON ProblemDetails | Cookie Events `OnRedirectToLogin/OnRedirectToAccessDenied` 统一走 `AuthenticationProblemDetailsExtensions.WriteAsync`，无 HTML 重定向 |
| 响应缓存头 | `AuthenticationResponseHeadersMiddleware` 对 `/api/auth/*` 用 `Response.OnStarting` 追加 no-store/no-cache；实测 500/403/415/401/200 均携带（AuthenticationContractTests 三个缓存头测试通过） |
| me 账号不存在 → 清除陈旧 Cookie + 401 | `Me` action 先 `SignOutAsync` 再返回 401；测试断言 `Set-Cookie: RoundTableMeeting.Auth=;` |
| Claim 最小化 | 仅 `round_table_meeting_user_id` 一个自定义 Claim；`/__test/auth-claims` 断言无 NameIdentifier 等额外 Claim |
| Options 校验与脱敏 | `AuthenticationOptions.Validate` 校验 Origin 纯净性（拒绝带路径/query/fragment/userinfo）、账号规范化唯一、密码 1–256；错误文案只提配置键不回显值；AI-035/Options 测试覆盖 |
| 启动种子 | `Program.cs` 启动 scope 内 `IDatabaseInitializer → SeedAsync`；`AccountSeeder` 幂等（存在即跳过、无新增不提交、配置内重复抛异常）；StartupFailureTests 注入有效认证配置后断言 `SqliteException` 且非 `OptionsValidationException`（防假绿按文档落地） |
| `IAuthenticationService` 命名冲突预案 | Controller 使用 `using AppAuthenticationService = ...` 别名 |
| 迁移 | `20260829162140_AddUserAccounts` 建表 + 唯一索引，列长 64/128/64/512 与文档一致；不写入种子数据 |

### 2.2 客户端状态机与路由

| 文档承诺 | 实现核实 |
| --- | --- |
| 路由树 | `router/index.tsx` 与 Client impl §6 冻结树一致：`/login` 独立、AuthGate(pathless) → AuthenticatedLayout → `/`、system/status 保留 AppLayout、`*` NotFoundPage 独立；002 的 `/` 系统状态重定向按预留被替换 |
| Provider 状态机 | `checking/authenticated/anonymous/error` 判别联合；operationId + AbortController 防竞态；登录保持 anonymous + isLoggingIn；logout 204 才转 anonymous，403/网络错误保留用户（AV-011 契约正确落地） |
| 全局 401 | `apiClient.ts` 中间件对 401 且 pathname 非 `/api/auth/` 时 `notifyUnauthorized()`，原样返回 Response；login/me/logout 不触发；`appProviders.test.tsx` 验证 `/system/status` 不被认证初始化错误阻断（AV-017 的路由级等价覆盖） |
| returnTo 安全 | `AuthGate` 保存结构化 `{ pathname, search, hash }`，`LoginPage.getReturnTo` 逐段校验（单 `/`、拒 `//`、拒反斜杠、拒协议前缀），AP-019 场景覆盖 |
| UserId 展示 | `AuthenticatedHomePage` Descriptions 显示 UserId/用户名/昵称（AP-013 落地） |
| LoginForm | 必填校验、maxLength 128/256、失败清空密码、415 文案“请求格式不受支持”、invalid_credentials 统一文案（AP-018 落地） |
| 公共出口 | `features/authentication/index.ts` 仅导出 Provider/AuthGate/CurrentUserBar/页面/Hook/类型，未暴露 Context 与 generated model |
| credentials | 显式 `credentials: 'same-origin'` |

### 2.3 测试实现映射

testPlan 各系列在实现中均有对应物且通过：AD（UserAccountTests，含 AD-009 长度膨胀、AD-010 主键契约）、AA（AuthenticationServiceTests 17 用例，含 AA-015 SuccessRehashNeeded、AA-016 dummy、AA-018 原始 128/NFC 后 64）、AR（UserAccountRepositoryTests）、AC（DependencyTests，含 AC-009）、AI（IntegrationTests 45 用例，含 AI-026 时间推进、AI-027/缓存头三态、AI-028 防假绿、AI-032/033/034）、AV/AP（客户端 61 用例，含 StrictMode、全局 401、公开页不阻断、returnTo 非法值）。AI-030 空号为文档遗留（见 L4）。

## 三、运行验证结果

| 验证项 | 结果 |
| --- | --- |
| `dotnet build Server/RoundTableMeeting.slnx -c Debug` | 通过，0 警告 0 错误 |
| `dotnet test`（Debug，`--no-build`） | 128/128 通过（D 13、A 14、App 22、Repo 34、Integration 45） |
| `pnpm test`（Vitest） | 61/61 通过（10 个文件） |
| `pnpm build`（tsc + vite） | 通过（`dist/` 生成） |

未执行：`pnpm generate:api` 链路（需长驻服务端进程）、`dotnet publish`（耗时较长且依赖 Node 24 精确口径）、CDP 浏览器验收——属于验收执行项而非代码缺陷，建议由实施者按 testPlan §14 与 AB 系列完成。

## 四、发现的问题

### 高/中优先级：无

### 低优先级

**L1【实现-文档偏差】`SuccessRehashNeeded` 的测试覆盖方式与文档不完全一致（AA-015）**
文档要求 Application.Tests 使用“真实 PasswordHasher”；该场景实现用 `RecordingPasswordHasher` fake 返回固定结果来驱动分支。这是对**分支逻辑**的合理测试（真实 hasher 无法按需产生 RehashNeeded），且其余用例确用真实 hasher；但与文档字面冲突。建议在模块 impl §7 补一句“RehashNeeded 分支允许用受控 fake 驱动”，或保持现状并接受该偏差。

**L2【实现瑕疵】`OriginValidationMiddleware` 对非 POST 的标记端点返回 415 而非语义更准确的 405**
`contentTypeValid` 把“方法非 POST”与“Content-Type 不符”合并进同一 415 分支。文档 §6.5 只约束“无请求体的退出接口只接受 POST”，未规定错误码；当前行为可接受，但 GET login 得到 415（而非 405/404）语义含混。建议后续主题引入时拆分方法与媒体类型判断；本主题可不改。

**L3【文档遗留】第二轮报告 L1–L6 中 4 项未随实现修订文档**
CurrentUserBar/文档中“已未认证/会话已经无效”的旧措辞、AI-030 空号、AU-003/AU-006 未断言 UserId 可见、模块 design §7“初始密码规范化后不能为空”的密码规范化措辞。实现代码本身全部正确（logout 仅认 204；AP-013 已测 UserId 展示；密码校验独立）。建议按项目文档维护策略做一次 10 分钟的文档微调。

**L4【环境/工程风险】本机 node_modules 中 `lightningcss-win32-x64-msvc` 二进制曾被文本化损坏，导致 `pnpm build` 失败（审查期间已修复，建议留档）**
现象：`pnpm build` 报 `Cannot find module '../lightningcss.win32-x64-msvc.node'`，实际是 `.node` 文件头出现 UTF-8 替换字符（`4d 5a 90 00` → `4d 5a ef bf bd`），属“二进制被按文本重写”的典型痕迹——与 `scripts/check-text-format.mjs` 无关（该脚本只扫 `src/api/generated`），推测来自某次对仓库/依赖目录执行的批量文本规范化。排查中发现该平台包为可选依赖，`pnpm install --frozen-lockfile --force` 与 `pnpm store prune` 后**不会**恢复已存在的损坏副本，需显式 `pnpm add lightningcss-win32-x64-msvc@1.33.0`（装回顶层后按包名解析路径即恢复），或删除 `node_modules` 全新安装。修复后 `package.json`/锁文件已还原，构建通过。
建议：a) 排查并停止对仓库（尤其 `node_modules`、`dist`）运行的文本规范化/行尾转换工具范围；b) 将“`pnpm build` 失败先校验 `.node` 文件头是否为 `4d 5a 90 00`”写入排障经验；c) 交付/CI 环境如复现，直接删除 `node_modules` 重装。

**L5【观察】运行 Vitest 时 Node 为 26.7.0，仅产生 `Unsupported engine` 警告未硬失败**
`.npmrc` 的 `engine-strict=true` 未拦截（pnpm 的 engine-strict 语义与文档预期存在差距）；Volta 内已装 Node 24.20.0。不影响 003 验收，但 002 已要求 `check:toolchain` 在 publish 前硬校验，请确认发布机走 `dotnet publish` 目标链路（其会执行 `pnpm run check:toolchain`）而非手工 `pnpm build`。

**L6【观察】`Me` 端点的 `AllowAnonymous` 未显式标注属正确行为，但 `[Authorize]` 与 FallbackPolicy 双保险依赖测试保障**
`me` 使用 `[Authorize]`（显式策略），业务无标注端点由 FallbackPolicy 兜底，AC-009/AI-032 已锁。此处仅提示：后续主题新增 Controller 时不得顺手给业务端点加 `[AllowAnonymous]`——文档 §6.1 已禁止，实现评审时请保持关注。

## 五、与 001/002 基线的兼容性核对

- 002 回归全部通过（128 个服务端测试中含既有 HttpContract/ErrorContract/ContainerCompatibility/Repository 框架测试）；`/system/status` 匿名、未知 API `route_not_found`、SPA fallback 排除边界均有新断言（AI-034）。
- 错误契约复用现有 `ApiProblemDetails`（`code/traceId/errors`），客户端 `toApiError` 未改动即消费新 code，跨端一致。
- 分层约束未破坏：Application 无 Web/EF 引用（AC-001/002 通过），`PasswordVerificationService`/`AccountSeeder` 的 DI 形态与 002 标记扫描模式一致；Domain 不含任何认证类型（AD-007）。
- 范围未越界：未出现添加用户/改密/角色/审计等延期能力的代码或接口（与文档 §3.2 一致）。

## 六、结论与建议

1. **实现通过审查**：文档冻结时承诺的契约、安全边界（dummy hash、FallbackPolicy、Origin、缓存头、8 小时票据、脱敏）与测试矩阵全部落地，构建与全部测试实证通过。
2. **关闭 003 前尚需完成**（属验收执行，非代码修改）：`pnpm generate:api` 回归、`dotnet publish` + 发布 HTTPS 验收（AB 系列）、testPlan §14 CDP 浏览器验收（AU 系列，含双账号切换与发布环境重复）。
3. **顺手项**：L3 的 4 处文档微调；L1 的偏差补注；L4 的损坏根因排查建议记入团队排障经验。
