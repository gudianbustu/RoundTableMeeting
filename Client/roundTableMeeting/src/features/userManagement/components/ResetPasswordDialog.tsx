import { Alert, Modal, Typography } from 'antd'
import type { ApiError } from '@/api/errors/ApiError'
import type { UserSummaryViewModel } from '../types/userManagement'

type Props = { user?: UserSummaryViewModel; loading: boolean; error?: ApiError; onCancel: () => void; onConfirm: () => Promise<void> }

export function ResetPasswordDialog({ user, loading, error, onCancel, onConfirm }: Props) {
  return <Modal open={Boolean(user)} title="重置密码" okText="重置为 123456" cancelText="取消" confirmLoading={loading} closable={!loading} maskClosable={!loading} keyboard={!loading} cancelButtonProps={{ disabled: loading }} onCancel={onCancel} onOk={() => void onConfirm()}>{user && <><Typography.Paragraph>将“{user.username}”（{user.nickname}）的密码重置为 <Typography.Text code>123456</Typography.Text>。</Typography.Paragraph><Typography.Paragraph type="secondary">已有登录会话不会立即失效。</Typography.Paragraph>{error && <Alert type="error" showIcon message="密码重置失败，请稍后重试。" />}</>}</Modal>
}
