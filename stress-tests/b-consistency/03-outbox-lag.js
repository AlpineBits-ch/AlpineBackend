/**
 * B3 — Wolverine Inbox/Outbox Pipeline Lag
 *
 * Measures end-to-end latency of the Wolverine event pipeline under load:
 *   REST POST message → ScyllaDB write + Wolverine journal write (PostgreSQL)
 *     → RabbitMQ publish → subscriber handler → SignalR group broadcast
 *
 * The sender embeds a send-timestamp in the message content. A receiver VU
 * connected via GuildHub reads that timestamp from the broadcast event and
 * computes the full pipeline lag.
 *
 * Under load, the PostgreSQL Wolverine journal table becomes a bottleneck:
 * every published event requires an INSERT into the outbox before RabbitMQ
 * delivery. This test reveals when that journal write contention starts
 * increasing the visible lag.
 *
 * Two scenarios run concurrently:
 *   senders   — N VUs each posting 1 message/sec
 *   listeners — N VUs each connected to GuildHub listening for broadcasts
 *
 * What to watch:
 *  - wolverine_pipeline_ms P95/P99: should stay under 1s at normal load
 *  - How lag grows as sender VU count doubles — should be linear on RabbitMQ,
 *    but if it's super-linear the bottleneck is the PostgreSQL journal
 *  - RabbitMQ queue depth for the messaging handler queue
 *
 * NOTE: BROADCAST_EVENT_NAME must match the SignalR target the GuildHub uses
 * when pushing new channel messages. Adjust to match your hub implementation.
 */
import http from 'k6/http';
import { check, sleep } from 'k6';
import { Counter } from 'k6/metrics';
import { fullSetup } from '../lib/setup.js';
import { getToken, credentialsFor } from '../lib/auth.js';
import { connectHub, Hub, MsgType } from '../lib/signalr.js';
import { wolverinePipelineMs, wsConnectFailRate } from '../lib/metrics.js';

const BASE_URL = __ENV.BASE_URL || 'http://localhost:5000';
const SENDER_VUS = parseInt(__ENV.SENDER_VUS || '50', 10);
const LISTENER_VUS = parseInt(__ENV.LISTENER_VUS || '200', 10);
const TEST_DURATION_S = parseInt(__ENV.TEST_DURATION_S || '180', 10);
const SEND_RATE_PER_S = parseFloat(__ENV.SEND_RATE_PER_S || '1');

const BROADCAST_EVENT_NAME = __ENV.BROADCAST_EVENT_NAME || 'ChannelMessageCreated';
const OUTBOX_TEST_MARKER = '__outbox_lag_test';

const eventsDropped = new Counter('outbox_events_dropped'); // lag > 30s = effectively dropped

export const options = {
  scenarios: {
    senders: {
      executor: 'constant-vus',
      vus: SENDER_VUS,
      duration: `${TEST_DURATION_S}s`,
      exec: 'senderScenario',
      tags: { scenario: 'sender' },
    },
    listeners: {
      executor: 'constant-vus',
      vus: LISTENER_VUS,
      duration: `${TEST_DURATION_S}s`,
      exec: 'listenerScenario',
      tags: { scenario: 'listener' },
    },
  },
  thresholds: {
    wolverine_pipeline_ms: ['p(95)<3000', 'p(99)<8000'],
    outbox_events_dropped: ['count<10'],
  },
};

export function setup() {
  return fullSetup();
}

export function senderScenario(data) {
  const idx = (__VU - 1) % SENDER_VUS;
  const creds = credentialsFor(idx);
  const channelId = data.channelId;

  let token;
  try {
    token = getToken(creds.username, creds.password);
  } catch {
    return;
  }

  const headers = {
    'Content-Type': 'application/json',
    Authorization: `Bearer ${token}`,
  };

  const interval = 1000 / SEND_RATE_PER_S;

  while (true) {
    const sendTs = Date.now();
    const payload = JSON.stringify({
      content: JSON.stringify({ _marker: OUTBOX_TEST_MARKER, _ts: sendTs }),
    });

    const res = http.post(
      `${BASE_URL}/api/v1/messaging/channels/${channelId}/messages`,
      payload,
      { headers }
    );
    check(res, { 'sent': (r) => r.status === 200 || r.status === 201 });

    sleep(interval / 1000);
  }
}

export function listenerScenario(data) {
  const idx = (__VU - 1 + SENDER_VUS) % (SENDER_VUS + LISTENER_VUS);
  const creds = credentialsFor(idx);

  let token;
  try {
    token = getToken(creds.username, creds.password);
  } catch {
    wsConnectFailRate.add(1);
    return;
  }

  connectHub(BASE_URL, Hub.Guild, token, {
    durationMs: TEST_DURATION_S * 1000,

    onMessage: (_socket, frame) => {
      if (frame.type !== MsgType.Invocation) return;
      if (frame.target !== BROADCAST_EVENT_NAME) return;

      const receiveTs = Date.now();
      const arg = frame.arguments && frame.arguments[0];
      if (!arg) return;

      try {
        const content = typeof arg.content === 'string' ? JSON.parse(arg.content) : arg.content;
        if (!content || content._marker !== OUTBOX_TEST_MARKER) return;

        const lag = receiveTs - content._ts;
        if (lag > 30_000) {
          eventsDropped.add(1);
        } else if (lag > 0) {
          wolverinePipelineMs.add(lag);
        }
      } catch {
        // non-test messages
      }
    },

    onError: () => wsConnectFailRate.add(1),
  });
}
