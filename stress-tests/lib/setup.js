import http from 'k6/http';
import { check, sleep } from 'k6';
import { registerUser, getToken, getAdminToken, credentialsFor } from './auth.js';

const BASE_URL = __ENV.BASE_URL || 'http://localhost:5000';
const USER_COUNT = parseInt(__ENV.TEST_USER_COUNT || '1000', 10);

/**
 * Full test environment setup. Called once by k6 before VUs start.
 *
 * 1. Registers TEST_USER_COUNT users (idempotent — safe to re-run)
 * 2. Admin user creates a shared test guild
 * 3. Creates a permanent invite
 * 4. All users join the guild
 * 5. Returns shared data consumed by VUs via setup() return value
 *
 * Set SKIP_PROVISION=true to skip user/guild creation and rely on
 * TEST_GUILD_ID / TEST_CHANNEL_ID env vars instead (faster re-runs).
 */
export function fullSetup() {
  if (__ENV.SKIP_PROVISION === 'true') {
    return loadExistingEnvironment();
  }

  console.log(`Provisioning ${USER_COUNT} test users…`);

  // Register admin first so we can create the guild
  registerUser('admin');
  const adminCreds = { username: `${__ENV.USER_PREFIX || 'echotest'}_admin@test.echo`, password: __ENV.TEST_PASSWORD || 'EchoTest1!' };
  const adminToken = getToken(adminCreds.username, adminCreds.password);

  // Provision ordinary users in batches to avoid overwhelming Identity
  for (let i = 0; i < USER_COUNT; i++) {
    registerUser(i);
    if (i % 100 === 99) {
      console.log(`  Registered ${i + 1}/${USER_COUNT} users`);
    }
  }

  // Create the shared test guild
  const guild = createGuild(adminToken, 'Echo Stress Test Guild');
  const guildId = guild.id || guild.guildId;

  // Get channels (the guild service should auto-create a default text channel)
  const channels = getChannels(adminToken, guildId);
  const textChannel = channels.find((c) => c.type === 0 || c.type === 'Text') || channels[0];
  const channelId = textChannel.id || textChannel.channelId;

  // Create a permanent invite so all users can join
  const inviteCode = createInvite(adminToken, guildId);

  // Have all test users join
  console.log('Joining all users to test guild…');
  for (let i = 0; i < USER_COUNT; i++) {
    const creds = credentialsFor(i);
    const token = getToken(creds.username, creds.password);
    joinGuild(token, inviteCode);
    if (i % 100 === 99) console.log(`  Joined ${i + 1}/${USER_COUNT} users`);
  }

  console.log(`Setup complete. Guild=${guildId} Channel=${channelId}`);

  return {
    guildId,
    channelId,
    inviteCode,
    adminCreds,
    userCount: USER_COUNT,
  };
}

/** Skip provisioning — read IDs from env, just mint tokens. */
function loadExistingEnvironment() {
  const guildId = __ENV.TEST_GUILD_ID;
  const channelId = __ENV.TEST_CHANNEL_ID;
  if (!guildId || !channelId) {
    throw new Error('SKIP_PROVISION=true requires TEST_GUILD_ID and TEST_CHANNEL_ID env vars');
  }
  return {
    guildId,
    channelId,
    inviteCode: __ENV.TEST_INVITE_CODE || '',
    userCount: USER_COUNT,
  };
}

// ── REST helpers ─────────────────────────────────────────────────────────────

function createGuild(token, name) {
  const res = http.post(
    `${BASE_URL}/api/v1/guild/guilds`,
    JSON.stringify({ name }),
    { headers: { 'Content-Type': 'application/json', Authorization: `Bearer ${token}` } }
  );
  check(res, { 'guild created (200/201)': (r) => r.status === 200 || r.status === 201 });
  return res.json();
}

function getChannels(token, guildId) {
  const res = http.get(
    `${BASE_URL}/api/v1/guild/guilds/${guildId}/channels`,
    { headers: { Authorization: `Bearer ${token}` } }
  );
  check(res, { 'channels fetched': (r) => r.status === 200 });
  return res.json() || [];
}

function createInvite(token, guildId) {
  const res = http.post(
    `${BASE_URL}/api/v1/guild/guilds/${guildId}/invite`,
    null,
    { headers: { Authorization: `Bearer ${token}` } }
  );
  check(res, { 'invite created': (r) => r.status === 200 || r.status === 201 });
  const body = res.json();
  return body.code || body.inviteCode || body;
}

function joinGuild(token, inviteCode) {
  const res = http.post(
    `${BASE_URL}/api/v1/guild/guilds/join/${inviteCode}`,
    null,
    { headers: { Authorization: `Bearer ${token}` } }
  );
  // 200, 201, or 409 (already member) are all acceptable
  return res.status === 200 || res.status === 201 || res.status === 409;
}
