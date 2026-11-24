import { Alert, Form, Input, Modal, Typography } from 'antd'
import { useEffect } from 'react'
import type { ApiError } from '@/api/errors/ApiError'
import type { UpdateNicknameInput, UserSummaryViewModel } from '../types/userManagement'

type Props = { user?: UserSummaryViewModel; loading: boolean; error?: ApiError; onCancel: () => void; onSubmit: (input: UpdateNicknameInput) => Promise<void> }

export function EditNicknameForm({ user, loading, error, onCancel, onSubmit }: Props) {
  const [form] = Form.useForm<UpdateNicknameInput>()
  useEffect(() => { if (user) { form.resetFields(); form.setFieldsValue({ nickname: user.nickname }) } }, [user, form])
  useEffect(() => { form.setFields([{ name: 'nickname', errors: error?.fieldErrors?.nickname ?? [] }]) }, [error, form])
  const alert = error && !error.fieldErrors ? '昵称修改失败，请稍后重试。' : undefined
  return <Modal centered className="user-directory-dialog" open={Boolean(user)} title="修改昵称" okText="保存" cancelText="取消" confirmLoading={loading} closable={!loading} maskClosable={!loading} keyboard={!loading} cancelButtonProps={{ disabled: loading }} onCancel={onCancel} onOk={() => form.submit()} destroyOnHidden><Form form={form} layout="vertical" onFinish={onSubmit}><Form.Item label="登录账号"><Typography.Text>{user?.username}</Typography.Text></Form.Item><Form.Item name="nickname" label="昵称" rules={[{ required: true, whitespace: true, message: '请输入昵称' }]}><Input maxLength={64} /></Form.Item><Typography.Text type="secondary">昵称修改不会改变登录账号。</Typography.Text>{alert && <Alert type="error" showIcon message={alert} />}</Form></Modal>
}
