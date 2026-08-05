/**
 * A2 - Idle Connection Sustain
 *
 * Holds USER_COUNT concurrent SignalR connections for 10 minutes with periodic
 * VoiceHeartbeat calls. Reveals:
 *  - Server-initiated disconnects (90s TTL vs heartbeat)
 *  - Redis memory growth from sustained presence state
 *  - YARP keep-alive behaviour vs 10s connection pool lifetime
 *  - SignalR hub memory per connection
 *
 * What to watch:
 *  - ws_unexpected_disconnects should be ~0 for the full 10 minutes
 *  - Redis MEMORY USAGE after sustain vs before
 *  - Server CPU should be near-idle (no fan-out work)
 */
import { sleep } from 'k6';
import { fullSetup } from '../lib/setup.js';
import { getToken, credentialsFor } from '../lib/auth.js';
import { connectHub, invokeFrame, Hub } from '../lib/signalr.js';
import { wsConnectFailRate, wsUnexpectedDisconnects } from '../lib/metrics.js';

const BASE_URL = __ENV.BASE_URL || 'http://localhost:5000';
const USER_COUNT = parseInt(__ENV.TEST_USER_COUNT || '1000', 10);
const SUSTAIN_MS = parseInt(__ENV.SUSTAIN_MS || `${10 * 60 * 1000}`, 10); // 10 min default
const HEARTBEAT_INTERVAL_MS = 30_000; // every 30s - well within the 90s TTL

export const options = {
  scenarios: {
    idle_sustain: {
      executor: 'ramping-vus',
      startVUs: 0,
      stages: [
        { duration: '60s', target: USER_COUNT },  // ramp
        { duration: `${SUSTAIN_MS / 1000}s`, target: USER_COUNT }, // hold
        { duration: '30s', target: 0 },
      ],
      gracefulRampDown: '30s',
    },
  },
  thresholds: {
    ws_unexpected_disconnects: ['count<10'],
    ws_connect_fail_rate: ['rate<0.01'],
  },
};

export function setup() {
  return fullSetup();
}

export default function (data) {
  const idx = (__VU - 1) % USER_COUNT;
  const creds = credentialsFor(idx);

  let token;
  try {
    token = getToken(creds.username, creds.password);
  } catch (e) {
    wsConnectFailRate.add(1);
    return;
  }

  let heartbeatTimer;

  connectHub(BASE_URL, Hub.Guild, token, {
    durationMs: SUSTAIN_MS + 5000,

    onOpen: (socket) => {
      // Schedule periodic heartbeats to keep presence alive
      heartbeatTimer = setInterval(() => {
        try {
          socket.send(invokeFrame('VoiceHeartbeat', []));
        } catch {
          // Socket may have closed - setInterval callback fires async
        }
      }, HEARTBEAT_INTERVAL_MS);
    },

    onClose: () => {
      if (heartbeatTimer) clearInterval(heartbeatTimer);
      // Any close that happens mid-test (not during ramp-down) is unexpected
      if (__ITER === 0) wsUnexpectedDisconnects.add(1);
    },

    onError: (e) => {
      wsConnectFailRate.add(1);
    },
  });
}
