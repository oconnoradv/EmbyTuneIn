# TuneIn for Emby - fork v1.3.0-fork.20-rc.2

This independent fork of [MediaBrowser/TuneIn](https://github.com/MediaBrowser/TuneIn)
adds configuration-first radio browsing, scoped refreshes, safer playback and
administrative controls. It is a **release candidate for testing**, not an
official upstream release or a claim of production runtime certification.
This candidate replaces the original TuneIn plugin: it uses the upstream plugin
ID, DLL and configuration filename. An upstream update can replace it.

## Consolidated fixes and enhancements

The following summarizes the fork's changes from the upstream 1.2.8.0 baseline;
it describes current behavior, not every intermediate implementation.

### Playback and browsing fixes

- **Pre-roll handoff:** Recognized required ads play through before a live
  alternative opens, rather than stopping while playback appears active. Separate
  decoding gives ad and live audio one consistent format; live alternatives remain
  fallbacks. Stations without pre-roll retain compressed passthrough.
- **Provider compatibility:** Support legacy radio responses with LF-only headers,
  repeated advisory ICY fields or identity transfer encoding, without relaxing
  framing or network checks. Fix relay HTTP 500s from an unset response factory
  and release streams when response creation fails.
- **Alphabetical pagination:** Accept bounded station/show name filters, including
  international letters. Normalize TuneIn's unescaped trailing `#` in the
  recognized alphabet filter to `%23`, preventing rejected links and failed
  refreshes without allowing arbitrary URL fragments.
- **Selections and identity:** Configuration-first browsing offers eight groups,
  lazy language/music/location selectors, multiple local areas and optional
  coordinates. Signed navigation enforces saved selections and group switches
  through descendants, pagination, playback and artwork; disabled or stale items
  cannot bypass those scopes. The plugin and library are displayed as TuneIn.

### Refresh and interface fixes

- **Scoped refresh:** One schedulable TuneIn-only task and individual Settings
  actions refresh enabled groups and selections, including descendant pages.
  Staged discovery, bounded cache reuse, configurable request pacing and
  rate-limit handling reduce repeated work. Staged root requests are accepted,
  and swallowed import failures are reported before artwork updates.
- **Timeouts and diagnostics:** A configurable timeout defaults to one hour;
  eligible timed-out discovery can resume from a bounded checkpoint. Status
  distinguishes timeout, cancellation and error, reports stages and request
  counts, and uses matching successful runs for progress estimates. Failed-task
  reports retain useful error context without exposing rejected URL values.
- **Settings and schedules:** Correct Emby checkbox labels, view lifecycle and
  stale page/controller behavior. Sections collapse after Save; an initially
  unlocked settings lock and active plugin refreshes prevent accidental saves.
  Install, update and Save do not request a global refresh. A one-time migration
  restores previously suppressed host schedules without overwriting later edits.
- **Artwork:** Add distinct group and library artwork and protected station logos;
  preserve previously migrated or custom primary images and stable playback IDs.

### Security and release safeguards

- **Host CVE:** For [CVE-2025-64113](https://nvd.nist.gov/vuln/detail/CVE-2025-64113),
  reject affected Emby Server versions and require patched stable 4.9.1.90+ or
  compatible beta 4.9.2.7+. The NuGet advisory was withdrawn because the flaw is
  in the server, not the reference package; installing this plugin does **not**
  patch Emby Server.
- **Untrusted data:** Validate bounded OPML and fixed-host catalogue requests;
  use administrator-only selectors and diagnostics, signed navigation, text-only
  rendering and protected local playback/artwork relays. Public-DNS pinning,
  redirect restrictions, bounded playlists and image validation reduce SSRF and
  unsafe-content risks. Remove unused, unversioned parser sources rather than
  attributing an unverified CVE to them.
- **Release integrity:** Lock dependencies and include build, test, analyzer,
  advisory and CodeQL gates, plus an SBOM, checksums and exact-source evidence.
  These safeguards do not replace host updates or real Emby testing.

## Requirements and installation

Use **Emby Server stable 4.9.1.90 or newer**, or a compatible patched beta
**4.9.2.7 or newer**. The plugin targets `netstandard2.0`, references Emby SDK
4.9.1.90 and relies on host-provided assemblies, including System.Memory 4.0.2.0
or newer. Do not copy SDK dependencies from the build directory. See
[SECURITY.md](SECURITY.md) for the host security requirement.

1. Download `MediaBrowser.Plugins.TuneIn.dll`, `SHA256SUMS.txt` and
   `INSTALL.md` from [v1.3.0-fork.20-rc.2](https://github.com/oconnoradv/EmbyTuneIn/releases/tag/v1.3.0-fork.20-rc.2).
   Keep the accompanying license. Verify the DLL against `SHA256SUMS.txt`, for
   example with `Get-FileHash .\MediaBrowser.Plugins.TuneIn.dll -Algorithm SHA256`
   on Windows or `shasum -a 256 MediaBrowser.Plugins.TuneIn.dll` on Linux/macOS.
2. Stop Emby. Back up the existing plugin DLLs, both TuneIn XML configuration
   files if present, and server data before any library refresh. Locate the
   actual plugins and plugin-configuration directories in Emby's dashboard.
   Remove or move both the old `MediaBrowser.Plugins.TuneIn.dll` and
   `MediaBrowser.Plugins.TuneIn.Fork.dll` out of the plugins directory. Do not
   load two TuneIn implementations together. Cancel pending upstream updates.
3. If both `MediaBrowser.Plugins.TuneIn.xml` and
   `MediaBrowser.Plugins.TuneIn.Fork.xml` exist, the original filename wins;
   neither file is overwritten automatically. To retain the fork's settings,
   back up both, move the original XML out of the configuration directory and
   let the plugin copy the fork XML on startup. If only the original XML exists,
   it is used unchanged. If only the fork XML exists, it is validated and copied
   to the original filename, preserving its signing key and selections. The
   source file is retained. An invalid fork XML stops migration rather than
   replacing settings. Keep all XML backups private.
4. Copy **only** the new DLL into the plugins directory and restart Emby. Open
   **Dashboard > Advanced > TuneIn** or **Dashboard > Plugins > TuneIn >
   Settings**. Confirm the page displays **TuneIn 1.3.0-fork.20-rc.2**;
   reopen it from the dashboard and hard-refresh if needed.
5. Enable desired radio groups and add their selections. Music, Language and
   Location need enabled selections; Favorites needs a username; Local Radio
   needs enabled areas and/or coordinates. Select **Enable configured browsing
   after saving**, then save. Return to the channel root and run a refresh if
   needed.

The plugin ID is `10b82431-f939-4507-a8e2-9509dd3fd6ae`. Original settings
retain their username and coordinates, but configured browsing requires an
explicit save and enabled selections. Existing host library/channel records,
artwork and schedules may not transfer between identities automatically; check
them and run the TuneIn-only refresh if needed. No automatic global refresh is
requested. If installation fails, stop Emby, remove the new DLL and restore the
chosen prior DLL and XML backups; library imports may require separate recovery.
Keep configuration XML private; it contains account, location and signing-key
information. These identity changes have not been tested in a live Emby server.

## Configure and browse

Settings sections are collapsible and collapse after a successful Save. The
refresh section starts collapsed unless a job is active. **Lock settings**
persists separately from the configuration and can be toggled without
interrupting a refresh. Unlock before editing or saving. Avoid saving while
Emby's separate global **Refresh Internet Channels** task is running: the
plugin's save guard covers its own refresh jobs, not that host task.

Use **Load root**, **Expand**, the label filter and **Add** to select languages,
music types and geographic areas. Only the requested selector branch is loaded;
some provider responses contain incidental station metadata, which the plugin
discards. Geographic levels vary by location. Coordinates are validated and
sent only for coordinate-based Local Radio; there is no IP-location fallback.
No stream is played or resolved during setup, and opening an unconfigured
channel root does not contact TuneIn.

The Music folder under a selected language contains only enabled saved music
types, retaining both filters in descendants and pagination. Other groups do not
inherit those music restrictions. Saving invalidates old scoped navigation and
applicable in-flight operations; reopen the channel root afterwards. Old or
disabled selections cannot be accessed through stale signed IDs.

If the library appears empty, enable Talk, enable configured browsing and save.
Talk needs no selector and should appear in the locally constructed root menu.
Other groups remain unavailable until their required selections are enabled.

## Refresh and status

**Refresh TuneIn**, under **Scheduled Tasks > Internet Channels**, refreshes
enabled TuneIn groups and saved selections only. It has no default trigger.
Run it manually or create a schedule in Emby. Settings also has individual
group actions; these do not add scheduled tasks. Emby's separate **Refresh
Internet Channels** task refreshes all providers and remains independently
scheduled. A one-time upgrade migration restores previously suppressed host
schedules only when recorded plugin state exists and the host schedule is still
empty; later administrator edits are preserved.

Each selected group is discovered and staged before Emby imports it. Discovery
is bounded to 1,000 folders, an estimated 32 MiB of staged results and 1,000
catalogue requests per group. Discovery failure or cancellation does not start
that group's import; Emby's import itself is not transactional. Identical
validated requests may reuse a bounded 128-page/8 MiB cache during a run and
for up to ten minutes after a successful refresh. It is cleared on Save or
restart. Differently filtered requests remain separate: a music listing alone
does not establish language or location membership.

In **Setup and account**, the catalogue request interval is adjustable from
1 to 30 whole seconds (default **3**). Calls are serialized and shorter
intervals may trigger provider throttling; no numerical TuneIn limit is claimed.
HTTP 429 responses are retried at most twice, respecting `Retry-After`, and long
provider delays continue to block subsequent catalogue calls.

The refresh timeout is adjustable from 1 to 1,440 minutes (default **60**).
After a timeout, resume can reuse a saved checkpoint of at most 128 pages and
8 MiB and continue remaining groups of a full refresh. Larger checkpoints
require restarting the group. An interrupted Emby import restarts rather than
resuming at a folder. Errors are reported separately from timeouts.

The Settings refresh section displays active group, stage, completed/started
catalogue requests, cache hits and import activity. A matching last successful
full refresh provides an estimate, not a guarantee. Import counts measure
folders requested and items served to Emby, not completed database writes.
Status updates while the page is open and can be requested manually; neither
contacts TuneIn. Emby's scheduled-task API exposes numeric progress but no
plugin-controlled text beneath its progress bar.

For a failed scheduled run, **Retrieve failed Refresh TuneIn report** displays
the stored report for that attempt, not the full server log. The report is
administrator-only, plain text and limited to 65,536 characters. A later run
replaces it. Errors identify the group, phase, request counts and last HTTP
status when available; safe rejected-parameter details omit values and URLs.
Redact private paths, URLs and account information before sharing reports.

**Known limitation:** Emby may remove and recreate category folders when
configuration changes alter their identifiers. Retaining folders until a refresh
renders them obsolete is deferred and is not fixed in this candidate.

## Playback and network protections

MP3/AAC and bounded M3U/PLS alternatives are supported. Emby receives only a
local relay URL; remote stream and playlist destinations are validated, pinned
to public DNS addresses and checked across redirects. Alternative failures do
not expose URLs in logs. HLS, private-network streams, HTTPS-to-HTTP redirect
downgrades, proxy-only networks and malformed or incompatible streams are
rejected. An initial HTTP station URL from the trusted HTTPS catalogue is
permitted. TLS trust, hostname and expiry are checked; online certificate
revocation is not required or claimed.

The recognized TuneIn pre-roll is played in full, then the relay opens one
working live alternative. Alternatives are fallbacks, not a queue, and an ad
error does not silently skip the ad. For these sessions the host-configured
FFmpeg decodes the ad and live stream separately into a single 48 kHz stereo
16-bit PCM/WAV stream. Stations without that recognized pre-roll retain
compressed passthrough. Emby probes/transcodes the local relay; decoding and
transcoding can increase CPU use. Unknown ad formats are not guessed.

Provider availability, geographic and partner restrictions, bounded opening,
playlist and refresh limits still apply. Artwork is served locally, with
validated remote images; existing migrated/custom primary images are preserved.

## Manual testing and troubleshooting

The plugin has been tested against **Emby Server 4.11.0.13 beta**. After
installing, check settings and administrator permissions, save/lock behavior,
category selections, artwork, scheduled and individual refreshes, timeout/recovery, and
settings persistence after restart. Test a station with required pre-roll past
the ad into sustained music, a station without pre-roll such as KEXP, and
stopping playback during both phases. Confirm local relay/transcoding behavior.
When reporting a problem, include the Emby version, OS, category and sanitized
error; do not share full stream URLs, account details, coordinates or XML.

## Build and release verification

Run the focused browsing/pagination regressions with the .NET 8 SDK:

```powershell
dotnet test .\MediaBrowser.Plugins.TuneIn.Tests\MediaBrowser.Plugins.TuneIn.Tests.csproj --filter "FullyQualifiedName~BrowsePaginationTests|FullyQualifiedName~Alphabetic|FullyQualifiedName~BareHash"
```

These tests follow provider OPML links through the channel's signed browsing API
with an injected catalogue transport; they do not contact TuneIn. They verify
that final `filter=s:~#` and `filter=p:~#` pagination values send the hash as `%23`,
preserve already encoded and international alphabet filters, and retain the
maintenance branch's rejection of untrusted destinations and other fragments.

From a clean checkout with a patched .NET 8 SDK, Node 24, GitHub CLI and
FFmpeg available:

```powershell
node .\scripts\verify-release.cjs --online --live --release
```

Set `DOTNET` and `TUNEIN_TEST_FFMPEG` to the respective executables when needed.
The verifier checks locked restore, build, formatting, analyzers, C# and
JavaScript tests, live provider access, dependency advisories and exact-source
version stamping. It produces the DLL, checksums, SBOM, inventory and release
evidence in `artifacts`. Hosted CodeQL is a separate gate. Passing these checks
does not replace real Emby testing or establish future advisory status.

See [CONTRIBUTING.md](CONTRIBUTING.md) for development and upstream-review
guidance, [SECURITY.md](SECURITY.md) for the security model and
[CHANGELOG.md](CHANGELOG.md) for the consolidated RC1 release changes. Obsolete
development releases, their assets and development tags are retired; upstream
commit history and the current RC1 release are preserved.
