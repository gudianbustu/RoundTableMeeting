import { CopyOutlined } from '@ant-design/icons'
import { Button, Flex, Input, message, Modal, Segmented, Spin, Typography } from 'antd'
import { useEffect, useState } from 'react'
import { copyText } from '@/shared/utils/copyText'
import { buildAgentInvitePrompt } from '../invitePrompts'
import type { MeetingInvitation } from '../types/meetingWorkspace'

export function InviteMeetingDialog({ open, invitation, meetingName, loading, onCancel, siteUrl: siteOverride, mcpUrl: mcpOverride }: { open: boolean; invitation?: MeetingInvitation; meetingName?: string; loading?: boolean; onCancel: () => void; siteUrl?: string; mcpUrl?: string }) {
  const siteUrl = invitation?.publicSiteUrl ?? siteOverride ?? window.location.origin
  const mcpUrl = mcpOverride ?? new URL('mcp', siteUrl.replace(/\/?$/, '/')).href
  const [step, setStep] = useState<'human' | 'agent'>('human')
  const [invitationKey, setInvitationKey] = useState('')
  const [agentName, setAgentName] = useState('')
  const [messageApi, contextHolder] = message.useMessage()
  useEffect(() => { if (open) { setStep('human'); setAgentName(''); setInvitationKey(Array.from(crypto.getRandomValues(new Uint8Array(16)), byte => byte.toString(16).padStart(2, '0')).join('')) } }, [open])
  const meetingCode = invitation?.meetingCode ?? ''
  const prompt = buildAgentInvitePrompt(agentName.trim(), meetingCode, mcpUrl, invitationKey)
  const copyValue = async (value: string, success: string) => {
    try {
      await copyText(value)
      messageApi.success(success)
    } catch { messageApi.error('复制失败，请手动选择并复制') }
  }
  const title = meetingName?.replace(/\s+/g, ' ').trim()
  const humanCopy = [title ? `邀请你加入「${title}」。` : '邀请你加入圆桌会议。', `网站地址：${siteUrl}`, `会议码：${meetingCode}`].join('\n\n')
  return <Modal centered className="meeting-form-modal invite-form-modal" open={open} width={560} footer={null} onCancel={onCancel} title="邀请入会">
    {contextHolder}
    {title && <p>{title}</p>}
    <Segmented block aria-label="邀请对象" value={step} onChange={value => setStep(value as 'human' | 'agent')}
      options={[{ label: '人类成员', value: 'human' }, { label: 'AI Agent', value: 'agent' }]} />
    {!invitation ? <Flex vertical align="center" gap="small" className="invite-panel">{loading ? <><Spin /><Typography.Text>正在获取会议码…</Typography.Text></> : <Typography.Text>会议码获取失败，请关闭后重试。</Typography.Text>}</Flex>
      : step === 'human' ? <div className="invite-panel">
        <strong>邀请文案</strong>
        <div className="invite-copy">{humanCopy}</div>
        <Button block type="primary" icon={<CopyOutlined aria-hidden="true" />} onClick={() => void copyValue(humanCopy, '邀请文案已复制')}>复制完整邀请文案</Button>
      </div> : <div className="invite-panel">
        <label htmlFor="invite-agent-name">Agent 显示名称</label>
        <Input id="invite-agent-name" placeholder="输入 Agent 显示名称" value={agentName} maxLength={64} allowClear onChange={event => setAgentName(event.target.value)} />
        <div className="invite-prompt-title"><strong>接入提示词</strong><small>Streamable HTTP</small></div>
        <div className="invite-copy agent-prompt" role="region" aria-label="Agent 接入提示词" tabIndex={0}>{agentName.trim() ? prompt : '输入名称后生成 MCP 接入提示词。'}</div>
        <Button block type="primary" icon={<CopyOutlined aria-hidden="true" />} disabled={!agentName.trim()} onClick={() => void copyValue(prompt, '提示词已复制')}>复制 MCP 接入提示词</Button>
      </div>}
  </Modal>
}
