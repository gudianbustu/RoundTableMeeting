import { MessageBody } from './MessageBody'
import { useEffect, useState } from 'react'
import { Button, Popover, Tag } from 'antd'
import { ExclamationCircleFilled, LoadingOutlined } from '@ant-design/icons'
import type { ChatAttachment, FailedSubmission, SendState } from '../types/realtimeChat'

export function PendingMessage({ submission, status, attachments, readOnly, onRetry }: { submission: FailedSubmission; status: SendState; attachments: ChatAttachment[]; readOnly: boolean; onRetry: () => Promise<void> }) {
  const [open, setOpen] = useState(false)
  const [busy, setBusy] = useState(false)
  useEffect(() => { setOpen(false) }, [status])
  const pending = status === 'pending_confirmation'
  const error = pending ? '暂时无法确认这条消息是否发送成功，请先核对结果。'
    : submission.errorCode === 'message_send_rate_limited' && submission.retryAfterSeconds ? `发送过于频繁，请在 ${submission.retryAfterSeconds} 秒后重试。`
    : submission.errorCode === 'recipient_unavailable' ? '对方当前不能接收私聊，可能已退出会议。'
    : submission.errorCode === 'reply_scope_mismatch' ? '回复对象与交流范围不一致，请取消回复后重新选择。'
    : submission.errorCode === 'membership_left' ? '已退出会议，不能发送新消息。'
    : submission.errorCode === 'local_attachment_missing' ? '此旧版附件未保留本地文件，请重新选择后发送。'
    : submission.uploadFailed ? '附件上传未完成，消息尚未发送。可重试，已上传成功的附件不会重复上传。'
    : submission.errorCode === 'submission_not_saved' ? '这条消息尚未发送成功，可以重试。'
    : '消息发送被拒绝，请检查内容后重试。'
  const retry = async () => { if (busy) return; setBusy(true); try { await onRetry() } finally { setBusy(false) } }
  return <article className="message-item message-pending" data-submission-id={submission.clientSubmissionId} aria-label="待发送消息">
    {submission.uploadProgress && <div role="status">{submission.uploadProgress}</div>}
    <header className="message-meta"><strong>我</strong>{submission.visibility === 'private' && <Tag color="purple">私聊</Tag>}</header>
    <div className="pending-message-row">
      <div className="pending-message-status">
        {status === 'sending' ? <span role="status" aria-label={submission.uploadProgress ?? "发送中"}><LoadingOutlined spin /></span> : <Popover trigger="click" open={open} onOpenChange={setOpen} content={<div className="pending-message-details"><p>{error}</p><Button size="small" loading={busy} onClick={() => void retry()}>{pending || readOnly ? '核对发送结果' : '重试发送'}</Button></div>}>
          <Button type="text" danger={!pending} aria-label={pending ? '发送结果待确认' : '发送失败，查看原因或重试'} icon={pending ? undefined : <ExclamationCircleFilled />} size="small">{pending ? '待确认' : null}</Button>
        </Popover>}
      </div>
      <div className="pending-message-body">
        {submission.content && <MessageBody content={submission.content} mentions={submission.candidates} />}
        {(submission.attachmentIds ?? []).map(id => <div className="pending-message-file" key={id}>{attachments.find(file => file.attachmentId === id)?.name ?? '附件'}</div>)}
      </div>
    </div>
  </article>
}
