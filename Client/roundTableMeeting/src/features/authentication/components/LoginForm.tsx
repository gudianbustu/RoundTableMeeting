import { Alert, Button, Form, Input } from 'antd'
import { useEffect } from 'react'
import type { ApiError } from '@/api/errors/ApiError'
import type { LoginInput } from '../types/authentication'

export function LoginForm({ onSubmit, loading, error }: { onSubmit: (values: LoginInput) => Promise<void>; loading: boolean; error?: ApiError }) {
  const [form] = Form.useForm<LoginInput>()
  useEffect(() => {
    const fieldErrors = error?.fieldErrors
    form.setFields([
      { name: 'username', errors: fieldErrors?.username ?? [] },
      { name: 'password', errors: fieldErrors?.password ?? [] },
    ])
  }, [error, form])

  const submit = async (values: LoginInput) => {
    try { await onSubmit(values) } catch { form.resetFields(['password']) }
  }
  const errorMessage = error?.code === 'unsupported_media_type'
    ? '请求格式不受支持，请刷新页面后重试。'
    : error?.code === 'invalid_credentials'
      ? '账号或密码错误。'
      : error?.code === 'validation_failed'
        ? '请检查输入信息。'
        : error?.kind === 'network'
          ? '服务暂时不可访问，请稍后重试。'
          : error?.kind === 'unknown' || error?.kind === 'problemDetails'
            ? '请求未能完成，请稍后重试。'
            : undefined

  return <Form form={form} layout="vertical" onFinish={submit} requiredMark={false}>
    {errorMessage && <Alert type="error" showIcon message={errorMessage} />}
    <Form.Item label="用户名" name="username" rules={[{ required: true, message: '请输入用户名。' }]}>
      <Input autoComplete="username" maxLength={128} />
    </Form.Item>
    <Form.Item label="密码" name="password" rules={[{ required: true, message: '请输入密码。' }]}>
      <Input.Password autoComplete="current-password" maxLength={256} />
    </Form.Item>
    <Button type="primary" htmlType="submit" loading={loading} disabled={loading} block>登录</Button>
  </Form>
}
