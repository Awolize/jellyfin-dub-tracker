(function () {
    'use strict';

    // Served next to index.html by the plugin's own middleware, so this stays valid
    // behind a Jellyfin base URL and needs no API client or authentication.
    var DATA_URL = 'anime-dub-status.json';
    var TRACKS_URL = 'anime-dub-status-tracks.json';

    // Any item element, not just posters: a season page may list episodes as rows.
    var CARD_SELECTOR = '[data-id]';
    var ID_ATTRIBUTE = 'data-id';
    var BADGE_CLASS = 'anime-dub-badge';
    var OVERLAY_SELECTORS = ['.cardScalable', '.cardImageContainer', '.cardBox', '.listItemImage'];

    // Jellyfin also puts data-id on page level containers, which are not items and have
    // no sensible corner to hang a badge on.
    var OVERLAY_QUERY = '.cardScalable, .cardImageContainer, .cardBox, .cardImage, .listItemImage';

    // Replaced by the label the server sends, which follows the tracked language and kind.
    var DEFAULT_BADGE_TEXT = 'DUB';
    var REFRESH_MS = 60 * 1000;
    var DEBOUNCE_MS = 250;

    var GREEN = '#4caf50';
    var YELLOW = '#f5b301';
    var NEUTRAL = '#607d8b';
    var MISSING = '#d32f2f';

    var dubbedIds = new Set();
    var coverage = new Map();
    var libraryMissing = new Set();
    var measured = false;
    var badgeText = DEFAULT_BADGE_TEXT;

    var seasonPercent = new Map();
    var missingEpisodes = new Set();
    var lastDetailId = null;
    var lastFetchedId = null;

    // A detail page for a playable item need not carry that item's id anywhere, so the
    // badge for the page itself goes beside its title instead.
    var DETAIL_TARGETS = [
        '.detailPagePrimaryContainer .itemName',
        '.detailPagePrimaryContainer',
        '.itemName',
        '.nameContainer'
    ];
    var detailBadge = null;
    var detailBadgeTarget = null;

    var lastLoad = 0;
    var inFlight = null;
    var detailInFlight = null;
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
                libraryMissing = new Set();

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

                    // Missing episodes for the whole library, which is what lets the home
                    // page's Next Up row be marked without a per-page request.
                    if (Array.isArray(payload.missing)) {
                        libraryMissing = new Set(payload.missing.map(normalize));
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

    // Which item's page is open, from Jellyfin's hash route.
    function currentDetailId() {
        var hash = window.location.hash || '';
        if (hash.indexOf('/details') < 0) {
            return null;
        }

        var separator = hash.indexOf('?');
        if (separator < 0) {
            return null;
        }

        var id = new URLSearchParams(hash.slice(separator + 1)).get('id');
        return id ? normalize(id) : null;
    }

    // What kind of item the page is showing, read off the controls that carry its id.
    function openItemType(openId) {
        var elements = document.querySelectorAll('[data-id][data-type]');

        for (var i = 0; i < elements.length; i++) {
            if (normalize(elements[i].getAttribute(ID_ATTRIBUTE)) === openId) {
                return elements[i].getAttribute('data-type');
            }
        }

        return null;
    }

    // An episode or season page only describes that one item, so the cards for its
    // siblings would never be judged. The parent series describes all of them, so ask
    // for that instead. A series page asks for itself, to avoid picking up an unrelated
    // series from a More Like This row.
    function resolveAggregateId(openId) {
        if (openItemType(openId) === 'Series') {
            return openId;
        }

        var parent = document.querySelector('.parentName [data-type="Series"][data-id]');
        if (parent) {
            var parentId = normalize(parent.getAttribute(ID_ATTRIBUTE));
            if (parentId) {
                return parentId;
            }
        }

        return openId;
    }

    // One request per page drives both the season fills and the episode marks: for a
    // series it lists every episode with its season, for a season just its own.
    function loadDetail() {
        var openId = currentDetailId();
        lastDetailId = openId;

        if (!openId) {
            seasonPercent = new Map();
            missingEpisodes = new Set();
            lastFetchedId = null;
            return Promise.resolve();
        }

        var target = resolveAggregateId(openId);

        if (target === lastFetchedId) {
            return detailInFlight || Promise.resolve();
        }

        lastFetchedId = target;

        detailInFlight = fetch(TRACKS_URL + '?series=' + target, { cache: 'no-cache', credentials: 'same-origin' })
            .then(function (response) {
                return response.ok ? response.json() : null;
            })
            .then(function (data) {
                var seasons = new Map();
                var missing = new Set();
                var perSeason = new Map();

                if (typeof data.labeled === 'string' && data.labeled) {
                    badgeText = data.labeled;
                }

                (data && Array.isArray(data.episodes) ? data.episodes : []).forEach(function (episode) {
                    var episodeId = normalize(episode.id);

                    // An episode that has not aired cannot be missing anything yet.
                    if (!episode.unaired && !episode.hasTrack) {
                        missing.add(episodeId);
                    }

                    if (!episode.seasonId) {
                        return;
                    }

                    var seasonId = normalize(episode.seasonId);
                    var counts = seasons.get(seasonId) || { released: 0, present: 0 };

                    if (!episode.unaired) {
                        counts.released++;
                        if (episode.hasTrack) {
                            counts.present++;
                        }
                    }

                    seasons.set(seasonId, counts);
                });

                seasons.forEach(function (counts, seasonId) {
                    perSeason.set(
                        seasonId,
                        counts.released === 0
                            ? null
                            : Math.round(100 * counts.present / counts.released));
                });

                seasonPercent = perSeason;
                missingEpisodes = missing;
            })
            .catch(function () {
                // Ignore; the next page change retries.
            })
            .then(function () {
                detailInFlight = null;
            });

        return detailInFlight;
    }

    function measuredState(percent, label) {
        if (percent >= 100) {
            return {
                key: 'full',
                text: label,
                background: GREEN,
                color: '#000',
                title: label + ': every released episode is in your library'
            };
        }

        if (percent <= 0) {
            return {
                key: 'none',
                text: label,
                background: YELLOW,
                color: '#000',
                title: label + ': it exists, but none of it is in your library'
            };
        }

        return {
            key: 'partial',
            text: label + ' ' + percent + '%',
            background: 'linear-gradient(90deg,' + GREEN + ' 0 ' + percent + '%,' + YELLOW + ' ' + percent + '% 100%)',
            color: '#000',
            title: label + ': ' + percent + '% of released episodes are in your library'
        };
    }

    // A season row on a show page, an episode that is missing the track, or a card for a
    // series whose dub the library only partly holds.
    function stateFor(id) {
        if (seasonPercent.has(id)) {
            var seasonValue = seasonPercent.get(id);
            return seasonValue === null ? null : measuredState(seasonValue, badgeText);
        }

        if (missingEpisodes.has(id) || libraryMissing.has(id)) {
            return {
                key: 'missing-episode',
                text: 'NO ' + badgeText,
                background: MISSING,
                color: '#fff',
                title: badgeText + ': this episode does not carry that track'
            };
        }

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

        return measuredState(coverage.has(id) ? coverage.get(id) : 0, badgeText);
    }

    // An item element, as opposed to a page container or an action control that happens
    // to carry data-id. Jellyfin marks up its buttons and links the same way, which is
    // how a badge ended up sitting on Mark as played.
    function looksLikeCard(element) {
        var className = String(element.className || '');

        if (/(^|\s)(card|listItem)(\s|$)/.test(className)) {
            return true;
        }

        return element.querySelector(OVERLAY_QUERY) !== null;
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

    function createChip(state) {
        var badge = document.createElement('div');
        badge.className = BADGE_CLASS;
        badge.setAttribute('data-state', state.key);

        // Flowing with whatever it sits beside, rather than floating over it.
        badge.style.position = 'static';
        badge.style.display = 'inline-block';
        badge.style.margin = '0 .5em';
        badge.style.verticalAlign = 'middle';

        badge.style.background = state.background;
        badge.style.color = state.color;
        badge.textContent = state.text;
        badge.title = state.title;
        badge.setAttribute('aria-label', state.title);

        return badge;
    }

    // A detail page's own id usually lives on a visible control, such as the play state
    // button. A text container with that id can exist without being rendered at all,
    // which is how a badge ends up in the DOM but invisible.
    function findControlAnchor(openId) {
        var elements = document.querySelectorAll('[data-id]');

        for (var i = 0; i < elements.length; i++) {
            var element = elements[i];
            if (normalize(element.getAttribute(ID_ATTRIBUTE)) !== openId) {
                continue;
            }

            var className = String(element.className || '');
            if (/detailButton|btnPlaystate|itemAction/.test(className) && element.parentElement) {
                return element;
            }
        }

        return null;
    }

    function removeDetailBadge() {
        if (detailBadge) {
            detailBadge.remove();
            detailBadge = null;
            detailBadgeTarget = null;
        }
    }

    function injectDetailBadge(state, openId) {
        removeDetailBadge();

        // Beside a control that is definitely rendered, if there is one.
        var anchor = openId ? findControlAnchor(openId) : null;
        if (anchor && anchor.parentElement) {
            var controlChip = createChip(state);
            anchor.parentElement.insertBefore(controlChip, anchor);
            detailBadge = controlChip;
            detailBadgeTarget = 'control:' + anchor.tagName + '.'
                + String(anchor.className || '').split(' ').filter(Boolean).slice(0, 2).join('.');
            return;
        }

        for (var i = 0; i < DETAIL_TARGETS.length; i++) {
            var host = document.querySelector(DETAIL_TARGETS[i]);
            if (!host) {
                continue;
            }

            var badge = createChip(state);

            // After a title reads naturally; inside a whole block it would land at the
            // bottom of that block, next to the action buttons.
            var hostClass = String(host.className || '');
            if (/itemName|nameContainer/.test(hostClass) || /^(H1|H2|H3)$/.test(host.tagName)) {
                host.appendChild(badge);
            } else {
                host.insertBefore(badge, host.firstChild);
            }

            detailBadge = badge;
            detailBadgeTarget = DETAIL_TARGETS[i];
            return;
        }
    }

    function apply() {
        var decorated = new Set();

        document.querySelectorAll(CARD_SELECTOR).forEach(function (card) {
            var id = normalize(card.getAttribute(ID_ATTRIBUTE));

            // Nested wrappers can repeat an id; decorate only the first, outermost one.
            if (decorated.has(id)) {
                return;
            }

            // Page level containers carry data-id too, and have no corner worth using.
            if (!looksLikeCard(card)) {
                return;
            }

            // Jellyfin keeps previously visited pages in the DOM, hidden. Their elements
            // come first in document order, so without this check they claim an id and the
            // page actually on screen never gets a badge.
            if (card.getClientRects().length === 0) {
                var stale = card.querySelector('.' + BADGE_CLASS);
                if (stale) {
                    stale.remove();
                }
                return;
            }

            var state = stateFor(id);
            var existing = card.querySelector('.' + BADGE_CLASS);

            if (!state) {
                if (existing) {
                    existing.remove();
                }
                return;
            }

            decorated.add(id);

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

        // The page's own item, when nothing on it carries that item's id.
        var openId = lastDetailId;
        var openState = openId && !decorated.has(openId) ? stateFor(openId) : null;

        if (openState) {
            injectDetailBadge(openState, openId);
        } else {
            removeDetailBadge();
        }
    }

    function schedule() {
        if (timer) {
            return;
        }
        timer = setTimeout(function () {
            timer = null;
            Promise.all([loadDubbedIds(), loadDetail()]).then(apply);
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

    // A one-call explanation of what the script currently believes and what it can see,
    // so a page that behaves unexpectedly can be diagnosed without guessing.
    window.animeDubStatus = {
        report: function () {
            var items = document.querySelectorAll(CARD_SELECTOR);

            return {
                label: badgeText,
                measured: measured,
                openItem: lastDetailId,
                fetchTarget: lastFetchedId,
                taggedSeries: dubbedIds.size,
                seasonsKnown: seasonPercent.size,
                episodesMissing: missingEpisodes.size,
                libraryMissing: libraryMissing.size,
                elementsWithDataId: items.length,
                badgesDrawn: document.querySelectorAll('.' + BADGE_CLASS).length,
                detailBadgeTarget: detailBadgeTarget,

                // Where each missing episode's id actually appears, or that it does not.
                missingEpisodeTargets: (function () {
                    var elements = document.querySelectorAll('[data-id]');
                    var out = [];

                    missingEpisodes.forEach(function (id) {
                        var found = null;

                        for (var i = 0; i < elements.length; i++) {
                            if (normalize(elements[i].getAttribute(ID_ATTRIBUTE)) === id) {
                                found = elements[i];
                                break;
                            }
                        }

                        out.push(id.slice(-8) + ' -> ' + (found
                            ? found.tagName + '.' + String(found.className || '').split(' ').filter(Boolean).slice(0, 2).join('.')
                            : 'NOT IN DOM'));
                    });

                    return out;
                })(),

                // What the elements carrying data-id actually are, most common first.
                classHistogram: (function () {
                    var counts = {};

                    Array.prototype.forEach.call(items, function (element) {
                        var key = element.tagName + '.'
                            + String(element.className || '').split(' ').filter(Boolean).slice(0, 2).join('.');
                        counts[key] = (counts[key] || 0) + 1;
                    });

                    return Object.keys(counts)
                        .map(function (key) { return key + ' x' + counts[key]; })
                        .sort()
                        .slice(0, 20);
                })(),
                samples: Array.prototype.slice.call(items, 0, 12).map(function (element) {
                    return element.tagName
                        + '.' + String(element.className || '').split(' ').filter(Boolean).slice(0, 2).join('.')
                        + ' [' + String(element.getAttribute(ID_ATTRIBUTE)).slice(-6) + ']';
                })
            };
        }
    };

    start();
})();
