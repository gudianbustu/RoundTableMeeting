# 登录页实现方案

## 1. 文件和依赖

文件固定为 features/authentication/pages/LoginPage.tsx。页面使用 useAuthentication、LoginForm、useLocation 和 useNavigate；不直接调用 generated Client 或 fetch。

## 2. 实现步骤

1. 读取当前认证状态。
2. checking 时显示稳定加载占位。
3. authenticated 时通过单一 Navigate 渲染分支导航到经过校验的 returnTo 或 /。
4. anonymous 时渲染 LoginForm。
5. error 只在认证初始化错误时显示重试入口；登录表单错误由提交结果管理。
6. LoginForm onSubmit 只 await Provider login；成功后 Provider 状态变为 authenticated，由步骤 3 的 Navigate 完成唯一一次导航。

## 3. 返回路径

returnTo 只从 `location.state.returnTo` 的结构化 `{ pathname, search, hash }` 读取，不使用登录 URL 查询参数。pathname、search、hash 分别按 AuthGate 规则校验后拼接；非法值使用 /。这样可以保留受保护页原有 query/hash，同时不接受外部 URL。

## 4. 错误处理

invalid_credentials 显示统一的账号或密码错误；validation_failed 显示字段错误；unsupported_media_type 显示“请求格式不受支持，请刷新页面后重试”；network/unknown 显示服务暂时不可访问并提供重试。认证初始化 error 使用 PageState 显示重试，不渲染登录表单。页面不显示原始响应、traceId 以外的诊断细节或后端异常。

## 5. 完成检查

- 已有会话访问 /login 不闪现表单。
- 空字段不发送请求。
- 提交期间按钮禁用。
- 登录成功后只导航一次。
- 失败后用户名保留，密码清空。
