# TuneIn for Emby - fork v1.3.0-fork.1

Configuration-first radio browsing with category switches, language/genre selection,
geographic pickers, multiple toggleable local cities/regions and optional coordinates.
Version **1.3.0-fork.1** retains the independent plugin ID, DLL name, configuration file and
dashboard/API page names. This prevents an upstream TuneIn update from replacing
the fork as happened with v0.0.1. It is intended for manual Emby testing, not a
claim of production runtime certification.

This is a feature prerelease based on upstream's **1.2.8.0** assembly line, not
an official upstream 1.3.0 release. The project declares one SemVer version;
the SDK derives assembly/file version **1.3.0.0**, and informational version
includes `1.3.0-fork.1+<tested-commit>`. Future fork candidates increment
`fork.2`, `fork.3`, etc.; fixes/features increment the appropriate base version.
Published v0.0.1-v0.0.3 remain unchanged historical manual-test releases.
See [CONTRIBUTING.md](CONTRIBUTING.md) for upstream review and versioning guidance.

## Requirements and manual installation

Use **Emby Server stable 4.9.1.90 or newer**, or a compatible patched beta
**4.9.2.7 or newer**. Older hosts are deliberately rejected. The plugin targets
`netstandard2.0`, is compiled against the stable Emby SDK 4.9.1.90, and relies on
Emby's supplied assemblies (including System.Memory 4.0.2.0 or newer). Do not
install host SDK DLLs from the build directory. See [SECURITY.md](SECURITY.md)
for the underlying server CVE and the withdrawn NuGet attribution.

1. Download `MediaBrowser.Plugins.TuneIn.Fork.dll`, `SHA256SUMS.txt` and `INSTALL.md`
   from this fork's **v1.3.0-fork.1** GitHub release. Keep the accompanying license.
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
   same configuration page; confirm **TuneIn Fork 1.3.0-fork.1** is displayed. Hard-refresh
   the dashboard after updating to avoid a cached configuration page/controller.
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
   an older menu. Saving notifies Emby of a content change but starts no plugin
   catalogue crawl. Emby's own refresh jobs can enumerate configured content.

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

## Individual radio-group refresh and API status

In **Advanced > TuneIn**, save configuration, then use **Refresh Local Radio**,
**Refresh Music**, **Refresh By Language**, **Refresh By Location**, **Refresh Talk**,
**Refresh Sports**, **Refresh Podcasts** or **Refresh My Favorites**. Only enabled,
configured groups are available. Unsaved edits must be saved first.

Each job refreshes the first page of its enabled saved starting nodes; Local Radio
queries configured coordinates and/or enabled saved areas separately. Language also
refreshes its metadata catalogue to validate selected IDs before fetching stations.
These requests preserve the normal category, selection, language and coordinate
restrictions. They do **not** recursively crawl descendants, follow pagination,
resolve streams or download every TuneIn station.

The page shows queued/running/completed/failed/cancelled state, completed/total
planned API requests and their percentage, actual HTTP response status when received,
catalogue-validation outcome, station/link row counts, completion time and errors.
**100% means the planned API requests completed, not that all stations were imported.**
Returned rows can include duplicates or links and are not an imported-station count.
HTTP 200 with an API fault or invalid catalogue is a failure, not a successful refresh.

After fetching pages, the plugin calls the SDK's scoped
`IChannelManager.RefreshChannelContent` for that group's exact root ID. Emby
synchronization is a separate phase with **no host progress percentage available**.
It may only reuse the selected group's cached starting pages; attempts to fetch
deeper/other-group pages are rejected. A host no-op, incomplete starting-page
enumeration or host error is reported as failed synchronization even if API progress
is 100%. Root menu enumeration is local; no other group's API is refreshed.
This SDK integration still requires manual verification on the real Emby server.

The first-page cache is configuration/query/category-bound, capped at 128 pages and
8 MiB of serialized catalogue data. Ordinary pages expire after 10 minutes;
explicitly refreshed pages after 30 minutes so the bounded job can synchronize them.
Refreshing a group cancels its older catalogue requests and invalidates only its
pages. Cache-capacity failures are explicit; use fewer saved selections if needed.
API calls remain serialized and spaced by at least three seconds.

**Cancel refresh** stops the chosen job. Saving configuration invalidates/cancels
applicable work. Duplicate starts reuse the active job; each group has at most one
active job, bounded to 20 minutes. Leaving the page stops browser polling, not the
server job. Reopening restores its current status; **Check refresh status** recovers
after a failed status request. Status checks make zero TuneIn requests. Job history
and page caches are in memory and reset when Emby restarts. These controls do not
replace or claim progress for Emby's global Refresh Internet Channels scheduled task.

## API behavior and deliberate limitations

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
security-policy rejections fail closed. **HLS, HTTPS-to-HTTP
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
| Advanced menu and plugin Settings | Both open TuneIn Fork 1.3.0-fork.1 and save the same independent configuration. |
| Checkbox controls and empty library diagnostic | Visible checkboxes; disabled/no-category status is explicit. Enabling browsing and Talk produces a local Talk root folder. |
| Save, restart and plugin update check | Installed fork remains 1.3.0-fork.1 (assembly 1.3.0.0); settings/selection state persists and upstream does not replace the DLL. |
| Administrator versus ordinary/anonymous user | Only the administrator can load selectors/save configuration/start, cancel or read refresh jobs; remote relay calls are rejected. Test cross-origin POST rejection under the host's authentication/CSRF policy. |
| Refresh Talk individually | One starting-page API request; HTTP and request progress are visible, then bounded Emby synchronization completes. Other groups make no TuneIn requests. Reopen the channel root and verify its Talk items. |
| Refresh Music/Language/Location and Local Radio | Only enabled saved starting nodes are fetched; Language includes its counted validation request; coordinates do not leak into other groups. Verify first-page content in the library, without recursive/global crawling. |
| API/host failure during refresh | Failed state and error are visible; HTTP 200 with a provider fault does not pass. API 100% with failed Emby synchronization is not reported as completed. |
| Cancel, duplicate start, leave/reopen and save during refresh | Single active job per group; cancellation/configuration changes stop work; foreground polling stops on navigation, resumes without another TuneIn refresh and never fabricates completion. |
| Load/expand selector | Only the requested branch loads; incidental station metadata warning is visible; no playback/crawl. |
| Disable a category or saved selection | Menu updates; old folder/media IDs cannot fetch the removed scope. |
| Enable category without required selections | Category remains unavailable; no unfiltered fallback. |
| Select two languages and a separate music genre | Each language remains filtered; music is independent of language/location selections. |
| Country/state/province/city hierarchy | Add/expand available levels; a leaf shows no further choices, not an error-shaped station list. |
| Coordinates and multiple saved local areas | Correct local stations, duplicate station IDs collapsed; invalid/NaN/out-of-range coordinates rejected. |
| Navigate another category | No local coordinates/account information sent to unrelated requests. |
| Playback MP3/AAC and PLS/M3U | Emby plays via its local relay/transcoder; case-sensitive paths work. HLS/private/downgrade targets fail explicitly. |
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