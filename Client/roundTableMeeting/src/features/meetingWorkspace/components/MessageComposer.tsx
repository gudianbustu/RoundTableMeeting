import { useImperativeHandle, useLayoutEffect, useRef, type ReactNode, type Ref } from 'react'
import type { MentionsRef } from 'antd/es/mentions'
import { Button, Mentions, Tag } from 'antd'
import { SendOutlined } from '@ant-design/icons'
import type { FailedSubmission, MentionCandidate, SendState } from '../types/realtimeChat'
import type { Participant } from '../types/meetingWorkspace'
import { getMentionCandidates } from '../chat/mentionCandidates'
export interface MessageComposerHandle { mention: (displayName: string) => void }
export function MessageComposer({ composerRef, tools, value, send, failedSubmission, participants, onChange, onSend, onRetry }: { composerRef?: Ref<MessageComposerHandle>; tools?: ReactNode; value: string; send: SendState; failedSubmission?: FailedSubmission; participants: Participant[]; onChange: (value: string) => void; onSend: (candidates: MentionCandidate[]) => void; onRetry: () => void }) {
  const inputRef = useRef<MentionsRef>(null)
  const focusPending = useRef(false)
  const mentionPopupRef = useRef<HTMLDivElement>(null)
  useImperativeHandle(composerRef, () => ({
    mention(displayName) {
      if (send === 'sending') return
      const separator = value && !/\s$/u.test(value) ? ' ' : ''
      focusPending.current = true
      onChange(value + separator + '@' + displayName.normalize('NFC') + ' ')
    },
  }), [send, value, onChange])
  useLayoutEffect(() => {
    if (!focusPending.current) return
    focusPending.current = false
    const textarea = inputRef.current?.nativeElement.querySelector('textarea')
    textarea?.focus({ preventScroll: true })
    textarea?.setSelectionRange(value.length, value.length)
  }, [value])
  const candidates = getMentionCandidates(value, participants)
  const failure = send === 'failed' ? failedSubmission : undefined
  const error = failure?.errorCode === 'message_send_rate_limited' && failure.retryAfterSeconds
    ? `发送过于频繁，请在 ${failure.retryAfterSeconds} 秒后重试。`
    : failure ? '消息发送失败，请重试。' : undefined
  return <div className="message-composer">
    <Mentions
      placement="top"
      popupRender={menu => <div ref={mentionPopupRef}>{menu}</div>}
      options={participants.map(participant => ({
        key: participant.participantId,
        value: participant.displayName.normalize('NFC'),
        label: <span className="mention-option"><span>{participant.displayName}</span><Tag color={participant.type === 'agent' ? 'cyan' : 'blue'}>{participant.type === 'agent' ? 'Agent' : '人类'}</Tag></span>,
      }))}
      filterOption={(query, option) => (option.value ?? '').normalize('NFC').toLocaleLowerCase().includes(query.normalize('NFC').toLocaleLowerCase())}
      notFoundContent="没有匹配的参与者"
      className="composer-mentions"
      onKeyDownCapture={event => {
        if (event.nativeEvent.isComposing || event.keyCode === 229 || (event.key === 'Enter' && event.shiftKey)) {
          event.stopPropagation()
          return
        }
        if (event.key === 'Tab' && !event.shiftKey) {
          // Scope the lookup to this composer's mounted popup and reuse its selection handler.
          const activeOption = mentionPopupRef.current?.querySelector<HTMLElement>('.ant-mentions-dropdown-menu-item-active:not(.ant-mentions-dropdown-menu-item-disabled)')
          if (activeOption) {
            event.preventDefault()
            event.stopPropagation()
            activeOption.click()
          }
        }
      }}
      ref={inputRef} variant="borderless" value={value} disabled={send === 'sending'} autoSize={{ minRows: 2, maxRows: 6 }} placeholder="输入消息" onChange={onChange} onPressEnter={event => { if (!event.shiftKey && !event.nativeEvent.isComposing) { event.preventDefault(); if (value.trim()) onSend(candidates) } }} />
    {error && <span role="alert">{error}</span>}
    <div className="message-composer-footer">
      {tools && <div className="message-composer-tools">{tools}</div>}
      <div className="message-composer-actions">
      {failure && <Button danger onClick={onRetry}>重试发送</Button>}
      <Button type="primary" aria-label="发送" title="发送" icon={<SendOutlined />} loading={send === 'sending'} disabled={!value.trim()} onClick={() => onSend(candidates)} />
      </div>
    </div>
  </div>
}
