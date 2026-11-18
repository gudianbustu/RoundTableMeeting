import { test } from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs/promises';
import os from 'node:os';
import path from 'node:path';
import { createServer } from 'node:http';
import { execFile } from 'node:child_process';
import { promisify } from 'node:util';
import { fileURLToPath } from 'node:url';
import { PassThrough } from 'node:stream';
import { createInterface } from 'node:readline';
import { Transport, MeetingClient, ClientError, listen } from '../public/agent/0.1.0/roundtable-mcp.mjs';

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

test('managed listening survives idle and a lost send response, then accepts the next decision', async t => {
  const client = await fixture(t);
  const input = new PassThrough();
  const lines = createInterface({ input });
  const events = [];
  let waits = 0;
  let sends = 0;
  let accepted;
  client.tool = async (name, args) => {
    if (name === 'get_submission') return { message: accepted };
    if (name === 'send_message') {
      sends++;
      assert.equal(args.request.replyToMessageId, 'one');
      assert.equal(args.request.visibility, undefined);
      accepted = { messageId: 'reply' };
      throw new ClientError('http_503');
    }
    assert.equal(name, 'wait_messages');
    assert.equal(args.mentionsOnly, true);
    if (++waits === 1) return { items: [], nextCursor: client.state.waitCursor, kind: 'idle' };
    const items = ['one', 'two'].map(messageId => ({
      message: { messageId, content: '私', visibility: 'private', author: { displayName: '甲' } },
      contentOffset: 0, totalContentCharacters: 1, complete: true,
    }));
    return { items, nextCursor: { afterSequence: 2, contentOffset: 0 }, kind: 'messages' };
  };
  await listen(client, lines, event => {
    events.push(event.event);
    if (event.event === 'message') {
      assert.equal(event.visibility, 'private');
      input.write(JSON.stringify({ command: event.messageId === 'one' ? 'reply' : 'skip',
        messageId: event.messageId, content: '收到' }) + '\n');
    } else if (event.messageId === 'two') lines.close();
  });
  assert.deepEqual(events, ['message', 'handled', 'message', 'handled']);
  assert.equal(sends, 1);
  assert.equal(waits, 2);
  assert.deepEqual(JSON.parse(await fs.readFile(client.file, 'utf8')).pendingMessages, {});
});

test('managed restart recovers a queued reply before delivering the next saved message', async t => {
  const client = await fixture(t);
  client.state.pendingMessages = { old: 'fixed', unread: 'other' };
  client.state.outbox.fixed = { status: 'pending', payload: { clientSubmissionId: 'fixed', replyToMessageId: 'old', content: '答复' } };
  client.state.inbox.unread = { messageId: 'unread', content: '你好' };
  await client.save();
  client.state = JSON.parse(await fs.readFile(client.file, 'utf8'));
  client.tool = async name => {
    assert.equal(name, 'get_submission');
    return { message: { messageId: 'sent' } };
  };
  const lines = createInterface({ input: new PassThrough() });
  const events = [];
  await listen(client, lines, event => {
    events.push([event.event, event.messageId]);
    if (event.event === 'message') lines.close();
  });
  assert.deepEqual(events, [['handled', 'old'], ['message', 'unread']]);
  assert.deepEqual(client.state.pendingMessages, { unread: 'other' });
});

test('expired listener reconnects on a new transport using the saved cursor', async t => {
  const client = await fixture(t);
  client.state.waitCursor = { afterSequence: 42, contentOffset: 0 };
  const calls = [];
  client.transport.call = async () => { throw new ClientError('connection_expired_reconnect'); };
  client.config.fetchImpl = async (_url, init) => {
    const rpc = JSON.parse(init.body);
    if (rpc.method === 'notifications/initialized') return new Response(null, { status: 202 });
    let result;
    if (rpc.method === 'initialize') result = { protocolVersion: '2024-11-05' };
    else if (rpc.method === 'tools/list') {
      result = { tools: ['join_meeting', 'wait_messages', 'read_messages', 'get_submission', 'send_message', 'renew_agent_lease'].map(name => ({ name })) };
    } else {
      const { name, arguments: args } = rpc.params;
      calls.push(name);
      let data;
      if (name === 'join_meeting') {
        assert.equal(args.joinMode, 'reconnect');
        data = { connectionSessionId: 'new' };
      } else {
        assert.equal(name, 'wait_messages');
        assert.equal(args.connectionSessionId, 'new');
        assert.equal(args.afterSequence, 42);
        data = { items: [{ message: { messageId: 'next', content: '问' }, complete: true, totalContentCharacters: 1, contentOffset: 0 }],
          nextCursor: { afterSequence: 43, contentOffset: 0 } };
      }
      result = { structuredContent: { succeeded: true, data } };
    }
    return Response.json({ id: rpc.id, result }, { headers: { 'mcp-session-id': 'new-transport' } });
  };
  const lines = createInterface({ input: new PassThrough() });
  await listen(client, lines, event => { assert.equal(event.messageId, 'next'); lines.close(); });
  assert.deepEqual(calls, ['join_meeting', 'wait_messages']);
  assert.equal(client.state.waitCursor.afterSequence, 43);
});

test('listener downloads without consuming the private message, then uploads and replies once', async t => {
  const client = await fixture(t);
  const bytes = Buffer.from([0, 1, 128, 255]);
  const filename = '报告 #1&2.bin';
  const target = path.join(client.directory, filename);
  const attempts = [];
  const server = createServer(async (req, res) => {
    assert.equal(req.headers.authorization, 'Bearer ticket');
    if (req.method === 'GET') { res.end(bytes); return; }
    const url = new URL(req.url, client.url);
    assert.equal(url.searchParams.get('name'), filename);
    const chunks = [];
    for await (const chunk of req) chunks.push(chunk);
    assert.deepEqual(Buffer.concat(chunks), bytes);
    attempts.push(url.searchParams.get('uploadRequestId'));
    if (attempts.length === 1) { res.writeHead(503).end(); return; }
    res.end(JSON.stringify({ attachmentId: 'outgoing' }));
  });
  await new Promise(resolve => server.listen(0, '127.0.0.1', resolve));
  t.after(() => server.close());
  client.url = `http://127.0.0.1:${server.address().port}/mcp`;
  client.state.pendingMessages = { question: 'submission' };
  client.state.inbox.question = { content: '请回传', visibility: 'private', attachments: [{ attachmentId: 'incoming' }] };
  const sent = [];
  client.tool = async (name, args) => {
    if (name === 'prepare_attachment_transfer') return { url: '/mcp-files', method: args.direction === 'upload' ? 'POST' : 'GET',
      maxFileBytes: 1000, bearerToken: 'ticket' };
    if (name === 'get_submission') return { message: null };
    assert.equal(name, 'send_message');
    sent.push(args.request);
    return { messageId: 'answer' };
  };
  const input = new PassThrough();
  const lines = createInterface({ input });
  const events = [];
  const command = args => input.write(JSON.stringify({ messageId: 'question', ...args }) + '\n');
  await listen(client, lines, event => {
    events.push(event.event);
    if (event.event === 'message') command({ command: 'download', attachmentId: 'wrong', output: target });
    else if (event.event === 'download_failed') {
      assert.equal(event.errorCode, 'attachment_not_in_message');
      command({ command: 'download', attachmentId: 'incoming', output: target });
    } else if (event.event === 'downloaded') command({ command: 'reply', files: [target] });
    else if (event.event === 'handled') lines.close();
  });
  assert.deepEqual(events, ['message', 'download_failed', 'downloaded', 'handled']);
  assert.deepEqual(await fs.readFile(target), bytes);
  assert.equal(attempts.length, 2);
  assert.ok(attempts[0]);
  assert.equal(attempts[0], attempts[1]);
  assert.deepEqual(sent, [{ content: '', clientSubmissionId: 'submission', replyToMessageId: 'question', attachmentIds: ['outgoing'] }]);
  assert.deepEqual(client.state.pendingMessages, {});
});

test('upload refuses cross-origin tickets and oversized files before transmitting bytes', async t => {
  const client = await fixture(t);
  const file = path.join(client.directory, 'result.bin');
  await fs.writeFile(file, 'abc');
  client.transport.fetch = () => assert.fail('should never send bytes');
  client.tool = async () => ({ url: 'https://other.invalid', method: 'POST', maxFileBytes: 3 });
  await assert.rejects(client.upload(file), /invalid_transfer_origin/);
  client.tool = async () => ({ url: '/mcp-files', method: 'POST', maxFileBytes: 2 });
  await assert.rejects(client.upload(file), /attachment_too_large/);
});

test('default download stays in the identity attachment directory and returns a distinct path', async t => {
  const client = await fixture(t);
  client.tool = async () => ({ url: '/mcp-files', method: 'GET', maxFileBytes: 10, bearerToken: 'ticket' });
  client.transport.fetch = async () => new Response('abc');
  const paths = [];
  for (let i = 0; i < 2; i++) {
    const saved = await client.download('a', undefined, '../../sensitive.txt');
    assert.equal(path.dirname(saved.path), path.join(client.directory, 'attachments'));
    assert.equal(await fs.readFile(saved.path, 'utf8'), 'abc');
    paths.push(saved.path);
  }
  assert.notEqual(paths[0], paths[1]);
  const nested = path.join(client.directory, 'new', 'nested', 'specified.txt');
  await client.download('a', nested);
  await assert.rejects(client.download('a', nested), { code: 'EEXIST' });
});

test('separate CLI invocations share one listener and redeliver the pending message', async t => {
  const dir = await fs.mkdtemp(path.join(os.tmpdir(), 'rtm-command-test-'));
  t.after(() => fs.rm(dir, { recursive: true, force: true }));
  const names = ['join_meeting', 'read_messages', 'wait_messages', 'send_message', 'get_submission', 'renew_agent_lease'];
  let joins = 0, sends = 0, leaves = 0, disconnected = false;
  const time = new Date().toISOString();
  const server = createServer(async (req, res) => {
    let body = ''; for await (const chunk of req) body += chunk;
    const rpc = JSON.parse(body);
    if (rpc.method === 'notifications/initialized') { res.writeHead(202).end(); return; }
    let result;
    if (rpc.method === 'initialize') result = { protocolVersion: '2024-11-05' };
    else if (rpc.method === 'tools/list') result = { tools: names.map(name => ({ name })) };
    else {
      const { name, arguments: args } = rpc.params;
      let data = {};
      if (name === 'join_meeting') { joins++; data = { participantId: 'p', connectionSessionId: 's' }; }
      if (name === 'wait_messages') {
        await new Promise(resolve => setTimeout(resolve, 50));
        const items = args.afterSequence ? [] : [{ message: { messageId: 'first', content: 'x', createdAt: time },
          complete: true, contentOffset: 0, totalContentCharacters: 1 }];
        data = { items, nextCursor: { afterSequence: 1, contentOffset: 0 }, kind: items.length ? 'messages' : 'idle' };
      }
      if (name === 'send_message') { sends++; data = { messageId: 'answer', createdAt: time }; }
      if (name === 'disconnect_agent') disconnected = true;
      if (name === 'leave_meeting') { leaves++; data = { membershipState: 'left' }; }
      result = { structuredContent: { succeeded: true, data } };
    }
    res.setHeader('content-type', 'application/json');
    res.end(JSON.stringify({ id: rpc.id, result }));
  });
  await new Promise(resolve => server.listen(0, '127.0.0.1', resolve));
  t.after(() => server.close());
  const config = path.join(dir, 'meeting.json');
  await fs.writeFile(config, JSON.stringify({ url: `http://127.0.0.1:${server.address().port}/mcp`,
    meetingCode: 'trial', displayName: 'test', stateDirectory: dir }));
  const script = fileURLToPath(new URL('../public/agent/0.1.0/roundtable-mcp.mjs', import.meta.url));
  const cli = async (...args) => JSON.parse((await promisify(execFile)(process.execPath, [script, config, ...args],
    { windowsHide: true, timeout: 15000 })).stdout);
  try {
    const [first, second] = await Promise.all([cli('next'), cli('next')]);
    assert.equal(joins, 1);
    assert.equal(first.messageId, 'first');
    assert.equal(second.messageId, first.messageId);
    assert.equal(first.createdAt, time);
    assert.ok(Date.parse(first.receivedAt) <= Date.parse(first.deliveredAt));
    await assert.rejects(cli('skip', 'old'));
    const ack = await cli('reply', 'first', '收到');
    assert.equal(ack.sentAt, time);
    assert.ok(Date.parse(ack.eventAt));
    assert.equal(sends, 1);
  } finally { assert.equal((await cli('stop')).event, 'stopped'); }
  assert.ok(disconnected);
  await cli('start');
  const waiting = cli('next').catch(error => JSON.parse(error.stdout));
  // Wait until the waiter has actually reached the listener.
  for (let i = 0; !(await cli('status')).waitingHost; i++) assert.ok(i < 20);
  assert.equal((await cli('leave')).event, 'left');
  assert.equal((await waiting).errorCode, 'listener_stopped');
  assert.equal((await cli('leave')).membershipState, 'left');
  assert.equal(leaves, 1);
  await assert.rejects(cli('next'), /explicit_rejoin_required/);
  assert.equal(joins, 2);
  assert.equal((await cli('rejoin')).event, 'ready');
  await cli('stop');
  await assert.rejects(fs.stat(path.join(dir, 'commands.json')), { code: 'ENOENT' });
  await assert.rejects(fs.stat(path.join(dir, 'client.lock')), { code: 'ENOENT' });
});

test('foreground relay without a host response releases the listener and preserves its pending message', async t => {
  const client = await fixture(t);
  client.state.pendingMessages = { pending: 'submission' };
  client.state.inbox.pending = { content: 'please respond' };
  const events = [];
  await listen(client, createInterface({ input: new PassThrough() }), event => events.push(event.event), { hostResponseTimeout: 30 });
  assert.deepEqual(events, ['message', 'host_unavailable']);
  assert.equal(client.state.pendingMessages.pending, 'submission');
});
