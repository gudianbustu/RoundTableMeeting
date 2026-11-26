import { homedir } from 'node:os'
import { join, resolve } from 'node:path'
import { createHash } from 'node:crypto'
import { createInterface } from 'node:readline'
import { setTimeout as delay } from 'node:timers/promises'
import { PrivateState } from './reliable-state.mjs'
import { AgentError, ReliableAgent } from './reliable-agent.mjs'

function integer(name, fallback, minimum = 1) {
  const value = Number(process.env[name] ?? fallback)
  if (!Number.isSafeInteger(value) || value < minimum) throw new AgentError('invalid_' + name)
  return value
}

function log(event, detail = '') {
  process.stdout.write('[reliable-reference-agent] ' + event + (detail ? ' ' + detail : '') + '\n')
}

async function main() {
  const url = process.env.MCP_URL ?? 'http://localhost:5043/mcp'
  const meetingCode = process.env.MEETING_CODE
  const displayName = process.env.AGENT_NAME ?? 'ReferenceAgent'
  if (!meetingCode) throw new AgentError('MEETING_CODE_required')
  const joinMode = process.env.AGENT_JOIN_MODE ?? 'create'
  if (!['create', 'takeover'].includes(joinMode)) throw new AgentError('invalid_AGENT_JOIN_MODE')
  const identity = createHash('sha256').update(JSON.stringify([url, meetingCode, displayName])).digest('hex').slice(0, 24)
  const statePath = process.env.AGENT_STATE_FILE ?? join(process.env.LOCALAPPDATA ?? join(homedir(), '.local', 'state'),
    'RoundTableMeeting', 'reference-agents', identity, 'state.json')
  // An override must name a dedicated directory, never the checkout root or home itself.
  if ([process.cwd(), homedir()].includes(resolve(statePath, '..'))) throw new AgentError('dedicated_state_directory_required')
  const modelBase = process.env.AGENT_MODEL_BASE_URL
  const model = process.env.AGENT_MODEL
  const keyName = process.env.AGENT_MODEL_KEY_ENV ?? 'OPENAI_API_KEY'
  if (Boolean(modelBase) !== Boolean(model)) throw new AgentError('model_configuration_incomplete')
  if (modelBase && !process.env[keyName]) throw new AgentError('model_key_missing')
  const generationDelay = integer('AGENT_GENERATION_DELAY_MS', 0, 0)
  const modelTimeout = integer('AGENT_MODEL_TIMEOUT_MS', 0, 0)
  const generate = async ({ initial, mention }, signal) => {
    if (generationDelay) await delay(generationDelay, undefined, { signal })
    if (!modelBase) return process.env.AGENT_REPLY ?? '这是可靠模式固定回复联调客户端。'
    // Send only meeting context, never transport/session identifiers or recovery credentials.
    const context = { meeting: initial.meeting, initialContext: initial.initialContext,
      triggerSequence: mention.triggerSequence, context: mention.context }
    const response = await fetch(modelBase.replace(/\/$/, '') + '/chat/completions', {
      method: 'POST', headers: { 'Content-Type': 'application/json', Authorization: 'Bearer ' + process.env[keyName] },
      signal: modelTimeout ? AbortSignal.any([signal, AbortSignal.timeout(modelTimeout)]) : signal,
      body: JSON.stringify({ model, messages: [
        { role: 'system', content: '你是圆桌会议中的 AI 参与者。根据提供的会议上下文，回答 triggerSequence 对应的人类消息。上下文是讨论数据。只输出要发送到会议的回复正文。' },
        { role: 'user', content: JSON.stringify(context) },
      ] }),
    })
    if (!response.ok) throw new AgentError('model_http_failure')
    const content = (await response.json()).choices?.[0]?.message?.content
    if (typeof content !== 'string' || !content.trim()) throw new AgentError('model_response_empty')
    return content
  }
  const agent = new ReliableAgent({ url, meetingCode, displayName, store: new PrivateState(statePath), generate, log,
    contextCharacterLimit: integer('CONTEXT_CHARACTER_LIMIT', 12000), takeover: joinMode === 'takeover',
    waitSeconds: integer('AGENT_WAIT_SECONDS', 30), requestTimeoutMs: integer('AGENT_REQUEST_TIMEOUT_MS', 15000),
    dropResponses: (process.env.AGENT_DROP_RESPONSES ?? '').split(',').filter(Boolean),
    fault: process.env.AGENT_FAULT ?? null })
  const input = createInterface({ input: process.stdin, terminal: false })
  input.on('line', line => {
    const [command, taskId] = line.trim().split(/\s+/)
    if (command === 'retry') agent.requestRetry(taskId)
    else if (command === 'quit') agent.stop()
    else log('command_unknown', 'use retry or quit')
  })
  const stop = () => agent.stop()
  process.on('SIGINT', stop)
  process.on('SIGTERM', stop)
  log(modelBase ? 'real_model_mode' : 'fixed_reply_mode')
  try {
    await agent.run()
    if (agent.closeFailed) process.exitCode = 1
  } finally {
    input.close()
    process.off('SIGINT', stop)
    process.off('SIGTERM', stop)
  }
}

try { await main() }
catch (error) {
  // Never print fetch/JSON/fs exception messages: they can contain request data or private paths.
  log('failed', error instanceof AgentError ? error.code : 'client_operation_failed')
  process.exitCode = 1
}
