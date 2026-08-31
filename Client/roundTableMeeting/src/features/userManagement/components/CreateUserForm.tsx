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
      { name: 'username', errors: error?.code === 'username_conflict' ? ['用户名已存在'] : fieldErrors.username ?? [] },
      { name: 'nickname', errors: fieldErrors.nickname ?? [] },
      { name: 'initialPassword', errors: fieldErrors.initialPassword ?? [] },
    ])
    if (error) form.setFieldValue('initialPassword', '')
  }, [error, form])
  const alert = error && error.code !== 'username_conflict' && !error.fieldErrors ? '账号创建失败，请稍后重试。' : undefined
  return <Modal open={open} title="添加账号" okText="创建" cancelText="取消" confirmLoading={loading} closable={!loading} maskClosable={!loading} keyboard={!loading} cancelButtonProps={{ disabled: loading }} onCancel={onCancel} onOk={() => void form.validateFields().then(onSubmit)} destroyOnHidden>
    <Form form={form} layout="vertical"><Form.Item name="username" label="用户名" rules={[{ required: true, whitespace: true, message: '请输入用户名' }]}><Input maxLength={128} autoComplete="off" /></Form.Item><Form.Item name="nickname" label="昵称" rules={[{ required: true, whitespace: true, message: '请输入昵称' }]}><Input maxLength={64} autoComplete="off" /></Form.Item><Form.Item name="initialPassword" label="初始密码" rules={[{ required: true, whitespace: true, message: '请输入初始密码' }]}><Input.Password maxLength={256} autoComplete="new-password" /></Form.Item>{alert && <Alert type="error" showIcon message={alert} />}</Form>
  </Modal>
}
