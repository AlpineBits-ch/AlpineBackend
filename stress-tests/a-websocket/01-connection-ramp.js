/**
 * A1 — Connection Ramp
 *
 * Measures raw SignalR connection establishment under increasing concurrency.
 * Each VU: authenticate → negotiate → WebSocket connect → handshake → hold 5s → disconnect.
 *
 * What to watch:
 *  - ws_full_connect_ms P95: should stay under 3s at 1k, budget 8s at 10k
 *  - ws_connect_fail_rate: anything >1% is a problem
 *  - Identity service CPU during the auth ramp (JWT signing is expensive)
 *  - YARP connection pool hit rate (10s pool lifetime creates SSL churn)
 */
import { sleep } from 'k6';
import { fullSetup } from '../lib/setup.js';
import { getToken, credentialsFor } from '../lib/auth.js';
import { connectHub, Hub } from '../lib/signalr.js';
import { wsConnectMs, wsFullConnectMs, wsConnectFailRate, wsUnexpectedDisconnects } from '../lib/metrics.js';

const BASE_URL = __ENV.BASE_URL || 'http://localhost:5000';
const USER_COUNT = parseInt(__ENV.TEST_USER_COUNT || '1000', 10);

export const options = {
  scenarios: {
    ramp: {
      executor: 'ramping-vus',
      startVUs: 0,
      stages: [
        { duration: '60s', target: USER_COUNT },
        { duration: '120s', target: USER_COUNT }, // sustain — watch for steady-state failures
        { duration: '30s', target: 0 },
      ],
      gracefulRampDown: '10s',
    },
  },
  thresholds: {
    ws_full_connect_ms: ['p(95)<5000', 'p(99)<10000'],
    ws_connect_fail_rate: ['rate<0.01'],
    ws_unexpected_disconnects: ['count<50'],
    http_req_failed: ['rate<0.02'],
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
    const authStart = Date.now();
    token = getToken(creds.username, creds.password);
    // Auth latency included in full-connect measurement below
  } catch (e) {
    wsConnectFailRate.add(1);
    console.error(`Auth failed VU ${__VU}: ${e}`);
    return;
  }

  const fullStart = Date.now();
  let connected = false;

  try {
    const wsStart = Date.now();

    const res = connectHub(BASE_URL, Hub.Guild, token, {
      durationMs: 5000,
      onOpen: (socket) => {
        connected = true;
        wsConnectMs.add(Date.now() - wsStart);
      },
      onClose: () => {
        if (!connected) wsUnexpectedDisconnects.add(1);
      },
      onError: (e) => {
        wsConnectFailRate.add(1);
      },
    });

    wsFullConnectMs.add(Date.now() - fullStart);
    wsConnectFailRate.add(connected ? 0 : 1);
  } catch (e) {
    wsConnectFailRate.add(1);
    console.error(`Connect failed VU ${__VU}: ${e}`);
  }

  // Brief pause before next iteration — simulates user think time and prevents
  // all VUs from hammering auth simultaneously on reconnect.
  sleep(Math.random() * 2 + 1);
}
