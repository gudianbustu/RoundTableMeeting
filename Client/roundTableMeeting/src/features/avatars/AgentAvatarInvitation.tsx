import { useState } from 'react'
import { Alert, Button, Flex, Input } from 'antd'
import { CopyOutlined } from '@ant-design/icons'
import { buildAgentInvitePrompt } from '@/features/meetingWorkspace/invitePrompts'
import { AvatarGrid } from './AvatarGrid'
import { AvatarImage } from './AvatarImage'
import { AvatarCatalogContent } from './AvatarCatalogContent'
import { bindAgentAvatar } from './avatarApi'
import { useAvatarCatalog } from './useAvatarCatalog'

export function AgentAvatarInvitation({ meetingId, meetingCode, mcpUrl, onCopy }: {
  meetingId: string; meetingCode: string; mcpUrl: string; onCopy: (text: string) => Promise<void>
}) {
  const catalog = useAvatarCatalog(true)
  const [name, setName] = useState('')
  const [selection, setSelection] = useState('')
  const [prompt, setPrompt] = useState('')
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState('')
  const selected = catalog.items.find(item => item.id === selection)
  async function generate() {
    if (busy || !name.trim() || !selected) return
    setBusy(true); setError('')
    try {
      const key = await bindAgentAvatar(meetingId, name.trim(), selected.id)
      setPrompt(buildAgentInvitePrompt(name.trim(), meetingCode, mcpUrl, key))
    } catch (cause) { setError(cause instanceof Error ? '生成邀请失败。' + cause.message : '生成邀请失败，请重试。') }
    finally { setBusy(false) }
  }
  return <div className="invite-panel rt-avatar-form">
    <label htmlFor="invite-agent-name">Agent 显示名称</label>
    <Input id="invite-agent-name" placeholder="输入 Agent 显示名称" maxLength={64} allowClear disabled={busy}
      value={name} onChange={e => { setName(e.target.value); setPrompt(''); setError('') }} />
    <strong>选择头像</strong>
    <AvatarCatalogContent catalog={catalog}><div className="rt-avatar-scroll">
      <AvatarGrid items={catalog.items} selected={selection} disabled={busy} onSelect={id => { setSelection(id); setPrompt(''); setError('') }} />
    </div></AvatarCatalogContent>
    {selected && <Flex align="center" gap="small"><span className="rt-avatar-preview">
      <AvatarImage src={selected.imageUrl} fallback={Array.from(selected.name)[0]} />
    </span>{name.trim() || selected.name}</Flex>}
    {error && <Alert type="error" title={error} />}
    {prompt ? <><div className="invite-copy agent-prompt" role="region" aria-label="Agent 接入提示词" tabIndex={0}>{prompt}</div>
      <Button block type="primary" icon={<CopyOutlined aria-hidden />} onClick={() => void onCopy(prompt)}>复制 MCP 接入提示词</Button></>
      : <Button block type="primary" disabled={!name.trim() || !selected} loading={busy} onClick={() => void generate()}>生成邀请</Button>}
  </div>
}
