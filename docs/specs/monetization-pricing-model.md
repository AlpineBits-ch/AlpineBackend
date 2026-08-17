# Pricing model, derived from the cost arithmetic

Companion to [monetization.md](monetization.md), which left every number in §3.5 as a placeholder.
This one puts numbers in them and shows the working.

**Modelled, not measured.** Every figure below comes from the bitrate assumptions in
`VoiceUsageRates` driven over modelled rosters, not from a provider invoice. Usage metering will
produce real numbers once deployed; **do not sign a price into a contract or a landing page until it
has.** Two inputs in particular need confirming: the Cloudflare per-GB rate and the free
allowance, and the real-world bitrate mix once simulcast layer selection is live.

---

## 1. The unit cost

One subscriber pulling one stream for one hour:

| Track | Bitrate | GB per subscriber-hour |
|---|---|---|
| Audio (Opus) | 32 kbps | **0.0144** |
| Video 480p30 | 600 kbps | 0.27 |
| Video 720p30 | 1500 kbps | 0.675 |
| Video 1080p30 | 2500 kbps | 1.125 |
| Video 1080p60 | 4000 kbps | 1.80 |
| Video 1440p60 | 6000 kbps | 2.70 |
| Video 2160p60 | 8000 kbps | **3.60** |

At an assumed **$0.05/GB**, one hour of one person watching one 1080p30 stream costs **5.6 cents**.
One hour of one person hearing one audio stream costs **0.07 cents**. That ratio, 78 to 1, is the
whole reason this document exists.

### Why 4K is affordable, and the condition attached

Naively, 4K looks unsellable: one 2160p60 share to 14 viewers is 50.4 GB/hour, $2.52/hour, and a
guild doing 3 h/day of it costs **$227/month** on its own.

That number is wrong, because **nobody watches fourteen 4K streams at 4K**. WP-03 built server-side
simulcast layer selection driven by the subscriber's rendered tile height, and a viewer in a grid
tile is served 720p no matter what the publisher sent. Only whoever fullscreens the share pulls the
top layer.

Realistic mix for that same share, 3 viewers fullscreen and 11 in tiles:

```
3 x 3.60  +  11 x 0.675  =  10.8 + 7.4  =  18.2 GB/hour
```

against **25.2 GB/hour** for the same 14 viewers all pulling 1080p60 with no layer selection. **Offering
4K with simulcast is cheaper than offering 1080p60 without it.**

**The condition:** this is only true once `preferredRid` is actually sent on the Cloudflare subscribe.
That is item 4 of WP-03b and is **now wired**: the server puts the layer it chose from the
subscriber's reported tile height on the subscribe itself, so the SFU serves it rather than the top
layer. Two caveats survive, and both are about publishers rather than about this server. A publisher
that sends a single encoding has no layers to choose between - the subscribe falls back to what
exists rather than to nothing, but it also saves nothing - and a client that never reports a tile
height gets a deliberate guess (the middle layer for a camera in a ranked room, full quality for a
screen share) rather than a measurement. **The mix in the arithmetic above is therefore an upper
bound on the saving until clients report tile sizes and publish simulcast encodings.**

---

## 2. What a guild actually costs

Three archetypes, priced with active-speaker planning **on** (k=5), because that is the state the
product is heading to.

### Quiet guild
20 members, 2 hours a week of voice, about 5 people, no video.

```
5 people below the ranking threshold, so all-to-all: 5 x 4 = 20 streams
20 x 0.0144 GB/h = 0.29 GB/h,  x 8 h/month = 2.3 GB/month
```
**Cost: $0.12/month.** Effectively free. Most guilds are this.

### Active social guild
50 members, 3 h/day voice averaging 8 people, plus 1 h/day of 1080p30 screenshare to 7 viewers.

```
Audio:  8 x 7 = 56 streams x 0.0144 x 90 h  =  72.6 GB
Video:  7 viewers x 1.125 x 30 h            = 236.3 GB
Total                                        = 308.9 GB/month
```
**Cost: $15.45/month.** Per member: **$0.31/month.**

### Heavy guild
200 members, 6 h/day voice averaging 15 people, plus 3 h/day of 1080p60 screenshare to 14 viewers.

```
Audio:  15 x 5 = 75 streams x 0.0144 x 180 h =  194.4 GB
Video:  14 viewers x 1.80 x 90 h             = 2268.0 GB
Total                                         = 2462.4 GB/month
```
**Cost: $123/month.** Per member: **$0.62/month.**

### The number that matters

**An active member costs $0.30 to $0.60 per month in SFU egress.** Everything else - storage, push,
compute, database - is rounding error beside it. That single figure is what any price has to clear.

---

## 3. The finding that changes the plan

**A flat-rate tier cannot bound its own cost, because the worst case is the product of its own
limits.**

Take a Pro tier of 50 participants, 1080p30, 4 concurrent publishers. Its theoretical worst case:

```
4 publishers x 49 subscribers x 1.125 GB/h = 220 GB/hour
sustained 4 h/day = 26.5 TB/month = $1,300/month
```

for a subscription priced somewhere around $40. No plausible price survives that, and the exposure
grows with exactly the numbers that make the tier attractive to sell. This is not a tail risk to be
accepted; it is one determined community away.

So the ceilings cannot be the only lever. **Ceilings shape the product; they do not bound the bill.**

### The resolution: a video allowance with degradation

Add a monthly **video egress allowance** per tier. When it is exhausted, the guild **drops a rung**
(1080p to 720p, then to 480p, then audio-only) for the rest of the cycle. It is never billed for
overage and never cut off.

This is not metered billing, and §3.2's argument against metering still stands: the price is fixed
and knowable in advance. It is §3.3's degrade-do-not-deny applied to a budget instead of a limit, and
it is the only mechanism that makes a flat price safe to offer.

**Audio is never metered and never degraded.** With active-speaker planning a 50-person room costs
3.6 GB/hour, so 100 hours a month is $18 for a room that holds a whole community together. Audio is
the product; video is the cost.

---

## 4. Proposed tiers

| Lever | Free | Plus | Pro |
|---|---|---|---|
| Price | - | **$9/mo** | **$29/mo** |
| Voice participants | 15 | 35 | 75 |
| Video ceiling | 720p30 | 1080p60 | **2160p60 (4K)** |
| Concurrent video publishers | 1 | 3 | 6 |
| **Video allowance** | **75 GB/mo** | **500 GB/mo** | **2,000 GB/mo** |
| Upload size | 35 MB | 150 MB | 500 MB |
| Guild storage | 10 GB | 250 GB | 1 TB |
| Message history | unlimited | unlimited | unlimited |
| Audit log window | 30 d | 90 d | 1 y |
| Custom emoji | 100 | 300 | 750 |
| Bots | 5 | 15 | unlimited |
| Vanity invite | no | no | yes |

**Worst-case egress cost at full allowance:** Free $3.75, Plus $25, Pro $90.

Which means **Pro at $29 with a fully-consumed allowance loses money**, and that is deliberate - see
§6. The allowance is set so that the *typical* guild in each tier sits at 20-40% of it.

Two numbers moved up from the spec's placeholder table on purpose. **Participants went from 10 to
15** because active-speaker planning made large audio rooms cheap and a 10-person cap is the single
most-felt limit in a chat product. **Free upload stays at 35 MB**, which is today's hardcoded
`InstanceDefaultUploadCeilingBytes` - lowering an existing limit is a visible takeaway and is not
worth the few dollars it saves.

### Venta Plus (user subscription) - **$6/mo**

Upload ceiling raised to 150 MB anywhere, **publish above 1080p30 wherever the guild permits it**, 10
devices, animated avatar and banner, longer search window, badge.

**The high-resolution publish right lives here, not in the guild plan**, which is exactly Discord's
structure and the reason Nitro sells. The paired rule already implements it: `voice.video_ceiling`
resolves as `min(guild ceiling, user ceiling)`, so a Pro guild advertises 4K and an individual member
still needs Plus to send it. Both sides monetize from one lever, and the machinery is already built
and tested.

| | Free user | Venta Plus |
|---|---|---|
| Publish ceiling | 1080p30 | the guild's ceiling, up to 4K |
| Watch ceiling | the guild's ceiling | the guild's ceiling |

**Watching is never gated.** Charging someone to see what a friend is already paying to send is the
kind of limit that reads as extraction, and it saves nothing - the stream is already being published.

**Marginal cost: near zero on everything except the 4K publish right**, which is bounded by the
guild's own allowance. The upload ceiling is storage, not egress. This is the highest-margin thing on
the list and it is why the model works.

Priced at $6 rather than $4 because it now carries 4K publishing. Still well under Nitro's $9.99.

### Boosts - **$4/mo**, thresholds at 4 and 12

- **4 boosts** unlocks Plus for the guild.
- **12 boosts** unlocks Pro.

Sanity check against §2: the active social guild costs $15.45/month, and 4 boosts is $16. **Roughly
one boost per 12 active members covers that guild's own bill.** That is the threshold curve's actual
justification, and it is a sentence you can say out loud to a community.

---

## 5. Where the money actually comes from

| Source | Margin | Scales with |
|---|---|---|
| Guild plans | thin, 20-40% | guilds (few) |
| Boosts | break-even by design | engaged members |
| **Venta Plus** | **~95%** | **members (many)** |

**Guild plans and boosts are not the business. They are the mechanism that stops heavy guilds
losing money.** Venta Plus is the business.

At $0.30-0.60 cost per active member per month, a $6 user subscription needs roughly **7-10%
conversion** to cover the whole platform's voice bill. That is around Discord Nitro's reported rate
rather than above it, which is what moving the 4K publish right into Venta Plus bought - the same
lever that makes Pro competitive also makes the user subscription worth buying.

The boost path means any shortfall lands on the guilds generating the cost rather than spread across
everyone.

---

## 6. Deliberate decisions

**Pro's allowance loses money if fully consumed.** A guild that saturates 1,800 GB is a flagship
community, and its members are the population most likely to buy Venta Plus and boosts. The
allowance is a bound on the loss, not a profit centre. If real data shows saturation is common rather
than rare, raise the price rather than cutting the allowance - cutting an allowance people are using
is the worst possible message.

**Message history stays unlimited at every tier.** Retention is the classic SaaS lever and it is
wrong here: the thing being deleted is somebody's conversation with their friends. Absorb the storage
cost; the reputational cost of "your chat is gone" is larger than the saving.

**Moderation, AutoMod and voice channels can never be withheld by a plan.** This is already enforced
in code by `GuildFeatureMap.PlanIndependentFeatures` and pinned by the
`PlanCannotWithholdModerationOrVoice` test, so a badly-configured plan cannot paywall safety tooling
even by accident. What a plan buys is voice *capacity*, never the module's existence.

**Free is a product, not a demo.** 15 participants, 720p, unlimited history and full moderation is a
genuinely usable community server. The tail that costs money is video, and that is where the
allowance bites.

---

## 7. What this needs before it is real

1. **Deploy WP-02 and collect a month.** Every number above is a model. The archetypes in §2 are
   invented; the real distribution of guild behaviour is the thing that decides whether the
   allowances are generous or stingy.
2. **Verify Cloudflare's rate and free allowance against an invoice.** A move from $0.05 to $0.08/GB
   changes Pro from thin to underwater.
3. ~~**Land WP-03b.**~~ Landed. `VOICE_ENFORCE` defaults on, guild channels report speech so they
   rank at all, and unplanned pulls are refused - so the audio figures in §2 are now what a room
   costs rather than what it would cost if clients cooperated. **The reduction has not been measured
   on a live call**, only modelled through WP-02's own arithmetic against modelled rosters; the meter
   now believes the plan, so the first real deployment produces the first honest number.
4. **Add the video allowance as an entitlement key.** The catalogue has no time or volume based
   lever - every existing key is an instantaneous ceiling. `voice.video_allowance_bytes` plus a
   monthly-reset consumption counter is new work, and §3 says it is not optional.
5. **Extend the video ladder with `1440p60` and `2160p60` rungs.** `EntitlementKeys.cs` currently
   tops out at `1080p60`. Note this gap was already found from the other direction: WP-09's survey
   reported that Alpine's quality picker **already offers 1440p and "source"**, neither of which has
   a server rung, so the client is ahead of the server today and the mapping is undefined. Adding the
   rungs also settles that - the (resolution, framerate) to rung mapping is the server's, and a
   client guessing it is making a pricing decision in a `.ts` file.
6. ~~**Wire `preferredRid` (WP-03b item 4) before enabling any rung above 1080p.**~~ Wired. What is
   left of this prerequisite is on the client side: a rung above 1080p is only affordable for viewers
   who are served a lower layer, and that needs publishers sending simulcast encodings and
   subscribers reporting `tileHeights`. **Confirm both in a real client before enabling a 4K rung**,
   because the server asking for a layer that nobody published saves nothing.
7. **Populate `Entitlements:Plans` in Billing's configuration.** `GrantService` refuses a plan name
   the instance has not configured, so none of the plan-shaped paths work until these numbers exist
   in a config file.

---

## 8. Every number here must be editable without a deploy

The tiers in §4 are a starting position, not a settled answer, and the whole point of §7 is that real
usage data will move them. So the catalogue cannot stay a configuration file.

**Plans move into Billing's database, edited from the admin console**, with the configured values
acting as seed and fallback so a fresh or self-hosted instance still boots with something sensible.
Four constraints come with that, and three of them are not obvious:

1. **An edit must not retroactively degrade live paying guilds.** Lowering a limit on a plan that
   1,000 guilds are sitting on would silently take capacity away from people who paid for it, which
   is both a support incident and arguably a contract breach against the drafted terms. So **an edit
   creates a new plan version**, and existing subscribers stay on the version they bought until
   moved deliberately. This is also exactly the grandfathering mechanism monetization.md §7.4 said
   to build early because it is impossible to retrofit fairly.
2. **Show the blast radius before saving.** "This affects 1,240 guilds" is the difference between a
   considered price change and a typo that pages you.
3. **Audit every change like a grant.** Who raised a ceiling, who moved a price, when, and why. This
   surface is more dangerous than the grant surface: a grant affects one subject, a plan edit affects
   every guild on that plan at once.
4. **Validation cannot be bypassed by an admin.** In particular `GuildFeatureMap.PlanIndependentFeatures`
   must still hold, so no plan - however edited - can withhold moderation, AutoMod or voice channels.
   That invariant already exists in code and is pinned by `PlanCannotWithholdModerationOrVoice`; the
   plan editor must sit behind it rather than beside it.

Tracked as **WP-11b** (storage, versioning, validation) with the editing UI folded into **WP-12**.
