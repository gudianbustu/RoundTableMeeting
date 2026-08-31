# 客户端用户管理模块实现方案

## 1. 类型与 API

`types/userManagement.ts` 定义 UserSummaryViewModel、CreateUserInput、UpdateNicknameInput，以及页面状态 `loading | ready | error`。用户视图模型不包含 generated 类型或附加数据。

四个 API 适配函数接受可选 AbortSignal，使用 AbortRequestOption 调用 Kiota。响应 DTO 必须校验 userId、username、nickname 完整；缺失字段按 unknown 安全错误处理。

## 2. 页面局部状态

UserManagementPage 使用本地 state/ref 管理 users、列表状态与错误、三个弹窗及其独立提交状态。页面不创建全局 UserManagementProvider。

列表加载和每类操作分别持有 AbortController；开始新同类操作前取消旧请求，页面卸载取消全部请求。

## 3. 列表与刷新

`loadUsers` 取消上一列表请求，设置 loading，调用 listUsers。成功替换完整数组并设 ready；失败保留 error 和重试。操作成功后的刷新复用同一函数。取消不进入 error。

## 4. 操作协作

添加成功关闭弹窗并刷新列表；失败绑定字段错误并由表单清空初始密码。昵称修改成功后触发列表刷新，目标为当前用户时独立调用认证模块 `refreshCurrentUser()`；两个刷新互不阻塞，任一失败都不回滚已成功的昵称。密码重置成功关闭确认框并显示固定密码结果。

## 5. 401 与竞态

四个 API 均属于非 auth API，401 由公共客户端通知 AuthenticationProvider。页面被 AuthGate 导航离开时 cleanup 取消请求；旧请求不能关闭较新的弹窗或覆盖错误。

## 6. 公共出口

`features/userManagement/index.ts` 只导出 UserManagementPage 和必要视图类型；API 适配和内部组件不向 app 暴露。

## 7. 测试重点

覆盖加载、重试、卸载取消、添加成功/冲突/字段错误/网络失败、本人和他人昵称修改、refreshCurrentUser、重置成功/目标不存在/网络失败及全局 401。
