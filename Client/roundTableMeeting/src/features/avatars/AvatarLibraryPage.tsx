import { useState } from 'react'
import { Alert, Button, Input, Modal } from 'antd'
import { PlusOutlined } from '@ant-design/icons'
import { Link } from 'react-router-dom'
import { AvatarGrid } from './AvatarGrid'
import { AvatarFileField } from './AvatarFileField'
import { AvatarCatalogContent } from './AvatarCatalogContent'
import { uploadPublicAvatar } from './avatarApi'
import { useAvatarCatalog } from './useAvatarCatalog'

export function AvatarLibraryPage() {
  const catalog = useAvatarCatalog(true)
  const [open, setOpen] = useState(false)
  const [file, setFile] = useState<File>()
  const [name, setName] = useState('')
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState('')
  async function upload() {
    if (busy || !file || !name.trim()) return
    setBusy(true); setError('')
    try {
      await uploadPublicAvatar(name.trim(), file)
      setOpen(false); catalog.reload()
    } catch (cause) { setError(cause instanceof Error ? '上传失败。' + cause.message : '上传失败，请重试。') }
    finally { setBusy(false) }
  }
  return <main className="rt-avatar-library">
    <Link to="/settings/profile">返回个人设置</Link>
    <header><h1>公共头像库</h1><Button type="primary" icon={<PlusOutlined />} onClick={() => {
      setFile(undefined); setName(''); setError(''); setOpen(true)
    }}>上传头像</Button></header>
    <AvatarCatalogContent catalog={catalog}><AvatarGrid items={catalog.items} label="公共头像库" /></AvatarCatalogContent>
    <Modal cancelText="取消" title="上传公共头像" open={open} onCancel={() => { if (!busy) setOpen(false) }} onOk={() => void upload()}
      confirmLoading={busy} okText="上传" okButtonProps={{ disabled: !file || !name.trim() }} cancelButtonProps={{ disabled: busy }}>
      <div className="rt-avatar-form"><AvatarFileField previewName={name} file={file} disabled={busy} onChange={next => {
        setFile(next); setError(''); if (next && !name.trim()) setName(next.name.replace(/\.[^.]+$/, '').slice(0, 64))
      }}><label>头像名称<Input maxLength={64} value={name} disabled={busy} onChange={e => setName(e.target.value)} /></label></AvatarFileField>
      {error && <Alert type="error" title={error} />}</div>
    </Modal>
  </main>
}
