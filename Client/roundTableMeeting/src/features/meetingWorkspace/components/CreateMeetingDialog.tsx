import { Form, Input, Modal, Radio } from 'antd'
import { useEffect, useRef } from 'react'
import { createUuid } from '@/shared/utils/createUuid'
import type { CreateMeetingInput } from '../types/meetingWorkspace'

export function CreateMeetingDialog({ open, loading, onCancel, onSubmit }: { open: boolean; loading?: boolean; onCancel: () => void; onSubmit: (value: CreateMeetingInput) => void }) {
  const [form] = Form.useForm<Omit<CreateMeetingInput, 'creationRequestId'>>()
  const requestId = useRef<string | undefined>(undefined)
  useEffect(() => {
    if (open && !requestId.current) { requestId.current = createUuid(); form.resetFields() }
    if (!open) requestId.current = undefined
  }, [form, open])
  const cancel = () => { requestId.current = undefined; form.resetFields(); onCancel() }
  const submit = async () => { const values = await form.validateFields(); onSubmit({ ...values, creationRequestId: requestId.current ?? createUuid() }) }
  return <Modal width={600} open={open} title="创建会议" okText="创建会议" centered className="meeting-form-modal" cancelText="取消" confirmLoading={loading} onCancel={cancel} onOk={() => void submit()}>
    <Form form={form} layout="vertical" initialValues={{ initialDiscussionMode: 'directed' }}><Form.Item name="name" label="会议名称" rules={[{ required: true, message: '请输入会议名称' }]}><Input maxLength={128} /></Form.Item><Form.Item name="description" label={<>讨论主题或背景 <small className="field-optional">选填</small></>}><Input.TextArea maxLength={2000} rows={2} /></Form.Item>
      <Form.Item name="initialDiscussionMode" label="初始讨论模式">
        <Radio.Group className="discussion-mode-options">
          <Radio value="directed"><strong>点名讨论</strong><small>通过 @ 邀请成员回应</small></Radio>
          <Radio value="free"><strong>自由讨论</strong><small>围绕话题自由交流</small></Radio>
        </Radio.Group>
      </Form.Item>
    </Form>
  </Modal>
}
