import type { UserSummaryViewModel } from '../types/userManagement'

type Props = {
  users: UserSummaryViewModel[]
  emptyText?: string
  editingUserId?: string
  resettingUserId?: string
  onEdit: (user: UserSummaryViewModel) => void
  onAdmin: (user: UserSummaryViewModel) => void
  onReset: (user: UserSummaryViewModel) => void
}

export function UserListTable({ users, emptyText, editingUserId, resettingUserId, onEdit, onAdmin, onReset }: Props) {
  return <>
    <table className="user-directory-table">
      <thead><tr><th scope="col">昵称</th><th scope="col">登录账号</th><th scope="col">操作</th></tr></thead>
      <tbody>{users.map(user => {
        const busy = user.userId === editingUserId || user.userId === resettingUserId
        return <tr key={user.userId}>
          <td><strong>{user.nickname}</strong></td>
          <td><span className="user-account-label" aria-hidden="true">登录账号　</span>{user.username}</td>
          <td><div className="user-row-actions">
            <button type="button" disabled={busy} onClick={() => onEdit(user)} aria-label={`修改 ${user.username} 的昵称`}>修改昵称</button>
            <button type="button" disabled={busy} onClick={() => onAdmin(user)}>{user.isAdmin ? '取消管理员' : '设为管理员'}</button>
            <button type="button" disabled={busy} onClick={() => onReset(user)} aria-label={`重置 ${user.username} 的密码`}>重置密码</button>
          </div></td>
        </tr>
      })}</tbody>
    </table>
    {users.length === 0 && <p className="user-directory-empty" role="status">{emptyText ?? '暂无账号'}</p>}
  </>
}
