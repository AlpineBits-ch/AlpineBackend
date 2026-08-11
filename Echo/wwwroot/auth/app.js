/*
 * auth.venta.gg - the sign-in site.
 *
 * Same-origin throughout: the API and /connect/* answer on this hostname, so there is no base URL,
 * no CORS and no token in storage. The access token this page obtains lives in a local variable
 * exactly long enough to establish the SSO cookie, and is then dropped - the cookie is HttpOnly, so
 * from that moment nothing here can read the credential it is holding on somebody's behalf.
 *
 * The page never sees the OIDC parameters. /connect/authorize parks the validated request in Redis
 * and redirects here with an opaque id; this page asks what it is for, does its part, and sends the
 * browser to the resume URL the server hands back. That is why a redirect_uri never appears in the
 * address bar - see docs/specs/sso.md §3.1.
 *
 * No build step and no framework, same as the other three gateway sites. The content security
 * policy on this host forbids inline script, so everything is here.
 */
(() => {
    'use strict';

    const API = '/api/v1/identity';

    /** The first-party client the sign-in page itself authenticates as. It holds the password, QR
     *  and Steam grants and deliberately not the authorization-code flow - that belongs to the
     *  websites federating against us, not to the login screen. */
    const CLIENT_ID = 'echo';

    /** No offline_access: this page needs one access token for one call. A refresh token would be a
     *  durable credential minted for a browser tab that is about to navigate away. */
    const SCOPE = 'openid profile email';

    const STORE_RQ = 'venta.auth.rq';
    const STORE_STEAM = 'venta.auth.steamPending';

    // ── Elements ────────────────────────────────────────────────────────────

    const $ = (selector, root = document) => root.querySelector(selector);
    const $$ = (selector, root = document) => [...root.querySelectorAll(selector)];

    const shell = $('#shell');
    const progress = $('#progress');

    // ── Small helpers ───────────────────────────────────────────────────────

    /** Text into a node, never markup. Server messages and typed input both pass through here. */
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

    function link(text, href) {
        const node = el('a', null, text);
        node.href = href;
        return node;
    }

    let busy = 0;

    function startProgress() {
        if (busy++ === 0) {
            progress.removeAttribute('data-done');
            progress.setAttribute('data-busy', '');
        }
    }

    function endProgress() {
        if (--busy <= 0) {
            busy = 0;
            progress.removeAttribute('data-busy');
            progress.setAttribute('data-done', '');
        }
    }

    /**
     * One request wrapper. Never throws on an HTTP status - every caller here has something
     * specific to say about a 401 or a 403, and a rejected promise would flatten all of them into
     * one "something went wrong".
     */
    async function call(method, path, { json, form, token, query } = {}) {
        const url = new URL(path, location.origin);
        if (query) Object.entries(query).forEach(([k, v]) => v != null && url.searchParams.set(k, v));

        const headers = {};
        let body;

        if (json !== undefined) {
            headers['Content-Type'] = 'application/json';
            body = JSON.stringify(json);
        } else if (form !== undefined) {
            headers['Content-Type'] = 'application/x-www-form-urlencoded';
            body = new URLSearchParams(Object.entries(form).filter(([, v]) => v != null)).toString();
        }

        if (token) headers.Authorization = `Bearer ${token}`;

        startProgress();
        try {
            const response = await fetch(url, { method, headers, body, credentials: 'same-origin' });
            const text = await response.text();

            let data = null;
            if (text) {
                try { data = JSON.parse(text); } catch { data = null; }
            }

            return { ok: response.ok, status: response.status, text, data };
        } catch {
            // A genuine transport failure - offline, DNS, a proxy that hung up. Distinct from any
            // HTTP status, and the only case where the page has nothing specific to say.
            return { ok: false, status: 0, text: '', data: null };
        } finally {
            endProgress();
        }
    }

    /** Renders a notice into a container, replacing whatever was there. */
    function notify(container, kind, ...content) {
        const box = el('div', `notice ${kind}`);
        box.append(icon(
            kind === 'ok' ? 'check-circle'
                : kind === 'danger' ? 'exclamation-circle'
                    : kind === 'warn' ? 'exclamation-triangle' : 'info-circle'));

        const body = el('div', 'grow');
        content.forEach(part => body.append(typeof part === 'string' ? document.createTextNode(part) : part));
        box.append(body);

        container.replaceChildren(box);
        return box;
    }

    function clearNotice(container) {
        container.replaceChildren();
    }

    function busyButton(button, label) {
        button.disabled = true;
        button.dataset.idle = button.dataset.idle || button.textContent;
        button.textContent = label;
    }

    function idleButton(button) {
        button.disabled = false;
        if (button.dataset.idle) button.textContent = button.dataset.idle;
    }

    // ── Screens ─────────────────────────────────────────────────────────────

    let current = null;

    function show(name) {
        current = name;

        $$('.screen', shell).forEach(section => {
            section.hidden = section.dataset.screen !== name;
        });

        // Only the sign-in screen is two panes wide. The shell animates between the two widths
        // rather than snapping, so moving from /login to /consent reads as one surface.
        if (name === 'login') shell.setAttribute('data-wide', '');
        else shell.removeAttribute('data-wide');

        const heading = $(`.screen[data-screen="${name}"] h1`, shell);
        if (heading) {
            // Moves the screen reader's cursor to the new screen. Without this, changing screens
            // client-side leaves focus wherever the last button was and announces nothing.
            heading.setAttribute('tabindex', '-1');
            heading.focus({ preventScroll: true });
        }
    }

    /** The terminal screen: a title, a line, and always at least one thing to do next. */
    function finish(kind, title, sub, actions = []) {
        $('#done-crest').replaceChildren(icon(
            kind === 'ok' ? 'check-circle' : kind === 'danger' ? 'times-circle' : 'info-circle'));
        $('#done-crest').className = `crest ${kind === 'danger' ? 'danger' : ''}`;
        $('#done-title').textContent = title;
        $('#done-sub').textContent = sub;

        const box = $('#done-actions');
        box.replaceChildren();
        actions.forEach(({ label, href, primary, onClick }) => {
            const node = href ? el('a', `btn ${primary ? 'primary' : ''}`, label) : el('button', `btn ${primary ? 'primary' : ''}`, label);
            if (href) node.href = href; else node.addEventListener('click', onClick);
            box.append(node);
        });

        show('done');
    }

    // ── The parked request ──────────────────────────────────────────────────

    const state = {
        rq: null,
        request: null,
        steamPending: null,
    };

    function rememberContext() {
        // Survives the trip out to Steam and back, which returns to /steam carrying only its own
        // parameters. sessionStorage rather than a cookie: it is per-tab, it dies with the tab, and
        // it is not sent anywhere.
        if (state.rq) sessionStorage.setItem(STORE_RQ, state.rq);
        if (state.steamPending) sessionStorage.setItem(STORE_STEAM, state.steamPending);
    }

    async function loadRequest(rq) {
        if (!rq) return null;

        const result = await call('GET', `${API}/sso/request/${encodeURIComponent(rq)}`);
        return result.ok ? result.data : null;
    }

    /** Where to send the browser once this page has done its part. */
    function resume() {
        if (state.request?.resumeUrl) {
            location.assign(state.request.resumeUrl);
            return true;
        }
        return false;
    }

    // ── Session ─────────────────────────────────────────────────────────────

    async function currentSession() {
        const result = await call('GET', `${API}/sso/session`);
        return result.ok && result.data?.signedIn ? result.data : null;
    }

    /**
     * Turns a freshly minted access token into the browser session, then continues wherever the
     * journey was going. This is the single funnel every one of the three ways in ends at.
     */
    async function establish(token, into) {
        // A Steam identity waiting to be attached to whatever account just signed in - the second
        // half of the "sign in to link it" door. Done before the redirect, because after it this
        // page is gone.
        const pending = state.steamPending || sessionStorage.getItem(STORE_STEAM);
        if (pending) {
            const linked = await call('POST', `${API}/authentication/steam/pending/${encodeURIComponent(pending)}/link`, { token });
            sessionStorage.removeItem(STORE_STEAM);
            state.steamPending = null;

            if (linked.status === 409) {
                notify(into, 'warn', 'That Steam account is already linked to a different venta account, '
                    + 'so it was not attached to this one. You are signed in.');
            }
        }

        const session = await call('POST', `${API}/sso/session`, { token });

        if (!session.ok) {
            notify(into, 'danger', 'Signed in, but this browser could not be kept signed in. Try again.');
            return;
        }

        sessionStorage.removeItem(STORE_RQ);

        if (resume()) return;

        finish('ok', 'You are signed in',
            `Signed in as ${session.data?.username ?? 'your account'} on this browser.`,
            [{ label: 'Open venta', href: 'https://venta.gg', primary: true }]);
    }

    /**
     * Everything /connect/token can answer, turned into a screen. Every branch here is a response
     * the backend really returns; see docs/specs/sso.md §9.2.
     *
     * Returns true when the caller should stop - the journey has either continued or ended.
     */
    async function handleTokenFailure(result, into, { email } = {}) {
        const body = (result.text || '').toLowerCase();

        if (result.status === 0) {
            notify(into, 'danger', 'Could not reach venta. Check your connection and try again.');
            return true;
        }

        if (result.status === 423) {
            notify(into, 'warn', 'Too many incorrect passwords. This account is locked for about 15 minutes. '
                + 'If it was not you, reset your password when it unlocks.');
            return true;
        }

        if (result.status === 403 && body.includes('not verified')) {
            // Straight to the verification screen with the address filled in and a code on its way,
            // rather than an error that names a problem and offers nothing.
            const address = email || '';
            await call('GET', `${API}/user/generate-verification-code`, { query: { email: address } });
            go(`/verify?email=${encodeURIComponent(address)}&sent=1`);
            return true;
        }

        if (result.status === 403) {
            const box = notify(into, 'danger', 'This account cannot sign in. ');
            box.querySelector('.grow').append(link('Appeal or ask support', supportUrl('/appeal')));
            return true;
        }

        if (result.status === 401 && body.includes('mfa_required')) {
            // The second factor is collected on the sign-in form, so a grant that hits it from
            // anywhere else (Steam, a redeemed QR code) has to route there rather than reveal a
            // code field on a screen that has no password box above it.
            if (current === 'login') enterMfa();
            else finish('info', 'One more step',
                'This account has two-factor authentication turned on. Sign in with your password '
                + 'to enter the code.',
                [{ label: 'Sign in', href: signInHref(), primary: true }]);
            return true;
        }

        if (result.status === 401 && body.includes('mfa_invalid')) {
            notify(into, 'danger', 'That code was not right. Codes change every 30 seconds - '
                + 'check your authenticator and try the current one.');
            return true;
        }

        return false;
    }

    function supportUrl(path) {
        return `${location.protocol}//support.${location.hostname.split('.').slice(1).join('.') || location.hostname}${path}`;
    }

    // ══ Sign-in screen ═══════════════════════════════════════════════════════

    const login = {
        alert: $('#login-alert'),
        form: $('#login-form'),
        identity: $('#login-identity'),
        password: $('#login-password'),
        passwordField: $('#login-password-field'),
        mfaField: $('#login-mfa-field'),
        mfa: $('#login-mfa'),
        submit: $('#login-submit'),
    };

    let mfaMode = false;

    function enterMfa() {
        mfaMode = true;
        login.passwordField.hidden = true;
        login.mfaField.hidden = false;
        login.submit.textContent = 'Verify';
        login.mfa.value = '';
        login.mfa.focus();

        notify(login.alert, 'info', 'Enter the code from your authenticator app to finish signing in.');
    }

    function leaveMfa() {
        mfaMode = false;
        login.passwordField.hidden = false;
        login.mfaField.hidden = true;
        login.submit.textContent = 'Sign in';
    }

    async function submitPassword(event) {
        event.preventDefault();
        clearNotice(login.alert);

        const username = login.identity.value.trim();
        const password = login.password.value;

        if (!username || !password) {
            notify(login.alert, 'danger', 'Enter your username or email address and your password.');
            return;
        }

        if (mfaMode && !login.mfa.value.trim()) {
            notify(login.alert, 'danger', 'Enter the code from your authenticator app.');
            return;
        }

        busyButton(login.submit, 'Signing in...');

        const result = await call('POST', '/connect/token', {
            form: {
                grant_type: 'password',
                client_id: CLIENT_ID,
                scope: SCOPE,
                username,
                password,
                mfa_code: mfaMode ? login.mfa.value.trim() : null,
                device_type: 'Web',
            },
        });

        idleButton(login.submit);

        if (result.ok && result.data?.access_token) {
            stopQr();
            await establish(result.data.access_token, login.alert);
            return;
        }

        if (await handleTokenFailure(result, login.alert, { email: username })) return;

        // Everything left is a wrong credential, and it is answered identically whether the account
        // exists or not - the server pays the same cost either way, and the page must not undo that
        // by wording the two differently.
        notify(login.alert, 'danger', 'That username or password is not right.');
        login.password.select();
    }

    function setupLogin() {
        login.form.addEventListener('submit', submitPassword);

        $('#login-reveal').addEventListener('click', () => toggleReveal($('#login-reveal'), login.password));
        $$('[data-reveal]').forEach(button =>
            button.addEventListener('click', () => toggleReveal(button, document.getElementById(button.dataset.reveal))));

        $('#login-recovery').addEventListener('click', () => {
            login.mfa.setAttribute('maxlength', '12');
            login.mfa.value = '';
            login.mfa.focus();
            notify(login.alert, 'info', 'Enter one of the recovery codes you saved when you turned on '
                + 'two-factor authentication. Each one works once.');
        });

        $('#login-switch').addEventListener('click', async () => {
            await call('DELETE', `${API}/sso/session`);
            $('#login-account').hidden = true;
            login.identity.value = '';
            login.identity.focus();
        });

        $('#login-steam').addEventListener('click', startSteam);

        $('#qr-toggle').addEventListener('click', () => {
            const body = $('#qr-body');
            const open = body.hasAttribute('data-open');

            if (open) body.removeAttribute('data-open'); else body.setAttribute('data-open', '');
            $('#qr-toggle').setAttribute('aria-expanded', String(!open));

            if (!open && !qr.code) startQr();
        });

        $('#qr-refresh').addEventListener('click', startQr);
    }

    function toggleReveal(button, input) {
        const shown = input.type === 'text';
        input.type = shown ? 'password' : 'text';
        button.setAttribute('aria-pressed', String(!shown));
        button.setAttribute('aria-label', shown ? 'Show password' : 'Hide password');
        button.replaceChildren(icon(shown ? 'eye' : 'eye-slash'));
    }

    async function openLogin() {
        show('login');

        if (state.request) {
            if (state.request.clientName) {
                $('#login-eyebrow').textContent = `Continue to ${state.request.clientName}`;
                $('#login-eyebrow').hidden = false;
            }
            $('#login-sub').textContent = state.request.forceLogin
                ? 'This site asked you to sign in again.'
                : 'Use your venta account to continue.';

            if (state.request.loginHint) login.identity.value = state.request.loginHint;
        }

        const params = new URLSearchParams(location.search);
        if (params.get('error') === 'request_expired') {
            notify(login.alert, 'warn', 'That sign-in link expired before it was finished. '
                + 'Go back to the site you came from and try again.');
        }

        // "Continue as" is not offered here on purpose. Reaching the sign-in screen with a live
        // session means the session was refused or a fresh credential was demanded, so offering to
        // reuse it would be offering the thing that just failed. It is shown to say who the browser
        // is currently signed in as, and to get out of the way.
        const session = await currentSession();
        if (session) {
            $('#login-account').hidden = false;
            $('#login-account-name').textContent = session.username || session.email || 'Signed in';
            $('#login-account-initial').textContent = (session.username || session.email || '?').slice(0, 1);
            if (!login.identity.value) login.identity.value = session.username || '';
        }

        (login.identity.value ? login.password : login.identity).focus({ preventScroll: true });

        // Started on load on the wide layout, where the panel is visible; on narrow it waits for the
        // panel to be opened. A three-minute code minted for a panel nobody can see is a code that
        // expires unseen.
        if (window.matchMedia('(min-width: 901px)').matches) startQr();
    }

    // ══ QR pairing ═══════════════════════════════════════════════════════════

    const qr = {
        code: null,
        expiresAt: 0,
        poll: null,
        tick: null,
        redeeming: false,
    };

    function qrStatus(text, tone) {
        const node = $('#qr-status');
        node.textContent = text;
        if (tone) node.dataset.state = tone; else delete node.dataset.state;
    }

    function stopQr() {
        clearInterval(qr.poll);
        clearInterval(qr.tick);
        qr.poll = null;
        qr.tick = null;
    }

    async function startQr() {
        stopQr();
        qr.code = null;
        qr.redeeming = false;

        $('#qr-refresh').hidden = true;
        $('#qr-veil').hidden = false;
        $('#qr-image').hidden = true;
        qrStatus('Preparing a code...');

        const result = await call('POST', `${API}/qr-login/start`, {
            json: { deviceName: navigator.userAgent, deviceType: 'Web' },
        });

        if (!result.ok || !result.data?.code) {
            $('#qr-veil').hidden = true;
            qrStatus('Could not start a code. Sign in with your password instead.', 'denied');
            $('#qr-refresh').hidden = false;
            return;
        }

        qr.code = result.data.code;
        qr.expiresAt = Date.now() + (result.data.expiresInSeconds || 180) * 1000;

        const image = $('#qr-image');
        image.src = `${API}/qr-login/${encodeURIComponent(qr.code)}/svg`;
        image.alt = `QR code for pairing code ${qr.code}`;
        image.hidden = false;
        $('#qr-veil').hidden = true;
        $('#qr-code').textContent = qr.code;

        qrStatus('Open venta on your phone, then Settings › Scan QR code.');

        qr.tick = setInterval(tickQr, 1000);
        qr.poll = setInterval(pollQr, 1500);
        tickQr();
    }

    function tickQr() {
        const remaining = Math.max(0, qr.expiresAt - Date.now());
        const seconds = Math.ceil(remaining / 1000);

        $('#qr-timer').textContent = remaining > 0
            ? `Expires in ${Math.floor(seconds / 60)}:${String(seconds % 60).padStart(2, '0')}`
            : '';

        if (remaining <= 0) expireQr();
    }

    function expireQr() {
        stopQr();
        qr.code = null;
        $('#qr-image').hidden = true;
        $('#qr-refresh').hidden = false;
        qrStatus('This code expired. Get a new one whenever you are ready.', 'expired');
    }

    async function pollQr() {
        // A tab in the background is a tab nobody is holding a phone up to. Polling stops rather
        // than running all night; the code's own expiry still applies when the tab comes back.
        if (document.hidden || !qr.code || qr.redeeming) return;

        const result = await call('GET', `${API}/qr-login/status/${encodeURIComponent(qr.code)}`);

        if (result.status === 404) { expireQr(); return; }
        if (!result.ok) return;

        const status = result.data?.status;

        if (status === 'Scanned') {
            qrStatus('Scanned. Confirm the sign-in on your phone.', 'scanned');
            return;
        }

        if (status === 'Denied') {
            stopQr();
            qr.code = null;
            $('#qr-image').hidden = true;
            $('#qr-refresh').hidden = false;
            qrStatus('That sign-in was denied on your phone. Start a new code if it was not you.', 'denied');
            return;
        }

        if (status === 'Approved') {
            qr.redeeming = true;
            stopQr();
            qrStatus('Approved. Signing you in...');

            const code = qr.code;
            qr.code = null;

            const exchange = await call('POST', '/connect/token', {
                form: {
                    grant_type: 'urn:echo:params:oauth:grant-type:qr_login',
                    client_id: CLIENT_ID,
                    scope: SCOPE,
                    qr_code: code,
                },
            });

            if (exchange.ok && exchange.data?.access_token) {
                await establish(exchange.data.access_token, login.alert);
                return;
            }

            if (await handleTokenFailure(exchange, login.alert)) return;

            qrStatus('That code could not be used. Try a new one.', 'denied');
            $('#qr-refresh').hidden = false;
        }
    }

    // ══ Steam ════════════════════════════════════════════════════════════════

    async function startSteam() {
        rememberContext();

        const result = await call('GET', `${API}/authentication/steam/login/start`, {
            query: { returnUrl: `${location.origin}/steam` },
        });

        if (!result.ok || !result.data?.redirectUrl) {
            notify(login.alert, 'danger', 'Could not reach Steam just now. Sign in with your password instead.');
            return;
        }

        location.assign(result.data.redirectUrl);
    }

    async function openSteam() {
        show('steam');

        const params = new URLSearchParams(location.search);
        const status = params.get('status');
        const alert = $('#steam-alert');
        const actions = $('#steam-actions');

        const backToSignIn = { label: 'Back to sign in', href: signInHref() };

        if (status === 'ok' && params.get('ticket')) {
            $('#steam-title').textContent = 'Signing you in';
            $('#steam-sub').textContent = 'One moment.';

            const result = await call('POST', '/connect/token', {
                form: {
                    grant_type: 'urn:echo:params:oauth:grant-type:steam',
                    client_id: CLIENT_ID,
                    scope: SCOPE,
                    steam_ticket: params.get('ticket'),
                },
            });

            if (result.ok && result.data?.access_token) {
                await establish(result.data.access_token, alert);
                return;
            }

            if (await handleTokenFailure(result, alert)) return;

            finish('danger', 'That did not work',
                'The Steam sign-in could not be completed. It may have taken too long.',
                [{ label: 'Try again', href: signInHref(), primary: true }]);
            return;
        }

        if (status === 'no_account') {
            await openSteamTwoDoor(params.get('pending'));
            return;
        }

        if (status === 'linked') {
            finish('ok', 'Steam linked', 'Your Steam account is now attached to your venta account.',
                [{ label: 'Continue', href: signInHref(), primary: true }]);
            return;
        }

        if (status === 'already_linked') {
            $('#steam-title').textContent = 'That Steam account is taken';
            $('#steam-sub').textContent =
                'It is already linked to a different venta account. Sign in to that one, '
                + 'or unlink it there first.';
            actions.replaceChildren();
            actions.append(anchorButton('Sign in', signInHref(), true));
            actions.append(anchorButton('Ask support', supportUrl('/contact')));
            return;
        }

        if (status === 'forbidden') {
            $('#steam-title').textContent = 'This account cannot sign in';
            $('#steam-sub').textContent = 'The venta account linked to this Steam profile is suspended.';
            actions.replaceChildren(anchorButton('Appeal or ask support', supportUrl('/appeal'), true));
            return;
        }

        finish('danger', 'Steam sign-in failed',
            'Steam did not confirm who you are. That usually means the attempt took too long.',
            [{ label: 'Try again', href: signInHref(), primary: true }, backToSignIn]);
    }

    /**
     * The two doors somebody meets when they sign in with a Steam account nothing is linked to.
     *
     * Both produce an ordinary, fully formed account. Creating one still collects an email address
     * and a date of birth, because everything else on the account - password recovery, being told
     * you were banned, answering a data request - assumes they exist.
     */
    async function openSteamTwoDoor(pending) {
        const actions = $('#steam-actions');

        if (!pending) {
            finish('danger', 'That Steam sign-in expired',
                'Start again from the sign-in screen.',
                [{ label: 'Back to sign in', href: signInHref(), primary: true }]);
            return;
        }

        state.steamPending = pending;
        rememberContext();

        const profile = await call('GET', `${API}/authentication/steam/pending/${encodeURIComponent(pending)}`);

        if (profile.status === 404) {
            finish('danger', 'That Steam sign-in expired',
                'It is only valid for a short time. Start again from the sign-in screen.',
                [{ label: 'Back to sign in', href: signInHref(), primary: true }]);
            return;
        }

        const persona = profile.data?.personaName;
        const avatar = profile.data?.avatarUrl;

        if (avatar) {
            const image = $('#steam-avatar');
            image.src = avatar;
            image.hidden = false;
            $('#steam-crest-icon').hidden = true;
        }

        $('#steam-title').textContent = persona ? `Hello, ${persona}` : 'One more step';
        $('#steam-sub').textContent =
            'No venta account is linked to this Steam profile yet. Link it to an account you already '
            + 'have, or make a new one - either way you only do this once.';

        actions.replaceChildren();

        const signIn = el('button', 'btn primary', 'Sign in to link it');
        signIn.addEventListener('click', () => go(signInHref()));

        const create = el('button', 'btn', 'Create a new account');
        create.addEventListener('click', () => go('/register'));

        actions.append(signIn, create);
    }

    function anchorButton(label, href, primary) {
        const node = el('a', `btn ${primary ? 'primary' : ''}`, label);
        node.href = href;
        return node;
    }

    /** Back to the sign-in screen, keeping the parked request if there is one. */
    function signInHref() {
        const rq = state.rq || sessionStorage.getItem(STORE_RQ);
        return rq ? `/login?rq=${encodeURIComponent(rq)}` : '/login';
    }

    // ══ Consent ══════════════════════════════════════════════════════════════

    async function openConsent() {
        show('consent');

        if (!state.request) {
            finish('danger', 'That request expired',
                'Go back to the site you came from and try again.',
                [{ label: 'Back to sign in', href: '/login', primary: true }]);
            return;
        }

        const name = state.request.clientName || 'this site';

        $('#consent-title').textContent = `Allow ${name}?`;
        $('#consent-sub').textContent = `${name} wants to use your venta account.`;

        if (state.request.logoUri) {
            const image = $('#consent-logo');
            image.src = state.request.logoUri;
            image.hidden = false;
            $('#consent-logo-fallback').hidden = true;
        }

        const list = $('#consent-scopes');
        list.replaceChildren();

        (state.request.scopes || []).forEach(scope => {
            const row = el('li');
            row.append(icon('check'));

            const text = el('div', 'grow');
            text.append(el('strong', null, scope.title));
            if (scope.description) text.append(el('span', null, scope.description));
            row.append(text);

            list.append(row);
        });

        const session = await currentSession();
        $('#consent-account').textContent = session
            ? `Signing in as ${session.username || session.email}.`
            : '';

        $('#consent-allow').onclick = () => decideConsent(true);
        $('#consent-deny').onclick = () => decideConsent(false);
    }

    async function decideConsent(granted) {
        const result = await call('POST', `${API}/sso/consent`, {
            json: { rq: state.rq, granted },
        });

        if (result.ok && result.data?.redirectUrl) {
            location.assign(result.data.redirectUrl);
            return;
        }

        if (result.status === 401) {
            go(signInHref());
            return;
        }

        notify($('#consent-alert'), 'danger', 'That request is no longer valid. '
            + 'Go back to the site you came from and start again.');
    }

    // ══ Sign out ═════════════════════════════════════════════════════════════

    async function openLogout() {
        show('logout');

        const name = state.request?.clientName;
        $('#logout-sub').textContent = name
            ? `${name} asked to sign you out. This ends your session on this browser.`
            : 'This ends your session on this browser. Sites you are already signed in to keep '
              + 'their own sessions until they expire.';

        $('#logout-cancel').onclick = () => {
            if (state.request?.clientName) history.back();
            else go('/login');
        };

        $('#logout-confirm').onclick = async () => {
            if (state.rq) {
                const result = await call('POST', `${API}/sso/logout`, { json: { rq: state.rq, granted: true } });

                if (result.ok && result.data?.redirectUrl) {
                    location.assign(result.data.redirectUrl);
                    return;
                }
            }

            await call('DELETE', `${API}/sso/session`);

            finish('ok', 'Signed out', 'You are signed out of venta on this browser.',
                [{ label: 'Sign in again', href: '/login', primary: true }]);
        };
    }

    // ══ Create an account ════════════════════════════════════════════════════

    function setupRegister() {
        $('#register-form').addEventListener('submit', async event => {
            event.preventDefault();

            const alert = $('#register-alert');
            clearNotice(alert);

            const email = $('#register-email').value.trim();
            const username = $('#register-username').value.trim();
            const password = $('#register-password').value;
            const birthDate = $('#register-birthdate').value;

            if (!email || !username || !password || !birthDate) {
                notify(alert, 'danger', 'Fill in every field to create your account.');
                return;
            }

            busyButton($('#register-submit'), 'Creating...');

            const result = await call('POST', `${API}/authentication/register`, {
                json: { email, username, password, birthDate: new Date(birthDate).toISOString() },
            });

            idleButton($('#register-submit'));

            if (result.status === 202 || result.ok) {
                go(`/verify?email=${encodeURIComponent(email)}&new=1`);
                return;
            }

            // Validation problems come back as a problem document with the field messages in it, and
            // those are the ones worth showing verbatim - "Invalid email format" tells somebody what
            // to change, "Registration failed" does not.
            notify(alert, 'danger', problemText(result)
                || 'That account could not be created. Check the details and try again.');
        });
    }

    function problemText(result) {
        const data = result.data;
        if (!data) return null;

        if (typeof data.detail === 'string' && data.detail) return data.detail;
        if (typeof data.title === 'string' && data.errors) {
            const first = Object.values(data.errors).flat()[0];
            if (typeof first === 'string') return first;
        }
        if (typeof data === 'string') return data;

        return null;
    }

    // ══ Verify ═══════════════════════════════════════════════════════════════

    function setupVerify() {
        $('#verify-form').addEventListener('submit', async event => {
            event.preventDefault();

            const alert = $('#verify-alert');
            clearNotice(alert);

            const email = $('#verify-email').value.trim();
            const code = $('#verify-code').value.trim();

            if (!email || !code) {
                notify(alert, 'danger', 'Enter the address and the code we sent it.');
                return;
            }

            busyButton($('#verify-submit'), 'Checking...');
            const result = await call('GET', `${API}/user/verify-email`, { query: { email, code } });
            idleButton($('#verify-submit'));

            if (result.ok) {
                finish('ok', 'Address confirmed', 'Your account is ready. Sign in to continue.',
                    [{ label: 'Sign in', href: signInHref(), primary: true }]);
                return;
            }

            // The server answers every unusable code the same way on purpose - a wrong code, an
            // expired one and an unknown address are one response - so the page must not invent a
            // distinction it was not told about.
            notify(alert, 'danger', 'That code was not accepted. It may have expired, or been used. '
                + 'Codes last five minutes, and five wrong tries destroys one.');
        });

        $('#verify-resend').addEventListener('click', async () => {
            const email = $('#verify-email').value.trim();
            if (!email) {
                notify($('#verify-alert'), 'danger', 'Enter your email address first.');
                return;
            }

            await call('GET', `${API}/user/generate-verification-code`, { query: { email } });

            // Always the conditional phrasing. The endpoint answers 202 whether or not the account
            // exists, by design, and a page that says "sent" would undo that.
            notify($('#verify-alert'), 'ok', 'If that address needs confirming, a new code is on its way. '
                + 'It replaces any earlier one.');
        });
    }

    function openVerify() {
        show('verify');

        const params = new URLSearchParams(location.search);
        const email = params.get('email') || '';

        $('#verify-email').value = email;
        $('#verify-email-field').hidden = Boolean(email);

        $('#verify-sub').textContent = email
            ? `Enter the code we sent to ${email}.`
            : 'Enter the code we sent you.';

        if (params.get('new')) {
            notify($('#verify-alert'), 'ok', 'Account created. Confirm your address to finish - '
                + 'the code is in the welcome email.');
        } else if (params.get('sent')) {
            notify($('#verify-alert'), 'info', 'This account still needs its email address confirmed. '
                + 'If that address exists, we have just sent a code.');
        }

        $('#verify-code').focus({ preventScroll: true });
    }

    // ══ Forgot / reset ═══════════════════════════════════════════════════════

    function setupForgot() {
        $('#forgot-form').addEventListener('submit', async event => {
            event.preventDefault();

            const email = $('#forgot-email').value.trim();
            if (!email) {
                notify($('#forgot-alert'), 'danger', 'Enter the address on your account.');
                return;
            }

            busyButton($('#forgot-submit'), 'Sending...');
            await call('GET', `${API}/user/request-password-reset`, { query: { email } });
            idleButton($('#forgot-submit'));

            // Same conditional phrasing, same reason as the verification resend.
            finish('ok', 'Check your email',
                'If an account uses that address, a reset code is on its way. It lasts 15 minutes.',
                [{ label: 'Enter the code', href: `/reset?email=${encodeURIComponent(email)}`, primary: true },
                 { label: 'Back to sign in', href: '/login' }]);
        });
    }

    function setupReset() {
        $('#reset-form').addEventListener('submit', async event => {
            event.preventDefault();

            const alert = $('#reset-alert');
            clearNotice(alert);

            const email = $('#reset-email').value.trim();
            const code = $('#reset-code').value.trim();
            const newPassword = $('#reset-password').value;

            if (!email || !code || !newPassword) {
                notify(alert, 'danger', 'Fill in every field.');
                return;
            }

            busyButton($('#reset-submit'), 'Changing...');
            const result = await call('POST', `${API}/user/reset-password`, {
                json: { email, code, newPassword },
            });
            idleButton($('#reset-submit'));

            if (!result.ok) {
                notify(alert, 'danger', 'That code was not accepted. It may have expired, or been used. '
                    + 'Reset codes last 15 minutes, and five wrong tries destroys one.');
                return;
            }

            // A reset can leave encrypted history unreadable. Saying so here, plainly, is the whole
            // reason this endpoint returns a body at all - see ResetPasswordResultDto.
            const data = result.data || {};

            if (data.encryptedHistoryRecoverable === false) {
                finish('danger', 'Password changed, encrypted history lost',
                    'Your password is updated and you can sign in. Your end-to-end encrypted '
                    + 'conversations cannot be opened again: the key was sealed under the old password '
                    + 'and there was no recovery code to fall back on.',
                    [{ label: 'Sign in', href: signInHref(), primary: true }]);
                return;
            }

            if (data.masterKeyRewrapRequired) {
                finish('warn', 'Password changed - one thing left',
                    'Sign in on the venta app and unlock with your recovery code. That re-seals your '
                    + 'encryption key under the new password. Until you do, encrypted conversations '
                    + 'stay locked.',
                    [{ label: 'Sign in', href: signInHref(), primary: true }]);
                return;
            }

            finish('ok', 'Password changed', 'Sign in with your new password.',
                [{ label: 'Sign in', href: signInHref(), primary: true }]);
        });
    }

    function openReset() {
        show('reset');

        const email = new URLSearchParams(location.search).get('email');
        if (email) {
            $('#reset-email').value = email;
            $('#reset-code').focus({ preventScroll: true });
        }
    }

    // ══ Router ═══════════════════════════════════════════════════════════════

    function go(href) {
        location.assign(href);
    }

    async function route() {
        const params = new URLSearchParams(location.search);

        state.rq = params.get('rq') || sessionStorage.getItem(STORE_RQ);
        state.steamPending = sessionStorage.getItem(STORE_STEAM);
        state.request = await loadRequest(state.rq);

        if (params.get('rq')) sessionStorage.setItem(STORE_RQ, params.get('rq'));

        const path = location.pathname.replace(/\/+$/, '') || '/login';

        switch (path) {
            case '/consent': return openConsent();
            case '/logout': return openLogout();
            case '/steam': return openSteam();
            case '/register': return show('register');
            case '/verify': return openVerify();
            case '/forgot': return show('forgot');
            case '/reset': return openReset();
            default: return openLogin();
        }
    }

    // ── Boot ────────────────────────────────────────────────────────────────

    setupLogin();
    setupRegister();
    setupVerify();
    setupForgot();
    setupReset();

    $('#legal-support').href = supportUrl('/');

    // Polling is suspended while the tab is hidden, so a code that expired in the background is
    // resolved the moment somebody comes back to it rather than sitting there looking live.
    document.addEventListener('visibilitychange', () => {
        if (!document.hidden && current === 'login' && qr.code) tickQr();
    });

    route();
})();
