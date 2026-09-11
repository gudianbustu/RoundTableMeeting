import { Button, Space, Table, Tooltip } from 'antd'
import { EditOutlined, KeyOutlined, SafetyCertificateOutlined } from '@ant-design/icons'
import type { UserSummaryViewModel } from '../types/userManagement'

type Props = { onAdmin: (user: UserSummaryViewModel) => void; users: UserSummaryViewModel[]; editingUserId?: string; resettingUserId?: string; onEdit: (user: UserSummaryViewModel) => void; onReset: (user: UserSummaryViewModel) => void }

export function UserListTable({ users, editingUserId, resettingUserId, onEdit, onReset, onAdmin }: Props) {
  return <Table<UserSummaryViewModel> rowKey="userId" dataSource={users} pagination={false} tableLayout="fixed" locale={{ emptyText: '暂无账号' }} columns={[
    { title: '用户名', dataIndex: 'username', key: 'username', onCell: () => ({ style: { overflowWrap: 'anywhere' } }) },
    { title: '昵称', dataIndex: 'nickname', key: 'nickname', onCell: () => ({ style: { overflowWrap: 'anywhere' } }) },
    { title: '身份', key: 'role', render: (_, user) => user.isAdmin ? '管理员' : '普通用户' },
    { title: '操作', key: 'actions', width: 220, render: (_, user) => <Space wrap><Tooltip title={user.isAdmin ? '取消管理员' : '设为管理员'}><Button type={user.isAdmin ? 'primary' : 'default'} aria-label={user.isAdmin ? '取消管理员' : '设为管理员'} aria-pressed={user.isAdmin} icon={<SafetyCertificateOutlined />} onClick={() => onAdmin(user)} /></Tooltip><Tooltip title="修改昵称"><Button aria-label={'修改 ' + user.username + ' 的昵称'} icon={<EditOutlined />} onClick={() => onEdit(user)} disabled={editingUserId === user.userId || resettingUserId === user.userId} /></Tooltip><Tooltip title="重置密码"><Button aria-label={'重置 ' + user.username + ' 的密码'} icon={<KeyOutlined />} onClick={() => onReset(user)} disabled={editingUserId === user.userId || resettingUserId === user.userId} /></Tooltip></Space> },
  ]} />
}
