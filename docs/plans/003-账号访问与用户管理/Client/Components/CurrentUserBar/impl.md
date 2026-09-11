# 当前用户栏组件实现方案

## 1. 文件和组件签名

文件固定为 features/authentication/components/CurrentUserBar.tsx。组件无业务 Props，从 useAuthentication 读取 state、logout、isLoggingOut 和 operationError。

## 2. 实现步骤

1. authenticated 时显示 nickname 和辅助 username。
2. logout 时设置本地提交状态，调用 Provider logout。
3. 服务端返回 204 时由 Provider 转 anonymous，路由回到 /login。
4. network/unknown 错误保留用户显示并展示可重试提示。
5. checking、anonymous 和 error 不显示旧资料。

## 3. 控件和可访问性

使用 Typography、Space、Button 和图标。退出按钮提供可见文字或 aria-label，不使用不可解释的图标。loading 状态不改变整体布局尺寸。

## 4. 测试实现

覆盖昵称显示、退出成功、退出幂等、退出网络失败保留当前用户和重复点击防护。

增加“个人设置”链接到 `/settings/profile`，所有已登录用户可见；只导航，不把昵称和密码表单塞入用户栏。验证键盘可达、路由跳转和昵称保存后的顶栏同步。
