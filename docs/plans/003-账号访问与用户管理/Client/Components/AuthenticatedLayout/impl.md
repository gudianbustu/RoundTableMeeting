# 认证后布局组件实现方案

## 1. 文件和组件签名

文件固定为 app/layouts/AuthenticatedLayout.tsx。布局使用 Outlet 和 CurrentUserBar，不直接调用认证 API。

## 2. 结构实现

1. 固定应用头部和标题。
2. 头部组合 CurrentUserBar。
3. 主区域组合 Outlet。
4. 使用响应式 flex/grid 约束，用户栏 loading 和错误不会推动主内容产生明显跳动。
5. 头部或紧凑导航区域增加带 Users 图标的用户管理入口，固定链接 `/users`，对全部 authenticated 用户可见。

## 3. 退出实现

CurrentUserBar 触发 Provider logout；布局不重复发请求、不预先清除 Context、不直接导航。Provider 确认退出后由路由状态进入 LoginPage。

## 4. 后续扩展

主题三在 Outlet 接入会议工作台，不能把会议数据查询、会议权限或会议列表逻辑放入该布局。

用户管理入口只负责导航，不预加载用户列表、不判断角色。主题三增加工作台导航时必须保留用户管理入口或将其迁入统一菜单，不能删除功能。
