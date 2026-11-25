import { useState } from 'react'
import { cleanup, fireEvent, render, screen, within } from '@testing-library/react'
import { afterEach, expect, it, vi } from 'vitest'
import { MeetingDirectory } from './MeetingDirectory'
import type { MeetingListItem } from '../types/meetingWorkspace'

afterEach(cleanup)

function setup(active: MeetingListItem[]) {
  const past = { meetingId: 'past', name: '历史讨论', myDisplayName: '成员', isCreator: false }
  const actions = { onSelect: vi.fn(), onRejoin: vi.fn(), onHide: vi.fn(), onCreate: vi.fn(), onJoin: vi.fn() }
  function Home() {
    const [view, onView] = useState<'active' | 'history'>('active')
    return <MeetingDirectory active={active} history={[past]} view={view} onView={onView} busy={false} {...actions} />
  }
  render(<Home />)
  return actions
}

it('将会议目录放在品牌卡与完整功能介绍之间，保留两类会议操作', async () => {
  const actions = setup([{ meetingId: 'current', name: '进行中的讨论', myDisplayName: '成员', isCreator: false }])
  const headings = screen.getAllByRole('heading').map(el => el.textContent)
  expect(headings).toEqual(['开始一场圆桌讨论', '我的会议', '在圆桌里，你可以', '邀请 Agent', '公开讨论', '一对一私聊'])
  expect(screen.queryByText('你创建和加入的会议会显示在这里。')).toBeNull()
  const card = screen.getByRole('button', { name: '进入会议：进行中的讨论' })
  expect(card.tagName).toBe('BUTTON')
  fireEvent.click(within(card).getByText('进行中的讨论'))
  expect(actions.onSelect).toHaveBeenCalledWith('current')
  fireEvent.click(screen.getByRole('tab', { name: /历史会议/ }))
  const directory = within(screen.getByRole('region', { name: '我的会议' }))
  fireEvent.click(directory.getByRole('button', { name: '查看记录' }))
  expect(actions.onSelect).toHaveBeenCalledWith('past')
  fireEvent.click(directory.getByRole('button', { name: '重新入会' }))
  expect(actions.onRejoin).toHaveBeenCalledWith('past')
  fireEvent.click(directory.getByRole('button', { name: '历史讨论的历史操作' }))
  fireEvent.click(await screen.findByText('从我的历史移除'))
  expect(actions.onHide).toHaveBeenCalledWith('past')
  expect(actions.onSelect).toHaveBeenCalledTimes(2)
})

it('只有历史会议时默认展示历史，仍可切回当前参加', () => {
  setup([])
  expect(screen.getByRole('tab', { name: /历史会议/ })).toHaveAttribute('aria-selected', 'true')
  fireEvent.click(screen.getByRole('tab', { name: /当前参加/ }))
  expect(screen.getByText('暂无当前参加的会议')).toBeInTheDocument()
  expect(screen.getByRole('heading', { name: '在圆桌里，你可以' })).toBeInTheDocument()
})
