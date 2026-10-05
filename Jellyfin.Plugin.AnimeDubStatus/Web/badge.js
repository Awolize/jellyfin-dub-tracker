(function () {
    'use strict';

    // Served next to index.html by the plugin's own middleware, so this stays valid
    // behind a Jellyfin base URL and needs no API client or authentication.
    var DATA_URL = 'anime-dub-status.json';

    var CARD_SELECTOR = '.card[data-id]';
    var ID_ATTRIBUTE = 'data-id';
    var BADGE_CLASS = 'anime-dub-badge';
    var OVERLAY_SELECTORS = ['.cardScalable', '.cardImageContainer', '.cardBox'];

    // Replaced by the label the server sends, which follows the tracked language.
    var DEFAULT_BADGE_TEXT = 'DUB';
    var REFRESH_MS = 5 * 60 * 1000;
    var DEBOUNCE_MS = 250;

    var dubbedIds = new Set();
    var badgeText = DEFAULT_BADGE_TEXT;
    var lastLoad = 0;
    var inFlight = null;
    var timer = null;

    var style = document.createElement('style');
    style.textContent =
        '.' + BADGE_CLASS + '{position:absolute;top:.4em;left:.4em;z-index:2;padding:.15em .5em;' +
        'border-radius:.3em;background:#f5b301;color:#000;font-size:.75em;font-weight:700;' +
        'letter-spacing:.03em;pointer-events:none;box-shadow:0 1px 3px rgba(0,0,0,.5)}';
    document.head.appendChild(style);

    function normalize(id) {
        return String(id || '').replace(/-/g, '').toLowerCase();
    }

    function loadDubbedIds() {
        if (inFlight) {
            return inFlight;
        }
        if (lastLoad && Date.now() - lastLoad < REFRESH_MS) {
            return Promise.resolve();
        }

        inFlight = fetch(DATA_URL, { cache: 'no-cache', credentials: 'same-origin' })
            .then(function (response) {
                return response.ok ? response.json() : null;
            })
            .then(function (payload) {
                // Accepts the current { label, ids } shape and a bare array from
                // an older server, so a rolling upgrade cannot break the badge.
                var ids = Array.isArray(payload) ? payload : payload && payload.ids;

                if (payload && !Array.isArray(payload) && typeof payload.label === 'string' && payload.label) {
                    badgeText = payload.label;
                }

                if (Array.isArray(ids)) {
                    dubbedIds = new Set(ids.map(normalize));
                    lastLoad = Date.now();
                }
            })
            .catch(function () {
                // Ignore; the next page change retries.
            })
            .then(function () {
                inFlight = null;
            });

        return inFlight;
    }

    function findOverlay(card) {
        for (var i = 0; i < OVERLAY_SELECTORS.length; i++) {
            var overlay = card.querySelector(OVERLAY_SELECTORS[i]);
            if (overlay) {
                return overlay;
            }
        }
        return card;
    }

    function apply() {
        document.querySelectorAll(CARD_SELECTOR).forEach(function (card) {
            var existing = card.querySelector('.' + BADGE_CLASS);
            var dubbed = dubbedIds.has(normalize(card.getAttribute(ID_ATTRIBUTE)));

            if (existing && !dubbed) {
                existing.remove();
                return;
            }
            if (existing) {
                if (existing.textContent !== badgeText) {
                    existing.textContent = badgeText;
                }
                return;
            }
            if (!dubbed) {
                return;
            }

            var host = findOverlay(card);
            if (getComputedStyle(host).position === 'static') {
                host.style.position = 'relative';
            }

            var badge = document.createElement('div');
            badge.className = BADGE_CLASS;
            badge.textContent = badgeText;
            host.appendChild(badge);
        });
    }

    function schedule() {
        if (timer) {
            return;
        }
        timer = setTimeout(function () {
            timer = null;
            loadDubbedIds().then(apply);
        }, DEBOUNCE_MS);
    }

    function start() {
        if (!document.body) {
            document.addEventListener('DOMContentLoaded', start, { once: true });
            return;
        }

        new MutationObserver(schedule).observe(document.body, { childList: true, subtree: true });
        schedule();
    }

    start();
})();
