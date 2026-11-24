import { Alert, Modal, Typography } from 'antd'
import type { ApiError } from '@/api/errors/ApiError'
import type { UserSummaryViewModel } from '../types/userManagement'

type Props = { user?: UserSummaryViewModel; loading: boolean; error?: ApiError; onCancel: () => void; onConfirm: () => Promise<void> }

export function ResetPasswordDialog({ user, loading, error, onCancel, onConfirm }: Props) {
  return <Modal centered className="user-directory-dialog" open={Boolean(user)} title="重置密码" okText="确认重置" cancelText="取消" confirmLoading={loading} closable={!loading} maskClosable={!loading} keyboard={!loading} cancelButtonProps={{ disabled: loading }} onCancel={onCancel} onOk={() => void onConfirm()}>{user && <><p>请确认要重置密码的账号。</p>
    <dl className="user-directory-target"><dt>昵称</dt><dd>{user.nickname}</dd><dt>登录账号</dt><dd>{user.username}</dd></dl>
    <Typography.Paragraph>密码将重置为 <Typography.Text code>123456</Typography.Text>，原密码将无法用于登录。</Typography.Paragraph><Typography.Paragraph type="secondary">已有登录会话不会立即失效。</Typography.Paragraph>{error && <Alert type="error" showIcon message="密码重置失败，请稍后重试。" />}</>}</Modal>
}
