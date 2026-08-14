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

    /** Absolute date on hover, relative in the row. A queue is read by age, not by timestamp. */
    function ago(iso) {
        if (!iso) return '';

        const seconds = (Date.now() - new Date(iso)) / 1000;
        if (seconds < 60) return 'just now';
        if (seconds < 3600) return `${Math.floor(seconds / 60)}m`;
        if (seconds < 86400) return `${Math.floor(seconds / 3600)}h`;
        if (seconds < 2592000) return `${Math.floor(seconds / 86400)}d`;
        return new Date(iso).toLocaleDateString(undefined, { month: 'short', day: 'numeric' });
    }

    function stamp(iso) {
        return iso ? new Date(iso).toLocaleString() : '-';
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

    $('#sign-out').addEventListener('click', signOut);

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

    function openDetail(title, content) {
        $('#detail-title').textContent = title;
        $('#detail-body').replaceChildren(content);
        $('#detail').classList.remove('hidden');
    }

    function closeDetail() {
        $('#detail').classList.add('hidden');
        $('#detail-body').replaceChildren();
        $$('.rw').forEach(row => row.setAttribute('aria-selected', 'false'));
    }

    $('#detail-close').addEventListener('click', () => { selectedId = null; closeDetail(); });

    // ── Modal ───────────────────────────────────────────────────────────────

    function modal(build) {
        const host = $('#modal-host');
        const box = $('#modal');

        function close() {
            host.classList.add('hidden');
            box.replaceChildren();
            removeEventListener('keydown', onKey);
        }

        function onKey(event) { if (event.key === 'Escape') close(); }

        box.replaceChildren(build(close));
        host.classList.remove('hidden');
        addEventListener('keydown', onKey);

        $('.modal-backdrop').onclick = close;
        box.querySelector('input, textarea, select, button')?.focus();

        return close;
    }

    function field(label, control, hint) {
        const wrap = el('div', 'field');
        wrap.append(el('label', null, label), control);
        if (hint) wrap.append(el('p', 'hint', hint));
        return wrap;
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

                const side = [el('span', 'rw-time', ago(report.createdAt))];

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
            ['Priority', report.priority],
            ['Status', report.status],
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
                form.append(field('Original report id', original));
            }

            const note = el('textarea');
            note.required = true;
            note.rows = 4;
            note.placeholder = status === 'Dismissed'
                ? 'e.g. Reviewed the channel; the message is heated but within the rules.'
                : 'e.g. Same incident as the earlier report from another member.';
            form.append(field('Resolution', note));

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

                title.push(tag({
                    Pending: 'Waiting', UnderReview: 'Being reviewed',
                    Granted: 'Accepted', Denied: 'Declined',
                }[appeal.status] || appeal.status,
                    appeal.status === 'Granted' ? 'ok' : appeal.status === 'Denied' ? 'danger' : 'info'));

                list.append(row({
                    mark: appeal.status === 'Pending' ? 'high' : 'normal',
                    title,
                    sub: appeal.body,
                    side: [el('span', 'rw-time', ago(appeal.createdAt))],
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
            ['Status', appeal.status],
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
                el('p', 'hint', `${appeal.status} by ${appeal.decidedByUserId} on ${stamp(appeal.decidedAt)}`)));

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

            form.append(field('What they will be told', note));

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
                title.push(tag(ticket.category));
                if (waiting) title.push(tag('Needs a reply', 'warn'));
                else if (ticket.status === 'AwaitingRequester') title.push(tag('With them'));
                else title.push(tag(ticket.status, 'ok'));

                list.append(row({
                    mark: waiting ? 'high' : 'normal',
                    title,
                    sub: ticket.contactEmail,
                    side: [el('span', 'rw-time', ago(ticket.lastActivityAt))],
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
            ['Category', ticket.category],
            ['Status', ticket.status],
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
                ['PendingDeletion', 'Deleting'],
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
                else if (user.status !== 'Active') title.push(tag(user.status));
                if (user.hasActiveSanction && user.status !== 'Banned') title.push(tag('Sanctioned', 'warn'));
                if (user.userType !== 'Default') title.push(tag(user.userType, 'info'));
                if (!user.emailVerified && user.userType !== 'Bot') title.push(tag('Unverified'));

                list.append(row({
                    mark: user.status === 'Banned' ? 'critical' : user.hasActiveSanction ? 'high' : '',
                    title,
                    sub: user.email || user.id,
                    side: [el('span', 'rw-time', ago(user.createdAt))],
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
            ['Status', user.user.status],
            ['Type', user.user.userType],
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
        controls.append(button('Act on this account', 'ban', () => issueAction(id), 'danger'));
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
                body.append(el('span', 'when', `${stamp(report.createdAt)} · ${report.status}`));
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

        form.addEventListener('submit', event => {
            event.preventDefault();

            // Promotion to Admin gets a confirmation step of its own. Everything else in this
            // console is reversible by the person who did it; handing someone the ability to demote
            // you is not.
            const promotingToAdmin = picker.value === 'Admin';

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
                ['Open reports', moderation.openReports, moderation.openReports > 0 ? 'warn' : ''],
                ['Critical', moderation.criticalReports, moderation.criticalReports > 0 ? 'alert' : ''],
                ['Unclaimed', moderation.unassignedReports],
                ['Older than 48h', moderation.staleReports, moderation.staleReports > 0 ? 'alert' : ''],
                ['Open appeals', moderation.openAppeals],
                ['Tickets needing a reply', moderation.ticketsAwaitingStaff],
                ['Open tickets', moderation.openTickets],
            ]));

            wrap.append(el('div', 'section-head', 'Enforcement'));
            wrap.append(tiles([
                ['Bans in force', moderation.activeBans],
                ['Suspensions running', moderation.activeSuspensions],
            ]));

            if (platformAvailable) {
                wrap.append(el('div', 'section-head', 'Accounts'));
                wrap.append(tiles([
                    ['Registered', platform.totalUsers],
                    ['Active', platform.activeUsers],
                    ['Banned', platform.bannedUsers],
                    ['Being deleted', platform.pendingDeletion],
                    ['Deleted', platform.deletedUsers],
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

    function tiles(entries) {
        const grid = el('div', 'tiles');

        entries.forEach(([label, value, kind]) => {
            const tile = el('div', `tile ${kind || ''}`);
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
                    side: [el('span', 'rw-time', ago(instance.lastSeen))],
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
            actions.append(button('Approve', 'check-circle', async () => {
                await call('POST', `${FED}/${encodeURIComponent(instance.id)}/approve`);
                toast('ok', `${instance.host} approved`);
                closeDetail();
                render();
            }, 'primary'));

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
            form.append(field('Reason', reason));

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
            form.append(field('Their hostname', host, 'The instance host, without a scheme or a path.'));

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

        box.append(field('File', picker));
        box.append(field('Database', source,
            'Decides which database each row is credited to and which product page a client links '
            + 'to. Getting it wrong misattributes every row in the file.'));
        box.append(field('Snapshot', version,
            'Travels with every row and appears in the public export, so a reader can tell which '
            + 'extract an answer came from.'));

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

            if (!file) { toast('danger', 'Pick a file first.'); return; }
            if (!version.value.trim()) { toast('danger', 'A snapshot name is required.'); return; }

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

        const list = el('div', 'rows');

        incidents.forEach(incident => {
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
                side: [el('span', 'rw-time', ago(incident.startedAt))],
                onOpen: () => openIncident(incident.id),
            }));
        });

        wrap.append(list, listFoot(total, incidents.length));
        return wrap;
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
        box.append(field('Update', body));

        const post = button('Post update', 'send', async () => {
            if (!body.value.trim()) return toast('danger', 'An update needs a body.');

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
            form.append(field('Title', title));
            form.append(field('Impact', impact, 'Decides the colour, and what the incident claims about its components.'));
            form.append(field('Affects', picker));
            form.append(schedule);
            form.append(field('First update', body));

            const actions = el('div', 'actions');
            actions.append(button('Cancel', 'times', close));

            const publish = button('Publish', 'send', async () => {
                if (!title.value.trim() || !body.value.trim()) {
                    return toast('danger', 'A title and a first update are both required.');
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
    };

    views.billing = {
        title: 'Billing',

        tools() {
            const tabs = [['subject', 'Subject'], ['plans', 'Plans']].map(([key, text]) => {
                const button = el('button', `btn sm ${billingTab === key ? 'primary' : 'ghost'}`, text);
                button.addEventListener('click', () => billingGo(key));
                return button;
            });

            if (billingTab === 'plans' && canEditBilling() && billingCatalogue?.billingDeployed) {
                const add = el('button', 'btn sm primary');
                add.append(icon('plus'), document.createTextNode(' New plan'));
                add.addEventListener('click', createPlan);
                tabs.push(add);
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

            return billingTab === 'plans' ? renderPlans() : renderBillingSubject();
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
        const wrap = el('div', 'pane');
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

        const form = el('form');
        form.style.cssText = 'display:flex;gap:8px;align-items:flex-end;margin-top:12px;';

        const kind = select([['Guild', 'Guild'], ['User', 'User']], billingSubject.kind);

        const id = el('input');
        id.value = billingSubject.id;
        id.spellcheck = false;
        id.placeholder = billingSubject.kind === 'Guild' ? 'guild_...' : 'user_...';

        kind.addEventListener('change', () => {
            id.placeholder = kind.value === 'Guild' ? 'guild_...' : 'user_...';
        });

        const wide = el('div', 'grow');
        wide.append(id);

        const submit = el('button', 'btn sm primary');
        submit.type = 'submit';
        submit.append(icon('search'), document.createTextNode(' Look up'));

        form.append(kind, wide, submit);

        form.addEventListener('submit', event => {
            event.preventDefault();
            billingSubject.kind = kind.value;
            billingSubject.id = id.value.trim();
            render();
        });

        box.append(form);
        return box;
    }

    function provenanceTable(entries) {
        const table = el('div', 'prov');

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
            actions.style.marginTop = '8px';
            actions.append(button('Revoke', 'times-circle', () => revokeGrant(grant), 'danger'));
            actions.append(button('Change expiry', 'clock', () => amendGrantExpiry(grant)));
            body.append(actions);
        }

        item.append(body);
        return item;
    }

    function issueGrant() {
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

            const plan = el('input');
            plan.spellcheck = false;
            plan.placeholder = 'pro';
            const planField = field('Plan', plan,
                'The plan name. Pin a version with name@number to grant a specific set of numbers.');
            form.append(planField);

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
                            plan: kind.value === 'Plan' ? plan.value.trim() : null,
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

    async function assignPlan() {
        const plans = await call('GET', `${BILLING}/plans`);

        if (!plans.length) {
            toast('danger', 'There are no plans to move anybody onto.');
            return;
        }

        modal(close => {
            const form = el('form');
            form.append(el('h2', null, 'Move to a plan version'));
            form.append(el('p', 'lede',
                'The one operation that deliberately changes what an existing subscriber resolves. '
                + 'Everything else leaves people on the version they bought.'));

            const picker = select(
                plans.map(plan => [plan.name, `${plan.displayName || plan.name} - current version ${plan.currentVersionNumber}`]),
                plans[0]?.name);

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

        const list = el('div', 'rows');

        plans.forEach(plan => {
            const title = [el('span', null, plan.displayName || plan.name)];
            title.push(el('span', 'mono faint', plan.name));
            title.push(tag(`v${plan.currentVersionNumber}`, 'info'));
            if (plan.archivedAt) title.push(tag('Archived', 'danger'));
            if (plan.seededFromConfiguration) title.push(tag('Seeded'));

            const current = plan.versions.find(version => version.isCurrent);

            list.append(row({
                title,
                sub: plan.description || money(current),
                side: [el('span', 'rw-time', `${plan.versions.length} version${plan.versions.length === 1 ? '' : 's'}`)],
                selected: plan.name === selectedId,
                onOpen: () => openPlan(plan.name),
            }));
        });

        const wrap = el('div');
        wrap.append(list, listFoot(plans.length, plans.length));
        return wrap;
    }

    function money(version) {
        if (!version || version.priceMinorUnits === null || version.priceMinorUnits === undefined) {
            return 'Not sold';
        }

        return `${(version.priceMinorUnits / 100).toFixed(2)} ${(version.currency || '').toUpperCase()}`.trim();
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

        const pane = el('div', 'pane');
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
                actions.style.marginTop = '8px';
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
                line.append(document.createTextNode(` ${auditVerb(entry.action)}`));

                // Only when the subject is an account. It is just as often a report, an action or a
                // ticket, and "banned rprt_01KZ8M..." is worse than saying nothing.
                if (entry.subjectId?.startsWith('user_')) {
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
                    side: [el('span', 'rw-time', ago(entry.createdAt))],
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
     */
    const AUDIT_VERBS = {
        'action.issued': 'actioned',
        'action.revoked': 'lifted a restriction on',
        'report.assigned': 'picked up a report about',
        'report.resolved': 'closed a report about',
        'report.reopened': 'reopened a report about',
        'appeal.claimed': 'took an appeal from',
        'appeal.decided': 'decided an appeal from',
        'ticket.replied': 'replied to a ticket from',
        'ticket.updated': 'updated a ticket from',
        'user.role-changed': 'changed the staff role of',
        'user.viewed': 'looked at',
        'status.incident-created': 'published a status incident',
        'status.incident-updated': 'posted a status update on',
        'status.incident-edited': 'edited a status incident',
        'status.incident-confirmed': 'took over a status incident',
        'status.incident-retracted': 'retracted a status incident',
        'status.component-created': 'added a status component',
        'status.component-updated': 'edited a status component',
        'billing.grant-issued': 'granted entitlements to',
        'billing.grant-revoked': 'revoked a grant',
        'billing.grant-amended': 'moved the expiry of a grant',
        'billing.plan-created': 'created a plan',
        'billing.plan-edited': 'wrote a new version of a plan',
        'billing.plan-version-activated': 'made a plan version current',
        'billing.plan-version-archived': 'archived a plan version',
        'billing.plan-archived': 'archived a plan',
        'billing.plan-assigned': 'moved onto a plan version',
        'billing.cache-invalidated': 'forced an entitlement re-resolve for',
    };

    const auditVerb = action => AUDIT_VERBS[action] || action.replace(/[.-]/g, ' ');

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
            `${who(entry.actorUserId, entry.actorName)} ${auditVerb(entry.action)}`
            + (entry.subjectId?.startsWith('user_')
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
