// RoundTableMeeting MCP client 0.1.0 — Node.js 24, no dependencies.
import fs from 'node:fs/promises';
import path from 'node:path';
import os from 'node:os';
import { randomUUID, createHash } from 'node:crypto';
import { pathToFileURL } from 'node:url';
import { createInterface } from 'node:readline';
import { isDeepStrictEqual } from 'node:util';

export class ClientError extends Error {
  constructor(code) { super(code); this.code = code; }
}

export class Transport {
  constructor(url, fetchImpl = fetch) {
    this.url = url; this.fetch = fetchImpl; this.sequence = 0;
    this.session = null; this.version = '2024-11-05';
  }
  headers() {
    return { 'Content-Type': 'application/json', Accept: 'application/json, text/event-stream',
      'MCP-Protocol-Version': this.version,
      ...(this.session ? { 'Mcp-Session-Id': this.session } : {}) };
  }
  async request(method, params, timeout = 20000, notification = false) {
    const id = ++this.sequence;
    const response = await this.fetch(this.url, {
      method: 'POST', headers: this.headers(), redirect: 'error',
      body: JSON.stringify({ jsonrpc: '2.0', ...(!notification ? { id } : {}), method, params }),
      signal: AbortSignal.timeout(timeout),
    });
    if (!response.ok) { await response.body?.cancel(); throw new ClientError(`http_${response.status}`); }
    const session = response.headers.get('mcp-session-id');
    if (session) this.session = session;
    if (notification) { await response.body?.cancel(); return; }
    const accept = value => {
      if (value.id !== id) return undefined;
      if (value.error) throw new ClientError(`rpc_${value.error.code}`);
      if (!Object.hasOwn(value, 'result')) throw new ClientError('invalid_rpc_response');
      return value;
    };
    if (response.headers.get('content-type')?.includes('text/event-stream')) {
      const reader = response.body.getReader();
      const decoder = new TextDecoder(); let buffer = '';
      try {
        for (;;) {
          const { value, done } = await reader.read();
          buffer += decoder.decode(value, { stream: !done });
          let boundary;
          while ((boundary = /\r?\n\r?\n/.exec(buffer))) {
            const event = buffer.slice(0, boundary.index);
            buffer = buffer.slice(boundary.index + boundary[0].length);
            const data = event.split(/\r?\n/).filter(line => line.startsWith('data:'))
              .map(line => line.slice(5).replace(/^ /, '')).join('\n');
            if (!data) continue;
            const match = accept(JSON.parse(data));
            if (match) return match.result;
          }
          if (done) throw new ClientError('incomplete_sse_response');
        }
      } finally { await reader.cancel().catch(() => {}); }
    }
    const match = accept(await response.json());
    if (!match) throw new ClientError('rpc_id_mismatch');
    return match.result;
  }
  async initialize() {
    const result = await this.request('initialize', { protocolVersion: this.version,
      capabilities: {}, clientInfo: { name: 'roundtable-official-script', version: '0.1.0' } });
    this.version = result.protocolVersion;
    await this.request('notifications/initialized', {}, 20000, true);
    const catalog = await this.request('tools/list', {});
    const names = new Set(catalog.tools.map(tool => tool.name));
    for (const name of ['join_meeting', 'read_messages', 'wait_messages', 'send_message', 'get_submission', 'renew_agent_lease']) {
      if (!names.has(name)) throw new ClientError('unsupported_server_protocol');
    }
    return result;
  }
  async call(name, args, timeout = 20000) {
    const result = await this.request('tools/call', { name, arguments: args }, timeout);
    const value = result.structuredContent ?? JSON.parse(result.content.find(item => item.type === 'text').text);
    if (result.isError || value.succeeded !== true) throw new ClientError(value.errorCode ?? 'tool_failed');
    if (value.protocolVersion && value.protocolVersion !== 'participant-sequence-v2') throw new ClientError('unsupported_message_protocol');
    return value.data;
  }
}

const zero = () => ({ afterSequence: 0, contentOffset: 0 });
export class MeetingClient {
  constructor(config) {
    const url = new URL(config.url);
    if (!['http:', 'https:'].includes(url.protocol) || url.username || url.password) throw new ClientError('invalid_server_url');
    if (!config.meetingCode || !config.displayName) throw new ClientError('missing_meeting_code_or_name');
    this.config = config; this.url = url.href;
    const key = createHash('sha256').update(JSON.stringify([this.url, config.meetingCode, config.displayName])).digest('hex').slice(0, 24);
    this.directory = config.stateDirectory ?? path.join(os.homedir(), '.roundtable-meeting', key);
    this.file = path.join(this.directory, 'state.json');
    this.transport = new Transport(this.url, config.fetchImpl);
    this.queue = Promise.resolve(); this.stopped = false;
  }
  exclusive(action) {
    const result = this.queue.then(action);
    this.queue = result.catch(() => {}); return result;
  }
  async save() {
    const temporary = `${this.file}.tmp`;
    const handle = await fs.open(temporary, 'w', 0o600);
    try { await handle.writeFile(JSON.stringify(this.state)); await handle.sync(); }
    finally { await handle.close(); }
    await fs.rename(temporary, this.file);
  }
  async open(joinMode) {
    await fs.mkdir(this.directory, { recursive: true, mode: 0o700 });
    try { this.lock = await fs.open(path.join(this.directory, 'client.lock'), 'wx', 0o600); }
    catch (error) { if (error.code === 'EEXIST') throw new ClientError('state_locked'); throw error; }
    let joined = false;
    try {
      try { this.state = JSON.parse(await fs.readFile(this.file, 'utf8')); }
      catch (error) { if (error.code !== 'ENOENT') throw error; }
      this.state ??= { version: 1, url: this.url, displayName: this.config.displayName,
        meetingKey: createHash('sha256').update(this.config.meetingCode).digest('hex'),
        readCursor: zero(), waitCursor: zero(), fragments: {}, inbox: {}, outbox: {} };
      if (this.state.version !== 1 || this.state.url !== this.url || this.state.displayName !== this.config.displayName ||
          this.state.meetingKey !== createHash('sha256').update(this.config.meetingCode).digest('hex')) throw new ClientError('state_identity_mismatch');
      if (this.state.identity && !joinMode) throw new ClientError('explicit_reconnect_required');
      await this.transport.initialize();
      this.state.identity = await this.transport.call('join_meeting', {
        meetingCode: this.config.meetingCode, displayName: this.config.displayName, joinMode: joinMode ?? 'create',
      });
      joined = true;
      delete this.state.leaveOperationId;
      await this.save();
      this.timer = setInterval(() => {
        if (this.renewing || this.stopped) return;
        this.renewing = true;
        this.tool('renew_agent_lease').then(() => { this.leaseError = null; }, error => { this.leaseError = error.code ?? 'renew_failed'; })
          .finally(() => { this.renewing = false; });
      }, 60000);
      this.timer.unref();
      return this.state.identity;
    } catch (error) {
      if (joined) await this.tool('disconnect_agent').catch(() => {});
      await this.release(); throw error;
    }
  }
  tool(name, args = {}, timeout) {
    if (this.stopped) throw new ClientError('client_stopped');
    return this.transport.call(name, { ...args, connectionSessionId: this.state.identity.connectionSessionId }, timeout);
  }
  async receive(wait = false, options = {}) {
    return this.exclusive(async () => {
      const cursorKey = wait ? 'waitCursor' : 'readCursor';
      const budget = options.characterBudget ?? 100000;
      const seconds = options.timeoutSeconds ?? 25;
      if (!Number.isInteger(budget) || budget < 1 || budget > 100000 || (wait && (!Number.isInteger(seconds) || seconds < 1 || seconds > 60))) throw new ClientError('invalid_read_options');
      const page = await this.tool(wait ? 'wait_messages' : 'read_messages', {
        ...this.state[cursorKey], characterBudget: budget, mentionsOnly: wait,
        ...(wait ? { timeoutSeconds: seconds } : {}),
      }, (wait ? seconds * 1000 : 0) + 20000);
      // Validate the whole page on a copy so a malformed fragment cannot advance a cursor.
      const next = structuredClone(this.state); const complete = [];
      for (const item of page.items) {
        const message = item.message; const key = `${cursorKey}:${message.messageId}`;
        const previous = next.fragments[key] ?? '';
        if ([...previous].length !== item.contentOffset) throw new ClientError('fragment_offset_mismatch');
        const content = previous + message.content;
        if (item.complete) {
          if ([...content].length !== item.totalContentCharacters) throw new ClientError('fragment_length_mismatch');
          const full = { ...message, content };
          next.inbox[message.messageId] = full; complete.push(full); delete next.fragments[key];
        } else next.fragments[key] = content;
      }
      next[cursorKey] = page.nextCursor;
      const old = this.state; this.state = next;
      try { await this.save(); } catch (error) { this.state = old; throw error; }
      return { items: complete, nextCursor: page.nextCursor, hasMore: page.hasMore, kind: page.kind,
        partial: page.items.some(item => !item.complete) };
    });
  }
  async send(request) {
    const snapshot = structuredClone(request);
    return this.exclusive(async () => {
      const payload = { ...snapshot, clientSubmissionId: snapshot.clientSubmissionId ?? randomUUID() };
      const id = payload.clientSubmissionId;
      const existing = this.state.outbox[id];
      if (existing && !isDeepStrictEqual(existing.payload, payload)) throw new ClientError('submission_payload_changed');
      if (!existing) this.state.outbox[id] = { payload, status: 'pending' };
      await this.save();
      return this.submit(id);
    });
  }
  async submit(id) {
    const entry = this.state.outbox[id];
    if (!entry) throw new ClientError('unknown_local_submission');
    await this.save();
    if (entry.status === 'sent') return entry.result;
    const known = await this.tool('get_submission', { clientSubmissionId: id });
    const message = known.message ?? await this.tool('send_message', { request: entry.payload });
    entry.status = 'sent'; entry.result = message; await this.save(); return message;
  }
  retry(id) { return this.exclusive(() => this.submit(id)); }
  async download(attachmentId, output) {
    const ticket = await this.tool('prepare_attachment_transfer', { direction: 'download', attachmentId });
    const url = new URL(ticket.url, new URL(this.url).origin);
    if (url.origin !== new URL(this.url).origin || ticket.method !== 'GET') throw new ClientError('invalid_transfer_origin');
    const response = await this.transport.fetch(url, { headers: { Authorization: `Bearer ${ticket.bearerToken}` },
      redirect: 'error', signal: AbortSignal.timeout(120000) });
    if (!response.ok) { await response.body?.cancel(); throw new ClientError(`download_http_${response.status}`); }
    const target = path.resolve(output); const temporary = `${target}.${randomUUID()}.part`;
    const handle = await fs.open(temporary, 'wx', 0o600); let bytes = 0;
    try {
      for await (const chunk of response.body) { bytes += chunk.length;
        if (bytes > ticket.maxFileBytes) throw new ClientError('attachment_too_large');
        await handle.writeFile(chunk);
      }
      await handle.close();
      await fs.link(temporary, target); // Never overwrite an existing local file.
      return { path: target, bytes };
    } finally { await handle.close().catch(() => {}); await fs.unlink(temporary).catch(() => {}); }
  }
  async release() {
    clearInterval(this.timer);
    if (this.lock) { await this.lock.close(); this.lock = null; await fs.unlink(path.join(this.directory, 'client.lock')); }
  }
  async close() {
    if (this.closing) return this.closing;
    this.closing = this.finishClose();
    return this.closing;
  }
  async finishClose() {
    clearInterval(this.timer);
    try { await this.queue; if (this.state?.identity && !this.stopped) await this.tool('disconnect_agent'); }
    finally { this.stopped = true; await this.release(); }
  }
}

async function main() {
  if (!process.argv[2]) throw new ClientError('usage_node_roundtable_mcp_config_json');
  const config = JSON.parse(await fs.readFile(process.argv[2], 'utf8'));
  const client = new MeetingClient(config);
  await client.open(config.joinMode);
  const output = value => process.stdout.write(`${JSON.stringify(value)}\n`);
  output({ event: 'ready', participantId: client.state.identity.participantId });
  const lines = createInterface({ input: process.stdin, crlfDelay: Infinity });
  const stop = async () => { lines.close(); await client.close(); };
  process.once('SIGINT', () => { void stop(); });
  process.once('SIGTERM', () => { void stop(); });
  try {
    for await (const line of lines) {
      let input;
      try {
        input = JSON.parse(line); let result;
        switch (input.command) {
          case 'info': result = await client.tool('get_meeting_info'); break;
          case 'read': result = await client.receive(false, input); break;
          case 'wait': result = await client.receive(true, input); break;
          case 'inbox': result = Object.values(client.state.inbox); break;
          case 'outbox': result = client.state.outbox; break;
          case 'send': result = await client.send(input.request); break;
          case 'retry': result = await client.retry(input.clientSubmissionId); break;
          case 'renew': result = await client.tool('renew_agent_lease'); break;
          case 'attachments': result = await client.tool('list_attachments', input.options); break;
          case 'download': result = await client.download(input.attachmentId, input.output); break;
          case 'status': result = await client.transport.call('get_meeting_status', {
            meetingId: client.state.identity.meetingId, lifecycleProof: client.state.identity.lifecycleProof }); break;
          case 'leave':
            if (input.confirm !== 'user_requested_leave') throw new ClientError('explicit_leave_confirmation_required');
            client.state.leaveOperationId ??= randomUUID(); await client.save();
            result = await client.tool('leave_meeting', { operationId: client.state.leaveOperationId });
            client.stopped = true; clearInterval(client.timer); break;
          case 'disconnect': await stop(); result = { disconnected: true }; break;
          default: throw new ClientError('unknown_command');
        }
        output({ id: input.id, succeeded: true, result });
      } catch (error) { output({ id: input?.id, succeeded: false, errorCode: error.code ?? error.name }); }
    }
  } finally { await client.close(); }
}

if (typeof process !== 'undefined' && process.argv[1] && import.meta.url === pathToFileURL(path.resolve(process.argv[1])).href) {
  main().catch(error => { process.stderr.write(`${error.code ?? error.name}\n`); process.exitCode = 1; });
}
