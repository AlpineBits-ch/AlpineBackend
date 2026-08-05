/**
 * B2 - Read-Your-Writes Consistency (ScyllaDB)
 *
 * Each VU:
 *   1. POSTs a message to a channel (writes to ScyllaDB via Messaging service)
 *   2. Immediately GETs the message list for that channel
 *   3. Checks whether the just-written message appears in the response
 *
 * ScyllaDB uses eventual consistency. At LOCAL_QUORUM (default for Cassandra-
 * compatible drivers) you expect read-your-writes within the same coordinator,
 * but under high write throughput or coordinator failover it can slip.
 *
 * What to watch:
 *  - read_your_writes_misses: ideally 0. >0 means ScyllaDB is not providing
 *    read-your-writes at the configured consistency level under load.
 *  - api_response_ms for the POST and GET pair - rising latency indicates
 *    ScyllaDB write pressure compressing read windows
 *  - Miss rate change as VU count scales: reveals consistency level vs load trade-off
 */
import http from 'k6/http';
import { check, sleep } from 'k6';
import { fullSetup } from '../lib/setup.js';
import { getToken, credentialsFor } from '../lib/auth.js';
import { readYourWritesMisses, apiResponseMs } from '../lib/metrics.js';

const BASE_URL = __ENV.BASE_URL || 'http://localhost:5000';
const USER_COUNT = parseInt(__ENV.TEST_USER_COUNT || '500', 10);
const TEST_DURATION_S = parseInt(__ENV.TEST_DURATION_S || '180', 10);
// How many messages to fetch after write - a smaller page reduces read latency
// but also reduces the chance of seeing the new message in time
const PAGE_SIZE = parseInt(__ENV.PAGE_SIZE || '20', 10);

export const options = {
  scenarios: {
    ryw: {
      executor: 'ramping-vus',
      startVUs: 0,
      stages: [
        { duration: '30s', target: USER_COUNT },
        { duration: `${TEST_DURATION_S}s`, target: USER_COUNT },
        { duration: '15s', target: 0 },
      ],
    },
  },
  thresholds: {
    read_your_writes_misses: ['count<5'],       // near-zero tolerance
    api_response_ms: ['p(95)<1000'],
    http_req_failed: ['rate<0.01'],
  },
};

export function setup() {
  return fullSetup();
}

export default function (data) {
  const idx = (__VU - 1) % USER_COUNT;
  const creds = credentialsFor(idx);
  const channelId = data.channelId;

  let token;
  try {
    token = getToken(creds.username, creds.password);
  } catch {
    return;
  }

  const headers = {
    'Content-Type': 'application/json',
    Authorization: `Bearer ${token}`,
  };

  // Unique marker to identify our message in the response
  const marker = `__ryw_${__VU}_${__ITER}_${Date.now()}`;

  // ── 1. Write ─────────────────────────────────────────────────────────────
  const writeStart = Date.now();
  const writeRes = http.post(
    `${BASE_URL}/api/v1/messaging/channels/${channelId}/messages`,
    JSON.stringify({ content: marker }),
    { headers }
  );
  apiResponseMs.add(Date.now() - writeStart);

  if (!check(writeRes, { 'message written': (r) => r.status === 200 || r.status === 201 })) {
    return;
  }

  // ── 2. Read immediately ──────────────────────────────────────────────────
  const readStart = Date.now();
  const readRes = http.get(
    `${BASE_URL}/api/v1/messaging/channels/${channelId}/messages?limit=${PAGE_SIZE}`,
    { headers }
  );
  apiResponseMs.add(Date.now() - readStart);

  if (!check(readRes, { 'messages fetched': (r) => r.status === 200 })) {
    return;
  }

  // ── 3. Verify marker is present ─────────────────────────────────────────
  const body = readRes.body || '';
  if (!body.includes(marker)) {
    readYourWritesMisses.add(1);
  }

  sleep(Math.random() * 0.5 + 0.1); // light think time to avoid pure hammer loop
}
