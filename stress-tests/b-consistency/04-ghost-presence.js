/**
 * B4 — Ghost Presence Window
 *
 * Measures how long a user remains "online" in the system after their
 * SignalR connection is abruptly terminated (no clean disconnect).
 *
 * Relevant architecture:
 *  - GuildHub.VoiceHeartbeat() refreshes a Redis key with a 4-hour sliding TTL
 *  - Presence state is set in Redis on connect and cleared on disconnect
 *  - If a connection drops without OnDisconnectedAsync completing,
 *    Redis retains the presence state until the TTL or the next heartbeat check
 *
 * Each VU:
 *   1. Connects to GuildHub (triggers presence → "online")
 *   2. Polls a presence REST endpoint until the user appears online
 *   3. Abruptly closes the WebSocket (socket.close() without clean SignalR close)
 *   4. Polls the presence endpoint every second until the user disappears
 *   5. Records how long the ghost presence window lasted
 *
 * What to watch:
 *  - ghost_presence_window_ms: should be ≤ 90,000ms (the heartbeat TTL)
 *    Values far below 90s suggest clean disconnect handling works well
 *    Values above 90s suggest the TTL is not being applied correctly
 *  - Presence endpoint HTTP latency under polling load
 *
 * NOTE: PRESENCE_ENDPOINT must return a JSON object with an "online" bool
 * or similar field. Adjust the check below to match your actual API.
 * Example: GET /api/v1/social/presence/{userId} → { "online": true/false }
 */
import http from 'k6/http';
import { sleep } from 'k6';
import { Counter } from 'k6/metrics';
import ws from 'k6/ws';
import { fullSetup } from '../lib/setup.js';
import { getToken, credentialsFor } from '../lib/auth.js';
import { negotiate, buildWsUrl, extractCookieHeader, handshakeFrame, Hub } from '../lib/signalr.js';
import { ghostPresenceWindowMs } from '../lib/metrics.js';

const BASE_URL = __ENV.BASE_URL || 'http://localhost:5000';
const CONCURRENCY = parseInt(__ENV.GHOST_CONCURRENCY || '50', 10);
const POLL_INTERVAL_S = 1;
const MAX_GHOST_WAIT_S = 150; // stop polling after 150s (> 90s TTL)

// Adjust this endpoint to match your presence/profile API
const PRESENCE_PATH = __ENV.PRESENCE_PATH || '/api/v1/social/presence';

const ghostsNeverCleared = new Counter('ghost_presence_never_cleared');

export const options = {
  scenarios: {
    ghost: {
      executor: 'constant-vus',
      vus: CONCURRENCY,
      duration: `${(MAX_GHOST_WAIT_S + 30) * 2}s`, // 2 cycles per VU
    },
  },
  thresholds: {
    ghost_presence_window_ms: ['p(95)<100000'], // 100s hard ceiling
    ghost_presence_never_cleared: ['count<5'],
  },
};

export function setup() {
  return fullSetup();
}

export default function (data) {
  const idx = (__VU - 1) % CONCURRENCY;
  const creds = credentialsFor(idx);

  let token, userId;
  try {
    token = getToken(creds.username, creds.password);
    // Fetch own profile to get userId for presence polling
    const profileRes = http.get(`${BASE_URL}/api/v1/social/profile/me`, {
      headers: { Authorization: `Bearer ${token}` },
    });
    if (profileRes.status === 200) {
      userId = profileRes.json('id') || profileRes.json('userId');
    }
  } catch {
    return;
  }

  if (!userId) {
    console.warn(`VU ${__VU}: could not determine userId, skipping ghost test`);
    return;
  }

  const presenceUrl = `${BASE_URL}${PRESENCE_PATH}/${userId}`;
  const authHeaders = { Authorization: `Bearer ${token}` };

  // ── 1. Connect and confirm online ────────────────────────────────────────
  const jar = http.cookieJar();
  const negResult = negotiate(BASE_URL, Hub.Guild, token, jar);
  const connectionToken = negResult.connectionToken || negResult.connectionId;
  const wsUrl = buildWsUrl(BASE_URL, Hub.Guild, connectionToken, token);
  const cookieHeader = extractCookieHeader(jar, BASE_URL);

  let socketRef = null;

  ws.connect(wsUrl, { headers: { Cookie: cookieHeader } }, (socket) => {
    socketRef = socket;
    socket.on('open', () => socket.send(handshakeFrame()));

    // Hold the connection briefly to let OnConnectedAsync complete
    socket.setTimeout(() => {
      // ── 2. Verify online before disconnect ───────────────────────────────
      const preRes = http.get(presenceUrl, { headers: authHeaders });
      const isOnline = presenceIsOnline(preRes);
      if (!isOnline) {
        // User never appeared online — skip measurement
        socket.close();
        return;
      }

      // ── 3. Abrupt close (no SignalR close handshake) ─────────────────────
      const disconnectTs = Date.now();
      socket.close();

      // ── 4. Poll until offline or timeout ────────────────────────────────
      let cleared = false;
      for (let i = 0; i < MAX_GHOST_WAIT_S; i++) {
        sleep(POLL_INTERVAL_S);
        const pollRes = http.get(presenceUrl, { headers: authHeaders });
        if (!presenceIsOnline(pollRes)) {
          ghostPresenceWindowMs.add(Date.now() - disconnectTs);
          cleared = true;
          break;
        }
      }

      if (!cleared) {
        ghostsNeverCleared.add(1);
        console.warn(`VU ${__VU}: user ${userId} still online after ${MAX_GHOST_WAIT_S}s`);
      }
    }, 3000);
  });

  sleep(2);
}

/** Parse presence response — adjust to match your actual presence API response shape. */
function presenceIsOnline(res) {
  if (res.status !== 200) return false;
  try {
    const body = res.json();
    // Common shapes: { online: bool }, { status: "online" }, { isOnline: bool }
    return body.online === true || body.isOnline === true || body.status === 'online';
  } catch {
    return false;
  }
}
