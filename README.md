# Jellyfin Anime Dub Tracker

A Jellyfin plugin that badges anime in the web UI when a dub exists, using data from
[MyDubList](https://mydublist.com).

Series that have an English dub are tagged `English Dub Available`, and a small badge is
drawn onto their cards in Jellyfin Web.

## Status

Working against Jellyfin 12.1. Verified in production:

- the badge script is injected into `/web/index.html` on every page load
- the dubbed-series list is served from the plugin with ETag revalidation
- the tag query returns the library's dubbed series

## How it works

Two independent halves, which is why the feature survives Jellyfin upgrades.

### 1. Tagging (server, cached)

`DubTagger` walks the library and adds or removes the tag `English Dub Available` on
series whose `MyAnimeList` or `AniList` provider ID appears in the MyDubList dataset.
`DubDataService` downloads and caches that dataset (`dubbed_english.json`) plus the
AniList-to-MAL mapping, using ETags and a validated write-then-swap cache under the
plugin's data directory. `UpdateDubDataTask` runs it at startup and every 24 hours.

### 2. Badging (web UI, per request)

Jellyfin offers no supported way to add JavaScript to its web client, and the web
directory is often immutable (a read-only Nix store path, for example), so the plugin
does not touch files on disk. Instead `WebUiInjectionMiddleware`, installed by
`WebUiInjectionStartupFilter` via `IPluginServiceRegistrator`, rewrites the response:

```
GET /web/index.html   → <script id="anime-dub-status-script" src="anime-dub-status.js" defer></script>
GET /web/anime-dub-status.js    → the badge script (embedded resource)
GET /web/anime-dub-status.json  → ["<series guid>", …]
```

The script tag is inserted before the document's final `</body>` and uses a **relative**
URL, so it keeps working when Jellyfin is served under a base URL. Responses carry an
ETag with `Cache-Control: no-cache`, and conditional requests are handled by the plugin
rather than passed downstream, so a `304` can never skip the rewrite.

`DubStatusIndex` caches the tagged-series list for five minutes and is invalidated
whenever tagging completes.

### Why not File Transformation?

Earlier work used the [File Transformation](https://github.com/IAmParadox27/jellyfin-plugin-file-transformation)
plugin's `PluginInterface.RegisterTransformation` API. That borrowed a third-party
plugin's undocumented internals, required Newtonsoft at compile time, and made
File Transformation a hard dependency. The current design uses only ASP.NET Core's
`IStartupFilter` and Jellyfin's own plugin API, so it has **no third-party dependencies**
at runtime and degrades to "no badge" rather than breaking the web UI.

## Install

Add this repository to Jellyfin under **Dashboard → Plugins → Repositories**:

```
https://raw.githubusercontent.com/Awolize/jellyfin-dub-tracker/master/manifest.json
```

Then install **Anime Dub Status** from the catalog and restart Jellyfin. Plugins are
loaded at startup, so a restart is always required, however the files got there.

## Configuration

There is currently **no user configuration**. The tag name, dataset language, update
interval, badge text and target item types are all hardcoded; see *Roadmap*. The plugin's
settings page is still the unmodified Jellyfin template and does not work — tracked as
the first item on the roadmap.

## Troubleshooting

```bash
# Did the injection hook install, and is it rewriting documents?
sudo journalctl -u jellyfin -b | grep -i "Anime Dub Status"

# Is the script served? (404 means the middleware is not in front of static files)
curl -sI http://<jellyfin>/web/anime-dub-status.js

# Is the tag in the served document?
curl -s http://<jellyfin>/web/ | grep -o anime-dub-status-script

# Is there data? An empty array means no series are tagged.
curl -s http://<jellyfin>/web/anime-dub-status.json
```

If the tag is present but no badges appear, the issue is client-side — check
`document.querySelectorAll('.anime-dub-badge').length` in the browser console. The badge
selector depends on Jellyfin Web's card markup (`.card[data-id]` plus an overlay element),
which is the most fragile part of the design and will change eventually.

Two log lines tell you where you stand: `installing its Jellyfin Web UI injection
middleware` at startup, then `is injecting its badge script into Jellyfin Web` on the
first rewritten document. If the first appears without the second, the middleware is not
seeing `/web/` responses.

The script is also served at `/AnimeDubStatus/badge.js`, which is useful for testing the
client by hand.

## Data sources and attribution

English dub data comes from **[MyDubList](https://mydublist.com)** by
[Joelis57](https://github.com/Joelis57/MyDubList), licensed
**[CC BY 4.0](https://creativecommons.org/licenses/by/4.0/)**. The dataset carries its own
attribution metadata (`_attribution`, `_license`, `_origin`); that attribution is
reproduced here and in the source, as the licence requires. If you redistribute this
plugin or its derived data, keep the attribution.

## Roadmap

The plugin is currently narrower than it needs to be: one language, one track kind
(dubbed audio), one media type (series), one data source. The natural generalisation is a
`(language, kind)` pair where `kind` is `Dub` or `Sub`, giving tags such as
`German Sub Available`, with availability supplied by pluggable providers.

### Languages

MyDubList already publishes about twenty languages under
`dubs/confidence/<tier>/dubbed_<language>.json`, including `dubbed_german.json`, and the
same file also exists at confidence tiers `very-high`, `high`, `normal` and `low`
(4275 / 4631 / 4999 / 5657 titles respectively). Supporting another language for anime is
therefore close to a configuration change rather than a new integration.

### Subtitles, and non-anime TV

There is no bulk dataset equivalent to MyDubList for general TV, so providers split into
two shapes — bulk datasets and per-title queries — and conflating them would be a mistake:

- **[OpenSubtitles](https://api.opensubtitles.com/api/v1)** answers "does a German
  subtitle exist for this episode?" directly:
  `GET /subtitles?imdb_id=tt…&languages=de&type=episode`. Free API key, aggressively
  rate-limited, so results must be cached per title for a long time.
- **[The Dubbing Database](https://dubdb.fandom.com)** is a community wiki whose pages are
  named per language (`Friends (German)`), covering live-action TV rather than only anime.
  It exposes a working MediaWiki API (`/api.php?action=query&list=search`), so it is
  machine-readable, but coverage and accuracy are community-driven.
- **[Deutsche Synchronkartei](https://www.synchronkartei.de)** is the authoritative German
  dub database, but publishes no API; using it would mean scraping.
- **TMDB and TheTVDB do not answer this question.** Their language fields describe
  original/spoken language and metadata localisation, not which audio or subtitle tracks
  exist. They are useful as the identity layer for resolving provider IDs.

Per-title providers also need a different cost model from bulk ones: one HTTP request per
series, quota-limited, cached for weeks, and probably opt-in per library.

### Other known gaps

- **Movies and OVAs are not covered** — both `DubTagger` and `DubStatusIndex` query
  `BaseItemKind.Series` only.
- **Only MAL and AniList provider IDs are matched**, so items identified only by TVDB,
  TMDB or IMDb never qualify.
- **Startup always rescans the whole library**: `UpdateDubDataTask` discards the
  "anything new downloaded" result from `DubDataService.UpdateAsync` and calls
  `DubTagger.ApplyAsync` unconditionally.
- **No retry or backoff** when fetching datasets, and a dub failure skips the mapping
  update.
- **The badge has no accessibility semantics** (`aria-label`/`title`) and its position is
  hardcoded to the top-left corner.

## Development

```bash
dotnet build -c Release
```

| Path | Purpose |
| --- | --- |
| `Services/DubDataService.cs` | Downloads, caches and queries the MyDubList data |
| `Services/DubTagger.cs` | Applies the tag to library series |
| `Services/DubStatusIndex.cs` | Cached tagged-series list served to the web client |
| `Services/WebUiInjectionMiddleware.cs` | Injects the script and serves the web assets |
| `Services/WebTransformation.cs` | The `index.html` splice, as a pure function |
| `Web/badge.js` | The client script (embedded in the DLL) |
| `Api/BadgeController.cs` | Manual-testing route for the script |

Releases are built by GitHub Actions on a `v*` tag, which zips the plugin DLL and updates
`manifest.json`.

## License

GPL-3.0. Dub data © MyDubList, CC BY 4.0.
