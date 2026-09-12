import test from 'node:test'
import assert from 'node:assert/strict'
import { mkdtemp, readFile, rm } from 'node:fs/promises'
import { tmpdir } from 'node:os'
import { join } from 'node:path'
import { createServer } from 'node:http'
import { setTimeout as delay } from 'node:timers/promises'
import { ReliableTransport, TransportError } from './reliable-transport.mjs'
import { PrivateState } from './reliable-state.mjs'
import { ReliableAgent } from './reliable-agent.mjs'

const capabilities = ['explicit_context_ack', 'connection_resume', 'explicit_takeover', 'connection_lease', 'task_retry', 'client_wait_timeout']
const tools = ['join_meeting', 'wait_for_mention', 'reply_to_mention', 'get_agent_status', 'resume_agent',
  'confirm_context', 'renew_agent_lease', 'report_task_failure', 'retry_task']
const leasePolicy = { leaseRenewIntervalSeconds: 1, maximumWaitTimeoutSeconds: 30 }

test('private state survives restart and rejects a concurrent owner', async () => {
  const directory = await mkdtemp(join(tmpdir(), 'rtm-private-test-'))
  const first = new PrivateState(join(directory, 'state.json'))
  const second = new PrivateState(join(directory, 'state.json'))
  try {
    assert.equal(await first.acquire(), undefined)
    await first.save({ credential: 'private-test-value', task: { reply: 'pending' } })
    await assert.rejects(second.acquire(), /state_locked/)
    await first.release()
    assert.deepEqual(await second.acquire(), { credential: 'private-test-value', task: { reply: 'pending' } })
    assert.equal(JSON.parse(await readFile(first.path, 'utf8')).task.reply, 'pending')
  } finally { await first.release(); await second.release(); await rm(directory, { recursive: true, force: true }) }
})

test('transport multiplexes an SSE wait and renewal, and times out a stalled body', { timeout: 10000 }, async () => {
  let stream
  let waitId
  let deleted = 0
  const server = createServer(async (request, response) => {
    if (request.method === 'GET') { stream = response; response.writeHead(200, { 'Content-Type': 'text/event-stream' }); response.write(': ready\n\n'); return }
    if (request.method === 'DELETE') { deleted++; response.writeHead(200).end(); return }
    let body = ''
    for await (const chunk of request) body += chunk
    const rpc = JSON.parse(body)
    if (rpc.id === undefined) { response.writeHead(202).end(); return }
    let result
    if (rpc.method === 'initialize') { response.setHeader('Mcp-Session-Id', 'test'); result = {} }
    else if (rpc.method === 'tools/list') result = { tools: tools.map(name => ({ name })) }
    else if (rpc.params.name === 'wait_for_mention') { waitId = rpc.id; response.writeHead(202).end(); return }
    else if (rpc.params.name === 'renew_agent_lease') {
      stream.write('data: ' + JSON.stringify({ jsonrpc: '2.0', id: waitId, result: { structuredContent: { kind: 'idle' } } }) + '\n\n')
      result = { structuredContent: { kind: 'lease_renewed' } }
    } else { response.writeHead(200, { 'Content-Type': 'text/event-stream' }); response.write(': stalled\n\n'); return }
    response.writeHead(200, { 'Content-Type': 'application/json' }).end(JSON.stringify({ jsonrpc: '2.0', id: rpc.id, result }))
  })
  await new Promise(resolve => server.listen(0, '127.0.0.1', resolve))
  const client = new ReliableTransport('http://127.0.0.1:' + server.address().port, 500)
  try {
    await client.open()
    const wait = client.tool('wait_for_mention', {}, { timeoutMs: 3000 })
    for (let index = 0; !waitId && index < 50; index++) await delay(10)
    assert.equal((await client.tool('renew_agent_lease', {})).kind, 'lease_renewed')
    assert.equal((await wait).kind, 'idle')
    // GET must remain alive beyond the timeout used for its response headers.
    await delay(650)
    assert.equal(client.failure, undefined)
    await assert.rejects(client.tool('stalled', {}), error => error instanceof TransportError && error.code === 'request_timeout')
    assert.equal(await client.close(), true)
    assert.equal(deleted, 1)
  } finally { await client.close(); server.closeAllConnections(); await new Promise(resolve => server.close(resolve)) }
})

function fixture({ permanent = false, slow = false, loseReply = false, collaboration } = {}) {
  let saved
  let taskState = 'queued'
  let generationCount = 0
  let generationInput
  let replies = 0
  let renewals = 0
  let retryCalls = 0
  const events = []
  const calls = []
  const mention = { kind: 'mention', taskId: 'task', deliveryAttemptId: 'attempt', replyIdempotencyKey: 'key', context: {} }
  if (collaboration) mention.collaboration = structuredClone(collaboration)
  const summary = () => ({ taskId: 'task', currentAttemptId: 'attempt', state: taskState, retryExhausted: taskState === 'failed',
    ...(collaboration ? { collaboration: structuredClone(collaboration) } : {}) })
  let agent
  const transport = { async open() {}, fail(error) { this.failure = error }, async close() { return true },
    async tool(name, args) {
      calls.push({ name, args: structuredClone(args) })
      let data
      if (name === 'join_meeting') data = { kind: 'joined', connection: { connectionSessionId: 'connection' }, initialDeliveryId: 'initial', capabilities: collaboration ? [...capabilities, 'multi_agent_collaboration_v1'] : capabilities, leasePolicy }
      else if (name === 'confirm_context') { if (args.kind === 'task') taskState = 'processing'; data = { kind: 'context_confirmed' } }
      else if (name === 'get_agent_status') data = { queueHead: taskState === 'replied' ? null : summary(), requestedTask: args.taskId ? summary() : null }
      else if (name === 'wait_for_mention') { taskState = 'delivered'; data = mention }
      else if (name === 'renew_agent_lease') { renewals++; data = {} }
      else if (name === 'report_task_failure') { taskState = 'failed'; data = {} }
      else if (name === 'retry_task') { retryCalls++; taskState = 'queued'; data = {} }
      else if (name === 'reply_to_mention') {
        if (taskState !== 'replied') replies++
        taskState = 'replied'
        data = { kind: 'replied' }
      } else throw new Error('unexpected ' + name)
      return { succeeded: true, data }
    } }
  agent = new ReliableAgent({ url: 'test', meetingCode: 'private-code', store: {
    async acquire() { return saved }, async save(state) { saved = structuredClone(state) }, async release() {},
  }, transportFactory: () => transport, generate: async (input, signal) => {
    generationCount++
    generationInput = structuredClone(input)
    if (slow) await delay(2200, undefined, { signal })
    return 'private-reply'
  }, fault: permanent ? 'permanent_failure' : null,
  dropResponses: loseReply ? ['join_meeting', 'wait_for_mention', 'confirm_context', 'reply_to_mention'] : [],
  log: (event, detail) => { events.push([event, detail]); if (event === 'replied') agent.stop() } })
  return { agent, events, calls, generationInput: () => generationInput, counts: () => ({ generationCount, replies, renewals, retryCalls }), saved: () => saved }
}

test('lost join, confirm, wait and reply responses replay without regenerating or duplicating', async () => {
  const f = fixture({ loseReply: true })
  await f.agent.run()
  assert.equal(f.counts().generationCount, 1)
  assert.equal(f.counts().replies, 1)
  assert.equal(f.events.filter(([event]) => event === 'fault_response_dropped').length, 4)
  for (const name of ['join_meeting', 'reply_to_mention']) {
    const calls = f.calls.filter(call => call.name === name)
    assert.equal(calls.length, 2)
    assert.deepEqual(calls[0].args, calls[1].args)
  }
  assert.doesNotMatch(JSON.stringify(f.events), /private-code|private-reply/)
})

test('renewal runs while generation is pending', { timeout: 5000 }, async () => {
  const f = fixture({ slow: true })
  await f.agent.run()
  assert.ok(f.counts().renewals >= 2)
  assert.equal(f.counts().replies, 1)
})

test('exhausted head renews and only retries after an explicit command', { timeout: 8000 }, async () => {
  const f = fixture({ permanent: true })
  const run = f.agent.run()
  try {
    for (let index = 0; !f.agent.blockedTask && index < 100; index++) await delay(10)
    assert.equal(f.agent.blockedTask, 'task')
    await delay(2150)
    assert.ok(f.counts().renewals >= 2)
    assert.equal(f.counts().retryCalls, 0)
    f.agent.requestRetry('unrelated-task')
    assert.equal(f.counts().retryCalls, 0)
    f.agent.requestRetry()
    await run
    assert.equal(f.counts().retryCalls, 1)
    assert.equal(f.counts().replies, 1)
  } finally { f.agent.stop(); await run }
})

test('restart resumes the original ID and recognizes a committed reply before generating', async () => {
  let saved = { version: 1, url: 'test', join: { meetingCode: 'code', displayName: 'ReferenceAgent', recoveryKey: 'secret' },
    connectionSessionId: 'original', initialConfirmed: true,
    task: { taskId: 'task', deliveryAttemptId: 'old-attempt', replyContent: 'already-generated' } }
  const calls = []
  let agent
  agent = new ReliableAgent({ url: 'test', meetingCode: 'code', store: {
    async acquire() { return structuredClone(saved) }, async save(state) { saved = structuredClone(state) }, async release() {},
  }, generate: async () => { assert.fail('committed task must not be regenerated') },
  transportFactory: () => ({ async open() {}, fail() {}, async close() { return true },
    async tool(name, args) {
      calls.push({ name, args })
      assert.equal(args.connectionSessionId, 'original')
      if (name === 'resume_agent') {
        assert.equal(saved.resumeOperationId, args.operationId)
        return { succeeded: true, data: { kind: 'resumed', capabilities, leasePolicy } }
      }
      assert.equal(name, 'get_agent_status')
      return { succeeded: true, data: { requestedTask: { state: 'replied', taskId: 'task' } } }
    } }), log: event => { if (event === 'reply_recovered') agent.stop() } })
  await agent.run()
  assert.equal(saved.task, null)
  assert.deepEqual(calls.map(call => call.name), ['resume_agent', 'get_agent_status'])
})

test('a resumed invalidated attempt discards its body before generating for the new attempt', async () => {
  let saved = { version: 1, url: 'test', join: { meetingCode: 'code', displayName: 'ReferenceAgent', recoveryKey: 'secret' },
    connectionSessionId: 'original', initialConfirmed: true,
    task: { taskId: 'task', deliveryAttemptId: 'old-attempt', replyContent: 'stale-body' } }
  let generated = 0
  let replied
  let agent
  agent = new ReliableAgent({ url: 'test', meetingCode: 'code', store: {
    async acquire() { return structuredClone(saved) }, async save(state) { saved = structuredClone(state) }, async release() {},
  }, generate: async () => { generated++; return 'new-body' },
  transportFactory: () => ({ async open() {}, fail() {}, async close() { return true },
    async tool(name, args) {
      let data = {}
      if (name === 'resume_agent') data = { kind: 'resumed', capabilities, leasePolicy }
      else if (name === 'get_agent_status') data = { requestedTask: { state: 'failed', currentAttemptId: 'old-attempt' },
        queueHead: { state: 'failed', retryExhausted: false } }
      else if (name === 'wait_for_mention') data = { kind: 'mention', taskId: 'task', deliveryAttemptId: 'new-attempt', replyIdempotencyKey: 'key' }
      else if (name === 'reply_to_mention') replied = args
      return { succeeded: true, data }
    } }), log: event => { if (event === 'replied') agent.stop() } })
  await agent.run()
  assert.equal(generated, 1)
  assert.equal(replied.deliveryAttemptId, 'new-attempt')
  assert.equal(replied.content, 'new-body')
})

test('superseded recovery stops without reverse takeover', async () => {
  const calls = []
  const agent = new ReliableAgent({ url: 'test', meetingCode: 'code', store: {
    async acquire() { return { version: 1, url: 'test', join: { meetingCode: 'code', displayName: 'ReferenceAgent', recoveryKey: 'secret' }, connectionSessionId: 'original' } },
    async save() {}, async release() {},
  }, generate: async () => assert.fail('must stop'), transportFactory: () => ({ async open() {}, async close() { return true },
    async tool(name) { calls.push(name); return { succeeded: false, errorCode: 'connection_superseded', data: { nextAction: 'stop' } } },
  }) })
  await assert.rejects(agent.run(), error => error.code === 'connection_superseded')
  assert.deepEqual(calls, ['resume_agent'])
})

for (const [name, collaboration] of [
  ['old server', undefined],
  ['root', { chainId: 'root-message', parentTaskId: null, sourceAuthorKind: 'human' }],
  ['derived', { chainId: 'root-message', parentTaskId: 'parent-task', sourceAuthorKind: 'agent' }],
]) {
  test(`collaboration compatibility preserves ${name} input without changing tool arguments`, async () => {
    const f = fixture({ collaboration, loseReply: true })
    await f.agent.run()
    assert.deepEqual(f.generationInput().mention.collaboration, collaboration)
    assert.equal(f.generationInput().initial.capabilities.includes('multi_agent_collaboration_v1'), Boolean(collaboration))
    assert.equal(f.counts().generationCount, 1)
    assert.equal(f.counts().replies, 1)
    for (const call of f.calls) {
      assert.equal(Object.hasOwn(call.args, 'collaboration'), false)
      assert.equal(Object.hasOwn(call.args, 'chainId'), false)
      assert.equal(Object.hasOwn(call.args, 'parentTaskId'), false)
    }
    assert.doesNotMatch(JSON.stringify(f.events), /root-message|parent-task/)
  })
}