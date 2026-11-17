# Changelog

## RC2 - 1.3.0-fork.20-rc.2

Consolidated changes in this fork from the upstream MediaBrowser/TuneIn 1.2.8.0
baseline. This entry replaces the intermediate development release logs and
describes the retained implementation, not superseded behavior.

### Playback

- Play recognized required pre-roll before opening one working live alternative,
  fixing ad-only playback while the client still appeared active.
- Decode ad/live inputs separately to one PCM/WAV format across codec changes;
  retain compressed passthrough for stations without recognized pre-roll.
- Accept provider LF-only headers, repeated advisory ICY fields and legacy identity
  transfer encoding while retaining strict framing and destination validation.
- Fix relay HTTP 500s through constructor-injected response factories; release
  connections and decoders on failure, cancellation and abandoned probes.
- Preserve case-sensitive stream paths and bounded MP3/AAC and M3U/PLS fallback
  resolution without treating live alternatives as a sequential queue.

### Browsing and configuration

- Add configuration-first browsing with eight group switches, lazy administrator
  selectors, saved languages/music/geographies, multiple local areas, validated
  coordinates and station deduplication; no automatic worldwide setup crawl.
- Enforce category and saved-selection ancestry through signed, restart-stable
  navigation, pagination, playback and artwork; reject disabled and stale scopes.
- Restrict language music hubs to saved music selections and honor Music/Talk/Sports
  switches beneath languages without restricting unrelated groups.
- Fix alphabetical station/show filters, including international letters and
  TuneIn's unescaped trailing `#`, normalized to `%23` only in the recognized filter.
- Use the upstream plugin/DLL/XML identity and TuneIn display name. Copy a
  validated previous fork configuration only when the original XML is absent;
  never overwrite an existing original configuration automatically.
- Correct checkbox labels, native settings lifecycle, versioned views and stale
  asynchronous replies. Add clear empty-library diagnostics, collapsible sections,
  an initially unlocked persistent settings lock and save protection during
  plugin-tracked refreshes.

### Refresh and diagnostics

- Provide one TuneIn-only scheduled task with no default trigger plus individual
  category actions in Settings; traverse enabled selections and descendant pages.
- Stage bounded discovery before category import, accept Emby's root query and
  surface swallowed import failures before artwork updates.
- Reuse equivalent validated catalogue responses within bounded caches instead of
  truncating sections; retain separate language/location membership requests.
- Add serialized, adjustable 1-30 second request pacing (default 3 seconds),
  HTTP 429 retry-delay handling and a configurable timeout (default 60 minutes).
- Distinguish timeout, error and cancellation; resume eligible bounded catalogue
  checkpoints and remaining full-refresh categories, restarting interrupted imports.
- Report category, stage, import activity, request/cache counts, matching-run
  estimates and administrator-requested bounded native failure reports.
- Preserve last-success history separately from failed attempts and configuration;
  guard duplicate starts, cancellation, launch failures and completion races.
- Avoid plugin-requested global refreshes on install/update/Save. Remove obsolete
  per-group tasks and schedule suppression; restore recorded host schedules once
  without overwriting subsequent administrator edits.

### Artwork

- Add original library and category artwork, content icons beneath languages and
  protected station PNG/JPEG logos with image identities separate from playback IDs.
- Migrate persisted folder primary images through scoped library updates and
  preserve previously migrated/custom images.

### Security and release safeguards

- Require patched Emby hosts for CVE-2025-64113: stable 4.9.1.90+ or compatible
  beta 4.9.2.7+. The withdrawn NuGet attribution concerned an underlying server
  flaw; the plugin DLL is not an Emby Server security patch.
- Validate fixed-host catalogue URLs and bounded OPML against a trusted schema;
  prohibit DTD/entities and render untrusted labels/reports as text.
- Protect playback/artwork through local relays, public-DNS pinning, redirect
  restrictions, bounded requests/playlists/images and server-side authorization.
- Remove unused unversioned parser sources without claiming an unverified CVE.
  Add locked dependencies, analyzers, regression/live-provider tests, advisory and
  CodeQL gates, exact-source version stamping, SBOM and SHA-256 release evidence.
- Preserve upstream license and commit history. Obsolete development releases,
  attached assets and fork/initial-development tags are retired; RC1 is retained.

### Testing and known limitations

- Previous fork RC1 was tested against Emby Server 4.11.0.13 beta. The RC2 identity
  migration has not been tested in a live host; automated results do not establish
  compatibility with every host/client or provider.
- Category-folder retention remains deferred; configuration changes may cause
  Emby to remove and recreate folders. Host import is not transactional.
- Resume requires a checkpoint within 128 pages/8 MiB. The save guard excludes
  Emby's global refresh, and scheduled-task APIs provide no plugin-controlled text
  beneath the native progress bar.
- Provider restrictions, traversal/request/time bounds and rejected HLS/private
  streams still apply. Online certificate revocation checking is not claimed.
  See README.md and SECURITY.md for operating and security details.
