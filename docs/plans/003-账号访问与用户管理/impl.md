# 账号访问与用户管理增量实现方案

## 1. 实施基线与范围

以独立工作树当前已有代码为基线，在登录、用户管理和会议工作台之上增加管理员权限与个人设置。本文只描述待实施步骤，不记录执行日志，不表示代码或测试已完成。业务契约以 [design.md](design.md)为准，验收编号见 [testPlan.md](testPlan.md)。

工作目录固定为仓库 `.worktrees/user-admin-settings`。所有源文件、迁移、生成客户端和构建产物都在该工作树内处理，不复制主目录的其他主题未提交内容。遵守 CRLF、不自行建分支、不主动提交的项目约定。

保留现有登录/Cookie/Origin/ProblemDetails/DI/发布链路、UserId 和会议路由。账号用户名规范化与密码哈希继续复用现有实现。不重建 UserAccount、登录页或 AddUserAccounts 迁移，不将首页替换为占位页。

## 2. 阶段与完成门

| 阶段 | 实施内容 | 依赖 | 验收编号 |
| --- | --- | --- | --- |
| A | IsAdmin、按列更新、账号事务、初始管理员 | 已有数据库与账号模型 | UA-001～005、035～037；按列更新与事务基础 |
| B | 管理员授权、管理员设置、本人昵称与改密、HTTP 契约 | A | UA-006～026、033 |
| C | 身份刷新结果、AdminGate、用户管理动作、个人设置 | B 的 OpenAPI | UA-027～032、034、038～044 |
| D | 集成、浏览器和发布验收 | A～C | UA 全部及受影响原用例、AB/AU 回归 |

每阶段先执行对应自动化验证，接口稳定后再生成 Kiota，避免前端依据临时契约实施。失败先定位并修正，不以文档标记替代通过结果。

## 3. 阶段 A：持久化与初始化

1. 修改 UserAccount 和映射：增加默认 false 的 IsAdmin 与设置行为，现有主键、用户名、昵称、哈希约束不变。
2. 新增 AddUserAccountAdmin 迁移及 Snapshot；保留全部已有迁移。用临时旧版本数据库迁移验证字段默认值与旧资料保留。
3. 扩展账号仓储的管理员计数及按列更新；增加账号写事务执行器，取得 SQLite 写锁后读取和更新，避免整实体 RegisterEdit 覆盖并发字段。具体文件与提交职责见 [Server/impl.md](Server/impl.md)。
4. 修改 AccountSeeder、IAccountSeeder、AuthenticationOptions 和 Program 启动传参。InitialAdminUsername 仅在无管理员时使用；已有账号提升也须提交。初始化失败整笔回滚，正常重启不覆盖在线身份调整。
5. 使用独立连接的真实 SQLite 验证最后管理员所需事务基础、并发初始化、字段隔离和取消回滚。

## 4. 阶段 B：服务与 HTTP

1. CurrentUserDto、UserSummaryDto 增加 bool IsAdmin；新增必填 UpdateAdminRequest、ChangePasswordRequest 和本人设置结果契约。LoginRequest、Cookie Claim 和 ICurrentUserContext 身份字段不变。
2. 新增 AdministratorAccessService 与 Web AdminOnly handler，使用数据库当前账号判断。UsersController 应用 AdminOnly；写用例在事务内再次检查操作者。
3. 扩展 UserManagementService.UpdateAdminAsync；取消最后管理员返回 last_admin_required，本人降级后保留登录。管理员设置、昵称、密码分别只写对应列。
4. 新增 AccountSettingsService 和 AccountController。本人目标仅来自 ICurrentUserContext；改密验证当前密码，失败使用 validation_failed，不误触发 401。管理重置继续固定 123456。
5. 更新 ProblemDetails、NoStore、Origin 元数据和 OpenAPI 响应声明；列表补充 403，新管理员接口补充 409。新增服务、仓储和事务保持 Scoped，哈希器沿用现有注册。
6. 调整测试工厂和启动失败夹具：默认 A 为管理员、B 为普通用户，专门失败用例才省略必要配置。验证原测试仍由其目标故障触发。

## 5. 阶段 C：客户端

1. 使用本工作树的服务端 OpenAPI 生成 Kiota；更新 API 适配、用户视图模型和测试数据，isAdmin=false 必须作为合法值。
2. 修改 AuthenticationProvider、Context 和 Hook 类型：refreshCurrentUser 返回 refreshed 或 cancelled，并提供 identityRevision 与 refreshRevision；对旧结果、取消、401 和网络错误采用不同处理。具体状态协调见 [认证模块实现](Client/Modules/Authentication/impl.md)。
3. 在 app 路由层增加 AdminGate。首次确认两个版本匹配的 refreshed 后才挂载管理页；普通资料刷新保持已挂载页面，身份变化或 forbidden 才撤销。版本变化不能形成自动刷新循环。
4. 扩展用户列表身份列、管理员调整确认和 API 适配。本人降级立即离开管理页面，最后管理员错误不乐观更新。
5. 增加 accountSettings feature 与 /settings/profile；两个独立表单分别修改昵称和密码，用户名及身份只读。保存、资料同步与取消结果分别反馈。
6. 保留 `/`、`/meetings/:meetingId` 的 MeetingWorkspacePage、现有错误边界和会议权限，仅增加账号入口。文件与适配函数见 [Client/impl.md](Client/impl.md)。

## 6. 阶段 D：验证与交付

按 testPlan 的测试文件映射与执行批次完成单元、SQLite 并发、HTTP、Vitest、浏览器和发布验证。浏览器执行管理员 A、普通用户 B 两种身份的真实操作闭环；不只通过组件 mock 证明权限有效。

本轮完成条件：全部 UA P0/P1 及受影响原用例通过；普通用户不能管理他人；最后管理员与并发写入规则成立；个人改密与固定重置符合会话约定；会议路由和既有权限保持有效；生成客户端、构建与发布可用。只在实际通过后报告完成，不在设计文档中新增执行日志。

运行环境、数据库、端口和 OpenAPI 来源必须与其他会话隔离，详细准备和执行命令见 [testPlan.md](testPlan.md)。迁移、测试与发布命令不代表授权修改主目录或部署生产环境。
