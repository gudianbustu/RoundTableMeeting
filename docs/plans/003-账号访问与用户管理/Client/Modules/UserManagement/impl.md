# 客户端用户管理模块实现方案

## 1. 类型与 API

`types/userManagement.ts` 定义 UserSummaryViewModel、CreateUserInput、UpdateNicknameInput，以及页面状态 `loading | ready | error`。用户视图模型不包含 generated 类型或附加数据。

五个 API 适配函数接受可选 AbortSignal，使用 AbortRequestOption 调用 Kiota。响应 DTO 必须校验 userId、username、nickname 完整且 isAdmin 为布尔值；缺失字段按 unknown 安全错误处理。

## 2. 页面局部状态

UserManagementPage 使用本地 state/ref 管理 users、列表状态与错误、昵称、添加、重置弹窗及管理员调整确认的独立提交状态。页面不创建全局 UserManagementProvider。

列表加载和每类操作分别持有 AbortController；开始新同类操作前取消旧请求，页面卸载取消全部请求。

## 3. 列表与刷新

`loadUsers` 取消上一列表请求，设置 loading，调用 listUsers。成功替换完整数组并设 ready；失败保留 error 和重试。操作成功后的刷新复用同一函数。取消不进入 error。

## 4. 操作协作

添加成功关闭弹窗并刷新列表；失败绑定字段错误并由表单清空初始密码。昵称修改成功后触发列表刷新，目标为当前用户时独立调用认证模块 `refreshCurrentUser()`；两个刷新互不阻塞，任一失败都不回滚已成功的昵称。密码重置成功关闭确认框并显示固定密码结果。

## 5. 401 与竞态

五个 API 均属于非 auth API，401 由公共客户端通知 AuthenticationProvider。页面被 AuthGate 导航离开时 cleanup 取消请求；旧请求不能关闭较新的弹窗或覆盖错误。

## 6. 公共出口

`features/userManagement/index.ts` 只导出 UserManagementPage 和必要视图类型；API 适配和内部组件不向 app 暴露。

## 7. 测试重点

覆盖加载、重试、卸载取消、添加成功/冲突/字段错误/网络失败、本人和他人昵称修改、refreshCurrentUser、重置成功/目标不存在/网络失败及全局 401。

## 8. 管理员调整

增加 UpdateAdminInput { isAdmin: boolean } 与 updateAdmin 适配。保留 false，校验响应 isAdmin 为 boolean。页面维护 adjustingAdminUserId 与独立请求版本，成功后刷新列表；目标为本人且返回 false 时立即导航离开管理页并刷新认证资料。

forbidden 清空列表并关闭所有弹窗，撤销当前页面的管理员放行状态，不触发 logout；last_admin_required 提示“至少保留一名管理员”，刷新列表，不乐观修改身份。目标不存在沿用 user_not_found；origin_not_allowed 按普通操作错误处理。

## 9. 实施顺序与拒绝后的清理

先更新视图模型及全部有用户摘要响应的适配，再增加 updateAdmin 和页面动作。复用既有 userManagement.test.tsx，普通用户正向管理测试改为拒绝预期；新增身份调整与本人降级场景。

收到 forbidden 时取消列表与全部写操作的 AbortController，递增对应请求版本并关闭弹窗、清空 users；失效响应不得设置 ready、补回旧列表或再次发起刷新。页面被 AdminGate 卸载后清理保持幂等。last_admin_required 不注销账号，提示后重新查询；本人降级不执行成功后的普通列表刷新。

本人昵称保存后的 refreshCurrentUser 只更新资料请求版本，成功、网络失败或取消均不让已确认 AdminGate 卸载页面；身份变化、401 或 forbidden 例外。保存反馈、列表刷新与资料刷新保持独立，补充真实 Provider 与 AdminGate 组合测试验证页面持续挂载。
