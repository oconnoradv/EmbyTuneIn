# TuneIn

## Configured browsing

Open the native TuneIn plugin settings page as an Emby administrator. Enable
**configured browsing after saving**, choose categories, and save. Until enabled,
the channel is empty and makes no catalogue requests. Opening settings or the
channel root and saving settings never crawl TuneIn or start playback.

Music, By Language, and By Location require at least one enabled saved selection.
Local Radio combines saved cities/regions and optional coordinates. My Favorites
requires a TuneIn username. Talk, Sports, and Podcasts have independent switches.
Disabled categories and selections cannot be opened or played via old item IDs,
including music, talk, and sports nested beneath By Language.

Use **Load root**, **Expand**, or **Refresh current branch** to request selector
labels explicitly. Filtering searches only already-loaded labels. Add choices,
enable or disable them individually, and save. These administrator-only selectors
do not resolve streams or recursively crawl the catalogue; TuneIn can include
station metadata in a requested branch, which is reported on the page. Branch
ancestry comes from loaded provider responses, not a caller-supplied path. After
a server restart or cache eviction, load selector ancestry from the root again.

Coordinates are optional and used only when their checkbox is enabled. Supply
finite decimal latitude (-90 through 90) and longitude (-180 through 180),
separated by a comma; the server normalizes them using invariant culture. Invalid
coordinates are rejected on save when enabled. Saved selections are limited to
100 per selector.

## Configuration and migration

The upstream plugin GUID (`10b82431-f939-4507-a8e2-9509dd3fd6ae`), assembly name,
configuration filename, native settings controller, artwork, and assembly/file
version (`1.2.8.0`) are retained. An existing configuration retains `Username`
and `LatLon`, but schema 0 leaves browsing and all categories disabled. No legacy
username or coordinate value enables browsing automatically. Saving the page
writes schema 1 and the explicit choices; leaving the gate unchecked keeps
browsing disabled.

The first saved configuration persists a server-generated navigation signing key
in the existing plugin configuration. Normal saves retain that server-owned key;
do not delete or regenerate it during migration. Changing browsing configuration
invalidates existing signed IDs and the channel cache key. Return to the channel
root after saving; legacy `subcat_`, `preset_`, and `stream_` URL IDs are not
accepted. Previously imported library items can remain visible until Emby
refreshes its channel metadata, but stale IDs are rejected at browse/playback
time. Configuration invalidation stays local: Save does not start a refresh,
raise a channel-change event, or alter any task schedule.

Navigation is bounded to 4096 payload bytes and 6000 ID characters, HMAC-signed
and bound to a configuration snapshot. Menu links retain their originating
category, selection, and nested language/music scope; known ancestor and
cross-category links are excluded. Language filters are reapplied to nested
requests, while music under a language starts from saved music choices instead
of an unrestricted hub. Only catalogue-generated station identifiers reach
the upstream playback resolver.

## Scoped native refresh

One visible **Refresh TuneIn** task is registered under **Internet Channels**,
with no default triggers. Run, cancel, or schedule it using Emby's native
Scheduled Tasks interface. It visits only enabled groups and enabled saved
selections. With no configured scopes it completes locally without resolving a
channel or contacting TuneIn. It never queues **Refresh Internet Channels** or
changes that task's triggers. Existing host triggers remain independent.

Each category has two stages. Discovery first builds a complete, bounded staged
tree using the same signed navigation and language/location/music restrictions
as interactive browsing. Only then does native Emby import that category through
`IChannelManager.RefreshChannelContent(channel, 8, categoryFolderId, token)`.
Native root queries during import see only the current category. Import queries
cannot fetch extra provider pages: an unstaged folder is an error, even if the
host catches it. A no-op importer or one that does not consume every staged
folder is also an error. Category completion requires the native call to return
and staged-consumption verification to pass.

Progress weights discovery and import within each configured category; it is
not a prediction of the provider's unknown catalogue size. Discovery request
counts and import folders served are separate. A served folder is **not** claimed
as a committed database write. Progress stays below 100 until all native imports
return successfully. Discovery is limited to 128 catalogue pages / 8 MiB of
serialized catalogue data, 1000 staged folders / an estimated 32 MiB of staged
items, and eight folder descents. Exceeding a bound fails before native import
instead of reporting a partial discovery as success. Emby's import is
non-transactional: cancellation or failure can leave partially applied metadata.
The host API does not expose per-item database commit results.

Settings add whole-number limits: **minimum request interval** 3..60 seconds
(default 3), **refresh timeout** 1..180 minutes (default 20), and **catalogue
cache lifetime** 1..120 minutes (default 15). Equivalent requests share validated
responses only within the same full configuration identity. Successful runs keep
a bounded in-memory cache; reuse never extends a page's original expiry.
Unsuccessful native imports do not commit newly discovered pages to that cache.
An HTTP request retains shared pacing ownership until the actual send completes,
including when its client ignores cancellation.

The timeout covers the whole configured task, including discovery, pacing waits,
and native import. Cancellation/timeout ends task execution promptly. If an
underlying operation ignores cancellation, diagnostics report a terminal outcome
with `Busy: true` until that work drains; another refresh and configuration saves
are rejected during that interval. No replacement background request is started
to bypass ownership. An operation that never returns requires host/transport
recovery; this plugin cannot safely force-abort third-party work.

Validated pages and completed-category checkpoints are atomically saved in
`TuneIn.refresh.xml` beside the plugin configuration, not in signed settings.
The next native run automatically reuses an unexpired checkpoint only if its
configuration identity and ordered completed-category prefix still match.
Changes to any saved setting invalidate both cache and checkpoint identity;
stale scope is never resumed. Pages keep their own expiry, and an older
completed-category prefix is replayed rather than skipped once its original run
exceeds the cache lifetime. The interrupted category's import always restarts
from its root; partial database changes are not rolled back. Provider/import
errors clear recovery state and fail the task; state read/write failures are
surfaced rather than silently treated as success. Treat the local checkpoint as
private provider metadata, particularly when Favorites is enabled.

**Check refresh status** performs an explicit administrator-only local read at
`GET /TuneIn/Refresh/Status`. It does not contact TuneIn, start work, or poll in
the background. Diagnostics contain stage, counts, outcome, partial-import and
draining indicators, and sanitized exception-type codes, not provider URLs,
credentials, response bodies, or raw exception messages. Native task failures
also propagate to Emby rather than being recorded as success. Diagnostic values
are rendered as plain text. Refresh-active save protection is enforced on the
server, not just in the settings page.

## Host requirements and integration

The scoped native import API requires `mediabrowser.server.core` 4.9.1.90.
That reference requires System.Memory 4.6.3. Use a compatible Emby Server stable
4.9.1.90+ or beta 4.9.2.7+ host. Do not use reflection or fall back to the
server-wide Internet Channels refresh on older hosts.

This channel retains Emby's upstream playback and playlist handling; it does not
include a custom stream relay or pre-roll handoff. `TuneInDirectory` accepts an
injected catalogue-fetch delegate, adapted by `TuneInRequests` to the host
`IHttpClient`. Catalogue and playback requests share one serialized pacer
(minimum three seconds, configurable up to 60). Received HTTP 429/503
`Retry-After` headers defer subsequent requests; failed provider requests are not
automatically retried. The host HTTP client may throw before returning a response,
in which case status and retry headers are unavailable. Future transport
integration must retain the shared pacer, cancellation, body bounds, OPML
validation, and actual HTTP-status reporting.

The playback callback is unchanged by this contribution. When a separately
maintained hardened playback implementation is integrated, first validate the
signed station and saved scope, then adapt only the validated station identifier
to that callback. Do not restore arbitrary raw URL IDs or bypass selection checks.

The newer SDK ignores `IHasCacheKey` and `InternalChannelFeatures`. The channel
uses supported `IHasChannelFeatures`, keeps category roots inside the channel,
and enforces the configuration snapshot identity at browse/playback boundaries.
Local selector, catalogue, and checkpoint invalidation do not force Emby to
remove old library metadata. Return to the channel root and run the native scoped
task after configuration changes; inspect stale or disabled library entries on
the host. New refresh settings participate in the configuration hash, so
previously signed IDs must be rediscovered after upgrading.

## Validation and runtime limitations

Run focused checks with:

```powershell
dotnet test MediaBrowser.Plugins.TuneIn.Tests\MediaBrowser.Plugins.TuneIn.Tests.csproj -c Release
node --test MediaBrowser.Plugins.TuneIn.Tests\configPage.test.cjs
dotnet build MediaBrowser.Plugins.TuneIn.sln -c Release
```

Automated native-import tests use a simulated manager. They do not prove host
database persistence, authorization middleware, scheduled-task behavior,
execution-context propagation, or stale-library cleanup. Before relying on the
refresh on a server, verify task registration and empty default schedule;
administrator-only diagnostics; enabled-scope-only imports; nested scopes;
no effects on other providers or schedules; provider failures; cancellation,
timeouts and recovery; draining/save protection; restart/resume; and
configuration-change invalidation. Do not treat folders served or progress as
proof that host database writes have committed. No real Emby-server runtime
validation is implied by these automated checks.
