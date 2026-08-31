# 昵称编辑表单实现方案

## 1. 文件和 Props

文件固定为 `features/userManagement/components/EditNicknameForm.tsx`。Props：user、onSubmit、loading、error。user 变化时 resetFields 并设置 nickname 为目标当前值。

## 2. 结构

用户名使用 Typography.Text 只读展示，不注册为 Form 字段；nickname 使用 Input maxLength=64、必填。提示只说明昵称修改不会改变用户名，不描述会议内显示名称行为。

## 3. 错误与提交

只绑定 nickname fieldErrors；error 切换时清理旧错误。user_not_found 由页面关闭；网络/unknown 使用安全 Alert。loading 禁止重复提交和关闭。

## 4. 测试

覆盖目标切换重置、用户名只读、昵称预填、字段校验、提示文案、错误清理和重复提交。
