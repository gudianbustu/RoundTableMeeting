import { useState } from 'react'
import { Button, Dropdown, Empty, Flex, Popover, Segmented, Skeleton, Typography } from 'antd'
import { DeleteOutlined, EllipsisOutlined, InfoCircleOutlined, LoginOutlined, PlusOutlined, TeamOutlined } from '@ant-design/icons'
import type { MeetingDetails, MeetingListItem } from '../types/meetingWorkspace'

export function MeetingNavigation({ meetings, selectedId, details, detailLoading, onDelete, onLeave, loading, onSelect, onCreate, onJoin, view = 'active', onViewChange }: { details?: MeetingDetails; detailLoading?: boolean; onDelete?: () => void; onLeave?: () => void; view?: 'active' | 'history'; onViewChange?: (view: 'active' | 'history') => void; meetings: MeetingListItem[]; selectedId?: string; loading?: boolean; onSelect: (id: string) => void; onCreate: () => void; onJoin: () => void }) {
  const [infoId, setInfoId] = useState<string>()
  const [menuId, setMenuId] = useState<string>()
  const activate = (id: string) => { if (selectedId !== id) onSelect(id) }
  return <aside className="meeting-navigation"><Flex vertical gap="middle" className="meeting-navigation-content">
    <Flex align="center" justify="space-between" wrap gap="small" className="meeting-navigation-heading"><Typography.Title level={4} style={{ margin: 0 }}>我的会议</Typography.Title><Flex gap="small"><Button aria-label="创建会议" title="创建会议" icon={<PlusOutlined />} onClick={onCreate} /><Button aria-label="加入会议" title="加入会议" icon={<LoginOutlined />} onClick={onJoin} /></Flex></Flex>
    <Segmented block value={view} options={[{ label: '当前会议', value: 'active' }, { label: '历史会议', value: 'history' }]} onChange={value => { setInfoId(undefined); setMenuId(undefined); onViewChange?.(value as 'active' | 'history') }} />
    <div className="meeting-navigation-scroll">{loading ? <Flex vertical gap="small"><Skeleton active paragraph={{ rows: 1 }} title={false} /><Skeleton active paragraph={{ rows: 1 }} title={false} /></Flex> : meetings.length === 0 ? <Empty image={Empty.PRESENTED_IMAGE_SIMPLE} description="还没有会议" /> : <nav aria-label="会议列表" className="meeting-navigation-list">{meetings.map(meeting => {
      const current = selectedId === meeting.meetingId && details?.meetingId === meeting.meetingId && !detailLoading ? details : undefined
      return <div key={meeting.meetingId} className={selectedId === meeting.meetingId ? 'meeting-navigation-item selected' : 'meeting-navigation-item'}>
        <button type="button" className="meeting-navigation-select" onClick={() => { setInfoId(undefined); setMenuId(undefined); onSelect(meeting.meetingId) }}><TeamOutlined /><span><strong>{meeting.name}</strong><small>{meeting.myDisplayName}</small></span></button>
        <div className={`meeting-navigation-actions${infoId === meeting.meetingId || menuId === meeting.meetingId ? ' is-open' : ''}`}>
          <Popover trigger="click" placement="rightTop" open={infoId === meeting.meetingId} onOpenChange={open => { setInfoId(open ? meeting.meetingId : undefined); setMenuId(undefined); if (open) activate(meeting.meetingId) }} title={meeting.name} content={<div className="meeting-description">{current ? current.description || '暂无会议描述' : '会议详情加载中或暂不可用，请稍后重试。'}</div>}>
            <Button type="text" size="small" aria-label={`会议信息 ${meeting.name}`} title="会议信息" icon={<InfoCircleOutlined />} />
          </Popover>
          <Dropdown trigger={['click']} placement="bottomRight" open={menuId === meeting.meetingId} onOpenChange={open => { setMenuId(open ? meeting.meetingId : undefined); setInfoId(undefined); if (open) activate(meeting.meetingId) }} menu={{ items: current ? [...(current.canLeave ? [{ key: 'leave', label: '退出会议' }] : []), ...(current.canDelete ? [{ key: 'delete', label: '删除会议', icon: <DeleteOutlined />, danger: true }] : []), ...(!current.canLeave && !current.canDelete ? [{ key: 'none', label: '暂无可用操作', disabled: true }] : [])] : [{ key: 'loading', label: '等待会议详情', disabled: true }], onClick: ({ key }) => { setMenuId(undefined); if (key === 'delete' && current?.canDelete) onDelete?.(); if (key === 'leave' && current?.canLeave) onLeave?.() } }}>
            <Button type="text" size="small" aria-label={`更多会议操作 ${meeting.name}`} title="更多会议操作" icon={<EllipsisOutlined />} />
          </Dropdown>
        </div>
      </div>
    })}</nav>}</div>
  </Flex></aside>
}