# 003 账号访问与用户管理 Codex 修复报告

## 1. 报告范围

本报告记录 003 源码实现阶段的审查、修复和验证结果。实现直接在当前主分支和共享工作区完成，没有创建新的工作树或分支，没有修改需求分析文档。

## 2. 总体结论

用户管理源码增量已经完成，包括用户列表、添加账号、修改昵称和固定密码重置四项能力。关键服务端、客户端、OpenAPI、自动化测试和真实 HTTP 闭环均已验证通过。

本轮没有引入角色、权限、管理员判断、审计、删除、禁用、搜索、分页、自助改密或找回密码。所有已登录用户拥有相同管理能力，该边界只适用于少量可信内部测试；进入生产或对外环境前必须重新收紧菜单和 API 授权，并补充审计、会话撤销和账号安全治理。

## 3. 已修复

### 3.1 服务端

1. 新增 UserManagement 的 Abstraction、Application、Domain、Repository 和 Web Controller。
2. 实现用户列表、添加账号、昵称 PATCH 和固定密码重置四条接口。
3. UserAccount 增加 ChangeNickname 和 ReplacePasswordHash，保留首次密码哈希初始化的一次性约束。
4. 用户列表按规范化用户名和 ID 稳定排序。
5. 预检查冲突和 SQLite 唯一索引并发冲突统一返回 username_conflict。
6. 目标账号不存在统一返回 user_not_found。
7. 创建和昵称 PATCH 使用要求 JSON 的 Origin metadata；密码重置使用无正文 Origin metadata。
8. 删除旧的按认证路径启发式工作的响应头中间件，统一使用 NoStoreResponse metadata 和 SensitiveResponseHeadersMiddleware。
9. AuthenticationController 与 UsersController 均使用 NoStoreResponse。
10. OpenAPI 声明用户管理成功响应和全部实际错误响应。

### 3.2 客户端

1. 新增 userManagement feature、受保护的 users 路由和认证布局入口。
2. refreshCurrentUser 改为必选异步操作，支持取消、版本隔离、401 转匿名和网络失败保留原资料。
3. 列表、创建、昵称修改和密码重置分别维护请求控制器、版本和提交状态；卸载时全部取消。
4. 当前账号昵称修改后的列表刷新和认证资料刷新独立执行，任一失败不回滚已成功的修改。
5. 创建、昵称和重置组件补齐字段错误、安全提示、输入长度、空白校验、失败清空初始密码、加载时禁止关闭和固定重置文案。
6. 修正目标用户行的禁用逻辑，其他用户行保持可操作。
7. 删除未使用的 userManagement 请求辅助文件。

### 3.3 测试

1. Domain 新增昵称修改和密码哈希替换测试。
2. Application 新增列表、创建、校验、冲突、昵称、重置和目标不存在测试。
3. Repository 新增稳定排序和唯一索引冲突转换测试。
4. Integration 新增四条 API、401、Origin、415、404、409、禁缓存、固定重置密码、OpenAPI 和 routing 测试。
5. Architecture 新增统一中间件、Controller metadata 和 action Origin 策略测试。
6. 客户端新增用户管理组件和认证资料刷新测试。

## 4. 用户指定项目

1. 当前只实现列表、添加账号、昵称修改和固定重置为 123456。
2. 当前不做权限管理，所有已登录用户能力相同。
3. 不修改需求分析文档。
4. 不创建新的工作树或分支。
5. 不实现角色、管理员、审计、删除、禁用、搜索、分页、自助改密、找回密码或会话撤销。
6. 密码重置不撤销已有 Cookie，不设置首次登录改密标记，不记录重置历史。

## 5. 拒绝修复或延期及原因

1. 角色、权限、管理员判断和审计：用户明确要求内部少量测试阶段完全开放，生产前再收紧。
2. 用户自行改密、找回密码和自定义重置：属于后续账号安全治理。
3. 删除、禁用、搜索、分页和完整用户中心：超出本轮最小范围。
4. 页面级 Playwright 或 CDP 自动化：当前环境没有可调用的浏览器自动化客户端，不能把 HTTP 验收冒充 DOM、Console 或截图验收。
5. Vite chunk size warning：构建成功，仅为性能提示，不阻断当前主题。
6. 并行 004 主题文件：属于其他会话负责的范围，本轮不修改、不提交、不纳入 003 版本。

## 6. 最终验证

- 服务端构建通过，0 警告、0 错误。
- 服务端测试 187/187 通过：Domain 16、Application 31、Repository 36、Architecture 14、Integration 90。
- 客户端 Vitest 70/70 通过，共 11 个测试文件。
- TypeScript 检查通过。
- Vite 构建通过，仅有 chunk size warning。
- Kiota 生成通过，users API 和模型已生成。
- 真实 HTTP：登录 200、用户列表 200、创建 201、昵称 PATCH 200、密码重置 204。
- OpenAPI 已包含用户列表、创建、昵称 PATCH 和密码重置路径。
- 用户管理响应包含 no-store 和 no-cache。
- 错误方法和未知路径保持 routing 的 404 或 405 语义。
- git diff --check 通过；003 相关源码和生成文件已检查为 CRLF、UTF-8 无 BOM。

## 7. 冻结结论

003 账号访问与用户管理源码实现可以冻结并进入提交。

提交时必须继续按文件边界排除并行 004 主题和其他不属于 003 的未提交修改。
