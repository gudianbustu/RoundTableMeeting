import { useState } from 'react'
import { Alert, Modal, Segmented } from 'antd'
import { AvatarCatalogContent } from './AvatarCatalogContent'
import { AvatarFileField } from './AvatarFileField'
import { AvatarGrid } from './AvatarGrid'
import { selectPersonalAvatar, uploadPersonalAvatar } from './avatarApi'
import { useAvatarCatalog } from './useAvatarCatalog'

export function PersonalAvatarDialog({ currentUrl, onClose, onSaved }: {
  currentUrl?: string; onClose: () => void; onSaved: () => void
}) {
  const [tab, setTab] = useState('library')
  const [id, setId] = useState<string>()
  const [file, setFile] = useState<File>()
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState('')
  const catalog = useAvatarCatalog(true)
  const selected = id ?? catalog.items.find(item => item.imageUrl === currentUrl)?.id
  async function save() {
    if (busy) return
    setBusy(true); setError('')
    try {
      if (tab === 'library' && selected) await selectPersonalAvatar(selected)
      else if (tab === 'upload' && file) await uploadPersonalAvatar(file)
      else return
      onSaved()
    } catch (cause) { setError(cause instanceof Error ? '头像保存失败。' + cause.message : '头像保存失败，请重试。') }
    finally { setBusy(false) }
  }
  return <Modal cancelText="取消" title="更换个人头像" open onCancel={() => { if (!busy) onClose() }} onOk={() => void save()} okText="保存头像"
    confirmLoading={busy} cancelButtonProps={{ disabled: busy }} okButtonProps={{ disabled: tab === 'library' ? !selected : !file }}>
    <div className="rt-avatar-form">
      <Segmented block disabled={busy} value={tab} options={[{ value: 'library', label: '从头像库选择' }, { value: 'upload', label: '上传自己的图片' }]}
        onChange={value => { setTab(value); setError('') }} />
      {tab === 'library' ? <AvatarCatalogContent catalog={catalog}><div className="rt-avatar-scroll">
        <AvatarGrid items={catalog.items} selected={selected} disabled={busy} onSelect={setId} /></div></AvatarCatalogContent>
        : <><AvatarFileField file={file} disabled={busy} onChange={setFile} /><small>上传的图片仅用于你的账号。</small></>}
      {error && <Alert type="error" title={error} />}
    </div>
  </Modal>
}
