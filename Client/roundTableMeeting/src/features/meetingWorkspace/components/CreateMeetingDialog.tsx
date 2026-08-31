import { Form, Input, Modal } from 'antd'
import { useEffect, useRef } from 'react'
import type { CreateMeetingInput } from '../types/meetingWorkspace'

export function CreateMeetingDialog({ open, loading, onCancel, onSubmit }: { open: boolean; loading?: boolean; onCancel: () => void; onSubmit: (value: CreateMeetingInput) => void }) {
  const [form] = Form.useForm<{ name: string; description?: string }>()
  const requestId = useRef<string | undefined>(undefined)
  useEffect(() => {
    if (open && !requestId.current) { requestId.current = crypto.randomUUID(); form.resetFields() }
    if (!open) requestId.current = undefined
  }, [form, open])
  const cancel = () => { requestId.current = undefined; form.resetFields(); onCancel() }
  const submit = async () => { const values = await form.validateFields(); onSubmit({ ...values, creationRequestId: requestId.current ?? crypto.randomUUID() }) }
  return <Modal open={open} title="创建会议" okText="创建" cancelText="取消" confirmLoading={loading} onCancel={cancel} onOk={() => void submit()}>
    <Form form={form} layout="vertical"><Form.Item name="name" label="会议名称" rules={[{ required: true, message: '请输入会议名称' }]}><Input maxLength={128} /></Form.Item><Form.Item name="description" label="讨论主题或背景"><Input.TextArea maxLength={2000} rows={4} /></Form.Item></Form>
  </Modal>
}
