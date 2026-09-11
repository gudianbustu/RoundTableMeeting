export class TransportError extends Error {
  constructor(code = 'transport_unavailable') { super(code); this.code = code }
}

// One transport owns one response map. A replacement never receives old responses.
export class ReliableTransport {
  constructor(url, timeoutMs = 15000) {
    this.url = url
    this.timeoutMs = timeoutMs
    this.nextId = 1
    this.pending = new Map()
    this.abort = new AbortController()
    this.failure = undefined
  }

  headers() {
    return { Accept: 'application/json, text/event-stream', 'Content-Type': 'application/json',
      ...(this.sessionId ? { 'Mcp-Session-Id': this.sessionId } : {}) }
  }

  deliver(message) {
    const pending = this.pending.get(message?.id)
    if (!pending) return
    if (message.error) pending.reject(new TransportError('rpc_rejected'))
    else pending.resolve(message.result)
  }

  fail(error) {
    this.failure ??= error
    for (const pending of this.pending.values()) pending.reject(this.failure)
    this.abort.abort()
  }

  async consume(response) {
    if (!response.body) throw new TransportError('response_missing')
    if (!response.headers.get('content-type')?.includes('text/event-stream')) {
      this.deliver(await response.json())
      return
    }
    const reader = response.body.getReader()
    const decoder = new TextDecoder()
    let buffer = ''
    try {
      while (true) {
        const { done, value } = await reader.read()
        if (done) break
        buffer += decoder.decode(value, { stream: true })
        let boundary
        while ((boundary = /\r?\n\r?\n/.exec(buffer))) {
          const event = buffer.slice(0, boundary.index)
          buffer = buffer.slice(boundary.index + boundary[0].length)
          const data = event.split(/\r?\n/).filter(line => line.startsWith('data:'))
            .map(line => line.slice(5).trimStart()).join('\n')
          if (data) this.deliver(JSON.parse(data))
        }
      }
    } finally { reader.releaseLock() }
  }

  async request(method, params, { timeoutMs = this.timeoutMs, notification = false } = {}) {
    if (this.failure || this.abort.signal.aborted) throw this.failure ?? new TransportError()
    const id = this.nextId++
    const controller = new AbortController()
    const signal = AbortSignal.any([this.abort.signal, controller.signal])
    let timer
    const answer = new Promise((resolve, reject) => {
      if (!notification) this.pending.set(id, { resolve, reject })
      timer = setTimeout(() => { controller.abort(); reject(new TransportError('request_timeout')) }, timeoutMs)
    })
    answer.catch(() => {})
    try {
      const send = async () => {
        const response = await fetch(this.url, { method: 'POST', headers: this.headers(), signal,
          body: JSON.stringify({ jsonrpc: '2.0', ...(notification ? {} : { id }), method, params }) })
        if (!response.ok) throw new TransportError('http_' + response.status)
        this.sessionId ??= response.headers.get('mcp-session-id')
        if (notification) { await response.arrayBuffer(); return }
        if (response.status !== 202) {
          // Some hosts retain an SSE response after emitting the requested result.
          void this.consume(response).catch(() => this.pending.get(id)?.reject(new TransportError('response_interrupted')))
        }
        return await answer
      }
      return await Promise.race([send(), answer])
    } catch (error) {
      throw error instanceof TransportError ? error : new TransportError()
    } finally {
      clearTimeout(timer)
      controller.abort()
      this.pending.delete(id)
    }
  }

  async open() {
    await this.request('initialize', { protocolVersion: '2025-11-25', capabilities: {},
      clientInfo: { name: 'round-table-reliable-reference-agent', version: '1.0.0' } })
    if (!this.sessionId) throw new TransportError('session_missing')
    await this.request('notifications/initialized', {}, { notification: true })
    const opening = new AbortController()
    const deadline = setTimeout(() => opening.abort(), this.timeoutMs)
    let response
    try {
      response = await fetch(this.url, { headers: this.headers(), signal: AbortSignal.any([this.abort.signal, opening.signal]) })
    } catch { throw new TransportError('event_stream_unavailable')
    } finally { clearTimeout(deadline) }
    if (!response.ok) throw new TransportError('event_stream_rejected')
    this.stream = this.consume(response).then(() => { if (!this.abort.signal.aborted) this.fail(new TransportError('event_stream_ended')) })
      .catch(() => { if (!this.abort.signal.aborted) this.fail(new TransportError('event_stream_interrupted')) })
    const list = await this.request('tools/list', {})
    const names = new Set(list?.tools?.map(tool => tool.name))
    for (const name of ['join_meeting', 'wait_for_mention', 'reply_to_mention', 'get_agent_status',
      'resume_agent', 'confirm_context', 'renew_agent_lease', 'report_task_failure', 'retry_task']) {
      if (!names.has(name)) throw new TransportError('reliable_capability_missing')
    }
  }

  async tool(name, args, options) {
    const result = await this.request('tools/call', { name, arguments: args }, options)
    try {
      return result.structuredContent ?? JSON.parse(result.content.find(item => item.type === 'text').text)
    } catch { throw new TransportError('tool_response_invalid') }
  }

  async close() {
    this.fail(new TransportError('transport_closed'))
    let closed = !this.sessionId
    if (this.sessionId) {
      try {
        const response = await fetch(this.url, { method: 'DELETE', headers: this.headers(), signal: AbortSignal.timeout(this.timeoutMs) })
        closed = response.ok || response.status === 404
        await response.arrayBuffer()
      } catch { closed = false }
    }
    await this.stream
    return closed
  }
}
