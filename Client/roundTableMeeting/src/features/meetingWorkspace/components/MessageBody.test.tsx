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
