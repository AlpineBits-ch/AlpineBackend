/**
 * C2 — Database Connection Pool Exhaustion
 *
 * Finds the exact VU concurrency where PgBouncer + PostgreSQL becomes the
 * bottleneck. Each VU holds an open request that requires a database connection
 * for the full duration (member list query with a JOIN — not cached).
 *
 * Architecture under test:
 *   App service → PgBouncer (transaction pooling, default_pool_size=25 per DB)
 *     → PostgreSQL (max_connections depends on your postgres config, not the 50
 *        in Env.cs — that's the NpgsqlDataSource pool, which PgBouncer replaces
 *        in prod)
 *
 * Pool exhaustion signature:
 *   - P95 latency suddenly jumps (requests queue at PgBouncer)
 *   - api_response_ms P99 diverges from P95
 *   - PostgreSQL pg_stat_activity shows max active connections
 *   - PgBouncer cl_waiting counter climbs
 *
 * What to watch:
 *  - api_response_ms: find the inflection point (VU count where P95 jumps)
 *  - server_errors: 5xx from the app when PgBouncer client limit is hit
 *  - PgBouncer SHOW POOLS; SHOW STATS; output during the test
 */
import http from 'k6/http';
import { check, sleep } from 'k6';
import { fullSetup } from '../lib/setup.js';
import { getToken, credentialsFor } from '../lib/auth.js';
import { apiResponseMs, serverErrors } from '../lib/metrics.js';

const BASE_URL = __ENV.BASE_URL || 'http://localhost:5000';
const USER_COUNT = parseInt(__ENV.TEST_USER_COUNT || '1000', 10);
const MAX_VUS = parseInt(__ENV.PEAK_VUS || '400', 10);

export const options = {
  scenarios: {
    db_exhaustion: {
      executor: 'ramping-vus',
      startVUs: 1,
      stages: [
        { duration: '30s',  target: 25  },  // below one PgBouncer pool
        { duration: '30s',  target: 50  },  // at one pool
        { duration: '30s',  target: 100 },  // 2× pool — queuing begins
        { duration: '30s',  target: 200 },
        { duration: '60s',  target: MAX_VUS },  // saturation zone
        { duration: '30s',  target: 25  },  // recovery — watch latency drop
      ],
    },
  },
  thresholds: {
    api_response_ms: ['p(95)<3000'],  // more lenient — we WANT to find saturation
    server_errors: ['count<200'],
    http_req_failed: ['rate<0.05'],
  },
};

export function setup() {
  return fullSetup();
}

export default function (data) {
  const idx = (__VU - 1) % USER_COUNT;
  const creds = credentialsFor(idx);
  const guildId = data.guildId;

  let token;
  try {
    token = getToken(creds.username, creds.password);
  } catch {
    return;
  }

  // Members endpoint requires a JOIN across guild_members and users tables
  // (non-trivial query, no result cache for this test — disable caching if possible)
  const start = Date.now();
  const res = http.get(
    `${BASE_URL}/api/v1/guild/${guildId}/members`,
    {
      headers: { Authorization: `Bearer ${token}` },
      // Add Cache-Control to prevent any gateway/CDN caching
      responseType: 'text',
    }
  );
  apiResponseMs.add(Date.now() - start);

  if (res.status >= 500) serverErrors.add(1);
  check(res, { 'ok': (r) => r.status === 200 });

  // No sleep — we want maximum concurrent DB pressure per VU
}
