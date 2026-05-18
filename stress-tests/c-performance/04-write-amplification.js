/**
 * C4 — Write Amplification Under Message Storm
 *
 * Simulates WRITER_VUS users each sending MSG_RATE messages per second.
 * At 1k users × 1 msg/s = 1,000 msg/s total.
 *
 * Each message write triggers:
 *  1. ScyllaDB INSERT (the message itself)
 *  2. PostgreSQL INSERT into Wolverine outbox journal (MessageCreated event)
 *  3. RabbitMQ publish after journal flush
 *  4. Downstream handler(s) update ReadState in PostgreSQL (1 row per guild member)
 *  5. SignalR group broadcast via Redis backplane
 *
 * So 1,000 msg/s = 1,000 ScyllaDB writes + 1,000+ PostgreSQL outbox writes
 *   + N×1,000 ReadState updates (where N = members in channel).
 * This test finds where that amplification breaks first.
 *
 * Two concurrent scenarios:
 *   writers   — WRITER_VUS each posting messages at MSG_RATE/s
 *   monitors  — MONITOR_VUS polling message history to measure read latency
 *               as write load increases (ScyllaDB read/write contention)
 *
 * What to watch:
 *  - api_response_ms POST: ScyllaDB + Wolverine write path latency
 *  - api_response_ms GET (monitor scenario): read latency under write pressure
 *  - server_errors: any 5xx indicates a service is overwhelmed
 *  - PostgreSQL pg_stat_bgwriter + pg_stat_database.blks_written
 *  - RabbitMQ management: queue depth for the MessageCreated subscriber
 *  - ScyllaDB nodetool tpstats: writes vs reads
 */
import http from 'k6/http';
import { check, sleep } from 'k6';
import { Trend } from 'k6/metrics';
import { fullSetup } from '../lib/setup.js';
import { getToken, credentialsFor } from '../lib/auth.js';
import { apiResponseMs, serverErrors } from '../lib/metrics.js';

const BASE_URL = __ENV.BASE_URL || 'http://localhost:5000';
const WRITER_VUS = parseInt(__ENV.WRITER_VUS || '500', 10);
const MONITOR_VUS = parseInt(__ENV.MONITOR_VUS || '50', 10);
const MSG_RATE = parseFloat(__ENV.MSG_RATE || '1'); // messages per second per writer VU
const TEST_DURATION_S = parseInt(__ENV.TEST_DURATION_S || '180', 10);
const USER_COUNT = WRITER_VUS + MONITOR_VUS;

const writeLatencyMs = new Trend('msg_write_latency_ms', true);
const readLatencyMs = new Trend('msg_read_latency_ms', true);

export const options = {
  scenarios: {
    writers: {
      executor: 'constant-vus',
      vus: WRITER_VUS,
      duration: `${TEST_DURATION_S}s`,
      exec: 'writerScenario',
      tags: { scenario: 'writer' },
    },
    monitors: {
      executor: 'constant-vus',
      vus: MONITOR_VUS,
      duration: `${TEST_DURATION_S}s`,
      exec: 'monitorScenario',
      tags: { scenario: 'monitor' },
    },
  },
  thresholds: {
    msg_write_latency_ms: ['p(95)<2000', 'p(99)<5000'],
    msg_read_latency_ms:  ['p(95)<1000'],
    server_errors: ['count<100'],
    http_req_failed: ['rate<0.02'],
  },
};

export function setup() {
  return fullSetup();
}

export function writerScenario(data) {
  const idx = (__VU - 1) % WRITER_VUS;
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

  const intervalMs = 1000 / MSG_RATE;

  while (true) {
    const start = Date.now();
    const res = http.post(
      `${BASE_URL}/api/v1/messaging/channels/${channelId}/messages`,
      JSON.stringify({ content: `load test ${__VU} ${Date.now()}` }),
      { headers }
    );

    const elapsed = Date.now() - start;
    writeLatencyMs.add(elapsed);
    apiResponseMs.add(elapsed);

    if (res.status >= 500) serverErrors.add(1);
    check(res, { 'message written': (r) => r.status === 200 || r.status === 201 });

    const remaining = intervalMs - elapsed;
    if (remaining > 0) sleep(remaining / 1000);
  }
}

export function monitorScenario(data) {
  const idx = (__VU - 1 + WRITER_VUS) % USER_COUNT;
  const creds = credentialsFor(idx);
  const channelId = data.channelId;

  let token;
  try {
    token = getToken(creds.username, creds.password);
  } catch {
    return;
  }

  const headers = { Authorization: `Bearer ${token}` };

  // Read the last 20 messages every 2 seconds to measure read contention
  while (true) {
    const start = Date.now();
    const res = http.get(
      `${BASE_URL}/api/v1/messaging/channels/${channelId}/messages?limit=20`,
      { headers }
    );
    readLatencyMs.add(Date.now() - start);
    if (res.status >= 500) serverErrors.add(1);

    sleep(2);
  }
}
