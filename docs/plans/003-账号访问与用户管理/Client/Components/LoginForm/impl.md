# 登录表单组件实现方案

## 1. 文件和组件签名

文件固定为 features/authentication/components/LoginForm.tsx。组件 Props 固定为：`onSubmit(values: LoginInput): Promise<void>`、`loading: boolean`、`error?: ApiError`。

## 2. Ant Design 实现

使用 Form、Input、Input.Password、Button 和 Alert。用户名原始输入必填且 maxLength=128，密码必填且 maxLength=256；客户端长度只用于即时反馈，服务端在 Trim/NFC 后按显示值 64、比较值 128 执行权威校验并返回 validation_failed。密码不 Trim、不做 Unicode 规范化。

当 ApiError.code 为 validation_failed 且包含 fieldErrors 时，将 username/password 的错误分别绑定到对应 Form.Item，同时保留不暴露后端原始细节的表单级安全提示。

## 3. 状态实现

- Form 内部保存输入值，不写入浏览器持久化存储。
- loading 时禁用提交按钮和重复提交。
- onFinish 调用 onSubmit；异常由父级传入或由表单边界显示。
- 提交失败后保留 username 字段并通过 form.resetFields(['password']) 清空 password。
- 使用 html label、aria-invalid、字段错误关联和键盘 Enter 提交。

## 4. 测试实现

使用 user-event 填写、提交、空字段校验、重复点击和失败后密码清空。不要整体 mock Ant Design；只在必要时 mock 父级 onSubmit。
