# 🦖 In-Game Chat Commands

All commands are typed into **in-game chat** and start with `!`. The bot replies to you privately.

> First time here? Run **`!link`** so we can tie your in-game name to your account, then **`!id`** to see your details.

---

## 👤 Account

### `!id`
Shows your **friendly id**, **steam id**, and linked **in-game name**.
```
!id
```

### `!link`
Links your current in-game name to your account. Run it whenever you change your name.
```
!link
```

---

## 📦 Dino Storage

Store a grown dino and pull it back out later. You start with **5 slots**.

> 🔒 You must be at least **50% grown** to store or load a dino.

### `!store`
Stores your **current dino** (species, growth, health & mutations) into a free slot, then removes it from the world.
```
!store
```

### `!load <slot number>`
Loads a stored dino back onto your **current pawn** and frees the slot. Slot numbers come from `!storage`.
```
!load 1
```

### `!storage`
Shows your **XP**, slot usage, and every stored dino with its species and growth.
```
!storage
```

### `!buyslot`
Buys **one extra storage slot** for **5,000 XP**.
```
!buyslot
```

---

## 🤝 Invites (nest together)

Invite a friend to teleport to you while you're both fresh spawns — great for nesting up together.

> 🥚 **Both** the person inviting **and** the person accepting must be a fresh spawn:
> **≤ 35% growth** and **within 5 minutes of spawning**.
> The person who **accepts** is the one who gets **teleported** to the inviter.

### `!invite <player>`
Sends an invite. The target can be an **in-game name**, a **friendly id**, or a **steam id**.
```
!invite CoolRaptor
!invite k3f9dz
!invite 76561198000000000
```
- If several players share the same in-game name, you'll be asked to **use the friendly id** instead.
- ⏳ 30-second cooldown.

### `!accept [player]`
Accepts an invite and **teleports you to the inviter**. With no argument it accepts your only pending invite; if you have several, it lists them and you pass the inviter's friendly id.
```
!accept
!accept k3f9dz
```

### `!reject [player]`
Declines a pending invite. Same targeting rules as `!accept`.
```
!reject
!reject k3f9dz
```

---

### ℹ️ Notes
- Identifiers are matched in this order: **steam id → friendly id → in-game name**.
- Cooldowns and growth/spawn limits are enforced by the server — you'll get a message telling you why if a command is blocked.
