# TuneIn for Emby - fork v1.3.0-fork.18

Configuration-first radio browsing with category switches, language/genre selection,
geographic pickers, multiple toggleable local cities/regions and optional coordinates.
Version **1.3.0-fork.18** retains the independent plugin ID, DLL name, configuration file and
dashboard/API namespace. This prevents an upstream TuneIn update from replacing
the fork as happened with v0.0.1. It is intended for manual Emby testing, not a
claim of production runtime certification.

This is a feature prerelease based on upstream's **1.2.8.0** assembly line, not
an official upstream 1.3.0 release. The project declares one SemVer version;
the SDK derives assembly/file version **1.3.0.0**, and informational version
includes `1.3.0-fork.18+<tested-commit>`. Future fork candidates increment
the suffix; fixes/features increment the appropriate base version.
This document consolidates the currently supported enhancements from every fork
release, rather than requiring earlier downloads. [CHANGELOG.md](CHANGELOG.md)
records their introduction and superseded behavior. Earlier fork prereleases are
marked deprecated after replacement; published assets and source tags remain
unchanged for provenance. Fork.9 and earlier
can stop after the advertisement while the client still appears to be playing;
fork.10 additionally lacks the live-provider HTTP compatibility fixes in fork.11.
Fork.12 lacks the TuneIn-only refresh task and task-specific diagnostics in this
release. Fork.13 does not preserve the underlying refresh exception in task
reports. Fork.14 rejects provider alphabetic browse filters. Version
1.3.0-fork.15 accepts those filters and improves the task report when Emby
omits nested exceptions. Fork.16 rejects Emby's channel-root query during staged
refresh; fork.17 allows that query and reports import failures before artwork
updates. Fork.18 handles the provider's unescaped `#` in alphabet pagination
links. Real Emby refresh retesting remains pending. It includes the handoff and live-provider
HTTP compatibility fixes, verified against four actual provider streams;
real Emby runtime verification remains pending.
Emby may remove and recreate category folders when configuration changes alter
their identifiers. Category-folder retention is deferred and is not fixed in
this release.
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
- A TuneIn-only **Refresh TuneIn** task that traverses enabled categories and their
  saved sub-category selections; native status and failed-task diagnostics.
- Independent plugin identity, Advanced/Plugins settings entry points, visible native
  checkbox/input controls, four collapsible configuration sections and versioned views.
- A futuristic 544x306 main-library logo without a FORK subtitle, distinct category
  icons, protected station artwork and preservation of previously migrated images.
- Locked dependencies, analyzers/format/tests, NuGet/GitHub/NVD advisory checks,
  security-extended CodeQL, SBOM, checksums and exact-source release evidence.
- No plugin-requested global refresh on installation, update or configuration Save.
  Run/schedule the TuneIn-only task or Emby's all-provider task through Scheduled
  Tasks. No per-category tasks or schedule-suppression settings remain.

## Requirements and manual installation

Use **Emby Server stable 4.9.1.90 or newer**, or a compatible patched beta
**4.9.2.7 or newer**. Older hosts are deliberately rejected. The plugin targets
`netstandard2.0`, is compiled against the stable Emby SDK 4.9.1.90, and relies on
Emby's supplied assemblies (including System.Memory 4.0.2.0 or newer). Do not
install host SDK DLLs from the build directory. See [SECURITY.md](SECURITY.md)
for the underlying server CVE and the withdrawn NuGet attribution.

1. Download `MediaBrowser.Plugins.TuneIn.Fork.dll`, `SHA256SUMS.txt` and `INSTALL.md`
   from this fork's **v1.3.0-fork.18** release package. Keep the accompanying license.
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
   same configuration page; confirm **TuneIn Fork 1.3.0-fork.18** is displayed. Reopen
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
   Use **Refresh TuneIn** under Scheduled Tasks to repopulate only selected TuneIn
   categories, or use Emby's built-in Refresh Internet Channels task for all providers.

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

## Refresh tasks

Save configuration in **Advanced > TuneIn**, then run or schedule **Refresh TuneIn**
under **Scheduled Tasks > Internet Channels**. This plugin task traverses only
enabled TuneIn categories and their saved sub-category selections, such as saved
languages, music genres and locations. Disabled categories and unselected
sub-categories are not queried; other channel providers are not refreshed. This
is not an unrestricted worldwide catalogue crawl. Provider availability and
Emby's traversal limits still apply.

The task has no default trigger. Run it manually or assign a schedule through
Emby's native Scheduled Tasks interface. It does not start on install, update or
configuration Save. The former eight **Refresh TuneIn - ...** tasks and legacy
per-group refresh/schedule APIs remain removed.

Emby's built-in **Refresh Internet Channels** task is separate: it refreshes all
channel providers, including TuneIn, subject to TuneIn's category and selection
restrictions. Use that host task when you intend to refresh all providers. Its
schedule is managed independently through Emby; this plugin does not suppress it.
On-demand browsing retains the bounded 128-page/8 MiB cache.

For **Refresh TuneIn**, the plugin first discovers each selected section and
stages its folder results before asking Emby to import that section. Discovery
is limited to 1,000 folders and an estimated 32 MiB of staged results per
section, as well as the existing request and time limits. Failure or
cancellation during discovery does not start that section's Emby import;
Emby's import itself is not transactional. Identical catalogue requests can
reuse validated responses from the same run or from a successful refresh for
up to ten minutes, but differently filtered requests remain separate. This
in-memory cache is bounded to 128 pages/8 MiB and is cleared by configuration
Save or restart. Music listings alone do not establish which stations belong
to a language or location, so those memberships still require provider data.

Catalogue calls remain serialized. In **Setup and account**, administrators can
set the interval between starts to 1–30 whole seconds; existing configurations
default to three seconds. Shorter intervals can trigger TuneIn throttling; no
published numerical TuneIn catalogue limit has been verified. HTTP 429
responses receive at most two retries, respecting `Retry-After` when supplied;
long provider delays stop the current request and continue to block subsequent
catalogue requests until the delay expires. Per-category refreshes
write request, response, cache-hit and discovery/import timing summaries to
the server debug log without URLs or account information. These measurements
do not establish the impact on a real Emby server.

### Restoring previously disabled schedules

On startup, a one-time migration checks the private `tunein-fork-refresh.xml` state
for the plugin's former global-trigger suppression or its saved trigger backup.
If the built-in task still has no triggers, its saved original schedule is restored.
If suppression is recorded but no backup exists, the task's own default triggers
are used (rather than hard-coding a weekly day/time). A nonempty current schedule
is treated as a later administrator edit and is preserved. Without recorded
plugin suppression/backup, no triggers are changed, even if the schedule is empty.
The old suppression setting is then cleared and the consumed global backup removed.
Failures are logged, rolled back where possible, and retained for retry on restart.
Historical per-group state is not deleted. Emby maintains native execution history
for both tasks, separate from the plugin's retained per-category history.
Protect and back up that file; do not attach it to issues.

The plugin does not implement `IHasChangeEvent` or explicitly start/queue a refresh
on install, update or Save. Restoring a schedule lets Emby's normal scheduler run
it when due; it does not request an immediate crawl. Other providers and host
triggers remain independent.

### Status and failed refresh log

In plugin Settings, **Refresh TuneIn** shows the actual dedicated task
state, host-reported progress and last execution result/time. While that task is
running, it also shows the current category and phase, including discovery,
Emby import and artwork updates, plus completed/started catalogue requests and
cache hits. The counts are for the current category, not an estimate of all
remaining pages. It loads once when the view opens; **Update status** reads it
again. There is no polling or provider request, and status does not start,
cancel or reschedule the task. A phase may be temporarily unavailable while
the task starts or finishes.

**Retrieve failed Refresh TuneIn report** is enabled only when the latest dedicated
task result failed. It retrieves Emby's stored error message/exception report for
that exact attempt, not the full server log or the global task's report. The
report is displayed as plain text, capped at 65,536 characters, with truncation
indicated. If Emby recorded no details, the report says so and directs you to the
server log's matching time range. A later result replaces the available report;
update status if it changes before retrieval. Both diagnostic endpoints are
administrator-only. Reports can include private paths/URLs/account details:
redact them before sharing.

New refresh failures retain the original exception chain along with the failed
category, phase, completed/started request counts and last observed HTTP status.
Rejected catalogue parameters report their name, rejection class, length and
occurrence count, without the value or URL. Safe parameter details are included
in the main task error even when Emby omits nested exception text. Previously stored generic reports
cannot recover exception details that were discarded.

Real host task registration/removal, trigger restoration, authorization and
diagnostics still need manual Emby verification; SDK/unit-test coverage is not
runtime proof.

### Category artwork

Eight bundled category cards now have distinct, original icons: radio, musical
notes, language bubbles, location pin, microphone, basketball, headphones and
favorites heart. Music/Talk/Sports subgroups beneath a language use their content
icon rather than the language icon. The main library Primary, Thumb and Backdrop
assets are now a futuristic **544x306** landscape design with no FORK subtitle.
Accepted station logos are unchanged. Existing primary images, including custom
images and folder artwork already migrated by earlier fork group tasks, are
preserved. The removed group tasks no longer perform a separate artwork migration;
normal host channel import/image handling applies. Protected local artwork URLs
and stable signed station IDs are unchanged.

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

Radio HTTP response parsing accepts provider LF-only status/header lines,
repeated advisory fields such as `icy-br`, and legacy identity transfer encoding
as a close-delimited body. Duplicate framing/decoding/redirect headers and
transfer-encoding/content-length ambiguity are still rejected. Catalogue responses
and chunk framing retain their strict parsing. These compatibility rules do not
relax DNS, redirect or local-only relay protections.

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
| Advanced menu and plugin Settings | Both open TuneIn Fork 1.3.0-fork.18 through versioned page/controller URLs and save the same independent configuration. The previous dashboard does not remain underneath the form. No individual-refresh controls or background polling; native status reads once on open. |
| Collapsible settings sections | Four sections: setup/account starts expanded; radio groups, coordinates and selections expand/collapse independently. Save stays visible; collapsing does not discard edits or request provider data. |
| Checkbox controls and empty library diagnostic | Visible checkboxes; disabled/no-category status is explicit. Enabling browsing and Talk produces a local Talk root folder. |
| Save, restart and plugin update check | Installed fork remains 1.3.0-fork.18 (assembly 1.3.0.0); settings/selection state persists and upstream does not replace the DLL. No plugin-requested global refresh is queued; distinguish existing/restored host triggers. Category-folder retention remains unresolved. |
| Administrator versus ordinary/anonymous user | Only the administrator can load selectors/save configuration/read native refresh status or failure reports; remote relay calls are rejected. |
| Refresh Music/Language/Location and Local Radio | Saved selections and descendants remain scoped; Language includes its counted validation request; local coordinates/account do not leak to unrelated groups. Repeated pages reuse the bounded cache. |
| Scheduled tasks | One **Refresh TuneIn** task appears without default triggers; its description says it refreshes enabled TuneIn categories/selections only. The built-in Refresh Internet Channels task remains separate and server-wide. Former per-group tasks and mutation routes remain absent. |
| Native refresh status and report | Settings reads the dedicated Refresh TuneIn task state/result; Update status makes no provider request. Its failed result enables that task's exact error report as text, not the server-wide refresh report. No details, truncation, changed result and access errors are explicit. |
| Native schedule restoration | Recorded plugin suppression restores saved triggers or native defaults only for the built-in Refresh Internet Channels task, and only when its current schedule is empty. Later administrator edits and never-managed schedules are untouched; subsequent restarts and Save do not alter them. |
| Playback after upgrading from fork.2 | Retry existing station items without reconfiguring. The local relay must return audio to ffprobe/FFmpeg, not HTTP 500 from an unset result factory. Probe-discovered audio streams should replace the empty stream list; do not bypass the relay or fabricate codecs. |
| Category/station artwork after upgrading | Existing migrated/custom primary images stay intact; stations retain protected PNG/JPEG logos. Playback IDs are unchanged. |
| Leave/reopen Settings during status/report requests | Late responses cannot overwrite the hidden/reopened view; no log is automatically fetched and no refresh is started. |
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