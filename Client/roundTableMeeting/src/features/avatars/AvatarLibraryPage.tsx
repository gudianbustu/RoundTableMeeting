import { useCallback, useEffect, useState } from 'react'
import { Alert, Button, Empty, Flex, Input, Modal, Popconfirm, Select, Spin } from 'antd'
import { Link } from 'react-router-dom'
import { AvatarGrid } from './AvatarGrid'
import { AvatarFileField } from './AvatarFileField'
import { uploadPublicAvatar } from './avatarApi'
import { notifyUnauthorized } from '@/api/client/authenticationEvents'
import type { AvatarChoice } from './avatar'

type Group = { name: string; items: AvatarChoice[] }
type Action = 'createGroup' | 'renameGroup' | 'deleteGroup' | 'moveGroup' | 'moveAvatar' | 'deleteAvatar'
type Dialog = 'createGroup' | 'renameGroup' | 'upload'
const titles = { createGroup: '新增分组', renameGroup: '修改分组名称', upload: '上传头像' }

async function request(init?: RequestInit) {
  const response = await fetch('/api/avatars/groups', { cache: 'no-store', ...init })
  if (response.status === 401) notifyUnauthorized()
  if (!response.ok) {
    const detail = await response.json().catch(() => ({}))
    throw new Error(detail.message ?? '操作失败，请重试。')
  }
  return response
}

export function AvatarLibraryPage() {
  const [groups, setGroups] = useState<Group[]>([])
  const [active, setActive] = useState('')
  const [selected, setSelected] = useState('')
  const [dialog, setDialog] = useState<Dialog>()
  const [name, setName] = useState('')
  const [file, setFile] = useState<File>()
  const [busy, setBusy] = useState(false)
  const [loading, setLoading] = useState(true)
  const [error, setError] = useState('')
  const load = useCallback(async (signal?: AbortSignal) => {
    const response = await request({ signal })
    const catalog: Group[] = await response.json()
    if (!signal?.aborted) {
      setGroups(catalog)
      setActive(current => catalog.some(g => g.name === current) ? current : catalog[0]?.name ?? '')
    }
  }, [])
  useEffect(() => {
    const controller = new AbortController()
    void load(controller.signal).catch(() => {
      if (!controller.signal.aborted) setError('头像库加载失败。')
    }).finally(() => { if (!controller.signal.aborted) setLoading(false) })
    return () => controller.abort()
  }, [load])

  const group = groups.find(g => g.name === active) ?? groups[0]
  const entry = group?.items.find(a => a.id === selected)
  const groupIndex = groups.indexOf(group)
  const itemIndex = entry ? group.items.indexOf(entry) : -1
  function open(kind: Dialog) {
    setName(kind === 'renameGroup' ? group.name : '')
    setFile(undefined); setError(''); setDialog(kind)
  }
  async function run(operation: () => Promise<unknown>) {
    if (busy) return
    setBusy(true); setError('')
    try {
      await operation()
      setDialog(undefined)
      await load()
    } catch (cause) { setError(cause instanceof Error ? cause.message : '操作失败，请重试。') }
    finally { setBusy(false) }
  }
  async function edit(action: Action, direction = 0) {
    const groupName = action === 'createGroup' ? name.trim() : group.name
    const itemName = entry && decodeURIComponent(entry.imageUrl.split('/').pop()!)
    await request({ method: 'POST', headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ action, group: groupName, name: action === 'renameGroup' ? name.trim() : itemName, direction }) })
    if (action === 'createGroup' || action === 'renameGroup') setActive(name.trim())
  }
  const feedback = error && <Alert type="error" title={error} />
  return <main className="rt-avatar-library">
    <Link to="/settings/profile">返回个人设置</Link>
    <header><h1>公共头像库</h1><Button disabled={busy || loading} onClick={() => open('createGroup')}>新增分组</Button></header>
    {!dialog && feedback}
    {loading ? <Spin aria-label="正在加载头像" /> : !group ? <Empty description="暂无头像分组" /> : <>
      <Flex align="center" wrap gap="small" className="rt-avatar-tools">
        <Select aria-label="头像分组" value={group.name} disabled={busy} style={{ minWidth: 160 }}
          options={groups.map(g => ({ value: g.name, label: g.name }))} onChange={value => { setActive(value); setSelected('') }} />
        <Button disabled={busy} onClick={() => open('renameGroup')}>修改组名</Button>
        <Button disabled={busy || groupIndex === 0} onClick={() => void run(() => edit('moveGroup', -1))}>分组前移</Button>
        <Button disabled={busy || groupIndex === groups.length - 1} onClick={() => void run(() => edit('moveGroup', 1))}>分组后移</Button>
        <Popconfirm title={`从公共库移除「${group.name}」及组内头像？`} okText="删除" cancelText="取消" onConfirm={() => run(() => edit('deleteGroup'))}>
          <Button danger disabled={busy}>删除分组</Button>
        </Popconfirm>
      </Flex>
      <Flex align="center" wrap gap="small" className="rt-avatar-tools">
        <Button type="primary" disabled={busy} onClick={() => open('upload')}>上传头像</Button>
        <Button disabled={busy || itemIndex <= 0} onClick={() => void run(() => edit('moveAvatar', -1))}>头像前移</Button>
        <Button disabled={busy || !entry || itemIndex === group.items.length - 1} onClick={() => void run(() => edit('moveAvatar', 1))}>头像后移</Button>
        <Popconfirm title={`从公共库移除「${entry?.name}」？`} okText="删除" cancelText="取消" onConfirm={() => run(() => edit('deleteAvatar'))}>
          <Button danger disabled={busy || !entry}>删除头像</Button>
        </Popconfirm>
      </Flex>
      {group.items.length ? <AvatarGrid items={group.items} selected={selected} onSelect={setSelected} disabled={busy} /> : <Empty description="该分组暂无头像" />}
    </>}
    <Modal open={!!dialog} title={dialog && titles[dialog]} onCancel={() => { if (!busy) setDialog(undefined) }}
      okText={dialog === 'upload' ? '上传' : '保存'} cancelText="取消" confirmLoading={busy}
      okButtonProps={{ disabled: !name.trim() || (dialog === 'upload' && !file) }}
      onOk={() => { if (dialog) void run(() => dialog === 'upload' ? uploadPublicAvatar(name.trim(), file!, group.name) : edit(dialog)) }}>
      <div className="rt-avatar-form">
        {dialog === 'upload' && <AvatarFileField file={file} disabled={busy} previewName={name} onChange={next => {
          setFile(next); if (next && !name.trim()) setName(next.name.replace(/\.[^.]+$/, '').slice(0, 64))
        }} />}
        <label>{dialog === 'upload' ? '头像名称' : '分组名称'}<Input value={name} disabled={busy} maxLength={64} onChange={e => setName(e.target.value)} /></label>
        {feedback}
      </div>
    </Modal>
  </main>
}
