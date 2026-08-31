# 用户列表组件实现方案

## 1. 文件和 Props

文件固定为 `features/userManagement/components/UserListTable.tsx`。Props：users、loading、editingUserId、resettingUserId、onEditNickname、onResetPassword。

## 2. Ant Design 表格

使用 `Table<UserSummaryViewModel>`，`rowKey="userId"`，`pagination=false`，`tableLayout="fixed"`。用户名和昵称内容允许任意断词。

桌面操作列显示编辑图标按钮和更多菜单；窄屏只显示更多菜单，包含“修改昵称”“重置密码”。图标按钮提供 Tooltip 和 aria-label。

## 3. 状态

loading 使用 Table loading。目标 id 匹配时禁用该目标对应命令；同一行另一类操作也禁用，避免并行修改。其他行保持可用。空数组显示“暂无账号”。

## 4. 测试

覆盖列内容、稳定 rowKey、行事件、目标行禁用、其他行可用、空状态和窄屏菜单语义。
