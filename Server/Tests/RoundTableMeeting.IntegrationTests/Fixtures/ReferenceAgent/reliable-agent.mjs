import { randomBytes, randomUUID } from 'node:crypto'
import { setTimeout as delay } from 'node:timers/promises'
import { ReliableTransport, TransportError } from './reliable-transport.mjs'

export class AgentError extends Error {
  constructor(code, data) { super(code); this.code = code; this.data = data }
}

const terminal = new Set(['connection_superseded', 'resume_rejected',
  'meeting_not_found', 'agent_request_invalid', 'reliability_mode_required', 'reliable_capability_missing'])
const recoverable = new Set(['connection_invalid', 'connection_expired', 'connection_disconnected',
  'operation_superseded', 'agent_operation_unavailable'])

export class ReliableAgent {
  constructor({ url, meetingCode, displayName = 'ReferenceAgent', contextCharacterLimit = 12000,
    takeover = false, store, generate, log = () => {}, transportFactory, waitSeconds = 30,
    requestTimeoutMs = 15000, dropResponses = [], fault = null }) {
    Object.assign(this, { url, meetingCode, displayName, contextCharacterLimit, takeover, store, generate, log,
      waitSeconds, requestTimeoutMs, fault })
    this.makeTransport = transportFactory ?? (() => new ReliableTransport(url, requestTimeoutMs))
    this.dropResponses = new Set(dropResponses)
    this.shutdown = new AbortController()
  }

  async save() { await this.store.save(this.state) }

  // Replays uncertain operations on the SAME transport with the same operation/body.
  async invoke(name, args, options) {
    const transport = this.transport
    const signal = this.operationSignal ?? this.shutdown.signal
    let transportRetries = 0
    let pendingRetries = 0
    let unavailableRetries = 0
    while (true) {
      signal.throwIfAborted()
      let result
      try {
        result = await transport.tool(name, args, options)
        const qualifiedName = name === 'confirm_context' ? name + ':' + args.kind : name
        const canDrop = name !== 'wait_for_mention' || result.data?.kind === 'mention'
        if (canDrop && (this.dropResponses.delete(qualifiedName) || this.dropResponses.delete(name))) {
          this.log('fault_response_dropped', name)
          throw new TransportError('injected_response_loss')
        }
      } catch (error) {
        if (!(error instanceof TransportError) || transportRetries++ >= 1 || transport.failure) throw error
        this.log('replaying_request', name)
        continue
      }
      if (result?.succeeded) return result.data
      if (result?.errorCode === 'operation_pending' && ['join_meeting', 'resume_agent'].includes(name) && pendingRetries < 4) {
        this.log('activation_pending', name)
        await this.backoff(this.retryDelay(result.data, 250 * 2 ** pendingRetries++), signal)
        continue
      }
      if (['agent_operation_unavailable', 'agent_operation_conflict'].includes(result?.errorCode) && unavailableRetries < 2) {
        this.log('operation_backoff', name)
        await this.backoff(this.retryDelay(result.data, 1000 * 2 ** unavailableRetries++), signal)
        continue
      }
      throw new AgentError(result?.errorCode ?? 'tool_response_invalid', result?.data)
    }
  }

  retryDelay(data, fallback) {
    const seconds = data?.retryAfterSeconds
    return Number.isSafeInteger(seconds) && seconds > 0 ? Math.max(fallback, seconds * 1000) : fallback
  }

  async backoff(milliseconds, signal) {
    // Split long rate-limit waits to avoid Node's timer overflow; never shorten a server hint.
    for (let remaining = milliseconds; remaining > 0;) {
      const chunk = Math.min(remaining, 60000)
      await delay(chunk, undefined, { signal })
      remaining -= chunk
    }
  }

  common(extra = {}) { return { connectionSessionId: this.state.connectionSessionId, ...extra } }

  async connect() {
    this.transport = this.makeTransport()
    await this.transport.open()
    let joined
    if (!this.state.connectionSessionId) {
      joined = await this.invoke('join_meeting', this.state.join)
      this.state.connectionSessionId = joined.connection?.connectionSessionId ?? joined.connectionSessionId
      if (!this.state.connectionSessionId) throw new AgentError('connection_response_invalid')
      this.state.initial = joined
      await this.save()
    }
    if (!joined || joined.kind === 'recovery_required') {
      // Resume receipts include the target transport: a new transport needs a new operation.
      this.state.resumeOperationId = randomUUID()
      await this.save()
      joined = await this.invoke('resume_agent', this.common({ recoveryKey: this.state.join.recoveryKey,
        operationId: this.state.resumeOperationId }))
      const initialContext = joined.initialContext ?? this.state.initial?.initialContext
      this.state.initial = { ...this.state.initial, ...joined, initialContext }
      if (joined.connection?.initialContextState === 'confirmed') this.state.initialConfirmed = true
      await this.save()
    }
    const required = ['explicit_context_ack', 'connection_resume', 'explicit_takeover', 'connection_lease', 'task_retry', 'client_wait_timeout']
    if (required.some(name => !joined.capabilities?.includes(name))) throw new AgentError('reliable_capability_missing')
    this.policy = joined.leasePolicy
    if (!Number.isInteger(this.policy?.leaseRenewIntervalSeconds) || this.policy.leaseRenewIntervalSeconds <= 0)
      throw new AgentError('lease_policy_invalid')
    this.log(joined.kind === 'resumed' ? 'resumed' : 'joined')
  }

  async renew(signal) {
    while (!signal.aborted) {
      await delay(this.policy.leaseRenewIntervalSeconds * 1000, undefined, { signal })
      await this.invoke('renew_agent_lease', this.common())
      this.log('lease_renewed')
    }
  }

  requestRetry(taskId) {
    // A command is tied to the head displayed NOW; it cannot accidentally retry a later task.
    if (!this.blockedTask || (taskId && taskId !== this.blockedTask)) { this.log('retry_rejected', 'no_matching_blocked_task'); return }
    if (!this.retryCommand) this.retryCommand = { taskId: this.blockedTask, operationId: randomUUID() }
  }

  stop() { this.shutdown.abort(); this.transport?.fail(new TransportError('stopped')) }

  async pause(milliseconds, signal) { await delay(Math.max(100, Math.min(milliseconds, 5000)), undefined, { signal }) }

  async business(signal) {
    if (this.state.initial?.initialDeliveryId && !this.state.initialConfirmed) {
      await this.invoke('confirm_context', this.common({ kind: 'initial', initialDeliveryId: this.state.initial.initialDeliveryId }))
      this.state.initialConfirmed = true
      await this.save()
      this.log('initial_confirmed')
    }
    while (!signal.aborted) {
      const task = this.state.task
      const status = await this.invoke('get_agent_status', this.common(task ? { taskId: task.taskId } : {}))
      const requested = status.requestedTask
      if (task && requested?.state === 'replied') {
        // Includes a reply committed before a lost response or a process crash.
        this.state.task = null
        await this.save()
        this.progress = (this.progress ?? 0) + 1
        this.log('reply_recovered')
        continue
      }
      if (task && (!requested || requested.currentAttemptId !== task.deliveryAttemptId ||
        !['delivered', 'processing'].includes(requested.state))) {
        this.state.task = null
        await this.save()
      }
      const head = status.queueHead
      if (head?.state === 'failed') {
        if (head.retryExhausted) {
          if (this.blockedTask !== head.taskId) this.log('blocked', 'enter retry to explicitly retry this head')
          this.blockedTask = head.taskId
          if (this.retryCommand) {
            if (this.retryCommand.taskId !== head.taskId) { this.retryCommand = null; continue }
            this.state.retry = this.retryCommand
            this.retryCommand = null
            await this.save()
          }
          if (this.state.retry?.taskId === head.taskId) {
            await this.invoke('retry_task', this.common(this.state.retry))
            this.state.retry = null
            this.blockedTask = null
            await this.save()
            this.log('retry_scheduled')
            continue
          }
          await this.pause(1000, signal)
          this.progress = (this.progress ?? 0) + 1
          continue
        }
        if (head.nextRetryAt && Date.parse(head.nextRetryAt) > Date.now()) {
          await this.pause(Date.parse(head.nextRetryAt) - Date.now(), signal)
          continue
        }
      }
      this.blockedTask = null
      if (this.state.retry) { this.state.retry = null; await this.save() }
      if (!this.state.task) {
        try {
          const waitSeconds = Math.min(this.waitSeconds, this.policy.maximumWaitTimeoutSeconds)
          const mention = await this.invoke('wait_for_mention', this.common({ waitTimeoutSeconds: waitSeconds }),
            { timeoutMs: waitSeconds * 1000 + this.requestTimeoutMs })
          if (mention.kind === 'idle') { this.progress = (this.progress ?? 0) + 1; this.log('idle'); continue }
          if (mention.kind !== 'mention') throw new AgentError('mention_response_invalid')
          this.state.task = mention
          await this.save()
          this.log('mention_saved')
        } catch (error) {
          if (['task_not_available', 'retry_pending', 'retry_exhausted', 'confirmation_expired'].includes(error.code)) {
            await this.pause(1000, signal); continue
          }
          throw error
        }
      }
      const mention = this.state.task
      try {
        await this.invoke('confirm_context', this.common({ kind: 'task', taskId: mention.taskId,
          deliveryAttemptId: mention.deliveryAttemptId }))
        if (this.fault === 'permanent_failure') {
          this.fault = null
          await this.invoke('report_task_failure', this.common({ taskId: mention.taskId,
            deliveryAttemptId: mention.deliveryAttemptId, failureCode: 'agent_permanent_failure' }))
          continue
        }
        if (mention.replyContent === undefined) {
          this.log('generation_started')
          let content
          try { content = await this.generate({ initial: this.state.initial, mention }, signal) }
          catch (error) {
            if (signal.aborted) throw error
            await this.invoke('report_task_failure', this.common({ taskId: mention.taskId,
              deliveryAttemptId: mention.deliveryAttemptId, failureCode: 'agent_transient_failure' }))
            this.log('generation_failed')
            continue
          }
          if (signal.aborted) throw new TransportError('generation_cancelled')
          if (typeof content !== 'string' || !content.trim()) throw new AgentError('model_response_empty')
          mention.replyContent = content
          await this.save()
        }
        await this.invoke('reply_to_mention', this.common({ taskId: mention.taskId, deliveryAttemptId: mention.deliveryAttemptId,
          replyIdempotencyKey: mention.replyIdempotencyKey, content: mention.replyContent }))
        this.state.task = null
        await this.save()
        this.progress = (this.progress ?? 0) + 1
        this.log('replied')
      } catch (error) {
        if (['delivery_attempt_stale', 'confirmation_expired', 'task_not_available'].includes(error.code)) {
          this.state.task = null
          await this.save()
          continue
        }
        throw error
      }
    }
  }

  async run() {
    try {
      this.state = await this.store.acquire()
      if (this.state && (this.state.url !== this.url || this.state.join.meetingCode !== this.meetingCode ||
        this.state.join.displayName !== this.displayName)) throw new AgentError('state_identity_mismatch')
      if (!this.state) {
        this.state = { version: 1, url: this.url, join: { meetingCode: this.meetingCode, displayName: this.displayName,
          contextCharacterLimit: this.contextCharacterLimit, reliabilityMode: 'explicit_ack_v1',
          joinMode: this.takeover ? 'takeover' : 'create', operationId: randomUUID(), recoveryKey: randomBytes(32).toString('base64url') } }
        await this.save()
      }
      let failures = 0
      while (!this.shutdown.signal.aborted) {
        let renewal
        const cycle = new AbortController()
        const signal = AbortSignal.any([cycle.signal, this.shutdown.signal])
        this.operationSignal = signal
        const previousProgress = this.progress ?? 0
        let renewError
        let recoveryDelay = 1000
        try {
          await this.connect()
          renewal = this.renew(signal).catch(error => {
            if (!signal.aborted) { renewError = error; cycle.abort(); this.transport.fail(error) }
          })
          await this.business(signal)
        } catch (caught) {
          const error = renewError ?? caught
          if (this.shutdown.signal.aborted) break
          if (terminal.has(error.code) || !(error instanceof TransportError || recoverable.has(error.code))) throw error
          this.log('recovering', error.code)
          failures = (this.progress ?? 0) === previousProgress ? failures + 1 : 1
          if (failures >= 10) throw new AgentError('recovery_attempts_exhausted')
          recoveryDelay = this.retryDelay(error.data, 1000 * Math.min(5, failures))
        } finally {
          cycle.abort()
          await renewal
          if (this.transport) {
            const closed = await this.transport.close()
            this.log(closed ? 'transport_closed' : 'transport_close_failed')
            if (!closed && this.shutdown.signal.aborted) this.closeFailed = true
          }
        }
        if (!this.shutdown.signal.aborted) {
          try { await this.backoff(recoveryDelay, this.shutdown.signal) }
          catch (error) { if (!this.shutdown.signal.aborted) throw error }
        }
      }
    } finally { await this.store.release() }
  }
}
