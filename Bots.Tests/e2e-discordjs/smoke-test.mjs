// Opt-in, manually-triggered check that a real discord.js bot can point at venta's Discord
// compat surface with nothing but a base-URL change and just work - this IS the acceptance bar
// for the Gateway compat layer, not a simulation of it (see Bots.Tests/Gateway/GatewayLiveE2ETests.cs
// for the hand-rolled ClientWebSocket equivalent).
//
// Usage:
//   npm install
//   BOTS_E2E_CLIENT_ID=user_xxx BOTS_E2E_CLIENT_SECRET=xxx node smoke-test.mjs
// ...or just `node smoke-test.mjs` if ../.e2e-credentials.local.json is populated (git-ignored).

import { readFileSync, existsSync } from 'node:fs';
import { fileURLToPath } from 'node:url';
import { dirname, join } from 'node:path';
import { Client, GatewayIntentBits } from 'discord.js';

const __dirname = dirname(fileURLToPath(import.meta.url));

function loadCredentials() {
    let clientId = process.env.BOTS_E2E_CLIENT_ID;
    let clientSecret = process.env.BOTS_E2E_CLIENT_SECRET;
    let baseUrl = process.env.BOTS_E2E_BASE_URL;

    if (!clientId || !clientSecret) {
        const credentialsPath = join(__dirname, '..', '.e2e-credentials.local.json');
        if (existsSync(credentialsPath)) {
            const fromFile = JSON.parse(readFileSync(credentialsPath, 'utf8'));
            clientId ??= fromFile.clientId;
            clientSecret ??= fromFile.clientSecret;
            baseUrl ??= fromFile.baseUrl;
        }
    }

    return { clientId, clientSecret, baseUrl: baseUrl ?? 'https://api.venta.gg' };
}

const { clientId, clientSecret, baseUrl } = loadCredentials();

if (!clientId || !clientSecret) {
    console.error('Set BOTS_E2E_CLIENT_ID/BOTS_E2E_CLIENT_SECRET, or populate Bots.Tests/.e2e-credentials.local.json.');
    process.exit(1);
}

// Mirrors DiscordCompatToken.Pack() server-side: base64(client_id:client_secret). A real Discord
// bot token is opaque to discord.js too - it's sent verbatim as `Authorization: Bot <token>` and
// as the raw Identify payload `token` field, so our compat-shaped token needs zero library changes.
const compatToken = Buffer.from(`${clientId}:${clientSecret}`).toString('base64');

const client = new Client({
    intents: [
        GatewayIntentBits.Guilds,
        GatewayIntentBits.GuildMessages,
        GatewayIntentBits.MessageContent,
    ],
    // This one option is the entire "migration" a real bot needs: point discord.js's REST client
    // at our compat surface instead of discord.com/api. It fetches the Gateway URL itself via
    // GET /gateway/bot through this same REST client, so no separate WebSocket override is
    // needed - discord.js connects to whatever URL our server returns from that endpoint.
    rest: { api: `${baseUrl}/api/discord` },
});

const timeout = setTimeout(() => {
    console.error('Timed out waiting for READY after 30s.');
    process.exit(1);
}, 30_000);

client.once('ready', () => {
    clearTimeout(timeout);
    console.log(`READY as ${client.user.tag} (id=${client.user.id})`);
    console.log(`Guilds visible: ${client.guilds.cache.size}`);
    for (const guild of client.guilds.cache.values()) {
        console.log(`  - ${guild.name} (${guild.id}): ${guild.channels.cache.size} channels, ${guild.roles.cache.size} roles`);
    }

    client.on('messageCreate', (message) => {
        console.log(`MESSAGE_CREATE: #${message.channel.id} ${message.author.tag}: ${message.content}`);
    });

    console.log('Listening for messages for 20s (send one in an installed guild to verify MESSAGE_CREATE) ...');
    setTimeout(() => {
        client.destroy();
        process.exit(0);
    }, 20_000);
});

client.on('error', (error) => {
    clearTimeout(timeout);
    console.error('discord.js client error:', error);
    process.exit(1);
});

client.on('shardError', (error) => {
    clearTimeout(timeout);
    console.error('Gateway shard error:', error);
    process.exit(1);
});

client.login(compatToken).catch((error) => {
    clearTimeout(timeout);
    console.error('login() rejected:', error);
    process.exit(1);
});
