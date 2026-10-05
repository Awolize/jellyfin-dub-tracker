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
    var REFRESH_MS = 60 * 1000;
    var DEBOUNCE_MS = 250;

    var GREEN = '#4caf50';
    var YELLOW = '#f5b301';
    var NEUTRAL = '#607d8b';

    var dubbedIds = new Set();
    var coverage = new Map();
    var measured = false;
    var badgeText = DEFAULT_BADGE_TEXT;
    var lastLoad = 0;
    var inFlight = null;
    var timer = null;

    var style = document.createElement('style');
    style.textContent =
        '.' + BADGE_CLASS + '{position:absolute;top:.4em;left:.4em;z-index:2;padding:.15em .5em;' +
        'border-radius:.3em;font-size:.75em;font-weight:700;letter-spacing:.03em;' +
        'pointer-events:none;box-shadow:0 1px 3px rgba(0,0,0,.5)}';
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
                // A bare array is the older payload shape and carries no coverage.
                var ids = Array.isArray(payload) ? payload : payload && payload.ids;

                coverage = new Map();

                if (payload && !Array.isArray(payload)) {
                    if (typeof payload.label === 'string' && payload.label) {
                        badgeText = payload.label;
                    }

                    measured = payload.measured === true;

                    if (payload.covered) {
                        Object.keys(payload.covered).forEach(function (key) {
                            coverage.set(normalize(key), payload.covered[key]);
                        });
                    }
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

    // The fill is the share of released episodes the library holds; the colour separates
    // "not looked yet" from "none of it" from "all of it".
    function stateFor(id) {
        if (!dubbedIds.has(id)) {
            return null;
        }

        if (!measured) {
            return {
                key: 'unknown',
                text: badgeText,
                background: NEUTRAL,
                color: '#fff',
                title: badgeText + ': how much of it you have has not been measured yet'
            };
        }

        var percent = coverage.has(id) ? coverage.get(id) : 0;

        if (percent >= 100) {
            return {
                key: 'full',
                text: badgeText,
                background: GREEN,
                color: '#000',
                title: badgeText + ': every released episode is in your library'
            };
        }

        if (percent <= 0) {
            return {
                key: 'none',
                text: badgeText,
                background: YELLOW,
                color: '#000',
                title: badgeText + ': the dub exists, but none of it is in your library'
            };
        }

        return {
            key: 'partial',
            text: badgeText + ' ' + percent + '%',
            background: 'linear-gradient(90deg,' + GREEN + ' 0 ' + percent + '%,' + YELLOW + ' ' + percent + '% 100%)',
            color: '#000',
            title: badgeText + ': ' + percent + '% of released episodes are in your library'
        };
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
            var state = stateFor(normalize(card.getAttribute(ID_ATTRIBUTE)));
            var existing = card.querySelector('.' + BADGE_CLASS);

            if (!state) {
                if (existing) {
                    existing.remove();
                }
                return;
            }

            // Only rebuild when something actually changed, so the observer stays cheap.
            if (existing
                && existing.getAttribute('data-state') === state.key
                && existing.textContent === state.text) {
                return;
            }

            if (existing) {
                existing.remove();
            }

            var host = findOverlay(card);
            if (getComputedStyle(host).position === 'static') {
                host.style.position = 'relative';
            }

            var badge = document.createElement('div');
            badge.className = BADGE_CLASS;
            badge.setAttribute('data-state', state.key);
            badge.style.background = state.background;
            badge.style.color = state.color;
            badge.textContent = state.text;
            badge.title = state.title;
            badge.setAttribute('aria-label', state.title);
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
