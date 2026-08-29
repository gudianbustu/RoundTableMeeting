# 认证门组件实现方案

## 1. 文件和组件签名

文件固定为 features/authentication/components/AuthGate.tsx。组件不接收 children，authenticated 状态直接渲染 `<Outlet />`，与路由嵌套结构保持单一实现。

## 2. 渲染实现

读取 useAuthentication：

- checking 固定使用 PageState。
- authenticated 渲染 Router `Outlet`，不接收或渲染 children。
- anonymous 使用 Navigate 导航到 /login，并通过 location.state 保存安全 returnTo。
- error 显示服务不可访问和 retry 操作。

## 3. 安全实现

AuthGate 不传递任意字符串 returnTo，而是在 location.state 中保存结构化对象 `{ pathname, search, hash }`。pathname 必须以单个 `/` 开头且不含反斜杠、协议或主机；search 只能为空或以 `?` 开头；hash 只能为空或以 `#` 开头。LoginPage 校验三部分后再拼接，非法值回退 `/`。不要把未经校验的 location.href 直接写入登录跳转状态。

AuthGate 不做会议成员、会议创建者或其他业务权限判断；这些判断由服务端业务接口完成。

## 4. 测试实现

覆盖四种认证状态、结构化 returnTo、非法 pathname/search/hash、重试、authenticated 渲染子路由和 anonymous 导航。断言不出现旧用户资料或受保护内容。
