import { Button, Dropdown, Empty, Tabs } from 'antd'
import { EllipsisOutlined } from '@ant-design/icons'
import type { MeetingListItem } from '../types/meetingWorkspace'

type Props = {
  active: MeetingListItem[]; history: MeetingListItem[]; view: 'active' | 'history'
  onView: (view: 'active' | 'history') => void; onSelect: (id: string) => void
  onRejoin: (id: string) => void; onHide: (id: string) => void
  onCreate: () => void; onJoin: () => void; busy: boolean
}
export function MeetingDirectory({ active, history, view, onView, onSelect, onRejoin, onHide, onCreate, onJoin, busy }: Props) {
  const onlyHistory = !active.length && history.length > 0
  const historical = onlyHistory || view === 'history'
  const actions = <div className="directory-entry-actions"><Button type="primary" onClick={onCreate}>创建会议</Button><Button onClick={onJoin}>加入会议</Button></div>
  const rows = historical ? history : active
  return <section className="meeting-directory">
    <header><h1>我的会议</h1>{!onlyHistory && actions}</header>
    {onlyHistory ? <>
      <div className="history-home-banner"><h2>当前没有参加的会议</h2><p>你仍可回看已退出的会议，或重新加入。</p>{actions}</div>
      <h2>历史会议</h2>
    </> : <Tabs activeKey={view} onChange={key => onView(key === 'history' ? 'history' : 'active')} items={[
      { key: 'active', label: '当前参加 · ' + active.length }, { key: 'history', label: '历史会议 · ' + history.length },
    ]} />}
    {!rows.length && <Empty description={historical ? '暂无历史会议' : '暂无当前参加的会议'} />}
    <ul>{rows.map(meeting => <li key={meeting.meetingId}>
      <div><strong>{meeting.name}</strong><small>{historical ? '已退出 · 可查看退出前的记录' : '当前参加'}</small></div>
      <div className="directory-actions"><Button type="link" onClick={() => onSelect(meeting.meetingId)}>{historical ? '查看记录' : '进入会议 ›'}</Button>
        {historical && <><Button type="link" disabled={busy} onClick={() => onRejoin(meeting.meetingId)}>重新入会</Button>
          <Dropdown trigger={['click']} menu={{ items: [{ key: 'hide', label: '从我的历史移除', onClick: () => onHide(meeting.meetingId) }] }}>
            <Button type="text" aria-label={meeting.name + '的历史操作'} icon={<EllipsisOutlined />} />
          </Dropdown></>}
      </div>
    </li>)}</ul>
  </section>
}
