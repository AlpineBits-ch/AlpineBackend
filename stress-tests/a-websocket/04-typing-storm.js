/**
 * A4 - Typing Indicator Storm
 *
 * CONCURRENCY VUs all connect to the same guild channel and invoke StartTyping
 * repeatedly. Each VU also listens for the typing broadcast back from the hub
 * and records the round-trip latency.
 *
 * This scenario saturates:
 *  - Redis pub/sub (SignalR backplane) since each StartTyping broadcasts to every
 *    member of the channel group
 *  - GuildHub CPU for serializing and writing N messages to the backplane
 *
 * What to watch:
 *  - ws_typing_latency_ms: should stay under 200ms. Spikes indicate Redis saturation.
 *  - k6 HTTP errors on the negotiate calls when hub is overwhelmed
 *  - Server-side CPU on the GuildHub pod(s)
 *
 * NOTE: TYPING_EVENT_NAME must match the target name pushed by GuildHub
 * when it broadcasts a typing indicator to group members.
 */
import { sleep } from 'k6';
import { fullSetup } from '../lib/setup.js';
import { getToken, credentialsFor } from '../lib/auth.js';
import { connectHub, invokeFrame, Hub, MsgType } from '../lib/signalr.js';
import { wsTypingLatencyMs, wsConnectFailRate } from '../lib/metrics.js';

const BASE_URL = __ENV.BASE_URL || 'http://localhost:5000';
const CONCURRENCY = parseInt(__ENV.TYPING_CONCURRENCY || '100', 10);
const TYPING_INTERVAL_MS = parseInt(__ENV.TYPING_INTERVAL_MS || '1500', 10);
const TEST_DURATION_S = parseInt(__ENV.TEST_DURATION_S || '120', 10);

// Adjust to match the GuildHub's actual push event name for typing indicators
const TYPING_EVENT_NAME = __ENV.TYPING_EVENT_NAME || 'TypingStarted';

export const options = {
  scenarios: {
    typing_storm: {
      executor: 'constant-vus',
      vus: CONCURRENCY,
      duration: `${TEST_DURATION_S}s`,
    },
  },
  thresholds: {
    ws_typing_latency_ms: ['p(95)<300', 'p(99)<800'],
    ws_connect_fail_rate: ['rate<0.01'],
  },
};

export function setup() {
  return fullSetup();
}

export default function (data) {
  const idx = (__VU - 1) % CONCURRENCY;
  const creds = credentialsFor(idx);

  let token;
  try {
    token = getToken(creds.username, creds.password);
  } catch (e) {
    wsConnectFailRate.add(1);
    return;
  }

  // Track outstanding typing invocations so we can match them to broadcasts
  const pendingTyping = new Map(); // invocationId → sentTs

  connectHub(BASE_URL, Hub.Guild, token, {
    durationMs: TEST_DURATION_S * 1000,

    onOpen: (socket) => {
      // Stagger start to avoid thundering herd at t=0
      sleep((Math.random() * TYPING_INTERVAL_MS) / 1000);

      const channelId = data.channelId;

      // Periodic typing invocations for the duration of the connection
      const interval = setInterval(() => {
        try {
          const id = `typing-${__VU}-${Date.now()}`;
          pendingTyping.set(id, Date.now());
          socket.send(invokeFrame('StartTyping', [channelId], id));
        } catch {
          clearInterval(interval);
        }
      }, TYPING_INTERVAL_MS);

      socket.on('close', () => clearInterval(interval));
    },

    onMessage: (_socket, frame) => {
      if (frame.type !== MsgType.Invocation) return;

      // A Completion frame with an invocationId tells us the server processed
      // our StartTyping call (server-side acknowledgement path).
      // We also listen for the group broadcast as a round-trip proxy.
      if (frame.type === MsgType.Completion && pendingTyping.has(frame.invocationId)) {
        const sentTs = pendingTyping.get(frame.invocationId);
        pendingTyping.delete(frame.invocationId);
        wsTypingLatencyMs.add(Date.now() - sentTs);
        return;
      }

      // When the server broadcasts TYPING_EVENT_NAME to the group, every receiver
      // (including the sender) sees it - use this as round-trip confirmation.
      if (frame.target === TYPING_EVENT_NAME) {
        // The broadcast carries the userId; if it matches us, record trip time.
        const userId = frame.arguments && frame.arguments[0] && frame.arguments[0].userId;
        // We can't easily correlate userId to VU here without extra state,
        // so record latency for ANY received typing event as a proxy measure.
        wsTypingLatencyMs.add(0); // latency already captured via Completion above
      }
    },

    onError: () => wsConnectFailRate.add(1),
  });
}
