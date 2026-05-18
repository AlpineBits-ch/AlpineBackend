/**
 * C1 — YARP Gateway Throughput Ceiling
 *
 * Finds the maximum sustainable HTTP request rate through the YARP reverse proxy
 * before P95 latency exceeds the 500ms budget. Uses a realistic mix of read-heavy
 * endpoints (guild info, channel list, message history) to simulate real traffic.
 *
 * Also validates:
 *  - YARP connection pool lifetime (10s): under high concurrency you should see
 *    SSL handshake overhead in the tail latency. Compare P95 vs P99 spread.
 *  - Active health check convergence: if a backend pod dies mid-test, requests
 *    to it should fail for at most 15s (active check interval) then stop.
 *  - Passive health check: 30% failure rate triggers pod ejection from routing.
 *
 * What to watch:
 *  - api_response_ms P95 crossing 500ms → throughput ceiling found
 *  - server_errors (5xx) — indicates backend overload, not proxy overload
 *  - rate_limit_rejections (429) — may trigger at 100 req/min/user;
 *    set TEST_USER_COUNT high enough that no single user exceeds 100/min
 */
import http from 'k6/http';
import { check, group, sleep } from 'k6';
import { fullSetup } from '../lib/setup.js';
import { getToken, credentialsFor } from '../lib/auth.js';
import { apiResponseMs, serverErrors, rateLimitRejections } from '../lib/metrics.js';

const BASE_URL = __ENV.BASE_URL || 'http://localhost:5000';
const USER_COUNT = parseInt(__ENV.TEST_USER_COUNT || '1000', 10);
const PEAK_VUS = parseInt(__ENV.PEAK_VUS || '500', 10);

export const options = {
  scenarios: {
    throughput: {
      executor: 'ramping-arrival-rate',
      startRate: 10,
      timeUnit: '1s',
      preAllocatedVUs: PEAK_VUS,
      maxVUs: PEAK_VUS * 2,
      stages: [
        { duration: '60s',  target: 100  }, // warm up
        { duration: '60s',  target: 500  }, // ramp
        { duration: '60s',  target: 1000 }, // push
        { duration: '60s',  target: 2000 }, // ceiling hunt
        { duration: '30s',  target: 100  }, // cool down
      ],
    },
  },
  thresholds: {
    api_response_ms: ['p(95)<500', 'p(99)<2000'],
    server_errors: ['count<100'],
    rate_limit_rejections: ['count<500'],
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
    token = getToken(creds.username, creds.password);
  } catch {
    return;
  }

  const headers = { Authorization: `Bearer ${token}` };
  const guildId = data.guildId;
  const channelId = data.channelId;

  // Weighted mix of endpoints that simulate real read traffic
  const r = Math.random();

  if (r < 0.30) {
    // Guild info — lightweight, hits PostgreSQL cache
    group('guild_info', () => {
      const start = Date.now();
      const res = http.get(`${BASE_URL}/api/v1/guild/${guildId}`, { headers });
      apiResponseMs.add(Date.now() - start);
      track(res);
    });
  } else if (r < 0.55) {
    // Channel list — moderate, JOIN query on roles/permissions
    group('channel_list', () => {
      const start = Date.now();
      const res = http.get(`${BASE_URL}/api/v1/guild/${guildId}/channels`, { headers });
      apiResponseMs.add(Date.now() - start);
      track(res);
    });
  } else if (r < 0.80) {
    // Message history — heaviest, hits ScyllaDB time-series read
    group('message_history', () => {
      const start = Date.now();
      const res = http.get(
        `${BASE_URL}/api/v1/messaging/channels/${channelId}/messages?limit=20`,
        { headers }
      );
      apiResponseMs.add(Date.now() - start);
      track(res);
    });
  } else {
    // Social profile — cross-service call (Guild → Social via Wolverine)
    group('social_profile', () => {
      const start = Date.now();
      const res = http.get(`${BASE_URL}/api/v1/social/profile/me`, { headers });
      apiResponseMs.add(Date.now() - start);
      track(res);
    });
  }
}

function track(res) {
  if (res.status === 429) rateLimitRejections.add(1);
  if (res.status >= 500) serverErrors.add(1);
  check(res, { 'success (2xx or 4xx)': (r) => r.status < 500 });
}
