/**
 * B1 - Event Propagation Latency (UserActiveEvent)
 *
 * Measures how long it takes for a presence change (user connecting to MessagingHub)
 * to become visible to other services. The flow is:
 *
 *   MessagingHub.OnConnectedAsync()
 *     → bus.PublishAsync(UserActiveEvent)         [Wolverine]
 *       → RabbitMQ exchange
 *         → handler in Social/Guild service
 *           → updates presence in Redis / DB
 *             → GuildHub broadcasts MemberPresenceChanged
 *
 * Each VU:
 *   1. Connects a LISTENER on GuildHub, waiting for MemberPresenceChanged
 *   2. A second HTTP token + MessagingHub connection triggers UserActiveEvent
 *   3. Records time from step 2 to the GuildHub broadcast in step 1
 *
 * What to watch:
 *  - presence_propagation_ms P95: should stay under 500ms at 1k; budget 2s at 10k
 *  - Sustained increase in propagation lag = Wolverine outbox / RabbitMQ backlog
 *  - Dead-letter queue growth in RabbitMQ management UI
 *
 * NOTE: PRESENCE_EVENT_NAME must match the GuildHub's group-push target name
 * for presence changes (e.g. "MemberPresenceChanged" or "UserOnline").
 */
import http from 'k6/http';
import { sleep } from 'k6';
import { Counter } from 'k6/metrics';
import { fullSetup } from '../lib/setup.js';
import { getToken, credentialsFor } from '../lib/auth.js';
import {
  connectHub,
  negotiate,
  buildWsUrl,
  handshakeFrame,
  invokeFrame,
  pingFrame,
  parseFrames,
  Hub,
  MsgType,
} from '../lib/signalr.js';
import { presencePropagationMs, wsConnectFailRate } from '../lib/metrics.js';

const BASE_URL = __ENV.BASE_URL || 'http://localhost:5000';
const USER_COUNT = parseInt(__ENV.TEST_USER_COUNT || '200', 10);
const TEST_DURATION_S = parseInt(__ENV.TEST_DURATION_S || '180', 10);
const PRESENCE_EVENT_NAME = __ENV.PRESENCE_EVENT_NAME || 'MemberPresenceChanged';
// Max time we wait for the presence event before declaring a timeout
const PROPAGATION_TIMEOUT_MS = parseInt(__ENV.PROPAGATION_TIMEOUT_MS || '10000', 10);

const propagationTimeouts = new Counter('presence_propagation_timeouts');

export const options = {
  scenarios: {
    propagation: {
      executor: 'constant-vus',
      vus: USER_COUNT,
      duration: `${TEST_DURATION_S}s`,
    },
  },
  thresholds: {
    presence_propagation_ms: ['p(95)<2000', 'p(99)<5000'],
    presence_propagation_timeouts: ['count<10'],
  },
};

export function setup() {
  return fullSetup();
}

export default function (data) {
  const idx = (__VU - 1) % USER_COUNT;

  // Two users per VU: observer watches, subject triggers the event
  const observerCreds = credentialsFor(idx);
  const subjectCreds = credentialsFor((idx + USER_COUNT / 2) % USER_COUNT);

  let observerToken, subjectToken;
  try {
    observerToken = getToken(observerCreds.username, observerCreds.password);
    subjectToken = getToken(subjectCreds.username, subjectCreds.password);
  } catch {
    wsConnectFailRate.add(1);
    return;
  }

  let presenceReceived = false;
  let triggerTs = 0;

  // Observer: connect to GuildHub and listen for presence events
  const observerConn = connectHub(BASE_URL, Hub.Guild, observerToken, {
    durationMs: PROPAGATION_TIMEOUT_MS + 3000,

    onOpen: (_socket) => {
      // Observer is connected - now trigger the presence change from the subject
      triggerTs = Date.now();

      // Connect subject to MessagingHub (fires UserActiveEvent on server)
      try {
        connectHub(BASE_URL, Hub.Messaging, subjectToken, {
          durationMs: 2000,
        });
      } catch {
        // Subject connect errors are recorded by connectHub internally
      }
    },

    onMessage: (_socket, frame) => {
      if (frame.type !== MsgType.Invocation) return;
      if (frame.target !== PRESENCE_EVENT_NAME) return;

      if (!presenceReceived && triggerTs > 0) {
        presenceReceived = true;
        presencePropagationMs.add(Date.now() - triggerTs);
      }
    },
  });

  if (!presenceReceived && triggerTs > 0) {
    propagationTimeouts.add(1);
    console.warn(`VU ${__VU}: presence event not received within ${PROPAGATION_TIMEOUT_MS}ms`);
  }

  sleep(1);
}
