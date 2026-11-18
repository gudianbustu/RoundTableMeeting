import { ArrowLeftOutlined, CopyOutlined, RobotOutlined, UserOutlined } from '@ant-design/icons'
import { Button, Flex, Input, message, Modal, Space, Spin, Typography } from 'antd'
import { useEffect, useState } from 'react'
import { copyText } from '@/shared/utils/copyText'
import { buildAgentInvitePrompt } from '../invitePrompts'
import type { MeetingInvitation } from '../types/meetingWorkspace'

type InviteStep = 'select' | 'human' | 'agent'
const stepTitles: Record<InviteStep, string> = { select: '邀请入会', human: '邀请人类成员', agent: '邀请 AI Agent' }

export function InviteMeetingDialog({ open, invitation, loading, onCancel, siteUrl = window.location.origin, mcpUrl = `${window.location.origin}/mcp` }: { open: boolean; invitation?: MeetingInvitation; loading?: boolean; onCancel: () => void; siteUrl?: string; mcpUrl?: string }) {
  const [step, setStep] = useState<InviteStep>('select')
  const [invitationKey, setInvitationKey] = useState('')
  const [agentName, setAgentName] = useState('')
  const [messageApi, contextHolder] = message.useMessage()
  useEffect(() => { if (open) { setStep('select'); setAgentName(''); setInvitationKey(Array.from(crypto.getRandomValues(new Uint8Array(16)), byte => byte.toString(16).padStart(2, '0')).join('')) } }, [open])
  const meetingCode = invitation?.meetingCode ?? ''
  const prompt = buildAgentInvitePrompt(agentName.trim(), meetingCode, mcpUrl, invitationKey)
  const copyValue = async (value: string, success: string) => {
    try {
      await copyText(value)
      messageApi.success(success)
    } catch { messageApi.error('复制失败，请手动选择并复制') }
  }
  return <Modal open={open} width={560} footer={null} onCancel={onCancel} title={step === 'select' ? stepTitles.select : <Space><Button aria-label="返回邀请方式选择" title="返回邀请方式选择" icon={<ArrowLeftOutlined />} size="small" onClick={() => setStep('select')} />{stepTitles[step]}</Space>}>
    {contextHolder}
    {!invitation ? <Flex vertical align="center" gap="small" style={{ padding: '24px 0' }}>{loading ? <><Spin /><Typography.Text type="secondary">正在获取会议码...</Typography.Text></> : <Typography.Text type="secondary">会议码获取失败，请关闭后重试，失败详情见页面顶部提示。</Typography.Text>}</Flex>
      : step === 'select' ? <Flex vertical gap="middle">
        <Button aria-label="邀请人类成员" icon={<UserOutlined />} onClick={() => setStep('human')} style={{ height: 'auto', padding: '12px 16px', justifyContent: 'flex-start', textAlign: 'left' }}>
          <Flex vertical gap={4}><Typography.Text strong>人类成员</Typography.Text><Typography.Text type="secondary">展示网站地址与会议码，由对方自助加入。</Typography.Text></Flex>
        </Button>
        <Button aria-label="邀请 AI Agent" icon={<RobotOutlined />} onClick={() => setStep('agent')} style={{ height: 'auto', padding: '12px 16px', justifyContent: 'flex-start', textAlign: 'left' }}>
          <Flex vertical gap={4}><Typography.Text strong>AI Agent</Typography.Text><Typography.Text type="secondary">输入 Agent 名称，生成 MCP 接入提示词并复制。</Typography.Text></Flex>
        </Button>
      </Flex>
      : step === 'human' ? <Flex vertical gap="middle">
        <Typography.Text type="secondary">请对方打开网站，使用会议码加入会议；点击右侧图标复制。</Typography.Text>
        <Typography.Paragraph style={{ marginBottom: 0 }}>网站地址：<Typography.Text code>{siteUrl}</Typography.Text><Button type="text" size="small" aria-label="复制网站地址" title="复制网站地址" icon={<CopyOutlined />} onClick={() => void copyValue(siteUrl, '网站地址已复制')} /></Typography.Paragraph>
        <Typography.Paragraph style={{ marginBottom: 0 }}>会议码：<Typography.Text code>{meetingCode}</Typography.Text><Button type="text" size="small" aria-label="复制会议码" title="复制会议码" icon={<CopyOutlined />} onClick={() => void copyValue(meetingCode, '会议码已复制')} /></Typography.Paragraph>
      </Flex>
      : <Flex vertical gap="middle">
        <Input aria-label="Agent 显示名称" placeholder="输入 Agent 显示名称" value={agentName} maxLength={64} allowClear onChange={event => setAgentName(event.target.value)} />
        {agentName.trim() ? <Typography.Paragraph code style={{ marginBottom: 0, whiteSpace: 'pre-wrap', overflowWrap: 'anywhere' }}>{prompt}</Typography.Paragraph> : <Typography.Text type="secondary">输入名称后将生成 MCP 接入提示词，复制后发送给 Agent 完成入会。</Typography.Text>}
        <Flex justify="flex-end"><Button type="primary" aria-label="复制提示词" title="复制提示词" icon={<CopyOutlined />} disabled={!agentName.trim()} onClick={() => void copyValue(prompt, '提示词已复制')}>复制提示词</Button></Flex>
      </Flex>}
  </Modal>
}
