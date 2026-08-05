/*
 * The public status page.
 *
 * Anonymous, read-only, and same-origin: the API answers on this hostname too, so there is no base
 * URL and no CORS to negotiate here. Nothing on this page writes anything.
 *
 * Two rules run through the whole file:
 *
 *   1. The copy comes from the server. Titles and bodies are rendered verbatim - the rule about how
 *      technical a status message may be lives in one place, and it is not this one.
 *   2. Every enum from the server is an open set. A value this build has never seen renders as the
 *      least alarming thing available, never as a crash and never as "major outage".
 */
(() => {
    'use strict';

    const API = '/api/v1/status';

    /** Foreground only. A status page left open in a background tab for a week must not be a load
     *  source, and nothing it shows matters while nobody is looking at it. */
    const POLL_MS = 30000;

    const HISTORY_PAGE = 25;

    // ── Helpers ─────────────────────────────────────────────────────────────

    const $ = (selector, root = document) => root.querySelector(selector);

    /** Text into a node, never markup. Incident bodies are staff-written free text; an innerHTML
     *  shortcut here would be the one place this page could be made to execute someone's prose. */
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

    function when(iso, withDate = true) {
        if (!iso) return '';
        const date = new Date(iso);
        return date.toLocaleString(undefined, withDate
            ? { year: 'numeric', month: 'short', day: 'numeric', hour: '2-digit', minute: '2-digit' }
            : { hour: '2-digit', minute: '2-digit' });
    }

    function dayLabel(iso) {
        if (!iso) return '';
        return new Date(iso).toLocaleDateString(undefined, { month: 'short', day: 'numeric' });
    }

    /** "14 minutes", "3 hours", "2 days" - coarse on purpose. Nobody needs the seconds, and a
     *  ticking counter on an outage page reads as a stopwatch on our own failure. */
    function since(iso) {
        if (!iso) return '';

        const minutes = Math.max(0, Math.round((Date.now() - new Date(iso).getTime()) / 60000));
        if (minutes < 1) return 'just now';
        if (minutes < 60) return `${minutes} minute${minutes === 1 ? '' : 's'}`;

        const hours = Math.round(minutes / 60);
        if (hours < 48) return `${hours} hour${hours === 1 ? '' : 's'}`;

        return `${Math.round(hours / 24)} days`;
    }

    async function call(path, query) {
        const url = new URL(path, location.origin);
        if (query) Object.entries(query).forEach(([k, v]) => v != null && url.searchParams.set(k, v));

        const response = await fetch(url, { headers: { Accept: 'application/json' } });
        if (!response.ok) throw new Error(`The server answered ${response.status}.`);

        return response.json();
    }

    // ── Vocabulary ──────────────────────────────────────────────────────────

    const COMPONENT_LABEL = {
        operational: 'Operational',
        degraded_performance: 'Degraded',
        partial_outage: 'Partial outage',
        major_outage: 'Major outage',
        under_maintenance: 'Maintenance',
    };

    const COMPONENT_TONE = {
        operational: '',
        degraded_performance: 'warn',
        partial_outage: 'danger',
        major_outage: 'danger',
        under_maintenance: 'info',
    };

    const STATE_LABEL = {
        investigating: 'Investigating',
        identified: 'Identified',
        monitoring: 'Monitoring',
        resolved: 'Resolved',
        scheduled: 'Scheduled',
        in_progress: 'In progress',
        completed: 'Completed',
        cancelled: 'Cancelled',
    };

    const VERDICT = {
        operational: { tone: 'ok', icon: 'check-circle', text: 'All systems operational.' },
        degraded: { tone: 'degraded', icon: 'exclamation-triangle', text: 'Some parts of venta are having trouble.' },
        partial_outage: { tone: 'down', icon: 'exclamation-circle', text: 'Some parts of venta are down.' },
        major_outage: { tone: 'down', icon: 'times-circle', text: 'venta is down.' },
        maintenance: { tone: 'maintenance', icon: 'wrench', text: 'Scheduled maintenance is in progress.' },
    };

    /** Unknown slugs fall back to a readable version of themselves rather than to a guess about
     *  severity. A future "rate_limited" state should read "Rate limited", not "Major outage". */
    function label(map, slug) {
        return map[slug] || (slug ? slug.replace(/_/g, ' ').replace(/^./, c => c.toUpperCase()) : 'Unknown');
    }

    function tone(slug) {
        return COMPONENT_TONE[slug] === undefined ? 'unknown' : COMPONENT_TONE[slug];
    }

    // ── Routing ─────────────────────────────────────────────────────────────

    const ROUTES = ['/', '/incident', '/history', '/maintenance'];

    function show(path) {
        const route = ROUTES.includes(path) ? path : '/';

        $('#page-overview').toggleAttribute('data-active', route === '/');
        $('#page-incident').toggleAttribute('data-active', route === '/incident');
        $('#page-history').toggleAttribute('data-active', route === '/history' || route === '/maintenance');

        if (route === '/incident') loadIncident(new URLSearchParams(location.search).get('ref'));
        if (route === '/history' || route === '/maintenance') startHistory(route === '/maintenance');

        window.scrollTo(0, 0);
    }

    function go(href) {
        history.pushState(null, '', href);
        show(new URL(href, location.origin).pathname);
    }

    document.addEventListener('click', event => {
        const link = event.target.closest('a[data-route]');
        if (!link || event.metaKey || event.ctrlKey || event.shiftKey) return;

        event.preventDefault();
        go(link.getAttribute('href'));
    });

    addEventListener('popstate', () => show(location.pathname));

    // ── Overview ────────────────────────────────────────────────────────────

    function renderVerdict(summary) {
        const verdict = VERDICT[summary.indicator] || VERDICT.operational;
        const box = $('#verdict');

        box.dataset.tone = verdict.tone;
        $('#verdict-text').textContent = verdict.text;
        $('.verdict-mark', box).replaceChildren(icon(verdict.icon));

        const active = (summary.incidents || []).length;
        const sub = active > 0
            ? `${active} active incident${active === 1 ? '' : 's'}. Updated ${when(summary.updatedAt, false)}.`
            : `Checked ${when(summary.updatedAt, false)}.`;

        $('#verdict-sub').textContent = sub;
        $('#updated').textContent = `Updated ${when(summary.updatedAt)}`;
    }

    function renderComponents(summary, uptime) {
        const strips = new Map((uptime?.components || []).map(c => [c.key, c]));
        const list = $('#components');

        list.replaceChildren(...(summary.components || []).map(component => {
            const row = el('div', 'component');

            row.append(el('div', 'component-name', component.name));
            if (component.description) row.append(el('div', 'component-desc', component.description));

            const state = el('div', 'component-state');
            state.append(el('span', `dot ${tone(component.status)}`.trim()));
            state.append(el('span', null, label(COMPONENT_LABEL, component.status)));
            row.append(state);

            const days = strips.get(component.key)?.days;
            if (days?.length) {
                const strip = el('div', 'strip');
                strip.append(...days.map(day => {
                    const bar = el('i', tone(day.status));
                    bar.title = day.uptime === null || day.uptime === undefined
                        ? `${dayLabel(day.day)}: no data`
                        : `${dayLabel(day.day)}: ${(day.uptime * 100).toFixed(2)}%`;
                    return bar;
                }));
                row.append(strip);

                const legend = el('div', 'strip-legend');
                legend.append(el('span', null, `${days.length} days ago`));
                legend.append(el('span', null, component.uptime90d != null
                    ? `${(component.uptime90d * 100).toFixed(2)}% uptime`
                    : 'No data yet'));
                legend.append(el('span', null, 'Today'));
                row.append(legend);
            }

            return row;
        }));
    }

    function severityClass(incident) {
        if (incident.kind === 'maintenance') return 'info';
        if (incident.impact === 'critical' || incident.impact === 'major') return 'critical';
        return '';
    }

    function incidentCard(incident, { timeline = true, componentNames = null } = {}) {
        const card = el('article', `incident ${severityClass(incident)}`.trim());
        if (incident.resolvedAt) card.classList.add('resolved');

        const head = el('div', 'incident-head');
        const heading = el('div');

        heading.append(el('h3', null, incident.title));

        const meta = el('div', 'incident-meta');
        meta.append(el('span', null, label(STATE_LABEL, incident.status)));
        meta.append(el('span', null, '·'));
        meta.append(el('span', null, incident.kind === 'maintenance' && incident.scheduledFor
            ? `Scheduled for ${when(incident.scheduledFor)}`
            : `Started ${when(incident.startedAt)}`));

        if (!incident.resolvedAt && incident.kind !== 'maintenance') {
            meta.append(el('span', null, '·'));
            meta.append(el('span', null, `Open for ${since(incident.startedAt)}`));
        }

        meta.append(el('span', 'mono', incident.reference));
        heading.append(meta);
        head.append(heading);
        card.append(head);

        if (incident.components?.length && componentNames) {
            const tags = el('div', 'incident-components');
            tags.append(...incident.components.map(key => el('span', 'tag', componentNames.get(key) || key)));
            card.append(tags);
        }

        if (timeline && incident.updates?.length) {
            const list = el('ul', 'timeline');

            list.append(...incident.updates.map(update => {
                const item = el('li');
                item.dataset.state = update.status;

                const line = el('div', 'timeline-head');
                line.append(el('span', 'timeline-state', label(STATE_LABEL, update.status)));
                line.append(el('span', 'timeline-time', when(update.postedAt)));
                item.append(line);
                item.append(el('p', 'timeline-body', update.body));

                return item;
            }));

            card.append(list);
        }

        return card;
    }

    function renderIncidents(summary) {
        const names = new Map((summary.components || []).map(c => [c.key, c.name]));

        const active = $('#active');
        active.replaceChildren(...(summary.incidents || [])
            .map(i => incidentCard(i, { componentNames: names })));

        const upcoming = $('#upcoming');
        upcoming.replaceChildren(...(summary.maintenance || [])
            .map(i => incidentCard(i, { componentNames: names })));

        const recent = $('#recent');
        if (!(summary.recent || []).length) {
            recent.replaceChildren(el('p', 'empty', 'No incidents in recent history.'));
            return;
        }

        recent.replaceChildren(...summary.recent.map(historyRow));
    }

    function historyRow(incident) {
        const row = el('div', 'history-row');

        const link = el('a', null, incident.title);
        link.href = `/incident?ref=${encodeURIComponent(incident.reference)}`;
        link.dataset.route = '';
        row.append(link);

        row.append(el('span', 'when', when(incident.resolvedAt || incident.startedAt)));

        return row;
    }

    // ── One incident ────────────────────────────────────────────────────────

    async function loadIncident(reference) {
        const view = $('#incident-view');

        if (!reference) {
            view.replaceChildren(el('p', 'empty', 'No incident was named in that link.'));
            return;
        }

        view.replaceChildren(el('p', 'empty', 'Loading...'));

        try {
            const incident = await call(`${API}/incidents/${encodeURIComponent(reference)}`);
            view.replaceChildren(incidentCard(incident));
            document.title = `${incident.title} - venta status`;
        } catch {
            view.replaceChildren(el('p', 'empty', 'We could not find that incident.'));
        }

        Icons.paint(view);
    }

    // ── History ─────────────────────────────────────────────────────────────

    let historyOffset = 0;
    let historyKind = null;

    async function startHistory(maintenanceOnly) {
        historyKind = maintenanceOnly ? 'Maintenance' : null;
        historyOffset = 0;

        $('#history-title').textContent = maintenanceOnly ? 'Maintenance history' : 'Incident history';
        $('#history').replaceChildren();

        await loadHistory();
    }

    async function loadHistory() {
        const button = $('#history-more');
        button.disabled = true;

        try {
            const page = await call(`${API}/incidents`, {
                limit: HISTORY_PAGE,
                offset: historyOffset,
                kind: historyKind,
            });

            const list = $('#history');
            if (!page.incidents.length && historyOffset === 0) {
                list.replaceChildren(el('p', 'empty', 'Nothing here yet.'));
            } else {
                page.incidents.forEach(incident => list.append(historyRow(incident)));
            }

            historyOffset += page.incidents.length;

            $('#history-count').textContent = `${page.total} total`;
            button.classList.toggle('hidden', historyOffset >= page.total);
        } catch {
            $('#history').append(el('p', 'empty', 'We could not load the history.'));
        } finally {
            button.disabled = false;
        }
    }

    $('#history-more').addEventListener('click', loadHistory);

    // ── Polling ─────────────────────────────────────────────────────────────

    let timer = null;

    async function refresh() {
        try {
            // Uptime is a second call and a much larger payload, so it is fetched once per refresh
            // alongside the summary rather than being folded into it. A failure of either leaves the
            // page showing what it last knew instead of an error - the previous answer is still the
            // best answer available.
            const [summary, uptime] = await Promise.all([
                call(`${API}/summary`),
                call(`${API}/uptime`).catch(() => null),
            ]);

            renderVerdict(summary);
            renderComponents(summary, uptime);
            renderIncidents(summary);

            Icons.paint(document.body);
        } catch {
            $('#verdict-sub').textContent = 'We could not reach the status service. Retrying.';
        }
    }

    function poll(on) {
        clearInterval(timer);
        timer = on ? setInterval(refresh, POLL_MS) : null;
    }

    document.addEventListener('visibilitychange', () => {
        if (document.hidden) {
            poll(false);
            return;
        }

        refresh();
        poll(true);
    });

    // Support is a sibling host derived the same way this one is, so it can be named without asking
    // the server: swap the first label. Skipped on a bare hostname rather than guessed at - the same
    // rule the support site uses for its docs link.
    const parts = location.hostname.split('.');
    if (parts.length >= 2) {
        const link = el('a', null, 'Support');
        link.href = `${location.protocol}//${['support', ...parts.slice(1)].join('.')}`;
        $('#support-link').replaceChildren(link);
    }

    show(location.pathname);
    refresh();
    poll(true);
})();
