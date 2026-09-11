# 客户端用户管理模块设计

## 1. 模块职责

用户管理模块负责用户列表读取、添加账号、修改昵称和固定密码重置的客户端协调。它位于独立 `features/userManagement/`，不并入认证 Provider，不保存权限状态，也不负责登录和会话恢复。

本模块仅管理员可用；认证 Provider 提供当前用户 isAdmin，应用导航和 AdminGate 控制可见性，服务端独立授权。

## 2. 目录结构

```text
features/userManagement/
  api/
    listUsers.ts
    createUser.ts
    updateNickname.ts
    resetPassword.ts
    updateAdmin.ts
  components/
    UserListTable.tsx
    CreateUserForm.tsx
    EditNicknameForm.tsx
    ResetPasswordDialog.tsx
  pages/
    UserManagementPage.tsx
  types/
    userManagement.ts
  index.ts
```

页面和组件只依赖 feature 类型与 API 适配，不直接 import `api/generated/`。

服务端对用户管理响应统一使用 `no-store/no-cache`；客户端不把用户列表写入持久化缓存、localStorage 或跨页面全局状态。

## 3. 视图模型

`UserSummaryViewModel` 只包含：

- `userId`；
- `username`；
- `nickname`；
- `isAdmin`。

`CreateUserInput` 包含 username、nickname、initialPassword；`UpdateNicknameInput` 只包含 nickname，并通过 PATCH 提交。重置密码不需要输入模型。

## 4. 数据加载与刷新

用户管理页进入时加载完整用户列表，状态固定为：

- `loading`；
- `ready`；
- `error`。

添加、修改昵称、重置密码和管理员调整各自拥有独立提交状态，不能用一个全局 loading 阻塞全部行操作。操作成功后重新加载列表，避免客户端自行维护排序和并发合并规则。列表请求失败时保留可重试入口。

## 5. 与认证模块协作

用户管理模块不修改认证 Context。昵称修改成功后始终触发列表刷新；若目标 `userId` 等于当前用户，同时独立调用认证模块新增的 `refreshCurrentUser()`，重新获取当前用户资料，使 `CurrentUserBar` 及时显示新昵称。列表刷新失败不能阻止认证资料刷新，认证资料刷新失败也不能回滚已经成功的昵称修改。

`refreshCurrentUser()` 保持当前受保护页面，不把正常资料同步显示成整页 checking；若返回 `401`，仍按认证模块规则转为匿名。其他用户昵称修改不触发认证资料刷新。

## 6. 错误映射

- `validation_failed`：绑定到对应表单字段，并显示安全通用提示。
- `username_conflict`：用户名字段显示“用户名已存在”。
- `user_not_found`：关闭当前操作弹窗，刷新列表并显示目标已不存在。
- `origin_not_allowed`：保留当前页面，显示操作未完成。
- 网络或未知错误：保留输入和当前列表，允许重试。
- 非认证业务 `401`：继续由公共客户端触发全局匿名处理。

客户端不显示原始 ProblemDetails detail、密码哈希或内部异常。

## 7. 权限边界

- /users 使用 AuthGate 和 AdminGate，进入时刷新当前用户确认管理员身份。
- 菜单仅 isAdmin=true 可见，不把本地菜单判断当作服务端授权。
- 管理 forbidden 清空列表、关闭弹窗并同步当前资料；origin_not_allowed 保留认证状态并显示操作失败。
- 本人降级成功立即离开管理页，即使后续资料刷新失败也不保留管理交互。
- 身份调整调用 updateAdmin(userId, { isAdmin })，提交前明确目标与调整结果；last_admin_required 保持现有状态并提示至少保留一名管理员。

## 8. 模块非目标

- 用户搜索、分页、删除和禁用。
- 用户名修改；个人自行改密由 accountSettings feature 负责。
- 权限树、角色选择和审计信息。
- 缓存全部用户到认证 Provider 或全局状态。
