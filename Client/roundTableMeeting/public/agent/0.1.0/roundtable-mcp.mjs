// RoundTableMeeting MCP client 0.1.0 — Node.js >=20.3.0, no dependencies.
import { setTimeout as delay } from 'node:timers/promises';
import fs from 'node:fs/promises';
import { fork } from 'node:child_process';
import { createServer } from 'node:http';
import { PassThrough } from 'node:stream';
import { openAsBlob } from 'node:fs';
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
  async request(method, params, timeout = 20000, notification = false, signal) {
    const id = ++this.sequence;
    const response = await this.fetch(this.url, {
      method: 'POST', headers: this.headers(), redirect: 'error',
      body: JSON.stringify({ jsonrpc: '2.0', ...(!notification ? { id } : {}), method, params }),
      signal: signal ? AbortSignal.any([signal, AbortSignal.timeout(timeout)]) : AbortSignal.timeout(timeout),
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
  async call(name, args, timeout = 20000, signal) {
    const result = await this.request('tools/call', { name, arguments: args }, timeout, false, signal);
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
  async open(joinMode, protectMembership = false) {
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
      if (protectMembership || joinMode === 'auto') {
        if (this.state.membershipLeft) throw new ClientError('explicit_rejoin_required');
        if (this.state.leaveOperationId) throw new ClientError('leave_outcome_unknown');
      }
      if (joinMode === 'auto') joinMode = this.state.identity ? 'reconnect' : 'create';
      if (this.state.identity && !joinMode) throw new ClientError('explicit_reconnect_required');
      await this.transport.initialize();
      this.state.identity = await this.transport.call('join_meeting', {
        meetingCode: this.config.meetingCode, displayName: this.config.displayName, joinMode: joinMode ?? 'create',
      });
      joined = true;
      delete this.state.membershipLeft;
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
  tool(name, args = {}, timeout, signal) {
    if (this.stopped) throw new ClientError('client_stopped');
    return this.transport.call(name, { ...args, connectionSessionId: this.state.identity.connectionSessionId }, timeout, signal);
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
      }, (wait ? seconds * 1000 : 0) + 20000, options.signal);
      if (!page.items.length && isDeepStrictEqual(page.nextCursor, this.state[cursorKey])) {
        return { items: [], nextCursor: page.nextCursor, hasMore: page.hasMore, kind: page.kind, partial: false };
      }
      // Validate the whole page on a copy so a malformed fragment cannot advance a cursor.
      const next = structuredClone(this.state); const complete = [];
      let discussionBatch = [];
      for (const item of page.items) {
        const message = item.message; const key = `${cursorKey}:${message.messageId}`;
        const previous = next.fragments[key] ?? '';
        if ([...previous].length !== item.contentOffset) throw new ClientError('fragment_offset_mismatch');
        const content = previous + message.content;
        if (item.complete) {
          if ([...content].length !== item.totalContentCharacters) throw new ClientError('fragment_length_mismatch');
          const full = { ...message, content };
          if (wait && options.managed) {
            next.receivedAt ??= {};
            next.receivedAt[message.messageId] ??= new Date().toISOString();
            next.pendingMessages ??= {};
            next.pendingMessages[message.messageId] ??= randomUUID();
            const topic = item.discussionTopicId;
            if (message.optionalAgentResponse && topic && topic !== message.messageId) {
              const first = discussionBatch[0];
              if (first && (discussionBatch.length >= 4 || first.topic !== topic || Date.parse(message.createdAt) - Date.parse(first.message.createdAt) > 750)) discussionBatch = [];
              if (discussionBatch.length) {
                full.discussionContext = discussionBatch.map(entry => entry.message);
                delete next.pendingMessages[discussionBatch.at(-1).message.messageId];
              }
              discussionBatch.push({ topic, message });
            } else {
              discussionBatch = [];
            }
          }
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
  async upload(file, submission = null, recover = action => action()) {
    if (typeof file !== 'string' || !path.isAbsolute(file)) throw new ClientError('absolute_file_path_required');
    return this.exclusive(async () => {
      const stat = await fs.stat(file);
      if (!stat.isFile()) throw new ClientError('regular_file_required');
      const prepare = () => this.tool('prepare_attachment_transfer', { direction: 'upload' });
      const uploads = this.state.uploads ??= {};
      const name = path.basename(file);
      const reusable = submission && Object.values(uploads).some(entry => entry.submission === submission &&
        entry.attachment && entry.name === name && entry.bytes === stat.size);
      let firstTicket = reusable ? null : await recover(prepare);
      const body = await openAsBlob(file);
      if (firstTicket && body.size > firstTicket.maxFileBytes) throw new ClientError('attachment_too_large');
      // A file-backed Blob fails reads if its underlying file changes between hashing and upload.
      const digest = createHash('sha256');
      for await (const chunk of body.stream()) digest.update(chunk);
      const key = JSON.stringify([submission, name, digest.digest('hex')]);
      const entry = uploads[key] ??= { requestId: randomUUID(), submission, name, bytes: body.size };
      if (entry.attachment) return entry.attachment;
      await this.save();
      const attachment = await recover(async () => {
        const ticket = firstTicket ?? await prepare();
        firstTicket = null;
        const url = new URL(ticket.url, new URL(this.url).origin);
        if (url.origin !== new URL(this.url).origin || ticket.method !== 'POST') throw new ClientError('invalid_transfer_origin');
        if (body.size > ticket.maxFileBytes) throw new ClientError('attachment_too_large');
        url.searchParams.set('name', name);
        url.searchParams.set('uploadRequestId', entry.requestId);
        const response = await this.transport.fetch(url, { method: 'POST', body, redirect: 'error',
          headers: { Authorization: `Bearer ${ticket.bearerToken}`, 'Content-Type': 'application/octet-stream' },
          signal: AbortSignal.timeout(120000) });
        if (!response.ok) { await response.body?.cancel(); throw new ClientError(`upload_http_${response.status}`); }
        const result = await response.json();
        if (!result?.attachmentId) throw new ClientError('invalid_upload_response');
        return result;
      });
      if (submission) entry.attachment = attachment;
      else delete uploads[key];
      await this.save();
      return attachment;
    });
  }
  async download(attachmentId, output, name = 'attachment') {
    if (output === undefined) {
      const safeName = path.win32.basename(String(name)).replace(/[<>:"/\\|?*\x00-\x1f]/g, '_')
        .slice(-90).replace(/[. ]+$/, '') || 'attachment';
      output = path.join(this.directory, 'attachments', `${randomUUID()}-${safeName}`);
    }
    const target = path.resolve(output);
    await fs.mkdir(path.dirname(target), { recursive: true, mode: 0o700 });
    const ticket = await this.tool('prepare_attachment_transfer', { direction: 'download', attachmentId });
    const url = new URL(ticket.url, new URL(this.url).origin);
    if (url.origin !== new URL(this.url).origin || ticket.method !== 'GET') throw new ClientError('invalid_transfer_origin');
    const response = await this.transport.fetch(url, { headers: { Authorization: `Bearer ${ticket.bearerToken}` },
      redirect: 'error', signal: AbortSignal.timeout(120000) });
    if (!response.ok) { await response.body?.cancel(); throw new ClientError(`download_http_${response.status}`); }
    const temporary = `${target}.${randomUUID()}.part`;
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
  async leave() {
    return this.exclusive(async () => {
      this.state.leaveOperationId ??= randomUUID();
      await this.save();
      const receipt = await this.tool('leave_meeting', { operationId: this.state.leaveOperationId });
      if (receipt.membershipState !== 'left') throw new ClientError('leave_unconfirmed');
      clearInterval(this.timer);
      this.stopped = true;
      this.state.membershipLeft = receipt;
      this.state.pendingMessages = {};
      this.state.receivedAt = {};
      await this.save();
      return receipt;
    });
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

// Only network failures may be retried automatically. An online identity conflict needs its owner.
async function withRecovery(client, action, signal) {
  let reconnect = false;
  let backoff = 500;
  while (!signal.aborted) {
    try {
      if (reconnect) {
        const transport = new Transport(client.url, client.config.fetchImpl);
        await transport.initialize();
        const identity = await transport.call('join_meeting', {
          meetingCode: client.config.meetingCode, displayName: client.config.displayName, joinMode: 'reconnect',
        });
        client.transport = transport;
        client.state.identity = identity;
        await client.save();
        reconnect = false;
      }
      return await action();
    } catch (error) {
      if (['connection_expired_reconnect', 'connection_unavailable', 'http_404'].includes(error.code)) reconnect = true;
      else if (!(error.name === 'TimeoutError' || error instanceof TypeError && error.cause ||
          ['incomplete_sse_response', 'http_429', 'http_502', 'http_503', 'http_504', 'upload_http_502', 'upload_http_503', 'upload_http_504',
            'download_http_502', 'download_http_503', 'download_http_504', 'rate_limited'].includes(error.code))) throw error;
      await delay(backoff, undefined, { signal });
      backoff = Math.min(backoff * 2, 10000);
    }
  }
  signal.throwIfAborted();
}

export async function listen(client, lines, emit, { hostResponseTimeout = 0 } = {}) {
  const output = event => emit({ ...event, eventAt: new Date().toISOString() });
  const controller = new AbortController();
  const { signal } = controller;
  const stop = () => { controller.abort(); lines.close(); };
  lines.once('close', stop);
  process.once('SIGTERM', stop);
  process.once('SIGINT', stop);
  const inputs = lines[Symbol.asyncIterator]();
  const recover = action => withRecovery(client, action, signal);
  try {
    while (!signal.aborted) {
      const id = Object.keys(client.state.pendingMessages ?? {})[0];
      if (!id) {
        await recover(() => client.receive(true, { managed: true, signal }));
        continue;
      }
      const submission = client.state.pendingMessages[id];
      let submit;
      let interrupted = false;
      if (client.state.outbox[submission]) {
        submit = () => client.retry(submission);
      } else {
        const message = client.state.inbox[id];
        if (message.optionalAgentResponse && !(await recover(() => client.tool('get_message', { messageId: id }))).optionalAgentResponse) {
          delete client.state.pendingMessages[id];
          if (client.state.receivedAt) delete client.state.receivedAt[id];
          await client.save();
          continue;
        }
        let quoted;
        if (message.replyToMessageId) {
          try {
            const original = await recover(() => client.tool('get_message', { messageId: message.replyToMessageId }));
            const characters = Array.from(original.content);
            quoted = { from: original.author.displayName, content: characters.slice(0, 1000).join(''),
              truncated: characters.length > 1000 };
          } catch (error) { if (error.code !== 'message_not_found') throw error; }
        }
        if (message.author?.kind === 'human' && client.creatorId === undefined) {
          const info = await recover(() => client.tool('get_meeting_info'));
          client.creatorId = info.participants.find(member => member.isCreator)?.participantId ?? null;
        }
        output({ event: 'message', messageId: id, from: message.author?.displayName,
          fromMeetingCreator: Boolean(client.creatorId && client.creatorId === message.author?.participantId),
          createdAt: message.createdAt, receivedAt: client.state.receivedAt?.[id],
          optionalAgentResponse: message.optionalAgentResponse === true, discussionContext: message.discussionContext,
          visibility: message.visibility, content: message.content, attachments: message.attachments,
          replyToMessageId: message.replyToMessageId, quoted });
        let handled = false;
        while (!handled && !signal.aborted) {
          // A foreground relay can keep stdin open after its model has gone away.
          // Apply this deadline only to an outstanding delivery, never to an empty meeting.
          const orphan = hostResponseTimeout && setTimeout(() => {
            output({ event: 'host_unavailable', messageId: id });
            stop();
          }, hostResponseTimeout);
          let next;
          try { next = await inputs.next(); } finally { clearTimeout(orphan); }
          if (next.done) return;
          let input;
          try { input = JSON.parse(next.value); } catch { output({ errorCode: 'invalid_json' }); continue; }
          if (input?.messageId !== id) { output({ errorCode: 'expected_current_message', messageId: id }); continue; }
          if (input.command === 'download') {
            try {
              const attachment = message.attachments?.find(item => item.attachmentId === input.attachmentId);
              if (!attachment) throw new ClientError('attachment_not_in_message');
              if (input.output !== undefined && (typeof input.output !== 'string' || !path.isAbsolute(input.output))) throw new ClientError('absolute_output_path_required');
              const result = await recover(() => client.download(input.attachmentId, input.output, attachment.name));
              output({ event: 'downloaded', messageId: id, attachmentId: input.attachmentId, ...result });
            } catch (error) { output({ event: 'download_failed', messageId: id, errorCode: error.code ?? error.name }); }
            continue;
          }
          if (input.command === 'reply') {
            const target = input.replyToMessageId ?? id;
            if (target !== id && !message.discussionContext?.some(item => item.messageId === target)) {
              output({ errorCode: 'invalid_reply_target', messageId: id }); continue;
            }
            const files = input.files ?? [];
            const content = input.content ?? '';
            if (!Array.isArray(files) || files.some(file => typeof file !== 'string' || !path.isAbsolute(file)) ||
                typeof content !== 'string' || (!content.trim() && !files.length)) {
              output({ errorCode: 'invalid_reply', messageId: id }); continue;
            }
            const attachmentIds = [];
            try {
              for (const file of files) {
                const attachment = await client.upload(file, submission, recover);
                if (!attachmentIds.includes(attachment.attachmentId)) attachmentIds.push(attachment.attachmentId);
              }
            } catch (error) { output({ event: 'upload_failed', messageId: id, errorCode: error.code ?? error.name }); continue; }
            submit = () => client.send({ content, clientSubmissionId: submission, replyToMessageId: target,
              ...(attachmentIds.length ? { attachmentIds } : {}) });
          } else if (input.command !== 'skip') {
            output({ errorCode: 'expected_reply_or_skip', messageId: id }); continue;
          }
          handled = true;
        }
        if (!handled) return;
      }
      let sentAt;
      if (submit) {
        try { sentAt = (await recover(submit))?.createdAt; } catch (error) {
          if (error.code === 'discussion_ended') {
            client.state.outbox[submission].status = 'rejected';
            interrupted = true;
          } else {
            if (!['validation_failed', 'recipient_unavailable', 'reply_scope_mismatch', 'message_not_found', 'attachment_unavailable'].includes(error.code)) throw error;
            client.state.outbox[submission].status = 'rejected';
            client.state.pendingMessages[id] = randomUUID();
            await client.save();
            output({ event: 'reply_rejected', messageId: id, errorCode: error.code });
            continue;
          }
        }
      }
      for (const [key, upload] of Object.entries(client.state.uploads ?? {})) {
        if (upload.submission === submission) delete client.state.uploads[key];
      }
      if (client.state.receivedAt) delete client.state.receivedAt[id];
      delete client.state.pendingMessages[id];
      await client.save();
      output({ event: interrupted ? 'discussion_ended' : 'handled', messageId: id, sentAt });
    }
  } catch (error) {
    if (!signal.aborted) throw error;
  } finally {
    process.off('SIGTERM', stop);
    process.off('SIGINT', stop);
    lines.off('close', stop);
    lines.close();
  }
}

// A single identity owns the MCP connection. Short-lived CLI processes only talk to this loopback endpoint.
async function serveCommands(client, rejoin) {
  await client.open(rejoin ? 'reconnect' : client.config.joinMode ?? 'auto', !rejoin);
  const endpointFile = path.join(client.directory, 'commands.json');
  const token = randomUUID();
  const input = new PassThrough();
  const lines = createInterface({ input });
  const waiting = new Set();
  let current, active, stopResponse, leaveRequested = false;
  let lastConsumer = Date.now();
  const watchdog = setInterval(() => {
    if (waiting.size || active || stopResponse) return;
    if (Date.now() - lastConsumer >= 300000) lines.close();
  }, 1000);
  watchdog.unref();
  function finish(res, value) {
    if (!res.destroyed) res.end(JSON.stringify(value));
  }
  function deliver(res) {
    finish(res, { ...current, deliveredAt: new Date().toISOString() });
  }
  function onEvent(event) {
    if (event.event === 'message') {
      current = event;
      for (const res of waiting) deliver(res);
      waiting.clear();
    } else {
      if (event.event === 'handled' || event.event === 'discussion_ended' || event.event === 'reply_rejected') current = null;
      if (active) finish(active, event);
      active = null;
      lastConsumer = Date.now();
      if (current) {
        for (const res of waiting) deliver(res);
        waiting.clear();
      }
    }
  }
  const server = createServer(async (req, res) => {
    res.setHeader('Content-Type', 'application/json');
    res.setHeader('Cache-Control', 'no-store');
    if (req.method !== 'POST' || req.url !== '/' || req.headers.authorization !== `Bearer ${token}` || req.headers.origin) {
      res.writeHead(403).end(); return;
    }
    try {
      const parts = []; let size = 0;
      for await (const part of req) {
        size += part.length;
        if (size > 8 * 1024 * 1024) throw new ClientError('command_too_large');
        parts.push(part);
      }
      const cmd = JSON.parse(Buffer.concat(parts).toString('utf8'));
      if (stopResponse) throw new ClientError('listener_stopping');
      if (cmd.command === 'status') return finish(res, { event: 'status', waitingHost: waiting.size > 0,
        commandInProgress: Boolean(active), pendingMessageId: current?.messageId ?? null });
      lastConsumer = Date.now();
      if (cmd.command === 'start' || cmd.command === 'rejoin') return finish(res, { event: 'ready', participantId: client.state.identity.participantId });
      if (cmd.command === 'stop' || cmd.command === 'leave') {
        if (active) throw new ClientError('command_in_progress');
        leaveRequested = cmd.command === 'leave';
        stopResponse = res; lines.close(); return;
      }
      // Whitespace keeps a blocking JSON response alive across long idle periods.
      res.flushHeaders();
      const keepalive = setInterval(() => res.write(' '), 30000);
      res.once('close', () => clearInterval(keepalive));
      if (cmd.command === 'next') {
        const candidate = current;
        if (candidate?.optionalAgentResponse && !active) {
          const live = await client.tool('get_message', { messageId: candidate.messageId });
          if (current === candidate && !active && !live.optionalAgentResponse) {
            current = null;
            input.write(JSON.stringify({ command: 'skip', messageId: candidate.messageId }) + '\n');
          }
        }
        if (current && !active) return deliver(res);
        waiting.add(res); res.once('close', () => { waiting.delete(res); lastConsumer = Date.now(); }); return;
      }
      if (!['download', 'reply', 'skip'].includes(cmd.command)) throw new ClientError('unknown_command');
      if (!current || current.messageId !== cmd.messageId) throw new ClientError('expected_current_message');
      if (active) throw new ClientError('command_in_progress');
      active = res;
      input.write(JSON.stringify(cmd) + '\n');
    } catch (error) { finish(res, { errorCode: error.code ?? 'invalid_command' }); }
  });
  try {
    await new Promise((resolve, reject) => { server.once('error', reject); server.listen(0, '127.0.0.1', resolve); });
    const endpoint = { pid: process.pid, port: server.address().port, token, identity: commandIdentity(client) };
    const temp = endpointFile + '.tmp';
    await fs.writeFile(temp, JSON.stringify(endpoint), { mode: 0o600 });
    await fs.rename(temp, endpointFile);
    if (process.connected) process.send({ ready: true }, () => {
      if (process.connected) process.disconnect();
    });
    await listen(client, lines, onEvent);
  } finally {
    clearInterval(watchdog);
    for (const res of waiting) finish(res, { errorCode: 'listener_stopped' });
    if (active) finish(active, { errorCode: 'listener_stopped' });
    server.close();
    lines.close();
    await fs.rm(endpointFile, { force: true });
    try {
      let receipt;
      try { if (leaveRequested) receipt = await client.leave(); }
      finally { await client.close(); }
      if (stopResponse) finish(stopResponse, receipt ? { event: 'left', ...receipt } : { event: 'stopped' });
    } catch (error) {
      if (stopResponse) finish(stopResponse, { errorCode: error.code ?? 'disconnect_failed' });
      throw error;
    }
  }
}

function commandIdentity(client) {
  return createHash('sha256').update(JSON.stringify([client.url, client.config.meetingCode, client.config.displayName])).digest('hex');
}

async function commandEndpoint(client, canStart) {
  const filename = path.join(client.directory, 'commands.json');
  async function read() {
    let endpoint;
    try { endpoint = JSON.parse(await fs.readFile(filename, 'utf8')); }
    catch (error) { if (error.code === 'ENOENT') return null; throw error; }
    if (endpoint.identity !== commandIdentity(client)) throw new ClientError('state_identity_mismatch');
    if (!Number.isSafeInteger(endpoint.pid) || endpoint.pid <= 0) throw new ClientError('invalid_listener_endpoint');
    try { process.kill(endpoint.pid, 0); }
    catch (error) { if (error.code === 'ESRCH') throw new ClientError('stale_listener_endpoint'); throw error; }
    return endpoint;
  }
  let endpoint = await read();
  if (endpoint) return endpoint;
  if (!canStart) throw new ClientError('listener_not_started');
  const child = fork(process.argv[1], [path.resolve(process.argv[2]), '--serve', ...(process.argv[3] === 'rejoin' ? ['rejoin'] : [])], {
    detached: true, windowsHide: true, stdio: ['ignore', 'ignore', 'ignore', 'ipc'], execArgv: [],
  });
  const started = await new Promise((resolve, reject) => {
    child.once('error', reject);
    child.once('message', resolve);
    child.once('exit', () => resolve({ errorCode: 'listener_start_failed' }));
  });
  if (child.connected) child.disconnect();
  child.unref();
  if (started.errorCode === 'state_locked') {
    // Concurrent first commands may reach this point while the winner negotiates its connection.
    for (let i = 0; i < 30; i++) { endpoint = await read(); if (endpoint) return endpoint; await delay(100); }
  }
  if (started.errorCode) throw new ClientError(started.errorCode);
  endpoint = await read();
  if (!endpoint) throw new ClientError('listener_start_failed');
  return endpoint;
}

async function runCommand(client, args) {
  const [command, messageId, value, ...files] = args;
  let request = { command };
  if (command === 'reply' && messageId && value !== undefined) {
    const quoted = files[0] === '--reply-to';
    if (quoted && !files[1]) throw new ClientError('invalid_command_arguments');
    request = { command, messageId, content: value, replyToMessageId: quoted ? files[1] : undefined, files: quoted ? files.slice(2) : files };
  }
  else if (command === 'skip' && messageId && args.length === 2) request = { command, messageId };
  else if (command === 'download' && messageId && value && args.length <= 4) request = { command, messageId, attachmentId: value, output: files[0] };
  else if (!['start', 'next', 'stop', 'leave', 'status', 'rejoin'].includes(command) || args.length !== 1) throw new ClientError('invalid_command_arguments');
  if (command === 'leave') {
    let state;
    try { state = JSON.parse(await fs.readFile(client.file, 'utf8')); }
    catch (error) { if (error.code === 'ENOENT') throw new ClientError('not_joined'); throw error; }
    if (state.url !== client.url || state.displayName !== client.config.displayName ||
        state.meetingKey !== createHash('sha256').update(client.config.meetingCode).digest('hex')) throw new ClientError('state_identity_mismatch');
    if (state.membershipLeft) {
      process.stdout.write(JSON.stringify({ event: 'left', ...state.membershipLeft }) + '\n'); return;
    }
    if (!state.identity) throw new ClientError('not_joined');
  }
  const endpoint = await commandEndpoint(client, ['start', 'next', 'leave', 'rejoin'].includes(command));
  const response = await fetch(`http://127.0.0.1:${endpoint.port}/`, { method: 'POST', redirect: 'error',
    headers: { Authorization: `Bearer ${endpoint.token}`, 'Content-Type': 'application/json' }, body: JSON.stringify(request),
  });
  if (!response.ok) throw new ClientError('local_command_failed');
  const result = await response.json();
  process.stdout.write(JSON.stringify(result) + '\n');
  if (result.errorCode) process.exitCode = 1;
}

async function main() {
  if (!process.argv[2]) throw new ClientError('usage_node_roundtable_mcp_config_json');
  const config = JSON.parse(await fs.readFile(process.argv[2], 'utf8'));
  const client = new MeetingClient(config);
  const mode = process.argv[3];
  if (mode === '--serve') return serveCommands(client, process.argv[4] === 'rejoin');
  if (mode && mode !== '--listen') return runCommand(client, process.argv.slice(3));
  const managed = mode === '--listen';
  await client.open(config.joinMode ?? (managed ? 'auto' : undefined), managed);
  const output = value => process.stdout.write(`${JSON.stringify(value)}\n`);
  output({ event: 'ready', participantId: client.state.identity.participantId });
  const lines = createInterface({ input: process.stdin, crlfDelay: Infinity });
  if (managed) {
    let requested = false;
    // Handle an explicit host command even while the message pump is idle.
    lines.on('line', line => {
      let cmd;
      try { cmd = JSON.parse(line); } catch { return; }
      if (cmd?.command === 'leave' && cmd.confirm === 'user_requested_leave') {
        requested = true; lines.close();
      }
    });
    try {
      await listen(client, lines, output, { hostResponseTimeout: 300000 });
      if (requested) output({ event: 'left', ...await client.leave() });
    } finally { await client.close(); }
    return;
  }
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
          case 'upload': result = await client.upload(input.file); break;
          case 'download': result = await client.download(input.attachmentId, input.output); break;
          case 'status': result = await client.transport.call('get_meeting_status', {
            meetingId: client.state.identity.meetingId, lifecycleProof: client.state.identity.lifecycleProof }); break;
          case 'leave':
            if (input.confirm !== 'user_requested_leave') throw new ClientError('explicit_leave_confirmation_required');
            result = await client.leave();
            output({ id: input.id, succeeded: true, result });
            lines.close(); return;
          case 'disconnect': await stop(); result = { disconnected: true }; break;
          default: throw new ClientError('unknown_command');
        }
        output({ id: input.id, succeeded: true, result });
      } catch (error) { output({ id: input?.id, succeeded: false, errorCode: error.code ?? error.name }); }
    }
  } finally { await client.close(); }
}

if (typeof process !== 'undefined' && process.argv[1] && import.meta.url === pathToFileURL(path.resolve(process.argv[1])).href) {
  main().catch(error => { if (process.connected) process.send({ errorCode: error.code ?? error.name }); process.stderr.write(`${error.code ?? error.name}\n`); process.exitCode = 1; });
}
