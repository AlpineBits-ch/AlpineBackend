// Builds `game-catalog.seed.json.gz` — the bootstrap game catalog.
//
// THIS IS A ONE-TIME BOOTSTRAP, NOT A RECURRING JOB. It is checked in so the output is
// reproducible and auditable, not so it can be run on a schedule. Nothing in the running system
// calls discord.com; the seed is committed and served from our own database from then on. See
// docs/specs/game-catalog.md for the reasoning.
//
// Usage (deliberately manual, from a workstation, with no Discord account involved):
//   curl -sL https://discord.com/api/v9/applications/detectable -o detectable.json
//   node build-seed.mjs detectable.json
//
// What is kept: the factual executable -> game mapping, names/aliases, and third-party store SKUs.
// What is deliberately dropped: `icon_hash` / `cover_image_hash`. Those are only usable against
// Discord's CDN, and the artwork decision is to source from Steam appids / IGDB instead — art is
// the game publishers' copyright, not Discord's to sublicense to us. Keeping the hashes would be
// an attractive nuisance and nothing else. Store SKUs are kept generously *because* this only runs
// once: dropping a field here means it cannot be recovered without another fetch.

import { readFileSync, writeFileSync } from 'node:fs';
import { gzipSync } from 'node:zlib';

const input = process.argv[2];
if (!input) {
    console.error('usage: node build-seed.mjs <detectable.json>');
    process.exit(1);
}

const raw = JSON.parse(readFileSync(input, 'utf8'));

// Discord's executable entries are not all plain filenames. Two forms matter:
//   "dead by daylight/deadbydaylight.exe"  — carries a directory component, so matching on the
//                                            process name alone can never hit it.
//   ">something.exe"                       — a negation: this executable must NOT be running.
// Both are preserved structurally rather than flattened, so the matcher can honour them.
const normalizeExecutable = (name) => {
    let n = String(name).trim().toLowerCase().replaceAll('\\', '/');
    const negated = n.startsWith('>');
    if (negated) n = n.slice(1);
    return { name: n, negated };
};

const apps = [];
let negationCount = 0;
let pathQualifiedCount = 0;

for (const app of raw) {
    const executables = [];
    for (const e of app.executables ?? []) {
        if (!e?.name || !e?.os) continue;
        const { name, negated } = normalizeExecutable(e.name);
        if (!name) continue;
        if (negated) negationCount++;
        if (name.includes('/')) pathQualifiedCount++;
        executables.push({ name, os: e.os, isLauncher: !!e.is_launcher, negated });
    }

    // Group store SKUs by distributor. Multiple ids per distributor are common (regional Xbox
    // SKUs, separate Steam entries per edition), so this is a map of arrays, not scalars.
    const stores = {};
    for (const sku of app.third_party_skus ?? []) {
        if (!sku?.distributor || !sku?.id) continue;
        (stores[sku.distributor] ??= []).push(String(sku.id));
    }
    for (const k of Object.keys(stores)) stores[k] = [...new Set(stores[k])];

    const aliases = (app.aliases ?? []).map((a) => String(a).trim()).filter(Boolean);

    // Apps with no executables are still worth keeping: they are exactly the RPC-integrating apps
    // whose application id arrives over the local socket and has to resolve to a trustworthy name.
    apps.push({
        discordApplicationId: String(app.id),
        name: String(app.name ?? '').trim(),
        ...(aliases.length ? { aliases } : {}),
        ...(executables.length ? { executables } : {}),
        ...(Object.keys(stores).length ? { stores } : {}),
    });
}

apps.sort((a, b) => a.discordApplicationId.localeCompare(b.discordApplicationId));

const seed = {
    // Bumped by hand when the seed is regenerated. The seeder writes this to the catalog version
    // row, and the client-facing ETag derives from it.
    version: '2026-08-04.1',
    generatedFrom: 'discord detectable applications (one-time bootstrap)',
    appCount: apps.length,
    apps,
};

const json = JSON.stringify(seed);
const gz = gzipSync(Buffer.from(json, 'utf8'), { level: 9 });
const out = new URL('./game-catalog.seed.json.gz', import.meta.url);
writeFileSync(out, gz);

const withExe = apps.filter((a) => a.executables?.length).length;
const withSteam = apps.filter((a) => a.stores?.steam?.length).length;
console.log(`apps                 ${apps.length}`);
console.log(`  with executables   ${withExe}`);
console.log(`  with steam appid   ${withSteam}`);
console.log(`executable entries   ${apps.reduce((n, a) => n + (a.executables?.length ?? 0), 0)}`);
console.log(`  path-qualified     ${pathQualifiedCount}`);
console.log(`  negated            ${negationCount}`);
console.log(`raw json             ${(json.length / 1048576).toFixed(2)} MiB`);
console.log(`gzipped              ${(gz.length / 1048576).toFixed(2)} MiB`);
