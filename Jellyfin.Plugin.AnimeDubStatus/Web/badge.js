(function () {
    'use strict';

    var TAG = 'English Dub Available';
    var REFRESH_MS = 5 * 60 * 1000;
    var dubbedIds = new Set();
    var lastLoad = 0;
    var inFlight = null;
    var timer = null;

    var style = document.createElement('style');
    style.textContent =
        '.anime-dub-badge{position:absolute;top:.4em;left:.4em;z-index:2;padding:.15em .5em;' +
        'border-radius:.3em;background:#f5b301;color:#000;font-size:.75em;font-weight:700;' +
        'letter-spacing:.03em;pointer-events:none;box-shadow:0 1px 3px rgba(0,0,0,.5)}';
    document.head.appendChild(style);

    function normalize(id) {
        return String(id || '').replace(/-/g, '').toLowerCase();
    }

    function loadDubbedIds() {
        var client = window.ApiClient;
        if (!client || !client.getCurrentUserId || !client.getCurrentUserId()) {
            return Promise.resolve();
        }
        if (inFlight) {
            return inFlight;
        }
        if (lastLoad && Date.now() - lastLoad < REFRESH_MS) {
            return Promise.resolve();
        }

        inFlight = client.getItems(client.getCurrentUserId(), {
            Recursive: true,
            IncludeItemTypes: 'Series',
            Tags: TAG,
            EnableImages: false,
            EnableUserData: false
        }).then(function (result) {
            dubbedIds = new Set(result.Items.map(function (item) { return normalize(item.Id); }));
            lastLoad = Date.now();
        }).catch(function () {
            // ignore, it will retry on the next page change
        }).then(function () {
            inFlight = null;
        });

        return inFlight;
    }

    function apply() {
        document.querySelectorAll('.card[data-id]').forEach(function (card) {
            var existing = card.querySelector('.anime-dub-badge');
            var dubbed = dubbedIds.has(normalize(card.getAttribute('data-id')));

            if (existing && !dubbed) {
                existing.remove();
                return;
            }
            if (existing || !dubbed) {
                return;
            }

            var host = card.querySelector('.cardScalable') || card;
            if (getComputedStyle(host).position === 'static') {
                host.style.position = 'relative';
            }

            var badge = document.createElement('div');
            badge.className = 'anime-dub-badge';
            badge.textContent = 'EN DUB';
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
        }, 250);
    }

    new MutationObserver(schedule).observe(document.body, { childList: true, subtree: true });
    schedule();
})();