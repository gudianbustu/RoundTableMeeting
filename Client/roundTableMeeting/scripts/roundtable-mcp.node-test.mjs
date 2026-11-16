import { test } from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs/promises';
import os from 'node:os';
import path from 'node:path';
import { createServer } from 'node:http';
import { Transport, MeetingClient } from '../public/agent/0.1.0/roundtable-mcp.mjs';

async function fixture(t) {
  const directory = await fs.mkdtemp(path.join(os.tmpdir(), 'rtm-script-'));
  t.after(() => fs.rm(directory, { recursive: true, force: true }));
  const client = new MeetingClient({ url: 'http://localhost/mcp', meetingCode: 'test', displayName: 'test', stateDirectory: directory });
  client.state = { identity: { connectionSessionId: 'test' }, readCursor: { afterSequence: 0, contentOffset: 0 },
    waitCursor: { afterSequence: 0, contentOffset: 0 }, fragments: {}, inbox: {}, outbox: {} };
  return client;
}

test('SSE matches concurrent request IDs and returns before stream closes', async t => {
  const server = createServer(async (request, response) => {
    let body = ''; for await (const chunk of request) body += chunk;
    const input = JSON.parse(body);
    response.writeHead(200, { 'Content-Type': 'text/event-stream' });
    response.write(': keepalive\r\n\r\ndata: {"jsonrpc":"2.0","method":"notification"}\r\n\r\n');
    const result = `data: ${JSON.stringify({ jsonrpc: '2.0', id: input.id, result: input.params })}\r\n\r\n`;
    response.write(result.slice(0, 12));
    setTimeout(() => response.write(result.slice(12)), input.params.delay);
  });
  await new Promise(resolve => server.listen(0, '127.0.0.1', resolve));
  t.after(() => { server.closeAllConnections(); server.close(); });
  const transport = new Transport(`http://127.0.0.1:${server.address().port}`);
  const result = await Promise.all([transport.request('a', { delay: 30 }, 1000), transport.request('b', { delay: 1 }, 1000)]);
  assert.deepEqual(result, [{ delay: 30 }, { delay: 1 }]);
});

test('stalled SSE body observes request timeout', async t => {
  const server = createServer((request, response) => { response.writeHead(200, { 'Content-Type': 'text/event-stream' }); response.write(': wait\n\n'); });
  await new Promise(resolve => server.listen(0, '127.0.0.1', resolve));
  t.after(() => { server.closeAllConnections(); server.close(); });
  await assert.rejects(new Transport(`http://127.0.0.1:${server.address().port}`).request('test', {}, 50));
});

test('Unicode fragments survive restart without returning partial content', async t => {
  const client = await fixture(t);
  const message = { messageId: 'message', sequence: 1, content: '中🙂' };
  client.tool = async () => ({ items: [{ message, contentOffset: 0, totalContentCharacters: 3, complete: false }],
    nextCursor: { afterSequence: 0, contentOffset: 2 }, hasMore: true, kind: 'messages' });
  assert.equal((await client.receive()).items.length, 0);
  client.state = JSON.parse(await fs.readFile(client.file, 'utf8'));
  client.tool = async (name, args) => {
    assert.equal(args.contentOffset, 2);
    return { items: [{ message: { ...message, content: '文' }, contentOffset: 2, totalContentCharacters: 3, complete: true }],
      nextCursor: { afterSequence: 1, contentOffset: 0 }, hasMore: false, kind: 'messages' };
  };
  assert.equal((await client.receive()).items[0].content, '中🙂文');
  assert.equal(client.state.inbox.message.content, '中🙂文');
  assert.equal(client.state.waitCursor.afterSequence, 0);
});

test('invalid fragment does not advance state', async t => {
  const client = await fixture(t); const before = structuredClone(client.state);
  client.tool = async () => ({ items: [{ message: { messageId: 'm', content: 'x' }, contentOffset: 8, complete: false }], nextCursor: { afterSequence: 9 } });
  await assert.rejects(client.receive(), /fragment_offset_mismatch/);
  assert.deepEqual(client.state, before);
});

test('lost send response resolves by saved UUID without a second send', async t => {
  const client = await fixture(t); let serverMessage; let sends = 0;
  client.tool = async (name, args) => {
    if (name === 'get_submission') return { status: serverMessage ? 'accepted' : 'unknown', message: serverMessage ?? null };
    const disk = JSON.parse(await fs.readFile(client.file, 'utf8'));
    assert.deepEqual(disk.outbox[args.request.clientSubmissionId].payload, args.request);
    sends++; serverMessage = { messageId: 'accepted', content: args.request.content };
    throw new Error('lost_response');
  };
  await assert.rejects(client.send({ content: 'only once' }), /lost_response/);
  client.state = JSON.parse(await fs.readFile(client.file, 'utf8'));
  const id = Object.keys(client.state.outbox)[0];
  assert.equal((await client.retry(id)).messageId, 'accepted');
  assert.equal(sends, 1);
  await assert.rejects(client.send({ clientSubmissionId: id, content: 'different' }), /submission_payload_changed/);
});

test('failed local persistence prevents any send', async t => {
  const client = await fixture(t); let calls = 0;
  client.save = async () => { throw new Error('disk_full'); };
  client.tool = async () => { calls++; };
  await assert.rejects(client.send({ content: 'never sent', clientSubmissionId: 'fixed' }), /disk_full/);
  await assert.rejects(client.retry('fixed'), /disk_full/);
  assert.equal(calls, 0);
});

test('caller mutation cannot change a queued or persisted submission', async t => {
  const client = await fixture(t); let release;
  const hold = client.exclusive(() => new Promise(resolve => { release = resolve; }));
  await new Promise(resolve => setImmediate(resolve));
  const request = { content: '@x', mentionCandidates: [{ participantId: 'peer', startOffset: 0, length: 2 }] };
  client.tool = async (name, args) => {
    if (name === 'get_submission') { request.mentionCandidates[0].length = 100; return { message: null }; }
    const persisted = JSON.parse(await fs.readFile(client.file, 'utf8'));
    assert.equal(args.request.mentionCandidates[0].length, 2);
    assert.deepEqual(persisted.outbox[args.request.clientSubmissionId].payload, args.request);
    return { messageId: 'sent' };
  };
  const sending = client.send(request); request.mentionCandidates[0].length = 50;
  release(); await hold; await sending;
});

test('wait rejects zero seconds locally before calling server', async t => {
  const client = await fixture(t);
  client.tool = () => assert.fail('invalid wait must not be sent');
  await assert.rejects(client.receive(true, { timeoutSeconds: 0 }), /invalid_read_options/);
});

test('wait and read run serially while lease renewal can proceed', async t => {
  const client = await fixture(t); let finish; const events = [];
  client.tool = async name => {
    events.push(name);
    if (name === 'wait_messages') await new Promise(resolve => { finish = resolve; });
    return { items: [], nextCursor: { afterSequence: 0, contentOffset: 0 }, kind: 'idle', hasMore: false };
  };
  const waiting = client.receive(true); const reading = client.receive();
  await new Promise(resolve => setImmediate(resolve));
  await client.tool('renew_agent_lease');
  assert.deepEqual(events, ['wait_messages', 'renew_agent_lease']);
  finish(); await Promise.all([waiting, reading]);
  assert.equal(events.at(-1), 'read_messages');
});

test('download rejects cross-origin credentials without making a request', async t => {
  const client = await fixture(t);
  client.tool = async () => ({ url: 'https://elsewhere.invalid/file', method: 'GET', bearerToken: 'private' });
  client.transport.fetch = () => { assert.fail('must not forward credentials'); };
  await assert.rejects(client.download('file', path.join(client.directory, 'file')), /invalid_transfer_origin/);
});

test('an existing state lock is not stolen', async t => {
  const client = await fixture(t);
  await fs.writeFile(path.join(client.directory, 'client.lock'), 'owner');
  await assert.rejects(client.open('create'), /state_locked/);
  assert.equal(await fs.readFile(path.join(client.directory, 'client.lock'), 'utf8'), 'owner');
});

test('successful reconnect starts a new leave operation lifecycle', async t => {
  const client = await fixture(t); client.state = undefined;
  client.transport.initialize = async () => {};
  client.transport.call = async name => name === 'join_meeting' ? { connectionSessionId: 'old' } : {};
  await client.open('create');
  client.state.leaveOperationId = 'old-operation'; await client.save(); await client.close();
  const next = new MeetingClient(client.config);
  next.transport.initialize = async () => {};
  next.transport.call = async name => name === 'join_meeting' ? { connectionSessionId: 'new' } : {};
  try {
    await next.open('reconnect');
    const persisted = JSON.parse(await fs.readFile(next.file, 'utf8'));
    assert.equal(persisted.identity.connectionSessionId, 'new');
    assert.equal(persisted.leaveOperationId, undefined);
  } finally { await next.close(); }
});

test('join followed by failed persistence disconnects the new connection', async t => {
  const client = await fixture(t); client.state = undefined; const calls = [];
  client.transport.initialize = async () => {};
  client.transport.call = async name => { calls.push(name); return { connectionSessionId: 'new' }; };
  client.save = async () => { throw new Error('disk_full'); };
  await assert.rejects(client.open('create'), /disk_full/);
  assert.deepEqual(calls, ['join_meeting', 'disconnect_agent']);
  await assert.rejects(fs.stat(path.join(client.directory, 'client.lock')), { code: 'ENOENT' });
});
