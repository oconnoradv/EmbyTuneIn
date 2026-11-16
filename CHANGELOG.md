# Consolidated fork history

This history covers the enhancements added in oconnoradv/EmbyTuneIn beyond the
upstream MediaBrowser/TuneIn 1.2.8.0 baseline. README.md (distributed as INSTALL.md),
SECURITY.md and CONTRIBUTING.md describe the current behavior in full. Historical
features explicitly removed below are not promises of current functionality.
Superseded prerelease download records/assets are retired after the replacement
release is verified; original source tags remain unchanged for provenance.

## 1.3.0-fork.9 - consolidated release

- Original futuristic 544x306 library Primary/Thumb/Backdrop artwork, without a
  FORK subtitle. An explicit group refresh migrates existing parent-channel images.
- Remove individual radio-group refresh and automatic-interval sections from
  plugin settings, including their AJAX requests, progress widgets and timers.
  Keep the eight native Scheduled Tasks, scheduling, cancellation and history.
- Remove provider content-change notifications that can request global refreshes;
  install/update/Save do not queue Refresh Internet Channels. Host-owned triggers
  and explicit administrator actions remain independent.
- Consolidate all fork enhancements and upgrade/security limitations in the latest
  documentation and release assets; squash accumulated source into the fork's
  actual default branch, master. No upstream submission is implied.

## 1.3.0-fork.8

- Migrate persisted folder Primary images using scoped library updates; changing
  the advertised URL alone did not replace images already stored by Emby.
- Route settings refresh buttons through real native task execution so native
  Last ran and shared history update consistently; guard startup, duplicate jobs,
  pre-start cancellation, launch failures and history-publication races.
- Remove the repeated built-in-task explanation from TuneIn task descriptions.
- Correct host-managed settings lifecycle/transitions, version HTML/controller
  routes, use native input/select label containers and ignore stale config replies.
- Settings buttons were subsequently removed in fork.9; native execution remains.

## 1.3.0-fork.7

- Persist per-group last-success and last-attempt/result independently of signed
  station configuration, retaining success after failure/cancellation.
- Distinct original artwork for all eight groups and content icons beneath languages.
- Introduce explicitly applied shared/per-group interval controls and reversible
  all-provider global-trigger management with persisted backups and rollback.
  These settings-page controls were removed in fork.9. Existing native triggers,
  backups and administrator-only compatibility APIs remain; nothing reapplies
  scheduling automatically.
- The global task always honored TuneIn configuration; no unrestricted bypass
  was implemented. The repeated notice was removed from task descriptions in fork.8.

## 1.3.0-fork.6

- Language Music hubs list only enabled saved music selections. Signed descendants,
  subgenres, stations, pagination and artwork preserve both language and music scope.
- Reject removed/disabled and legacy unscoped items, including after restart.
  Do not impose this language-specific music intersection on unrelated groups.

## 1.3.0-fork.5

- Music/Talk/Sports switches apply beneath every language, through all descendant
  folders, stations, playback and artwork. Preserve the selected language on requests.

## 1.3.0-fork.4

- Bundled category-folder artwork and station PNG/JPEG logos through signed
  loopback-only endpoints, independent of stable playback IDs.
- Restrict remote images to two verified TuneIn CDNs, HTTPS, pinned public DNS,
  bounded redirects/time/bytes and validated raster dimensions/signatures.

## 1.3.0-fork.3

- Fix relay HTTP 500 by requiring constructor-injected IHttpResultFactory rather
  than relying on property population. Dispose opened radio resources when
  response creation fails; retain normal server probing/transcoding.

## 1.3.0-fork.2

- Eight dedicated native group tasks populate actual selected station metadata
  through the host's eight-level descendant/pagination traversal, not only one page.
- Scoped cancellation, growing request totals, HTTP/API-fault reporting, missing-node
  and swallowed-provider-error detection; 1000-request/20-minute limits.
- Bound/evict cache entries rather than truncating sections at cache capacity.
- Collapsible settings with accessible Save, later reduced to four sections in fork.9.

## 1.3.0-fork.1

- Independent group refresh/cancel/status APIs and controls, bounded jobs and scoped
  cache warming. Native full-section population superseded first-page warming in fork.2.
- Remove unused HtmlAgilityPack/IniParser sources, centralize SemVer/source stamping,
  preserve upstream license/history and document upstream-review decisions.
- Settings refresh controls were removed in fork.9; protected APIs remain compatible.

## 0.0.3

- Correct native checkbox label/caption markup and dynamic widget initialization.
- Explicit disabled-browsing/no-enabled-category diagnostics and refreshed controller
  identity, preventing an apparently empty library with no setup explanation.

## 0.0.2

- Independent plugin GUID, DLL/XML identity, dashboard/controller/API namespaces and
  Advanced-menu entry point so upstream updates cannot replace the fork.
- Preserve subsequent fork settings across upgrades; leave old upstream/v0.0.1 XML
  untouched rather than silently importing it.

## 0.0.1

- Configuration-first gate and eight category switches; selected languages, music
  genres, geographic hierarchy, multiple local areas and validated coordinates.
- Explicit lazy administrator selectors with disclosed/discarded incidental geographic
  station metadata; no worldwide setup crawl or silent IP-location fallback.
- Stable signed navigation, server-side selection/category enforcement, stale-ID
  rejection, scoped pagination, coordinate propagation and station deduplication.
- Exact catalogue URL policy and bounded schema-validated OPML parsing; protected
  local relay, public-DNS pinning, redirect checks, case-sensitive stream paths,
  bounded M3U/PLS alternatives and forced server transcoding.
- Patched Emby host minimum, locked dependencies, clean build/analyzers/format/tests,
  NuGet/GitHub/NVD advisory gates, security-extended CodeQL, SBOM and checksums.
- Trusted OPML schema validation and security-review findings were fixed before
  publication. NIST SSDF alignment is practical evidence, not certification.

## Continuing limitations

Real Emby installation, dashboard/authentication/CSRF, library images, scheduled
task integration and playback require manual verification. Provider availability,
geographic levels/restrictions, eight-level/request/time limits, rejected HLS/private
streams and unavailable online revocation checking remain documented limitations,
not removed features or guarantees. See INSTALL.md/README.md and SECURITY.md.
