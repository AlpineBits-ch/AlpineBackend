import http from 'k6/http';
import ws from 'k6/ws';

export const RECORD_SEP = '\x1e';

export const MsgType = {
  Invocation: 1,
  StreamItem: 2,
  Completion: 3,
  StreamInvocation: 4,
  CancelInvocation: 5,
  Ping: 6,
  Close: 7,
};

// Hub paths as exposed through the YARP proxy
export const Hub = {
  Guild: '/api/v1/guild/ws/hubs/guild',
  Messaging: '/api/v1/messaging/ws/hubs/messaging',
  Voice: '/api/v1/messaging/ws/hubs/voice',
};

/**
 * SignalR negotiate - must be called before WebSocket connect.
 * Returns the negotiate response JSON including connectionToken.
 */
export function negotiate(baseUrl, hubPath, token, cookieJar) {
  const res = http.post(
    `${baseUrl}${hubPath}/negotiate?negotiateVersion=1`,
    null,
    {
      headers: {
        Authorization: `Bearer ${token}`,
        'Content-Type': 'application/json',
      },
      jar: cookieJar,
    }
  );
  if (res.status !== 200) {
    throw new Error(`SignalR negotiate failed [${res.status}]: ${res.body}`);
  }
  return res.json();
}

/**
 * Build the WebSocket URL after negotiate.
 * YARP session affinity cookie is carried by the cookieJar automatically;
 * access_token is duplicated in the query string for the SignalR JWT fallback.
 */
export function buildWsUrl(baseUrl, hubPath, connectionToken, token) {
  const wsBase = baseUrl.replace(/^http:/, 'ws:').replace(/^https:/, 'wss:');
  return `${wsBase}${hubPath}?id=${encodeURIComponent(connectionToken)}&access_token=${encodeURIComponent(token)}`;
}

/**
 * Extract session affinity cookie from a CookieJar so it can be forwarded
 * as a header on the WebSocket upgrade request (k6 ws.connect does not
 * share the HTTP CookieJar automatically).
 */
export function extractCookieHeader(cookieJar, targetUrl) {
  const cookies = cookieJar.cookiesForURL(targetUrl);
  return Object.entries(cookies)
    .map(([k, v]) => `${k}=${Array.isArray(v) ? v[0] : v}`)
    .join('; ');
}

/** Parse one or more SignalR records from a raw WebSocket frame. */
export function parseFrames(raw) {
  return raw
    .split(RECORD_SEP)
    .filter((s) => s.length > 0)
    .map((s) => {
      try {
        return JSON.parse(s);
      } catch {
        return null;
      }
    })
    .filter(Boolean);
}

export function encodeFrame(obj) {
  return JSON.stringify(obj) + RECORD_SEP;
}

export function handshakeFrame() {
  return encodeFrame({ protocol: 'json', version: 1 });
}

export function invokeFrame(target, args, invocationId) {
  return encodeFrame({
    type: MsgType.Invocation,
    invocationId: invocationId || `${Date.now()}`,
    target,
    arguments: args || [],
  });
}

export function pingFrame() {
  return encodeFrame({ type: MsgType.Ping });
}

/**
 * Full connect lifecycle helper.
 *
 * Handles:
 *  - negotiate + cookie forwarding for YARP session affinity
 *  - WebSocket connect
 *  - SignalR handshake
 *  - Automatic ping responses
 *  - Caller-supplied onMessage and onOpen callbacks
 *
 * Returns the ws.connect() response object.
 *
 * @param {string}   baseUrl    e.g. "http://echo.internal"
 * @param {string}   hubPath    one of Hub.*
 * @param {string}   token      JWT access token
 * @param {object}   callbacks  { onOpen, onMessage, onClose, onError, durationMs }
 */
export function connectHub(baseUrl, hubPath, token, callbacks = {}) {
  const jar = http.cookieJar();
  const negResult = negotiate(baseUrl, hubPath, token, jar);
  const connectionToken = negResult.connectionToken || negResult.connectionId;

  const wsUrl = buildWsUrl(baseUrl, hubPath, connectionToken, token);
  const cookieHeader = extractCookieHeader(jar, baseUrl);

  const params = {
    headers: {
      Cookie: cookieHeader,
    },
  };

  const duration = callbacks.durationMs || 30000;

  const res = ws.connect(wsUrl, params, (socket) => {
    let handshakeAck = false;

    socket.on('open', () => {
      socket.send(handshakeFrame());
    });

    socket.on('message', (raw) => {
      const frames = parseFrames(raw);
      for (const frame of frames) {
        // First frame after open is the handshake ack: {}
        if (!handshakeAck) {
          handshakeAck = true;
          if (callbacks.onOpen) callbacks.onOpen(socket);
          continue;
        }

        if (frame.type === MsgType.Ping) {
          socket.send(pingFrame());
          continue;
        }

        if (frame.type === MsgType.Close) {
          socket.close();
          return;
        }

        if (callbacks.onMessage) callbacks.onMessage(socket, frame);
      }
    });

    socket.on('close', () => {
      if (callbacks.onClose) callbacks.onClose();
    });

    socket.on('error', (e) => {
      if (callbacks.onError) callbacks.onError(e);
    });

    socket.setTimeout(() => socket.close(), duration);
  });

  return res;
}
