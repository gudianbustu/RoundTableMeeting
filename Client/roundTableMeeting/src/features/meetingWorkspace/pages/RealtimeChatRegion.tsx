import { useCallback, useEffect, useMemo, useReducer, useRef } from 'react'
import { listMessages } from '../api/listMessages'
import { sendMessage } from '../api/sendMessage'
import { MeetingChatConnection } from '../api/meetingChatConnection'
import { chatReducer, createInitialChatState } from '../chat/messageReducer'
import { selectMessages } from '../chat/messageSelectors'
import { ChatConnectionStatus } from '../components/ChatConnectionStatus'
import { MessageComposer } from '../components/MessageComposer'
import { MessageTimeline } from '../components/MessageTimeline'
import type { MentionCandidate } from '../types/realtimeChat'
import type { Participant } from '../types/meetingWorkspace'
import { getMentionCandidates } from '../chat/mentionCandidates'

export function RealtimeChatRegion({ meetingId, participants, onMeetingUnavailable, onPresenceChanged }: { meetingId: string; participants: Participant[]; onMeetingUnavailable?: () => void; onPresenceChanged?: (presence: Record<string, 'online' | 'offline'>) => void }) {
  const generation = useRef(1); const controller = useRef<AbortController | undefined>(undefined); const connection = useRef<MeetingChatConnection | undefined>(undefined); const lastApplied = useRef(0); const gapRepairing = useRef(false); const [state, dispatch] = useReducer(chatReducer, createInitialChatState(meetingId, generation.current));
  const scope = useCallback(() => ({ meetingId, generation: generation.current }), [meetingId]);
  const reportUnavailable = useCallback(() => { dispatch({ ...scope(), type: 'meetingDeleted' }); onMeetingUnavailable?.() }, [onMeetingUnavailable, scope]);
  const load = useCallback(async (request: { beforeSequence?: number; afterSequence?: number; throughSequence?: number; limit?: number }, initial = false) => { controller.current?.abort(); const current = new AbortController(); controller.current = current; dispatch({ ...scope(), type: 'historyStateChanged', state: initial ? 'loading' : 'appending' }); try { const page = await listMessages(meetingId, request, current.signal); if (!current.signal.aborted) dispatch({ ...scope(), type: 'historyPageMerged', page, initial }) } catch (error) { if (!current.signal.aborted) { dispatch({ ...scope(), type: 'historyStateChanged', state: 'error' }); if ((error as { code?: string }).code === 'meeting_not_found') reportUnavailable() } } }, [meetingId, reportUnavailable, scope]);
  const synchronize = useCallback(async (snapshot: { onlineParticipantIds: string[]; highWatermarkSequence: number }, scoped: () => { meetingId: string; generation: number }, initial = false) => { dispatch({ ...scoped(), type: 'presenceSnapshot', participantIds: snapshot.onlineParticipantIds, highWatermarkSequence: snapshot.highWatermarkSequence }); if (initial) await load({ throughSequence: snapshot.highWatermarkSequence, limit: 100 }, true); else { controller.current?.abort(); const current = new AbortController(); controller.current = current; let after = lastApplied.current; while (!current.signal.aborted && after < snapshot.highWatermarkSequence) { const page = await listMessages(meetingId, { afterSequence: after, throughSequence: snapshot.highWatermarkSequence, limit: 100 }, current.signal); dispatch({ ...scoped(), type: 'historyPageMerged', page }); if (!page.hasMoreAfter || !page.lastSequence || page.lastSequence <= after) break; after = page.lastSequence } } dispatch({ ...scoped(), type: 'historyStateChanged', state: 'caughtUp' }) }, [load, meetingId]);
  const connect = useCallback(async () => { const currentGeneration = generation.current; const scoped = () => ({ meetingId, generation: currentGeneration }); await connection.current?.stop(); const hub = new MeetingChatConnection({ onState: stateValue => dispatch({ ...scoped(), type: 'connectionStateChanged', state: stateValue }), onMessage: message => dispatch({ ...scoped(), type: 'messageEventReceived', message }), onPresence: (participantId, presence) => dispatch({ ...scoped(), type: 'presenceChanged', participantId, state: presence }), onDeleted: reportUnavailable, onResubscribed: snapshot => void synchronize(snapshot, scoped) }); connection.current = hub; try { const snapshot = await hub.start(meetingId); if (currentGeneration !== generation.current) { await hub.stop(); return } await synchronize(snapshot, scoped, true) } catch (error) { await hub.stop().catch(() => undefined); if (currentGeneration !== generation.current) return; if ((error as { message?: string }).message?.includes('meeting_not_found')) { dispatch({ ...scoped(), type: 'connectionStateChanged', state: 'rejected' }); reportUnavailable() } else dispatch({ ...scoped(), type: 'connectionStateChanged', state: 'disconnected' }) } }, [meetingId, reportUnavailable, synchronize]);
  useEffect(() => { generation.current += 1; dispatch({ type: 'reset', meetingId, generation: generation.current }); void connect(); return () => { generation.current += 1; controller.current?.abort(); void connection.current?.stop(); connection.current = undefined } }, [connect, meetingId]);
  const messages = useMemo(() => selectMessages(state), [state]);
  useEffect(() => { lastApplied.current = state.lastAppliedSequence }, [state.lastAppliedSequence]);
  useEffect(() => {
    if (state.highWatermarkSequence <= state.lastAppliedSequence || state.history === 'loading' || gapRepairing.current) return
    gapRepairing.current = true
    const currentGeneration = generation.current
    const scoped = () => ({ meetingId, generation: currentGeneration })
    const repair = async () => {
      const current = new AbortController()
      controller.current?.abort()
      controller.current = current
      let after = lastApplied.current
      while (!current.signal.aborted && after < state.highWatermarkSequence) {
        const page = await listMessages(meetingId, { afterSequence: after, throughSequence: state.highWatermarkSequence, limit: 100 }, current.signal)
        dispatch({ ...scoped(), type: 'historyPageMerged', page })
        if (!page.hasMoreAfter || !page.lastSequence || page.lastSequence <= after) break
        after = page.lastSequence
      }
    }
    void repair().catch(() => undefined).finally(() => { gapRepairing.current = false })
  }, [meetingId, state.highWatermarkSequence, state.lastAppliedSequence, state.history]);
  useEffect(() => { onPresenceChanged?.(state.presence) }, [onPresenceChanged, state.presence]);
  const submit = async (_candidates: MentionCandidate[], retry = false) => { const content = (retry && state.failedSubmission ? state.failedSubmission.content : state.draft).normalize('NFC').trim(); const candidates = retry && state.failedSubmission ? state.failedSubmission.candidates : getMentionCandidates(content, participants); const clientSubmissionId = retry && state.failedSubmission ? state.failedSubmission.clientSubmissionId : crypto.randomUUID(); const submission = { clientSubmissionId, content, candidates }; dispatch({ ...scope(), type: 'sendStarted', submission }); try { const result = await sendMessage(meetingId, content, clientSubmissionId, candidates); dispatch({ ...scope(), type: 'sendSucceeded', message: result.message }) } catch (error) { const apiError = error as { code?: string; retryAfterSeconds?: number }; if (apiError.code === 'meeting_not_found') reportUnavailable(); else dispatch({ ...scope(), type: 'sendFailed', submission: { ...submission, errorCode: apiError.code, retryAfterSeconds: apiError.retryAfterSeconds } }) } };
  const retry = () => { if (state.failedSubmission) void submit(state.failedSubmission.candidates, true) };
  return <section className="realtime-chat-region"><ChatConnectionStatus connection={state.connection} history={state.history} error={state.history === 'error' ? '消息历史加载失败。' : undefined} onRetry={() => void connect()} /><MessageTimeline messages={messages} hasEarlierMessages={state.hasEarlierMessages} loading={state.history === 'loading' || state.history === 'appending'} onLoadEarlier={() => state.earliestLoadedSequence && void load({ beforeSequence: state.earliestLoadedSequence, limit: 100 })} newMessageCount={state.newMessageCount} onNewMessageClick={() => dispatch({ ...scope(), type: 'newMessageNoticeRead' })} /><MessageComposer value={state.draft} send={state.send} failedSubmission={state.failedSubmission} participants={participants} onChange={draft => dispatch({ ...scope(), type: 'draftChanged', draft })} onSend={candidates => void submit(candidates)} onRetry={retry} /></section>
}
