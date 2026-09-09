const mcpUrl = process.env.MCP_URL ?? 'http://localhost:5043/mcp'
const meetingCode = process.env.MEETING_CODE
const displayName = process.env.AGENT_NAME ?? 'ReferenceAgent'
const contextCharacterLimit = parsePositiveInteger(process.env.CONTEXT_CHARACTER_LIMIT ?? '12000', 'CONTEXT_CHARACTER_LIMIT')
const fixedReply = process.env.AGENT_REPLY ?? '这是固定回复联调客户端，请将它替换为真实模型生成内容。'
const waitRequestTimeoutMs = parseNonNegativeInteger(process.env.WAIT_REQUEST_TIMEOUT_MS ?? '0', 'WAIT_REQUEST_TIMEOUT_MS')

if (!meetingCode) throw new Error('MEETING_CODE is required.')

const clientName = 'round-table-ai-meeting-reference-agent'
const expectedTools = new Set(['get_agent_status', 'join_meeting', 'reply_to_mention', 'wait_for_mention'])
let nextRequestId = 1
let protocolSessionId
let connectionSessionId
let stopping = false
let shutdownPromise
let eventStreamTask
const pendingResponses = new Map()
const eventStreamAbort = new AbortController()
const requestControllers = new Set()

function parsePositiveInteger(value, name) {
  const parsed = Number(value)
  if (!Number.isSafeInteger(parsed) || parsed <= 0) throw new Error(name + ' must be a positive integer.')
  return parsed
}

function parseNonNegativeInteger(value, name) {
  const parsed = Number(value)
  if (!Number.isSafeInteger(parsed) || parsed < 0) throw new Error(name + ' must be a non-negative integer.')
  return parsed
}

function log(event, details = '') {
  process.stdout.write('[ai-meeting-reference-agent] ' + event + (details ? ' ' + details : '') + '\n')
}

function idSummary(value) {
  if (!value) return 'none'
  const text = String(value)
  return text.length <= 8 ? text : '...' + text.slice(-8)
}

function errorSummary(error) {
  if (error?.name === 'AbortError') return 'request_aborted'
  return error instanceof Error ? error.message : 'unknown_error'
}

function parseSseData(raw) {
  const data = raw
    .split(/\r?\n/)
    .filter(line => line.startsWith('data:'))
    .map(line => line.slice(5).trim())
    .join('\n')
  return JSON.parse(data || raw.trim())
}

async function readRpcResponse(response) {
  const raw = await response.text()
  if (!raw.trim()) return undefined
  return parseSseData(raw)
}

function resolvePending(message) {
  if (message?.id === undefined || message?.id === null) return
  const key = String(message.id)
  const pending = pendingResponses.get(key)
  if (!pending) return
  pendingResponses.delete(key)
  if (message.error) {
    pending.reject(new Error('MCP ' + message.error.code + ': ' + message.error.message))
    return
  }
  pending.resolve(message)
}

function rejectAllPending(error) {
  for (const [key, pending] of pendingResponses) {
    pendingResponses.delete(key)
    pending.reject(error)
  }
}

async function readEventStream(response) {
  if (!response.body) throw new Error('MCP event stream did not provide a response body.')
  const reader = response.body.getReader()
  const decoder = new TextDecoder()
  let buffer = ''
  try {
    while (!stopping) {
      const { done, value } = await reader.read()
      if (done) break
      buffer += decoder.decode(value, { stream: true })
      let boundary
      while ((boundary = buffer.search(/\r?\n\r?\n/)) >= 0) {
        const separator = buffer.slice(boundary).match(/^\r?\n\r?\n/)[0]
        const event = buffer.slice(0, boundary)
        buffer = buffer.slice(boundary + separator.length)
        const data = event
          .split(/\r?\n/)
          .filter(line => line.startsWith('data:'))
          .map(line => line.slice(5).trim())
          .join('\n')
        if (!data) continue
        try {
          resolvePending(JSON.parse(data))
        } catch (error) {
          log('event_parse_failed', errorSummary(error))
        }
      }
    }
  } catch (error) {
    if (!stopping || error?.name !== 'AbortError') throw error
  } finally {
    reader.releaseLock()
  }
  if (!stopping) throw new Error('MCP event stream ended unexpectedly.')
}

async function openEventStream() {
  const response = await fetch(mcpUrl, {
    method: 'GET',
    headers: { Accept: 'text/event-stream', 'Mcp-Session-Id': protocolSessionId },
    signal: eventStreamAbort.signal,
  })
  if (!response.ok) throw new Error('MCP event stream failed with HTTP ' + response.status + '.')
  log('event_stream_open')
  return readEventStream(response)
}

async function initialize() {
  const response = await fetch(mcpUrl, {
    method: 'POST',
    headers: { Accept: 'application/json, text/event-stream', 'Content-Type': 'application/json' },
    body: JSON.stringify({
      jsonrpc: '2.0',
      id: nextRequestId++,
      method: 'initialize',
      params: {
        protocolVersion: '2025-11-25',
        capabilities: {},
        clientInfo: { name: clientName, version: '1.0.0' },
      },
    }),
  })
  if (!response.ok) throw new Error('MCP initialize failed with HTTP ' + response.status + '.')
  protocolSessionId = response.headers.get('Mcp-Session-Id')
  if (!protocolSessionId) throw new Error('MCP initialize did not return Mcp-Session-Id.')
  const message = await readRpcResponse(response)
  if (!message?.result) throw new Error('MCP initialize did not return a result.')
  log('initialized')
  await sendNotification('notifications/initialized', {})
  eventStreamTask = openEventStream()
  eventStreamTask.catch(error => {
    if (!stopping) {
      log('event_stream_failed', errorSummary(error))
      rejectAllPending(error)
    }
  })
  await listTools()
}

async function sendNotification(method, params) {
  const response = await fetch(mcpUrl, {
    method: 'POST',
    headers: {
      Accept: 'application/json, text/event-stream',
      'Content-Type': 'application/json',
      'Mcp-Session-Id': protocolSessionId,
    },
    body: JSON.stringify({ jsonrpc: '2.0', method, params }),
  })
  if (!response.ok) throw new Error('MCP notification ' + method + ' failed with HTTP ' + response.status + '.')
  await response.arrayBuffer()
}

function createPending(requestId, timeoutMs, onTimeout = () => {}) {
  let timer
  const response = new Promise((resolve, reject) => {
    pendingResponses.set(String(requestId), { resolve, reject })
  })
  const timeout = timeoutMs > 0
    ? new Promise((_, reject) => {
        timer = setTimeout(() => {
          const pending = pendingResponses.get(String(requestId))
          if (pending) {
            pendingResponses.delete(String(requestId))
            const error = new Error('MCP request timed out locally.')
            error.name = 'AbortError'
            onTimeout()
            reject(error)
          }
        }, timeoutMs)
      })
    : null
  return {
    promise: timeout ? Promise.race([response, timeout]) : response,
    clear: () => { if (timer) clearTimeout(timer) },
  }
}

async function callTool(name, args, { timeoutMs = 0 } = {}) {
  if (stopping) throw new Error('MCP session is stopping.')
  const requestId = nextRequestId++
  const controller = new AbortController()
  requestControllers.add(controller)
  let pending
  try {
    pending = createPending(requestId, timeoutMs, () => controller.abort())
    // Keep the timeout active through response-body/SSE consumption, not only headers.
    pending.promise.catch(() => {})
    const response = await fetch(mcpUrl, {
      method: 'POST',
      headers: {
        Accept: 'application/json, text/event-stream',
        'Content-Type': 'application/json',
        'Mcp-Session-Id': protocolSessionId,
      },
      signal: controller.signal,
      body: JSON.stringify({ jsonrpc: '2.0', id: requestId, method: 'tools/call', params: { name, arguments: args } }),
    })
    if (response.status === 202) return await pending.promise
    if (!response.ok) throw new Error('MCP ' + name + ' failed with HTTP ' + response.status + '.')
    return await Promise.race([readRpcResponse(response), pending.promise])
  } finally {
    controller.abort()
    pending?.clear()
    pendingResponses.delete(String(requestId))
    requestControllers.delete(controller)
  }
}

function toolPayload(message, name) {
  const result = message?.result
  if (!result) throw new Error('MCP ' + name + ' did not return a result.')
  if (result.structuredContent) return result.structuredContent
  const text = result.content?.find(item => item.type === 'text')?.text
  if (!text) throw new Error('MCP ' + name + ' did not return structured or text content.')
  return JSON.parse(text)
}

async function listTools() {
  const message = await callProtocolRequest('tools/list', {})
  const names = new Set(message?.result?.tools?.map(tool => tool.name) ?? [])
  for (const expected of expectedTools) {
    if (!names.has(expected)) throw new Error('MCP tool ' + expected + ' is unavailable.')
  }
  log('tools_ready', 'count=' + names.size)
}

async function callProtocolRequest(method, params) {
  if (stopping) throw new Error('MCP session is stopping.')
  const requestId = nextRequestId++
  const pending = createPending(requestId, 0)
  try {
    const response = await fetch(mcpUrl, {
      method: 'POST',
      headers: {
        Accept: 'application/json, text/event-stream',
        'Content-Type': 'application/json',
        'Mcp-Session-Id': protocolSessionId,
      },
      body: JSON.stringify({ jsonrpc: '2.0', id: requestId, method, params }),
    })
    if (response.status === 202) return await pending.promise
    pendingResponses.delete(String(requestId))
    if (!response.ok) throw new Error('MCP ' + method + ' failed with HTTP ' + response.status + '.')
    return await readRpcResponse(response)
  } finally {
    pending.clear()
    pendingResponses.delete(String(requestId))
  }
}

async function callAgentTool(name, args, options = {}) {
  return toolPayload(await callTool(name, args, options), name)
}

async function runAgent() {
  await initialize()
  const join = await callAgentTool('join_meeting', {
    meetingCode,
    displayName,
    contextCharacterLimit,
  })
  if (!join.succeeded) throw new Error('join_meeting failed: ' + join.errorCode)
  connectionSessionId = join.data.connection.connectionSessionId
  log('joined', 'name=' + displayName + ' connection=' + idSummary(connectionSessionId))

  let replyCount = 0
  while (!stopping) {
    const wait = await callAgentTool(
      'wait_for_mention',
      { connectionSessionId },
      { timeoutMs: waitRequestTimeoutMs },
    )
    if (!wait.succeeded) {
      if (wait.errorCode === 'task_not_available') await diagnoseStatus()
      throw new Error('wait_for_mention failed: ' + wait.errorCode)
    }
    if (wait.data?.kind === 'idle') {
      log('idle', 'continuing_same_session')
      continue
    }
    if (wait.data?.kind !== 'mention') throw new Error('wait_for_mention returned an unknown result kind.')

    const mention = wait.data
    log('mention_received', 'task=' + idSummary(mention.taskId) + ' attempt=' + idSummary(mention.deliveryAttemptId))
    const reply = await callAgentTool('reply_to_mention', {
      connectionSessionId,
      taskId: mention.taskId,
      deliveryAttemptId: mention.deliveryAttemptId,
      replyIdempotencyKey: mention.replyIdempotencyKey,
      content: fixedReply,
    })
    if (!reply.succeeded) throw new Error('reply_to_mention failed: ' + reply.errorCode)
    replyCount += 1
    log('replied', 'count=' + replyCount + ' message=' + idSummary(reply.data?.message?.messageId))
  }
}

async function diagnoseStatus() {
  try {
    const status = await callAgentTool('get_agent_status', { connectionSessionId })
    if (!status.succeeded) {
      log('status_failed', 'error=' + status.errorCode)
      return
    }
    const head = status.data?.queueHead
    log('status', 'connection=' + (status.data?.connection?.state ?? 'unknown') + ' head=' + idSummary(head?.taskId) + ' head_state=' + (head?.state ?? 'none'))
  } catch (error) {
    log('status_failed', 'error=' + errorSummary(error))
  }
}

async function closeProtocolSession() {
  if (!protocolSessionId) return true
  const closingSessionId = protocolSessionId
  protocolSessionId = undefined
  try {
    const response = await fetch(mcpUrl, {
      method: 'DELETE',
      headers: { 'Mcp-Session-Id': closingSessionId },
    })
    if (!response.ok) {
      log('close_failed', 'http=' + response.status)
      return false
    }
    await response.arrayBuffer()
    log('closed')
    return true
  } catch (error) {
    log('close_failed', 'error=' + errorSummary(error))
    return false
  }
}

async function stop() {
  if (shutdownPromise) return shutdownPromise
  shutdownPromise = (async () => {
    stopping = true
    for (const controller of requestControllers) controller.abort()
    eventStreamAbort.abort()
    rejectAllPending(Object.assign(new Error('MCP session is stopping.'), { name: 'AbortError' }))
    if (eventStreamTask) {
      try { await eventStreamTask } catch (error) { if (error?.name !== 'AbortError') log('event_stream_closed', errorSummary(error)) }
    }
    const closed = await closeProtocolSession()
    if (!closed) process.exitCode = 1
  })()
  return shutdownPromise
}

async function main() {
  process.on('SIGINT', () => { void stop() })
  process.on('SIGTERM', () => { void stop() })
  try {
    await runAgent()
  } catch (error) {
    if (!stopping) {
      log('failed', errorSummary(error))
      process.exitCode = 1
    }
  } finally {
    await stop()
  }
}

await main()
