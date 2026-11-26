import { act, cleanup, fireEvent, render, screen } from '@testing-library/react'
import { afterEach, expect, it, vi } from 'vitest'
import { VoiceRecorder } from './VoiceRecorder'

const stopTrack = vi.fn()
const stream = { getTracks: () => [{ stop: stopTrack }] }
class FakeRecorder {
  state = 'inactive'
  mimeType = 'audio/webm'
  onstop?: () => void
  ondataavailable?: (event: { data: Blob }) => void
  start() { this.state = 'recording' }
  stop() {
    this.state = 'inactive'
    this.ondataavailable?.({ data: new Blob(['clip'], { type: this.mimeType }) })
    this.onstop?.()
  }
}
function setup(request = vi.fn().mockResolvedValue(stream)) {
  vi.stubGlobal('MediaRecorder', FakeRecorder)
  Object.defineProperty(navigator, 'mediaDevices', { configurable: true, value: { getUserMedia: request } })
  const onFile = vi.fn()
  return { ...render(<VoiceRecorder disabled={false} onFile={onFile} />), onFile }
}
afterEach(() => { cleanup(); vi.unstubAllGlobals(); vi.clearAllMocks(); Reflect.deleteProperty(navigator, 'mediaDevices') })

it('adds each finished recording separately and drops a canceled recording', async () => {
  const { onFile } = setup()
  for (const action of ['完成', '取消', '完成']) {
    fireEvent.click(screen.getByRole('button', { name: '录音' }))
    await screen.findByRole('button', { name: '完成' })
    fireEvent.click(screen.getByRole('button', { name: action }))
  }
  expect(onFile).toHaveBeenCalledTimes(2)
  expect(onFile.mock.calls[0][0]).toMatchObject({ type: 'audio/webm', size: 4 })
  expect(stopTrack).toHaveBeenCalledTimes(3)
})
it('releases a permission grant received after the conversation unmounts', async () => {
  let grant!: (value: typeof stream) => void
  const { unmount, onFile } = setup(vi.fn(() => new Promise(resolve => { grant = resolve })))
  fireEvent.click(screen.getByRole('button', { name: '录音' }))
  unmount()
  await act(async () => grant(stream))
  expect(stopTrack).toHaveBeenCalledOnce()
  expect(onFile).not.toHaveBeenCalled()
})
it('allows a new attempt after permission denial', async () => {
  setup(vi.fn().mockRejectedValueOnce(new Error('denied')).mockResolvedValue(stream))
  fireEvent.click(screen.getByRole('button', { name: '录音' }))
  expect(await screen.findByRole('alert')).toHaveTextContent('麦克风')
  fireEvent.click(screen.getByRole('button', { name: '录音' }))
  expect(await screen.findByRole('button', { name: '完成' })).toBeEnabled()
})
