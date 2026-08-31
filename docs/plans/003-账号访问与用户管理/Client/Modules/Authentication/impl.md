# 客户端认证模块实现方案

## 1. 状态类型

types/authentication.ts 定义 CurrentUserViewModel 和判别联合状态：checking、authenticated、anonymous、error。authenticated 必须携带 user；其他状态不保留过期用户。

Context 对生产代码不公开，只通过 useAuthentication 使用；测试通过 `src/test/authenticationTestUtils.tsx` 中的专用 Provider 注入值。Context 值包含 state、login(username,password)、logout()、retry()、refreshCurrentUser()、isLoggingIn、isLoggingOut 和 operationError。login 返回 Promise<CurrentUserViewModel>，logout/retry/refreshCurrentUser 返回 Promise<void>；页面负责登录成功导航，Provider 负责状态真实性和操作错误。

## 2. 初始化

首次挂载：

1. operationId 增加并设 checking。
2. 创建 AbortController 调用 getCurrentUser。
3. 成功且 operationId 仍有效时设 authenticated。
4. 401 unauthenticated 设 anonymous。
5. network/unknown 设 error，并保留 retry。
6. effect 清理取消请求；取消不进入 error。

StrictMode 的旧请求不得覆盖较新的结果。

## 3. 登录

1. 为登录分配新的 operationId。
2. 设置 isLoggingIn=true 并清空 operationError，调用 login API；不把全局状态改为 checking，以免整个登录页替换。
3. 成功设 authenticated 并返回 user。
4. invalid_credentials 保持 anonymous 并向页面抛出安全 ApiError。
5. network/unknown 保持 anonymous，把 ApiError 写入 operationError，不伪造登录成功。
6. finally 设置 isLoggingIn=false；旧 operationId 不得改写新操作状态。

登录成功导航由 LoginPage 完成。返回路径必须通过安全相对路径函数校验。

## 4. 退出

1. 设置 isLoggingOut=true 并清空 operationError，调用 logout API。
2. 204 视为确认退出，设 anonymous。
3. 403、network/unknown 保持 authenticated，并向 CurrentUserBar 返回错误。
4. 错误写入 operationError。
5. finally 设置 isLoggingOut=false；旧 operationId 不得改写新操作状态。

不能在请求发出前预先清理用户，否则网络失败会造成客户端与服务端状态不一致。

## 5. 全局 401

Provider 挂载时订阅 unauthorized 事件，卸载时取消订阅。事件到达时：

- 取消当前初始化/登录/退出操作；
- 增加 operationId；
- 清理 operationError 和用户并设 anonymous。

所有 `/api/auth/*` 请求都不触发全局事件：login 的 invalid_credentials 和 me 的 unauthenticated 由对应认证操作直接处理；logout 按服务端 204/403 契约处理。只有后续非认证业务请求的 401 unauthenticated 才触发全局清理。

## 6. useAuthentication

### 6.1 refreshCurrentUser

用户管理模块成功修改当前账号昵称后调用 refreshCurrentUser：

1. 分配新的 operationId 和 AbortController，但不把 state 改为 checking。
2. 调用 getCurrentUser。
3. 成功时替换 authenticated.user。
4. 401 时清理状态并转 anonymous。
5. network/unknown 保留原 authenticated 用户并向调用方抛出错误，不显示假昵称。
6. 新的登录、退出或全局 401 可以取消资料刷新，旧结果不能覆盖新状态。

### 6.2 Hook 约束

Hook 只能在 Provider 内使用，缺少 Provider 时抛出明确开发错误。页面和组件不直接访问 Context 对象，不自行调用 getCurrentUser；用户管理只能调用 refreshCurrentUser。

## 7. 公共出口

features/authentication/index.ts 只导出 AuthenticationProvider、LoginPage、AuthenticatedHomePage、AuthGate、CurrentUserBar、useAuthentication 和必要类型；refreshCurrentUser 通过 Hook 暴露，不单独导出底层函数。不导出 generated model、底层 API 适配或 Context 实例。

## 8. 测试重点

- 初始化成功、401、网络错误和重试。
- StrictMode 重复初始化和旧请求晚返回。
- 登录成功、无效凭证、网络失败。
- 退出成功、已经未认证、网络失败。
- 全局 401 清理状态，invalid_credentials 不清理。
- Provider 卸载后不更新状态。
- refreshCurrentUser 成功、401、网络失败和被更新操作取消。
