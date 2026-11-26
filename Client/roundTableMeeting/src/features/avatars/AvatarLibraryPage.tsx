import { useCallback, useEffect, useRef, useState } from 'react'
import { Alert, Button, Empty, Input, Modal, Spin } from 'antd'
import { PlusOutlined } from '@ant-design/icons'
import { AvatarFileField } from './AvatarFileField'
import { LibraryGroupCard, SortableList, type AvatarGroup, type LibraryDialog } from './AvatarLibrarySort'
import { uploadPublicAvatar } from './avatarApi'
import { notifyUnauthorized } from '@/api/client/authenticationEvents'

const titles = {
  createGroup: '新增分组', renameGroup: '编辑分组', deleteGroup: '删除分组',
  upload: '上传头像', renameAvatar: '修改头像文件名', deleteAvatar: '删除头像',
}
async function request(init?: RequestInit) {
  const response = await fetch('/api/avatars/groups', { cache: 'no-store', ...init })
  if (response.status === 401) notifyUnauthorized()
  if (!response.ok) {
    const detail = await response.json().catch(() => ({}))
    throw new Error(detail.message ?? '操作失败，请重试。')
  }
  return response
}
type Edit = { action: string; group: string; name?: string; target?: string; after?: boolean }
function edit(body: Edit) {
  return request({ method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(body) })
}
const filename = (url: string) => decodeURIComponent(url.slice(url.lastIndexOf('/') + 1))

export function AvatarLibraryPage() {
  const [groups, setGroups] = useState<AvatarGroup[]>([])
  const [selected, setSelected] = useState('')
  const [dialog, setDialog] = useState<LibraryDialog>()
  const [name, setName] = useState('')
  const [file, setFile] = useState<File>()
  const [busy, setBusy] = useState(false)
  const pending = useRef(false)
  const [loading, setLoading] = useState(true)
  const [error, setError] = useState('')
  const load = useCallback(async (signal?: AbortSignal) => {
    const response = await request({ signal })
    const catalog: AvatarGroup[] = await response.json()
    if (!signal?.aborted) setGroups(catalog)
    return catalog
  }, [])
  useEffect(() => {
    const controller = new AbortController()
    void load(controller.signal).catch(() => {
      if (!controller.signal.aborted) setError('头像库加载失败。')
    }).finally(() => { if (!controller.signal.aborted) setLoading(false) })
    return () => controller.abort()
  }, [load])

  function open(next: LibraryDialog) {
    setName(next.kind === 'renameGroup' ? next.group.name : next.item?.name ?? '')
    setFile(undefined); setError(''); setDialog(next)
  }
  async function run(operation: () => Promise<unknown>, nextSelection?: (catalog: AvatarGroup[]) => string) {
    if (pending.current) return
    pending.current = true; setBusy(true); setError('')
    try {
      await operation()
      setDialog(undefined)
      const catalog = await load()
      if (nextSelection) setSelected(nextSelection(catalog))
      else setSelected(current => catalog.some(g => g.items.some(i => i.id === current)) ? current : '')
    } catch (cause) { setError(cause instanceof Error ? cause.message : '操作失败，请重试。') }
    finally { pending.current = false; setBusy(false) }
  }
  function submit() {
    if (!dialog) return
    const value = name.trim()
    const { kind, group, item } = dialog
    if (kind === 'upload') {
      if (file) void run(() => uploadPublicAvatar(value, file, group.name))
      return
    }
    if (kind === 'createGroup') { void run(() => edit({ action: kind, group: value })); return }
    if (kind === 'renameAvatar' && item) {
      const old = filename(item.imageUrl)
      const next = value + old.slice(old.lastIndexOf('.'))
      void run(() => edit({ action: kind, group: group.name, name: old, target: value }),
        catalog => catalog.find(g => g.name === group.name)?.items.find(i => filename(i.imageUrl) === next)?.id ?? '')
      return
    }
    const selectedItem = group.items.find(i => i.id === selected)
    void run(() => edit({ action: kind, group: group.name, name: kind === 'renameGroup' ? value : item && filename(item.imageUrl) }),
      kind === 'renameGroup' && selectedItem
        ? catalog => catalog.find(g => g.name === value)?.items.find(i => filename(i.imageUrl) === filename(selectedItem.imageUrl))?.id ?? ''
        : undefined)
  }

  const deleting = dialog?.kind === 'deleteGroup' || dialog?.kind === 'deleteAvatar'
  const feedback = error && <Alert type="error" title={error} />
  return <div className="rt-avatar-library">
    <header className="library-heading"><div><h1>公共头像库</h1>
      <p>拖动分组标题调整分组顺序，拖动头像调整组内顺序。</p></div>
      <Button type="primary" icon={<PlusOutlined />} disabled={busy || loading} onClick={() => open({ kind: 'createGroup' })}>新增分组</Button>
    </header>
    {!dialog && feedback}
    {loading ? <Spin aria-label="正在加载头像" /> : !groups.length ? <Empty description="暂无头像分组" /> :
      <SortableList groups ids={groups.map(g => g.name)}
        onMove={(group, target, after) => void run(() => edit({ action: 'reorderGroup', group, target, after }))}>
        {groups.map(group => <LibraryGroupCard key={group.name} group={group} selected={selected} select={setSelected} busy={busy} open={open}
          onMove={(from, to, after) => void run(() => edit({ action: 'reorderAvatar', group: group.name, name: filename(from), target: filename(to), after }))} />)}
      </SortableList>}
    <Modal open={!!dialog} title={dialog && titles[dialog.kind]} onCancel={() => { if (!busy) setDialog(undefined) }}
      okText={deleting ? '删除' : dialog?.kind === 'upload' ? '上传' : '保存'} cancelText="取消" confirmLoading={busy}
      okButtonProps={{ danger: deleting, disabled: !deleting && (!name.trim() || (dialog?.kind === 'upload' && !file)) }}
      cancelButtonProps={{ disabled: busy }} onOk={submit}>
      <div className="rt-avatar-form">
        {dialog?.kind === 'deleteGroup' ? <p>确定从公共库删除「{dialog.group.name}」分组及其中的 {dialog.group.items.length} 张头像？</p> :
          dialog?.kind === 'deleteAvatar' ? <p>确定从「{dialog.group.name}」删除头像「{dialog.item?.name}」？</p> : <>
            {dialog?.kind === 'upload' && <AvatarFileField file={file} disabled={busy} previewName={name} onChange={next => {
              setFile(next); if (next && !name.trim()) setName(next.name.replace(/\.[^.]+$/, '').slice(0, 64))
            }} />}
            <label>{dialog?.kind === 'upload' ? '头像名称' : dialog?.kind === 'renameAvatar' ? '文件名' : '分组名称'}
              <Input value={name} disabled={busy} maxLength={dialog?.kind === 'upload' ? 64 : 180}
                suffix={dialog?.kind === 'renameAvatar' && dialog.item ? filename(dialog.item.imageUrl).slice(dialog.item.name.length) : undefined}
                onChange={e => setName(e.target.value)} onPressEnter={() => { if (name.trim() && !busy) submit() }} />
            </label>
          </>}
        {feedback}
      </div>
    </Modal>
  </div>
}
