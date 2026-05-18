/**
 * A3 — Message Fan-Out Storm
 *
 * Two concurrent k6 scenarios:
 *   sender    — 1 VU posts a channel message every SEND_INTERVAL_MS.
 *               The message content encodes the send timestamp so receivers
 *               can measure end-to-end broadcast latency without clock sync.
 *
 *   receivers — USER_COUNT-1 VUs, all connected to the same guild's GuildHub,
 *               listening for the server-pushed message event.
 *
 * What to watch:
 *  - ws_broadcast_latency_ms P95/P99: the point at which latency degrades
 *    is where the Redis backplane or SignalR group-send becomes the bottleneck
 *  - How latency grows as sender rate increases (SEND_INTERVAL_MS env var)
 *  - Whether all receivers see every message (counter: messages_missed)
 *
 * NOTE: BROADCAST_EVENT_NAME must match the target name the GuildHub uses
 * when pushing new channel messages to group members. Inspect the hub with
 * `Clients.Group(...).SendAsync("EventName", ...)` to confirm the exact string.
 */
import http from 'k6/http';
import { check, sleep } from 'k6';
import { Counter } from 'k6/metrics';
import { fullSetup } from '../lib/setup.js';
import { getToken, credentialsFor, getAdminToken } from '../lib/auth.js';
import { connectHub, Hub, MsgType } from '../lib/signalr.js';
import { wsBroadcastLatencyMs, wsConnectFailRate } from '../lib/metrics.js';

const BASE_URL = __ENV.BASE_URL || 'http://localhost:5000';
const USER_COUNT = parseInt(__ENV.TEST_USER_COUNT || '1000', 10);
const SEND_INTERVAL_MS = parseInt(__ENV.SEND_INTERVAL_MS || '200', 10);
const TEST_DURATION_S = parseInt(__ENV.TEST_DURATION_S || '120', 10);

// Adjust to the actual SignalR target name used by GuildHub's channel message push
const BROADCAST_EVENT_NAME = __ENV.BROADCAST_EVENT_NAME || 'ChannelMessageCreated';

const messagesMissed = new Counter('fanout_messages_missed');
const messagesReceived = new Counter('fanout_messages_received');

export const options = {
  scenarios: {
    sender: {
      executor: 'constant-vus',
      vus: 1,
      duration: `${TEST_DURATION_S}s`,
      exec: 'senderScenario',
      tags: { scenario: 'sender' },
    },
    receivers: {
      executor: 'ramping-vus',
      startVUs: 0,
      stages: [
        { duration: '20s', target: USER_COUNT - 1 },
        { duration: `${TEST_DURATION_S - 20}s`, target: USER_COUNT - 1 },
      ],
      exec: 'receiverScenario',
      tags: { scenario: 'receiver' },
    },
  },
  thresholds: {
    ws_broadcast_latency_ms: ['p(95)<500', 'p(99)<1500'],
    ws_connect_fail_rate: ['rate<0.01'],
  },
};

export function setup() {
  return fullSetup();
}

/** Sender: POST a message to the channel with the send timestamp embedded. */
export function senderScenario(data) {
  const adminToken = getAdminToken();
  const channelId = data.channelId;

  while (true) {
    const sendTs = Date.now();
    const payload = JSON.stringify({
      content: JSON.stringify({ _ts: sendTs, _test: 'fanout' }),
    });

    const res = http.post(
      `${BASE_URL}/api/v1/messaging/channels/${channelId}/messages`,
      payload,
      { headers: { 'Content-Type': 'application/json', Authorization: `Bearer ${adminToken}` } }
    );

    check(res, { 'message sent': (r) => r.status === 200 || r.status === 201 });

    sleep(SEND_INTERVAL_MS / 1000);
  }
}

/** Receiver: connect to GuildHub and timestamp every broadcast. */
export function receiverScenario(data) {
  const idx = (__VU - 1) % USER_COUNT;
  const creds = credentialsFor(idx);

  let token;
  try {
    token = getToken(creds.username, creds.password);
  } catch (e) {
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

      // The sender embedded the timestamp inside content as JSON
      let sendTs = null;
      try {
        const content = typeof arg.content === 'string' ? JSON.parse(arg.content) : arg.content;
        if (content && content._test === 'fanout') {
          sendTs = content._ts;
        }
      } catch {
        return; // non-test messages
      }

      if (sendTs) {
        const lag = receiveTs - sendTs;
        if (lag > 0) {
          wsBroadcastLatencyMs.add(lag);
          messagesReceived.add(1);
        }
      }
    },

    onError: () => wsConnectFailRate.add(1),
  });
}
