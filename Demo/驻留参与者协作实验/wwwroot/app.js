const messagesElement = document.querySelector('#messages')
const inviteCodeElement = document.querySelector('#inviteCode')
const participantElement = document.querySelector('#participant')
const statusElement = document.querySelector('#status')
const feedbackElement = document.querySelector('#feedback')
const contentElement = document.querySelector('#content')

let lastMessageSequence = 0

async function request(url, options) {
  const response = await fetch(url, {
    ...options,
    headers: { 'Content-Type': 'application/json', ...options?.headers },
  })
  const body = await response.json()
  if (!response.ok) throw new Error(body.message ?? '请求失败')
  return body
}

function renderMessage(message) {
  const article = document.createElement('article')
  article.className = `message ${message.role}`
  const author = message.role === 'user' ? '用户' : message.role === 'agent' ? message.author : '系统'
  article.innerHTML = `<div><strong>${escapeHtml(author ?? '')}</strong><time>${new Date(message.createdAt).toLocaleTimeString()}</time></div><p>${escapeHtml(message.content)}</p>`
  return article
}

function escapeHtml(value) {
  const node = document.createElement('div')
  node.textContent = value
  return node.innerHTML
}

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
