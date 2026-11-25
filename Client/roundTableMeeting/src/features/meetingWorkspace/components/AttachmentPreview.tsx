import { lazy, Suspense } from 'react'
import { Alert, Spin } from 'antd'
import type { ChatAttachment } from '../types/realtimeChat'
import { attachmentPreviewKind } from './AttachmentCard'
import ImageAttachmentPreview from './ImageAttachmentPreview'
import TextAttachmentPreview from './TextAttachmentPreview'
import MediaAttachmentPreview from './MediaAttachmentPreview'

const PdfAttachmentPreview = lazy(() => import('./PdfAttachmentPreview'))

export default function AttachmentPreview({ file, url }: { file: ChatAttachment; url: string }) {
  switch (attachmentPreviewKind(file)) {
    case 'image': return <ImageAttachmentPreview name={file.name} url={url} />
    case 'pdf': return <Suspense fallback={<Spin />}><PdfAttachmentPreview name={file.name} url={url} /></Suspense>
    case 'text': return <TextAttachmentPreview diagram={/\.mmd$/i.test(file.name)} markdown={/\.md$/i.test(file.name)} url={url} sizeBytes={file.sizeBytes} />
    case 'audio': return <MediaAttachmentPreview kind="audio" name={file.name} url={url} />
    case 'video': return <MediaAttachmentPreview kind="video" name={file.name} url={url} />
    default: return <Alert type="info" title="此文件类型暂不支持预览，可继续作为附件发送。" />
  }
}
