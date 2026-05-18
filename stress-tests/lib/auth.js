import http from 'k6/http';

const BASE_URL = __ENV.BASE_URL || 'http://localhost:5000';
const CLIENT_ID = __ENV.CLIENT_ID || 'echo';
const TEST_PASSWORD = __ENV.TEST_PASSWORD || 'EchoTest1!';
const USER_PREFIX = __ENV.USER_PREFIX || 'echotest';
// Registration endpoint — adjust if the Identity service differs
const REGISTER_URL = __ENV.REGISTER_URL || `${BASE_URL}/api/v1/identity/authentication/register`;

/**
 * Obtain a JWT access token via OpenIddict password grant.
 * Returns the access_token string or throws on failure.
 */
export function getToken(username, password) {
  const res = http.post(
    `${BASE_URL}/connect/token`,
    {
      grant_type: 'password',
      username,
      password,
      client_id: CLIENT_ID,
      scope: 'openid offline_access',
    },
    { headers: { 'Content-Type': 'application/x-www-form-urlencoded' } }
  );
  if (res.status !== 200) {
    throw new Error(`Token request failed for ${username}: [${res.status}] ${res.body}`);
  }
  return res.json('access_token');
}

/**
 * Register a test user. Idempotent — 409 Conflict is treated as success.
 * Adjust the body shape to match your Identity register endpoint.
 */
export function registerUser(index) {
  const email = `${USER_PREFIX}_${index}@test.echo`;
  const displayName = `TestUser${index}`;
  const birthDate = new Date(2000, 0, 1);

  console.log(`Registering user ${email} ${displayName} with birth date ${birthDate.toISOString()}`);
  const res = http.post(
    REGISTER_URL,
    JSON.stringify({ email, password: TEST_PASSWORD, username: displayName, birthDate }),
    { headers: { 'Content-Type': 'application/json' } }
  );

  const isDuplicate = res.status === 409 ||
    (res.status === 400 && res.body);
  if (res.status !== 200 && res.status !== 201 && !isDuplicate) {
    throw new Error(`Register failed for ${email}: [${res.status}] ${res.body}`);
  }

  return { username: email, password: TEST_PASSWORD };
}

/**
 * Build credentials for VU index i (0-based).
 * Used in default() to derive the user from __VU without re-reading setup data.
 */
export function credentialsFor(index) {
  return {
    username: `${USER_PREFIX}_${index}@test.echo`,
    password: TEST_PASSWORD,
  };
}

/**
 * Acquire an admin token using dedicated admin credentials from env.
 * The admin account must already exist in the Identity service.
 */
export function getAdminToken() {
  const adminUser = __ENV.ADMIN_USER || `${USER_PREFIX}_admin@test.echo`;
  const adminPass = __ENV.ADMIN_PASSWORD || TEST_PASSWORD;
  return getToken(adminUser, adminPass);
}
