/*
 * Icon injection.
 *
 * Every icon is its own file under /assets/icons/. Nothing is inlined into the pages: the set comes
 * from primeicons (MIT), which is what the Alpine client uses, so the console and the client draw
 * from one source and an icon updated upstream is updated by replacing a file rather than by
 * hunting through markup.
 *
 * Usage:  <i data-icon="ban"></i>
 *         <i data-icon="check" class="ok"></i>
 *
 * The SVG is fetched once per name, parsed once, and cloned per use. Colour comes from
 * `currentColor` (see the stylesheet), so the same file works on any surface in either theme.
 *
 * Icons added to the DOM later - every list row on this page is rendered from JavaScript - are
 * picked up by calling `Icons.paint(root)` after rendering, or automatically by the MutationObserver
 * below. The observer is the safety net, not the mechanism: relying on it alone would repaint on
 * every unrelated DOM change.
 */
const Icons = (() => {
    const BASE = '/assets/icons';

    /** name -> Promise<SVGElement|null>. Cached as the promise, not the result, so twenty rows
     *  asking for the same icon in the same tick share one request rather than firing twenty. */
    const cache = new Map();

    function load(name) {
        if (cache.has(name)) return cache.get(name);

        const request = fetch(`${BASE}/${name}.svg`)
            .then(response => (response.ok ? response.text() : null))
            .then(text => {
                if (!text) return null;

                // Parsed as XML rather than assigned through innerHTML. These files ship inside the
                // image and are not user content, but an icon loader that pipes fetched text into
                // innerHTML is the shape of a bug worth never writing.
                const doc = new DOMParser().parseFromString(text, 'image/svg+xml');
                const svg = doc.querySelector('svg');
                if (!svg) return null;

                svg.setAttribute('aria-hidden', 'true');
                svg.setAttribute('focusable', 'false');
                return svg;
            })
            .catch(() => null);

        cache.set(name, request);
        return request;
    }

    function paint(root = document) {
        const targets = [...root.querySelectorAll('[data-icon]:not([data-icon-done])')];

        // querySelectorAll never returns the root itself, and the root *is* the new node when a
        // single <i data-icon> is appended. Missing that was the whole reason the first version of
        // this needed the caller to pass a parent.
        if (root.matches?.('[data-icon]:not([data-icon-done])')) targets.unshift(root);

        targets.forEach(async element => {
            const name = element.dataset.icon;
            if (!name) return;

            // Stamped before the await, not after: two paint() calls racing over the same element
            // would otherwise both pass the :not() filter and inject two copies.
            element.setAttribute('data-icon-done', '');

            const svg = await load(name);
            if (!svg) {
                element.removeAttribute('data-icon-done');
                return;
            }

            element.replaceChildren(svg.cloneNode(true));
        });
    }

    /** Waits for every icon currently on the page. Only used by the tests and the print view. */
    function ready() {
        return Promise.all([...cache.values()]);
    }

    if (document.readyState === 'loading') {
        document.addEventListener('DOMContentLoaded', () => paint());
    } else {
        paint();
    }

    new MutationObserver(mutations => {
        for (const mutation of mutations) {
            for (const node of mutation.addedNodes) {
                if (node.nodeType !== Node.ELEMENT_NODE) continue;

                if (node.matches?.('[data-icon]') || node.querySelector?.('[data-icon]')) paint(node);
            }
        }
    }).observe(document.documentElement, { childList: true, subtree: true });

    return { paint, ready, load };
})();
