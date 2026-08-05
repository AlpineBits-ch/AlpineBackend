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
     * One request wrapper, with a single refresh attempt on 401.
     *
     * The retry is deliberately not a loop: if a refreshed token is also rejected, the session is
     * genuinely over and looping just delays telling the operator that.
     */
    async function call(method, path, { body, query, retried = false } = {}) {
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
                ...(body ? { 'Content-Type': 'application/json' } : {}),
            },
            body: body ? JSON.stringify(body) : undefined,
        });

        if (response.status === 401 && !retried && tokens.refresh) {
            try {
                const refreshed = await grant({
                    grant_type: 'refresh_token',
                    client_id: 'echo',
                    refresh_token: tokens.refresh,
                });

                tokens.set(refreshed.access_token, refreshed.refresh_token);
                return call(method, path, { body, query, retried: true });
            } catch {
                signOut();
                throw new Error('Your session expired. Sign in again.');
            }
        }

        if (response.status === 401) {
            signOut();
            throw new Error('Your session expired. Sign in again.');
        }

        const text = await response.text();
        let payload = null;
        try { payload = text ? JSON.parse(text) : null; } catch { /* not JSON */ }

        if (!response.ok) throw new Error(payload?.message || `The server answered ${response.status}.`);

        return payload;
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

    async function start() {
        try {
            session = await call('GET', `${API}/session`);
        } catch (error) {
            // A correct password on a non-staff account lands here. Saying so is the whole reason
            // this call exists - the alternative is showing an empty queue to someone who will
            // reasonably conclude the console is broken.
            tokens.clear();
            $('#signin-error').replaceChildren(banner('danger',
                error.message.includes('moderator') || error.message.includes('administrator')
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

    // ── View: audit ─────────────────────────────────────────────────────────

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

                list.append(row({
                    // The one entry type that decides who can act on everyone else gets the stripe.
                    mark: entry.action === 'user.role-changed' ? 'critical' : '',
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
