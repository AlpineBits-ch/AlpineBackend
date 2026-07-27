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

// Fetches a real JWT for the bot - the native command-invoke endpoint below is a normal
// venta-JWT-authed endpoint, not Discord-compat, so the packed "Bot" token doesn't apply there.
// Mirrors what BotTokenTranslator does server-side.
async function getBotJwt() {
    const response = await fetch(`${baseUrl}/connect/token`, {
        method: 'POST',
        headers: { 'Content-Type': 'application/x-www-form-urlencoded' },
        body: new URLSearchParams({ grant_type: 'client_credentials', client_id: clientId, client_secret: clientSecret }),
    });
    if (!response.ok) throw new Error(`/connect/token failed: ${response.status}`);
    const body = await response.json();
    return body.access_token;
}

// Invokes a command using the bot's own JWT to stand in for "a human invoked this" - the native
// endpoint only checks the caller has SendMessages in the channel, which the bot itself does once
// installed, so this exercises the exact same dispatch path a real human invocation would without
// needing a separate human test account.
async function invokeCommand(guildId, channelId, commandName) {
    const jwt = await getBotJwt();
    const response = await fetch(`${baseUrl}/api/v1/bots/guilds/${guildId}/channels/${channelId}/interactions`, {
        method: 'POST',
        headers: { Authorization: `Bearer ${jwt}`, 'Content-Type': 'application/json' },
        body: JSON.stringify({ botUserId: clientId, commandName, options: [] }),
    });
    if (!response.ok) throw new Error(`invoke failed: ${response.status} ${await response.text()}`);
}

client.once('ready', async () => {
    clearTimeout(timeout);
    console.log(`READY as ${client.user.tag} (id=${client.user.id})`);
    console.log(`Guilds visible: ${client.guilds.cache.size}`);
    for (const guild of client.guilds.cache.values()) {
        console.log(`  - ${guild.name} (${guild.id}): ${guild.channels.cache.size} channels, ${guild.roles.cache.size} roles`);
    }

    client.on('messageCreate', (message) => {
        console.log(`MESSAGE_CREATE: #${message.channel.id} ${message.author.tag}: ${message.content}`);
    });

    const guild = client.guilds.cache.first();
    const textChannel = guild?.channels.cache.find((c) => c.isTextBased() && !c.isThread());

    if (!guild || !textChannel) {
        console.log('Bot is not installed in any guild with a text channel - skipping the slash-command check.');
    } else {
        try {
            const commandName = `e2e-ping-${Date.now()}`.slice(0, 20);
            console.log(`Registering global command "${commandName}" ...`);
            await client.application.commands.create({ name: commandName, description: 'E2E test command' });

            client.on('interactionCreate', async (interaction) => {
                if (!interaction.isChatInputCommand() || interaction.commandName !== commandName) return;
                console.log(`INTERACTION_CREATE: ${interaction.commandName} in #${interaction.channelId}`);
                await interaction.reply('pong from discord.js e2e smoke test');
                console.log('Replied to the interaction - check the channel for the message.');
            });

            console.log('Invoking the command via the native REST endpoint (simulating a user typing "/")...');
            await invokeCommand(guild.id, textChannel.id, commandName);
        } catch (error) {
            console.error('Slash-command check failed:', error);
        }
    }

    console.log('Listening for 20s (send a message, or check the channel for the command reply) ...');
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
