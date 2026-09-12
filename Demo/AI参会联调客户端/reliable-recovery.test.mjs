import test from 'node:test'
import assert from 'node:assert/strict'
import { ReliableAgent } from './reliable-agent.mjs'

const capabilities = ['explicit_context_ack', 'connection_resume', 'explicit_takeover', 'connection_lease', 'task_retry', 'client_wait_timeout']
const leasePolicy = { leaseRenewIntervalSeconds: 1, maximumWaitTimeoutSeconds: 30 }

function invocationFixture(tool) {
  const agent = new ReliableAgent({ url: 'test', meetingCode: 'test-code' })
  agent.transport = { tool, fail() {} }
  return agent
}

for (const name of ['join_meeting', 'resume_agent']) {
  test(name + ' pending activation retries the identical operation on the same transport', async () => {
    const calls = []
    const waits = []
    const args = { operationId: 'persisted-operation', recoveryKey: 'private-test-key' }
    const agent = invocationFixture(async (tool, actual) => {
      calls.push({ tool, args: structuredClone(actual) })
      return calls.length < 5 ? { succeeded: false, errorCode: 'operation_pending' }
        : { succeeded: true, data: { kind: 'ready' } }
    })
    agent.backoff = async milliseconds => { waits.push(milliseconds) }
    assert.equal((await agent.invoke(name, args)).kind, 'ready')
    assert.deepEqual(waits, [250, 500, 1000, 2000])
    assert.equal(calls.length, 5)
    assert.ok(calls.every(call => call.tool === name && call.args.operationId === args.operationId))
    assert.ok(calls.every(call => call.args.recoveryKey === args.recoveryKey))
  })
}

test('persistent activation pending stops after a bounded number of same-session requests', async () => {
  let count = 0
  const agent = invocationFixture(async () => { count++; return { succeeded: false, errorCode: 'operation_pending' } })
  agent.backoff = async () => {}
  await assert.rejects(agent.invoke('resume_agent', { operationId: 'same' }), error => error.code === 'operation_pending')
  assert.equal(count, 5)
})

test('temporary failures honor retryAfterSeconds before replaying an unchanged request', async () => {
  const waits = []
  const calls = []
  const agent = invocationFixture(async (name, args) => {
    calls.push({ name, args: structuredClone(args) })
    return calls.length < 3 ? { succeeded: false, errorCode: 'agent_operation_unavailable', data: { retryAfterSeconds: calls.length * 7 } }
      : { succeeded: true, data: {} }
  })
  agent.backoff = async milliseconds => { waits.push(milliseconds) }
  await agent.invoke('retry_task', { taskId: 'head', operationId: 'explicit-retry' })
  assert.deepEqual(waits, [7000, 14000])
  assert.deepEqual(calls[0], calls[1])
  assert.deepEqual(calls[1], calls[2])
})

test('stop cancels activation backoff without issuing a second request', async () => {
  let entered
  const waiting = new Promise(resolve => { entered = resolve })
  let calls = 0
  const agent = invocationFixture(async () => { calls++; return { succeeded: false, errorCode: 'operation_pending', data: { retryAfterSeconds: 60 } } })
  agent.log = event => { if (event === 'activation_pending') entered() }
  const pending = agent.invoke('join_meeting', {})
  await waiting
  agent.stop()
  await assert.rejects(pending, error => error.name === 'AbortError')
  assert.equal(calls, 1)
})

function savedFixture({ makeTransport, log = () => {}, state: provided } = {}) {
  let saved = provided ?? { version: 1, url: 'test', join: { meetingCode: 'code', displayName: 'ReferenceAgent', recoveryKey: 'private-key' },
    connectionSessionId: 'original', initialConfirmed: true }
  const agent = new ReliableAgent({ url: 'test', meetingCode: 'code', store: {
    async acquire() { return structuredClone(saved) }, async save(state) { saved = structuredClone(state) }, async release() {},
  }, transportFactory: makeTransport, log, generate: async () => assert.fail('generation must not run') })
  agent.backoff = async () => {}
  return { agent, saved: () => saved }
}

test('successful handshakes followed by repeated business failure cannot reconnect forever', async () => {
  let connections = 0
  let closes = 0
  let statuses = 0
  const f = savedFixture({ makeTransport: () => {
    if (++connections > 11) throw new Error('Unbounded reconnect detected')
    return { async open() {}, fail() {}, async close() { closes++; return true },
      async tool(name) {
        if (name === 'resume_agent') return { succeeded: true, data: { kind: 'resumed', capabilities, leasePolicy } }
        assert.equal(name, 'get_agent_status')
        statuses++
        return { succeeded: false, errorCode: 'agent_operation_unavailable' }
      } }
  } })
  await assert.rejects(f.agent.run(), error => error.code === 'recovery_attempts_exhausted')
  assert.equal(connections, 10)
  assert.equal(closes, 10)
  assert.equal(statuses, 30)
})

test('superseded resume operation closes the old transport and uses a new operation while preserving initial history', async () => {
  const operations = []
  const order = []
  let count = 0
  let f
  const context = { snapshotSequence: 1, messages: [{ content: 'saved initial history' }] }
  f = savedFixture({ state: { version: 1, url: 'test', join: { meetingCode: 'code', displayName: 'ReferenceAgent', recoveryKey: 'private-key' },
    connectionSessionId: 'original', initialConfirmed: true, initial: { initialContext: context }, task: { taskId: 'completed' } },
  makeTransport: () => {
    const number = ++count
    return { async open() { order.push('open' + number) }, fail() {}, async close() { order.push('close' + number); return true },
      async tool(name, args) {
        if (name === 'resume_agent') {
          operations.push(args.operationId)
          assert.equal(args.connectionSessionId, 'original')
          return number === 1 ? { succeeded: false, errorCode: 'operation_superseded' }
            : { succeeded: true, data: { kind: 'resumed', capabilities, leasePolicy, initialContext: null,
              connection: { initialContextState: 'confirmed' } } }
        }
        assert.equal(name, 'get_agent_status')
        return { succeeded: true, data: { requestedTask: { state: 'replied' } } }
      } }
  }, log: event => { if (event === 'reply_recovered') f.agent.stop() } })
  await f.agent.run()
  assert.equal(operations.length, 2)
  assert.notEqual(operations[0], operations[1])
  assert.deepEqual(order, ['open1', 'close1', 'open2', 'close2'])
  assert.deepEqual(f.saved().initial.initialContext, context)
})

test('a fatal business error cancels concurrent renewal backoff', { timeout: 5000 }, async () => {
  let releaseStatus
  const renewalEntered = new Promise(resolve => { releaseStatus = resolve })
  const f = savedFixture({ makeTransport: () => ({ async open() {}, fail() {}, async close() { return true },
    async tool(name) {
      if (name === 'resume_agent') return { succeeded: true, data: { kind: 'resumed', capabilities, leasePolicy } }
      if (name === 'get_agent_status') {
        await renewalEntered
        return { succeeded: false, errorCode: 'connection_superseded' }
      }
      assert.equal(name, 'renew_agent_lease')
      releaseStatus()
      return { succeeded: false, errorCode: 'agent_operation_unavailable', data: { retryAfterSeconds: 60 } }
    } }) })
  f.agent.backoff = ReliableAgent.prototype.backoff
  await assert.rejects(f.agent.run(), error => error.code === 'connection_superseded')
})

test('resume accepts collaboration summaries and recovers a committed derived reply without generation', async () => {
  const collaboration = { chainId: 'root-message', parentTaskId: 'parent-task', sourceAuthorKind: 'agent' }
  const summary = { taskId: 'completed', state: 'replied', collaboration }
  const calls = []
  let f
  f = savedFixture({ state: { version: 1, url: 'test', join: { meetingCode: 'code', displayName: 'ReferenceAgent', recoveryKey: 'private-key' },
    connectionSessionId: 'original', initialConfirmed: true,
    task: { taskId: 'completed', deliveryAttemptId: 'attempt', replyContent: 'saved reply', collaboration } },
  makeTransport: () => ({ async open() {}, fail() {}, async close() { return true },
    async tool(name, args) {
      calls.push({ name, args })
      if (name === 'resume_agent') return { succeeded: true, data: { kind: 'resumed',
        capabilities: [...capabilities, 'multi_agent_collaboration_v1'], leasePolicy,
        lastRepliedTask: summary, connection: { initialContextState: 'confirmed' } } }
      assert.equal(name, 'get_agent_status')
      return { succeeded: true, data: { queueHead: null, requestedTask: summary } }
    } }), log: event => { if (event === 'reply_recovered') f.agent.stop() } })
  await f.agent.run()
  assert.equal(f.saved().task, null)
  assert.deepEqual(f.saved().initial.lastRepliedTask.collaboration, collaboration)
  assert.deepEqual(calls.map(call => call.name), ['resume_agent', 'get_agent_status'])
  assert.equal(calls[1].args.taskId, 'completed')
  for (const call of calls) assert.equal(Object.hasOwn(call.args, 'collaboration'), false)
})