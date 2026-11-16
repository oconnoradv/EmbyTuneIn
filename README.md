# TuneIn for Emby - fork v1.3.0-fork.10

Configuration-first radio browsing with category switches, language/genre selection,
geographic pickers, multiple toggleable local cities/regions and optional coordinates.
Version **1.3.0-fork.10** retains the independent plugin ID, DLL name, configuration file and
dashboard/API namespace. This prevents an upstream TuneIn update from replacing
the fork as happened with v0.0.1. It is intended for manual Emby testing, not a
claim of production runtime certification.

This is a feature prerelease based on upstream's **1.2.8.0** assembly line, not
an official upstream 1.3.0 release. The project declares one SemVer version;
the SDK derives assembly/file version **1.3.0.0**, and informational version
includes `1.3.0-fork.10+<tested-commit>`. Future fork candidates increment
the suffix; fixes/features increment the appropriate base version.
This document consolidates the currently supported enhancements from every fork
release, rather than requiring earlier downloads. [CHANGELOG.md](CHANGELOG.md)
records their introduction and superseded behavior. Older prerelease download
records are retired; historical source tags remain for provenance.
All earlier fork versions are deprecated because their pre-roll-to-live handoff
can stop after the advertisement while the client still appears to be playing.
Some stations, including KEXP, may work; that does not make those builds suitable
for general playback. Version 1.3.0-fork.10 fixes the handoff in regression coverage;
real Emby runtime verification remains pending.
See [CONTRIBUTING.md](CONTRIBUTING.md) for upstream review and versioning guidance.

## Enhancements beyond upstream

The comparison baseline is upstream MediaBrowser/TuneIn's 1.2.8.0 line, not a
claim that upstream has accepted these changes.

- Configuration-first browsing, eight independent category switches, selected
  languages/music/geographies, multiple local areas and optional validated coordinates.
- Administrator-only lazy selectors, signed restart-stable navigation, stale-ID
  rejection, bounded caches and server-enforced category/selection ancestry.
- Language subfolders honor group switches and enabled saved music types throughout
  descendants, pagination, playback and artwork; unrelated categories stay independent.
- Protected local playback relay, pinned public DNS/redirect checks, bounded
  playlist alternatives and constructor-injected host responses with correct cleanup.
- Eight native per-group Scheduled Tasks with scoped station population, cancellation,
  failure detection and persisted last-success/last-attempt history.
- Independent plugin identity, Advanced/Plugins settings entry points, visible native
  checkbox/input controls, four collapsible configuration sections and versioned views.
- A futuristic 544x306 main-library logo without a FORK subtitle, distinct category
  icons, protected station artwork and revision-marked migration of stored images.
- Locked dependencies, analyzers/format/tests, NuGet/GitHub/NVD advisory checks,
  security-extended CodeQL, SBOM, checksums and exact-source release evidence.
- No plugin-requested global refresh on installation, update or configuration Save.
  Refresh buttons and automatic-interval controls now live only in native Scheduled
  Tasks, not on the TuneIn settings page.

## Requirements and manual installation

Use **Emby Server stable 4.9.1.90 or newer**, or a compatible patched beta
**4.9.2.7 or newer**. Older hosts are deliberately rejected. The plugin targets
`netstandard2.0`, is compiled against the stable Emby SDK 4.9.1.90, and relies on
Emby's supplied assemblies (including System.Memory 4.0.2.0 or newer). Do not
install host SDK DLLs from the build directory. See [SECURITY.md](SECURITY.md)
for the underlying server CVE and the withdrawn NuGet attribution.

1. Download `MediaBrowser.Plugins.TuneIn.Fork.dll`, `SHA256SUMS.txt` and `INSTALL.md`
   from this fork's **v1.3.0-fork.10** release package. Keep the accompanying license.
2. Compare the DLL's SHA-256 against its entry in `SHA256SUMS.txt`. On Windows:
   `Get-FileHash .\MediaBrowser.Plugins.TuneIn.Fork.dll -Algorithm SHA256`.
   On Linux/macOS: `shasum -a 256 MediaBrowser.Plugins.TuneIn.Fork.dll`.
3. Stop Emby. Back up the existing TuneIn DLL and its XML configuration.
   Locate the server's actual data/plugins directory using Emby's dashboard paths;
   paths vary by installation. Copy **only the plugin DLL** into that plugins
   directory. Move the old `MediaBrowser.Plugins.TuneIn.dll` out of the plugins
   directory before installing the fork DLL; do not leave upstream/v0.0.1 and the
   fork installed together. Remove/cancel any pending upstream TuneIn update using
   Emby's dashboard before restarting. Restart Emby.
4. As an administrator, open **Dashboard > Advanced > TuneIn**, or
   **Dashboard > Plugins > TuneIn (oconnoradv fork) > Settings**. Both open the
   same configuration page; confirm **TuneIn Fork 1.3.0-fork.10** is displayed. Reopen
   Settings from the plugin or Advanced menu rather than an old bookmarked page.
   Both HTML and controller URLs are now versioned to avoid stale fork.1/fork.7
   markup. Hard-refresh the dashboard after updating. The controller uses Emby's
   plugin lifecycle adapter so the previous dashboard view is hidden correctly;
   inputs use the host's labeled container layout.
   The fork uses its own `MediaBrowser.Plugins.TuneIn.Fork.xml` configuration.
   On migration from upstream/v0.0.1, re-enter your username/coordinates and
   selections: the old XML is left untouched, not automatically imported.
   Browsing stays paused until explicitly saved and enabled. Subsequent fork
   upgrades preserve this independent configuration.
5. Enable the desired categories. Use **Load root**, **Expand**, the label filter,
   and **Add** to build selections. Toggle/remove saved entries as needed.
   Language, location and music require at least one enabled selection.
   Favorites requires a username. Local Radio requires coordinates and/or enabled
   saved cities/regions; it never silently falls back to IP geolocation.
6. Check **Enable configured browsing after saving** and save. Return to the
   TuneIn channel root; refresh channel metadata if the Emby client still shows
   an older menu. Saving invalidates plugin caches and old scoped identifiers,
   but emits no content-change event that could request a global channel refresh.
   Use the dedicated Scheduled Tasks to repopulate configured groups.

The v0.0.3 checkbox fix is retained: the enable-browsing,
coordinate, category and saved-selection controls are visible Emby checkboxes.
The page reports disabled browsing or no enabled categories explicitly.
If the library is empty, first enable **Talk** (which needs no selector), check
**Enable configured browsing after saving**, and save. Reopen the channel root:
**Talk** must appear without a TuneIn request to construct the root menu. Refresh
Internet Channels does not bypass the gate or populate an unrestricted catalogue.
For Music/Language/Location and Local Radio, also add/enable the required
selections or valid local coordinates. Do not attach your private configuration XML.

The fork's plugin ID is `d9314bc3-33e0-4080-88ef-8e7e718524f2`, not upstream's
`10b82431-f939-4507-a8e2-9509dd3fd6ae`. Ordinary upstream updates no longer target
this plugin or its DLL; global automatic plugin updates need not be disabled.
Do not separately reinstall upstream TuneIn alongside the fork.
If installation fails, stop Emby, remove only this DLL and restore your backup.
The old plugin does not have this fork's configuration/security protections.
Keep XML configuration private: it contains the account, coordinates and a
runtime-generated navigation signing key. Never attach it unredacted to an issue.

## Radio-group refresh in Scheduled Tasks

Save configuration in **Advanced > TuneIn**, then open **Scheduled Tasks >
Internet Channels**. Run **Refresh TuneIn - Local Radio**, **Music**, **By Language**,
**By Location**, **Talk**, **Sports**, **Podcasts** or **My Favorites** as appropriate.
Only enabled, configured groups may query TuneIn. The individual-group refresh
section and Automatic refresh intervals section have been removed from the plugin
settings page; use the native task controls to run, cancel or schedule refreshes.

Each job now populates the chosen section's station metadata using
`IChannelManager.RefreshChannelContent` with its exact signed root folder ID.
It traverses enabled saved starting nodes, descendant folders and pagination
through **eight refresh levels**, matching the inspected Emby 4.9.1.90
Refresh Internet Channels implementation. Unlike that global task, only the
chosen group is refreshed; other providers/groups are not queried.
The native scheduled-task worker updates Emby's real execution history rather
than only running an independent plugin job. Duplicate API starts reuse the
active group job; cancelling before native startup cannot start a second crawl.
Local coordinates and saved areas remain separate, scoped requests; language
selection is validated first. Unfiltered genre/geographic hub links are excluded.
No streams are played. This replaces fork.1's first-page-only/cache-only refresh.

Each task has its own fixed key and can be run or assigned triggers independently.
No automatic default TuneIn triggers are installed: setup, startup, installation,
update and saving alone do not start station discovery or request the global task.
Disabled/unconfigured groups fail explicitly without contacting TuneIn.
Task cancellation propagates to the shared group job, and failures remain failures
in both the task and the configuration status. Duplicate starts share the active job.

Diagnostic API request totals grow as deeper pages are discovered. The retained
administrator-only `/TuneInFork/Refresh` status API reports completed/known requests,
actual HTTP responses, validation outcomes, station/link row counts, completion time
and explicit errors. It is not polled by the settings page. Progress remains
**indeterminate while discovery/import is active**, instead of advertising an
early 100%. Native per-folder import percentages are unavailable; the dedicated
scheduled task reports 100% only after successful completion. Returned row counts
are not unique/imported-station counts. HTTP 200 with an API fault is a failure.
A native no-op, missing saved starting node or swallowed provider error cannot
report successful population. Real library integration still needs manual testing.

On-demand catalogue pages are cached by configuration/query/category; repeated
navigation reuses them. The cache remains capped at 128 pages/8 MiB and evicts
old entries rather than truncating a large section to the cache capacity.
Refreshing invalidates only that group's pages and cancels its older requests.
Requests remain serialized and spaced by at least three seconds. Jobs have a
**1000 catalogue-request and 20-minute budget**; reaching a limit is reported
as incomplete/failed, not a completed section. Eight levels, provider availability
and the budget mean this is not a promise to import the entire worldwide catalogue.

The native task's **Stop** control stops the chosen job. Saving configuration invalidates/cancels
applicable work. Duplicate starts reuse the active job; each group has at most one
active job, bounded to 20 minutes. Leaving Scheduled Tasks does not cancel the
server job. Diagnostic status checks make zero TuneIn requests. Active jobs,
API counters and page caches reset when Emby restarts. Last successful group refresh
and last attempt/result persist in `tunein-fork-refresh.xml` in Emby's data directory.
Failed or cancelled attempts do not replace the last successful time. Each TuneIn
task's description displays this history in UTC; Emby's **Last ran** field records
the actual native task execution. Reopen/reload an already-open Scheduled Tasks page to
retrieve the latest result. Global Refresh Internet Channels runs
do not populate this group-job history. A persistence error is reported in group
status and sanitized server logs; timestamps may then not survive a restart.

### Installation, updates and existing schedules

The plugin no longer implements Emby's `IHasChangeEvent`: cache/configuration
invalidation does not notify Emby to queue a global refresh. It does not call,
queue or create a startup trigger for **Refresh Internet Channels** on installation,
update or Save. No replacement startup crawl is introduced.

This is not a server-wide scheduler disable. Emby's own existing/default triggers,
other plugins and administrator-requested runs remain under host control. Review
the built-in task's triggers in **Scheduled Tasks** if you also want its independent
automatic runs disabled. That task affects all Internet Channel providers and still
honors TuneIn selections; it never bypasses configuration for an unrestricted library.
Its read-only description is unchanged, and the repeated explanatory text is absent
from the eight TuneIn task descriptions.

Previously applied fork.7/fork.8 intervals remain native Emby triggers until edited
or removed in Scheduled Tasks; removing the old settings section does not silently
restore or overwrite them. The administrator-only legacy Schedule API remains for
compatibility/explicit restoration, but nothing on the settings page calls it.
Persisted trigger backups/preferences and refresh history remain in the private
`tunein-fork-refresh.xml` file, separate from station/account configuration. No
startup timer reapplies them. Back up this state file; do not attach it to issues.

### Category artwork

Eight bundled category cards now have distinct, original icons: radio, musical
notes, language bubbles, location pin, microphone, basketball, headphones and
favorites heart. Music/Talk/Sports subgroups beneath a language use their content
icon rather than the language icon. The main library Primary, Thumb and Backdrop
assets are now a futuristic **544x306** landscape design with no FORK subtitle.
Accepted station logos are unchanged. Native channel import ignores new image URLs if an item already
has a primary image. After upgrading, run each affected group's refresh once from
Scheduled Tasks: the refresh explicitly updates persisted primary image references
for that section's folders, including descendants. The first successful group
refresh also migrates its parent TuneIn channel's existing stored logo references
to the new bundled landscape design; no global refresh is required.
Stations and other groups are untouched. Per-item artwork revision markers
avoids repeatedly replacing already-migrated images and preserves later custom
images. Removed/invalid signed folders are skipped with a warning. Reload the
client after completion; no manual image deletion or station-ID change is needed.

## API behavior and deliberate limitations

Language subfolders honor the Music, Talk and Sports group switches as well as
the selected language. Disabled groups are not offered within a language, and
signed descendant folders, stations and artwork retain their group restriction.
The Music folder beneath each language is built locally from your enabled saved
music selections, not the unrestricted provider music menu. Opening a selected
music type queries only that branch with the language filter; its subgenres,
stations, artwork and pagination retain both signed selection scopes. Broad
classifier links cannot return to an unfiltered music menu. Selecting a parent
music type includes the subgenres TuneIn returns within that branch.
After upgrading from fork.5 or earlier, refresh **By Language** to replace older
music items lacking signed music-selection ancestry and remove cached unselected
types. Legacy unscoped language/music descendants are rejected rather than allowed
to bypass selections. Local Radio, By Location and other sections do not inherit
these music-type limits; the main Music section retains its existing selections.
Settings do not need to be reset.

Opening the configuration page and enumerating an unconfigured channel root make
**zero TuneIn requests**. Root categories are constructed locally. Selector requests
are explicit administrator actions, cached by category/parent and expanded one
branch at a time. Saving does not resolve streams or recursively crawl the catalogue.

Language and music roots and the geographic root returned metadata-only links in
live probes. **Deeper selector responses can include station metadata.** For example,
the US region response included station rows with state links even with
`filter=regions`; those station rows are discarded during selector discovery.
This incidental metadata download was explicitly approved for this fork. It does
not mean station streams were downloaded or played. There is no reliable documented
metadata-only country/state/city hierarchy endpoint. The UI states this limitation.

Geographic levels vary: add a country, region, state, province or city wherever
TuneIn exposes it. A leaf may have no further choices. The Local Radio picker uses
the same geographic tree; selected areas and coordinates are queried separately and
station IDs are deduplicated. Coordinates are invariant-format, finite and
range-checked, and propagate only through coordinate-based Local Radio navigation.
Other categories do not inherit geographic, username or language filters.

Current language hubs returned generic `c424724`/`c424725` links; these are normalized
to the corresponding classifier while carrying the selected language filter.
Unknown/unavailable language IDs are rejected before station browsing.
Related/recommendation links are suppressed and pagination is lazy, scoped to its
current node. No worldwide recursive crawl is implemented.

For SSRF protection, radio playback goes through a **local-only Emby server relay**.
Remote stream/playlist addresses and redirects are DNS-checked and connected to a
pinned public address; they are never handed directly to Emby or the client.
Direct play/direct stream are disabled; Emby probes/transcodes the relay. This can
use more CPU and requires the server's normal transcoding capability.
MP3/AAC and bounded M3U/PLS playlist resolution are supported, including advertised
alternatives (at most 8 entries per playlist, 16 URL openings and 4 playlist levels,
within a 30-second opening budget). Alternative failures are logged without URLs;
security-policy rejections fail closed.

TuneIn's recognized leading `multi_bump_sonic_pre_pre.mp3` asset on its HTTPS
`tunein-ondemand.cdnstream1.com` host is **required pre-roll**, not a live-stream
alternative. The relay plays it through, then opens one working live alternative.
It never skips the ad, repeats it on a live opening failure, or plays all station
alternatives in sequence. For these sessions only, the host-configured FFmpeg
decodes each input separately to 48 kHz stereo 16-bit PCM behind one streaming
WAV header. This keeps probing and playback consistent across MP3-to-AAC (or other
sample-rate/channel) transitions without concatenating incompatible compressed
bytes. It adds a decoder process and loopback PCM traffic; stations without this
pre-roll, such as KEXP, keep the existing compressed passthrough relay.
The relay source URL is revisioned to invalidate old ad-only probe identities;
signed station/item IDs and saved selections are unchanged.
Each deferred opening has the same 30-second/playlist limits. Ad errors fail
explicitly rather than bypassing required playback; live EOF ends the stream
rather than starting another alternative. Cancellation, configuration changes and
abandoned probes release the upstream connection and decoder. Unknown provider
ad formats are not guessed from arbitrary filenames. Real Emby probe-to-playback
handoff and sustained playback still require the manual checks below.

**HLS, HTTPS-to-HTTP
redirect downgrades, private-network radio services, proxy-only networks and malformed
or incompatible streams are rejected**. The initial public radio URL can be HTTP
when supplied by the trusted HTTPS TuneIn catalogue, because many stations still
use HTTP. Original URL case is preserved. Provider availability, geographic and
partner restrictions still apply.

TLS 1.2 certificate trust, hostname and expiration checks are enforced. Mandatory
online certificate revocation is not enabled (the standard .NET client policy):
the build environment's CRL/OCSP services were unreachable. No online-revocation
validation or revocation-clean claim is made.

References, not a provider availability contract:
[community live API guide](https://github.com/skarl/bose-soundtouch-resurrect/blob/main/docs/tunein-api.md),
[archived Browse documentation](https://github.com/skarl/bose-soundtouch-resurrect/blob/main/docs/reference/radiotime/methods/browse.md)
and [archived OPML overview](https://github.com/skarl/bose-soundtouch-resurrect/blob/main/docs/reference/radiotime/overview.md).

## Manual Emby test checklist

No Emby server was available for automated runtime installation, dashboard
integration, authentication or playback testing. The release includes build,
regression, dependency/advisory, SBOM and checksum evidence instead. Please verify:

| Test | Expected outcome |
|---|---|
| Fresh/legacy install, startup and unconfigured root | No automatic TuneIn traffic; empty channel until explicitly configured. |
| Advanced menu and plugin Settings | Both open TuneIn Fork 1.3.0-fork.10 through versioned page/controller URLs and save the same independent configuration. The previous dashboard does not remain underneath the form. There are no individual-refresh or interval sections/API polls. |
| Collapsible settings sections | Four sections: setup/account starts expanded; radio groups, coordinates and selections expand/collapse independently. Save stays visible; collapsing does not discard edits or request provider data. |
| Checkbox controls and empty library diagnostic | Visible checkboxes; disabled/no-category status is explicit. Enabling browsing and Talk produces a local Talk root folder. |
| Save, restart and plugin update check | Installed fork remains 1.3.0-fork.10 (assembly 1.3.0.0); settings/selection state persists and upstream does not replace the DLL. No plugin-requested global refresh is queued; distinguish existing host triggers. |
| Administrator versus ordinary/anonymous user | Only the administrator can load selectors/save configuration/start, cancel or read refresh jobs; remote relay calls are rejected. Test cross-origin POST rejection under the host's authentication/CSRF policy. |
| Refresh a section individually | Its configured folders, deeper station pages and pagination populate through eight levels; other groups/providers are not queried. Verify actual station items, not just root folders. |
| Refresh Music/Language/Location and Local Radio | Saved selections and descendants remain scoped; Language includes its counted validation request; local coordinates/account do not leak to unrelated groups. Repeated pages reuse the bounded cache. |
| Dedicated scheduled tasks | Eight distinct TuneIn tasks appear under Internet Channels; no automatic triggers on install. Run/schedule one group and verify its actual stations, shared status, cancellation and task failure reporting. |
| Retained refresh times | A group task updates last-success/last-attempt in its description and native Last ran; restart preserves history. A failed attempt retains the previous success. Reload Scheduled Tasks if its display was cached. |
| Native schedule management | Existing triggers survive upgrade unchanged. Configure/remove them only through explicit administrator actions; configuration Save never changes schedules. |
| API/host/budget failure during refresh | Failed state and error are visible; HTTP 200 faults, swallowed errors, incomplete node enumeration and limits cannot report completed population. Discovery progress remains indeterminate until done. |
| Playback after upgrading from fork.2 | Retry existing station items without reconfiguring. The local relay must return audio to ffprobe/FFmpeg, not HTTP 500 from an unset result factory. Probe-discovered audio streams should replace the empty stream list; do not bypass the relay or fabricate codecs. |
| Category/station artwork after upgrading | Run one group task to migrate the main TuneIn logo to 544x306 without the FORK subtitle; refresh each affected group for its folder icons. Stations retain protected PNG/JPEG logos. Playback IDs, unrelated groups and later custom images remain unchanged. Reload the client. |
| Cancel, duplicate start, leave/reopen and save during refresh | Single active job per group; native cancellation/configuration changes stop work; leaving the page does not fabricate completion or start a second job. |
| Load/expand selector | Only the requested branch loads; incidental station metadata warning is visible; no playback/crawl. |
| Disable a category or saved selection | Menu updates; old folder/media IDs cannot fetch the removed scope. |
| Enable category without required selections | Category remains unavailable; no unfiltered fallback. |
| Select two languages and music types | Each language's Music folder lists only enabled saved music types, then shows language-filtered stations/descendants. The main Music section stays independent of language/location selections. |
| Disable/remove one music selection, save and refresh By Language | That type disappears below every language. Its old folder/station/artwork IDs cannot access it, including after server restart; enabled siblings remain usable. |
| Disable Talk, Music or Sports, then refresh By Language | The disabled hub and its descendants disappear beneath every selected language; enabled sibling groups remain. Old disabled/legacy descendant IDs cannot fetch, play or retrieve artwork. |
| Country/state/province/city hierarchy | Add/expand available levels; a leaf shows no further choices, not an error-shaped station list. |
| Coordinates and multiple saved local areas | Correct local stations, duplicate station IDs collapsed; invalid/NaN/out-of-range coordinates rejected. |
| Navigate another category | No local coordinates/account information sent to unrelated requests. |
| Playback MP3/AAC and PLS/M3U | Emby plays via its local relay/transcoder; case-sensitive paths work. HLS/private/downgrade targets fail explicitly. |
| Required pre-roll to live music | Test asia DREAM radio / Big B Radio past the complete ad and into sustained music, plus KEXP without pre-roll. MP3-to-AAC transitions must retain one PCM/WAV probe format. Stop during the ad and music; no plugin decoder/upstream connection should remain. Failed ads must not silently skip to music; live alternatives remain fallbacks, not a queue. |
| Save during browsing/playback | Old scoped IDs and applicable in-flight operations are invalidated; reopen from root. |
| Restart with unchanged configuration | Signed IDs remain valid; channel refresh/reopening works without manually editing URLs. |

When reporting a failure, include Emby version, OS, selected category and a sanitized
error. Do not include full stream URLs, account, coordinates or configuration keys.

## Reproduce release gates

Install a patched .NET 8 SDK, Node 24 and GitHub CLI. From a clean checkout:

```powershell
node .\scripts\verify-release.cjs --online --live --release
```

`--online` cross-checks all resolved plugin packages against GitHub's advisory API
and verifies returned CVE records against NVD. Failed/incomplete audits are errors.
`--live` additionally runs a real pinned-DNS/TLS language-root request.
`--release` requires a clean committed revision and both online/live gates.
Outputs under `artifacts` include the DLL, checksums, CycloneDX SBOM, resolved
dependency inventory, advisory reports, installation/license/security notes and
exact source/build/test evidence. The artifact directory is not source-controlled.

CI runs locked restore, warning-free build, C# whitespace/analyzer checks,
offline regressions, JavaScript syntax/UI tests, transitive NuGet/GHSA/NVD checks,
and CodeQL C#/JavaScript security-extended analysis. It fails on open CodeQL
findings for the analyzed ref. Live API and real Emby testing are not CI claims.
Re-run advisory checks at release time: a clean historical result is not a
guarantee against future disclosures. [SECURITY.md](SECURITY.md) maps these
controls to practical NIST SSDF evidence, **not certification**.