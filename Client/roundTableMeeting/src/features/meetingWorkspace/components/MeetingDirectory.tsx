import { useEffect } from 'react'
import { FirstMeetingHome } from './FirstMeetingHome'
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
  useEffect(() => {
    if (onlyHistory) onView('history')
  }, [onlyHistory, onView])
  const historical = view === 'history'
  const rows = historical ? history : active
  return <FirstMeetingHome onCreate={onCreate} onJoin={onJoin} onlyHistory={onlyHistory}>
    <section className={'meeting-directory' + (historical ? ' history-home-list' : '')} aria-label="我的会议">
      <h2 className="first-home-bandtitle">我的会议</h2>
      <Tabs activeKey={view} onChange={key => onView(key === 'history' ? 'history' : 'active')} items={[
        { key: 'active', label: '当前参加 · ' + active.length }, { key: 'history', label: '历史会议 · ' + history.length },
      ]} />
      {!rows.length && <Empty description={historical ? '暂无历史会议' : '暂无当前参加的会议'} />}
      <ul>{rows.map(meeting => historical ? <li className="directory-history-card" key={meeting.meetingId}>
        <div><strong>{meeting.name}</strong><small>已退出 · 可查看退出前的记录</small></div>
        <div className="directory-actions">
          <Button className="directory-history-open" onClick={() => onSelect(meeting.meetingId)}>查看记录</Button>
          <Button disabled={busy} onClick={() => onRejoin(meeting.meetingId)}>重新入会</Button>
          <Dropdown trigger={['click']} menu={{ items: [{ key: 'hide', label: '从我的历史移除', onClick: () => onHide(meeting.meetingId) }] }}>
            <Button type="text" aria-label={meeting.name + '的历史操作'} icon={<EllipsisOutlined />} />
          </Dropdown>
        </div>
      </li> : <li className="directory-active-card" key={meeting.meetingId}>
        <button type="button" onClick={() => onSelect(meeting.meetingId)} aria-label={'进入会议：' + meeting.name}>
          <span><strong>{meeting.name}</strong><small>当前参加</small></span>
          <span className="directory-enter-hint" aria-hidden="true">进入会议 ›</span>
        </button>
      </li>)}</ul>
    </section>
  </FirstMeetingHome>
}
