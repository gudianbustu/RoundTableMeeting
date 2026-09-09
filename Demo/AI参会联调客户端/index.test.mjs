import test from 'node:test'
import assert from 'node:assert/strict'
import { createServer } from 'node:http'
import { spawn } from 'node:child_process'
import { fileURLToPath } from 'node:url'

test('wait timeout covers an unfinished response body and closes the protocol session', { timeout: 10000 }, async () => {
  let deleteCount = 0
  let waitStarted = false
  const server = createServer(async (request, response) => {
    if (request.method === 'DELETE') {
      deleteCount += 1
      response.writeHead(200).end()
      return
    }
    if (request.method === 'GET') {
      response.writeHead(200, { 'Content-Type': 'text/event-stream' })
      response.write(': connected\n\n')
      return
    }
    let body = ''
    for await (const chunk of request) body += chunk
    const rpc = JSON.parse(body)
    if (rpc.id === undefined) { response.writeHead(202).end(); return }
    let result
    if (rpc.method === 'initialize') {
      response.setHeader('Mcp-Session-Id', 'local-timeout-test')
      result = { protocolVersion: '2025-11-25', capabilities: {}, serverInfo: { name: 'timeout-test', version: '1' } }
    } else if (rpc.method === 'tools/list') {
      result = { tools: ['get_agent_status', 'join_meeting', 'reply_to_mention', 'wait_for_mention'].map(name => ({ name })) }
    } else if (rpc.params.name === 'join_meeting') {
      result = { structuredContent: { succeeded: true, data: { connection: { connectionSessionId: 'test-connection' } } } }
    } else if (rpc.params.name === 'wait_for_mention') {
      waitStarted = true
      response.writeHead(200, { 'Content-Type': 'text/event-stream' })
      response.write(': headers and first body chunk arrived\n\n')
      return
    } else { response.writeHead(400).end(); return }
    response.writeHead(200, { 'Content-Type': 'application/json' })
    response.end(JSON.stringify({ jsonrpc: '2.0', id: rpc.id, result }))
  })
  await new Promise(resolve => server.listen(0, '127.0.0.1', resolve))
  const child = spawn(process.execPath, [fileURLToPath(new URL('./index.mjs', import.meta.url))], {
    env: { ...process.env, MCP_URL: `http://127.0.0.1:${server.address().port}/mcp`, MEETING_CODE: 'local-test-code', WAIT_REQUEST_TIMEOUT_MS: '250' },
    stdio: ['ignore', 'pipe', 'pipe'],
    windowsHide: true,
  })
  let output = ''
  child.stdout.on('data', chunk => { output += chunk })
  child.stderr.on('data', chunk => { output += chunk })
  try {
    const code = await new Promise((resolve, reject) => {
      const timer = setTimeout(() => reject(new Error('Client remained stuck after receiving response headers')), 5000)
      child.once('error', error => { clearTimeout(timer); reject(error) })
      child.once('exit', code => { clearTimeout(timer); resolve(code) })
    })
    assert.equal(waitStarted, true)
    assert.equal(code, 1)
    assert.equal(deleteCount, 1)
    assert.match(output, /failed request_aborted/)
    assert.match(output, /\bclosed\b/)
    assert.doesNotMatch(output, /local-test-code|local-timeout-test/)
  } finally {
    if (child.exitCode === null) child.kill()
    server.closeAllConnections()
    await new Promise(resolve => server.close(resolve))
  }
})
