import test from 'node:test'
import assert from 'node:assert/strict'
import { ReliableAgent } from './reliable-agent.mjs'

for (const persistent of [false, true]) {
  test(`conditional write conflicts preserve requests and bound retries (persistent=${persistent})`, async () => {
    const calls = []
    const pauses = []
    const agent = new ReliableAgent({ url: 'test', meetingCode: 'test' })
    agent.transport = { async tool(name, args) {
      calls.push({ name, args: structuredClone(args) })
      return persistent || calls.length < 3
        ? { succeeded: false, errorCode: 'agent_operation_conflict' }
        : { succeeded: true, data: { kind: 'resumed' } }
    } }
    agent.backoff = async milliseconds => { pauses.push(milliseconds) }
    const args = { connectionSessionId: 'existing', operationId: 'same-operation', recoveryKey: 'private-key' }
    if (persistent) await assert.rejects(agent.invoke('resume_agent', args), error => error.code === 'agent_operation_conflict')
    else assert.equal((await agent.invoke('resume_agent', args)).kind, 'resumed')
    assert.equal(calls.length, 3)
    assert.deepEqual(calls[0], calls[1])
    assert.deepEqual(calls[1], calls[2])
    assert.deepEqual(pauses, [1000, 2000])
  })
}

test('a fingerprint conflict remains terminal without retrying the changed operation', async () => {
  const agent = new ReliableAgent({ url: 'test', meetingCode: 'test' })
  let calls = 0
  agent.transport = { async tool() { calls++; return { succeeded: false, errorCode: 'operation_conflict' } } }
  agent.backoff = async () => assert.fail('A fingerprint mismatch must not be retried')
  await assert.rejects(agent.invoke('resume_agent', {}), error => error.code === 'operation_conflict')
  assert.equal(calls, 1)
})
