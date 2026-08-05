/**
 * A5 - Voice State Churn
 *
 * VOICE_USERS VUs connect to GuildHub and rapidly toggle mute/deafen/camera state.
 * Each state change hits Redis (read-modify-write on the voice state hash) and
 * then broadcasts to all other users in the voice channel group.
 *
 * This scenario reveals:
 *  - Redis write contention under concurrent voice state mutations
 *  - GuildHub broadcast fan-out latency under continuous state changes
 *  - Whether optimistic Redis operations (e.g. SET NX/XX) can keep up
 *
 * What to watch:
 *  - ws_voice_state_latency_ms: measures time from invoking MuteChanged/DeafenChanged
 *    to receiving the Completion frame back (server-processed round-trip)
 *  - Redis INFO stats: ops/sec, latency, memory
 */
import { sleep } from 'k6';
import { fullSetup } from '../lib/setup.js';
import { getToken, credentialsFor } from '../lib/auth.js';
import { connectHub, invokeFrame, Hub, MsgType } from '../lib/signalr.js';
import { wsVoiceStateLatencyMs, wsConnectFailRate, wsUnexpectedDisconnects } from '../lib/metrics.js';

const BASE_URL = __ENV.BASE_URL || 'http://localhost:5000';
const VOICE_USERS = parseInt(__ENV.VOICE_USERS || '200', 10);
const CHURN_INTERVAL_MS = parseInt(__ENV.CHURN_INTERVAL_MS || '500', 10);
const TEST_DURATION_S = parseInt(__ENV.TEST_DURATION_S || '120', 10);

export const options = {
  scenarios: {
    voice_churn: {
      executor: 'constant-vus',
      vus: VOICE_USERS,
      duration: `${TEST_DURATION_S}s`,
    },
  },
  thresholds: {
    ws_voice_state_latency_ms: ['p(95)<300', 'p(99)<1000'],
    ws_connect_fail_rate: ['rate<0.01'],
    ws_unexpected_disconnects: ['count<10'],
  },
};

export function setup() {
  return fullSetup();
}

export default function (data) {
  const idx = (__VU - 1) % VOICE_USERS;
  const creds = credentialsFor(idx);

  let token;
  try {
    token = getToken(creds.username, creds.password);
  } catch (e) {
    wsConnectFailRate.add(1);
    return;
  }

  const pending = new Map(); // invocationId → sentTs

  // Randomly pick which state to toggle each iteration
  const voiceMethods = ['MuteChanged', 'DeafenChanged', 'CameraChanged'];
  let muteState = false;
  let deafenState = false;
  let cameraState = false;

  const stateFor = (method) => {
    if (method === 'MuteChanged') { muteState = !muteState; return [muteState]; }
    if (method === 'DeafenChanged') { deafenState = !deafenState; return [deafenState]; }
    return [cameraState = !cameraState];
  };

  connectHub(BASE_URL, Hub.Guild, token, {
    durationMs: TEST_DURATION_S * 1000,

    onOpen: (socket) => {
      // Stagger start across VUs
      sleep((Math.random() * CHURN_INTERVAL_MS) / 1000);

      const interval = setInterval(() => {
        try {
          const method = voiceMethods[Math.floor(Math.random() * voiceMethods.length)];
          const args = stateFor(method);
          const id = `voice-${__VU}-${Date.now()}`;
          pending.set(id, Date.now());
          socket.send(invokeFrame(method, args, id));
        } catch {
          clearInterval(interval);
        }
      }, CHURN_INTERVAL_MS);

      socket.on('close', () => clearInterval(interval));
    },

    onMessage: (_socket, frame) => {
      // Completion frames confirm the hub processed our invocation
      if (frame.type === MsgType.Completion && pending.has(frame.invocationId)) {
        const sentTs = pending.get(frame.invocationId);
        pending.delete(frame.invocationId);
        wsVoiceStateLatencyMs.add(Date.now() - sentTs);
      }
    },

    onClose: () => wsUnexpectedDisconnects.add(1),
    onError: () => wsConnectFailRate.add(1),
  });
}
