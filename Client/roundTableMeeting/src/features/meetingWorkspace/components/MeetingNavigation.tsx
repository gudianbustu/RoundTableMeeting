import { useState, type ButtonHTMLAttributes } from 'react'
import { CSS } from '@dnd-kit/utilities'
import { useSortable } from '@dnd-kit/sortable'
import { SortableList } from '@/features/avatars/AvatarLibrarySort'
import { useMeetingOrder } from '../hooks/useMeetingOrder'
import { Badge, Button, Dropdown, Empty, Flex, Popover, Segmented, Skeleton, Typography } from 'antd'
import { RightOutlined, DeleteOutlined, EllipsisOutlined, InfoCircleOutlined, LoginOutlined, PlusOutlined } from '@ant-design/icons'
import type { MeetingDetails, MeetingListItem } from '../types/meetingWorkspace'

function SortableMeetingButton({ meetingId, ...button }: ButtonHTMLAttributes<HTMLButtonElement> & { meetingId: string }) {
  const { attributes, listeners, setNodeRef, isDragging, isOver, activeIndex, index, transform } = useSortable({ id: meetingId })
  return <button {...button} {...attributes} {...listeners} ref={setNodeRef}
    style={isDragging ? { transform: CSS.Translate.toString(transform) } : undefined}
    data-dragging={isDragging || undefined}
    data-insert={isOver && !isDragging ? (activeIndex < index ? 'after' : 'before') : undefined} />
}

export function MeetingNavigation({ orderOwner, unreadCounts = {}, meetings, historyMeetings, selectedId, details, detailLoading, onDelete, onLeave, onRejoin, onHide, operationLoading, loading, onSelect, onCreate, onJoin, view = 'active', onViewChange }: { orderOwner?: string; unreadCounts?: Record<string, number>; details?: MeetingDetails; detailLoading?: boolean; onRejoin?: (id: string) => void; onHide?: (id: string) => void; operationLoading?: boolean; onDelete?: () => void; onLeave?: () => void; view?: 'active' | 'history'; onViewChange?: (view: 'active' | 'history') => void; meetings: MeetingListItem[]; historyMeetings?: MeetingListItem[]; selectedId?: string; loading?: boolean; onSelect: (id: string) => void; onCreate: () => void; onJoin: () => void }) {
  const [historyExpanded, setHistoryExpanded] = useState(false)
  const [infoId, setInfoId] = useState<string>()
  const [menuId, setMenuId] = useState<string>()
  const { ordered, move } = useMeetingOrder(meetings, orderOwner)
  const activate = (id: string) => { if (selectedId !== id) onSelect(id) }
  const renderList = (rows: MeetingListItem[], history: boolean) => loading ? <Flex vertical gap="small"><Skeleton active paragraph={{ rows: 1 }} title={false} /><Skeleton active paragraph={{ rows: 1 }} title={false} /></Flex> : rows.length === 0 ? <Empty image={Empty.PRESENTED_IMAGE_SIMPLE} description={history ? '暂无历史会议' : '还没有会议'} /> : renderRows(rows, history)
  const renderRows = (rows: MeetingListItem[], history: boolean) => {
    const sortable = Boolean(orderOwner && !history)
    const items = rows.map(meeting => {
      const current = selectedId === meeting.meetingId && details?.meetingId === meeting.meetingId && !detailLoading ? details : undefined
      const count = !history ? unreadCounts[meeting.meetingId] ?? 0 : 0
      const historical = history && (!current || current.membershipState === 'left')
      const menuItems = historical ? [
          { key: 'rejoin', label: '重新入会', icon: <LoginOutlined />, disabled: operationLoading },
          { key: 'hide', label: '从我的历史移除', icon: <DeleteOutlined />, danger: true, disabled: operationLoading },
        ] : !current ? [{ key: 'loading', label: '等待会议详情', disabled: true }] : history ? [{ key: 'none', label: '暂无可用操作', disabled: true }] : [
          ...(current.canLeave ? [{ key: 'leave', label: '退出会议' }] : []),
          ...(current.canDelete ? [{ key: 'delete', label: '永久删除会议', icon: <DeleteOutlined />, danger: true }] : []),
          ...(!current.canLeave && !current.canDelete ? [{ key: 'none', label: '暂无可用操作', disabled: true }] : []),
        ]
      const selection = {
        type: 'button' as const, className: 'meeting-navigation-select',
        'aria-current': selectedId === meeting.meetingId ? 'page' as const : undefined,
        onClick: () => { setInfoId(undefined); setMenuId(undefined); onSelect(meeting.meetingId) },
      }
      const label = <><i aria-hidden="true" className="meeting-chat-icon" /><span><span className="meeting-nav-title"><strong>{meeting.name}</strong>{count > 0 && <Badge count={count} overflowCount={999} aria-label={`未读 ${count > 999 ? '999+' : count} 条`} />}</span>{!historyMeetings && <small>{meeting.myDisplayName}</small>}</span></>
      return <div key={meeting.meetingId} className={selectedId === meeting.meetingId ? 'meeting-navigation-item selected' : 'meeting-navigation-item'}>
        {sortable
          ? <SortableMeetingButton meetingId={meeting.meetingId} {...selection}>{label}</SortableMeetingButton>
          : <button {...selection}>{label}</button>}
        <div className={`meeting-navigation-actions${infoId === meeting.meetingId || menuId === meeting.meetingId ? ' is-open' : ''}`}>
          <Popover trigger="click" placement="rightTop" open={infoId === meeting.meetingId} onOpenChange={open => { setInfoId(open ? meeting.meetingId : undefined); setMenuId(undefined); if (open) activate(meeting.meetingId) }} title={meeting.name} content={<div className="meeting-description">{current ? current.description || '暂无会议描述' : '会议详情加载中或暂不可用，请稍后重试。'}</div>}>
            <Button type="text" size="small" aria-label={`会议信息 ${meeting.name}`} title="会议信息" icon={<InfoCircleOutlined />} />
          </Popover>
          <Dropdown trigger={['click']} placement="bottomRight" open={menuId === meeting.meetingId} onOpenChange={open => { setMenuId(open ? meeting.meetingId : undefined); setInfoId(undefined); if (open && !history) activate(meeting.meetingId) }} menu={{ items: menuItems, onClick: ({ key }) => { setMenuId(undefined); if (historical && key === 'rejoin') onRejoin?.(meeting.meetingId); if (historical && key === 'hide') onHide?.(meeting.meetingId); if (key === 'delete' && current?.canDelete) onDelete?.(); if (key === 'leave' && current?.canLeave) onLeave?.() } }}>
            <Button type="text" size="small" aria-label={`更多会议操作 ${meeting.name}`} title="更多会议操作" icon={<EllipsisOutlined />} />
          </Dropdown>
        </div>
      </div>
    })
    return <nav aria-label={history ? '历史会议列表' : '会议列表'} className={sortable ? undefined : 'meeting-navigation-list'}>
      {sortable ? <SortableList ids={rows.map(row => row.meetingId)} groups listClassName="meeting-navigation-list" onMove={move}>{items}</SortableList> : items}
    </nav>
  }
  return <aside className="meeting-navigation"><Flex vertical gap="middle" className="meeting-navigation-content">
    {!historyMeetings && <Flex align="center" justify="space-between" wrap gap="small" className="meeting-navigation-heading"><Typography.Title level={4} style={{ margin: 0 }}>我的会议</Typography.Title><Flex gap="small"><Button aria-label="创建会议" title="创建会议" icon={<PlusOutlined />} onClick={onCreate} /><Button aria-label="加入会议" title="加入会议" icon={<LoginOutlined />} onClick={onJoin} /></Flex></Flex>}
    {!historyMeetings && <Segmented block value={view} options={[{ label: '当前会议', value: 'active' }, { label: '历史会议', value: 'history' }]} onChange={value => { setInfoId(undefined); setMenuId(undefined); onViewChange?.(value as 'active' | 'history') }} />}
    <div className="meeting-navigation-scroll" data-sort-viewport>{renderList(ordered, !historyMeetings && view === 'history')}</div>
    {historyMeetings && <div className="meeting-navigation-add">
      <Dropdown trigger={['click']} placement="topRight" align={{ offset: [-3, -6] }} classNames={{ root: 'meeting-add-menu' }} menu={{ items: [
        { key: 'join', label: '加入会议', onClick: onJoin },
        { key: 'create', label: '创建会议', onClick: onCreate },
      ] }}>
        <Button shape="circle" className="meeting-add-trigger" aria-label="加入或创建会议" icon={<PlusOutlined />} />
      </Dropdown>
    </div>}
    {historyMeetings && <div className="meeting-navigation-history">
      <button type="button" className="meeting-history-toggle" aria-expanded={historyExpanded} onClick={() => setHistoryExpanded(open => !open)}>
        <RightOutlined rotate={historyExpanded ? 90 : 0} aria-hidden="true" />历史会议
      </button>
      {historyExpanded && <div className="meeting-navigation-scroll">{renderList(historyMeetings, true)}</div>}
    </div>}
  </Flex></aside>
}
