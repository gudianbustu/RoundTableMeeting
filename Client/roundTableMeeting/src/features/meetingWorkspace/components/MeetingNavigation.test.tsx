import { cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react'
import { afterEach, expect, it, vi } from 'vitest'
import { MeetingNavigation } from './MeetingNavigation'
import { ParticipantList } from './ParticipantList'

afterEach(cleanup)
const meetings = [{ meetingId: 'a', name: '甲会议', myDisplayName: 'Alice', isCreator: true }, { meetingId: 'b', name: '乙会议', myDisplayName: 'Alice', isCreator: false }]
const details = { meetingId: 'a', name: '甲会议', description: '会议背景', canManageInvitation: true, canDelete: true, participants: [] }

it('moves invite to the participant heading and only exposes it when authorized', () => {
  const invite = vi.fn()
  const { rerender } = render(<ParticipantList participants={[]} onInvite={invite} />)
  expect(screen.getByText('参与者').parentElement).toContainElement(screen.getByRole('button', { name: '邀请入会' }))
  fireEvent.click(screen.getByRole('button', { name: '邀请入会' }))
  expect(invite).toHaveBeenCalledOnce()
  rerender(<ParticipantList participants={[]} />)
  expect(screen.queryByRole('button', { name: '邀请入会' })).not.toBeInTheDocument()
})

it('keeps list selection separate from info and permission-controlled destructive actions', async () => {
  const select = vi.fn(), remove = vi.fn()
  render(<MeetingNavigation meetings={meetings} selectedId="a" details={details} onSelect={select} onCreate={vi.fn()} onJoin={vi.fn()} onDelete={remove} />)
  fireEvent.click(screen.getByRole('button', { name: '会议信息 甲会议' }))
  expect(await screen.findByText('会议背景')).toBeInTheDocument()
  expect(select).not.toHaveBeenCalled()
  fireEvent.click(screen.getByRole('button', { name: '更多会议操作 甲会议' }))
  fireEvent.click(await screen.findByRole('menuitem', { name: /删除会议/ }))
  expect(remove).toHaveBeenCalledOnce()
  expect(select).not.toHaveBeenCalled()
  expect(document.querySelector('button button')).toBeNull()
})

it('routes historical deletion and rejoin to personal membership actions', async () => {
  const hide = vi.fn(), rejoin = vi.fn(), remove = vi.fn()
  render(<MeetingNavigation meetings={meetings} selectedId="a" view="history" details={{ ...details, membershipState: 'left', canDelete: false }} onSelect={vi.fn()} onCreate={vi.fn()} onJoin={vi.fn()} onHide={hide} onRejoin={rejoin} onDelete={remove} />)
  const more = screen.getByRole('button', { name: '更多会议操作 甲会议' })
  fireEvent.click(more)
  fireEvent.click(await screen.findByRole('menuitem', { name: /重新入会/ }))
  expect(rejoin).toHaveBeenCalledWith('a')
  fireEvent.click(more)
  fireEvent.click(await screen.findByRole('menuitem', { name: /从我的历史移除/ }))
  expect(hide).toHaveBeenCalledWith('a')
  expect(remove).not.toHaveBeenCalled()
})

it('waits for the target meeting permissions instead of reusing the previous meeting delete action', async () => {
  const select = vi.fn(), remove = vi.fn(), leave = vi.fn()
  const props = { meetings, onSelect: select, onCreate: vi.fn(), onJoin: vi.fn(), onDelete: remove, onLeave: leave }
  const { rerender } = render(<MeetingNavigation {...props} selectedId="a" details={details} />)
  fireEvent.click(screen.getByRole('button', { name: '更多会议操作 乙会议' }))
  expect(select).toHaveBeenCalledWith('b')
  expect(await screen.findByRole('menuitem', { name: '等待会议详情' })).toHaveAttribute('aria-disabled', 'true')
  expect(screen.queryByRole('menuitem', { name: /删除会议/ })).not.toBeInTheDocument()
  rerender(<MeetingNavigation {...props} selectedId="b" details={{ ...details, meetingId: 'b', canDelete: false, canLeave: true }} />)
  await waitFor(() => expect(screen.getByRole('menuitem', { name: '退出会议' })).toBeInTheDocument())
  fireEvent.click(screen.getByRole('menuitem', { name: '退出会议' }))
  expect(leave).toHaveBeenCalledOnce()
  expect(remove).not.toHaveBeenCalled()
})
