import { useImperativeHandle, useLayoutEffect, useRef, type ReactNode, type Ref } from 'react'
import type { MentionsRef } from 'antd/es/mentions'
import { Button, Mentions, Tag } from 'antd'
import { SendOutlined } from '@ant-design/icons'
import type { MentionCandidate, SendState } from '../types/realtimeChat'
import type { Participant } from '../types/meetingWorkspace'
import { getMentionCandidates, groupMentionNames, mentionableParticipants } from '../chat/mentionCandidates'
export interface MessageComposerHandle { mention: (displayName: string) => void; privateChat?: (participantId: string) => void }
export function MessageComposer({ publicChat = false, placeholder = '输入消息', composerRef, attachments, onCancelReply, onFiles, tools, value, send, participants, onChange, onSend, hasAttachments = false, disabled = false }: { publicChat?: boolean; placeholder?: string; composerRef?: Ref<MessageComposerHandle>; attachments?: ReactNode; onCancelReply?: () => void; onFiles?: (files: File[]) => void; tools?: ReactNode; hasAttachments?: boolean; disabled?: boolean; value: string; send: SendState; participants: Participant[]; onChange: (value: string) => void; onSend: (candidates: MentionCandidate[]) => void }) {
  const inputRef = useRef<MentionsRef>(null)
  const focusPending = useRef(false)
  const mentionPopupRef = useRef<HTMLDivElement>(null)
  useImperativeHandle(composerRef, () => ({
    mention(displayName) {
      if (disabled || send === 'sending' || send === 'pending_confirmation') return
      const separator = value && !/\s$/u.test(value) ? ' ' : ''
      focusPending.current = true
      onChange(value + separator + '@' + displayName.normalize('NFC') + ' ')
    },
  }), [disabled, send, value, onChange])
  useLayoutEffect(() => {
    if (!focusPending.current) return
    focusPending.current = false
    const textarea = inputRef.current?.nativeElement.querySelector('textarea')
    textarea?.focus({ preventScroll: true })
    textarea?.setSelectionRange(value.length, value.length)
  }, [value])
  const candidates = getMentionCandidates(value, participants, publicChat)
  return <div className="message-composer"
    onPaste={event => {
      const images = Array.from(event.clipboardData.files).filter(file => file.type.startsWith('image/'))
      if (!images.length) return
      event.preventDefault()
      if (!disabled && send !== 'sending' && send !== 'pending_confirmation') onFiles?.(images)
    }}
    onDragOver={event => {
      if (!Array.from(event.dataTransfer.types).includes('Files')) return
      event.preventDefault()
      event.dataTransfer.dropEffect = disabled || send === 'sending' || send === 'pending_confirmation' ? 'none' : 'copy'
    }}
    onDrop={event => {
      if (!Array.from(event.dataTransfer.types).includes('Files')) return
      event.preventDefault()
      if (!disabled && send !== 'sending' && send !== 'pending_confirmation') onFiles?.(Array.from(event.dataTransfer.files))
    }}>
    {attachments}
    <Mentions
      placement="top"
      popupRender={menu => <div ref={mentionPopupRef}>{menu}</div>}
      options={[
        ...(publicChat ? groupMentionNames.map(name => ({
          key: 'group:' + name, value: name,
          label: <span className="mention-option"><span>{name}</span><Tag>群体</Tag></span>,
        })) : []),
        ...mentionableParticipants(participants, publicChat).map(participant => ({
          key: participant.participantId,
          value: participant.displayName.normalize('NFC'),
          label: <span className="mention-option"><span>{participant.displayName}</span><Tag color={participant.type === 'agent' ? 'cyan' : 'blue'}>{participant.type === 'agent' ? 'Agent' : '人类'}</Tag></span>,
        })),
      ]}
      filterOption={(query, option) => (option.value ?? '').normalize('NFC').toLocaleLowerCase().includes(query.normalize('NFC').toLocaleLowerCase())}
      notFoundContent="没有匹配的参与者"
      className="composer-mentions"
      onKeyDownCapture={event => {
        if (event.nativeEvent.isComposing || event.keyCode === 229 || (event.key === 'Enter' && event.shiftKey)) {
          event.stopPropagation()
          return
        }
        if (event.key === 'Backspace' && !value.length && onCancelReply && !disabled && send !== 'sending' && send !== 'pending_confirmation') {
          event.preventDefault()
          onCancelReply()
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
      ref={inputRef} variant="borderless" value={value} disabled={disabled || send === 'sending' || send === 'pending_confirmation'} autoSize={{ minRows: 2, maxRows: 6 }} placeholder={placeholder} onChange={onChange} onPressEnter={event => { if (!event.shiftKey && !event.nativeEvent.isComposing) { event.preventDefault(); if (!disabled && (value.trim() || hasAttachments)) onSend(candidates) } }} />
    <div className="message-composer-footer">
      {tools && <div className="message-composer-tools">{tools}</div>}
      <div className="message-composer-actions">
      <Button type="primary" aria-label="发送" title="发送" icon={<SendOutlined />} loading={send === 'sending'} disabled={disabled || send === 'pending_confirmation' || (!value.trim() && !hasAttachments)} onClick={() => onSend(candidates)}>发送</Button>
      </div>
    </div>
  </div>
}
