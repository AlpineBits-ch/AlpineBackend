/*
 * The moderation console.
 *
 * Vanilla, no build step - the pages are served straight out of wwwroot by a .NET container with no
 * Node in it, and a build step would mean the console could silently be stale relative to the
 * source it ships beside.
 *
 * Same-origin throughout: the API and /connect/token answer on this hostname too, so there is no
 * base URL and no CORS entry. The console is what is host-gated; the API is not.
 *
 * Nothing here uses innerHTML. Every string rendered below is a username, a note a moderator typed,
 * or a report body written by whoever filed it - a console that pipes those into markup is a console
 * where filing a report is how you attack the people reviewing it.
 */
(() => {
    'use strict';

    const API = '/api/v1/admin';
    const TOKEN_KEY = 'venta.moderation.token';
    const REFRESH_KEY = 'venta.moderation.refresh';

    let session = null;
    let view = 'queue';
    let selectedId = null;

    // ── DOM helpers ─────────────────────────────────────────────────────────

    const $ = (selector, root = document) => root.querySelector(selector);
    const $$ = (selector, root = document) => [...root.querySelectorAll(selector)];

    function el(tag, className, text) {
        const node = document.createElement(tag);
        if (className) node.className = className;
        if (text !== undefined && text !== null) node.textContent = text;
        return node;
    }

    function icon(name) {
        const node = document.createElement('i');
        node.dataset.icon = name;
        return node;
    }

    function tag(text, kind) {
        return el('span', kind ? `tag ${kind}` : 'tag', text);
    }

    /**
     * Relative in the row. A queue is read by age, not by timestamp.
     *
     * The year is carried on anything past a month, because a queue sorted by age puts the oldest
     * rows together at one end and "Mar 4" above "Mar 11" says nothing about whether the gap between
     * them is a week or three years.
     */
    function ago(iso) {
        if (!iso) return '';

        const seconds = (Date.now() - new Date(iso)) / 1000;
        if (seconds < 60) return 'just now';
        if (seconds < 3600) return `${Math.floor(seconds / 60)}m`;
        if (seconds < 86400) return `${Math.floor(seconds / 3600)}h`;
        if (seconds < 2592000) return `${Math.floor(seconds / 86400)}d`;
        return new Date(iso).toLocaleDateString(undefined, { month: 'short', day: 'numeric', year: 'numeric' });
    }

    /** The relative age as a row cell, with the absolute time on hover. `ago` returns a string, so
     *  the hover this file has always claimed to have needs a node to hang off. */
    function agoNode(iso) {
        const node = el('span', 'rw-time', ago(iso));
        if (iso) node.title = stamp(iso);
        return node;
    }

    /**
     * An absolute time, with the zone it is in.
     *
     * Moderators on the same account are rarely in the same place, and a suspension that ends
     * "at 9pm" is a different instruction in Berlin than in Denver. Built from parts rather than
     * concatenated: dateStyle and timeStyle cannot be combined with timeZoneName, and pasting the
     * abbreviation on afterwards puts it on the wrong side of the string in half the locales.
     */
    const STAMP_FORMAT = new Intl.DateTimeFormat(undefined, {
        year: 'numeric', month: 'short', day: 'numeric',
        hour: 'numeric', minute: '2-digit',
        timeZoneName: 'short',
    });

    function stamp(iso) {
        return iso ? STAMP_FORMAT.format(new Date(iso)) : '-';
    }

    /**
     * The words this console already uses for an enum, and a readable fallback for the ones nobody
     * has written wording for yet.
     *
     * The override map is not decoration: these are the phrasings the lists have shipped with, and a
     * screen that says "Under review" beside one that says "Being reviewed" reads as two different
     * states to the person triaging both.
     *
     * Entitlement sources are deliberately absent. Billing has its own map in SOURCE_TEXT, which is
     * the one the provenance rows read from; wording them twice is how the two drift apart.
     */
    const ENUM_TEXT = {
        UnderReview: 'Being reviewed',
        AwaitingRequester: 'With them',
        PendingDeletion: 'Pending deletion',
        ActionTaken: 'Action taken',
    };

    function humanEnum(value) {
        if (!value) return value;
        if (ENUM_TEXT[value]) return ENUM_TEXT[value];

        const words = String(value)
            .replace(/([a-z0-9])([A-Z])/g, '$1 $2')
            .replace(/([A-Z]+)([A-Z][a-z])/g, '$1 $2')
            .split(' ');

        // Sentence case rather than title case - these sit inside sentences and beside prose, not in
        // headings. A run of capitals is left as it is: it is an acronym, and lowercasing it costs
        // the reader the word.
        return words
            .map((word, index) => word.length > 1 && word === word.toUpperCase()
                ? word
                : index === 0
                    ? word.charAt(0).toUpperCase() + word.slice(1).toLowerCase()
                    : word.toLowerCase())
            .join(' ');
    }

    function toast(kind, message) {
        const box = el('div', `toast ${kind}`);
        box.append(icon(kind === 'ok' ? 'check-circle' : kind === 'danger' ? 'exclamation-circle' : 'info-circle'));
        box.append(el('div', 'grow', message));

        $('#toasts').append(box);
        setTimeout(() => box.remove(), kind === 'danger' ? 9000 : 4500);
    }

    function empty(message, iconName = 'check-circle') {
        const box = el('div', 'empty');
        box.append(icon(iconName));
        box.append(el('div', null, message));
        return box;
    }

    // ── Auth ────────────────────────────────────────────────────────────────

    const tokens = {
        get access() { return sessionStorage.getItem(TOKEN_KEY); },
        get refresh() { return sessionStorage.getItem(REFRESH_KEY); },
        set(access, refresh) {
            // sessionStorage, not localStorage: a moderation session should not outlive the tab it
            // was opened in. A staff token sitting in localStorage on a shared machine is the
            // easiest way to hand someone the whole console.
            sessionStorage.setItem(TOKEN_KEY, access);
            if (refresh) sessionStorage.setItem(REFRESH_KEY, refresh);
        },
        clear() {
            sessionStorage.removeItem(TOKEN_KEY);
            sessionStorage.removeItem(REFRESH_KEY);
        },
    };

    async function grant(body) {
        const response = await fetch('/connect/token', {
            method: 'POST',
            headers: { 'Content-Type': 'application/x-www-form-urlencoded' },
            body: new URLSearchParams(body),
        });

        const text = await response.text();

        if (!response.ok) {
            // The token endpoint answers MFA states as a bare 401 body rather than as JSON.
            if (text.includes('mfa_required')) throw Object.assign(new Error('mfa_required'), { mfa: true });
            if (text.includes('mfa_invalid')) throw Object.assign(new Error('That code was not accepted.'), { mfa: true });

            let payload = null;
            try { payload = JSON.parse(text); } catch { /* not JSON */ }

            throw new Error(payload?.error_description || 'That email or password is not right.');
        }

        return JSON.parse(text);
    }

    /**
     * The in-flight refresh, if there is one.
     *
     * Single-flighted because this console fires several requests at once - the view render and the
     * badge poll land on the same tick - so an expired access token produces two or three
     * simultaneous 401s. Each starting its own refresh means several grants racing to write
     * `tokens.set`, with the last writer winning and the others' tokens orphaned. One promise, every
     * caller awaits it.
     */
    let refreshing = null;

    function refreshOnce() {
        refreshing ??= grant({
            grant_type: 'refresh_token',
            client_id: 'echo',
            refresh_token: tokens.refresh,
        })
            .then(result => {
                tokens.set(result.access_token, result.refresh_token);
                return result;
            })
            .finally(() => { refreshing = null; });

        return refreshing;
    }

    /**
     * One request wrapper, with a single refresh attempt on 401.
     *
     * The retry is deliberately not a loop: if a refreshed token is also rejected, the session is
     * genuinely over and looping just delays telling the operator that.
     *
     * `raw` sends a body that is not JSON - today the NDJSON pieces of a catalog extract - with
     * `contentType` naming it. It is a buffer rather than a stream on purpose: the 401 path below
     * resends the request, and a stream cannot be replayed, so a token expiring midway through a
     * forty-piece upload would silently drop whichever piece was in flight.
     */
    async function call(method, path, { body, raw, contentType, query, signal, retried = false } = {}) {
        const url = new URL(path, location.origin);
        if (query) {
            Object.entries(query).forEach(([k, v]) => {
                if (v !== null && v !== undefined && v !== '') url.searchParams.set(k, v);
            });
        }

        const response = await fetch(url, {
            method,
            headers: {
                Authorization: `Bearer ${tokens.access}`,
                ...(contentType ? { 'Content-Type': contentType }
                    : body ? { 'Content-Type': 'application/json' } : {}),
            },
            body: raw !== undefined ? raw : body ? JSON.stringify(body) : undefined,
            signal,
        });

        if (response.status === 401 && !retried && tokens.refresh) {
            try {
                await refreshOnce();
                return call(method, path, { body, raw, contentType, query, signal, retried: true });
            } catch {
                signOut();
                throw fail(401, 'session_expired', 'Your session expired. Sign in again.');
            }
        }

        if (response.status === 401) {
            signOut();
            throw fail(401, 'session_expired', 'Your session expired. Sign in again.');
        }

        const text = await response.text();
        let payload = null;
        try { payload = text ? JSON.parse(text) : null; } catch { /* not JSON */ }

        if (!response.ok) {
            // The status and the server's own code travel with the error. Callers need to tell a
            // dead session from a dependency that is briefly unavailable, and a message string is
            // not something to make that decision on.
            throw fail(response.status, payload?.code,
                payload?.message || `The server answered ${response.status}.`);
        }

        return payload;
    }

    function fail(status, code, message) {
        return Object.assign(new Error(message), { status, code });
    }

    /** Worth waiting out rather than signing out for: the gateway could not reach Identity, or
     *  something downstream is briefly unwell. The session itself is untouched. */
    function isTransient(error) {
        return error.code === 'staff_check_unavailable'
            || (typeof error.status === 'number' && error.status >= 500)
            || error.status === undefined;   // fetch itself threw: offline, DNS, dropped connection
    }

    function busy(button, on) {
        const label = button.dataset.label || button.textContent.trim();
        button.disabled = on;

        const mark = icon(on ? 'spinner' : 'lock');
        if (on) mark.classList.add('spin');

        button.replaceChildren(mark, document.createTextNode(` ${on ? 'Working…' : label}`));
    }

    $('#gate-host').textContent = location.hostname;

    $('#signin-form').addEventListener('submit', async event => {
        event.preventDefault();

        const button = $('#g-submit');
        const errors = $('#signin-error');
        errors.replaceChildren();
        busy(button, true);

        const mfa = $('#g-mfa').value.trim();

        try {
            const result = await grant({
                grant_type: 'password',
                client_id: 'echo',
                username: $('#g-user').value.trim(),
                password: $('#g-pass').value,
                // Only the two protocol scopes, which is what the desktop client asks for.
                //
                // `profile` and `email` are rejected with invalid_scope: OpenIddict validates a
                // requested scope against the scopes *registered on the server*, and Identity never
                // calls RegisterScopes. The `echo` application does carry scp:profile and scp:email
                // permissions, but a permission is a per-client allowlist over registered scopes -
                // it does not register anything, so the allowlist points at scopes that do not
                // exist. Asking for them fails before the client is even consulted.
                //
                // Nothing here needs them anyway: the console gets its identity and its tier from
                // GET /api/v1/admin/session, resolved from the account row on every request, which
                // is deliberately not a token claim (a claim outlives a demotion).
                scope: 'openid offline_access',
                // Names the row this sign-in adds to the operator's own "Active sessions" list.
                // Without it CreateSession falls back to the raw User-Agent, and because the console
                // holds its session per tab, a day's work leaves a column of identical Mozilla/5.0
                // entries that nobody can tell apart or safely revoke.
                device_name: 'Moderation console',
                ...(mfa ? { mfa_code: mfa } : {}),
            });

            tokens.set(result.access_token, result.refresh_token);
            await start();
        } catch (error) {
            if (error.message === 'mfa_required') {
                $('#mfa-field').classList.remove('hidden');
                $('#g-mfa').focus();
                errors.replaceChildren(banner('info', 'This account has two-factor sign-in. Enter your code.'));
            } else {
                if (error.mfa) $('#mfa-field').classList.remove('hidden');
                errors.replaceChildren(banner('danger', error.message));
            }
        } finally {
            busy(button, false);
        }
    });

    function banner(kind, message) {
        const box = el('div', `notice ${kind}`);
        box.append(icon(kind === 'danger' ? 'exclamation-circle' : 'info-circle'));
        box.append(el('div', 'grow', message));
        return box;
    }

    function signOut() {
        tokens.clear();
        session = null;
        $('#app').classList.add('hidden');
        $('#gate').classList.remove('hidden');
    }

    // The button revokes; signOut() on its own does not. The difference matters because signOut() is
    // also how a dead session ends, and a token that was just refused has nothing left to give back.
    // Pressing this is the one moment the operator means "end it on the server too", and on a shared
    // machine that is the difference between forgetting the refresh token here and leaving it
    // redeemable for another fortnight.
    $('#sign-out').addEventListener('click', () => {
        const refresh = tokens.refresh;

        if (refresh) {
            // Not awaited, and keepalive so it still goes if the tab is closed on the same gesture. A
            // revoke that cannot be delivered must never be a reason somebody stays signed in on
            // screen, so the local sign-out below happens either way.
            fetch('/connect/revoke', {
                method: 'POST',
                headers: { 'Content-Type': 'application/x-www-form-urlencoded' },
                body: new URLSearchParams({
                    token: refresh,
                    token_type_hint: 'refresh_token',
                    client_id: 'echo',
                }),
                keepalive: true,
            }).catch(() => {});
        }

        signOut();
    });

    /**
     * The staff check could not be completed. Waits, retries, and if it still cannot, says so and
     * offers a button - without touching the tokens.
     *
     * Two automatic attempts before bothering anybody: the common case is a service restarting, and
     * it is back within a few seconds. Signing the operator out instead is what this replaces.
     */
    async function stall(error, attempt) {
        if (attempt < 2) {
            $('#signin-error').replaceChildren(banner('info', 'Checking your access...'));
            await new Promise(resolve => setTimeout(resolve, 1200 * (attempt + 1)));
            return start(attempt + 1);
        }

        const box = banner('warn', error.code === 'staff_check_unavailable'
            ? 'We could not verify your access just now. You are still signed in - this is on our side.'
            : 'We could not reach the server. You are still signed in.');

        const retry = el('button', 'btn sm');
        retry.type = 'button';
        retry.append(icon('refresh'), document.createTextNode(' Try again'));
        retry.addEventListener('click', () => start());

        box.append(retry);
        $('#signin-error').replaceChildren(box);
    }

    /**
     * Brings up the console for whatever session is already in hand.
     *
     * <b>Only a definitive answer discards the session.</b> This used to clear the tokens on any
     * failure at all, which meant a moderator was thrown back to the sign-in form - and had to retype
     * their password - every time the staff check could not be completed. That check asks Identity
     * over the bus on every request and fails closed by design, so one slow RabbitMQ round trip, one
     * Identity restart, one dropped connection was a full sign-out. It is by far the most likely
     * reason the console feels like it logs you out constantly.
     *
     * Now: 401 and "you are not staff" clear the session, because both are final. Anything else
     * keeps it and offers a retry.
     */
    async function start(attempt = 0) {
        try {
            session = await call('GET', `${API}/session`);
        } catch (error) {
            if (isTransient(error)) return stall(error, attempt);

            // A correct password on a non-staff account lands here. Saying so is the whole reason
            // this call exists - the alternative is showing an empty queue to someone who will
            // reasonably conclude the console is broken.
            tokens.clear();
            $('#signin-error').replaceChildren(banner('danger',
                error.code === 'staff_required'
                    ? 'That account is not a moderator or administrator on this instance.'
                    : error.message));
            return;
        }

        $('#gate').classList.add('hidden');
        $('#app').classList.remove('hidden');

        $('#who-name').textContent = session.userName || session.userId;
        $('#who-role').textContent = session.role;

        $$('.admin-only').forEach(node => node.classList.toggle('hidden', !session.canViewAudit));

        go(location.hash.replace('#', '') || 'queue');
        refreshBadges();
        setInterval(refreshBadges, 60_000);
    }

    // ── Badge counts ────────────────────────────────────────────────────────

    async function refreshBadges() {
        if (!session) return;

        try {
            const { moderation } = await call('GET', `${API}/stats`);

            setBadge('#badge-reports', moderation.openReports, moderation.criticalReports > 0);
            setBadge('#badge-appeals', moderation.openAppeals, false);
            setBadge('#badge-tickets', moderation.ticketsAwaitingStaff, false);
        } catch { /* a badge is not worth a toast */ }

        try {
            // Counts open incidents, and burns hot on the ones the detector published that nobody
            // has acknowledged - those resolve themselves at the first clean window, so an operator
            // who wants to keep one needs to be told it exists.
            const { total, unconfirmed } = await call('GET', `${API}/status/incidents`, {
                query: { openOnly: true, limit: 1 },
            });

            setBadge('#badge-status', total, unconfirmed > 0);
        } catch { /* same */ }
    }

    function setBadge(selector, count, hot) {
        const node = $(selector);
        node.textContent = count > 0 ? String(count) : '';
        node.classList.toggle('hot', Boolean(hot));
    }

    // ── Views ───────────────────────────────────────────────────────────────

    const views = {};

    function go(name) {
        if (!views[name]) name = 'queue';

        // Admin-only views are declared once, in the markup. Reading the guard off the same
        // attribute the rail hides on means a hash typed by hand cannot reach a view the rail is
        // hiding - the two can never disagree.
        const item = $(`.rail-item[data-view="${name}"]`);
        if (item?.classList.contains('admin-only') && !session?.canViewAudit) name = 'queue';

        view = name;
        selectedId = null;
        closeDetail();

        $$('.rail-item').forEach(item => {
            item.setAttribute('aria-current', item.dataset.view === name ? 'page' : 'false');
        });

        $('#view-title').textContent = views[name].title;
        $('#view-tools').replaceChildren(...(views[name].tools?.() || []));

        if (location.hash !== `#${name}`) history.replaceState(null, '', `#${name}`);

        render();
    }

    $$('.rail-item').forEach(item => item.addEventListener('click', () => go(item.dataset.view)));
    $('#refresh').addEventListener('click', () => { render(); refreshBadges(); });
    addEventListener('hashchange', () => go(location.hash.replace('#', '')));

    async function render() {
        const host = $('#view');
        host.replaceChildren(loading());

        try {
            const content = await views[view].render();
            host.replaceChildren(content);
        } catch (error) {
            host.replaceChildren(banner('danger', error.message));
        }
    }

    function loading() {
        const box = el('div', 'empty');
        const mark = icon('spinner');
        mark.classList.add('spin');
        box.append(mark, el('div', null, 'Loading…'));
        return box;
    }

    // ── Detail pane ─────────────────────────────────────────────────────────

    /** Whoever had focus when the pane opened, so closing it can hand focus back rather than
     *  dropping it on the body somewhere above the row that is being worked. */
    let detailOpener = null;

    function openDetail(title, content) {
        // The pane sits after the list in DOM order, so a keyboard user who opens a row would
        // otherwise have to tab through every remaining row to reach what they just opened. Focus
        // goes to the heading rather than to the close button: the heading names what arrived, and
        // the close button is the one thing in the pane nobody opened it to press.
        detailOpener = document.activeElement;

        $('#detail-title').textContent = title;
        $('#detail-body').replaceChildren(content);
        $('#detail').classList.remove('hidden');
        $('#detail-title').focus();
    }

    function closeDetail() {
        const pane = $('#detail');
        const held = pane.contains(document.activeElement);

        pane.classList.add('hidden');
        $('#detail-body').replaceChildren();
        $$('.rw, .plan-row').forEach(row => row.setAttribute('aria-selected', 'false'));

        // Only reclaim focus if the pane actually had it. `go()` closes the pane as part of changing
        // view, and yanking focus back to a row that is about to be replaced by a different view's
        // rows is worse than leaving it where the click put it.
        if (!held) { detailOpener = null; return; }

        const back = detailOpener?.isConnected ? detailOpener : $('.rw') || $('#refresh');
        back?.focus();
        detailOpener = null;
    }

    $('#detail-close').addEventListener('click', () => { selectedId = null; closeDetail(); });

    // ── Keyboard ────────────────────────────────────────────────────────────

    /*
     * List navigation, for a tool that is shaped like a queue.
     *
     * Two scoping rules, and everything below obeys both. Nothing fires while a modal is open - the
     * modal owns Escape and Tab, and a shortcut that reaches past it would act on a list the operator
     * cannot see. Nothing fires while the caret is in a field either, because there `j` and `k` are
     * letters somebody is typing into a suspension note, not commands.
     *
     * The cursor is the row's own `aria-selected`, not a variable: the list is rebuilt on every
     * render, so any element or index kept here would be pointing at a row that no longer exists.
     */
    function modalOpen() {
        return !$('#modal-host').classList.contains('hidden');
    }

    function typingInto(node) {
        return node instanceof HTMLElement
            && (node.isContentEditable || ['INPUT', 'TEXTAREA', 'SELECT'].includes(node.tagName));
    }

    function visibleRows() {
        return $$('.rw').filter(node => node.getClientRects().length > 0);
    }

    /** Answers whether it moved, so a view with no rows in it - the overview, the billing document -
     *  leaves the arrow keys to the scroller they belong to. */
    function moveRow(delta) {
        const rows = visibleRows();
        if (!rows.length) return false;

        const at = rows.findIndex(node => node.getAttribute('aria-selected') === 'true');
        const next = at < 0
            ? (delta > 0 ? 0 : rows.length - 1)
            : Math.min(rows.length - 1, Math.max(0, at + delta));

        rows[next].scrollIntoView({ block: 'nearest' });
        rows[next].click();   // the row's own handler marks the selection and opens the detail
        return true;
    }

    function openSelectedRow() {
        visibleRows().find(node => node.getAttribute('aria-selected') === 'true')?.click();
    }

    addEventListener('keydown', event => {
        if (modalOpen() || event.ctrlKey || event.metaKey || event.altKey) return;

        // Escape is the one key that still works from inside a field: it is how you get out of one.
        if (event.key === 'Escape') {
            if ($('#detail').classList.contains('hidden')) return;
            event.preventDefault();
            selectedId = null;
            closeDetail();
            return;
        }

        if (typingInto(document.activeElement)) return;

        if (event.key === 'j' || event.key === 'ArrowDown') { if (moveRow(1)) event.preventDefault(); return; }
        if (event.key === 'k' || event.key === 'ArrowUp') { if (moveRow(-1)) event.preventDefault(); return; }

        // A row is a button, so the browser already opens the one that has focus. Enter is only
        // wired for the case where focus has moved on - into the detail pane, most often - and the
        // selected row is still the row the operator means.
        if (event.key === 'Enter') {
            if (document.activeElement?.closest('.rw')) return;
            openSelectedRow();
            return;
        }

        if (event.key === '/') {
            const search = $('#view-tools input[type="search"]')
                || $('#view input[type="search"]')
                || $('#view-tools input')
                || $('#view-tools select');

            if (!search) return;
            event.preventDefault();   // otherwise the slash lands in the field we just focused
            search.focus();
            search.select?.();
        }
    });

    // ── Modal ───────────────────────────────────────────────────────────────

    const FOCUSABLE = [
        'a[href]', 'button:not([disabled])', 'input:not([disabled])',
        'select:not([disabled])', 'textarea:not([disabled])', '[tabindex]:not([tabindex="-1"])',
    ].join(', ');

    /** One counter for every generated id in the file, so a heading and an error message can never
     *  collide on one. */
    let idSeq = 0;

    function modal(build) {
        const host = $('#modal-host');
        const box = $('#modal');
        const app = $('#app');

        // Stashed before the modal is built, because building it is what steals the focus. A
        // moderator who opens a suspension dialog off a row and cancels it belongs back on that row,
        // not on the body at the top of a list they have to find their place in again.
        const opener = document.activeElement;

        let confirming = null;

        function close() {
            host.classList.add('hidden');
            box.replaceChildren();
            box.removeAttribute('aria-labelledby');
            box.removeAttribute('aria-label');
            removeEventListener('keydown', onKey, true);

            // The rest of the console goes back to being reachable. `inert` is what actually stops
            // Tab and the pointer; aria-hidden is there for the assistive tech that predates it.
            app.removeAttribute('inert');
            app.removeAttribute('aria-hidden');

            confirming = null;
            if (opener?.isConnected) opener.focus();
        }

        function focusables() {
            return $$(FOCUSABLE, box).filter(node => node.getClientRects().length > 0);
        }

        /**
         * The initial value of every control the builder put in the modal.
         *
         * Recorded rather than read back off `defaultValue`, because `select()` sets `selected` on an
         * option as a property, which never becomes the default the DOM would report.
         */
        const initial = new Map();

        function valueOf(control) {
            return control.type === 'checkbox' || control.type === 'radio'
                ? String(control.checked)
                : control.value;
        }

        function pristine(control) {
            // Controls a builder added after the modal opened - one more rule row, say - are not in
            // the snapshot, so they are compared against the DOM's own idea of untouched.
            if (initial.has(control)) return initial.get(control);
            if (control.type === 'checkbox' || control.type === 'radio') return String(control.defaultChecked);
            if (control.tagName === 'SELECT') {
                return [...control.options].find(option => option.defaultSelected)?.value
                    ?? control.options[0]?.value ?? '';
            }
            return control.defaultValue ?? '';
        }

        function dirty() {
            return $$('input, textarea, select', box)
                .some(control => valueOf(control) !== pristine(control));
        }

        /**
         * Escape and the backdrop are both easy to hit by accident, and half a written appeal
         * decision is not something to lose without being asked about it. The builder's own Cancel
         * button keeps closing immediately: that one was aimed at.
         *
         * Asked inside the modal rather than through `window.confirm`, which blocks the whole tab and
         * cannot be styled, read out, or dismissed with the keyboard the way the rest of this is.
         */
        function tryClose() {
            if (confirming) return;
            if (!dirty()) return close();

            confirming = el('div', 'modal-confirm');
            confirming.append(el('p', null, 'Discard your changes?'));

            const row = el('div', 'btn-row');
            const keep = button('Keep editing', 'check', () => {
                confirming?.remove();
                confirming = null;
                box.querySelector('input, textarea, select')?.focus();
            }, 'ghost');

            row.append(keep, button('Discard', 'times', close, 'danger'));
            confirming.append(row);
            box.append(confirming);
            keep.focus();   // the safe half of a two-button question is the one that should be armed
        }

        function onKey(event) {
            if (event.key === 'Escape') {
                event.preventDefault();
                event.stopPropagation();

                // Escape out of the discard question means "no, keep editing", not "discard".
                if (confirming) {
                    confirming.remove();
                    confirming = null;
                    box.querySelector('input, textarea, select')?.focus();
                    return;
                }

                tryClose();
                return;
            }

            if (event.key !== 'Tab') return;

            // Without this, Tab walks straight out of the dialog and into the list behind it, which
            // the backdrop stops the mouse reaching but does nothing about the keyboard.
            const items = focusables();
            if (!items.length) return;

            const first = items[0];
            const last = items[items.length - 1];
            const active = document.activeElement;

            if (event.shiftKey && (active === first || !box.contains(active))) {
                event.preventDefault();
                last.focus();
            } else if (!event.shiftKey && (active === last || !box.contains(active))) {
                event.preventDefault();
                first.focus();
            }
        }

        box.replaceChildren(build(close));
        host.classList.remove('hidden');

        // Every builder in this file opens with an h2. Naming the dialog off it means the heading is
        // read on arrival instead of a bare "dialog"; the fallback is for a builder that does not.
        const heading = box.querySelector('h2');
        if (heading) {
            heading.id ||= `modal-title-${++idSeq}`;
            box.setAttribute('aria-labelledby', heading.id);
        } else {
            box.setAttribute('aria-label', 'Dialog');
        }

        app.setAttribute('inert', '');
        app.setAttribute('aria-hidden', 'true');

        $$('input, textarea, select', box).forEach(control => initial.set(control, valueOf(control)));

        // Capturing, so the trap sees Tab and Escape before anything the builder bound.
        addEventListener('keydown', onKey, true);

        $('.modal-backdrop').onclick = tryClose;
        box.querySelector('input, textarea, select, button')?.focus();

        return close;
    }

    /**
     * A labelled control.
     *
     * `options.required` marks the label rather than the control: a required field has to be
     * tellable apart before it is filled in and refused, and the marker is the only part of that
     * which survives a screenshot pasted into a ticket.
     */
    function field(label, control, hint, options = {}) {
        const wrap = el('div', 'field');
        const text = el('label', null, label);

        if (options.required) {
            const mark = el('span', 'req', '*');
            mark.title = 'Required';
            mark.setAttribute('aria-label', '(required)');
            text.append(mark);

            if (control.matches?.('input, textarea, select')) control.setAttribute('aria-required', 'true');
        }

        wrap.append(text, control);
        if (hint) wrap.append(el('p', 'hint', hint));
        return wrap;
    }

    /**
     * A refusal, next to the control that caused it.
     *
     * Not a toast: a toast naming a field is read after the eye has already left it, and it is gone
     * four seconds later. Tracked per control rather than per wrapper so a `.field` holding two
     * inputs can refuse one of them without wiping the other's message.
     */
    const fieldErrors = new WeakMap();

    function fieldError(control, message) {
        clearFieldError(control);

        const note = el('p', 'field-error');
        note.id = `field-error-${++idSeq}`;
        note.append(icon('exclamation-circle'), el('span', null, message));

        control.setAttribute('aria-invalid', 'true');
        control.setAttribute('aria-describedby', note.id);

        const wrap = control.closest?.('.field');
        if (wrap) wrap.append(note); else control.after(note);

        fieldErrors.set(control, note);

        // A message still sitting under a field somebody has since corrected is the console arguing
        // with itself, so the first keystroke takes it away.
        control.addEventListener('input', () => clearFieldError(control), { once: true });
        control.focus();

        return note;
    }

    function clearFieldError(control) {
        fieldErrors.get(control)?.remove();
        fieldErrors.delete(control);
        control.removeAttribute('aria-invalid');
        control.removeAttribute('aria-describedby');
    }

    function select(options, value) {
        const node = el('select');
        options.forEach(([v, text]) => {
            const option = el('option', null, text);
            option.value = v;
            if (v === value) option.selected = true;
            node.append(option);
        });
        return node;
    }

    // Written for people, not enum names. A moderator picking "HateSpeech" from a dropdown is
    // reading a database column; the person on the other end reads the same words in their email.
    const REASONS = [
        ['Spam', 'Spam or unsolicited advertising'],
        ['Harassment', 'Harassment or targeted abuse'],
        ['HateSpeech', 'Hateful conduct'],
        ['ViolentThreats', 'Threats of violence'],
        ['SelfHarm', 'Content promoting self-harm'],
        ['SexualContent', 'Unwanted sexual content'],
        ['ChildSafety', 'Content endangering a minor'],
        ['Impersonation', 'Impersonation'],
        ['Malware', 'Malware or malicious links'],
        ['IllegalContent', 'Illegal content'],
        ['Other', 'Breach of the community rules'],
    ];

    const REASON_TEXT = Object.fromEntries(REASONS);

    const PRIORITY_CLASS = {
        Critical: 'critical', High: 'high', Normal: 'normal', Low: '',
    };

    // ── Rows ────────────────────────────────────────────────────────────────

    function row({ mark, title, sub, side, selected, onOpen }) {
        const button = el('button', 'rw');
        button.setAttribute('aria-selected', String(Boolean(selected)));

        button.append(el('div', `rw-mark ${mark || ''}`));

        const main = el('div', 'rw-main');
        const heading = el('div', 'rw-title');
        title.forEach(part => heading.append(part));
        main.append(heading);
        if (sub) main.append(el('div', 'rw-sub', sub));
        button.append(main);

        const aside = el('div', 'rw-side');
        (side || []).forEach(part => aside.append(part));
        button.append(aside);

        button.addEventListener('click', () => {
            $$('.rw').forEach(other => other.setAttribute('aria-selected', 'false'));
            button.setAttribute('aria-selected', 'true');
            onOpen();
        });

        return button;
    }

    function listFoot(total, shown) {
        return el('div', 'list-foot', total > shown
            ? `Showing ${shown} of ${total}`
            : `${total} ${total === 1 ? 'item' : 'items'}`);
    }

    // ── Blocks ──────────────────────────────────────────────────────────────

    function block(heading, ...children) {
        const box = el('div', 'block');
        if (heading) box.append(el('h3', null, heading));
        children.forEach(child => child && box.append(child));
        return box;
    }

    function kv(pairs) {
        const list = el('dl', 'kv');
        pairs.forEach(([key, value]) => {
            if (value === null || value === undefined) return;
            list.append(el('dt', null, key));
            list.append(value instanceof Node ? wrapDd(value) : el('dd', null, String(value)));
        });
        return list;
    }

    function wrapDd(node) {
        const dd = el('dd');
        dd.append(node);
        return dd;
    }

    function copyable(text) {
        const button = el('button', 'btn ghost sm mono');
        button.append(document.createTextNode(text));
        button.title = 'Copy';
        button.addEventListener('click', event => {
            event.stopPropagation();
            navigator.clipboard?.writeText(text).then(() => toast('ok', 'Copied'), () => {});
        });
        return button;
    }

    // ── View: report queue ──────────────────────────────────────────────────

    const filters = { queue: { openOnly: true, priority: '', assignee: '' } };

    views.queue = {
        title: 'Reports',
        tools() {
            const scope = select([
                ['open', 'Open reports'],
                ['mine', 'Assigned to me'],
                ['none', 'Unclaimed'],
                ['all', 'Everything'],
            ], filters.queue.assignee === 'me' ? 'mine'
                : filters.queue.assignee === 'none' ? 'none'
                : filters.queue.openOnly ? 'open' : 'all');

            scope.addEventListener('change', () => {
                const value = scope.value;
                filters.queue.openOnly = value !== 'all';
                filters.queue.assignee = value === 'mine' ? 'me' : value === 'none' ? 'none' : '';
                render();
            });

            const priority = select([
                ['', 'Any priority'],
                ['Critical', 'Critical only'],
                ['High', 'High and above'],
            ], filters.queue.priority);

            priority.addEventListener('change', () => {
                filters.queue.priority = priority.value;
                render();
            });

            return [scope, priority];
        },

        async render() {
            const { total, reports } = await call('GET', `${API}/reports`, {
                query: {
                    openOnly: filters.queue.openOnly,
                    assignee: filters.queue.assignee,
                    priority: filters.queue.priority || undefined,
                    limit: 100,
                },
            });

            if (!reports.length) {
                return empty(filters.queue.openOnly
                    ? 'Nothing open. The queue is clear.'
                    : 'No reports match these filters.');
            }

            const list = el('div', 'rows');

            reports.forEach(report => {
                const title = [el('span', null, REASON_TEXT[report.reason] || report.reason)];

                if (report.priority === 'Critical') title.push(tag('Critical', 'danger'));
                else if (report.priority === 'High') title.push(tag('High', 'warn'));

                if (report.duplicateCount > 0) title.push(tag(`${report.duplicateCount + 1} reports`, 'info'));
                if (report.status !== 'Open' && report.status !== 'Triaged') title.push(tag(report.status));
                else if (report.assignedToUserId) title.push(tag('Claimed'));

                const side = [agoNode(report.createdAt)];

                list.append(row({
                    mark: PRIORITY_CLASS[report.priority],
                    title,
                    sub: report.details || `${report.subjectKind} report against ${report.targetUserId}`,
                    side,
                    selected: report.id === selectedId,
                    onOpen: () => openReport(report.id),
                }));
            });

            const wrap = el('div');
            wrap.append(list, listFoot(total, reports.length));
            return wrap;
        },
    };

    async function openReport(id) {
        selectedId = id;
        openDetail('Report', loading());

        const { report, history, targetReportCount } = await call('GET', `${API}/reports/${id}`);

        const pane = el('div', 'pane');

        pane.append(block(null, kv([
            ['Reason', REASON_TEXT[report.reason] || report.reason],
            ['Priority', humanEnum(report.priority)],
            ['Status', humanEnum(report.status)],
            ['Filed', stamp(report.createdAt)],
            ['Target', copyable(report.targetUserId)],
            ['Reporter', report.reporterUserId ? copyable(report.reporterUserId) : 'Opened by staff'],
            ['Subject', report.subjectId ? copyable(report.subjectId) : report.subjectKind],
            ['Assigned', report.assignedToUserId || 'Nobody'],
        ])));

        if (targetReportCount > 0) {
            const other = banner('warn', `This account has ${targetReportCount} other report${targetReportCount === 1 ? '' : 's'}.`);
            pane.append(other);
        }

        if (report.details) pane.append(block('What was reported', el('div', 'quote', report.details)));

        if (report.evidence) {
            const box = block('Evidence');

            // The label is the point: a moderator acting on this is acting on one person's account
            // of events and must know that.
            //
            // Why it is unverified differs by conversation, and the reporting client says which.
            // Encrypted (opt-in, per conversation): the server holds ciphertext and never could
            // corroborate it. Plain (the default): the server holds the message and simply does not
            // check - so this is a gap that could be closed, not a law of physics, and the wording
            // must not imply otherwise.
            const encrypted = (() => {
                try { return JSON.parse(report.evidence)?.encrypted === true; } catch { return false; }
            })();

            box.append(banner('warn', encrypted
                ? 'Supplied by the reporting client, from an end-to-end encrypted conversation. '
                  + 'The server holds only ciphertext and cannot corroborate any of it.'
                : 'Supplied by the reporting client and not checked against the stored message. '
                  + 'Treat it as the reporter\'s account of what they saw.'));

            let pretty = report.evidence;
            try { pretty = JSON.stringify(JSON.parse(report.evidence), null, 2); } catch { /* leave as-is */ }
            box.append(el('div', 'evidence', pretty));
            pane.append(box);
        }

        if (report.resolution) {
            pane.append(block('Resolution', el('div', 'quote', report.resolution),
                el('p', 'hint', `Closed by ${report.resolvedByUserId} on ${stamp(report.resolvedAt)}`)));
        }

        // Actions
        const actions = el('div', 'btn-row');

        if (report.status === 'Open' || report.status === 'Triaged') {
            if (report.assignedToUserId !== session.userId) {
                actions.append(button('Claim', 'user', async () => {
                    await call('PATCH', `${API}/reports/${id}`, { body: { assignedToUserId: session.userId } });
                    toast('ok', 'Claimed');
                    openReport(id);
                    render();
                }));
            } else {
                actions.append(button('Release', 'times', async () => {
                    await call('PATCH', `${API}/reports/${id}`, { body: { assignedToUserId: '' } });
                    toast('ok', 'Released');
                    openReport(id);
                    render();
                }));
            }

            actions.append(button('Act on the account', 'ban', () => issueAction(report.targetUserId, {
                reason: report.reason,
                reportId: report.id,
            }), 'primary'));

            actions.append(button('Dismiss', 'times-circle', () => resolveReport(id, 'Dismissed')));
            actions.append(button('Duplicate', 'comments', () => resolveReport(id, 'Duplicate')));

            // A report can name a published page in its details or its subject id, and acting on the
            // account does not take the page off the instance's own domain.
            const reported = wikiAddressIn(`${report.subjectId || ''}\n${report.details || ''}`);
            if (reported) {
                actions.append(button('Open the reported page', 'eye', () => openWikiAddress(reported)));
            }
        } else {
            actions.append(button('Reopen', 'refresh', async () => {
                await call('PATCH', `${API}/reports/${id}`, { body: { status: 'Triaged' } });
                toast('ok', 'Reopened');
                openReport(id);
                render();
            }));
        }

        pane.append(block('Actions', actions));

        if (history.length) {
            pane.append(block(`History of ${report.targetUserId}`, timeline(history)));
        }

        openDetail('Report', pane);
    }

    function button(label, iconName, onClick, kind = '') {
        const node = el('button', `btn sm ${kind}`);
        node.append(icon(iconName), document.createTextNode(` ${label}`));
        node.addEventListener('click', async () => {
            node.disabled = true;
            try { await onClick(); } catch (error) { toast('danger', error.message); } finally { node.disabled = false; }
        });
        return node;
    }

    function timeline(actions) {
        const list = el('div', 'timeline');

        actions.forEach(action => {
            const struck = Boolean(action.revokedAt);
            const item = el('div', `event ${struck ? 'struck' : ''}`);

            item.append(icon({
                Ban: 'ban', Suspension: 'clock', Warning: 'exclamation-triangle',
                Unban: 'check-circle', Note: 'pencil',
            }[action.kind] || 'pencil'));

            const body = el('div');
            const line = el('div', 'what');
            line.append(document.createTextNode(`${action.kind} · ${REASON_TEXT[action.reason] || action.reason}`));
            if (action.active) line.append(document.createTextNode(' '), tag('In force', 'danger'));
            body.append(line);

            const meta = [stamp(action.createdAt), action.reference];
            if (action.expiresAt) meta.push(`until ${stamp(action.expiresAt)}`);
            if (action.revokedAt) meta.push(`revoked ${stamp(action.revokedAt)}`);
            body.append(el('span', 'when', meta.join(' · ')));

            if (action.internalNote) body.append(el('div', 'hint', action.internalNote));

            item.append(body);
            list.append(item);
        });

        return list;
    }

    function resolveReport(id, status) {
        modal(close => {
            const form = el('form');
            form.append(el('h2', null, status === 'Dismissed' ? 'Dismiss this report' : 'Mark as duplicate'));
            form.append(el('p', 'lede', status === 'Dismissed'
                ? 'Say why. The note stays on the report, and it is what answers the appeal if one arrives later.'
                : 'Point at the report this duplicates. Its count goes up so the queue shows one loud report rather than several quiet ones.'));

            let original;
            if (status === 'Duplicate') {
                original = el('input');
                original.placeholder = 'rprt_...';
                original.required = true;
                form.append(field('Original report id', original, null, { required: true }));
            }

            const note = el('textarea');
            note.required = true;
            note.rows = 4;
            note.placeholder = status === 'Dismissed'
                ? 'e.g. Reviewed the channel; the message is heated but within the rules.'
                : 'e.g. Same incident as the earlier report from another member.';
            form.append(field('Resolution', note, null, { required: true }));

            const actions = el('div', 'actions');
            const cancel = el('button', 'btn', 'Cancel');
            cancel.type = 'button';
            cancel.addEventListener('click', close);

            const confirm = el('button', 'btn primary', status === 'Dismissed' ? 'Dismiss' : 'Mark duplicate');
            confirm.type = 'submit';
            actions.append(cancel, confirm);
            form.append(actions);

            form.addEventListener('submit', async event => {
                event.preventDefault();
                confirm.disabled = true;

                try {
                    await call('PATCH', `${API}/reports/${id}`, {
                        body: {
                            status,
                            resolution: note.value,
                            duplicateOfId: original?.value.trim(),
                        },
                    });

                    close();
                    toast('ok', 'Report closed');
                    closeDetail();
                    render();
                    refreshBadges();
                } catch (error) {
                    toast('danger', error.message);
                    confirm.disabled = false;
                }
            });

            return form;
        });
    }

    // ── Issuing an action ───────────────────────────────────────────────────

    function issueAction(userId, preset = {}) {
        modal(close => {
            const form = el('form');
            form.append(el('h2', null, 'Act on this account'));

            // Whose account, said in the dialog rather than left to the row somebody clicked several
            // minutes ago. Nothing else on this form names the person it restricts, and the id is
            // beside the name because two accounts can read alike and only one of them did it.
            const target = el('p', 'lede');
            target.append(el('strong', null, preset.userName || 'This account'));
            target.append(document.createTextNode(` · ${userId}`));
            form.append(target);

            form.append(el('p', 'lede', 'The account is restricted first, then the record is written. If the account service refuses, nothing is changed.'));

            const kind = select([
                ['Warning', 'Warning - no restriction'],
                ['Suspension', 'Suspension - time limited'],
                ['Ban', 'Ban - indefinite'],
                ['Note', 'Note - internal only, invisible to them'],
                ['Unban', 'Unban - restore the account'],
            ], preset.kind || 'Warning');
            form.append(field('Action', kind));

            const hours = el('input');
            hours.type = 'number';
            hours.min = '1';
            hours.value = '72';
            const hoursField = field('Duration in hours', hours, 'How long the suspension lasts.');
            hoursField.classList.add('hidden');
            form.append(hoursField);

            const reason = select(REASONS, preset.reason || 'Other');
            form.append(field('Reason', reason, 'Shown to the user, in these words.'));

            const publicNote = el('textarea');
            publicNote.rows = 3;
            publicNote.placeholder = 'What they did, in a sentence. This is the part they read.';
            const publicField = field('Message to the user', publicNote);
            form.append(publicField);

            const internal = el('textarea');
            internal.rows = 2;
            internal.placeholder = 'Context for the next moderator. Never leaves this console.';
            form.append(field('Internal note', internal));

            const notify = el('input');
            notify.type = 'checkbox';
            notify.checked = true;
            const notifyWrap = el('label', 'hstack');
            notifyWrap.style.cssText = 'font-size:13.5px;color:var(--muted);cursor:pointer;';
            notifyWrap.append(notify, document.createTextNode(' Email them about this'));
            const notifyField = el('div', 'field');
            notifyField.append(notifyWrap);
            form.append(notifyField);

            function sync() {
                hoursField.classList.toggle('hidden', kind.value !== 'Suspension');
                // A Note is invisible to the account by design, so there is nothing to send and
                // nothing to write to them.
                const silent = kind.value === 'Note';
                notifyField.classList.toggle('hidden', silent);
                publicField.classList.toggle('hidden', silent);
            }

            kind.addEventListener('change', sync);
            sync();

            const actions = el('div', 'actions');
            const cancel = el('button', 'btn', 'Cancel');
            cancel.type = 'button';
            cancel.addEventListener('click', close);

            const confirm = el('button', 'btn primary danger', 'Apply');
            confirm.type = 'submit';
            actions.append(cancel, confirm);
            form.append(actions);

            form.addEventListener('submit', async event => {
                event.preventDefault();
                confirm.disabled = true;

                try {
                    await call('POST', `${API}/users/${encodeURIComponent(userId)}/actions`, {
                        body: {
                            kind: kind.value,
                            reason: reason.value,
                            publicNote: publicNote.value,
                            internalNote: internal.value,
                            durationHours: kind.value === 'Suspension' ? Number(hours.value) : null,
                            reportId: preset.reportId,
                            notify: notify.checked,
                        },
                    });

                    close();
                    toast('ok', `${kind.value} recorded`);

                    // Close the report that prompted it, so the queue does not keep an actioned
                    // report open waiting for a second click nobody remembers to make.
                    if (preset.reportId) {
                        await call('PATCH', `${API}/reports/${preset.reportId}`, {
                            body: {
                                status: 'ActionTaken',
                                resolution: `${kind.value}: ${publicNote.value || REASON_TEXT[reason.value]}`,
                            },
                        });
                        closeDetail();
                    }

                    render();
                    refreshBadges();
                } catch (error) {
                    toast('danger', error.message);
                    confirm.disabled = false;
                }
            });

            return form;
        });
    }

    // ── View: appeals ───────────────────────────────────────────────────────

    /**
     * What an appeal's state is called, in one place.
     *
     * The list and the detail pane used to word these separately, so the same appeal was "Waiting" in
     * the queue and "Pending" once opened - which reads as two different states rather than as one
     * state described twice. Only the ones this view renames: everything else, "Being reviewed"
     * included, falls through to `humanEnum` so that there is one place to change the wording.
     */
    const APPEAL_STATUS_TEXT = {
        Pending: 'Waiting', Granted: 'Accepted', Denied: 'Declined',
    };

    const appealStatusText = status => APPEAL_STATUS_TEXT[status] || humanEnum(status);

    views.appeals = {
        title: 'Appeals',
        tools() {
            const scope = select([['open', 'Undecided'], ['all', 'Everything']], 'open');
            scope.addEventListener('change', () => { filters.appealsOpen = scope.value === 'open'; render(); });
            return [scope];
        },

        async render() {
            const openOnly = filters.appealsOpen !== false;
            const { total, appeals } = await call('GET', `${API}/appeals`, { query: { openOnly, limit: 100 } });

            if (!appeals.length) return empty('No appeals waiting.');

            const list = el('div', 'rows');

            appeals.forEach(appeal => {
                const title = [el('span', null, appeal.action?.kind || 'Appeal')];

                title.push(tag(appealStatusText(appeal.status),
                    appeal.status === 'Granted' ? 'ok' : appeal.status === 'Denied' ? 'danger' : 'info'));

                list.append(row({
                    mark: appeal.status === 'Pending' ? 'high' : 'normal',
                    title,
                    sub: appeal.body,
                    side: [agoNode(appeal.createdAt)],
                    selected: appeal.id === selectedId,
                    onOpen: () => openAppeal(appeal.id),
                }));
            });

            const wrap = el('div');
            wrap.append(list, listFoot(total, appeals.length));
            return wrap;
        },
    };

    async function openAppeal(id) {
        selectedId = id;
        openDetail('Appeal', loading());

        const { appeal, report, history } = await call('GET', `${API}/appeals/${id}`);
        const pane = el('div', 'pane');

        pane.append(block(null, kv([
            ['Appeal', copyable(appeal.reference)],
            ['Status', appealStatusText(appeal.status)],
            ['Filed', stamp(appeal.createdAt)],
            ['From', appeal.contactEmail],
            ['Account', appeal.action ? copyable(appeal.action.targetUserId) : null],
        ])));

        pane.append(block('What they say', el('div', 'quote', appeal.body)));

        if (appeal.action) {
            const box = block('The decision they are appealing', kv([
                ['Action', `${appeal.action.kind} · ${REASON_TEXT[appeal.action.reason] || appeal.action.reason}`],
                ['Reference', copyable(appeal.action.reference)],
                ['Issued', stamp(appeal.action.createdAt)],
                ['By', appeal.action.actorUserId],
                ['Expires', appeal.action.expiresAt ? stamp(appeal.action.expiresAt) : 'Never'],
            ]));

            if (appeal.action.publicNote) box.append(el('div', 'quote', appeal.action.publicNote));
            if (appeal.action.internalNote) {
                box.append(el('p', 'hint', `Internal: ${appeal.action.internalNote}`));
            }
            pane.append(box);
        }

        // The original complaint, so the appeal is not decided on one side's account of events.
        if (report) {
            pane.append(block('The report behind it',
                el('div', 'quote', report.details || '(no detail given)')));
        }

        if (appeal.status === 'Granted' || appeal.status === 'Denied') {
            pane.append(block('Decision',
                el('div', 'quote', appeal.decisionNote || ''),
                el('p', 'hint', `${appealStatusText(appeal.status)} by ${appeal.decidedByUserId} on ${stamp(appeal.decidedAt)}`)));

            if (appeal.status === 'Granted' && appeal.action?.active) {
                const warn = banner('warn', 'This appeal was accepted but the account is still restricted. Issue an unban to restore it.');
                pane.append(warn);

                pane.append(block('Follow up', button('Unban this account', 'unlock',
                    () => issueAction(appeal.action.targetUserId, { kind: 'Unban', reason: appeal.action.reason }),
                    'primary')));
            }
        } else {
            const actions = el('div', 'btn-row');

            if (appeal.status === 'Pending') {
                actions.append(button('Take it', 'user', async () => {
                    await call('POST', `${API}/appeals/${id}/claim`);
                    openAppeal(id);
                    render();
                }));
            }

            actions.append(button('Accept', 'check-circle', () => decideAppeal(id, true), 'primary'));
            actions.append(button('Decline', 'times-circle', () => decideAppeal(id, false), 'danger'));

            pane.append(block('Decide', actions));
        }

        if (history?.length) pane.append(block('Account history', timeline(history)));

        openDetail('Appeal', pane);
    }

    function decideAppeal(id, granted) {
        modal(close => {
            const form = el('form');
            form.append(el('h2', null, granted ? 'Accept this appeal' : 'Decline this appeal'));
            form.append(el('p', 'lede', granted
                ? 'This records the decision and emails them. The account stays restricted until you issue an unban - that is a second, deliberate step.'
                : 'This is final: there is one appeal per decision. Your note is what they read, so write it for them.'));

            const note = el('textarea');
            note.required = true;
            note.rows = 5;
            note.placeholder = granted
                ? 'e.g. You are right that the message was quoting someone else. The ban is being lifted.'
                : 'e.g. We looked again at the full thread. The messages were directed at one member over several days, which is what the rule covers.';

            form.append(field('What they will be told', note, null, { required: true }));

            const actions = el('div', 'actions');
            const cancel = el('button', 'btn', 'Cancel');
            cancel.type = 'button';
            cancel.addEventListener('click', close);

            const confirm = el('button', `btn primary ${granted ? '' : 'danger'}`, granted ? 'Accept' : 'Decline');
            confirm.type = 'submit';
            actions.append(cancel, confirm);
            form.append(actions);

            form.addEventListener('submit', async event => {
                event.preventDefault();
                confirm.disabled = true;

                try {
                    const result = await call('POST', `${API}/appeals/${id}/decide`, {
                        body: { granted, note: note.value, notify: true },
                    });

                    close();
                    toast('ok', granted ? 'Appeal accepted' : 'Appeal declined');
                    if (result.followUpMessage) toast('info', result.followUpMessage);

                    openAppeal(id);
                    render();
                    refreshBadges();
                } catch (error) {
                    toast('danger', error.message);
                    confirm.disabled = false;
                }
            });

            return form;
        });
    }

    // ── View: tickets ───────────────────────────────────────────────────────

    /** The one ticket state this view renames - the queue calls it "Needs a reply" and the pane used
     *  to call it AwaitingStaff. The rest, "With them" included, are worded by `humanEnum`. */
    const TICKET_STATUS_TEXT = { AwaitingStaff: 'Needs a reply' };

    const ticketStatusText = status => TICKET_STATUS_TEXT[status] || humanEnum(status);

    views.tickets = {
        title: 'Support',
        tools() {
            const scope = select([['open', 'Open'], ['mine', 'Assigned to me'], ['all', 'Everything']], 'open');
            scope.addEventListener('change', () => { filters.ticketScope = scope.value; render(); });
            return [scope];
        },

        async render() {
            const scope = filters.ticketScope || 'open';
            const { total, tickets } = await call('GET', `${API}/tickets`, {
                query: {
                    openOnly: scope !== 'all',
                    assignee: scope === 'mine' ? 'me' : undefined,
                    limit: 100,
                },
            });

            if (!tickets.length) return empty('No tickets waiting.', 'inbox');

            const list = el('div', 'rows');

            tickets.forEach(ticket => {
                const waiting = ticket.status === 'Open' || ticket.status === 'AwaitingStaff';

                const title = [el('span', null, ticket.subject)];
                title.push(tag(humanEnum(ticket.category)));
                if (waiting) title.push(tag('Needs a reply', 'warn'));
                else title.push(tag(ticketStatusText(ticket.status),
                    ticket.status === 'AwaitingRequester' ? '' : 'ok'));

                list.append(row({
                    mark: waiting ? 'high' : 'normal',
                    title,
                    sub: ticket.contactEmail,
                    side: [agoNode(ticket.lastActivityAt)],
                    selected: ticket.id === selectedId,
                    onOpen: () => openTicket(ticket.id),
                }));
            });

            const wrap = el('div');
            wrap.append(list, listFoot(total, tickets.length));
            return wrap;
        },
    };

    async function openTicket(id) {
        selectedId = id;
        openDetail('Ticket', loading());

        const ticket = await call('GET', `${API}/tickets/${id}`);
        const pane = el('div', 'pane');

        pane.append(block(null, kv([
            ['Reference', copyable(ticket.reference)],
            ['From', ticket.contactEmail],
            ['Account', ticket.requesterUserId ? copyable(ticket.requesterUserId) : 'Not signed in'],
            ['Category', humanEnum(ticket.category)],
            ['Status', ticketStatusText(ticket.status)],
            ['Opened', stamp(ticket.createdAt)],
            ['Assigned', ticket.assignedToUserId || 'Nobody'],
        ])));

        const thread = el('div', 'timeline');
        ticket.messages.forEach(message => {
            const box = el('div', 'block');
            box.style.marginBottom = '10px';

            const head = el('div', 'hstack');
            head.append(el('span', 'msg-from', message.internal ? 'Internal note'
                : message.authorKind === 'Requester' ? 'Them' : 'Support'));
            head.append(el('span', 'when', stamp(message.createdAt)));
            if (message.internal) head.append(tag('Not sent', 'warn'));
            box.append(head);
            box.append(el('div', 'quote', message.body));
            thread.append(box);
        });
        pane.append(block('Conversation', thread));

        if (ticket.status !== 'Closed') pane.append(replyBlock(ticket));

        const controls = el('div', 'btn-row');

        if (ticket.assignedToUserId !== session.userId) {
            controls.append(button('Claim', 'user', async () => {
                await call('PATCH', `${API}/tickets/${id}`, { body: { assignedToUserId: session.userId } });
                openTicket(id);
                render();
            }));
        }

        if (ticket.status !== 'Resolved') {
            controls.append(button('Mark resolved', 'check-circle', async () => {
                await call('PATCH', `${API}/tickets/${id}`, { body: { status: 'Resolved' } });
                toast('ok', 'Resolved');
                openTicket(id);
                render();
                refreshBadges();
            }));
        }

        if (ticket.status !== 'Closed') {
            controls.append(button('Close', 'times-circle', async () => {
                await call('PATCH', `${API}/tickets/${id}`, { body: { status: 'Closed' } });
                toast('ok', 'Closed');
                openTicket(id);
                render();
                refreshBadges();
            }));
        }

        if (ticket.requesterUserId) {
            controls.append(button('Open the account', 'user', () => openUser(ticket.requesterUserId)));
        }

        // A report about a published wiki page arrives here rather than through the report queue:
        // the wiki host has no session, so its readers have the support form and nothing else. The
        // address is in the text they wrote, and this is what makes it lead somewhere.
        const reported = wikiAddressIn(
            [ticket.subject, ...ticket.messages.map(message => message.body)].join('\n'));

        if (reported) {
            controls.append(button('Open the reported page', 'eye', () => openWikiAddress(reported)));
        }

        pane.append(block('Ticket', controls));

        openDetail(ticket.reference, pane);
    }

    function replyBlock(ticket) {
        const box = block('Reply');
        const form = el('form');

        const body = el('textarea');
        body.rows = 5;
        body.required = true;
        body.placeholder = 'Written to them, in the interface\'s voice.';
        form.append(field(null, body));

        const internal = el('input');
        internal.type = 'checkbox';
        const label = el('label', 'hstack');
        label.style.cssText = 'font-size:13px;color:var(--muted);cursor:pointer;margin-bottom:12px;';
        label.append(internal, document.createTextNode(' Internal note - not sent to them'));
        form.append(label);

        const send = el('button', 'btn primary sm');
        send.type = 'submit';
        send.append(icon('send'), document.createTextNode(' Send'));
        form.append(send);

        form.addEventListener('submit', async event => {
            event.preventDefault();
            send.disabled = true;

            try {
                await call('POST', `${API}/tickets/${ticket.id}/messages`, {
                    body: { body: body.value, internal: internal.checked },
                });

                toast('ok', internal.checked ? 'Note saved' : 'Reply sent');
                openTicket(ticket.id);
                render();
                refreshBadges();
            } catch (error) {
                toast('danger', error.message);
                send.disabled = false;
            }
        });

        box.append(form);
        return box;
    }

    // ── View: accounts ──────────────────────────────────────────────────────

    views.users = {
        title: 'Accounts',
        tools() {
            const search = el('input');
            search.type = 'search';
            search.placeholder = 'Username, email, or user id';
            search.value = filters.userQuery || '';

            let timer;
            search.addEventListener('input', () => {
                clearTimeout(timer);
                // Debounced: this search hits Identity's database over the bus, and a request per
                // keystroke would be a self-inflicted load test on the service that also answers
                // every sign-in.
                timer = setTimeout(() => { filters.userQuery = search.value; render(); }, 300);
            });

            const status = select([
                ['', 'Any status'],
                ['Active', 'Active'],
                ['Banned', 'Banned'],
                ['PendingDeletion', 'Pending deletion'],
                ['Deleted', 'Deleted'],
            ], filters.userStatus || '');

            status.addEventListener('change', () => { filters.userStatus = status.value; render(); });

            return [search, status];
        },

        async render() {
            const { total, users } = await call('GET', `${API}/users`, {
                query: { q: filters.userQuery, status: filters.userStatus, limit: 50 },
            });

            if (!users.length) return empty('No accounts match.', 'search');

            const list = el('div', 'rows');

            users.forEach(user => {
                const title = [el('span', null, user.userName || '(no name)')];

                if (user.status === 'Banned') title.push(tag('Banned', 'danger'));
                else if (user.status !== 'Active') title.push(tag(humanEnum(user.status)));
                if (user.hasActiveSanction && user.status !== 'Banned') title.push(tag('Sanctioned', 'warn'));
                if (user.userType !== 'Default') title.push(tag(humanEnum(user.userType), 'info'));
                if (!user.emailVerified && user.userType !== 'Bot') title.push(tag('Unverified'));

                list.append(row({
                    mark: user.status === 'Banned' ? 'critical' : user.hasActiveSanction ? 'high' : '',
                    title,
                    // A bot has no address and never will, so the id it used to fall back to read as
                    // a row whose email had failed to load. Say what the row actually is instead.
                    sub: user.email
                        || (user.userType === 'Bot' ? 'Bot account, no email' : user.id),
                    side: [agoNode(user.createdAt)],
                    selected: user.id === selectedId,
                    onOpen: () => openUser(user.id),
                }));
            });

            const wrap = el('div');
            wrap.append(list, listFoot(total, users.length));
            return wrap;
        },
    };

    async function openUser(id) {
        selectedId = id;
        go2Users();
        openDetail('Account', loading());

        const { user, actions, reportsAgainst, reportsFiledCount, activeSanction } =
            await call('GET', `${API}/users/${encodeURIComponent(id)}`);

        const pane = el('div', 'pane');

        pane.append(block(null, kv([
            ['Name', user.user.userName],
            ['Id', copyable(user.user.id)],
            ['Email', user.user.email || '-'],
            ['Verified', user.user.emailVerified ? `Yes, ${stamp(user.emailVerifiedAt)}` : 'No'],
            ['Status', humanEnum(user.user.status)],
            ['Type', humanEnum(user.user.userType)],
            ['Joined', stamp(user.user.createdAt)],
            ['Last seen', user.lastSignInAt ? stamp(user.lastSignInAt) : 'Never signed in'],
            ['Devices', user.deviceCount],
            ['Two-factor', user.twoFactorEnabled ? 'On' : 'Off'],
            ['Locked out', user.lockedOut ? `Until ${stamp(user.lockoutEnd)}` : 'No'],
        ])));

        if (user.deletionRequestedAt) {
            pane.append(banner('warn',
                `This account asked to be deleted on ${stamp(user.deletionRequestedAt)}. Moderation actions on it will be refused.`));
        }

        if (activeSanction) {
            const box = block('In force now', kv([
                ['Action', activeSanction.kind],
                ['Reason', REASON_TEXT[activeSanction.reason] || activeSanction.reason],
                ['Reference', copyable(activeSanction.reference)],
                ['Since', stamp(activeSanction.createdAt)],
                ['Until', activeSanction.expiresAt ? stamp(activeSanction.expiresAt) : 'No end date'],
            ]));

            if (activeSanction.publicNote) box.append(el('div', 'quote', activeSanction.publicNote));

            box.append(el('div', 'btn-row', ''), document.createTextNode(''));
            const row = box.querySelector('.btn-row');
            row.style.marginTop = '12px';
            row.append(button('Lift this', 'unlock', () => revoke(activeSanction.id, id), 'primary'));

            pane.append(box);
        }

        const controls = el('div', 'btn-row');
        controls.append(button('Act on this account', 'ban',
            () => issueAction(id, { userName: user.user.userName }), 'danger'));
        // "What is this account entitled to, and why" is a support question somebody already has the
        // id in hand for, so it is one click rather than a copy-paste into another screen.
        controls.append(button('Entitlements', 'key', () => openBillingFor('User', id)));
        pane.append(block('Actions', controls));

        // Administrators only, and hidden rather than disabled for everyone else: a moderator who
        // could promote themselves would be an administrator with extra steps, so this control does
        // not exist for them at all.
        if (session.canViewAudit && user.user.userType !== 'Bot' && id !== session.userId) {
            pane.append(roleBlock(id, user.user));
        } else if (session.canViewAudit && id === session.userId) {
            pane.append(block('Staff role',
                el('p', 'hint',
                    'This is your own account. Another administrator has to change your role - the '
                    + 'alternative is an instance whose last administrator demoted themselves.')));
        }

        pane.append(block('History', actions.length ? timeline(actions) : el('p', 'hint', 'Nothing recorded.')));

        const filed = el('p', 'hint', `This account has filed ${reportsFiledCount} report${reportsFiledCount === 1 ? '' : 's'}.`);

        if (reportsAgainst.length) {
            const list = el('div', 'timeline');
            reportsAgainst.forEach(report => {
                const item = el('div', 'event');
                item.append(icon('flag'));
                const body = el('div');
                body.append(el('div', 'what', REASON_TEXT[report.reason] || report.reason));
                body.append(el('span', 'when', `${stamp(report.createdAt)} · ${humanEnum(report.status)}`));
                item.append(body);
                list.append(item);
            });
            pane.append(block(`Reports against (${reportsAgainst.length})`, list, filed));
        } else {
            pane.append(block('Reports', el('p', 'hint', 'Never reported.'), filed));
        }

        openDetail(user.user.userName || 'Account', pane);
    }

    /**
     * The staff-tier control.
     *
     * A select plus an explicit Apply, not a select that saves on change: this is the write that
     * decides who can act on everyone else, and a stray scroll wheel over a focused dropdown should
     * not be able to make somebody an administrator.
     */
    function roleBlock(userId, user) {
        const box = block('Staff role');

        box.append(el('p', 'hint',
            'Moderators work the report, appeal and support queues. Administrators can also see the '
            + 'audit log, act on other staff, and change roles. Checked against the account on every '
            + 'request, so a demotion takes effect immediately - it does not wait for a token to expire.'));

        const form = el('form');
        form.style.cssText = 'display:flex;gap:8px;align-items:flex-end;margin-top:12px;';

        const picker = select([
            ['Default', 'Not staff'],
            ['Moderator', 'Moderator'],
            ['Admin', 'Administrator'],
        ], user.userType === 'Bot' ? 'Default' : user.userType);

        const wrap = el('div', 'grow');
        wrap.append(picker);
        form.append(wrap);

        const apply = el('button', 'btn sm primary');
        apply.type = 'submit';
        apply.disabled = true;
        apply.append(icon('check'), document.createTextNode(' Apply'));
        form.append(apply);

        picker.addEventListener('change', () => { apply.disabled = picker.value === user.userType; });

        form.addEventListener('submit', async event => {
            event.preventDefault();

            // Promotion to Admin gets a confirmation step of its own. Everything else in this
            // console is reversible by the person who did it; handing someone the ability to demote
            // you is not.
            const promotingToAdmin = picker.value === 'Admin';

            /*
             * The last administrator, refused before the modal opens.
             *
             * The guard further up covers demoting yourself. Demoting the one other administrator
             * leaves the same instance with the same nobody left to undo it: changing a role is
             * administrator-only, so there would be no account able to hand the tier back, and no
             * amount of moderators between them can do it.
             *
             * Advisory, not authoritative. The count comes from the accounts endpoint's own `type`
             * filter - the overview's staff figure counts moderators too and so cannot answer this -
             * and a deleted or banned account still carries its tier, so the number reads high if
             * anything. That errs towards letting a demotion through rather than towards blocking a
             * legitimate one, and the server stays the one that decides.
             */
            const demotingAnAdmin = user.userType === 'Admin' && picker.value !== 'Admin';
            let administrators = null;

            if (demotingAnAdmin) {
                apply.disabled = true;
                administrators = await adminCount();
                apply.disabled = false;

                if (administrators !== null && administrators <= 1) {
                    fieldError(picker,
                        'This is the only administrator left on the instance. Promote somebody else '
                        + 'first - changing roles is administrator-only, so nobody would be able to '
                        + 'hand the tier back.');
                    return;
                }
            }

            modal(close => {
                const confirmForm = el('form');
                confirmForm.append(el('h2', null,
                    promotingToAdmin ? 'Make this account an administrator' : 'Change this account\'s role'));

                confirmForm.append(el('p', 'lede', promotingToAdmin
                    ? `${user.userName || userId} will be able to ban any account, read the audit log, `
                      + 'change other people\'s roles - including yours - and manage federation. There is '
                      + 'no way to undo this except by them agreeing, or by another administrator.'
                    : `${user.userName || userId} will be set to ${picker.value === 'Default' ? 'not staff' : 'moderator'}. `
                      + 'It takes effect on their next request.'));

                // The count could not be read, so the guard above could not run. Said here rather
                // than swallowed: a demotion that turns out to have been the last administrator is
                // not something the operator can put back afterwards.
                if (demotingAnAdmin && administrators === null) {
                    confirmForm.append(banner('warn',
                        'We could not check how many administrators are left. If this is the last one, '
                        + 'nobody will be able to hand the tier back - changing roles is '
                        + 'administrator-only.'));
                }

                const actions = el('div', 'actions');
                const cancel = el('button', 'btn', 'Cancel');
                cancel.type = 'button';
                cancel.addEventListener('click', close);

                const confirm = el('button', `btn primary ${promotingToAdmin ? 'danger' : ''}`,
                    promotingToAdmin ? 'Make administrator' : 'Change role');
                confirm.type = 'submit';
                actions.append(cancel, confirm);
                confirmForm.append(actions);

                confirmForm.addEventListener('submit', async confirmEvent => {
                    confirmEvent.preventDefault();
                    confirm.disabled = true;

                    try {
                        const result = await call('POST', `${API}/users/${encodeURIComponent(userId)}/role`, {
                            body: { role: picker.value },
                        });

                        close();
                        toast('ok', `${result.userName || userId} is now ${
                            result.role === 'Default' ? 'not staff' : result.role.toLowerCase()}`);

                        openUser(userId);
                        render();
                    } catch (error) {
                        toast('danger', error.message);
                        confirm.disabled = false;
                    }
                });

                return confirmForm;
            });
        });

        box.append(form);
        return box;
    }

    /**
     * How many administrators the instance has, or null if the question could not be asked.
     *
     * One row is enough: the accounts endpoint counts the whole match before paging, so the total is
     * the answer and the page is not. Null rather than a guess on failure - a demotion guard that
     * reads a failed request as "zero administrators" would block every demotion the moment the
     * account service hiccups.
     */
    async function adminCount() {
        try {
            const { total } = await call('GET', `${API}/users`, { query: { type: 'Admin', limit: 1 } });
            return typeof total === 'number' ? total : null;
        } catch {
            return null;
        }
    }

    /** Jump to the accounts view without clearing the selection openUser just made. */
    function go2Users() {
        if (view === 'users') return;

        view = 'users';
        $$('.rail-item').forEach(item => item.setAttribute('aria-current', item.dataset.view === 'users' ? 'page' : 'false'));
        $('#view-title').textContent = views.users.title;
        $('#view-tools').replaceChildren(...views.users.tools());
        history.replaceState(null, '', '#users');
        render();
    }

    function revoke(actionId, userId) {
        modal(close => {
            const form = el('form');
            form.append(el('h2', null, 'Lift this restriction'));
            form.append(el('p', 'lede', 'The account can sign in again as soon as this goes through - unless another restriction is still running, which is left alone.'));

            const reason = el('textarea');
            reason.rows = 3;
            reason.placeholder = 'Why it is being lifted. They see this.';
            form.append(field('Reason', reason));

            const actions = el('div', 'actions');
            const cancel = el('button', 'btn', 'Cancel');
            cancel.type = 'button';
            cancel.addEventListener('click', close);

            const confirm = el('button', 'btn primary', 'Lift it');
            confirm.type = 'submit';
            actions.append(cancel, confirm);
            form.append(actions);

            form.addEventListener('submit', async event => {
                event.preventDefault();
                confirm.disabled = true;

                try {
                    await call('POST', `${API}/actions/${actionId}/revoke`, {
                        body: { reason: reason.value, notify: true },
                    });

                    close();
                    toast('ok', 'Restriction lifted');
                    openUser(userId);
                    render();
                } catch (error) {
                    toast('danger', error.message);
                    confirm.disabled = false;
                }
            });

            return form;
        });
    }

    // ── View: overview ──────────────────────────────────────────────────────

    views.stats = {
        title: 'Overview',
        async render() {
            const { platform, moderation, platformAvailable } = await call('GET', `${API}/stats`);
            const wrap = el('div');

            wrap.append(el('div', 'section-head', 'Queues'));
            wrap.append(tiles([
                ['Open reports', moderation.openReports, moderation.openReports > 0 ? 'warn' : '',
                    () => toQueue({ openOnly: true, priority: '', assignee: '' })],
                ['Critical', moderation.criticalReports, moderation.criticalReports > 0 ? 'alert' : '',
                    () => toQueue({ openOnly: true, priority: 'Critical', assignee: '' })],
                ['Unclaimed', moderation.unassignedReports,
                    '', () => toQueue({ openOnly: true, priority: '', assignee: 'none' })],
                // Not a link: "older than 48h" is a figure the server computes and there is no
                // staleness filter behind the queue to send anybody to. A tile that navigated to
                // something close but different would be worse than one that stays a number.
                ['Older than 48h', moderation.staleReports, moderation.staleReports > 0 ? 'alert' : ''],
                ['Open appeals', moderation.openAppeals, '', () => {
                    filters.appealsOpen = true;
                    go('appeals');
                }],
                // Same reason as the stale tile: the queue filters on open, not on which side the
                // ticket is waiting for.
                ['Tickets needing a reply', moderation.ticketsAwaitingStaff],
                ['Open tickets', moderation.openTickets, '', () => {
                    filters.ticketScope = 'open';
                    go('tickets');
                }],
            ]));

            wrap.append(el('div', 'section-head', 'Enforcement'));
            wrap.append(tiles([
                ['Bans in force', moderation.activeBans],
                ['Suspensions running', moderation.activeSuspensions],
            ]));

            if (platformAvailable) {
                wrap.append(el('div', 'section-head', 'Accounts'));
                wrap.append(tiles([
                    // Registered, bots, staff and unverified stay flat: the accounts view filters on
                    // status, and none of those four is a status.
                    ['Registered', platform.totalUsers],
                    ['Active', platform.activeUsers, '', () => toAccounts('Active')],
                    ['Banned', platform.bannedUsers, '', () => toAccounts('Banned')],
                    ['Pending deletion', platform.pendingDeletion, '', () => toAccounts('PendingDeletion')],
                    ['Deleted', platform.deletedUsers, '', () => toAccounts('Deleted')],
                    ['Bots', platform.botAccounts],
                    ['Staff', platform.staffAccounts],
                    ['Email unverified', platform.unverifiedEmail],
                ]));

                wrap.append(el('div', 'section-head', 'New sign-ups'));
                wrap.append(tiles([
                    ['Last 24 hours', platform.newUsers24h],
                    ['Last 7 days', platform.newUsers7d],
                    ['Last 30 days', platform.newUsers30d],
                ]));
            } else {
                // Degraded, and said so. The queue numbers are local and still true; blanking the
                // whole page because Identity is down would be the wrong failure.
                const warn = banner('warn', 'Account numbers are unavailable - the account service did not answer. The queue numbers above are current.');
                warn.style.margin = '18px';
                wrap.append(warn);
            }

            return wrap;
        },
    };

    /** The queue, filtered the way a tile promised. The tools row reads these back, so the dropdowns
     *  arrive showing the same scope the number came from. */
    function toQueue(scope) {
        Object.assign(filters.queue, scope);
        go('queue');
    }

    function toAccounts(status) {
        filters.userStatus = status;
        filters.userQuery = '';
        go('users');
    }

    /**
     * The overview's figures.
     *
     * A number somebody can act on is a control, not a caption: "Unclaimed: 14" is the queue a
     * moderator came here to work, and it used to be text they then had to go and reproduce by hand
     * in a dropdown. Tiles that map onto a filter the list views already have are buttons that go
     * there; the rest stay flat, because a tile that looks clickable and is not is worse than one
     * that never offered.
     */
    function tiles(entries) {
        const grid = el('div', 'tiles');

        entries.forEach(([label, value, kind, onOpen]) => {
            const tile = el(onOpen ? 'button' : 'div', `tile ${kind || ''}`);

            if (onOpen) {
                tile.type = 'button';
                tile.title = `${label} - open the list`;
                // A button carries the browser's own font and alignment, which is not the tile's.
                tile.style.cssText = 'font:inherit;color:inherit;text-align:left;width:100%;cursor:pointer;';
                tile.addEventListener('click', onOpen);

                // The ring the rail and the rows use, applied here rather than left to the browser's
                // default so that a keyboard operator sees the same marker everywhere in the console.
                // Keyboard focus only: a ring painted on every mouse click is noise, and an engine
                // that cannot answer the question gets the ring rather than nothing.
                tile.addEventListener('focus', () => {
                    let keyboard = true;
                    try { keyboard = tile.matches(':focus-visible'); } catch { /* older engine */ }
                    if (!keyboard) return;

                    tile.style.outline = '2px solid var(--brand)';
                    tile.style.outlineOffset = '2px';
                });

                tile.addEventListener('blur', () => { tile.style.outline = ''; });
            }

            tile.append(el('div', 'tile-label', label));
            tile.append(el('div', 'tile-value', Number(value ?? 0).toLocaleString()));
            grid.append(tile);
        });

        return grid;
    }

    // ── View: federation ────────────────────────────────────────────────────
    //
    // These are the Federation service's own endpoints, proxied by the gateway untouched (see
    // ProxyConfig's federation-admin-route). They are gated on UserType.Admin specifically, not on
    // staff generally: approving a peer marks it Active, and Active is the only thing standing
    // between a remote instance and injecting events here. A Moderator is not enough, which is why
    // this view is admin-only in the rail as well.

    const FED = '/api/v1/admin/federation';

    views.federation = {
        title: 'Federation',
        tools() {
            const add = el('button', 'btn sm primary');
            add.append(icon('plus'), document.createTextNode(' Federate with an instance'));
            add.addEventListener('click', initiateHandshake);
            return [add];
        },

        async render() {
            let instances;
            let settings = null;

            try {
                [instances, settings] = await Promise.all([
                    call('GET', `${FED}/instances`),
                    call('GET', `${FED}/settings`).catch(() => null),
                ]);
            } catch (error) {
                // The federation surface has its own authorization, resolved by the Federation
                // service rather than by this gateway - so it can refuse a caller the console has
                // already admitted. Saying which of the two refused, and why, is the difference
                // between a fixable message and the bare 403 this surface was reported to give.
                const box = el('div', 'pane');
                box.append(banner('danger',
                    `The federation service refused this request: ${error.message}`));
                box.append(block('Why this happens',
                    el('p', 'hint',
                        'Federation admin requires UserType.Admin on your account specifically - a '
                        + 'Moderator is refused here even though the rest of this console admits them. '
                        + 'If you are an administrator, the other cause is that the federation service '
                        + 'could not reach the account service over the bus; its log says which, on the '
                        + 'InstanceAdminHandler category.')));
                return box;
            }

            const wrap = el('div');

            if (settings) {
                const box = el('div', 'pane');
                const card = block('Who may federate with us');

                const policy = select([
                    ['AutoAccept', 'Accept any instance that asks'],
                    ['RequireApproval', 'Hold new instances for approval'],
                ], settings.acceptancePolicy);

                policy.addEventListener('change', async () => {
                    try {
                        await call('PATCH', `${FED}/settings`, { body: { acceptancePolicy: policy.value } });
                        toast('ok', 'Policy saved');
                    } catch (error) {
                        toast('danger', error.message);
                    }
                });

                card.append(policy);
                box.append(card);
                wrap.append(box);
            }

            if (!instances.length) {
                wrap.append(empty('No instances federated yet.', 'external-link'));
                return wrap;
            }

            const list = el('div', 'rows');

            instances.forEach(instance => {
                const title = [el('span', null, instance.host)];

                title.push(tag(instance.status,
                    instance.status === 'Active' ? 'ok'
                        : instance.status === 'Pending' ? 'warn' : 'danger'));

                list.append(row({
                    mark: instance.status === 'Pending' ? 'high' : instance.status === 'Active' ? '' : 'critical',
                    title,
                    sub: instance.defederationReason || instance.name,
                    side: [agoNode(instance.lastSeen)],
                    selected: instance.id === selectedId,
                    onOpen: () => openInstance(instance),
                }));
            });

            wrap.append(list, listFoot(instances.length, instances.length));
            return wrap;
        },
    };

    function openInstance(instance) {
        selectedId = instance.id;

        const pane = el('div', 'pane');

        pane.append(block(null, kv([
            ['Host', copyable(instance.host)],
            ['Name', instance.name],
            ['Status', instance.status],
            ['Id', copyable(instance.id)],
            ['Last seen', stamp(instance.lastSeen)],
            ['Federated', stamp(instance.createdAt)],
            ['Shared resources', instance.federatedResources?.length ?? 0],
        ])));

        if (instance.defederationReason) {
            pane.append(block('Defederated because', el('div', 'quote', instance.defederationReason)));
        }

        const actions = el('div', 'btn-row');

        if (instance.status === 'Pending') {
            actions.append(button('Approve', 'check-circle', () => approve(instance), 'primary'));

            actions.append(button('Deny', 'times-circle', async () => {
                await call('POST', `${FED}/${encodeURIComponent(instance.id)}/deny`);
                toast('ok', `${instance.host} denied`);
                closeDetail();
                render();
            }, 'danger'));
        }

        if (instance.status === 'Active') {
            actions.append(button('Defederate', 'ban', () => defederate(instance), 'danger'));
        }

        pane.append(block('Actions', actions.children.length
            ? actions
            : el('p', 'hint', 'Nothing to do from here in this state.')));

        openDetail(instance.host, pane);
    }

    /**
     * Approval, behind the same kind of gesture defederation asks for.
     *
     * Defederating is the reversible direction - an instance shut out can be let back in - and it has
     * a modal and a mandatory reason. Approving is the direction that opens the door, since Active is
     * the only thing standing between a remote instance and injecting events here, and it used to be
     * one click on a row somebody may have opened to read rather than to act on.
     *
     * No reason field: nothing is recorded against an instance that is about to be trusted, and a box
     * nobody has to fill in is not a second gesture. The second click is.
     */
    function approve(instance) {
        modal(close => {
            const form = el('form');
            form.append(el('h2', null, `Approve ${instance.host}?`));
            form.append(el('p', 'lede',
                `${instance.name || instance.host} becomes Active, and events from it are accepted `
                + 'here from then on - its accounts can reach members of this instance, and what it '
                + 'sends federates in. Defederating later stops new events; what already arrived '
                + 'stays.'));

            const actions = el('div', 'actions');
            const cancel = el('button', 'btn', 'Cancel');
            cancel.type = 'button';
            cancel.addEventListener('click', close);

            const confirm = el('button', 'btn primary', 'Approve');
            confirm.type = 'submit';
            actions.append(cancel, confirm);
            form.append(actions);

            form.addEventListener('submit', async event => {
                event.preventDefault();
                confirm.disabled = true;

                try {
                    await call('POST', `${FED}/${encodeURIComponent(instance.id)}/approve`);

                    close();
                    toast('ok', `${instance.host} approved`);
                    closeDetail();
                    render();
                } catch (error) {
                    toast('danger', error.message);
                    confirm.disabled = false;
                }
            });

            return form;
        });
    }

    function defederate(instance) {
        modal(close => {
            const form = el('form');
            form.append(el('h2', null, `Defederate ${instance.host}`));
            form.append(el('p', 'lede',
                'This stops accepting events from that instance. Content already federated here stays; '
                + 'nothing new arrives.'));

            const reason = el('textarea');
            reason.rows = 3;
            reason.required = true;
            reason.placeholder = 'Why. Recorded against the instance.';
            form.append(field('Reason', reason, null, { required: true }));

            const actions = el('div', 'actions');
            const cancel = el('button', 'btn', 'Cancel');
            cancel.type = 'button';
            cancel.addEventListener('click', close);

            const confirm = el('button', 'btn primary danger', 'Defederate');
            confirm.type = 'submit';
            actions.append(cancel, confirm);
            form.append(actions);

            form.addEventListener('submit', async event => {
                event.preventDefault();
                confirm.disabled = true;

                try {
                    await call('POST', `${FED}/${encodeURIComponent(instance.id)}/defederate`, {
                        body: { reason: reason.value },
                    });

                    close();
                    toast('ok', `${instance.host} defederated`);
                    closeDetail();
                    render();
                } catch (error) {
                    toast('danger', error.message);
                    confirm.disabled = false;
                }
            });

            return form;
        });
    }

    function initiateHandshake() {
        modal(close => {
            const form = el('form');
            form.append(el('h2', null, 'Federate with an instance'));
            form.append(el('p', 'lede',
                'We contact the other instance and exchange keys. It has to accept us too, so this may '
                + 'end up Pending on their side.'));

            const host = el('input');
            host.required = true;
            host.placeholder = 'chat.example.com';
            host.spellcheck = false;
            form.append(field('Their hostname', host, 'The instance host, without a scheme or a path.',
                { required: true }));

            const actions = el('div', 'actions');
            const cancel = el('button', 'btn', 'Cancel');
            cancel.type = 'button';
            cancel.addEventListener('click', close);

            const confirm = el('button', 'btn primary', 'Start handshake');
            confirm.type = 'submit';
            actions.append(cancel, confirm);
            form.append(actions);

            form.addEventListener('submit', async event => {
                event.preventDefault();
                confirm.disabled = true;

                try {
                    await call('POST', `${FED}/initiate`, { body: { targetHost: host.value.trim() } });
                    close();
                    toast('ok', 'Handshake started');
                    render();
                } catch (error) {
                    toast('danger', error.message);
                    confirm.disabled = false;
                }
            });

            return form;
        });
    }

    // ── View: product catalog ───────────────────────────────────────────────
    //
    // The pantry's barcode table. Not moderation, and here anyway: it is the one operator job on
    // this instance that needs a file off a workstation, and a console already signed in as an
    // administrator beats handing somebody a curl line and a bearer token.
    //
    // These are the Guild service's own endpoints, reached through the gateway's guild-route, which
    // strips the /guild segment - so the path here is not the one the service's own routes declare.
    // Do not build these URLs from the info response's `exportUrl`: that field is the service's
    // internal path and answers 404 from this origin.

    const CATALOG = '/api/v1/guild/pantry/catalog';

    /**
     * The family, mirroring ProductCatalogSources. `bulk` is whether the service can fetch and load
     * that database by itself - ProductCatalogAutoImportService.Exports - which is everything except
     * food, whose export is 11.77 GB and is loaded from a file instead.
     *
     * Hardcoded for the same reason REASONS is: this console ships in the same image as the service
     * it talks to, so the two cannot drift apart in a deployment. The server validates the source
     * regardless and its 400 names the list it will accept.
     */
    const CATALOG_SOURCES = [
        { id: 'openfoodfacts', name: 'Open Food Facts', what: 'Groceries', bulk: false },
        { id: 'openbeautyfacts', name: 'Open Beauty Facts', what: 'Cosmetics and personal care', bulk: true },
        { id: 'openproductsfacts', name: 'Open Products Facts', what: 'Cleaning and everything else', bulk: true },
        { id: 'openpetfoodfacts', name: 'Open Pet Food Facts', what: 'Pet food', bulk: true },
    ];

    const CATALOG_SOURCE_NAME = Object.fromEntries(CATALOG_SOURCES.map(s => [s.id, s.name]));

    /**
     * Bytes per request when loading a file.
     *
     * The file is posted in pieces rather than in one request, which is the whole reason a
     * multi-hundred-megabyte extract can be loaded from a browser at all. Three things make one big
     * request wrong: Kestrel caps a body at 30 MB and the gateway in front of this one still does,
     * a single request holding a connection open for the length of a full import is what proxy
     * activity timeouts exist to kill, and a connection dropped at 90% loses the entire upload.
     *
     * Pieces are safe because the import is an upsert keyed on the barcode: each one is independent,
     * re-posting one changes nothing, and a run that stops halfway is fixed by running it again.
     *
     * 4 MB is roughly twenty thousand rows, or forty of the importer's committed batches - a couple
     * of seconds of server work, comfortably inside every timeout between here and the database.
     */
    const CHUNK_BYTES = 4 * 1024 * 1024;

    views.catalog = {
        title: 'Product catalog',

        async render() {
            const info = await call('GET', CATALOG);

            const wrap = el('div', 'pane');

            wrap.append(tiles([
                ['Products', info.count],
                ...info.sources.map(source => [
                    CATALOG_SOURCE_NAME[source.source] || source.source, source.count,
                ]),
            ]));

            wrap.append(block('This instance\'s copy', kv([
                ['Rows', info.count.toLocaleString()],
                ['Last import', stamp(info.lastImportedAt)],
                ['Licence', info.license],
                ['Snapshots', info.sourceVersions.length
                    ? el('span', 'mono', info.sourceVersions.slice(0, 6).join(', '))
                    : 'None yet'],
                // The public copy the licence obliges us to offer. A plain link because the export
                // is anonymous by design - it is owed to every recipient, not to every account.
                ['Export', linkOut(`${location.origin}${CATALOG}/export`)],
            ])));

            wrap.append(catalogRefreshBlock(info));
            wrap.append(catalogUploadBlock());
            wrap.append(catalogSearchBlock());

            wrap.append(block('Attribution', el('p', 'hint', info.attribution)));

            return wrap;
        },
    };

    /** The scheduled import, run now. Only for the databases small enough that the service fetches
     *  them itself; food has no button here because it has no automatic import. */
    function catalogRefreshBlock(info) {
        const box = block('Fetch from the source');

        box.append(el('p', 'hint',
            'Downloads the published export and loads it. This normally happens by itself in the '
            + 'small hours; the button is for an instance that wants coverage today. It answers '
            + 'immediately and keeps working in the background - watch the counts above, refreshing '
            + 'this page, rather than waiting on the button.'));

        const buttons = el('div', 'btn-row');

        CATALOG_SOURCES.filter(source => source.bulk).forEach(source => {
            const held = info.sources.find(s => s.source === source.id);

            buttons.append(button(
                held ? `Refresh ${source.name}` : `Load ${source.name}`, 'refresh',
                async () => {
                    const started = await call('POST', `${CATALOG}/import/auto`, {
                        query: { source: source.id },
                    });
                    toast('ok', started.message || 'Import started');
                }));
        });

        box.append(buttons);

        box.append(el('p', 'hint',
            'Open Food Facts is not here: its export is 11.77 GB, too big to stream through a '
            + 'service, so groceries are loaded from a file below. Until they are, food barcodes '
            + 'still resolve one at a time as households scan them - what is missing is finding a '
            + 'grocery by name that nobody has scanned yet.'));

        return box;
    }

    /**
     * A locally-produced extract, posted in pieces.
     *
     * The file is deploy/off-catalog-extract.sh's output: newline-delimited JSON, one product per
     * line, already filtered to the markets this instance serves. It never lands in memory whole -
     * each piece is sliced off the File as it is sent, so the size of the file is not the size of
     * anything this tab holds.
     */
    function catalogUploadBlock() {
        const box = block('Load an extract file');

        box.append(el('p', 'hint',
            'The output of deploy/off-catalog-extract.sh - newline-delimited JSON, one product per '
            + 'line. This is how the grocery database gets loaded. Sent in 4 MB pieces, so a dropped '
            + 'connection costs one piece rather than the upload, and re-running a file that partly '
            + 'loaded is safe.'));

        const picker = el('input');
        picker.type = 'file';
        picker.accept = '.jsonl,.ndjson,.json,application/x-ndjson';

        const source = select(CATALOG_SOURCES.map(s => [s.id, `${s.name} - ${s.what}`]), 'openfoodfacts');

        const version = el('input');
        version.placeholder = 'openfoodfacts-2026-08-08';

        // Prefilled from the file's own modification date, not from today's. The snapshot a row came
        // from is a fact about when the extract was built; a file sitting on a workstation for a
        // fortnight before somebody loads it is not a fortnight fresher for having been uploaded.
        function suggest() {
            const file = picker.files?.[0];
            const when = file ? new Date(file.lastModified) : new Date();
            version.value = `${source.value}-${when.toISOString().slice(0, 10)}`;
        }

        picker.addEventListener('change', suggest);
        source.addEventListener('change', suggest);

        box.append(field('File', picker, null, { required: true }));
        box.append(field('Database', source,
            'Decides which database each row is credited to and which product page a client links '
            + 'to. Getting it wrong misattributes every row in the file.'));
        box.append(field('Snapshot', version,
            'Travels with every row and appears in the public export, so a reader can tell which '
            + 'extract an answer came from.',
            { required: true }));

        const bar = el('div', 'progress');
        const fill = el('span');
        bar.append(fill);
        bar.classList.add('hidden');

        const note = el('div', 'upload-note');

        const actions = el('div', 'btn-row');
        let aborter = null;

        const cancel = el('button', 'btn ghost');
        cancel.append(icon('times'), document.createTextNode(' Stop'));
        cancel.classList.add('hidden');
        cancel.addEventListener('click', () => aborter?.abort());

        const load = el('button', 'btn primary');
        load.append(icon('send'), document.createTextNode(' Load'));

        load.addEventListener('click', async () => {
            const file = picker.files?.[0];

            // Beside the control that is empty, not in the corner of the screen: this block is three
            // fields tall and a toast naming neither of them leaves the operator guessing which.
            if (!file) { fieldError(picker, 'Choose the extract file to load.'); return; }

            if (!version.value.trim()) {
                fieldError(version, 'Name the snapshot this file came from - it travels with every row.');
                return;
            }

            aborter = new AbortController();
            load.disabled = picker.disabled = source.disabled = version.disabled = true;
            cancel.classList.remove('hidden');
            bar.classList.remove('hidden');
            fill.style.width = '0';

            // Mirrored out here so the catch below can say where a stopped upload got to. The
            // pieces already sent are committed, so "how far" is the one thing the operator needs.
            let sent = 0;

            try {
                const result = await postExtract({
                    file,
                    source: source.value,
                    sourceVersion: version.value.trim(),
                    signal: aborter.signal,
                    onProgress(state) {
                        sent = state.sent;
                        fill.style.width = `${Math.round((state.sent / file.size) * 100)}%`;
                        note.textContent =
                            `${bytes(state.sent)} of ${bytes(file.size)} - `
                            + `${state.totals.created.toLocaleString()} new, `
                            + `${state.totals.updated.toLocaleString()} updated`;
                    },
                });

                note.textContent =
                    `Done. ${result.read.toLocaleString()} rows read, `
                    + `${result.created.toLocaleString()} new, `
                    + `${result.updated.toLocaleString()} updated, `
                    + `${result.skipped.toLocaleString()} skipped, `
                    + `${result.malformed.toLocaleString()} unreadable, `
                    + `${result.missesResolved.toLocaleString()} previously-missing barcodes answered.`;

                toast('ok', `Loaded ${result.created + result.updated} products`);
            } catch (error) {
                // An abort is the operator's own decision, not a failure. Everything already sent
                // stayed - the pieces are committed as they land - so the honest thing to say is
                // where it stopped, not that nothing happened.
                if (error.name === 'AbortError') {
                    note.textContent =
                        `Stopped after ${bytes(sent)} of ${bytes(file.size)}. What was sent is `
                        + 'loaded - re-run the same file to finish it; nothing is written twice.';
                    toast('info', 'Upload stopped');
                } else {
                    note.textContent = error.message;
                    toast('danger', error.message);
                }
            } finally {
                aborter = null;
                load.disabled = picker.disabled = source.disabled = version.disabled = false;
                cancel.classList.add('hidden');
            }
        });

        actions.append(load, cancel);
        box.append(bar, note, actions);

        return box;
    }

    /**
     * Posts the file a piece at a time, cutting each piece at a line boundary.
     *
     * The cut is on the last newline *byte* in the slice rather than on a character in decoded text,
     * so a piece boundary landing in the middle of a multi-byte character cannot corrupt it: the
     * bytes are never decoded here at all, they are forwarded as they were read.
     */
    async function postExtract({ file, source, sourceVersion, onProgress, signal }) {
        const totals = { read: 0, created: 0, updated: 0, skipped: 0, malformed: 0, missesResolved: 0 };
        let sent = 0;

        while (sent < file.size) {
            const end = Math.min(sent + CHUNK_BYTES, file.size);
            let piece = new Uint8Array(await file.slice(sent, end).arrayBuffer());

            if (end < file.size) {
                const newline = piece.lastIndexOf(10);

                // No newline in four megabytes is not a long row, it is the wrong file - a Parquet
                // or a CSV, most likely. Said plainly here because the server would otherwise
                // receive it, reject every line as malformed and report a successful import of
                // nothing.
                if (newline < 0) {
                    throw new Error(
                        'This does not look like newline-delimited JSON: the first four megabytes '
                        + 'contain no line break. The import wants the .jsonl output of '
                        + 'off-catalog-extract.sh, not the Parquet or CSV the source publishes.');
                }

                piece = piece.subarray(0, newline + 1);
            }

            const report = await call('POST', `${CATALOG}/import`, {
                raw: piece,
                contentType: 'application/x-ndjson',
                query: { source, sourceVersion },
                signal,
            });

            Object.keys(totals).forEach(key => { totals[key] += report[key] ?? 0; });

            sent += piece.length;
            onProgress({ sent, totals });
        }

        return totals;
    }

    function bytes(count) {
        if (count < 1024) return `${count} B`;
        if (count < 1024 * 1024) return `${(count / 1024).toFixed(0)} KB`;
        if (count < 1024 * 1024 * 1024) return `${(count / 1024 / 1024).toFixed(1)} MB`;
        return `${(count / 1024 / 1024 / 1024).toFixed(2)} GB`;
    }

    /** Does the table actually answer? The one question the numbers above cannot settle: a count of
     *  four hundred thousand says nothing about whether a household can find shampoo by typing it. */
    function catalogSearchBlock() {
        const box = block('Spot check');

        box.append(el('p', 'hint',
            'The same search the app calls. Three characters minimum, matched against the German, '
            + 'French, Italian and English names and the brand.'));

        const query = el('input');
        query.type = 'search';
        query.placeholder = 'shampoo, spülmittel, ...';

        const results = el('div');
        let timer = null;

        query.addEventListener('input', () => {
            clearTimeout(timer);

            const term = query.value.trim();
            if (term.length < 3) { results.replaceChildren(); return; }

            timer = setTimeout(async () => {
                try {
                    const found = await call('GET', `${CATALOG}/search`, {
                        query: { q: term, limit: 15 },
                    });

                    results.replaceChildren(catalogResults(found));
                } catch (error) {
                    results.replaceChildren(banner('danger', error.message));
                }
            }, 250);
        });

        box.append(field('Search', query), results);
        return box;
    }

    function catalogResults(found) {
        if (!found.results.length) return empty(`Nothing matches "${found.query}".`, 'search');

        const list = el('div', 'rows');

        found.results.forEach(match => {
            const title = [el('span', null, match.name)];
            title.push(tag(CATALOG_SOURCE_NAME[match.source] || match.source));

            list.append(row({
                title,
                sub: [match.brand, match.quantity ? `${match.quantity}${match.quantityUnit || ''}` : null]
                    .filter(Boolean).join(' - ') || match.barcode,
                side: [el('span', 'rw-time mono', match.barcode)],
                onOpen: () => openDetail(match.name, catalogDetail(match)),
            }));
        });

        const foot = el('div', 'list-foot',
            found.countIsLowerBound
                ? `Showing ${found.results.length} of more than ${found.count}`
                : `Showing ${found.results.length} of ${found.count}`);

        const wrap = el('div');
        wrap.append(list, foot);
        return wrap;
    }

    function catalogDetail(match) {
        const pane = el('div', 'pane');

        pane.append(block(null, kv([
            ['Barcode', copyable(match.barcode)],
            ['Name', match.name],
            ['Language', match.language],
            ['Brand', match.brand],
            ['Pack', match.quantity ? `${match.quantity} ${match.quantityUnit || ''}`.trim() : null],
            ['Database', match.sourceName],
            ['Product page', linkOut(match.sourceUrl)],
            ['Loaded', stamp(match.importedAt)],
        ])));

        pane.append(block('Attribution', el('p', 'hint', match.attribution)));
        return pane;
    }

    // ── View: status page ───────────────────────────────────────────────────
    //
    // Three panes behind one rail item. Signals is the technical view - error rates, request
    // counts, destination health - and is the only place in the product those numbers exist; the
    // public page is forbidden from carrying them (docs/specs/status-and-incidents.md §5).
    //
    // Reads are open to moderators. Every write here is administrator-only, because publishing to
    // status.<host> is a public statement in the instance's name rather than a decision about one
    // account. The buttons are hidden for a moderator and the server refuses them regardless.

    const STATUS = `${API}/status`;

    let statusTab = 'incidents';

    const INCIDENT_STATES = [
        ['Investigating', 'Investigating - we do not yet know the cause'],
        ['Identified', 'Identified - we know the cause'],
        ['Monitoring', 'Monitoring - a fix is out, watching it'],
        ['Resolved', 'Resolved - it is over'],
    ];

    const MAINTENANCE_STATES = [
        ['Scheduled', 'Scheduled'],
        ['InProgress', 'In progress'],
        ['Completed', 'Completed'],
        ['Cancelled', 'Cancelled'],
    ];

    const IMPACTS = [
        ['None', 'None - nothing a user would notice'],
        ['Minor', 'Minor - degraded, most things work'],
        ['Major', 'Major - a part of the platform is down'],
        ['Critical', 'Critical - the platform is unusable'],
    ];

    const STATE_TEXT = {
        investigating: 'Investigating', identified: 'Identified', monitoring: 'Monitoring',
        resolved: 'Resolved', scheduled: 'Scheduled', in_progress: 'In progress',
        completed: 'Completed', cancelled: 'Cancelled',
    };

    const COMPONENT_TEXT = {
        operational: 'Operational', degraded_performance: 'Degraded', partial_outage: 'Partial outage',
        major_outage: 'Major outage', under_maintenance: 'Maintenance',
    };

    /** Unknown slugs read as themselves rather than as a guess. A build that has not been told about
     *  a new state should say so, not pick the scariest one it knows. */
    function pretty(map, slug) {
        return map[slug] || (slug ? slug.replace(/_/g, ' ').replace(/^./, c => c.toUpperCase()) : '-');
    }

    function impactKind(impact) {
        if (impact === 'critical') return 'danger';
        if (impact === 'major') return 'danger';
        if (impact === 'minor') return 'warn';
        return 'info';
    }

    function statusGo(tab) {
        statusTab = tab;
        $('#view-tools').replaceChildren(...(views.status.tools() || []));
        render();
    }

    views.status = {
        title: 'Status page',

        tools() {
            const tabs = [['incidents', 'Incidents'], ['signals', 'Signals'], ['components', 'Components']]
                .map(([key, text]) => {
                    const button = el('button', `btn sm ${statusTab === key ? 'primary' : 'ghost'}`, text);
                    button.addEventListener('click', () => statusGo(key));
                    return button;
                });

            if (!session?.canViewAudit) return tabs;

            const declare = el('button', 'btn sm primary');
            declare.append(icon('plus'), document.createTextNode(' Publish'));
            declare.addEventListener('click', () => declareIncident());
            tabs.push(declare);

            return tabs;
        },

        async render() {
            if (statusTab === 'signals') return renderSignals();
            if (statusTab === 'components') return renderStatusComponents();
            return renderStatusIncidents();
        },
    };

    async function renderStatusIncidents() {
        const { incidents, total, unconfirmed } = await call('GET', `${STATUS}/incidents`, {
            query: { limit: 50, includeRetracted: true },
        });

        const wrap = el('div');

        if (unconfirmed > 0) {
            // The one thing on this page waiting for a person: the detector published something and
            // nobody has looked at it. It will resolve itself on the first clean window unless
            // somebody takes it over.
            wrap.append(banner('warn',
                `${unconfirmed} automatic incident${unconfirmed === 1 ? '' : 's'} nobody has acknowledged. `
                + 'Unless you take one over, it will resolve itself as soon as the signal clears.'));
        }

        if (!incidents.length) {
            wrap.append(empty('Nothing has ever been published here.', 'activity'));
            return wrap;
        }

        const rows = el('div', 'rows');

        /*
         * Runs of the same automatic incident, collapsed into one row.
         *
         * The detector republishes an incident every time its window reopens, so a signal that flaps
         * for an hour arrives as eight rows carrying the same title, the same component and the same
         * state, minutes apart. Read down the list that is eight things wrong; it is one thing wrong
         * eight times, and the two are not the same page to somebody deciding what to do next.
         *
         * Only automatic ones, and only consecutive ones: two incidents a person published under one
         * title are two statements about the world, and a run interrupted by something else is no
         * longer one episode. Nothing is filtered away - the head row opens the run in place, every
         * occurrence keeps its own row, its own reference and its own detail pane, and the count in
         * the foot still counts all of them.
         */
        const runs = [];

        incidents.forEach(incident => {
            // What makes this the same incident republished rather than a new one, matched against
            // whichever run is currently open. Compared field by field: a joined key would make two
            // different incidents equal whenever the separator happened to fall differently.
            const previous = runs[runs.length - 1];
            const against = previous?.items[0];

            const same = incident.origin === 'automatic'
                && against?.origin === 'automatic'
                && against.title === incident.title
                && against.status === incident.status
                && against.isRetracted === incident.isRetracted
                && against.components.join(',') === incident.components.join(',');

            if (same) previous.items.push(incident);
            else runs.push({ items: [incident] });
        });

        // Where each incident's row goes: into the list, or into the container its run's head row
        // reveals. Settled before the loop below so that the row itself is still built one way only.
        const destination = new Map();

        runs.forEach(run => {
            if (run.items.length === 1) {
                destination.set(run.items[0].id, rows);
                return;
            }

            const nest = el('div', 'rows hidden');
            nest.style.marginLeft = '18px';
            rows.append(runRow(run, nest), nest);
            run.items.forEach(incident => destination.set(incident.id, nest));
        });

        incidents.forEach(incident => {
            const list = destination.get(incident.id);

            const title = el('div', 'rw-title');
            title.append(el('strong', null, incident.title));

            if (incident.origin === 'automatic') title.append(tag('Automatic', 'info'));
            if (incident.origin === 'automatic' && !incident.confirmed && !incident.resolvedAt) {
                title.append(tag('Unacknowledged', 'warn'));
            }
            if (incident.isRetracted) title.append(tag('Retracted'));
            if (incident.kind === 'maintenance') title.append(tag('Maintenance'));

            list.append(row({
                mark: incident.resolvedAt ? '' : (impactKind(incident.impact) === 'danger' ? 'critical' : 'high'),
                title: [title],
                sub: `${pretty(STATE_TEXT, incident.status)} · ${incident.components.join(', ') || 'no components'} · ${incident.reference}`,
                side: [agoNode(incident.startedAt)],
                onOpen: () => openIncident(incident.id),
            }));
        });

        wrap.append(rows, listFoot(total, incidents.length));
        return wrap;
    }

    /** The single row a collapsed run shows, and the click that opens the run under it. */
    function runRow(run, nest) {
        const first = run.items[0];

        const title = el('div', 'rw-title');
        title.append(el('strong', null, first.title));
        title.append(tag('Automatic', 'info'));
        if (first.isRetracted) title.append(tag('Retracted'));
        title.append(tag(`${run.items.length} times`, 'warn'));

        const node = row({
            mark: first.resolvedAt ? '' : (impactKind(first.impact) === 'danger' ? 'critical' : 'high'),
            title: [title],
            sub: `${pretty(STATE_TEXT, first.status)} · ${first.components.join(', ') || 'no components'} · `
                + `${run.items.length} in a row, open to list them`,
            side: [agoNode(first.startedAt)],
            onOpen: () => {
                const collapsed = nest.classList.toggle('hidden');
                node.setAttribute('aria-expanded', String(!collapsed));
            },
        });

        node.setAttribute('aria-expanded', 'false');
        return node;
    }

    async function openIncident(id) {
        const incident = await call('GET', `${STATUS}/incidents/${id}`);
        const pane = el('div');

        pane.append(kv([
            ['Reference', copyable(incident.reference)],
            ['Kind', incident.kind === 'maintenance' ? 'Maintenance' : 'Incident'],
            ['State', pretty(STATE_TEXT, incident.status)],
            ['Impact', tag(pretty({}, incident.impact), impactKind(incident.impact))],
            ['Origin', incident.origin === 'automatic' ? 'Detected automatically' : 'Published by staff'],
            ['Components', incident.components.join(', ') || '-'],
            ['Started', stamp(incident.startedAt)],
            ['Resolved', incident.resolvedAt ? stamp(incident.resolvedAt) : null],
            ['Scheduled', incident.scheduledFor ? `${stamp(incident.scheduledFor)} - ${stamp(incident.scheduledUntil)}` : null],
            ['Public link', linkOut(incident.url)],
        ]));

        if (incident.detectionDetail) {
            // Staff-only, and the reason the public copy is allowed to be vague. Everything the
            // detector measured is here and nowhere else.
            pane.append(block('What the detector saw', el('pre', 'pre-wrap small', incident.detectionDetail)));
        }

        if (incident.updates?.length) {
            const list = el('div', 'rows');

            incident.updates.forEach(update => {
                const head = el('div', 'rw-title');
                head.append(el('strong', null, pretty(STATE_TEXT, update.status)));
                if (!update.authorUserId) head.append(tag('Automatic', 'info'));

                const item = el('div', 'block');
                item.append(head);
                item.append(el('div', 'rw-sub', stamp(update.postedAt)));
                item.append(el('p', 'pre-wrap', update.body));
                list.append(item);
            });

            pane.append(block('Timeline', list));
        }

        if (session?.canViewAudit && !incident.isRetracted) pane.append(incidentActions(incident));

        openDetail(incident.title, pane);
    }

    function linkOut(url) {
        const link = el('a', null, url);
        link.href = url;
        link.target = '_blank';
        link.rel = 'noreferrer';
        return link;
    }

    function incidentActions(incident) {
        const box = el('div', 'block');
        box.append(el('h3', null, 'Post an update'));

        // Said out loud rather than implied by a missing button: people expect an edit control here
        // and its absence is a decision, not an oversight.
        box.append(el('p', 'hint',
            'Updates cannot be edited or deleted once posted. To correct one, post another.'));

        const body = el('textarea');
        body.rows = 5;
        body.maxLength = 4000;
        body.placeholder = incident.origin === 'automatic'
            ? 'What is actually happening, in the words a user would use.'
            : 'What changed since the last update?';

        const state = select(
            incident.kind === 'maintenance' ? MAINTENANCE_STATES : INCIDENT_STATES,
            incident.kind === 'maintenance' ? 'InProgress' : 'Identified');

        box.append(field('State', state));
        box.append(field('Update', body, null, { required: true }));

        const post = button('Post update', 'send', async () => {
            if (!body.value.trim()) {
                fieldError(body, 'An update with no words posts a state change nobody can read.');
                return;
            }

            busy(post, true);
            try {
                await call('POST', `${STATUS}/incidents/${incident.id}/updates`, {
                    body: { body: body.value.trim(), status: state.value },
                });
                toast('ok', 'Posted');
                closeDetail();
                render();
                refreshBadges();
            } catch (error) {
                toast('danger', error.message);
            } finally {
                busy(post, false);
            }
        }, 'primary');

        const actions = el('div', 'actions');
        actions.append(post);

        if (incident.origin === 'automatic' && !incident.confirmed) {
            actions.append(button('Take over', 'user', async () => {
                try {
                    await call('POST', `${STATUS}/incidents/${incident.id}/confirm`);
                    toast('ok', 'This incident is yours now. It will not resolve itself.');
                    openIncident(incident.id);
                    render();
                } catch (error) {
                    toast('danger', error.message);
                }
            }));
        }

        actions.append(button('Retract', 'trash', () => retractIncident(incident), 'danger'));

        box.append(actions);
        return box;
    }

    function retractIncident(incident) {
        modal(close => {
            const form = el('div');
            form.append(el('h2', null, 'Take this off the public page?'));
            form.append(el('p', 'lede',
                'It stops appearing on status.' + location.hostname.split('.').slice(1).join('.')
                + ' and in every client banner. It is not deleted, and anyone who already read it still read it.'));

            const reason = el('input');
            reason.placeholder = 'Why (recorded in the audit log)';
            form.append(field('Reason', reason));

            const actions = el('div', 'actions');
            actions.append(button('Cancel', 'times', close));
            actions.append(button('Retract', 'trash', async () => {
                try {
                    await call('POST', `${STATUS}/incidents/${incident.id}/retract`, {
                        body: { reason: reason.value.trim() },
                    });
                    close();
                    closeDetail();
                    toast('ok', 'Retracted');
                    render();
                } catch (error) {
                    toast('danger', error.message);
                }
            }, 'danger'));

            form.append(actions);
            return form;
        });
    }

    /**
     * The publish form.
     *
     * One action, not two: the title, the impact, the components and the first update are all on
     * this screen, because an incident published with no first update is a red banner with nothing
     * under it - which is worse for a reader than silence.
     */
    async function declareIncident(prefill = {}) {
        const { components } = await call('GET', `${STATUS}/components`);

        modal(close => {
            const form = el('div');
            form.append(el('h2', null, 'Publish to the status page'));
            form.append(el('p', 'lede',
                'This goes live immediately, on the status page and in every client banner. Write what '
                + 'a user would notice, not what a monitor measured.'));

            const kind = select([['Incident', 'Incident'], ['Maintenance', 'Scheduled maintenance']], 'Incident');
            const title = el('input');
            title.maxLength = 200;
            title.value = prefill.title || '';
            title.placeholder = 'What a user would say is wrong';

            const impact = select(IMPACTS, prefill.impact || 'Minor');

            const body = el('textarea');
            body.rows = 5;
            body.maxLength = 4000;
            body.value = prefill.body || '';
            body.placeholder = 'What people are seeing, and that we are on it. No error rates.';

            const picker = el('div', 'check-list');
            const chosen = new Set(prefill.components || []);

            components.forEach(component => {
                const line = el('label', 'check');
                const box = el('input');
                box.type = 'checkbox';
                box.checked = chosen.has(component.key);
                box.addEventListener('change', () => box.checked ? chosen.add(component.key) : chosen.delete(component.key));
                line.append(box, el('span', null, component.name));
                picker.append(line);
            });

            const from = el('input');
            from.type = 'datetime-local';
            const until = el('input');
            until.type = 'datetime-local';

            const schedule = el('div', 'row hidden');
            schedule.append(field('From', from), field('Until', until));

            kind.addEventListener('change', () => schedule.classList.toggle('hidden', kind.value !== 'Maintenance'));

            form.append(field('Kind', kind));
            form.append(field('Title', title, null, { required: true }));
            form.append(field('Impact', impact, 'Decides the colour, and what the incident claims about its components.'));
            form.append(field('Affects', picker));
            form.append(schedule);
            form.append(field('First update', body, null, { required: true }));

            const actions = el('div', 'actions');
            actions.append(button('Cancel', 'times', close));

            const publish = button('Publish', 'send', async () => {
                // In the dialog, against the field that is empty. This form is built on a div rather
                // than a form element, so nothing native points at the gap, and a toast in the corner
                // of a dimmed screen names neither the field nor the fix.
                if (!title.value.trim()) {
                    fieldError(title, 'Give it a title. This is the line the status page leads with.');
                    return;
                }

                if (!body.value.trim()) {
                    fieldError(body, 'Write the first update. An incident with nothing under it is a red banner and no information.');
                    return;
                }

                busy(publish, true);
                try {
                    await call('POST', `${STATUS}/incidents`, {
                        body: {
                            kind: kind.value,
                            title: title.value.trim(),
                            body: body.value.trim(),
                            impact: impact.value,
                            components: [...chosen],
                            scheduledFor: kind.value === 'Maintenance' && from.value ? new Date(from.value).toISOString() : null,
                            scheduledUntil: kind.value === 'Maintenance' && until.value ? new Date(until.value).toISOString() : null,
                        },
                    });

                    close();
                    toast('ok', 'Published');
                    statusGo('incidents');
                } catch (error) {
                    toast('danger', error.message);
                    busy(publish, false);
                }
            }, 'primary');

            actions.append(publish);
            form.append(actions);
            return form;
        });
    }

    async function renderSignals() {
        const { replica, report } = await call('GET', `${STATUS}/signals`);
        const wrap = el('div');

        if (!report.autoDetectionEnabled) {
            wrap.append(banner('warn', 'Automatic detection is switched off on this instance. Nothing will be published without a person doing it.'));
        }

        // Said plainly, because it changes how the numbers should be read: several gateways each
        // see a slice of the traffic, and this is one of them.
        wrap.append(banner('info',
            `Counted on replica ${replica} over the last ${report.windowSeconds}s. `
            + `Degraded at ${(report.degradedRate * 100).toFixed(1)}%, outage at ${(report.outageRate * 100).toFixed(1)}%, `
            + `after ${report.openSamples} consecutive windows and at least ${report.minimumVolume} requests.`));

        const list = el('div', 'rows');

        report.components.forEach(signal => {
            const title = el('div', 'rw-title');
            title.append(el('strong', null, signal.name));

            if (!signal.monitored) title.append(tag('Manual', 'info'));
            if (signal.suppressed) title.append(tag('In maintenance', 'info'));
            if (signal.openIncidentReference) title.append(tag(signal.openIncidentReference, 'warn'));

            const numbers = signal.requests > 0
                ? `${signal.errors}/${signal.requests} failed (${(signal.errorRate * 100).toFixed(1)}%)`
                : 'no traffic in the window';

            const health = signal.destinations > 0
                ? ` · ${signal.destinations - signal.unhealthyDestinations}/${signal.destinations} destinations healthy`
                : '';

            const side = [el('span', 'rw-time', pretty(COMPONENT_TEXT, signal.status))];

            if (session?.canViewAudit) {
                const declare = el('button', 'btn sm');
                declare.append(icon('plus'));
                declare.title = `Publish an incident for ${signal.name}`;
                declare.addEventListener('click', event => {
                    event.stopPropagation();
                    declareIncident({ components: [signal.key], title: `Problems with ${signal.name}` });
                });
                side.unshift(declare);
            }

            list.append(row({
                mark: signal.verdict === 'outage' ? 'critical' : signal.verdict === 'degraded' ? 'high' : '',
                title: [title],
                sub: `${numbers}${health} · ${signal.badStreak} bad / ${signal.cleanStreak} clean in a row`,
                side,
                onOpen: () => {},
            }));
        });

        wrap.append(list);
        return wrap;
    }

    async function renderStatusComponents() {
        const { components } = await call('GET', `${STATUS}/components`);
        const list = el('div', 'rows');

        components.forEach(component => {
            const title = el('div', 'rw-title');
            title.append(el('strong', null, component.name));
            title.append(el('span', 'mono faint', component.key));
            if (!component.isVisible) title.append(tag('Hidden'));

            list.append(row({
                title: [title],
                sub: component.clusters.length ? component.clusters.join(', ') : 'Not monitored automatically',
                side: [el('span', 'rw-time', pretty(COMPONENT_TEXT, component.status))],
                onOpen: () => openStatusComponent(component),
            }));
        });

        return list;
    }

    function openStatusComponent(component) {
        const pane = el('div');

        pane.append(kv([
            ['Key', copyable(component.key)],
            ['State', pretty(COMPONENT_TEXT, component.status)],
            ['Since', stamp(component.statusSince)],
            ['Clusters', component.clusters.join(', ') || 'none'],
        ]));

        if (!session?.canViewAudit) {
            openDetail(component.name, pane);
            return;
        }

        const name = el('input');
        name.value = component.name;

        const description = el('input');
        description.value = component.description || '';

        const hint = el('textarea');
        hint.rows = 3;
        hint.maxLength = 300;
        hint.value = component.impactHint || '';

        const visible = el('input');
        visible.type = 'checkbox';
        visible.checked = component.isVisible;

        const edit = el('div', 'block');
        edit.append(el('h3', null, 'Edit'));
        edit.append(field('Name', name, 'Shown on the public page.'));
        edit.append(field('Description', description));
        edit.append(field('Impact sentence', hint,
            'The sentence an automatically generated incident is built from. Say what a user would '
            + 'notice, in their words - it is published verbatim.'));

        const visibleRow = el('label', 'check');
        visibleRow.append(visible, el('span', null, 'Show on the public page'));
        edit.append(visibleRow);

        const save = button('Save', 'check', async () => {
            busy(save, true);
            try {
                await call('PATCH', `${STATUS}/components/${component.id}`, {
                    body: {
                        name: name.value.trim(),
                        description: description.value.trim(),
                        impactHint: hint.value.trim(),
                        isVisible: visible.checked,
                    },
                });
                toast('ok', 'Saved');
                closeDetail();
                render();
            } catch (error) {
                toast('danger', error.message);
            } finally {
                busy(save, false);
            }
        }, 'primary');

        const actions = el('div', 'actions');
        actions.append(save);
        edit.append(actions);
        pane.append(edit);

        openDetail(component.name, pane);
    }

    // ── View: billing ───────────────────────────────────────────────────────
    //
    // Two panes behind one rail item: a subject - what it resolves to, which source won each key, its
    // grants and its plan - and the plan catalogue.
    //
    // Reads admit moderators, every write is administrator-only. Money is not a moderation power: a
    // moderator who can grant Pro can grant it to themselves, and the moderator tier is handed out far
    // more freely than the admin one precisely because it was never meant to reach anything with a
    // cost attached. Answering "why does this guild have Pro" is support work though, so the screen
    // that answers it is open to them. The write controls are not drawn for a moderator, the gateway
    // refuses them, and the billing service refuses them again with the same token against the same
    // authority - none of the three is load-bearing alone.

    const BILLING = `${API}/billing`;

    let billingTab = 'subject';

    /** The key table, the enums and whether this instance sells anything. Fetched once: it changes
     *  only when the image does. */
    let billingCatalogue = null;

    /** What the subject pane is pointed at. Kept across tab switches and re-renders, because an
     *  operator working one ticket looks at one guild from several angles. */
    const billingSubject = { kind: 'Guild', id: '' };

    /** Which wallet the credit tab is pointed at. Its own field rather than `billingSubject`: wallets
     *  are user-scoped and guilds do not hold balances (section 8.4), so a guild id in the subject box
     *  is not a wallet that failed to load, it is a question with no answer. */
    const creditWallet = { id: '' };

    /** The campaigns the credit tab last read, kept so the issue dialog can offer them instead of
     *  asking somebody to retype a code that is listed on the screen behind it. Null until the tab
     *  has been opened, and left alone when the fetch fails, so the dialog can tell the two apart. */
    let creditCampaigns = null;

    /** Which subject the promotions tab has been asked about. A kind as well as an id, unlike the
     *  wallet above: a trial is recorded against the owner account and against the guild it was
     *  applied to, and those two rows answer different halves of "why can this account not start a
     *  trial". */
    const promotionSubject = { kind: 'User', id: '' };

    /** The eligibility rule catalogue, fetched once per session. Served by the billing service rather
     *  than written here, so a rule added there appears in the campaign editor without a deploy of
     *  this image - which is the whole reason there is an endpoint for it. */
    let promotionRules = null;

    /**
     * Whether to draw the write controls.
     *
     * Read from the catalogue rather than from the console-wide admin flag, so the console asks the
     * same question the gateway answers for itself on every write. The session flag is the fallback
     * for the moment before the catalogue lands, and the two say the same thing today - but a control
     * drawn from a different source than the one that refuses it is how a button that 403s appears.
     *
     * It is not a permission either way. Every write re-resolves the tier from Identity, twice.
     */
    function canEditBilling() {
        return billingCatalogue?.canWrite ?? Boolean(session?.canViewAudit);
    }

    /** The six precedence bands, in the words the spec uses for them. `CatalogueDefault` is not a
     *  source and says so - it is what the resolver credits when nobody supplied a key, and leaving
     *  it blank would read as a bug rather than as an answer. */
    const SOURCE_TEXT = {
        LicenseMode: 'Self-host license',
        AdminGrant: 'Admin grant',
        PromotionalGrant: 'Promotion',
        Subscription: 'Subscription',
        Boost: 'Boost',
        PlanDefault: 'Plan default',
        CatalogueDefault: 'Nobody - catalogue default',
    };

    const SOURCE_KIND = {
        LicenseMode: 'info',
        AdminGrant: 'warn',
        PromotionalGrant: 'warn',
        Subscription: 'ok',
        Boost: 'ok',
        PlanDefault: 'info',
        CatalogueDefault: '',
    };

    /**
     * A refusal's code as a heading.
     *
     * The service's own sentence is always shown underneath and is the useful half - it names the
     * offending key and lists the ones it accepts. The heading exists so the shape of the problem is
     * readable before the sentence is.
     */
    const BILLING_REFUSALS = {
        unknown_entitlement_key: 'That key does not exist here',
        invalid_entitlement_value: 'That value does not fit its key',
        withholds_plan_independent_module: 'No plan may withhold that',
        reason_required: 'A reason is required',
        expiry_in_the_past: 'That expiry has already passed',
        already_revoked: 'That grant was already revoked',
        unknown_plan: 'No such plan',
        plan_exists: 'That plan already exists',
        plan_in_use: 'Somebody is still on that plan',
        version_in_use: 'Somebody is still on that version',
        version_archived: 'That version is archived',
        invalid_price: 'That price cannot be stored',
        admin_required: 'This needs an administrator account',
        billing_not_deployed: 'This instance has no billing service',
        billing_unreachable: 'The billing service did not answer',

        // Credit. `not_reversible` is the one to read carefully: it is not a missing feature, it is
        // section 8.6's no-spend-then-refund rule holding. Reversing a spend would hand the credit
        // back for a grant that has already been given.
        user_required: 'A user id is required',
        actor_required: 'The service could not tell who is asking',
        idempotency_key_required: 'This request needs an idempotency key',
        amount_not_positive: 'An issuance has to be a positive number of points',
        amount_zero: 'An adjustment of nothing is not an adjustment',
        insufficient_balance: 'There is not enough in this wallet',
        wallet_cap_exceeded: 'That would take the wallet over its cap',
        unknown_campaign: 'No such campaign',
        campaign_closed: 'That campaign is paused or outside its dates',
        campaign_budget_exhausted: 'That campaign has no budget left',
        campaign_per_user_cap_reached: 'This account has had its share of that campaign',
        entry_not_found: 'No such ledger entry',
        not_reversible: 'Only an issuance can be reversed',
        nothing_to_reverse: 'That issuance has already been taken back',
        campaign_code_required: 'A campaign needs a code',
        campaign_description_required: 'A campaign needs a description',
        campaign_budget_required: 'A campaign needs a positive budget',
        campaign_threshold_out_of_range: 'That alert threshold is not a percentage',
        campaign_code_taken: 'That campaign code is already in use',
        campaign_not_found: 'No such campaign',
        campaign_budget_below_issued: 'That budget is below what has already been issued',

        // Promotions. The three "already" codes are the ones a support agent will meet most, and they
        // are refusals by design rather than faults: a redemption is permanent, an expired one still
        // refuses, and that is the whole of what stops a new guild every month being a new trial
        // every month.
        campaign_plan_required: 'A campaign needs the plan it confers',
        campaign_trial_days_required: 'A trial needs a length',
        campaign_unknown_rule: 'That is not an eligibility rule',
        already_redeemed: 'This account has already had this campaign',
        guild_already_redeemed: 'This guild has already had this campaign',
        identity_already_redeemed: 'A device, number or card here has already redeemed this',
        not_eligible: 'This account does not meet what the campaign asks',
        target_required: 'A guild has to be named, and managed by the person asking',
        not_permitted: 'This account may not do that',
        signals_unavailable: 'The eligibility signals could not be read',
        no_trial_to_move: 'There is no live trial to move',
    };

    views.billing = {
        title: 'Billing',

        tools() {
            // Credit and promotions are absent rather than empty without a billing service. Section
            // 8.8 asks for exactly that: in selfhost everything already resolves to the maximum, so a
            // wallet is meaningless and both an infinite balance and a zero one read as a bug - and a
            // trial that confers a plan somebody already has in full is the same nothing.
            const names = [['subject', 'Subject'], ['plans', 'Plans']];
            if (billingCatalogue?.billingDeployed) {
                names.push(['credit', 'Credit'], ['promotions', 'Promotions']);
            }

            const tabs = names.map(([key, text]) => {
                const button = el('button', `btn sm ${billingTab === key ? 'primary' : 'ghost'}`, text);
                button.addEventListener('click', () => billingGo(key));
                return button;
            });

            if (canEditBilling() && billingCatalogue?.billingDeployed) {
                if (billingTab === 'plans') {
                    const add = el('button', 'btn sm primary');
                    add.append(icon('plus'), document.createTextNode(' New plan'));
                    add.addEventListener('click', createPlan);
                    tabs.push(add);
                }

                if (billingTab === 'credit') {
                    const add = el('button', 'btn sm primary');
                    add.append(icon('plus'), document.createTextNode(' New campaign'));
                    add.addEventListener('click', createCreditCampaign);
                    tabs.push(add);
                }

                if (billingTab === 'promotions') {
                    const add = el('button', 'btn sm primary');
                    add.append(icon('plus'), document.createTextNode(' New campaign'));
                    add.addEventListener('click', createPromotionCampaign);
                    tabs.push(add);
                }
            }

            return tabs;
        },

        async render() {
            if (!billingCatalogue) {
                billingCatalogue = await call('GET', `${BILLING}/catalogue`);

                // The tools were drawn before the catalogue was known, so the New plan button could
                // not be. Redrawn once here rather than making every caller of go() await a fetch.
                $('#view-tools').replaceChildren(...views.billing.tools());
            }

            // Neither the credit nor the promotions tab exists on an instance with no billing
            // service, and a deep link or a stale tab from before the catalogue landed must not
            // leave the section blank.
            if (!billingCatalogue.billingDeployed && (billingTab === 'credit' || billingTab === 'promotions')) {
                billingTab = 'subject';
            }

            if (billingTab === 'plans') return renderPlans();
            if (billingTab === 'credit') return renderCredit();
            if (billingTab === 'promotions') return renderPromotions();
            return renderBillingSubject();
        },
    };

    function billingGo(tab) {
        billingTab = tab;
        $('#view-tools').replaceChildren(...(views.billing.tools() || []));
        render();
    }

    /** Point the section at a subject from somewhere else in the console - the accounts view does
     *  this, because "what is this account entitled to" is a question a support agent already has an
     *  id in hand for. */
    function openBillingFor(kind, id) {
        billingSubject.kind = kind;
        billingSubject.id = id;
        billingTab = 'subject';
        go('billing');
    }

    // ── Billing: the subject pane ───────────────────────────────────────────

    async function renderBillingSubject() {
        const wrap = el('div', 'pane billing');
        wrap.append(billingLookup());

        if (!billingCatalogue.billingDeployed) {
            // Not an error. Selfhost is the default and resolves everything from the license mode
            // above every other source, so there is genuinely nothing to grant - and the provenance
            // below is still the whole of the picture, which is worth saying rather than implying.
            wrap.append(banner('info',
                'This instance has no billing service, so there is nothing here to grant and no plan '
                + 'to edit. Entitlements resolve from the license mode and the configured plan table, '
                + 'and what you see below is the whole of the picture.'));
        }

        if (!billingSubject.id) {
            wrap.append(empty('Enter a user or guild id to see what it resolves to, and why.', 'search'));
            return wrap;
        }

        const path = `${billingSubject.kind}/${encodeURIComponent(billingSubject.id)}`;
        const deployed = billingCatalogue.billingDeployed;

        const [provenance, grants, assignment] = await Promise.all([
            call('GET', `${BILLING}/provenance/${path}`),
            deployed
                ? call('GET', `${BILLING}/grants/${path}`).catch(error => ({ error }))
                : null,
            // A subject that has never been assigned anything is a 404 and is most of them, so that
            // one status is an answer rather than a failure.
            deployed
                ? call('GET', `${BILLING}/plans/subjects/${path}`)
                    .catch(error => (error.status === 404 ? null : { error }))
                : null,
        ]);

        wrap.append(block(null, kv([
            ['Subject', provenance.subjectKind],
            ['Id', copyable(provenance.subjectId)],
            ['Version', provenance.versionAvailable
                ? String(provenance.version)
                : 'unknown - billing did not answer'],
            ['Plan', planLine(assignment, deployed)],
            ['Pinned', assignment && !assignment.error ? stamp(assignment.assignedAt) : null],
            ['Pinned by', assignment && !assignment.error ? assignment.assignedBy : null],
            ['Instance', provenance.licenseMode],
            ['Resolved', stamp(provenance.resolvedAt)],
        ])));

        if (canEditBilling()) wrap.append(subjectActions(deployed));

        const provenanceBox = block('Effective entitlements');
        provenanceBox.append(el('p', 'hint',
            'Every key this subject can hold, what it resolved to, and which source is credited with '
            + 'it. Sources are additive - a lower band never takes away what a higher one gave - so '
            + 'the credit goes to the highest-standing source that supplied the winning value.'));
        provenanceBox.append(provenanceTable(provenance.entries));
        wrap.append(provenanceBox);

        if (deployed) wrap.append(grantsBlock(grants));

        return wrap;
    }

    function planLine(assignment, deployed) {
        if (!deployed) return 'The configured default for this scope';
        if (assignment?.error) return 'could not be read just now';
        if (!assignment) return 'Not pinned - resolves through the instance default';

        return `${assignment.plan}, version ${assignment.versionNumber}`;
    }

    function billingLookup() {
        const box = block('Look up a subject');

        box.append(el('p', 'hint',
            'Every effective entitlement key for a user or a guild, its value, and which source won '
            + 'it. This is what answers "it says I have Pro but it does not work", and it reads from '
            + 'the same resolver the platform enforces with, cache and all.'));

        const form = el('form', 'lookup');

        const kind = select([['Guild', 'Guild'], ['User', 'User']], billingSubject.kind);
        kind.id = 'billing-kind';

        // Monospace because the value is an identifier that gets compared against one pasted from a
        // ticket, and a proportional font is where a transposed character hides.
        const id = el('input', 'mono');
        id.id = 'billing-id';
        id.value = billingSubject.id;
        id.spellcheck = false;
        id.autocomplete = 'off';
        id.placeholder = billingSubject.kind === 'Guild' ? 'guild_...' : 'user_...';

        kind.addEventListener('change', () => {
            id.placeholder = kind.value === 'Guild' ? 'guild_...' : 'user_...';
        });

        const submit = el('button', 'btn primary');
        submit.type = 'submit';
        submit.append(icon('search'), document.createTextNode(' Look up'));

        form.append(lookupField('Subject', kind), lookupField('Id', id), submit);

        form.addEventListener('submit', event => {
            event.preventDefault();
            billingSubject.kind = kind.value;
            billingSubject.id = id.value.trim();
            render();
        });

        box.append(form);
        return box;
    }

    /** A labelled cell of the lookup grid. Not `field()`: that stacks with a 16px gap underneath for
     *  a form read top to bottom, and this row is read left to right. */
    function lookupField(text, control) {
        const cell = el('div');
        const label = el('label', null, text);
        label.htmlFor = control.id;
        cell.append(label, control);
        return cell;
    }

    function provenanceTable(entries) {
        const table = el('div', 'prov');

        const head = el('div', 'prov-head');
        head.append(el('div', null, 'Key'), el('div', null, 'Resolved to'), el('div', null, 'Credited to'));
        table.append(head);

        entries.forEach(entry => {
            // A key nobody set is dimmed rather than dropped. The row missing is what makes somebody
            // conclude the screen is broken; the row present and quiet says "this is a floor, not a
            // decision anybody made".
            const line = el('div', `prov-row ${entry.isCatalogueDefault ? 'faint' : ''}`);

            const key = el('div', 'prov-key');
            key.append(el('span', 'mono', entry.key));
            key.append(el('span', 'faint', ` ${entry.scope.toLowerCase()}`));
            line.append(key);

            const value = el('div', 'prov-val');
            value.append(el('strong', null, entry.value));
            if (entry.value !== entry.default) {
                value.append(el('span', 'faint', ` (default ${entry.default})`));
            }
            line.append(value);

            const source = el('div', 'prov-src');
            source.append(tag(SOURCE_TEXT[entry.source] || entry.source, SOURCE_KIND[entry.source] ?? ''));
            if (entry.detail) source.append(el('span', 'mono faint', entry.detail));
            line.append(source);

            table.append(line);
        });

        return table;
    }

    function subjectActions(deployed) {
        const box = block('Actions');
        const actions = el('div', 'btn-row');

        if (deployed) {
            actions.append(button('Issue a grant', 'plus', issueGrant, 'primary'));
            actions.append(button('Move to a plan version', 'refresh', assignPlan));
        }

        actions.append(button('Force a re-resolve', 'refresh', forceInvalidate));
        box.append(actions);

        box.append(el('p', 'hint',
            'Forcing a re-resolve expires this subject\'s cached answer rather than deleting it, so '
            + 'the next request goes back to the sources while a billing outage still has something '
            + 'to fall back on. The cache key names the configuration that produced the answer, so '
            + 'this reaches every service configured like this gateway - which is all of them in a '
            + 'normal deployment - and leaves a differently configured one to its own TTL.'));

        return box;
    }

    async function forceInvalidate() {
        await call('POST', `${BILLING}/cache/${billingSubject.kind}/${encodeURIComponent(billingSubject.id)}/invalidate`);
        toast('ok', 'Re-resolved from the sources');
        render();
    }

    // ── Billing: grants ─────────────────────────────────────────────────────

    function grantsBlock(payload) {
        const box = block('Grants');

        if (payload?.error) {
            box.append(banner('warn', `The grant list could not be read: ${payload.error.message}`));
            return box;
        }

        box.append(el('p', 'hint',
            'Every grant ever attached to this subject, revoked and expired ones included. Nothing '
            + 'here is ever deleted: revoking sets fields and the row stays, because "who gave this '
            + 'guild Pro and why" has to be answerable a year later.'));

        if (!payload.grants.length) {
            box.append(el('p', 'hint', 'Nothing has ever been granted to this subject.'));
            return box;
        }

        const list = el('div', 'timeline');
        payload.grants.forEach(grant => list.append(grantEntry(grant)));
        box.append(list);

        return box;
    }

    function grantEntry(grant) {
        const item = el('div', `event ${grant.revokedAt ? 'struck' : ''}`);
        item.append(icon(grant.isActive ? 'check-circle' : grant.revokedAt ? 'times-circle' : 'clock'));

        const body = el('div');

        const line = el('div', 'what');
        line.append(document.createTextNode(grant.plan ? `Plan ${grant.plan}` : 'Specific entitlements'));
        line.append(document.createTextNode(' '));
        line.append(tag(grant.source));

        if (grant.isActive) line.append(tag('In force', 'ok'));
        else if (grant.revokedAt) line.append(tag('Revoked', 'danger'));
        else line.append(tag('Expired'));

        body.append(line);

        const meta = [`issued ${stamp(grant.createdAt)} by ${grant.createdBy}`];
        meta.push(grant.expiresAt ? `expires ${stamp(grant.expiresAt)}` : 'permanent');
        if (grant.revokedAt) meta.push(`revoked ${stamp(grant.revokedAt)} by ${grant.revokedBy}`);
        body.append(el('span', 'when', meta.join(' · ')));

        body.append(el('div', 'hint', grant.reason));
        if (grant.revokeReason) body.append(el('div', 'hint', `Revoked because: ${grant.revokeReason}`));

        const values = Object.entries(grant.entitlements || {});
        if (values.length) {
            const chips = el('div', 'chips');
            values.forEach(([key, value]) => chips.append(el('span', 'chip mono', `${key} = ${value}`)));
            body.append(chips);
        }

        body.append(el('div', 'hint mono', grant.id));

        if (canEditBilling() && !grant.revokedAt) {
            const actions = el('div', 'btn-row');
            actions.append(button('Revoke', 'times-circle', () => revokeGrant(grant), 'danger'));
            actions.append(button('Change expiry', 'clock', () => amendGrantExpiry(grant)));
            body.append(actions);
        }

        item.append(body);
        return item;
    }

    async function issueGrant() {
        // Fetched before the dialog opens so the plan can be picked rather than spelled. A billing
        // service that will not answer is no reason to refuse to open: a grant of entitlement keys
        // needs no plan at all, so a failure here falls back to the free-text field this dialog used
        // to have instead of leaving the operator with nothing.
        const plans = await planList().catch(() => null);

        modal(close => {
            const form = el('form');
            form.append(el('h2', null, 'Issue a grant'));
            form.append(el('p', 'lede',
                'A grant is additive: it raises what this subject resolves and can never lower it. '
                + 'Layering one over a paid subscription is safe, and letting it expire leaves the '
                + 'paid state intact because nothing ever overwrote it.'));

            const kind = select(
                billingCatalogue.grantKinds.map(name => [name, name === 'Plan'
                    ? 'A plan, by name'
                    : 'Specific entitlement keys']),
                'Plan');

            form.append(field('What to grant', kind));

            // The version pin stays, as its own control. It used to be typed into the name as
            // "pro@3", which is a real capability - a grant that has to keep resolving to one
            // specific set of numbers however often the plan is edited afterwards - and dropping it
            // for the sake of a dropdown would have cost more than the dropdown is worth.
            const plan = plans ? select(planChoices(plans), plans[0]?.name) : el('input');
            const version = el('input');
            version.type = 'number';
            version.min = '1';

            let planField;
            let versionField = null;

            if (plans) {
                planField = field('Plan', plan, 'Which plan this grant confers.');
                versionField = field('Version', version,
                    'Leave empty and the grant follows whatever version is current, which is what a '
                    + 'new customer gets. Naming one pins it to that set of numbers for good.');
            } else {
                plan.spellcheck = false;
                plan.placeholder = 'pro';
                planField = field('Plan', plan,
                    'The plan list could not be read just now, so this is the name as typed. Pin a '
                    + 'version with name@number to grant a specific set of numbers.');
            }

            form.append(planField);
            if (versionField) form.append(versionField);

            /** The two controls joined back into the one string the service takes. The pin lives in
             *  the name on the wire, so splitting it in the form does not change the request. */
            function readPlan() {
                const name = plan.value.trim();
                if (!plans || !version.value) return name;
                return `${name}@${Number(version.value)}`;
            }

            const editor = entitlementEditor({}, billingSubject.kind);
            const keysField = field('Entitlement values', editor.node,
                'Leave a key blank to say nothing about it. A grant that sets nothing grants nothing.');
            keysField.classList.add('hidden');
            form.append(keysField);

            const source = select(billingCatalogue.grantSources.map(name => [name, name]), 'Staff');
            form.append(field('Source', source,
                'Which band this grant sits in. Staff is a hand-issued one; the rest are for the '
                + 'systems that will issue their own.'));

            const expires = el('input');
            expires.type = 'datetime-local';
            form.append(field('Expires', expires, 'Leave empty for a permanent grant.'));

            const reason = el('textarea');
            reason.rows = 3;
            reason.required = true;
            reason.placeholder = 'e.g. Compensation for the 3 August voice outage, agreed with support.';
            form.append(field('Reason', reason,
                'Required. It is the whole value of keeping revoked grants forever.'));

            const errors = el('div');
            form.append(errors);

            function sync() {
                planField.classList.toggle('hidden', kind.value !== 'Plan');
                versionField?.classList.toggle('hidden', kind.value !== 'Plan');
                keysField.classList.toggle('hidden', kind.value !== 'Entitlements');
            }

            kind.addEventListener('change', sync);
            sync();

            const actions = el('div', 'actions');
            const cancel = el('button', 'btn', 'Cancel');
            cancel.type = 'button';
            cancel.addEventListener('click', close);

            const confirm = el('button', 'btn primary', 'Issue');
            confirm.type = 'submit';
            actions.append(cancel, confirm);
            form.append(actions);

            form.addEventListener('submit', async event => {
                event.preventDefault();
                confirm.disabled = true;

                try {
                    await call('POST', `${BILLING}/grants`, {
                        body: {
                            subjectKind: billingSubject.kind,
                            subjectId: billingSubject.id,
                            grantKind: kind.value,
                            plan: kind.value === 'Plan' ? readPlan() : null,
                            entitlements: kind.value === 'Entitlements' ? editor.read() : null,
                            expiresAt: expires.value ? new Date(expires.value).toISOString() : null,
                            reason: reason.value.trim(),
                            source: source.value,
                        },
                    });

                    close();
                    toast('ok', 'Granted');
                    render();
                } catch (error) {
                    billingRefusal(error, errors, editor);
                    confirm.disabled = false;
                }
            });

            return form;
        });
    }

    function revokeGrant(grant) {
        modal(close => {
            const form = el('form');
            form.append(el('h2', null, 'Revoke this grant'));
            form.append(el('p', 'lede',
                'The row stays, with your name and your reason on it. Anything this subject holds from '
                + 'another source - a subscription, another grant - is untouched, because a grant only '
                + 'ever added to what was already there.'));

            const reason = el('textarea');
            reason.rows = 3;
            reason.required = true;
            reason.placeholder = 'Why it is being taken back.';
            form.append(field('Reason', reason));

            const errors = el('div');
            form.append(errors);

            const actions = el('div', 'actions');
            const cancel = el('button', 'btn', 'Cancel');
            cancel.type = 'button';
            cancel.addEventListener('click', close);

            const confirm = el('button', 'btn primary danger', 'Revoke');
            confirm.type = 'submit';
            actions.append(cancel, confirm);
            form.append(actions);

            form.addEventListener('submit', async event => {
                event.preventDefault();
                confirm.disabled = true;

                try {
                    await call('POST', `${BILLING}/grants/${encodeURIComponent(grant.id)}/revoke`, {
                        body: { reason: reason.value.trim() },
                    });

                    close();
                    toast('ok', 'Revoked');
                    render();
                } catch (error) {
                    billingRefusal(error, errors);
                    confirm.disabled = false;
                }
            });

            return form;
        });
    }

    function amendGrantExpiry(grant) {
        modal(close => {
            const form = el('form');
            form.append(el('h2', null, 'Change when this grant ends'));
            form.append(el('p', 'lede',
                'Extend it, shorten it, or make it permanent. Making it permanent is the same '
                + 'operation with no date on it.'));

            const expires = el('input');
            expires.type = 'datetime-local';
            if (grant.expiresAt) expires.value = localInput(grant.expiresAt);
            const expiresField = field('Expires', expires);
            form.append(expiresField);

            const permanent = el('input');
            permanent.type = 'checkbox';
            permanent.checked = !grant.expiresAt;

            const permanentLabel = el('label', 'check');
            permanentLabel.append(permanent, el('span', null, 'Permanent - no end date'));
            const permanentField = el('div', 'field');
            permanentField.append(permanentLabel);
            form.append(permanentField);

            permanent.addEventListener('change', () => {
                expiresField.classList.toggle('hidden', permanent.checked);
            });
            expiresField.classList.toggle('hidden', permanent.checked);

            const errors = el('div');
            form.append(errors);

            const actions = el('div', 'actions');
            const cancel = el('button', 'btn', 'Cancel');
            cancel.type = 'button';
            cancel.addEventListener('click', close);

            const confirm = el('button', 'btn primary', 'Save');
            confirm.type = 'submit';
            actions.append(cancel, confirm);
            form.append(actions);

            form.addEventListener('submit', async event => {
                event.preventDefault();
                confirm.disabled = true;

                try {
                    await call('PATCH', `${BILLING}/grants/${encodeURIComponent(grant.id)}/expiry`, {
                        body: {
                            expiresAt: permanent.checked || !expires.value
                                ? null
                                : new Date(expires.value).toISOString(),
                        },
                    });

                    close();
                    toast('ok', permanent.checked ? 'Now permanent' : 'Expiry moved');
                    render();
                } catch (error) {
                    billingRefusal(error, errors);
                    confirm.disabled = false;
                }
            });

            return form;
        });
    }

    /** A UTC instant as a datetime-local control wants it, which is local wall-clock with no zone.
     *  Feeding it the ISO string directly puts the prefill an offset out, and an operator extending a
     *  grant by an hour would silently move it by their timezone as well. */
    function localInput(iso) {
        const at = new Date(iso);
        return new Date(at.getTime() - at.getTimezoneOffset() * 60000).toISOString().slice(0, 16);
    }

    /**
     * The plan catalogue, for the three dialogs that ask somebody to name a plan.
     *
     * They used to disagree about how: this one offered the list it is looking at anyway, while the
     * grant and campaign dialogs took the name as free text and let an unknown_plan refusal be the
     * thing that tells an operator they mistyped it - a round trip after the fact, with the rest of
     * the form still to fill in again. Null rather than an empty array when there is nothing to
     * choose from, because "no plans exist" and "a plan list arrived" want different dialogs.
     */
    async function planList() {
        const plans = await call('GET', `${BILLING}/plans`);
        return plans.length ? plans : null;
    }

    /** The label this console has always used for a plan: the display name with the version a new
     *  customer would get, because "pro" and "Pro, currently on version 4" are the same choice
     *  described at two very different levels of usefulness. */
    function planChoices(plans) {
        return plans.map(plan => [
            plan.name,
            `${plan.displayName || plan.name} - current version ${plan.currentVersionNumber}`,
        ]);
    }

    async function assignPlan() {
        const plans = await planList();

        if (!plans) {
            toast('danger', 'There are no plans to move anybody onto.');
            return;
        }

        modal(close => {
            const form = el('form');
            form.append(el('h2', null, 'Move to a plan version'));
            form.append(el('p', 'lede',
                'The one operation that deliberately changes what an existing subscriber resolves. '
                + 'Everything else leaves people on the version they bought.'));

            const picker = select(planChoices(plans), plans[0]?.name);

            form.append(field('Plan', picker));

            const version = el('input');
            version.type = 'number';
            version.min = '1';
            form.append(field('Version', version,
                'Leave empty for the plan\'s current version, which is what a new customer gets. '
                + 'Naming one is how somebody is moved onto - or back onto - a specific set of numbers.'));

            const reason = el('textarea');
            reason.rows = 3;
            reason.required = true;
            reason.placeholder = 'e.g. Moved onto v4 after they agreed to the new storage numbers.';
            form.append(field('Reason', reason));

            const errors = el('div');
            form.append(errors);

            const actions = el('div', 'actions');
            const cancel = el('button', 'btn', 'Cancel');
            cancel.type = 'button';
            cancel.addEventListener('click', close);

            const confirm = el('button', 'btn primary', 'Move');
            confirm.type = 'submit';
            actions.append(cancel, confirm);
            form.append(actions);

            form.addEventListener('submit', async event => {
                event.preventDefault();
                confirm.disabled = true;

                try {
                    await call('PUT', `${BILLING}/plans/subjects/${billingSubject.kind}/${encodeURIComponent(billingSubject.id)}`, {
                        body: {
                            plan: picker.value,
                            versionNumber: version.value ? Number(version.value) : null,
                            reason: reason.value.trim(),
                        },
                    });

                    close();
                    toast('ok', 'Moved');
                    render();
                } catch (error) {
                    billingRefusal(error, errors);
                    confirm.disabled = false;
                }
            });

            return form;
        });
    }

    // ── Billing: the plan editor ────────────────────────────────────────────

    async function renderPlans() {
        if (!billingCatalogue.billingDeployed) {
            return empty(
                'No billing service on this instance, so there is no plan table. Plans come from '
                + 'configuration and are read-only here.', 'key');
        }

        const plans = await call('GET', `${BILLING}/plans`, { query: { includeArchived: true } });

        if (!plans.length) {
            return empty('No plans yet. The catalogue seeds from configuration on the billing service\'s first start.', 'key');
        }

        const wrap = el('div', 'billing');

        wrap.append(el('p', 'hint plans-intro',
            'Every plan on this instance, archived ones included. A plan is a stack of versions: an '
            + 'edit writes a new version and leaves every subject on the one it was already on.'));

        const list = el('div', 'plans');

        const head = el('div', 'plans-head');
        head.append(
            el('div', null, 'Plan'),
            el('div', 'end', 'Price'),
            el('div', 'end', 'Versions'),
            el('div', 'end', 'Status'));
        list.append(head);

        plans.forEach(plan => list.append(planRow(plan)));

        wrap.append(list, listFoot(plans.length, plans.length));
        return wrap;
    }

    function planRow(plan) {
        const current = plan.versions.find(version => version.isCurrent);

        const line = el('button', 'plan-row');
        line.setAttribute('aria-selected', String(plan.name === selectedId));

        const identity = el('div', 'plan-id');
        const title = el('div', 'plan-title');
        title.append(el('span', null, plan.displayName || plan.name));
        // Only worth a line of its own when it is not already the title: most plans are named after
        // their slug and repeating "Pro pro" reads as a rendering fault.
        if (plan.displayName && plan.displayName !== plan.name) {
            title.append(el('span', 'mono faint', plan.name));
        }
        identity.append(title);
        if (plan.description) identity.append(el('div', 'plan-desc', plan.description));
        line.append(identity);

        const sold = current?.priceMinorUnits !== null && current?.priceMinorUnits !== undefined;
        line.append(el('div', sold ? 'plan-price' : 'plan-price unsold', money(current)));

        const versions = el('div', 'plan-versions');
        versions.append(el('b', null, String(plan.versions.length)));
        versions.append(el('span', null, `current v${plan.currentVersionNumber}`));
        line.append(versions);

        const status = el('div', 'plan-status');
        status.append(plan.archivedAt ? tag('Archived', 'danger') : tag('Live', 'ok'));
        if (plan.seededFromConfiguration) status.append(tag('Seeded'));
        line.append(status);

        line.addEventListener('click', () => {
            $$('.plan-row').forEach(other => other.setAttribute('aria-selected', 'false'));
            line.setAttribute('aria-selected', 'true');
            openPlan(plan.name);
        });

        return line;
    }

    /**
     * A price, in the shape the currency actually has.
     *
     * The minor unit is not always a hundredth: JPY and KRW have none at all, and a few currencies
     * have three, so dividing by 100 turned a 500 yen plan into "5.00 JPY" on the one screen an
     * operator uses to check what somebody is being charged. The divisor is read back out of the
     * formatter instead, which is the same table the symbol and its placement come from.
     */
    /** The currency codes this engine actually knows. Null on an engine without supportedValuesOf,
     *  which means the check below is skipped rather than rejecting every price. */
    const CURRENCY_CODES = (() => {
        try { return new Set(Intl.supportedValuesOf('currency')); }
        catch { return null; }
    })();

    function money(version) {
        if (!version || version.priceMinorUnits === null || version.priceMinorUnits === undefined) {
            return 'Not sold';
        }

        const currency = (version.currency || '').trim().toUpperCase();

        // A version with a price and no currency is a malformed row, not a free plan, and Intl throws
        // on an empty code. The minor units on their own are the only honest thing left to show.
        if (!currency) return `${version.priceMinorUnits} (no currency on this version)`;

        // Intl does not refuse a well-formed code it has never heard of. Handed "ZZZ" it assumes two
        // minor units and formats confidently, which is the same wrong number this function was
        // written to stop printing. Only a malformed code throws, so an unknown one has to be caught
        // against the table rather than left to the try below.
        if (CURRENCY_CODES && !CURRENCY_CODES.has(currency)) {
            return `${version.priceMinorUnits} ${currency}`;
        }

        try {
            const format = new Intl.NumberFormat(undefined, { style: 'currency', currency });
            const digits = format.resolvedOptions().maximumFractionDigits;
            return format.format(version.priceMinorUnits / 10 ** digits);
        } catch {
            // A malformed code lands here rather than taking the screen down with it.
            return `${version.priceMinorUnits} ${currency}`;
        }
    }

    async function openPlan(name) {
        selectedId = name;
        openDetail(name, loading());

        const encoded = encodeURIComponent(name);

        const [plan, blast, audit] = await Promise.all([
            call('GET', `${BILLING}/plans/${encoded}`),
            call('GET', `${BILLING}/plans/${encoded}/blast-radius`).catch(() => null),
            call('GET', `${BILLING}/plans/${encoded}/audit`).catch(() => []),
        ]);

        const pane = el('div', 'pane billing');
        const current = plan.versions.find(version => version.isCurrent);

        pane.append(block(null, kv([
            ['Name', copyable(plan.name)],
            ['Shown as', plan.displayName || '-'],
            ['Current', `Version ${plan.currentVersionNumber}`],
            ['Price', money(current)],
            ['Origin', plan.seededFromConfiguration ? 'Seeded from configuration' : 'Created here'],
            ['Created', `${stamp(plan.createdAt)} by ${plan.createdBy}`],
            ['Archived', plan.archivedAt ? `${stamp(plan.archivedAt)} by ${plan.archivedBy}` : null],
        ])));

        if (plan.description) pane.append(block('Description', el('p', 'hint', plan.description)));

        pane.append(blastBlock(blast));

        if (canEditBilling() && !plan.archivedAt) {
            const actions = el('div', 'btn-row');
            actions.append(button('Edit - writes a new version', 'pencil', () => editPlan(plan), 'primary'));
            actions.append(button('Archive the plan', 'trash', () => archivePlan(plan), 'danger'));
            pane.append(block('Actions', actions));
        }

        pane.append(versionsBlock(plan));
        pane.append(planAuditBlock(audit));

        openDetail(plan.displayName || plan.name, pane);
    }

    /**
     * "This affects 1,240 guilds", before anybody presses save.
     *
     * Split by version because the two populations behave differently: a pinned subject is not moved
     * by an edit, while one reached through a bare plan grant resolves through whatever is current
     * and therefore does change.
     */
    function blastBlock(blast) {
        const box = block('Blast radius');

        if (!blast) {
            box.append(el('p', 'hint', 'The blast radius could not be read just now.'));
            return box;
        }

        box.append(el('p', 'lede', blast.totalSubjects === 1
            ? 'One subject is on this plan.'
            : `${blast.totalSubjects.toLocaleString()} subjects are on this plan.`));

        if (blast.appliesToEveryUnassignedSubject) {
            box.append(banner('warn',
                'This is the instance default for its scope, so every subject that has never been '
                + 'assigned anything is on it as well. Billing holds no guild table and will not '
                + 'invent a number for them, so the count above is only what it can see.'));
        }

        const list = el('dl', 'kv');

        blast.byVersion.forEach(entry => {
            list.append(el('dt', null,
                `Version ${entry.versionNumber}${entry.versionNumber === blast.currentVersion ? ' (current)' : ''}`));
            list.append(el('dd', null,
                `${entry.subjects.toLocaleString()} subject${entry.subjects === 1 ? '' : 's'}`));
        });

        if (!blast.byVersion.length) {
            list.append(el('dt', null, 'Nobody'));
            list.append(el('dd', null, 'No subject is on this plan yet.'));
        }

        box.append(list);

        box.append(el('p', 'hint',
            'An edit writes a new version and leaves every one of them where they are. Only moving '
            + 'somebody between versions changes what they resolve.'));

        return box;
    }

    function versionsBlock(plan) {
        const box = block('Versions');
        const list = el('div', 'timeline');

        // Newest first: the interesting version is nearly always the one somebody just wrote.
        [...plan.versions].reverse().forEach(version => {
            const item = el('div', `event ${version.archivedAt ? 'struck' : ''}`);
            item.append(icon(version.isCurrent ? 'check-circle' : 'history'));

            const body = el('div');

            const line = el('div', 'what');
            line.append(document.createTextNode(`Version ${version.versionNumber}`));
            if (version.isCurrent) line.append(tag('Current', 'ok'));
            if (version.archivedAt) line.append(tag('Archived'));
            line.append(tag(money(version)));
            body.append(line);

            body.append(el('span', 'when', `${stamp(version.createdAt)} by ${version.createdBy}`));
            body.append(el('div', 'hint', version.reason));
            if (version.archiveReason) {
                body.append(el('div', 'hint', `Archived because: ${version.archiveReason}`));
            }

            const chips = el('div', 'chips');
            Object.entries(version.values).forEach(([key, value]) => {
                chips.append(el('span', 'chip mono', `${key} = ${value}`));
            });
            body.append(chips);

            if (canEditBilling() && !plan.archivedAt && !version.isCurrent && !version.archivedAt) {
                const actions = el('div', 'btn-row');
                actions.append(button('Make current', 'refresh', () => activateVersion(plan, version)));
                actions.append(button('Archive', 'trash', () => archiveVersion(plan, version), 'danger'));
                body.append(actions);
            }

            item.append(body);
            list.append(item);
        });

        box.append(list);
        return box;
    }

    const PLAN_ACTIONS = {
        PlanCreated: 'created the plan',
        VersionCreated: 'wrote a new version',
        VersionActivated: 'made a version current',
        VersionArchived: 'archived a version',
        PlanArchived: 'archived the plan',
        SubjectAssigned: 'moved a subject onto a version',
    };

    function planAuditBlock(entries) {
        const box = block('Change history');

        box.append(el('p', 'hint',
            'Append-only. This surface is more dangerous than the grant surface - a grant affects one '
            + 'subject, a plan edit affects every guild on the plan at once - so every change carries '
            + 'who made it and why.'));

        if (!entries.length) {
            box.append(el('p', 'hint', 'Nothing recorded.'));
            return box;
        }

        const list = el('div', 'timeline');

        entries.forEach(entry => {
            const item = el('div', 'event');
            item.append(icon('history'));

            const body = el('div');

            const line = el('div', 'what');
            line.append(document.createTextNode(
                `${entry.actor} ${PLAN_ACTIONS[entry.action] || entry.action}`));
            if (entry.versionNumber !== null && entry.versionNumber !== undefined) {
                line.append(document.createTextNode(' '), tag(`v${entry.versionNumber}`, 'info'));
            }
            body.append(line);

            const meta = [stamp(entry.occurredAt)];
            if (entry.affectedSubjects !== null && entry.affectedSubjects !== undefined) {
                meta.push(`${entry.affectedSubjects.toLocaleString()} subject${entry.affectedSubjects === 1 ? '' : 's'} announced`);
            }
            if (entry.subject) meta.push(entry.subject);
            body.append(el('span', 'when', meta.join(' · ')));

            body.append(el('div', 'hint', entry.reason));

            item.append(body);
            list.append(item);
        });

        box.append(list);
        return box;
    }

    /**
     * One control per entitlement key, prefilled from a plan version or a grant.
     *
     * The rows come from the server's catalogue rather than from a list written here, so the editor
     * cannot normally offer a key the validator does not know. It can still be refused for one: this
     * console ships in the gateway image and the billing service ships in its own, so during a rolling
     * deploy one of the two is briefly holding the newer key table. That refusal is what
     * `billingRefusal` renders, and why it lists the keys this build knows.
     */
    function entitlementEditor(values = {}, subjectKind = null) {
        const host = el('div', 'prov-edit');
        const controls = new Map();

        billingCatalogue.keys
            // A grant on a user cannot usefully carry a guild key: the resolver drops a value whose
            // scope does not match its subject, so offering one would be offering a control that
            // does nothing.
            .filter(key => !subjectKind || key.scope === 'Paired' || key.scope === subjectKind)
            .forEach(key => {
                const line = el('div', 'prov-edit-row');
                line.dataset.key = key.name;

                const label = el('div', 'prov-edit-label');
                label.append(el('span', 'mono', key.name));
                label.append(el('span', 'faint',
                    `${key.valueKind.toLowerCase()} · ${key.scope.toLowerCase()} · default ${key.default}`));
                line.append(label);

                const held = values[key.name] ?? '';
                let control;

                if (key.valueKind === 'Ladder') {
                    control = select([['', 'not set'], ...key.rungs.map(rung => [rung, rung])], held);
                } else if (key.valueKind === 'Flag') {
                    control = select([['', 'not set'], ['true', 'granted'], ['false', 'withheld']], held);
                } else {
                    control = el('input');
                    control.value = held;
                    control.spellcheck = false;
                    control.placeholder = 'a number, or unlimited';
                }

                controls.set(key.name, control);
                line.append(control);
                host.append(line);
            });

        return {
            node: host,

            read() {
                const set = {};
                controls.forEach((control, name) => {
                    const value = control.value.trim();
                    if (value) set[name] = value;
                });
                return set;
            },

            /** Points at the field a refusal named. Returns whether it was one of these. */
            mark(name) {
                let found = false;

                $$('.prov-edit-row', host).forEach(line => {
                    const hit = line.dataset.key === name;
                    line.classList.toggle('bad', hit);
                    found ||= hit;
                });

                $('.prov-edit-row.bad', host)?.scrollIntoView({ block: 'nearest' });
                return found;
            },
        };
    }

    /**
     * A billing refusal, rendered as the thing it is.
     *
     * The service answers a 400 with a machine-readable code and a sentence that names the offending
     * key and lists the ones it accepts. Showing that as "the server answered 400" throws all of it
     * away, so: the code picks a heading, the quoted key is pulled out of the sentence and its field
     * is marked, and for an unknown key the known set is drawn from the catalogue this editor was
     * built from rather than parsed back out of prose.
     */
    function billingRefusal(error, host, editor = null) {
        host.replaceChildren();

        const heading = BILLING_REFUSALS[error.code];
        host.append(banner('danger', heading ? `${heading}. ${error.message}` : error.message));

        // Quoted because the same sentence is what a curl user reads, and quoting the offending
        // value is how it names one there. Failing to find it costs the highlight and nothing else.
        const named = /'([^']+)'/.exec(error.message || '')?.[1];
        if (named && editor) editor.mark(named);

        if (error.code === 'unknown_entitlement_key') {
            host.append(el('p', 'hint', 'Keys this console knows about:'));

            const chips = el('div', 'chips');
            billingCatalogue.keys.forEach(key => chips.append(el('span', 'chip mono', key.name)));
            host.append(chips);
        }
    }

    function createPlan() {
        modal(close => {
            const form = el('form');
            form.append(el('h2', null, 'Create a plan'));
            form.append(el('p', 'lede',
                'A new plan starts at version 1 with nobody on it, so there is nothing to affect. '
                + 'Names are keys: lowercase, no spaces, and no @ - that is how a pinned version is '
                + 'addressed.'));

            const name = el('input');
            name.required = true;
            name.spellcheck = false;
            name.placeholder = 'pro';
            form.append(field('Name', name));

            const display = el('input');
            display.placeholder = 'Venta Pro';
            form.append(field('Shown as', display, 'Where capitalisation belongs.'));

            const description = el('input');
            form.append(field('Description', description));

            const editor = entitlementEditor();
            form.append(field('Entitlement values', editor.node,
                'At least one. A plan that sets nothing resolves to the catalogue defaults, which is '
                + 'what having no plan already does.'));

            const price = el('input');
            price.type = 'number';
            price.min = '0';
            form.append(field('Price in minor units', price, 'Cents, rappen, pence. Empty for a plan that is not sold.'));

            const currency = el('input');
            currency.maxLength = 3;
            currency.placeholder = 'chf';
            form.append(field('Currency', currency));

            const reason = el('textarea');
            reason.rows = 2;
            reason.required = true;
            form.append(field('Reason', reason));

            const errors = el('div');
            form.append(errors);

            const actions = el('div', 'actions');
            const cancel = el('button', 'btn', 'Cancel');
            cancel.type = 'button';
            cancel.addEventListener('click', close);

            const confirm = el('button', 'btn primary', 'Create');
            confirm.type = 'submit';
            actions.append(cancel, confirm);
            form.append(actions);

            form.addEventListener('submit', async event => {
                event.preventDefault();
                confirm.disabled = true;

                try {
                    await call('POST', `${BILLING}/plans`, {
                        body: {
                            name: name.value.trim(),
                            displayName: display.value.trim() || null,
                            description: description.value.trim() || null,
                            values: editor.read(),
                            priceMinorUnits: price.value === '' ? null : Number(price.value),
                            currency: currency.value.trim() || null,
                            reason: reason.value.trim(),
                        },
                    });

                    close();
                    toast('ok', 'Plan created');
                    render();
                } catch (error) {
                    billingRefusal(error, errors, editor);
                    confirm.disabled = false;
                }
            });

            return form;
        });
    }

    /**
     * The edit, in two deliberate steps.
     *
     * Step one writes the numbers; step two shows how many subjects the plan reaches, fetched at the
     * moment of asking rather than reused from the pane behind this dialog. "This affects 1,240
     * guilds" is the difference between a considered price change and a typo that pages somebody, and
     * a number read ten minutes ago is not that sentence.
     */
    function editPlan(plan) {
        const current = plan.versions.find(version => version.isCurrent);
        const next = plan.currentVersionNumber + 1;

        modal(close => {
            const form = el('form');
            form.append(el('h2', null, `Edit ${plan.displayName || plan.name}`));
            form.append(el('p', 'lede',
                `This writes version ${next} and makes it current. Everybody already on this plan `
                + 'stays on the version they bought until somebody moves them deliberately, which is '
                + 'what stops an edit taking away capacity people paid for.'));

            const editor = entitlementEditor(current?.values || {});
            form.append(field('Entitlement values', editor.node,
                'The whole plan, not a patch. A key left blank is one this plan says nothing about.'));

            const price = el('input');
            price.type = 'number';
            price.min = '0';
            price.value = current?.priceMinorUnits ?? '';
            form.append(field('Price in minor units', price, 'Cents, rappen, pence. Empty for a plan that is not sold.'));

            const currency = el('input');
            currency.maxLength = 3;
            currency.value = current?.currency || '';
            currency.placeholder = 'chf';
            form.append(field('Currency', currency));

            const reason = el('textarea');
            reason.rows = 3;
            reason.required = true;
            reason.placeholder = 'e.g. Raising the Pro storage quota to 500 GB after the July usage review.';
            form.append(field('Reason', reason, 'Required, and recorded in the plan\'s change history.'));

            const errors = el('div');
            form.append(errors);

            const review = el('div');
            form.append(review);

            const actions = el('div', 'actions');
            const cancel = el('button', 'btn', 'Cancel');
            cancel.type = 'button';
            cancel.addEventListener('click', close);

            const confirm = el('button', 'btn primary', 'Show what this affects');
            confirm.type = 'submit';
            actions.append(cancel, confirm);
            form.append(actions);

            let reviewed = false;

            // Any change to what is being written after the radius was shown invalidates the
            // agreement, so the second click becomes a first click again. Otherwise somebody reads
            // "12 guilds", edits the values, and saves against a number they were never shown. The
            // reason is deliberately not one of these: rewording it changes nothing about the reach.
            function unreview() {
                if (!reviewed) return;
                reviewed = false;
                review.replaceChildren();
                confirm.replaceChildren(document.createTextNode('Show what this affects'));
            }

            [editor.node, price, currency].forEach(node => {
                node.addEventListener('input', unreview);
                node.addEventListener('change', unreview);
            });

            form.addEventListener('submit', async event => {
                event.preventDefault();

                if (!reason.value.trim()) {
                    toast('danger', 'A reason is required.');
                    return;
                }

                confirm.disabled = true;

                try {
                    if (!reviewed) {
                        const blast = await call(
                            'GET', `${BILLING}/plans/${encodeURIComponent(plan.name)}/blast-radius`);

                        review.replaceChildren(blastBlock(blast));
                        reviewed = true;
                        confirm.replaceChildren(document.createTextNode(`Write version ${next}`));
                        return;
                    }

                    await call('POST', `${BILLING}/plans/${encodeURIComponent(plan.name)}/versions`, {
                        body: {
                            values: editor.read(),
                            priceMinorUnits: price.value === '' ? null : Number(price.value),
                            currency: currency.value.trim() || null,
                            reason: reason.value.trim(),
                        },
                    });

                    close();
                    toast('ok', `Version ${next} is now current`);
                    openPlan(plan.name);
                    render();
                } catch (error) {
                    billingRefusal(error, errors, editor);
                } finally {
                    confirm.disabled = false;
                }
            });

            return form;
        });
    }

    function activateVersion(plan, version) {
        planReason({
            title: `Make version ${version.versionNumber} current again`,
            lede: 'The rollback path. Every subject on this plan is told to re-resolve, including the '
                + 'ones pinned to an older version whose numbers did not move - this is an '
                + 'invalidation, so telling somebody about a change that turns out to be nothing is '
                + 'cheap and not telling them is not.',
            confirm: 'Make current',
            path: `${BILLING}/plans/${encodeURIComponent(plan.name)}/versions/${version.versionNumber}/activate`,
            done: () => { toast('ok', `Version ${version.versionNumber} is current`); openPlan(plan.name); },
        });
    }

    function archiveVersion(plan, version) {
        planReason({
            title: `Archive version ${version.versionNumber}`,
            lede: 'Takes a set of numbers out of circulation. Refused while anybody is still pinned '
                + 'to it, because the catalogue stops publishing an archived version and whoever was '
                + 'on it would resolve to nothing.',
            confirm: 'Archive',
            danger: true,
            path: `${BILLING}/plans/${encodeURIComponent(plan.name)}/versions/${version.versionNumber}/archive`,
            done: () => { toast('ok', 'Version archived'); openPlan(plan.name); },
        });
    }

    function archivePlan(plan) {
        planReason({
            title: `Archive ${plan.displayName || plan.name}`,
            lede: 'Stops the plan being sold. It is never deleted, and it is refused while anybody is '
                + 'still on it - archiving takes the numbers out of the catalogue, so anyone left '
                + 'would silently lose what they pay for.',
            confirm: 'Archive',
            danger: true,
            path: `${BILLING}/plans/${encodeURIComponent(plan.name)}/archive`,
            done: () => { toast('ok', 'Plan archived'); closeDetail(); },
        });
    }

    /** The four plan operations whose entire body is a required reason. One dialog rather than four
     *  near-identical ones. */
    function planReason({ title, lede, confirm: label, danger, path, done }) {
        modal(close => {
            const form = el('form');
            form.append(el('h2', null, title));
            form.append(el('p', 'lede', lede));

            const reason = el('textarea');
            reason.rows = 3;
            reason.required = true;
            form.append(field('Reason', reason, 'Required, and recorded in the plan\'s change history.'));

            const errors = el('div');
            form.append(errors);

            const actions = el('div', 'actions');
            const cancel = el('button', 'btn', 'Cancel');
            cancel.type = 'button';
            cancel.addEventListener('click', close);

            const confirm = el('button', `btn primary ${danger ? 'danger' : ''}`, label);
            confirm.type = 'submit';
            actions.append(cancel, confirm);
            form.append(actions);

            form.addEventListener('submit', async event => {
                event.preventDefault();
                confirm.disabled = true;

                try {
                    await call('POST', path, { body: { reason: reason.value.trim() } });
                    close();
                    done();
                    render();
                } catch (error) {
                    billingRefusal(error, errors);
                    confirm.disabled = false;
                }
            });

            return form;
        });
    }

    // ── Billing: credit ─────────────────────────────────────────────────────
    //
    // The staff side of monetization.md section 8. Everything here issues credit, corrects it, takes
    // it back or stops a campaign issuing it.
    //
    // Nothing here buys, sells, gifts, transfers, refunds or withdraws it, and that absence is the
    // design rather than a gap somebody has not got to yet. Section 8.1 is a hard rule: the moment
    // money can become a balance it stops being marketing and becomes stored value, which brings
    // prepaid-card statutes, expiry prohibitions, escheatment and deferred revenue accounting to a
    // feature whose entire purpose is giving things away. The billing service has no route for any of
    // it either, so a control here would have nothing to call - which is the point.

    const CREDIT = `${BILLING}/credit`;

    /** Points as a person reads them. The number is abstract on purpose (section 8.2) and there is no
     *  currency anywhere near it: a wallet that picks a currency breaks the moment there is a second
     *  price list, and "give me the 5 euros" is the conversation points exist to avoid. */
    function points(value) {
        const amount = Number(value ?? 0);
        return `${Math.abs(amount).toLocaleString()} ${Math.abs(amount) === 1 ? 'credit' : 'credits'}`;
    }

    async function renderCredit() {
        const wrap = el('div', 'pane billing');
        wrap.append(creditLookup());

        // The campaign list is not about whichever wallet is in the box above it, so it is fetched
        // either way. Somebody arriving to check a budget should not have to invent a user id first.
        const campaigns = await call('GET', `${CREDIT}/campaigns`).catch(error => ({ error }));

        // Kept for the issue dialog, which offers this list rather than fetching it again: it is the
        // same list the operator is looking at while deciding which campaign to name.
        if (!campaigns.error) creditCampaigns = campaigns;

        if (creditWallet.id) {
            const id = encodeURIComponent(creditWallet.id);

            const [wallet, ledger] = await Promise.all([
                call('GET', `${CREDIT}/wallets/${id}`).catch(error => ({ error })),
                call('GET', `${CREDIT}/wallets/${id}/ledger`, { query: { limit: 200 } })
                    .catch(error => ({ error })),
            ]);

            wrap.append(walletBlock(wallet));

            if (!wallet.error) {
                if (canEditBilling()) {
                    wrap.append(creditActions());
                    wrap.append(creditRepairBlock());
                }

                wrap.append(creditLedgerBlock(ledger));
            }
        } else {
            wrap.append(empty(
                'Find an account above to see a balance, the lots behind it, and the ledger.',
                'search'));
        }

        wrap.append(campaignsBlock(campaigns));
        return wrap;
    }

    /**
     * An account chooser, for the two lookups that used to ask for a raw `user_...` id.
     *
     * It runs the same search the Accounts view runs, which matches a username, an email, or an id
     * exactly - so pasting an id still works exactly as it did. What changes is that not having one
     * to hand stops being a dead end: nobody arrives at a wallet from a support ticket knowing a
     * ULID, and the answer used to be to go to another tab, find the account, copy the id and come
     * back.
     *
     * Returns the two nodes rather than one wrapper, because each caller labels its own field and the
     * label has to point at the input itself for the association to mean anything.
     */
    function userPicker(initial, { id } = {}) {
        const input = el('input');
        if (id) input.id = id;
        input.type = 'search';
        input.value = initial || '';
        input.spellcheck = false;
        input.autocomplete = 'off';
        input.placeholder = 'Username, email or user_...';

        const results = el('div', 'rows hidden');

        // What was actually chosen, as opposed to what is in the box. Cleared on every keystroke: the
        // moment the text stops describing the account that was picked, the text is the more honest
        // of the two, and it may well be an id somebody has just pasted.
        let chosen = initial || '';
        let timer = null;

        // Answers can arrive out of order, and a slow one for "dom" landing after a fast one for
        // "dominic" would leave the wrong list under the box.
        let sequence = 0;

        function show(...children) {
            results.replaceChildren(...children);
            results.classList.remove('hidden');
        }

        function hide() {
            results.replaceChildren();
            results.classList.add('hidden');
        }

        async function lookup(term) {
            const mine = ++sequence;

            let users;
            try {
                ({ users } = await call('GET', `${API}/users`, { query: { q: term, limit: 8 } }));
            } catch (error) {
                // Said out loud. A silent failure here looks identical to "no such account", which is
                // the one wrong conclusion this box can lead somebody to.
                if (mine === sequence) show(el('p', 'hint', `The account search failed: ${error.message}`));
                return;
            }

            if (mine !== sequence) return;

            if (!users.length) {
                show(el('p', 'hint',
                    'No account matches that. A full id still works even when the name does not.'));
                return;
            }

            show(...users.map(user => row({
                title: [
                    el('span', null, user.userName || '(no name)'),
                    ...(user.status === 'Banned' ? [tag('Banned', 'danger')]
                        : user.status !== 'Active' ? [tag(user.status)] : []),
                ],
                sub: user.email ? `${user.email} · ${user.id}` : user.id,
                onOpen: () => {
                    chosen = user.id;
                    input.value = user.userName || user.id;
                    hide();
                },
            })));
        }

        input.addEventListener('input', () => {
            chosen = '';
            clearTimeout(timer);

            const term = input.value.trim();
            if (!term) {
                hide();
                return;
            }

            // Debounced for the same reason the Accounts view debounces: this search reaches
            // Identity's database over the bus, and a request per keystroke is a load test on the
            // service that also answers every sign-in.
            timer = setTimeout(() => lookup(term), 300);
        });

        return {
            input,
            results,
            /** The picked id, or whatever is in the box - which is an id when one was pasted. */
            read: () => chosen || input.value.trim(),
        };
    }

    function creditLookup() {
        const box = block('Look up a wallet');

        box.append(el('p', 'hint',
            'One wallet per user, and guilds never hold one (section 8.4) - a guild balance is an '
            + 'ownership dispute waiting for the first time a guild changes hands. To give a guild '
            + 'something, issue a grant from the Subject tab instead: no wallet is involved and the '
            + 'audit trail is better.'));

        const form = el('form', 'lookup two');

        const picker = userPicker(creditWallet.id, { id: 'credit-id' });

        const cell = lookupField('User', picker.input);
        cell.append(picker.results);

        const submit = el('button', 'btn primary');
        submit.type = 'submit';
        submit.append(icon('search'), document.createTextNode(' Look up'));

        form.append(cell, submit);

        form.addEventListener('submit', event => {
            event.preventDefault();
            creditWallet.id = picker.read();
            render();
        });

        box.append(form);
        return box;
    }

    function walletBlock(wallet) {
        const box = block('Wallet');

        if (wallet.error) {
            box.append(banner('warn', `This wallet could not be read: ${wallet.error.message}`));
            return box;
        }

        // Beside the balance, every time, because section 8.1 asks for it wherever a balance is
        // displayed. The sentence comes from the server rather than being written here, so the day
        // the legal wording changes it changes in one place for all four surfaces.
        if (wallet.disclaimer) box.append(banner('info', wallet.disclaimer));

        const inLots = wallet.lots.reduce((sum, lot) => sum + lot.points, 0);

        box.append(kv([
            ['User', copyable(wallet.userId)],
            ['Balance', points(wallet.balance)],
            ['Held in open lots', points(inLots)],
            ['Wallet cap', points(wallet.capPoints)],
            ['Open lots', String(wallet.lots.length)],
        ]));

        // Two numbers computed different ways: the balance sums every entry, the lot total sums only
        // what is left in lots that have not lapsed. In a healthy ledger they are the same number, so
        // saying so when they are not is worth more than the row is.
        if (inLots !== wallet.balance) {
            box.append(banner('warn',
                'The balance and the open lots disagree, which means at least one entry is not '
                + 'attributable to a lot that is still open. Rebuilding the cached balance will not '
                + 'change either of these two numbers - both are computed from the entries - so this '
                + 'is worth reading the ledger over.'));
        }

        box.append(lotsTable(wallet.lots));
        return box;
    }

    /**
     * The lots, earliest-expiring first, which is also the order they will be spent in.
     *
     * Lots rather than one number because expiry does not work without them: a single balance cannot
     * say which part of itself was about to lapse, and "500 credits, expires 12 March" is the whole
     * of what a recipient needs in order to decide whether to spend it.
     */
    function lotsTable(lots) {
        if (!lots.length) {
            return el('p', 'hint', 'Nothing open. Every lot has been spent, reversed or has lapsed.');
        }

        const table = el('div', 'prov');

        const head = el('div', 'prov-head');
        head.append(
            el('div', null, 'Lot'),
            el('div', null, 'Left of issued'),
            el('div', null, 'Expires'));
        table.append(head);

        lots.forEach(lot => {
            const line = el('div', 'prov-row');

            const key = el('div', 'prov-key');
            key.append(el('span', 'mono', lot.lotId));
            if (lot.campaignId) key.append(el('span', 'faint', ` ${lot.campaignId}`));
            line.append(key);

            const value = el('div', 'prov-val');
            value.append(el('strong', null, points(lot.points)));
            if (lot.points !== lot.originalPoints) {
                value.append(el('span', 'faint', ` of ${points(lot.originalPoints)}`));
            }
            line.append(value);

            const when = el('div', 'prov-src');
            when.append(el('span', null, stamp(lot.expiresAt)));

            // A lot inside its last thirty days is the one an operator is being asked about, and
            // section 8.5 puts the user-facing warning at the same distance.
            const days = (new Date(lot.expiresAt) - Date.now()) / 86400000;
            if (days <= 30) when.append(tag('Lapsing soon', 'warn'));

            line.append(when);
            table.append(line);
        });

        return table;
    }

    function creditActions() {
        const box = block('Actions');
        const actions = el('div', 'btn-row');

        actions.append(button('Issue credit', 'plus', issueCredit, 'primary'));
        actions.append(button('Adjust the balance', 'pencil', adjustCredit));
        box.append(actions);

        box.append(el('p', 'hint',
            'Issuing hands over a new lot with its own expiry. An adjustment is a hand correction in '
            + 'either direction and is deliberately a separate operation, because credit taken away '
            + 'is not an issuance and must not read as one on the ledger. Both carry a reason that is '
            + 'required here, in the service and in the database.'));

        return box;
    }

    /**
     * The two operations that are not ordinary buttons.
     *
     * A void is the fraud control from section 8.6: it reverses every outstanding lot at once and is
     * not a partial judgement anybody can make. A rebuild touches no ledger row at all, but it moves
     * the number every other screen reads, and the cache is only allowed to exist because this
     * exists. Both are kept out of the row above so neither is ever the click next to the one
     * somebody meant.
     */
    function creditRepairBlock() {
        const box = block('Fraud void and repair');

        box.append(el('p', 'hint',
            'Voiding writes a reversal for every lot with something left in it and takes the balance '
            + 'to zero. Nothing is deleted - an account banned in error gets its history back rather '
            + 'than a blank page - and it does not stop future issuance on its own, which is the '
            + 'ban\'s job. Rebuilding recomputes the cached balance from the entries; it changes no '
            + 'entry and is the reason a cached balance is allowed to exist at all.'));

        const actions = el('div', 'btn-row');
        actions.append(button('Void this wallet for fraud', 'ban', voidCredit, 'danger'));
        actions.append(button('Rebuild the cached balance', 'refresh', rebuildCredit));
        box.append(actions);

        return box;
    }

    function creditLedgerBlock(ledger) {
        const box = block('Ledger');

        if (ledger.error) {
            box.append(banner('warn', `The ledger could not be read: ${ledger.error.message}`));
            return box;
        }

        box.append(el('p', 'hint',
            'Append-only and complete: reversed, spent and expired lines all stay. This is the screen '
            + 'section 8.8 puts first, and the reason the ledger is append-only at all - "where did my '
            + 'credits go" has to have an answer that does not depend on anybody remembering.'));

        if (!ledger.entries.length) {
            box.append(el('p', 'hint', 'Nothing has ever moved in this wallet.'));
            return box;
        }

        const list = el('div', 'timeline');
        ledger.entries.forEach(entry => list.append(creditEntry(entry)));
        box.append(list);

        return box;
    }

    const CREDIT_ICONS = {
        Issue: 'plus', Spend: 'key', Expiry: 'clock', Reversal: 'times-circle', Adjustment: 'pencil',
    };

    function creditEntry(entry) {
        const item = el('div', `event ${entry.kind === 'Reversal' ? 'struck' : ''}`);
        item.append(icon(CREDIT_ICONS[entry.kind] || 'history'));

        const body = el('div');

        // The sentence is rendered by the service rather than here, so the four surfaces that show a
        // ledger cannot drift the moment one of them forgets a kind.
        const line = el('div', 'what');
        line.append(document.createTextNode(entry.summary));
        line.append(tag(entry.kind, entry.amount < 0 ? 'danger' : 'ok'));
        body.append(line);

        const meta = [stamp(entry.createdAt)];
        if (entry.createdBy) meta.push(`by ${entry.createdBy}`);
        if (entry.campaignId) meta.push(entry.campaignId);
        if (entry.grantId) meta.push(`grant ${entry.grantId}`);
        body.append(el('span', 'when', meta.join(' · ')));

        if (entry.reason) body.append(el('div', 'hint', entry.reason));
        body.append(el('div', 'hint mono', entry.id));

        // Only an issuance. Reversing a spend would hand the credit back for a grant that has already
        // been given, which is a refund - the service refuses it, and offering the button anyway
        // would be teaching an operator to expect something that cannot happen.
        if (canEditBilling() && entry.amount > 0) {
            const actions = el('div', 'btn-row');
            actions.append(button('Take this back', 'times-circle',
                () => reverseCreditEntry(entry), 'danger'));
            body.append(actions);
        }

        item.append(body);
        return item;
    }

    // ── Billing: credit writes ──────────────────────────────────────────────

    /**
     * A fresh idempotency key per dialog, not per submit.
     *
     * The service makes the key unique in the database, so a retry that carries the same one replays
     * the original answer instead of issuing twice. Minting it when the dialog opens is what makes
     * the retry after a dropped connection safe; minting it per click would make the second click a
     * second issuance, which is the one mistake this whole mechanism exists to prevent.
     */
    function newIdempotencyKey() {
        // `randomUUID` needs a secure context, which every real deployment of this console is. The
        // fallback is not for correctness - the key only has to be unique across the handful an
        // operator can produce - it is so that opening the dialog over plain http fails at the
        // request rather than as a TypeError with a dead Issue button behind it.
        const unique = crypto.randomUUID?.()
            ?? `${Date.now()}-${Math.random().toString(36).slice(2)}`;

        return `console:${unique}`;
    }

    /** A campaign as one line in a dropdown. The state is said in the label because a paused campaign
     *  is still offered: the service is the one that refuses it, and an option that quietly vanishes
     *  is how somebody concludes the campaign was deleted. */
    function campaignChoice(campaign) {
        const state = campaign.pausedAt ? ' (paused)' : '';
        return campaign.description
            ? `${campaign.code}${state} - ${campaign.description}`
            : `${campaign.code}${state}`;
    }

    function issueCredit() {
        const idempotencyKey = newIdempotencyKey();

        modal(close => {
            const form = el('form');
            form.append(el('h2', null, 'Issue credit'));
            form.append(el('p', 'lede',
                'This creates a new lot with its own expiry date. Credit is issued, never sold: there '
                + 'is no path that turns money into a balance, and none that turns a balance back '
                + 'into money.'));

            const amount = el('input');
            amount.type = 'number';
            amount.min = '1';
            amount.required = true;
            form.append(field('Credits', amount,
                'Abstract points, not a currency. The cash price of anything they buy is shown beside '
                + 'the point price wherever they are spent.'));

            // Picked from the list rendered directly below this dialog rather than retyped. A
            // mistyped code was accepted quietly and issued outside every budget, which is the one
            // outcome the budget exists to make impossible - and it left no trace saying so.
            const campaign = creditCampaigns?.length
                ? select([['', 'No campaign - counted against no budget at all']]
                    .concat(creditCampaigns.map(entry => [entry.code, campaignChoice(entry)])), '')
                : el('input');

            if (!creditCampaigns?.length) {
                campaign.spellcheck = false;
                campaign.placeholder = 'incident-2026-08';
            }

            form.append(field('Campaign', campaign,
                'Optional, and "no campaign" is a real answer. Naming one counts this issuance '
                + 'against that campaign\'s budget and its per-recipient cap, which is what makes '
                + 'either of them mean anything.'));

            const lifetime = el('input');
            lifetime.type = 'number';
            lifetime.min = '1';
            form.append(field('Lifetime in days', lifetime,
                'Empty uses the campaign\'s lifetime, or the instance default of twelve months. '
                + 'Expiry is an abuse control as much as a cost one - it bounds what a farmed '
                + 'stockpile is ever worth.'));

            const reason = el('textarea');
            reason.rows = 3;
            reason.required = true;
            reason.placeholder = 'e.g. Goodwill for the 3 August voice outage, agreed with support.';
            form.append(field('Reason', reason,
                'Required. The ledger is append-only, so this is the only chance to say why.'));

            const errors = el('div');
            form.append(errors);

            const actions = el('div', 'actions');
            const cancel = el('button', 'btn', 'Cancel');
            cancel.type = 'button';
            cancel.addEventListener('click', close);

            const confirm = el('button', 'btn primary', 'Issue');
            confirm.type = 'submit';
            actions.append(cancel, confirm);
            form.append(actions);

            form.addEventListener('submit', async event => {
                event.preventDefault();
                if (!requireReason(reason, errors)) return;

                confirm.disabled = true;

                try {
                    await call('POST', `${CREDIT}/wallets/${encodeURIComponent(creditWallet.id)}/issue`, {
                        body: {
                            amount: Number(amount.value),
                            reason: reason.value.trim(),
                            idempotencyKey,
                            campaign: campaign.value.trim() || null,
                            lifetimeDays: lifetime.value ? Number(lifetime.value) : null,
                        },
                    });

                    close();
                    toast('ok', 'Issued');
                    render();
                } catch (error) {
                    billingRefusal(error, errors);
                    confirm.disabled = false;
                }
            });

            return form;
        });
    }

    function adjustCredit() {
        const idempotencyKey = newIdempotencyKey();

        modal(close => {
            const form = el('form');
            form.append(el('h2', null, 'Adjust this balance'));
            form.append(el('p', 'lede',
                'A hand correction in either direction. A positive adjustment creates a lot like any '
                + 'issuance, so credit that arrived by hand lapses on the same terms as credit that '
                + 'arrived by campaign - a balance with a piece in it that never expires is the thing '
                + 'lots exist to prevent.'));

            const amount = el('input');
            amount.type = 'number';
            amount.required = true;
            form.append(field('Credits', amount,
                'Negative to take credit away. A negative adjustment consumes the earliest-expiring '
                + 'lots first, exactly as a spend would.'));

            const reason = el('textarea');
            reason.rows = 3;
            reason.required = true;
            reason.placeholder = 'Why this number is being changed by hand.';
            form.append(field('Reason', reason,
                'Required. It is a number somebody typed, and next year it has to be explainable.'));

            const errors = el('div');
            form.append(errors);

            const actions = el('div', 'actions');
            const cancel = el('button', 'btn', 'Cancel');
            cancel.type = 'button';
            cancel.addEventListener('click', close);

            const confirm = el('button', 'btn primary', 'Adjust');
            confirm.type = 'submit';
            actions.append(cancel, confirm);
            form.append(actions);

            form.addEventListener('submit', async event => {
                event.preventDefault();
                if (!requireReason(reason, errors)) return;

                confirm.disabled = true;

                try {
                    await call('POST', `${CREDIT}/wallets/${encodeURIComponent(creditWallet.id)}/adjust`, {
                        body: {
                            amount: Number(amount.value),
                            reason: reason.value.trim(),
                            idempotencyKey,
                        },
                    });

                    close();
                    toast('ok', 'Adjusted');
                    render();
                } catch (error) {
                    billingRefusal(error, errors);
                    confirm.disabled = false;
                }
            });

            return form;
        });
    }

    function reverseCreditEntry(entry) {
        creditReason({
            title: 'Take this issuance back',
            lede: 'Writes a reversal against the lot this issuance created, bounded by what is left '
                + 'in it. Only an issuance can be taken back: reversing a spend would return the '
                + 'credit while the grant it bought stays live, which is a refund with the serial '
                + 'numbers filed off. To end something a spend bought, revoke the grant instead.',
            confirm: 'Take it back',
            danger: true,
            path: `${CREDIT}/entries/${encodeURIComponent(entry.id)}/reverse`,
            done: () => toast('ok', 'Taken back'),
        });
    }

    function voidCredit() {
        creditReason({
            title: 'Void this wallet for fraud',
            lede: 'Every lot with something left in it is reversed and the balance goes to zero. This '
                + 'is the fraud control, not a correction - there is no partial version of it, '
                + 'because a partial fraud void is a judgement the ledger has no way to record. The '
                + 'entries all stay.',
            confirm: 'Void the wallet',
            danger: true,
            // Typed out in full, because this is the one operation on the section that empties an
            // account in a single click and the id is the only thing distinguishing the right
            // account from the one whose ticket is open in the next tab.
            confirmWith: creditWallet.id,
            path: `${CREDIT}/wallets/${encodeURIComponent(creditWallet.id)}/void`,
            done: () => toast('ok', 'Wallet voided'),
        });
    }

    function rebuildCredit() {
        modal(close => {
            const form = el('form');
            form.append(el('h2', null, 'Rebuild the cached balance'));
            form.append(el('p', 'lede',
                'Recomputes the cached balance by summing the entries. No entry is written, read or '
                + 'changed, and if the cache was already right this does nothing visible. It exists '
                + 'because a cached balance is only defensible while something can rebuild it from '
                + 'the ledger it claims to summarise.'));

            const errors = el('div');
            form.append(errors);

            const actions = el('div', 'actions');
            const cancel = el('button', 'btn', 'Cancel');
            cancel.type = 'button';
            cancel.addEventListener('click', close);

            const confirm = el('button', 'btn primary', 'Rebuild');
            confirm.type = 'submit';
            actions.append(cancel, confirm);
            form.append(actions);

            form.addEventListener('submit', async event => {
                event.preventDefault();
                confirm.disabled = true;

                try {
                    const result = await call(
                        'POST', `${CREDIT}/wallets/${encodeURIComponent(creditWallet.id)}/rebuild`);

                    close();
                    toast('ok', `Cached balance is now ${points(result.balance)}`);
                    render();
                } catch (error) {
                    billingRefusal(error, errors);
                    confirm.disabled = false;
                }
            });

            return form;
        });
    }

    /**
     * The credit operations whose whole body is a required reason.
     *
     * The same shape as `planReason`, kept separate for `confirmWith`: a fraud void empties an
     * account in one click and nothing else on this section does, so it is the one place worth making
     * somebody type the id they are acting on.
     */
    function creditReason({ title, lede, confirm: label, danger, confirmWith, path, done }) {
        modal(close => {
            const form = el('form');
            form.append(el('h2', null, title));
            form.append(el('p', 'lede', lede));

            const reason = el('textarea');
            reason.rows = 3;
            reason.required = true;
            form.append(field('Reason', reason, 'Required, and kept on the ledger row forever.'));

            let typed = null;

            if (confirmWith) {
                typed = el('input', 'mono');
                typed.spellcheck = false;
                typed.autocomplete = 'off';
                form.append(field('Type the user id to confirm', typed, confirmWith));
            }

            const errors = el('div');
            form.append(errors);

            const actions = el('div', 'actions');
            const cancel = el('button', 'btn', 'Cancel');
            cancel.type = 'button';
            cancel.addEventListener('click', close);

            const confirm = el('button', `btn primary ${danger ? 'danger' : ''}`, label);
            confirm.type = 'submit';
            actions.append(cancel, confirm);
            form.append(actions);

            form.addEventListener('submit', async event => {
                event.preventDefault();
                if (!requireReason(reason, errors)) return;

                if (typed && typed.value.trim() !== confirmWith) {
                    errors.replaceChildren(banner('danger',
                        'That is not the id of the wallet on screen. Nothing has been sent.'));
                    return;
                }

                confirm.disabled = true;

                try {
                    await call('POST', path, { body: { reason: reason.value.trim() } });
                    close();
                    done();
                    render();
                } catch (error) {
                    billingRefusal(error, errors);
                    confirm.disabled = false;
                }
            });

            return form;
        });
    }

    /**
     * The blank reason, refused here.
     *
     * The gateway refuses it too and so does the service, and neither of those is redundant. This one
     * exists because the round trip that would establish it is one nobody needs to wait for, and
     * because rendering it through `billingRefusal` with the service's own code means a reason
     * refused locally and a reason refused remotely read identically.
     */
    function requireReason(control, errors) {
        if (control.value.trim()) return true;

        billingRefusal(
            {
                code: 'reason_required',
                message: 'The ledger is append-only, so this is the only chance to record why the '
                    + 'balance moved.',
            },
            errors);

        control.focus();
        return false;
    }

    // ── Billing: campaigns ──────────────────────────────────────────────────

    function campaignsBlock(campaigns) {
        const box = block('Campaigns');

        if (campaigns.error) {
            box.append(banner('warn', `The campaigns could not be read: ${campaigns.error.message}`));
            return box;
        }

        box.append(el('p', 'hint',
            'Every campaign credit can be issued under, and how much of its budget is gone. A budget '
            + 'is required and there is no unlimited value for it, because a campaign that issues '
            + 'with no human in the loop turns one loop bug into a five-figure liability overnight. '
            + 'Campaigns are never deleted: pausing sets a field, and the lots a campaign issued '
            + 'outlive it by up to a year and still need it to explain them.'));

        if (!campaigns.length) {
            box.append(el('p', 'hint', 'No campaigns yet.'));
            return box;
        }

        // Automated first. That is the population section 8.6 is actually about: a campaign a person
        // clicks through has a person in front of every issuance, and one that does not is the one
        // where a loop bug spends the budget overnight. Within each half, closest to its cap first,
        // so the one about to matter is at the top rather than in alphabetical order.
        const sorted = [...campaigns].sort((a, b) =>
            Number(b.automated) - Number(a.automated) || consumed(b) - consumed(a));

        const list = el('div', 'timeline');
        sorted.forEach(campaign => list.append(campaignEntry(campaign)));
        box.append(list);

        return box;
    }

    const consumed = campaign =>
        campaign.totalBudgetPoints > 0 ? campaign.issuedPoints / campaign.totalBudgetPoints : 1;

    function campaignEntry(campaign) {
        const item = el('div', `event ${campaign.pausedAt ? 'struck' : ''}`);
        item.append(icon(campaign.automated ? 'refresh' : 'user'));

        const body = el('div');

        const line = el('div', 'what');
        line.append(el('span', 'mono', campaign.code));

        // Said in words rather than left to the icon. It is the one property of a campaign that
        // decides how much the budget below it is carrying.
        line.append(campaign.automated
            ? tag('Automated - no human in the loop', 'warn')
            : tag('Hand-issued'));

        if (campaign.pausedAt) line.append(tag('Paused', 'danger'));
        else if (!isCampaignOpen(campaign)) line.append(tag('Outside its dates'));
        if (campaign.alertedAt) line.append(tag('Alert fired', 'danger'));
        if (!campaign.remainingPoints) line.append(tag('Budget spent', 'danger'));

        body.append(line);
        body.append(el('div', 'hint', campaign.description));
        body.append(budgetBar(campaign));

        const meta = [`opened ${stamp(campaign.createdAt)} by ${campaign.createdBy}`];
        if (campaign.startsAt) meta.push(`from ${stamp(campaign.startsAt)}`);
        if (campaign.endsAt) meta.push(`until ${stamp(campaign.endsAt)}`);
        if (campaign.maxPerUserPoints) meta.push(`${points(campaign.maxPerUserPoints)} per account`);
        if (campaign.pausedAt) meta.push(`paused ${stamp(campaign.pausedAt)} by ${campaign.pausedBy}`);
        body.append(el('span', 'when', meta.join(' · ')));

        if (canEditBilling()) {
            const actions = el('div', 'btn-row');
            if (!campaign.pausedAt) {
                actions.append(button('Pause', 'times-circle', () => pauseCampaign(campaign), 'danger'));
            }
            actions.append(button('Change the budget', 'pencil', () => setCampaignBudget(campaign)));
            body.append(actions);
        }

        item.append(body);
        return item;
    }

    function isCampaignOpen(campaign) {
        const now = Date.now();
        return (!campaign.startsAt || new Date(campaign.startsAt) <= now)
            && (!campaign.endsAt || new Date(campaign.endsAt) > now);
    }

    /** Issued against budget, as a bar and as the two numbers. The bar is what makes a list of
     *  campaigns scannable; the numbers are what somebody puts in a ticket. */
    function budgetBar(campaign) {
        const box = el('div', 'budget');

        const share = Math.min(1, Math.max(0, consumed(campaign)));
        const alertAt = (campaign.alertThresholdPercent || 100) / 100;

        const track = el('div', 'budget-track');
        const fill = el('div', `budget-fill ${share >= 1 ? 'danger' : share >= alertAt ? 'warn' : ''}`);
        fill.style.width = `${(share * 100).toFixed(1)}%`;
        track.append(fill);
        box.append(track);

        box.append(el('div', 'budget-text',
            `${points(campaign.issuedPoints)} issued of ${points(campaign.totalBudgetPoints)}`
            + ` · ${Math.round(share * 100)}% · ${points(campaign.remainingPoints)} left`
            + ` · alert at ${campaign.alertThresholdPercent}%`));

        return box;
    }

    function createCreditCampaign() {
        modal(close => {
            const form = el('form');
            form.append(el('h2', null, 'Open a campaign'));
            form.append(el('p', 'lede',
                'A campaign is a budgeted reason to hand out credit. The budget is the control: it is '
                + 'required, it cannot be unlimited, and it is checked inside the same transaction as '
                + 'the issuance it counts.'));

            const code = el('input');
            code.required = true;
            code.spellcheck = false;
            code.placeholder = 'incident-2026-08';
            form.append(field('Code', code,
                'What an issuance names and what the budget is counted against.'));

            const description = el('input');
            description.required = true;
            form.append(field('Description', description,
                'Somebody will read this a year from now while working out where a lot came from.'));

            const budget = el('input');
            budget.type = 'number';
            budget.min = '1';
            budget.required = true;
            form.append(field('Total budget in credits', budget, 'A positive number. There is no unlimited.'));

            const perUser = el('input');
            perUser.type = 'number';
            perUser.min = '1';
            form.append(field('Cap per account', perUser, 'Optional. Empty for no per-recipient cap.'));

            const perIssue = el('input');
            perIssue.type = 'number';
            perIssue.min = '1';
            form.append(field('Default issuance', perIssue,
                'Optional. The size of one issuance when the caller does not name an amount.'));

            const lifetime = el('input');
            lifetime.type = 'number';
            lifetime.min = '1';
            form.append(field('Lot lifetime in days', lifetime,
                'Optional. Empty falls back to the instance default of twelve months.'));

            const threshold = el('input');
            threshold.type = 'number';
            threshold.min = '1';
            threshold.max = '100';
            threshold.value = '80';
            form.append(field('Alert at', threshold,
                'Percent of the budget. The alert has to fire before the cap, not at it - a campaign '
                + 'that has already stopped is one somebody finds out about from a support ticket.'));

            const starts = el('input');
            starts.type = 'datetime-local';
            form.append(field('Starts', starts, 'Optional.'));

            const ends = el('input');
            ends.type = 'datetime-local';
            form.append(field('Ends', ends, 'Optional.'));

            const automated = el('input');
            automated.type = 'checkbox';
            const automatedLabel = el('label', 'check');
            automatedLabel.append(automated,
                el('span', null, 'Issues automatically, with no human in the loop'));
            const automatedField = el('div', 'field');
            automatedField.append(automatedLabel);
            automatedField.append(el('p', 'hint',
                'Referrals, incident compensation, anything a job issues. It changes nothing about '
                + 'enforcement - the budget is mandatory either way - and sorts the campaign to the '
                + 'top of this list, because these are the ones a loop bug empties overnight.'));
            form.append(automatedField);

            const errors = el('div');
            form.append(errors);

            const actions = el('div', 'actions');
            const cancel = el('button', 'btn', 'Cancel');
            cancel.type = 'button';
            cancel.addEventListener('click', close);

            const confirm = el('button', 'btn primary', 'Open');
            confirm.type = 'submit';
            actions.append(cancel, confirm);
            form.append(actions);

            form.addEventListener('submit', async event => {
                event.preventDefault();
                confirm.disabled = true;

                try {
                    await call('POST', `${CREDIT}/campaigns`, {
                        body: {
                            code: code.value.trim(),
                            description: description.value.trim(),
                            totalBudgetPoints: Number(budget.value),
                            maxPerUserPoints: perUser.value ? Number(perUser.value) : null,
                            defaultIssuePoints: perIssue.value ? Number(perIssue.value) : null,
                            lotLifetimeDays: lifetime.value ? Number(lifetime.value) : null,
                            alertThresholdPercent: Number(threshold.value),
                            automated: automated.checked,
                            startsAt: starts.value ? new Date(starts.value).toISOString() : null,
                            endsAt: ends.value ? new Date(ends.value).toISOString() : null,
                        },
                    });

                    close();
                    toast('ok', 'Campaign opened');
                    render();
                } catch (error) {
                    billingRefusal(error, errors);
                    confirm.disabled = false;
                }
            });

            return form;
        });
    }

    function pauseCampaign(campaign) {
        modal(close => {
            const form = el('form');
            form.append(el('h2', null, `Pause ${campaign.code}`));
            form.append(el('p', 'lede',
                'Stops this campaign issuing. Nothing is deleted and nothing already issued is '
                + 'touched: the lots it created keep their expiry dates and keep pointing at it for '
                + 'their explanation.'));

            const errors = el('div');
            form.append(errors);

            const actions = el('div', 'actions');
            const cancel = el('button', 'btn', 'Cancel');
            cancel.type = 'button';
            cancel.addEventListener('click', close);

            const confirm = el('button', 'btn primary danger', 'Pause');
            confirm.type = 'submit';
            actions.append(cancel, confirm);
            form.append(actions);

            form.addEventListener('submit', async event => {
                event.preventDefault();
                confirm.disabled = true;

                try {
                    await call('POST', `${CREDIT}/campaigns/${encodeURIComponent(campaign.code)}/pause`);
                    close();
                    toast('ok', 'Campaign paused');
                    render();
                } catch (error) {
                    billingRefusal(error, errors);
                    confirm.disabled = false;
                }
            });

            return form;
        });
    }

    function setCampaignBudget(campaign) {
        modal(close => {
            const form = el('form');
            form.append(el('h2', null, `Change the budget for ${campaign.code}`));
            form.append(el('p', 'lede',
                `${points(campaign.issuedPoints)} have already been issued against this campaign. A `
                + 'budget below that is refused, because it would describe a campaign as having spent '
                + 'more than it was ever allowed to.'));

            const budget = el('input');
            budget.type = 'number';
            budget.min = '1';
            budget.required = true;
            budget.value = campaign.totalBudgetPoints;
            form.append(field('Total budget in credits', budget));

            const errors = el('div');
            form.append(errors);

            const actions = el('div', 'actions');
            const cancel = el('button', 'btn', 'Cancel');
            cancel.type = 'button';
            cancel.addEventListener('click', close);

            const confirm = el('button', 'btn primary', 'Save');
            confirm.type = 'submit';
            actions.append(cancel, confirm);
            form.append(actions);

            form.addEventListener('submit', async event => {
                event.preventDefault();
                confirm.disabled = true;

                try {
                    await call('PATCH', `${CREDIT}/campaigns/${encodeURIComponent(campaign.code)}/budget`, {
                        body: { totalBudgetPoints: Number(budget.value) },
                    });

                    close();
                    toast('ok', 'Budget changed');
                    render();
                } catch (error) {
                    billingRefusal(error, errors);
                    confirm.disabled = false;
                }
            });

            return form;
        });
    }

    // ── Billing: promotions ─────────────────────────────────────────────────
    //
    // Section 7's trial campaigns. The credit tab above hands out points; this one hands out a plan
    // for a while. Two tabs over two tables rather than one campaign screen with a unit picker,
    // because the only things the two share are budget, alert and pause semantics.
    //
    // Monitoring is the important half here, not creating. A campaign that has already stopped
    // issuing is one somebody finds out about from a support ticket, so the list is ordered by how
    // close each campaign is to its cap rather than alphabetically, and the alert state is a tag
    // rather than a number somebody has to go looking for.
    //
    // Three things this screen deliberately does not do:
    //
    // It never renders a hash, and offers no field that takes one. The identity marks behind a
    // redemption - device, phone, card - are salted hashes, and a console that displayed one would be
    // the confirmation oracle the salt exists to prevent: paste a hash computed elsewhere, see
    // whether it matches. What a support agent needs is whether the redemption happened and when,
    // which is what the service sends and all of what it sends.
    //
    // It never calls a phone number verified. The signal is a number somebody typed, checked for
    // E.164 shape and nothing else, and this console is exactly where a reader would pick up the
    // wrong idea and later trust it as a guarantee the platform cannot make.
    //
    // It offers no delete, no clear and no reset. A redemption is permanent and expiry sets a field,
    // because a removed redemption is a re-granted trial - the one failure the whole wave exists to
    // prevent. Pausing is how a campaign is stopped, and the billing service has no route for
    // anything else, so a control here would have nothing to call.

    const PROMOTIONS = `${BILLING}/promotions`;

    /** Redemptions as a person reads them. The unit is the whole difference between this budget and
     *  the credit one above: a promotion hands out one thing per person, so what an operator caps is
     *  how many people got it rather than how much was spent. */
    function redemptionCount(value) {
        const count = Number(value ?? 0);
        return `${count.toLocaleString()} ${count === 1 ? 'redemption' : 'redemptions'}`;
    }

    /**
     * A rule code as a person reads it.
     *
     * Derived from the code rather than looked up in a table written here, deliberately: the
     * catalogue is served so that a rule added in the billing service appears in this editor without
     * a deploy of the gateway image, and a display-name map maintained on this side would quietly
     * take that back the first time somebody added a rule and forgot it. The service's own refusal
     * sentence is shown underneath, which is where the meaning actually lives.
     */
    function ruleLabel(code) {
        return String(code || '').replace(/_/g, ' ').replace(/^./, first => first.toUpperCase());
    }

    /** The strongest rule in the catalogue, and the one whose name undersells it: what it buys is not
     *  a card but the fingerprint taken from it. Named here because the campaign list flags it on its
     *  own, ahead of anybody opening the campaign to read the rules. */
    const CARD_RULE = 'payment_card';

    /**
     * What an operator needs told at the moment of choosing, beyond the refusal the service serves.
     *
     * Two entries and both of them are corrections to something the code name implies. The phone one
     * is the important one: there is no verified phone on this platform, the column that looks like
     * one is documented dead, and this console is the surface most likely to teach somebody
     * otherwise. The card one corrects the other way round - the name sounds like a payment, and the
     * reason to require it has nothing to do with charging anybody.
     */
    const RULE_NOTES = {
        phone_number_on_file: 'A number somebody typed, checked for E.164 shape and nothing else. '
            + 'This is not a verified phone and there is no such thing here - the verification design '
            + 'was dropped because the client talks to the SMS provider directly, so the message is '
            + 'sent and paid for before this platform is ever called. It is still worth requiring, '
            + 'because a farmer who reuses a number is caught by the identity mark that goes with it, '
            + 'but what it says is "this account is not that one" and never "this is a real person".',
        payment_card: 'The applicant has to have a card attached before the trial starts. The reason '
            + 'to ask for one is not the card, it is the fingerprint taken from it: the same physical '
            + 'card carries the same fingerprint on every account it is ever attached to, so this is '
            + 'the rule that stops one card taking a second trial on a fresh account. The card is '
            + 'read from the payment provider before the decision is made, and no evidence of one is '
            + 'a refusal rather than a pass - nobody having looked is not the same as there being no '
            + 'card. The client asks the same question before a card is attached, to decide whether '
            + 'to offer the trial at all, and is refused on this rule until one is; that is the '
            + 'expected answer there rather than a fault to report.',
    };

    async function renderPromotions() {
        const wrap = el('div', 'pane billing');

        // The campaign list is not about whichever subject is in the box above it, so it is fetched
        // either way: somebody arriving to check a budget should not have to invent an id first.
        const [campaigns] = await Promise.all([
            call('GET', `${PROMOTIONS}/campaigns`).catch(error => ({ error })),
            promotionRules
                ? Promise.resolve()
                : call('GET', `${PROMOTIONS}/rules`)
                    .then(rules => { promotionRules = rules; })
                    .catch(() => { promotionRules = []; }),
        ]);

        const byId = new Map(Array.isArray(campaigns)
            ? campaigns.map(campaign => [campaign.id, campaign])
            : []);

        wrap.append(promotionLookup());

        if (promotionSubject.id) {
            const rows = await call('GET',
                `${PROMOTIONS}/subjects/${promotionSubject.kind}`
                + `/${encodeURIComponent(promotionSubject.id)}/redemptions`)
                .catch(error => ({ error }));

            wrap.append(subjectRedemptionsBlock(rows, byId));
        }

        wrap.append(promotionCampaignsBlock(campaigns));
        return wrap;
    }

    function promotionLookup() {
        const box = block('Why was this account refused a trial');

        box.append(el('p', 'hint',
            'Every campaign a subject has ever redeemed, expired and released rows included. A trial '
            + 'is recorded twice - against the owner account and against the guild it was applied to '
            + '- so an account that looks clean can still be refused because of the guild, and the '
            + 'answer is only complete once both have been looked at.'));

        const form = el('form', 'lookup');

        const kind = select([['User', 'User'], ['Guild', 'Guild']], promotionSubject.kind);
        kind.id = 'promo-kind';

        // Two controls behind one label, because only half of this lookup can be searched: accounts
        // are searchable by name or email, and there is no equivalent for a guild here, so a guild is
        // still identified by the id somebody arrived with.
        const picker = userPicker(promotionSubject.kind === 'User' ? promotionSubject.id : '',
            { id: 'promo-id' });

        const userCell = lookupField('Account', picker.input);
        userCell.append(picker.results);

        const guild = el('input', 'mono');
        guild.id = 'promo-guild-id';
        guild.value = promotionSubject.kind === 'Guild' ? promotionSubject.id : '';
        guild.spellcheck = false;
        guild.autocomplete = 'off';
        guild.placeholder = 'guild_...';

        const guildCell = lookupField('Guild id', guild);

        function sync() {
            const asGuild = kind.value === 'Guild';
            userCell.classList.toggle('hidden', asGuild);
            guildCell.classList.toggle('hidden', !asGuild);
        }

        kind.addEventListener('change', sync);
        sync();

        const submit = el('button', 'btn primary');
        submit.type = 'submit';
        submit.append(icon('search'), document.createTextNode(' Look up'));

        form.append(lookupField('Subject', kind), userCell, guildCell, submit);

        form.addEventListener('submit', event => {
            event.preventDefault();
            promotionSubject.kind = kind.value;
            promotionSubject.id = kind.value === 'Guild' ? guild.value.trim() : picker.read();
            render();
        });

        box.append(form);
        return box;
    }

    function subjectRedemptionsBlock(rows, byId) {
        const box = block('What this subject has redeemed');

        if (rows.error) {
            box.append(banner('warn', `The redemptions could not be read: ${rows.error.message}`));
            return box;
        }

        if (!rows.length) {
            box.append(el('p', 'hint',
                'Nothing, ever. If this subject was refused a trial it was not because of a '
                + 'redemption of its own - check the other half of the pair, and check the '
                + 'campaign\'s eligibility rules and its budget below.'));
            return box;
        }

        const list = el('div', 'timeline');
        rows.forEach(row => list.append(redemptionEntry(row, byId?.get(row.campaignId))));
        box.append(list);

        // Said once, under the list, rather than repeated on every row that has run out. It is the
        // single most common misreading of this screen: a trial that ended looks like it should have
        // freed the account up, and it deliberately has not.
        box.append(el('p', 'hint',
            'Every row here still refuses a second redemption, including the ones that have run out '
            + 'and the ones released to another guild. That is the control, not a leftover: a '
            + 'redemption cleaned up when the trial ended would be a monthly reminder rather than '
            + 'anti-abuse. There is deliberately no way to remove one.'));

        return box;
    }

    const isGuildRow = row => String(row.subjectKind || '').toLowerCase() === 'guild';

    /** Whether the trial this row records is over. The expiry stamp is written by a sweep rather than
     *  by the clock, so a row can be past its end date without carrying one - and reading only the
     *  stamp would show a finished trial as live for as long as the sweep is behind. */
    const hasEnded = row =>
        Boolean(row.expiredAt) || Boolean(row.endsAt && new Date(row.endsAt) <= Date.now());

    /**
     * One redemption as a sentence.
     *
     * This is the screen somebody opens after a person has been told they already had a trial, so
     * what it owes them is a sentence they can paste into a reply. A row of four timestamps and an
     * opaque campaign id is the same information and answers nobody.
     */
    function redemptionEntry(row, campaign) {
        const ended = hasEnded(row);
        const item = el('div', `event ${ended || row.releasedAt ? 'struck' : ''}`);

        item.append(icon(row.releasedAt ? 'external-link' : ended ? 'clock' : 'check-circle'));

        const body = el('div');

        const line = el('div', 'what');
        line.append(el('span', 'mono', campaign?.code || row.campaignId));
        line.append(tag(isGuildRow(row) ? 'Guild' : 'Account'));
        if (row.releasedAt) line.append(tag('Moved to another guild'));
        else if (ended) line.append(tag('Ran out'));
        else line.append(tag('Live', 'ok'));
        body.append(line);

        body.append(el('div', 'hint', redemptionSentence(row, campaign)));

        const meta = [`redeemed ${stamp(row.redeemedAt)}`];
        if (row.endsAt) meta.push(`ends ${stamp(row.endsAt)}`);
        if (row.expiredAt) meta.push(`expired ${stamp(row.expiredAt)}`);
        if (row.releasedAt) meta.push(`released ${stamp(row.releasedAt)}`);
        meta.push(`owner ${row.ownerUserId}`);
        body.append(el('span', 'when', meta.join(' · ')));

        body.append(el('div', 'hint mono', `${row.subjectId} · ${row.id}`));

        item.append(body);
        return item;
    }

    function redemptionSentence(row, campaign) {
        const subject = isGuildRow(row) ? 'This guild' : 'This account';
        const what = campaign ? `'${campaign.code}'` : 'this campaign';
        const when = stamp(row.redeemedAt);

        if (row.releasedAt) {
            return `${subject} redeemed ${what} on ${when}, and the trial was moved elsewhere on `
                + `${stamp(row.releasedAt)}. The row stays and still refuses a second one; moving a `
                + 'trial does not reset its clock and does not free the guild it left.';
        }

        if (hasEnded(row)) {
            const sentence = `${subject} redeemed ${what} on ${when} and it ran out on `
                + `${stamp(row.expiredAt || row.endsAt)}. It still refuses a second one - the record `
                + 'of a trial is permanent, and this is the row a re-trial attempt is refused by.';

            // The stamp is informational and every eligibility query ignores it, so a row past its end
            // date without one is not a fault and must not be reported as a trial that is still live.
            return row.expiredAt
                ? sentence
                : `${sentence} The expiry stamp has not been written yet, which changes nothing.`;
        }

        return row.endsAt
            ? `${subject} redeemed ${what} on ${when} and it runs until ${stamp(row.endsAt)}.`
            : `${subject} redeemed ${what} on ${when}.`;
    }

    function promotionCampaignsBlock(campaigns) {
        const box = block('Campaigns');

        if (campaigns.error) {
            box.append(banner('warn', `The campaigns could not be read: ${campaigns.error.message}`));
            return box;
        }

        box.append(el('p', 'hint',
            'A campaign is a budgeted reason to confer a plan for a while. The budget counts '
            + 'redemptions rather than points - a promotion hands out one thing per person - it is '
            + 'required, and there is deliberately no unlimited value: an uncapped campaign is one '
            + 'loop away from handing every plan on the instance out for free. Campaigns are never '
            + 'deleted; pausing sets a field, and the redemptions a campaign produced outlive it and '
            + 'still need it to explain them.'));

        if (!campaigns.length) {
            box.append(el('p', 'hint', 'No campaigns yet.'));
            return box;
        }

        // Closest to its cap first, with the paused ones after the live ones. The campaign about to
        // stop refusing nobody is the one worth seeing, and burying it under whatever was opened most
        // recently is how it becomes a support ticket instead.
        const sorted = [...campaigns].sort((a, b) =>
            Number(Boolean(a.pausedAt)) - Number(Boolean(b.pausedAt))
            || promotionConsumed(b) - promotionConsumed(a));

        const list = el('div', 'timeline');
        sorted.forEach(campaign => list.append(promotionCampaignEntry(campaign)));
        box.append(list);

        return box;
    }

    const promotionConsumed = campaign => campaign.totalBudgetRedemptions > 0
        ? campaign.issuedRedemptions / campaign.totalBudgetRedemptions
        : 1;

    const isGuildCampaign = campaign => String(campaign.subjectKind || '').toLowerCase() === 'guild';

    function promotionCampaignEntry(campaign) {
        const item = el('div', `event ${campaign.pausedAt ? 'struck' : ''}`);
        item.append(icon(isGuildCampaign(campaign) ? 'users' : 'user'));

        const body = el('div');

        const line = el('div', 'what');
        line.append(el('span', 'mono', campaign.code));

        if (campaign.pausedAt) line.append(tag('Paused', 'danger'));
        else if (!isCampaignOpen(campaign)) line.append(tag('Outside its dates'));
        if (campaign.alertedAt) line.append(tag('Alert fired', 'danger'));
        if (!campaign.remainingRedemptions) line.append(tag('Budget spent', 'danger'));

        // Said on the list rather than only in the editor, because it is the one rule that narrows a
        // campaign to people who have already got as far as attaching a card - which is the answer to
        // a redemption count that looks lower than the reach of the offer.
        if ((campaign.requiredRules || []).includes(CARD_RULE)) {
            line.append(tag('Needs a card on file'));
        }

        body.append(line);
        body.append(el('div', 'hint', campaign.description));
        body.append(promotionBudgetBar(campaign));

        const meta = [
            `${campaign.plan} for ${campaign.trialDays} days`,
            `on a ${isGuildCampaign(campaign) ? 'guild' : 'user'}`,
            `${campaign.maxPerSubject} per subject`,
            `opened ${stamp(campaign.createdAt)} by ${campaign.createdBy}`,
        ];
        if (campaign.startsAt) meta.push(`from ${stamp(campaign.startsAt)}`);
        if (campaign.endsAt) meta.push(`until ${stamp(campaign.endsAt)}`);
        if (campaign.pausedAt) meta.push(`paused ${stamp(campaign.pausedAt)} by ${campaign.pausedBy}`);
        body.append(el('span', 'when', meta.join(' · ')));

        const actions = el('div', 'btn-row');
        actions.append(button('Open', 'search', () => openPromotionCampaign(campaign)));

        if (canEditBilling()) {
            if (campaign.pausedAt) {
                actions.append(button('Resume', 'check-circle', () => resumePromotionCampaign(campaign)));
            } else {
                actions.append(button('Pause', 'times-circle',
                    () => pausePromotionCampaign(campaign), 'danger'));
            }

            actions.append(button('Change the budget', 'pencil', () => setPromotionBudget(campaign)));
        }

        body.append(actions);

        item.append(body);
        return item;
    }

    /** Redeemed against budget, as a bar and as the two numbers. Same shape as the credit bar and
     *  deliberately so - the unit differs, the question does not. */
    function promotionBudgetBar(campaign) {
        const box = el('div', 'budget');

        const share = Math.min(1, Math.max(0, promotionConsumed(campaign)));
        const alertAt = (campaign.alertThresholdPercent || 100) / 100;

        const track = el('div', 'budget-track');
        const fill = el('div', `budget-fill ${share >= 1 ? 'danger' : share >= alertAt ? 'warn' : ''}`);
        fill.style.width = `${(share * 100).toFixed(1)}%`;
        track.append(fill);
        box.append(track);

        box.append(el('div', 'budget-text',
            `${campaign.issuedRedemptions.toLocaleString()} of `
            + `${redemptionCount(campaign.totalBudgetRedemptions)} taken`
            + ` · ${Math.round(share * 100)}% · ${campaign.remainingRedemptions.toLocaleString()} left`
            + ` · alert at ${campaign.alertThresholdPercent}%`));

        return box;
    }

    async function openPromotionCampaign(campaign) {
        selectedId = campaign.code;
        openDetail(campaign.code, loading());

        const encoded = encodeURIComponent(campaign.code);

        const [fresh, rows] = await Promise.all([
            call('GET', `${PROMOTIONS}/campaigns/${encoded}`).catch(() => campaign),
            call('GET', `${PROMOTIONS}/campaigns/${encoded}/redemptions`, { query: { limit: 200 } })
                .catch(error => ({ error })),
        ]);

        const pane = el('div', 'pane billing');

        pane.append(block(null, kv([
            ['Code', copyable(fresh.code)],
            ['Id', copyable(fresh.id)],
            ['Confers', `${fresh.plan} for ${fresh.trialDays} days`],
            ['Applied to', isGuildCampaign(fresh) ? 'A guild' : 'The account itself'],
            ['Per subject', `${fresh.maxPerSubject} ${fresh.maxPerSubject === 1 ? 'time' : 'times'}`],
            ['Window', campaignWindow(fresh)],
            ['Budget', `${fresh.issuedRedemptions.toLocaleString()} of `
                + `${redemptionCount(fresh.totalBudgetRedemptions)} taken`],
            ['Alert', fresh.alertedAt
                ? `Fired ${stamp(fresh.alertedAt)}, at ${fresh.alertThresholdPercent}% of the budget`
                : `At ${fresh.alertThresholdPercent}% of the budget, not yet fired`],
            ['Paused', fresh.pausedAt ? `${stamp(fresh.pausedAt)} by ${fresh.pausedBy}` : null],
            ['Opened', `${stamp(fresh.createdAt)} by ${fresh.createdBy}`],
        ])));

        pane.append(block('Description', el('p', 'hint', fresh.description)));
        pane.append(campaignRulesBlock(fresh));
        pane.append(promotionBudgetBlock(fresh));
        pane.append(campaignRedemptionsBlock(rows, fresh));

        openDetail(fresh.code, pane);
    }

    function campaignWindow(campaign) {
        if (!campaign.startsAt && !campaign.endsAt) return 'Open-ended';
        if (!campaign.startsAt) return `Until ${stamp(campaign.endsAt)}`;
        if (!campaign.endsAt) return `From ${stamp(campaign.startsAt)}`;

        return `${stamp(campaign.startsAt)} to ${stamp(campaign.endsAt)}`;
    }

    /**
     * What the campaign asks of an account, in the rule catalogue's own words.
     *
     * The mask arrives as rule codes rather than as a number, and the sentence beside each one is the
     * sentence the applicant is shown when it refuses them - which is the useful thing to read here,
     * because a campaign is a decision about what to refuse people for.
     */
    function campaignRulesBlock(campaign) {
        const box = block('What it asks of an account');

        const required = campaign.requiredRules || [];

        if (!required.length) {
            box.append(el('p', 'hint',
                'Nothing at all. Any account inside the window can redeem this, subject only to the '
                + 'budget and to never having redeemed it before.'));
            return box;
        }

        const served = new Map((promotionRules || []).map(rule => [rule.code, rule.description]));

        const list = el('div', 'checks');

        required.forEach(code => {
            const row = el('div', 'check-row');
            row.append(el('strong', null, ruleLabel(code)));

            const sentence = served.get(code);
            if (sentence) row.append(el('p', 'hint', sentence));

            if (code === 'minimum_account_age') {
                row.append(el('p', 'hint',
                    `Older than ${campaign.minimumAccountAgeDays} days. An account whose creation date `
                    + 'cannot be read fails this rather than passing it.'));
            }

            if (RULE_NOTES[code]) row.append(el('p', 'hint', RULE_NOTES[code]));

            list.append(row);
        });

        box.append(list);

        // Stated once, here, because it is the question this block invites and the answer is not
        // obvious from the rules above it.
        box.append(el('p', 'hint',
            'A device, phone number or card that has already redeemed this campaign refuses a second '
            + 'attempt as well, whichever account it turns up on. Those are matched as salted hashes '
            + 'and are deliberately not shown or searchable anywhere in this console - a screen that '
            + 'confirmed a hash would be the oracle the salt exists to prevent.'));

        return box;
    }

    function promotionBudgetBlock(campaign) {
        const box = block('Budget');
        box.append(promotionBudgetBar(campaign));

        box.append(el('p', 'hint',
            'The alert has to fire before the cap, not at it. A campaign that has already stopped is '
            + 'one somebody finds out about from a support ticket, and raising a budget after the '
            + 'fact does not go back and grant the people it refused in the meantime.'));

        return box;
    }

    function campaignRedemptionsBlock(rows, campaign) {
        const box = block('Redemptions');

        if (rows.error) {
            box.append(banner('warn', `The redemptions could not be read: ${rows.error.message}`));
            return box;
        }

        if (!rows.length) {
            box.append(el('p', 'hint', 'Nobody has redeemed this campaign yet.'));
            return box;
        }

        // Two rows per trial, and worth saying so before somebody reports the list as duplicated: a
        // guild trial is recorded against the owner account and against the guild, because either
        // alone is farmable - the account row alone says nothing about a guild sold on, and the guild
        // row alone means a new guild every month.
        box.append(el('p', 'hint',
            'A trial applied to a guild appears twice, once against the owner account and once '
            + 'against the guild. Both rows are permanent and both refuse a second redemption.'));

        const list = el('div', 'timeline');
        rows.forEach(row => list.append(redemptionEntry(row, campaign)));
        box.append(list);

        return box;
    }

    // ── Billing: promotion writes ───────────────────────────────────────────

    /**
     * The rule picker, built from the catalogue the service serves.
     *
     * Rules are offered by code with the service's own refusal sentence under each, and two of them
     * carry a note of their own - see `RULE_NOTES`. The notes are shown whether or not the rule is
     * ticked, because what they correct is what the code name led somebody to expect before they
     * ticked it.
     */
    function rulePicker(selected = []) {
        const host = el('div', 'checks');
        const controls = new Map();

        (promotionRules || []).forEach(rule => {
            const row = el('div', 'check-row');

            const box = el('input');
            box.type = 'checkbox';
            box.checked = selected.includes(rule.code);

            const label = el('label', 'check');
            label.append(box, el('span', null, ruleLabel(rule.code)));
            row.append(label);

            row.append(el('p', 'hint', rule.description));
            if (RULE_NOTES[rule.code]) row.append(el('p', 'hint', RULE_NOTES[rule.code]));

            controls.set(rule.code, box);
            host.append(row);
        });

        if (!controls.size) {
            host.append(el('p', 'hint',
                'The rule catalogue could not be read, so this campaign can only be opened with no '
                + 'eligibility rules on it. That is a campaign anybody can redeem - close this and '
                + 'try again once the billing service is answering.'));
        }

        return {
            node: host,
            read: () => [...controls].filter(([, box]) => box.checked).map(([code]) => code),
        };
    }

    async function createPromotionCampaign() {
        // A campaign editor opened before the tab finished loading waits for the catalogue rather
        // than offering an empty rule list, which would read as "this campaign asks for nothing".
        if (!promotionRules) {
            promotionRules = await call('GET', `${PROMOTIONS}/rules`).catch(() => []);
        }

        // The plan a campaign confers is picked from the same list the Plans tab shows. A campaign
        // naming a plan that does not exist is worse than a mistyped grant: it is only found out
        // about when the first person tries to redeem it and is refused for a reason that reads like
        // their fault.
        const plans = await planList().catch(() => null);

        modal(close => {
            const form = el('form');
            form.append(el('h2', null, 'Open a promotion'));
            form.append(el('p', 'lede',
                'A budgeted reason to confer a plan for a while: a trial, a partner offer, a '
                + 'win-back. The budget counts people rather than points, it is required, and it is '
                + 'checked inside the same transaction as the redemption it counts.'));

            const code = el('input');
            code.required = true;
            code.spellcheck = false;
            code.placeholder = 'pro-trial-2026';
            form.append(field('Code', code,
                'What a redemption names and what the budget is counted against. It has to be '
                + 'unique - two campaigns sharing one would make "how many people got this" '
                + 'unanswerable.'));

            const description = el('input');
            description.required = true;
            form.append(field('Description', description,
                'Somebody will read this a year from now while working out why an account was '
                + 'refused a trial.'));

            const plan = plans ? select(planChoices(plans), plans[0]?.name) : el('input');
            plan.required = true;

            if (!plans) {
                plan.spellcheck = false;
                plan.placeholder = 'pro';
            }

            form.append(field('Plan it confers', plan, plans
                ? 'A campaign that grants nothing is a row nobody can explain later, so this is '
                    + 'required. It confers whatever version is current when somebody redeems.'
                : 'The plan list could not be read just now, so this is the name from the Plans tab '
                    + 'as typed. A campaign that grants nothing is a row nobody can explain later, '
                    + 'so this is required.'));

            const trialDays = el('input');
            trialDays.type = 'number';
            trialDays.min = '1';
            trialDays.required = true;
            trialDays.value = '30';
            form.append(field('Trial length in days', trialDays,
                'Handed to the payment provider as the trial period, so it is the clock itself '
                + 'rather than a copy of one. There is no open-ended value: an unbounded trial is a '
                + 'pricing decision made by accident.'));

            const kind = select([['Guild', 'A guild'], ['User', 'The account itself']], 'Guild');
            form.append(field('Applied to', kind,
                'A guild plan is conferred on one guild of the owner\'s choosing. A user plan '
                + 'applies to the account and needs no guild.'));

            const budget = el('input');
            budget.type = 'number';
            budget.min = '1';
            budget.required = true;
            form.append(field('Total budget in redemptions', budget,
                'How many people may ever take this. A positive number, and there is no unlimited.'));

            const perSubject = el('input');
            perSubject.type = 'number';
            perSubject.min = '1';
            perSubject.value = '1';
            form.append(field('Per subject', perSubject,
                'One, for anything that is a trial. Higher only makes sense for something like a '
                + 'referral reward that is meant to be repeatable.'));

            const threshold = el('input');
            threshold.type = 'number';
            threshold.min = '1';
            threshold.max = '100';
            threshold.value = '80';
            form.append(field('Alert at', threshold,
                'Percent of the budget. The alert has to fire before the cap, not at it - a campaign '
                + 'that has already stopped is one somebody finds out about from a support ticket.'));

            const starts = el('input');
            starts.type = 'datetime-local';
            form.append(field('Starts', starts, 'Optional.'));

            const ends = el('input');
            ends.type = 'datetime-local';
            form.append(field('Ends', ends, 'Optional.'));

            const minimumAge = el('input');
            minimumAge.type = 'number';
            minimumAge.min = '0';
            minimumAge.value = '0';
            form.append(field('Minimum account age in days', minimumAge,
                'Read only by the minimum account age rule below, and meaningless without it.'));

            const rules = rulePicker();
            form.append(field('Eligibility rules', rules.node,
                'What the campaign asks of an account before it may redeem. Every one of these is '
                + 'checked at the moment of redemption against signals asked for fresh, never '
                + 'cached: a stale answer here fails in the direction of granting a trial that '
                + 'should have been refused.'));

            const errors = el('div');
            form.append(errors);

            const actions = el('div', 'actions');
            const cancel = el('button', 'btn', 'Cancel');
            cancel.type = 'button';
            cancel.addEventListener('click', close);

            const confirm = el('button', 'btn primary', 'Open');
            confirm.type = 'submit';
            actions.append(cancel, confirm);
            form.append(actions);

            form.addEventListener('submit', async event => {
                event.preventDefault();
                confirm.disabled = true;

                try {
                    await call('POST', `${PROMOTIONS}/campaigns`, {
                        body: {
                            code: code.value.trim(),
                            description: description.value.trim(),
                            plan: plan.value.trim(),
                            trialDays: Number(trialDays.value),
                            totalBudgetRedemptions: Number(budget.value),
                            subjectKind: kind.value,
                            requiredRules: rules.read(),
                            minimumAccountAgeDays: Number(minimumAge.value || 0),
                            maxPerSubject: Number(perSubject.value || 1),
                            alertThresholdPercent: Number(threshold.value),
                            startsAt: starts.value ? new Date(starts.value).toISOString() : null,
                            endsAt: ends.value ? new Date(ends.value).toISOString() : null,
                        },
                    });

                    close();
                    toast('ok', 'Campaign opened');
                    render();
                } catch (error) {
                    billingRefusal(error, errors);
                    confirm.disabled = false;
                }
            });

            return form;
        });
    }

    function pausePromotionCampaign(campaign) {
        modal(close => {
            const form = el('form');
            form.append(el('h2', null, `Pause ${campaign.code}`));
            form.append(el('p', 'lede',
                'Stops this campaign being redeemed. Nothing is deleted and nothing already redeemed '
                + 'is touched: the trials it conferred run to their own end dates, and the rows it '
                + 'produced keep refusing a second redemption. This is how a campaign is stopped - '
                + 'there is no delete, because a removed redemption is a re-granted trial.'));

            const errors = el('div');
            form.append(errors);

            const actions = el('div', 'actions');
            const cancel = el('button', 'btn', 'Cancel');
            cancel.type = 'button';
            cancel.addEventListener('click', close);

            const confirm = el('button', 'btn primary danger', 'Pause');
            confirm.type = 'submit';
            actions.append(cancel, confirm);
            form.append(actions);

            form.addEventListener('submit', async event => {
                event.preventDefault();
                confirm.disabled = true;

                try {
                    await call('POST',
                        `${PROMOTIONS}/campaigns/${encodeURIComponent(campaign.code)}/pause`);
                    close();
                    closeDetail();
                    toast('ok', 'Campaign paused');
                    render();
                } catch (error) {
                    billingRefusal(error, errors);
                    confirm.disabled = false;
                }
            });

            return form;
        });
    }

    function resumePromotionCampaign(campaign) {
        modal(close => {
            const form = el('form');
            form.append(el('h2', null, `Resume ${campaign.code}`));
            form.append(el('p', 'lede',
                'Lets this campaign be redeemed again, under whatever budget it has left. If it was '
                + 'paused because of a farming pattern, the rules it asks for are what stops that '
                + 'pattern recurring - resuming without changing them resumes the pattern too.'));

            const errors = el('div');
            form.append(errors);

            const actions = el('div', 'actions');
            const cancel = el('button', 'btn', 'Cancel');
            cancel.type = 'button';
            cancel.addEventListener('click', close);

            const confirm = el('button', 'btn primary', 'Resume');
            confirm.type = 'submit';
            actions.append(cancel, confirm);
            form.append(actions);

            form.addEventListener('submit', async event => {
                event.preventDefault();
                confirm.disabled = true;

                try {
                    await call('POST',
                        `${PROMOTIONS}/campaigns/${encodeURIComponent(campaign.code)}/resume`);
                    close();
                    closeDetail();
                    toast('ok', 'Campaign resumed');
                    render();
                } catch (error) {
                    billingRefusal(error, errors);
                    confirm.disabled = false;
                }
            });

            return form;
        });
    }

    function setPromotionBudget(campaign) {
        modal(close => {
            const form = el('form');
            form.append(el('h2', null, `Change the budget for ${campaign.code}`));
            form.append(el('p', 'lede',
                `${redemptionCount(campaign.issuedRedemptions)} have already been taken against this `
                + 'campaign. A budget below that is refused, because it would claim fewer people got '
                + 'it than did. Lowering it to exactly what has been redeemed is how a campaign is '
                + 'stopped while keeping its history readable; pausing is the other way.'));

            const budget = el('input');
            budget.type = 'number';
            budget.min = '1';
            budget.required = true;
            budget.value = campaign.totalBudgetRedemptions;
            form.append(field('Total budget in redemptions', budget,
                'Raising it puts the campaign back below its alert threshold, so the next crossing '
                + 'is announced again rather than passing in silence.'));

            const errors = el('div');
            form.append(errors);

            const actions = el('div', 'actions');
            const cancel = el('button', 'btn', 'Cancel');
            cancel.type = 'button';
            cancel.addEventListener('click', close);

            const confirm = el('button', 'btn primary', 'Save');
            confirm.type = 'submit';
            actions.append(cancel, confirm);
            form.append(actions);

            form.addEventListener('submit', async event => {
                event.preventDefault();
                confirm.disabled = true;

                try {
                    await call('PATCH',
                        `${PROMOTIONS}/campaigns/${encodeURIComponent(campaign.code)}/budget`, {
                            body: { totalBudgetRedemptions: Number(budget.value) },
                        });

                    close();
                    closeDetail();
                    toast('ok', 'Budget changed');
                    render();
                } catch (error) {
                    billingRefusal(error, errors);
                    confirm.disabled = false;
                }
            });

            return form;
        });
    }

    // ── View: public wiki ───────────────────────────────────────────────────

    /*
     * Publishing a wiki puts prose on this instance's own domain, under its brand, reachable by
     * anyone. That is a takedown obligation rather than a visibility setting, and the guild's own
     * publish switch is not reachable from here: it is gated on a permission inside the guild that
     * no instance moderator holds, and should not be given one.
     *
     * So this view is the operator's half. It shows exactly what an address serves anonymously -
     * read here rather than by visiting the live page, because deciding from memory after it is
     * gone is the failure mode - and takes it off.
     */
    const wikiState = { address: '', result: null };

    /** Pulls a wiki address out of whatever a report or a ticket said. Assumes the default `wiki.`
     *  label; an instance that moved it with WIKI_DOMAIN loses the shortcut, not the takedown. */
    const WIKI_ADDRESS_PATTERN = /(?:https?:\/\/)?[a-z0-9-]+\.wiki\.[a-z0-9.-]+(?:\/[a-z0-9-]+)?/i;

    function wikiAddressIn(text) {
        return text ? (text.match(WIKI_ADDRESS_PATTERN)?.[0] ?? null) : null;
    }

    /** Opens the wiki view on an address, from wherever it was found. */
    function openWikiAddress(address) {
        wikiState.address = address;
        wikiState.result = null;
        go('wiki');
        lookUpWiki(address);
    }

    async function lookUpWiki(address) {
        wikiState.address = address;

        try {
            wikiState.result = await call('GET', `${API}/wiki`, { query: { address } });
        } catch (error) {
            wikiState.result = null;
            toast('danger', error.message);
        }

        render();
    }

    views.wiki = {
        title: 'Public wiki',

        async render() {
            const host = el('div');

            const form = el('form');
            const input = el('input');
            input.type = 'search';
            input.value = wikiState.address;
            input.placeholder = 'wiki address, or the slug';
            input.required = true;

            form.append(field('Published address', input, null, { required: true }));

            const look = el('button', 'btn primary sm', 'Look up');
            look.type = 'submit';
            form.append(look);

            form.addEventListener('submit', event => {
                event.preventDefault();
                lookUpWiki(input.value.trim());
            });

            host.append(block('What is published', form,
                el('p', 'hint',
                   'Any form works: the address from a report, the wiki\'s own hostname, or the slug '
                   + 'on its own.')));

            if (!wikiState.result) {
                host.append(empty('Nothing looked up yet.', 'search'));
                return host;
            }

            const found = wikiState.result;
            const link = el('a', 'btn ghost sm', found.url);
            link.href = found.url;
            link.target = '_blank';
            link.rel = 'noreferrer';

            host.append(block(found.pageSlug ? found.title : found.guildName, kv([
                ['Address', link],
                ['Published by', found.guildName],
                ['Category', found.category || null],
                ['Last edited', found.updatedAt ? stamp(found.updatedAt) : null],
                ['Pages published', found.pages ? String(found.pages.length) : null],
            ])));

            if (found.content) {
                // Shown as the author wrote it, not as the site renders it: what is being judged is
                // the text, and re-rendering it inside the console is how a console grows the same
                // markup-injection problem the public host was built to avoid.
                host.append(block('The page', el('div', 'evidence', found.content)));
            }

            if (found.pages?.length) {
                const list = el('div', 'rows');
                found.pages.forEach(page => list.append(row({
                    title: [el('span', null, page.title)],
                    sub: page.url,
                    onOpen: () => openWikiAddress(`${found.slug}/${page.slug}`),
                })));

                host.append(block('Published pages', list));
            }

            const actions = el('div', 'btn-row');

            if (found.pageSlug) {
                actions.append(button('Take this page down', 'times-circle',
                    () => takeWikiDown(`${found.slug}/${found.pageSlug}`, found.title), 'danger'));
            }

            actions.append(button('Take the whole wiki down', 'ban',
                () => takeWikiDown(found.slug, found.guildName), found.pageSlug ? '' : 'danger'));

            host.append(block('Actions', actions,
                el('p', 'hint',
                   'This only stops the instance serving it. Anything that already fetched the page '
                   + 'still has its copy, and so does any cache in front of us.')));

            return host;
        },
    };

    function takeWikiDown(address, label) {
        modal(close => {
            const form = el('form');
            form.append(el('h2', null, 'Take this off the public host'));
            form.append(el('p', 'lede',
                `${label} stops being served anonymously. The guild keeps the page and can publish `
                + 'it again, so a takedown that needs to stick needs an action against the account too.'));

            const note = el('textarea');
            note.required = true;
            note.rows = 4;
            note.placeholder = 'e.g. Page is a phishing landing page dressed as a login screen.';
            form.append(field('Why', note, null, { required: true }));

            const actions = el('div', 'actions');
            const cancel = el('button', 'btn', 'Cancel');
            cancel.type = 'button';
            cancel.addEventListener('click', close);

            const confirm = el('button', 'btn danger', 'Take it down');
            confirm.type = 'submit';
            actions.append(cancel, confirm);
            form.append(actions);

            form.addEventListener('submit', async event => {
                event.preventDefault();
                confirm.disabled = true;

                try {
                    const result = await call('POST', `${API}/wiki/unpublish`, {
                        body: { address, reason: note.value },
                    });

                    close();
                    toast('ok', result.unpublished
                        ? `Off the public host within ${result.cacheSeconds} seconds`
                        : 'It was already off the public host');

                    // Not looked up again: the address is meant to answer nothing now, and a
                    // "nothing is published there" error toast on a successful takedown reads as a
                    // failure.
                    wikiState.result = null;
                    render();
                } catch (error) {
                    toast('danger', error.message);
                    confirm.disabled = false;
                }
            });

            return form;
        });
    }

    // ── View: audit ─────────────────────────────────────────────────────────

    /** Entries that reach further than the subject named on them. A grant affects one subject; a
     *  plan version affects every guild on the plan at once. */
    const WIDE_AUDIT_ACTIONS = new Set([
        'billing.plan-created',
        'billing.plan-edited',
        'billing.plan-version-activated',
        'billing.plan-version-archived',
        'billing.plan-archived',
    ]);

    views.audit = {
        title: 'Audit log',
        tools() {
            const search = el('input');
            search.type = 'search';
            search.placeholder = 'Actor or subject id';
            search.value = filters.auditQuery || '';

            let timer;
            search.addEventListener('input', () => {
                clearTimeout(timer);
                timer = setTimeout(() => { filters.auditQuery = search.value; render(); }, 300);
            });

            return [search];
        },

        async render() {
            const term = filters.auditQuery?.trim();

            // One box, two fields. A moderator looking something up has an id in their hand and
            // does not know or care whether it was the actor or the subject.
            const [byActor, bySubject] = await Promise.all([
                call('GET', `${API}/audit`, { query: { actorId: term, limit: 100 } }),
                term
                    ? call('GET', `${API}/audit`, { query: { subject: term, limit: 100 } })
                    : Promise.resolve({ entries: [], total: 0 }),
            ]);

            const seen = new Set();
            const entries = [...byActor.entries, ...bySubject.entries]
                .filter(entry => !seen.has(entry.id) && seen.add(entry.id))
                .sort((a, b) => new Date(b.createdAt) - new Date(a.createdAt));

            if (!entries.length) return empty('Nothing recorded yet.', 'history');

            const list = el('div', 'rows');

            entries.forEach(entry => {
                // "Sam banned Alex", not "action.issued  user_3Gc... -> user_3Gl...". The ids are
                // still in the detail pane and still the record; a list is for reading, and two
                // 31-character ULIDs side by side are not something anybody reads.
                const line = el('div', 'rw-title');
                line.append(el('strong', null, who(entry.actorUserId, entry.actorName)));
                line.append(document.createTextNode(` ${auditVerb(entry.action, namesAnAccount(entry))}`));

                // Only when the subject is an account. It is just as often a report, an action or a
                // ticket, and "banned rprt_01KZ8M..." is worse than saying nothing.
                if (namesAnAccount(entry)) {
                    line.append(document.createTextNode(' '));
                    line.append(el('strong', null, who(entry.subjectId, entry.subjectName)));
                }

                if (entry.action === 'user.role-changed') line.append(tag('Privilege', 'danger'));
                // A plan change is the other entry here that is not about one account: it moves what
                // every guild on the plan gets at once.
                else if (WIDE_AUDIT_ACTIONS.has(entry.action)) line.append(tag('Whole plan', 'danger'));

                list.append(row({
                    // The entries that reach past one account get the stripe: who can act on everyone
                    // else, and what everyone on a plan gets.
                    mark: entry.action === 'user.role-changed' || WIDE_AUDIT_ACTIONS.has(entry.action)
                        ? 'critical'
                        : '',
                    title: [line],
                    sub: auditSubtitle(entry),
                    side: [agoNode(entry.createdAt)],
                    onOpen: () => openDetail('Audit entry', auditPane(entry)),
                }));
            });

            const wrap = el('div');
            wrap.append(list, listFoot(entries.length, entries.length));
            return wrap;
        },
    };

    /**
     * The dotted action name as something a person reads.
     *
     * Written as verbs that complete "<actor> ... <subject>", so the row is a sentence rather than
     * a column of enum values. Anything unmapped falls back to the raw name with its punctuation
     * softened - an audit log must still render an action nobody has written a label for yet, since
     * the alternative is a new entry type silently displaying as blank.
     *
     * A verb is either one phrase, or a [phrase, preposition] pair whose preposition is only spoken
     * when a name is actually going to follow it. The pair is what the list above needs: it prints
     * the subject only when the subject is an account, so a report id leaves the sentence ending on
     * whatever the verb ended on. "dominic lifted a restriction on" and then nothing was the result,
     * and the fix belongs here rather than in the row, which is right not to read a ULID out loud.
     */
    const AUDIT_VERBS = {
        'action.issued': ['issued an action', 'against'],
        'action.revoked': ['lifted a restriction', 'on'],
        'report.assigned': ['picked up a report', 'about'],
        'report.resolved': ['closed a report', 'about'],
        'report.reopened': ['reopened a report', 'about'],
        'appeal.claimed': ['took an appeal', 'from'],
        'appeal.decided': ['decided an appeal', 'from'],
        'ticket.replied': ['replied to a ticket', 'from'],
        'ticket.updated': ['updated a ticket', 'from'],
        'user.role-changed': ['changed a staff role', 'for'],
        'user.viewed': ['looked at an account', 'belonging to'],
        'wiki.unpublished': 'took a wiki off the public host',
        'wiki.page-unpublished': 'took a wiki page off the public host',
        'status.incident-created': 'published a status incident',
        'status.incident-updated': ['posted a status update', 'on'],
        'status.incident-edited': 'edited a status incident',
        'status.incident-confirmed': 'took over a status incident',
        'status.incident-retracted': 'retracted a status incident',
        'status.component-created': 'added a status component',
        'status.component-updated': 'edited a status component',
        'billing.grant-issued': ['granted entitlements', 'to'],
        'billing.grant-revoked': 'revoked a grant',
        'billing.grant-amended': 'moved the expiry of a grant',
        'billing.plan-created': 'created a plan',
        'billing.plan-edited': 'wrote a new version of a plan',
        'billing.plan-version-activated': 'made a plan version current',
        'billing.plan-version-archived': 'archived a plan version',
        'billing.plan-archived': 'archived a plan',
        'billing.plan-assigned': 'moved onto a plan version',
        'billing.cache-invalidated': ['forced an entitlement re-resolve', 'for'],
        'billing.credit-issued': ['issued credit', 'to'],
        'billing.credit-adjusted': ['corrected a credit balance', 'for'],
        'billing.credit-reversed': 'took back a credit issuance',
        'billing.credit-voided': ['voided a credit wallet', 'belonging to'],
        'billing.credit-rebuilt': ['rebuilt a cached credit balance', 'for'],
        'billing.credit-campaign-created': 'opened a credit campaign',
        'billing.credit-campaign-paused': 'paused a credit campaign',
        'billing.credit-campaign-budget-set': 'changed a credit campaign budget',
        'billing.promotion-campaign-created': 'opened a promotion campaign',
        'billing.promotion-campaign-paused': 'paused a promotion campaign',
        'billing.promotion-campaign-resumed': 'resumed a promotion campaign',
        'billing.promotion-campaign-budget-set': 'changed a promotion campaign budget',
        'discovery.ban-issued': 'banned a guild from discovery',
        'discovery.ban-lifted': 'lifted a discovery ban',
    };

    function auditVerb(action, withName = false) {
        const written = AUDIT_VERBS[action];

        if (!written) return action.replace(/[.-]/g, ' ');
        if (typeof written === 'string') return written;

        const [phrase, preposition] = written;
        return withName ? `${phrase} ${preposition}` : phrase;
    }

    /** Whether the row is about to print a person's name, which is what decides the verb's shape. */
    const namesAnAccount = entry => Boolean(entry.subjectId?.startsWith('user_'));

    /** A name when we have one, the id when we do not. Never both - the id is in the detail pane. */
    const who = (id, name) => name || id || 'someone';

    /**
     * The second line: what was done, and to which record.
     *
     * The subject id goes here rather than in the sentence when it is not an account, because
     * "closed a report about Alex - rprt_01KZ8M..." reads correctly while putting the id in the
     * sentence does not.
     */
    function auditSubtitle(entry) {
        const parts = [];

        if (entry.detail) parts.push(entry.detail);
        if (entry.subjectId && !entry.subjectId.startsWith('user_')) parts.push(entry.subjectId);

        return parts.join(' · ');
    }

    function auditPane(entry) {
        const pane = el('div', 'pane');

        // The sentence first, so the pane opens with what happened rather than with a dotted
        // identifier. Everything below it is the evidence for that sentence.
        const summary = el('div', 'quote');
        summary.textContent =
            `${who(entry.actorUserId, entry.actorName)} ${auditVerb(entry.action, namesAnAccount(entry))}`
            + (namesAnAccount(entry)
                ? ` ${who(entry.subjectId, entry.subjectName)}`
                : '')
            + (entry.detail ? ` - ${entry.detail}` : '');

        pane.append(block(null, summary));

        pane.append(block('Record', kv([
            ['Action', entry.action],
            ['Actor', copyable(entry.actorUserId)],
            ['Actor name', entry.actorName || 'unknown'],
            ['Subject', entry.subjectId ? copyable(entry.subjectId) : '-'],
            // Only for an account subject; a report id has no name and the row would read "unknown"
            // as though something had failed to resolve.
            ['Subject name', entry.subjectId?.startsWith('user_')
                ? entry.subjectName || 'unknown'
                : null],
            ['When', stamp(entry.createdAt)],
            ['From', entry.ipAddress || '-'],
        ])));

        // The subject is where the trail continues, so make it one click rather than a copy-paste.
        if (entry.subjectId?.startsWith('user_')) {
            pane.append(block('Follow', button('Open this account', 'user',
                () => openUser(entry.subjectId))));
        }

        return pane;
    }

    // ── Entry ───────────────────────────────────────────────────────────────

    if (tokens.access) {
        start();
    } else {
        $('#g-user').focus();
    }
})();
