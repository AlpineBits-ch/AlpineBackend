/**
 * C3 — YARP Rate Limiter Boundary Validation
 *
 * Validates the rate limiter implementation:
 *   - 100 req/min per authenticated user (fixed window, QueueLimit=0)
 *   - Unauthenticated requests rate-limited by IP
 *   - Requests are REJECTED immediately on quota exhaustion (no queuing)
 *
 * Three sub-scenarios:
 *   burst     — send 110 requests within the first 10s (should get ~10 x 429)
 *   sustained — send exactly 100/min for 3 minutes (should get 0 x 429)
 *   crossover — mix of WebSocket-upgrade + API calls to verify the upgrade
 *               request counts toward the same quota
 *
 * What to watch:
 *  - rate_limit_rejections: "burst" should see ~10 per user; "sustained" should see ~0
 *  - Verify the 429 response has the expected Retry-After header
 *  - Ensure WebSocket negotiation is NOT double-counted (negotiate POST → upgrade)
 *  - At 1k users all bursting simultaneously: verify YARP doesn't OOM on the
 *    fixed-window state storage (check YARP process memory)
 */
import http from 'k6/http';
import { check, sleep, group } from 'k6';
import { Counter } from 'k6/metrics';
import { fullSetup } from '../lib/setup.js';
import { getToken, credentialsFor } from '../lib/auth.js';
import { negotiate, Hub } from '../lib/signalr.js';
import { rateLimitRejections } from '../lib/metrics.js';

const BASE_URL = __ENV.BASE_URL || 'http://localhost:5000';
const USER_COUNT = parseInt(__ENV.TEST_USER_COUNT || '200', 10);

const unexpectedRejections = new Counter('rate_limit_unexpected_rejections');
const expectedRejections = new Counter('rate_limit_expected_rejections');

export const options = {
  scenarios: {
    burst: {
      executor: 'per-vu-iterations',
      vus: USER_COUNT,
      iterations: 1,
      exec: 'burstScenario',
      tags: { scenario: 'burst' },
    },
    sustained: {
      executor: 'constant-vus',
      vus: USER_COUNT,
      duration: '180s',
      exec: 'sustainedScenario',
      startTime: '30s', // start after burst finishes
      tags: { scenario: 'sustained' },
    },
    crossover: {
      executor: 'per-vu-iterations',
      vus: 50,
      iterations: 1,
      exec: 'crossoverScenario',
      startTime: '240s',
      tags: { scenario: 'crossover' },
    },
  },
  thresholds: {
    'rate_limit_unexpected_rejections': ['count<5'],
    'http_req_failed': ['rate<0.15'],  // burst scenario intentionally creates 429s
  },
};

export function setup() {
  return fullSetup();
}

/** Burst: fire 110 requests in rapid succession, expect ~10 to be rejected */
export function burstScenario(data) {
  const idx = (__VU - 1) % USER_COUNT;
  const creds = credentialsFor(idx);
  const guildId = data.guildId;

  let token;
  try {
    token = getToken(creds.username, creds.password);
  } catch {
    return;
  }

  let rejected = 0;
  let accepted = 0;

  for (let i = 0; i < 110; i++) {
    const res = http.get(
      `${BASE_URL}/api/v1/guild/${guildId}`,
      { headers: { Authorization: `Bearer ${token}` } }
    );

    if (res.status === 429) {
      rateLimitRejections.add(1);
      expectedRejections.add(1);
      rejected++;
      check(res, { 'Retry-After header present': (r) => r.headers['Retry-After'] !== undefined });
    } else if (res.status === 200) {
      accepted++;
    } else if (res.status >= 500) {
      unexpectedRejections.add(1);
    }
  }

  // Should have exactly ~10 rejections (the 11 requests beyond the 100/min limit)
  const expectedRejects = 10;
  const tolerance = 3;
  check(null, {
    [`burst: ~${expectedRejects} rejections (got ${rejected})`]:
      () => Math.abs(rejected - expectedRejects) <= tolerance,
  });
}

/** Sustained: stay right at the 100/min limit — should receive 0 rejections */
export function sustainedScenario(data) {
  const idx = (__VU - 1) % USER_COUNT;
  const creds = credentialsFor(idx);
  const guildId = data.guildId;

  let token;
  try {
    token = getToken(creds.username, creds.password);
  } catch {
    return;
  }

  // 100 req/min = 1 req per 600ms. Stay slightly under with 650ms sleep.
  const res = http.get(
    `${BASE_URL}/api/v1/guild/${guildId}`,
    { headers: { Authorization: `Bearer ${token}` } }
  );

  if (res.status === 429) {
    rateLimitRejections.add(1);
    unexpectedRejections.add(1); // under limit — this should never happen
  }

  sleep(0.65);
}

/**
 * Crossover: verify SignalR negotiate counts toward the rate limit quota.
 * Consume 95 requests, then negotiate, confirm negotiate is tracked,
 * then send 5 more — they should be rejected if negotiate was counted.
 */
export function crossoverScenario(data) {
  const idx = (__VU - 1) % 50;
  const creds = credentialsFor(idx);
  const guildId = data.guildId;

  let token;
  try {
    token = getToken(creds.username, creds.password);
  } catch {
    return;
  }

  group('consume_quota', () => {
    for (let i = 0; i < 95; i++) {
      http.get(`${BASE_URL}/api/v1/guild/${guildId}`, {
        headers: { Authorization: `Bearer ${token}` },
      });
    }
  });

  // Negotiate (POST) — should count toward the 100/min limit
  let negStatus = 0;
  group('negotiate', () => {
    const jar = http.cookieJar();
    try {
      negotiate(BASE_URL, Hub.Guild, token, jar);
      negStatus = 200;
    } catch (e) {
      if (e.message.includes('429')) negStatus = 429;
    }
  });

  // Remaining 4 API calls — if negotiate counted, at most 4 should succeed
  let rejectedAfterNeg = 0;
  group('post_negotiate', () => {
    for (let i = 0; i < 5; i++) {
      const res = http.get(`${BASE_URL}/api/v1/guild/${guildId}`, {
        headers: { Authorization: `Bearer ${token}` },
      });
      if (res.status === 429) rejectedAfterNeg++;
    }
  });

  check(null, {
    'negotiate counted toward quota': () => rejectedAfterNeg >= 1 || negStatus === 429,
  });
}
