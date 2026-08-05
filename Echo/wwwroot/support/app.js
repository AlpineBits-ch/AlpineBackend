/*
 * The public support site.
 *
 * Anonymous throughout - the people who most need this are the ones who cannot sign in. Every
 * request carries its own credential: a ticket needs its access key, an appeal needs the action's
 * reference plus the email it was filed under.
 *
 * Same-origin: the API answers on this hostname too, so there is no base URL and no CORS.
 */
(() => {
    'use strict';

    const API = '/api/v1/support';

    // ── Helpers ─────────────────────────────────────────────────────────────

    const $ = (selector, root = document) => root.querySelector(selector);
    const $$ = (selector, root = document) => [...root.querySelectorAll(selector)];

    /** Text into a node, never markup. Every string below is either a server message or something
     *  the user typed, and the one place this page would break is an innerHTML shortcut. */
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

    function notice(kind, message, into) {
        const box = el('div', `notice ${kind}`);
        box.append(icon(kind === 'ok' ? 'check-circle' : kind === 'danger' ? 'exclamation-circle' : 'info-circle'));
        box.append(el('div', 'grow', message));

        into.replaceChildren(box);
        return box;
    }

    function when(iso) {
        if (!iso) return '';
        return new Date(iso).toLocaleString(undefined, {
            year: 'numeric', month: 'short', day: 'numeric', hour: '2-digit', minute: '2-digit',
        });
    }

    /**
     * One request wrapper for the whole page.
     *
     * Failures surface the server's own message when there is one. "Something went wrong" to a
     * person who is already stuck is the thing this avoids, and every endpoint here returns
     * {code, message}.
     */
    async function call(method, path, { body, query } = {}) {
        const url = new URL(path, location.origin);
        if (query) Object.entries(query).forEach(([k, v]) => v != null && url.searchParams.set(k, v));

        let response;
        try {
            response = await fetch(url, {
                method,
                headers: body ? { 'Content-Type': 'application/json' } : undefined,
                body: body ? JSON.stringify(body) : undefined,
            });
        } catch {
            throw new Error('We could not reach the server. Check your connection and try again.');
        }

        const text = await response.text();
        let payload = null;
        try { payload = text ? JSON.parse(text) : null; } catch { /* not JSON */ }

        if (response.ok) return payload;

        if (response.status === 429) {
            const retry = response.headers.get('Retry-After');
            throw new Error(`Too many attempts. Wait ${retry ? `${retry} seconds` : 'a moment'} and try again.`);
        }

        throw new Error(payload?.message || `The server answered ${response.status}.`);
    }

    /** Swaps a submit button into its working state and back, keeping its label stable. */
    function busy(button, on) {
        const label = button.dataset.label || button.textContent.trim();
        button.disabled = on;

        const mark = icon(on ? 'spinner' : button.dataset.restIcon || 'send');
        if (on) mark.classList.add('spin');

        button.replaceChildren(mark, document.createTextNode(` ${on ? 'Working…' : label}`));
    }

    // ── Navigation ──────────────────────────────────────────────────────────

    const pages = ['contact', 'appeal', 'ticket'];

    function show(name, push = true) {
        if (!pages.includes(name)) name = 'contact';

        pages.forEach(page => {
            $(`#page-${page}`).toggleAttribute('data-active', page === name);
        });

        $$('.door').forEach(door => {
            door.setAttribute('aria-selected', String(door.dataset.nav === name));
        });

        if (push) {
            // replaceState when we are already on this page, so pressing the same door twice does
            // not bury the previous page under duplicate history entries.
            const at = location.pathname.replace(/^\//, '').replace(/\/$/, '');
            const url = `/${name}${location.search}`;

            if (at === name) history.replaceState({ page: name }, '', url);
            else history.pushState({ page: name }, '', url);

            // Only on an explicit choice, and only as far as the sheet - jumping to the top would
            // scroll away from the door the person just pressed.
            $(`#page-${name}`).scrollIntoView({ behavior: 'smooth', block: 'nearest' });
        }
    }

    // Both the doors and the "→" links inside the FAQ answers.
    document.addEventListener('click', event => {
        const trigger = event.target.closest('[data-nav]');
        if (trigger) show(trigger.dataset.nav);
    });

    addEventListener('popstate', () => show(location.pathname.replace(/^\//, ''), false));

    // ── Contact ─────────────────────────────────────────────────────────────

    $('#contact-form').addEventListener('submit', async event => {
        event.preventDefault();

        const button = $('#c-submit');
        const result = $('#contact-result');
        busy(button, true);

        try {
            const payload = await call('POST', `${API}/tickets`, {
                body: {
                    email: $('#c-email').value.trim(),
                    subject: $('#c-subject').value.trim(),
                    category: $('#c-category').value,
                    body: $('#c-body').value,
                },
            });

            $('#contact-form').classList.add('hidden');
            result.replaceChildren(receipt(payload));
            result.scrollIntoView({ behavior: 'smooth', block: 'nearest' });
        } catch (error) {
            notice('danger', error.message, result);
        } finally {
            busy(button, false);
        }
    });

    /** What we hand back after a ticket is opened: the reference, and the only copy of the key. */
    function receipt(payload) {
        const box = el('div');

        box.append(el('h3', null, 'We got your message'));
        box.append(el('p', 'lede small', "We'll reply to that address."));

        box.append(el('label', null, 'Your reference'));
        const ref = el('div', 'ref');
        ref.append(icon('inbox'), document.createTextNode(payload.reference));
        box.append(ref);

        // Shown on screen as well as mailed. The mail can be delayed, filtered, or sent to an
        // address with a typo in it - and if it is, this is the only copy of the key there will
        // ever be.
        const warn = el('div', 'notice warn');
        warn.append(icon('exclamation-triangle'));
        warn.append(el('div', 'grow',
            'Save this key. It is the only way back into your ticket and we cannot re-send it.'));
        box.append(warn);

        box.append(el('label', null, 'Access key'));
        box.append(el('div', 'ref key', payload.token));

        const open = el('button', 'btn primary');
        open.append(icon('arrow-left'), document.createTextNode(' Open the ticket'));
        open.addEventListener('click', () => {
            $('#t-ref').value = payload.reference;
            $('#t-token').value = payload.token;
            show('ticket');
            $('#ticket-form').requestSubmit();
        });
        box.append(open);

        return box;
    }

    // ── Appeal ──────────────────────────────────────────────────────────────

    $('#appeal-form').addEventListener('submit', async event => {
        event.preventDefault();

        const button = $('#a-submit');
        const result = $('#appeal-result');
        busy(button, true);

        try {
            const payload = await call('POST', `${API}/appeals`, {
                body: {
                    reference: $('#a-ref').value.trim(),
                    email: $('#a-email').value.trim(),
                    body: $('#a-body').value,
                },
            });

            $('#appeal-form').classList.add('hidden');
            const box = notice('ok', payload.message, result);

            // The server answers the same body whether or not the reference matched a live action,
            // so it does not always hand back a reference. Showing one only when there is one is
            // the honest rendering of that.
            if (payload.reference) {
                const ref = el('div', 'ref');
                ref.style.marginBottom = '0';
                ref.append(icon('shield'), document.createTextNode(payload.reference));
                box.querySelector('.grow').append(ref);
            }
        } catch (error) {
            notice('danger', error.message, result);
        } finally {
            busy(button, false);
        }
    });

    $('#appeal-status-form').addEventListener('submit', async event => {
        event.preventDefault();

        const result = $('#appeal-status-result');
        result.replaceChildren(el('p', 'hint', 'Checking…'));

        try {
            const payload = await call('GET', `${API}/appeals/${encodeURIComponent($('#as-ref').value.trim())}`, {
                query: { email: $('#as-email').value.trim() },
            });

            const decided = payload.status === 'Granted' || payload.status === 'Denied';

            const box = el('div');
            box.style.marginTop = '18px';

            const head = el('div', 'hstack');
            head.append(el('span', 'mono', payload.reference));
            head.append(el('span',
                `tag ${payload.status === 'Granted' ? 'ok' : payload.status === 'Denied' ? 'danger' : 'info'}`,
                {
                    Pending: 'Waiting to be read',
                    UnderReview: 'Being reviewed',
                    Granted: 'Accepted',
                    Denied: 'Declined',
                }[payload.status] || payload.status));
            box.append(head);

            box.append(el('p', 'hint', `Submitted ${when(payload.submittedAt)}`));

            if (decided) {
                box.append(el('p', 'hint', `Decided ${when(payload.decidedAt)}`));

                if (payload.decision) {
                    const note = el('div', 'msg them');
                    note.style.marginTop = '14px';
                    note.append(el('div', 'msg-body', payload.decision));
                    box.append(note);
                }

                // Said here as well as in the email, because this page is where somebody comes when
                // they are deciding whether to try again. `final` is the server's flag, not a rule
                // re-implemented from the status.
                if (payload.final) {
                    const done = el('div', 'notice info');
                    done.style.marginTop = '14px';
                    done.append(icon('info-circle'));
                    done.append(el('div', 'grow',
                        'Each decision can be appealed once, and this was that appeal. There is no '
                        + 'further appeal - submitting another will not get it looked at again.'));
                    box.append(done);
                }
            } else {
                box.append(el('p', 'hint', 'We will email you when it has been decided.'));
            }

            result.replaceChildren(box);
        } catch (error) {
            notice('danger', error.message, result);
        }
    });

    // ── Ticket ──────────────────────────────────────────────────────────────

    function renderTicket(ticket) {
        const view = $('#ticket-view');
        view.classList.remove('hidden');

        const head = el('div', 'thread-head');
        const title = el('div', 'hstack');
        title.append(el('h2', 'grow', ticket.subject));
        title.append(el('span',
            `tag ${ticket.status === 'Resolved' || ticket.status === 'Closed' ? 'ok' : 'info'}`,
            {
                Open: 'Open',
                AwaitingStaff: 'With support',
                AwaitingRequester: 'Waiting for you',
                Resolved: 'Resolved',
                Closed: 'Closed',
            }[ticket.status] || ticket.status));
        head.append(title);
        head.append(el('div', 'thread-meta',
            `${ticket.reference} · ${ticket.category} · opened ${when(ticket.createdAt)}`));

        const thread = el('div', 'thread');
        ticket.messages.forEach(message => {
            const mine = message.from === 'You';
            const box = el('div', `msg ${mine ? 'mine' : 'them'}`);

            const line = el('div', 'msg-head');
            line.append(el('span', 'msg-from', mine ? 'You' : 'Support'));
            line.append(el('span', 'msg-time', when(message.createdAt)));
            box.append(line);

            box.append(el('div', 'msg-body', message.body));
            thread.append(box);
        });

        view.replaceChildren(head, thread);

        if (ticket.status === 'Closed') {
            const closed = el('div', 'notice info');
            closed.append(icon('info-circle'));
            closed.append(el('div', 'grow', 'This ticket is closed. Start a new one if you still need help.'));
            view.append(closed);
            return;
        }

        view.append(replyForm(ticket));
    }

    function replyForm(ticket) {
        const sheet = el('div', 'sheet secondary');
        const body = el('div', 'sheet-body');
        const form = el('form');

        const field = el('div', 'field');
        field.append(el('label', null, 'Add a reply'));

        const textarea = el('textarea');
        Object.assign(textarea, { maxLength: 8000, rows: 5, required: true });
        textarea.placeholder = 'Anything else we should know?';
        field.append(textarea);
        form.append(field);

        const actions = el('div', 'actions');
        const send = el('button', 'btn primary');
        send.type = 'submit';
        send.dataset.label = 'Reply';
        send.append(icon('send'), document.createTextNode(' Reply'));
        actions.append(send);
        form.append(actions);

        const feedback = el('div');

        form.addEventListener('submit', async event => {
            event.preventDefault();
            busy(send, true);

            try {
                await call('POST', `${API}/tickets/${encodeURIComponent(ticket.reference)}/messages`,
                    { query: { token: ticket.token }, body: { body: textarea.value } });

                // Re-read rather than appending locally: the reply also moves the ticket's status,
                // and a thread showing the new message beside a stale status is a page arguing with
                // itself.
                await loadTicket(ticket.reference, ticket.token);
            } catch (error) {
                notice('danger', error.message, feedback);
                busy(send, false);
            }
        });

        body.append(form, feedback);
        sheet.append(body);
        return sheet;
    }

    async function loadTicket(reference, token) {
        const result = $('#ticket-result');
        result.replaceChildren();

        try {
            const ticket = await call('GET', `${API}/tickets/${encodeURIComponent(reference)}`, { query: { token } });
            ticket.token = token;
            renderTicket(ticket);
        } catch (error) {
            $('#ticket-view').classList.add('hidden');
            notice('danger', error.message, result);
        }
    }

    $('#ticket-form').addEventListener('submit', event => {
        event.preventDefault();
        loadTicket($('#t-ref').value.trim(), $('#t-token').value.trim());
    });

    // ── Entry ───────────────────────────────────────────────────────────────

    // Deep links from the emails: /appeal?ref=..., /ticket?ref=...&token=...
    const params = new URLSearchParams(location.search);
    const path = location.pathname.replace(/^\//, '').replace(/\/$/, '');

    show(path || 'contact', false);

    if (params.get('ref')) {
        if (path === 'ticket') {
            $('#t-ref').value = params.get('ref');
            $('#t-token').value = params.get('token') || '';
            if (params.get('token')) loadTicket(params.get('ref'), params.get('token'));
        } else {
            $('#a-ref').value = params.get('ref');
            $('#as-ref').value = params.get('ref');
        }
    }

    // Only one FAQ answer open at a time. A page of simultaneously-expanded answers is a page
    // nobody can find their place in.
    $$('.qa').forEach(item => {
        item.addEventListener('toggle', () => {
            if (!item.open) return;
            $$('.qa').forEach(other => { if (other !== item) other.open = false; });
        });
    });

    // The docs host is derived the same way this one is, so it can be named without asking the
    // server: swap the first label. Skipped on a bare hostname rather than guessed at.
    const parts = location.hostname.split('.');
    if (parts.length >= 2) {
        const docs = ['docs', ...parts.slice(1)].join('.');
        const href = `${location.protocol}//${docs}`;

        const footerLink = el('a', null, docs);
        footerLink.href = href;
        $('#docs-link').append(document.createTextNode('Developers: '), footerLink);

        const faqLink = el('a', null, 'Read the documentation →');
        faqLink.href = href;
        $('#faq-docs-link').append(faqLink);
    }
})();
