# 用户管理页实现方案

## 1. 文件和依赖

文件固定为 `features/userManagement/pages/UserManagementPage.tsx`。页面使用 userManagement API 适配、四个组件、useAuthentication、PageState、Ant Design Button/Modal/message 和已有图标库。

## 2. 页面结构

根节点使用 `<section className="user-management-page">`，包含紧凑标题行、带 UserPlus 图标的“添加账号”按钮和 UserListTable。添加与昵称修改分别由 Modal 承载 CreateUserForm 和 EditNicknameForm；ResetPasswordDialog 自身负责渲染确认 Modal，页面只传入目标、状态和事件。

## 3. 状态流程

首次 useEffect 调用 loadUsers。loading 渲染稳定表格区域；error 渲染 ErrorState；ready 渲染表格。弹窗开关和提交逻辑按用户管理模块实现方案执行。

页面不在 render 中创建 API Promise，不使用表格排序/分页，不把 users 写入 URL 或 storage。

## 4. 反馈与响应式

添加成功显示“账号已添加”，昵称成功显示“昵称已更新”，重置成功显示“密码已重置为 123456”。窄屏弹窗宽度使用 `min(520px, calc(100vw - 32px))`，表格操作收纳为菜单。

## 5. 完成检查

`/users` 只能在 authenticated 路由渲染；四种写操作互不共用 submitting；页面卸载无 late state update；显示管理员或普通用户身份，并提供管理员设置操作。

## 6. 管理员交互

页面在 AdminGate 放行后才挂载列表加载。增加身份调整确认与 adjustingAdminUserId；同一目标行写入期间禁用其他行内操作。forbidden 立即丢弃 users、关闭所有操作弹窗并刷新当前资料；不能保留旧目录供继续浏览。last_admin_required 由安全文案提示，不显示原始 detail。
