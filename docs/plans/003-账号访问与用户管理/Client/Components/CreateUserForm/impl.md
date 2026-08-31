# 添加账号表单实现方案

## 1. 文件和 Props

文件固定为 `features/userManagement/components/CreateUserForm.tsx`。Props：onSubmit、loading、error。

## 2. 字段实现

使用 Form vertical：username Input maxLength=128 autocomplete=off；nickname Input maxLength=64；initialPassword Input.Password maxLength=256 autocomplete=new-password。initialPassword 的本地规则拒绝空字符串和全空格，但不修改值；包含非空白字符时保留首尾空格原样提交。

## 3. 错误与提交

每次显式设置 username/nickname/initialPassword 三个字段 errors，不存在使用 [] 清理旧错误，只接受这三个键。username_conflict 映射 username。

Alert 只显示安全通用文案，不显示后端原始 detail。提交中禁用重复提交；失败后保留 username/nickname，清空 initialPassword；成功后由页面销毁 Modal。

## 4. 测试

覆盖必填、长度、原始密码空格保留、冲突字段错误、错误清理、失败清密、重复提交和成功事件。
