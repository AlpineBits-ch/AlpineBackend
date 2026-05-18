import { Trend, Counter, Rate } from 'k6/metrics';

// ── WebSocket / SignalR ───────────────────────────────────────────────────────

/** Time from ws.connect() call to SignalR handshake ack received (ms) */
export const wsConnectMs = new Trend('ws_connect_ms', true);

/** Time from negotiate POST start to handshake ack (full connect pipeline) */
export const wsFullConnectMs = new Trend('ws_full_connect_ms', true);

/** End-to-end broadcast latency: message send timestamp → client receive (ms) */
export const wsBroadcastLatencyMs = new Trend('ws_broadcast_latency_ms', true);

/** Time a typing-indicator broadcast takes to arrive after StartTyping call (ms) */
export const wsTypingLatencyMs = new Trend('ws_typing_latency_ms', true);

/** Time a voice-state change takes to broadcast back to peers (ms) */
export const wsVoiceStateLatencyMs = new Trend('ws_voice_state_latency_ms', true);

/** Total unexpected WebSocket disconnects */
export const wsUnexpectedDisconnects = new Counter('ws_unexpected_disconnects');

/** Rate of connection attempts that fail during negotiate or handshake */
export const wsConnectFailRate = new Rate('ws_connect_fail_rate');

// ── Data Consistency ─────────────────────────────────────────────────────────

/** Time from MessagingHub connect to presence visible in GuildHub (ms) */
export const presencePropagationMs = new Trend('presence_propagation_ms', true);

/** Time from REST message POST to SignalR broadcast received (ms) — Wolverine lag proxy */
export const wolverinePipelineMs = new Trend('wolverine_pipeline_ms', true);

/** How often a message was NOT present immediately after POST (read-your-writes miss) */
export const readYourWritesMisses = new Counter('read_your_writes_misses');

/** How long after disconnect a user still appears online (ghost presence window, ms) */
export const ghostPresenceWindowMs = new Trend('ghost_presence_window_ms', true);

// ── Infrastructure / Performance ─────────────────────────────────────────────

/** HTTP requests rejected by YARP rate limiter (429) */
export const rateLimitRejections = new Counter('rate_limit_rejections');

/** Requests that received 5xx from any service (proxy or app error) */
export const serverErrors = new Counter('server_errors');

/** Track when P95 response time crosses the 500ms threshold — used in thresholds */
export const apiResponseMs = new Trend('api_response_ms', true);
