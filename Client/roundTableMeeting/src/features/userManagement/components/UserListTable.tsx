import { Button, Space, Table, Tooltip } from 'antd'
import { EditOutlined, KeyOutlined } from '@ant-design/icons'
import type { UserSummaryViewModel } from '../types/userManagement'

type Props = { users: UserSummaryViewModel[]; editingUserId?: string; resettingUserId?: string; onEdit: (user: UserSummaryViewModel) => void; onReset: (user: UserSummaryViewModel) => void }

export function UserListTable({ users, editingUserId, resettingUserId, onEdit, onReset }: Props) {
  return <Table<UserSummaryViewModel> rowKey="userId" dataSource={users} pagination={false} tableLayout="fixed" locale={{ emptyText: '暂无账号' }} columns={[
    { title: '用户名', dataIndex: 'username', key: 'username', onCell: () => ({ style: { overflowWrap: 'anywhere' } }) },
    { title: '昵称', dataIndex: 'nickname', key: 'nickname', onCell: () => ({ style: { overflowWrap: 'anywhere' } }) },
    { title: '操作', key: 'actions', width: 120, render: (_, user) => <Space><Tooltip title="修改昵称"><Button aria-label={'修改 ' + user.username + ' 的昵称'} icon={<EditOutlined />} onClick={() => onEdit(user)} disabled={editingUserId === user.userId || resettingUserId === user.userId} /></Tooltip><Tooltip title="重置密码"><Button aria-label={'重置 ' + user.username + ' 的密码'} icon={<KeyOutlined />} onClick={() => onReset(user)} disabled={editingUserId === user.userId || resettingUserId === user.userId} /></Tooltip></Space> },
  ]} />
}
