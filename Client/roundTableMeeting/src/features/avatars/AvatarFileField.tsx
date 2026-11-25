import { Alert, Button, Flex, Upload } from 'antd'
import { UploadOutlined } from '@ant-design/icons'
import { useEffect, useState, type ReactNode } from 'react'

export function AvatarFileField({ file, onChange, disabled, children, previewName }: { file?: File; onChange: (file?: File) => void; disabled: boolean; children?: ReactNode; previewName?: string }) {
  const [error, setError] = useState('')
  const [preview, setPreview] = useState<string>()
  useEffect(() => {
    if (!file) { setPreview(undefined); return }
    const url = URL.createObjectURL(file)
    setPreview(url)
    return () => URL.revokeObjectURL(url)
  }, [file])
  return <><Flex gap="middle" align="center" wrap>
    {file && <><span className="rt-avatar-preview"><img className="rt-avatar-image" src={preview} alt="所选图片预览" /></span>
      <span className="rt-avatar-filename">{file.name}</span></>}
    <Upload accept="image/png,image/jpeg,image/webp" maxCount={1} showUploadList={false} disabled={disabled}
      beforeUpload={next => {
        const allowed = next.type ? ['image/png', 'image/jpeg', 'image/webp'].includes(next.type) : /\.(png|jpe?g|webp)$/i.test(next.name)
        const issue = !allowed ? '请选择 PNG、JPEG 或 WebP 图片。' : next.size === 0 ? '图片文件为空。' : next.size > 2 * 1024 * 1024 ? '头像图片不能超过 2 MiB。' : ''
        setError(issue)
        onChange(issue ? undefined : next)
        return false
      }}>
      <Button disabled={disabled} icon={<UploadOutlined />}>{file ? '重新选择图片' : '选择图片'}</Button>
    </Upload>
    {error && <Alert type="error" title={error} />}
  </Flex>
    {children}
    {file && preview && previewName !== undefined && <div className="rt-avatar-chat-preview">
      <small>聊天头像预览</small><Flex align="center" gap="small">
        <img src={preview} width={32} height={32} alt="" /><span>{previewName.trim() || file.name}</span>
      </Flex>
    </div>}
  </>
}
