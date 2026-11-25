import { render } from '@testing-library/react'
import { expect, it } from 'vitest'
import { MessageBody } from './MessageBody'

it('renders chat Markdown and preserves source offsets for selected mentions', () => {
  const content = ['## 进展', '', '😀 @A_B **@A_B**', '', '- 完成', '', '| 名称 |', '| --- |', '| 示例 |', '', '```js', '**literal**', '```'].join('\r\n')
  const mentions = [{ participantId: '1', displayName: 'A_B', startOffset: content.lastIndexOf('@A_B'), length: 4 }]
  const view = render(<MessageBody content={content} mentions={mentions} />)
  expect(view.getByRole('heading', { name: '进展' })).toBeVisible()
  expect(view.container.querySelector('strong > mark')).toHaveTextContent('@A_B')
  expect(view.container.querySelectorAll('mark')).toHaveLength(1)
  expect(view.getByRole('listitem')).toHaveTextContent('完成')
  expect(view.getByRole('cell')).toHaveTextContent('示例')
  expect(view.container.querySelector('pre code')).toHaveTextContent('**literal**')
})

it('treats raw HTML as text and only renders safe links', () => {
  const content = '<iframe src="about:blank"></iframe>\n\n[打开](https://example.org) [无效](javascript:void) ![示意图](https://example.org/img)'
  const view = render(<MessageBody content={content} mentions={[]} />)
  expect(view.container.querySelector('iframe, img')).toBeNull()
  expect(view.getByText(/<iframe/)).toBeVisible()
  expect(view.getByRole('link', { name: '打开' })).toHaveAttribute('href', 'https://example.org')
  expect(view.queryByRole('link', { name: '无效' })).toBeNull()
})

it('recognizes diagram fences inside chat without changing adjacent Markdown or other code', () => {
  const source = ['**进展**', '~~~mmd', 'graph TD', 'A-->B', '~~~', '~~~ts', 'sequenceDiagram', '~~~'].join('\n')
  const view = render(<MessageBody diagrams content={source} mentions={[]} />)
  expect(view.getAllByRole('button', { name: '查看 Mermaid 图表' })).toHaveLength(1)
  expect(view.container.querySelector('strong')).toHaveTextContent('进展')
  expect(view.container.querySelector('code.language-ts')).toHaveTextContent('sequenceDiagram')
  view.rerender(<MessageBody content={source} mentions={[]} />)
  expect(view.container.querySelector('code.language-mmd')).toBeInTheDocument()
})

it('recognizes a standalone diagram declaration and preserves prose about syntax', () => {
  const view = render(<MessageBody diagrams content={'sequenceDiagram\nAlice->>Bob: Hi'} mentions={[]} />)
  expect(view.getByRole('button', { name: '查看 Mermaid 图表' })).toBeVisible()
  view.rerender(<MessageBody diagrams content="sequenceDiagram describes a conversation." mentions={[]} />)
  expect(view.queryByRole('button')).toBeNull()
})

it('highlights a group once when several members share the same UTF-16 range', () => {
  const content = '😀 @所有Agent 与 @小明'
  const mentions = ['甲', '乙'].map(displayName => ({ participantId: displayName, displayName, startOffset: 3, length: 8 }))
  mentions.push({ participantId: '人', displayName: '小明', startOffset: content.indexOf('@小明'), length: 3 })
  const view = render(<MessageBody content={content} mentions={mentions} />)
  expect(Array.from(view.container.querySelectorAll('mark')).map(mark => mark.textContent)).toEqual(['@所有Agent', '@小明'])
})
