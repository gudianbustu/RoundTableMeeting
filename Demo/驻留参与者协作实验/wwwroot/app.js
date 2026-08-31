/** 用于渲染实验实时状态的 DOM 引用 */
const messagesElement = document.querySelector('#messages')
const inviteCodeElement = document.querySelector('#inviteCode')
const participantElement = document.querySelector('#participant')
const statusElement = document.querySelector('#status')
const feedbackElement = document.querySelector('#feedback')
const contentElement = document.querySelector('#content')

/** 上次渲染的消息序号，用于避免重建未变化的列表 */
let lastMessageSequence = 0

/** 发送 JSON 请求并返回解析后的响应体 */
async function request(url, options) {
  const response = await fetch(url, {
    ...options,
    headers: { 'Content-Type': 'application/json', ...options?.headers },
  })
  const body = await response.json()
  if (!response.ok) throw new Error(body.message ?? '请求失败')
  return body
}

/** 构建消息元素并转义用户提供的内容 */
function renderMessage(message) {
  const article = document.createElement('article')
  article.className = `message ${message.role}`
  const author = message.role === 'user' ? '用户' : message.role === 'agent' ? message.author : '系统'
  article.innerHTML = `<div><strong>${escapeHtml(author ?? '')}</strong><time>${new Date(message.createdAt).toLocaleTimeString()}</time></div><p>${escapeHtml(message.content)}</p>`
  return article
}

/** 通过浏览器 DOM 转义文本，避免插入原始 HTML */
function escapeHtml(value) {
  const node = document.createElement('div')
  node.textContent = value
  return node.innerHTML
}

/** 刷新会话状态，并仅在序号变化时重新渲染消息 */
async function refresh() {
  try {
    const state = await request('/api/lab')
    inviteCodeElement.textContent = state.inviteCode
    participantElement.textContent = state.participant?.agentName ?? '等待加入'
    statusElement.textContent = state.participant ? 'Codex 已入会' : '等待 Codex'
    if (state.participant) {
      contentElement.placeholder = `@${state.participant.agentName} 你好，请用一句话说明你收到了这条消息。`
    }
    if (state.messages.length === 0) {
      messagesElement.innerHTML = '<div class="empty">等待 Codex 通过 MCP 加入会话</div>'
      lastMessageSequence = 0
      return
    }
    if (state.messages.at(-1).sequence !== lastMessageSequence) {
      messagesElement.replaceChildren(...state.messages.map(renderMessage))
      lastMessageSequence = state.messages.at(-1).sequence
      messagesElement.scrollTop = messagesElement.scrollHeight
    }
  } catch (error) {
    statusElement.textContent = '服务不可用'
    feedbackElement.textContent = error.message
  }
}

document.querySelector('#composer').addEventListener('submit', async (event) => {
  event.preventDefault()
  const content = contentElement.value.trim()
  if (!content) return
  try {
    const result = await request('/api/lab/messages', { method: 'POST', body: JSON.stringify({ content }) })
    contentElement.value = ''
    feedbackElement.textContent = result.message
    await refresh()
  } catch (error) {
    feedbackElement.textContent = error.message
  }
})

document.querySelector('#reset').addEventListener('click', async () => {
  await request('/api/lab/reset', { method: 'POST', body: '{}' })
  lastMessageSequence = 0
  feedbackElement.textContent = '实验已重置，需要让 Codex 使用新的入会码重新加入。'
  await refresh()
})

await refresh()
setInterval(refresh, 1000)
