import { Alert, Form, Input, Modal } from 'antd'
import { useEffect } from 'react'
import type { ApiError } from '@/api/errors/ApiError'
import type { CreateUserInput } from '../types/userManagement'

type Props = { open: boolean; loading: boolean; error?: ApiError; onCancel: () => void; onSubmit: (input: CreateUserInput) => Promise<void> }

export function CreateUserForm({ open, loading, error, onCancel, onSubmit }: Props) {
  const [form] = Form.useForm<CreateUserInput>()
  useEffect(() => { if (open) form.resetFields() }, [open, form])
  useEffect(() => {
    const fieldErrors = error?.fieldErrors ?? {}
    form.setFields([
      { name: 'username', errors: error?.code === 'username_conflict' ? ['这个登录账号已被使用，请换一个。'] : fieldErrors.username ?? [] },
      { name: 'nickname', errors: fieldErrors.nickname ?? [] },
    ])
  }, [error, form])
  let alert: string | undefined
  if (error?.code === 'account_initial_password_configuration_invalid') {
    alert = '当前实例尚未配置有效的开户初始密码，请联系部署管理员。'
  } else if (error && error.code !== 'username_conflict' && !error.fieldErrors) {
    alert = '账号创建失败，请稍后重试。'
  }
  return <Modal centered className="user-directory-dialog" open={open} title="添加账号" okText="添加账号" cancelText="取消" confirmLoading={loading} closable={!loading} maskClosable={!loading} keyboard={!loading} cancelButtonProps={{ disabled: loading }} onCancel={onCancel} onOk={() => form.submit()} destroyOnHidden>
    <Form form={form} layout="vertical" onFinish={onSubmit}>
      <Form.Item name="username" label="登录账号" rules={[{ required: true, whitespace: true, message: '请输入登录账号' }]}>
        <Input maxLength={128} autoComplete="off" />
      </Form.Item>
      <Form.Item name="nickname" label="昵称" rules={[{ required: true, whitespace: true, message: '请输入昵称' }]}>
        <Input maxLength={64} autoComplete="off" />
      </Form.Item>
      {alert && <Alert type="error" showIcon message={alert} />}
    </Form>
  </Modal>
}
