# Security and release policy

## Trust boundaries and secure defaults

New and legacy configurations remain disabled until an administrator explicitly
configures/enables/saves them. Missing selections do not fail open. Selector APIs
use Emby's `Authenticated(Roles = "Admin")`; configuration writes use Emby's built-in
administrator configuration API, not a new unauthenticated/CSRF-exempt write route.
Actual host authorization and browser integration require manual Emby testing.

Per-group refresh start/cancel routes are POST-only and all refresh/status routes
require `Authenticated(Roles = "Admin")`, without a local authentication bypass.
They use the host ApiClient authentication and host CSRF policy, accept only a fixed
category and an opaque current job ID, and never accept URLs, coordinates or custom
request parameters. Verify real-host non-admin/anonymous and cross-origin behavior
in manual tests; SDK attributes alone are not proof of browser CSRF enforcement.
Status polling performs no provider requests. Jobs are bounded to eight category
slots, one active job/category, enabled saved scopes, 1000 catalogue requests and a 20-minute limit;
configuration/category invalidation cancels applicable requests. The response cache
is capped at 128 pages/8 MiB serialized XML and remains protected by the same schema.
Native scoped Emby synchronization fetches selected descendants on demand to the
same eight-level limit as the host scheduled refresh. Cross-group or unfiltered
selection-hub escapes are rejected; failures swallowed by the host are remembered
and rethrown at the job boundary. Missing saved-node enumeration is not success.
The eight dedicated native tasks have no default triggers and reuse the same gates,
scope, cancellation, limits and failure reporting; a disabled group never queries.
Compatibility refresh APIs execute those same native workers so host task history
is genuine. The settings page has no refresh/interval controls or status polling.
A pending launch cannot contact TuneIn before native execution starts;
launch failures/no-ops and pre-start cancellation cannot claim a populated section.
Status exposes response codes/counts/states, not request URLs or configuration secrets.

Schedule GET/POST endpoints also require an authenticated administrator. Applying
shared/per-group intervals or disabling Emby's all-provider refresh triggers is
explicit opt-in; startup and ordinary configuration Save never change triggers.
Only fixed task keys are managed. Integer intervals and unique category overrides
are validated before mutation; original triggers are persisted first and restored
when management is released. Trigger-write errors attempt rollback and remain
visible failures. Verify actual host authorization/CSRF and trigger persistence
manually, especially because the built-in task affects every channel provider.
Refresh history and schedule/trigger backups use a separate private data file;
bounded XML loading prohibits DTD/entities. They do not change signed station
configuration or expose account/coordinate values. Protect and back up the state
file until managed triggers have been restored; active counters remain in memory.

Navigation identifiers are bounded, signed payloads: HMAC-SHA256 binds category,
selection ancestry/navigation parameters and the entire configuration fingerprint.
The key is generated at runtime and persisted in administrator-only plugin
configuration. Signature checks precede parsing; XML DTD/entities are prohibited.
Identifiers survive unchanged-config restarts but are rejected after settings,
selection or category changes. Coordinates/account are derived from current
configuration, not embedded in navigation IDs. Cache invalidation stays inside the
plugin: `IHasChangeEvent` and deprecated `IHasCacheKey` are not implemented.
Install/update/Save do not request the global task; this does not disable existing
host/administrator task triggers.

Catalogue requests are rebuilt as escaped HTTPS queries to the exact
`opml.radiotime.com` host and approved Browse/Describe/Tune paths. Legacy HTTP links
are treated only as data from that trusted HTTPS response and rebuilt as HTTPS;
HTTP catalogue requests and arbitrary client/persisted URLs are never accepted.
Related paths are dropped, language scopes are reapplied and cross-node pagination
is rejected. Remote titles use text DOM rendering, not XPath or HTML interpolation.
Untrusted remote image URLs are not emitted to Emby. Item image URLs target a
loopback-only endpoint with the same signed/configuration-gated authorization as
playback. Group PNGs are embedded resources. Station logos retain only a signed
CDN index/relative path (separate from stable playback IDs), never an arbitrary host.
Only the API-advertised `cdn-profiles.tunein.com` and
`cdn-radiotime-logos.tunein.com` destinations are allowed, upgraded to HTTPS;
every redirect remains on those hosts and every DNS answer is public/pinned.
Downloads have a 30-second/2 MiB budget and PNG/JPEG signature/dimension limits
(4096 per side, 16 Mi pixels). SVG/active content and other formats are rejected.
Missing/rejected logo metadata uses bundled group artwork, with rejected
destinations logged without URLs. Image retrieval is lazy and does not query the
station catalogue or resolve/play a stream; explicit refresh populates image URLs
for existing items. Emby's image cache/rendering still needs real-server verification.
Group refresh also migrates obsolete stored primary image references on the chosen
section's folders. Every folder must resolve through current signed navigation and
belong to that group; stations and other groups are not modified. Only the same
loopback artwork URLs are written. No provider URLs are downloaded by this
migration, no arbitrary image files are deleted, and the per-folder revision marker
avoids overwriting subsequent custom images on every refresh.

The relay's HTTP result factory is constructor-injected, not assumed to be populated
through a service property. A response-construction failure releases the opened
radio connection and its operation rather than leaking the stream.

The server-local relay performs radio and playlist I/O itself. Every target and
redirect is URI-validated and DNS-checked; a single non-public answer rejects the
destination. Connections use the validated IP directly while TLS uses the original
hostname and default certificate trust/name/expiry validation. There is no DNS
re-resolution at connection time. Loopback, private, link-local, multicast,
carrier-grade NAT, mapped IPv6, documentation and transition ranges are rejected.
Redirects are bounded and HTTPS downgrades are rejected. HTTP headers, metadata,
playlist sizes and nesting are bounded; API faults inside HTTP 200 are errors.
Radio response status/header lines may end in LF instead of CRLF for legacy
provider compatibility. Repeated advisory ICY/CORS/cache headers are merged, but
duplicate Content-Length, Transfer-Encoding, Content-Encoding, Content-Type and
Location fields are rejected, as are invalid header names/control characters.
Legacy radio `Transfer-Encoding: identity` is close-delimited; any transfer
encoding combined with Content-Length is rejected. Catalogue parsing and chunk
framing remain strict.
No unrestricted stream URLs reach Emby's HTTP client/transcoder. Relay access
requires a server-local socket, with Emby's local-only authentication attribute as
an additional host control. In-flight requests are cancelled after settings changes.

Recognized provider pre-roll sequences are normalized with the host's configured
FFmpeg executable. FFmpeg receives only piped bytes from the same validated
transport, with its input protocol whitelist restricted to `pipe`; remote URLs
are never passed to it. Only the exact known HTTPS pre-roll host/path has sequential
semantics; all live URLs retain bounded fallback resolution. Decoder diagnostics
are drained without retaining provider metadata. Decode/transport failures are
errors, not permission to skip required pre-roll. Probe disposal and cancellation
terminate the decoder and close its upstream response.

TLS 1.2 is required. Online CRL/OCSP revocation is not mandatory, matching ordinary
.NET client behavior. The local network could not reach those services; their
availability is not silently counted as a passed check. Certificate trust/name/expiry
validation remains enabled, but **no online revocation-clean claim** is made.
Outbound system proxies and private radio services are not supported by the relay.
Filesystem permissions must protect Emby's configuration and runtime signing key.

## CVE/advisory applicability and host requirements

The plugin uses current stable `MediaBrowser.Server.Core`/`MediaBrowser.Common`
4.9.1.90 references and System.Memory 4.6.3. Exact direct/transitive versions and
NuGet content hashes are locked; the release SBOM/inventory are generated from
the resolved lock and include host-supplied/build-only components marked excluded
from binary distribution. Only the plugin DLL is installed, not host libraries.

The legacy bundled HtmlAgilityPack snapshot has no reliable pinned package/version
provenance in this repository. Do not invent a CVE/version mapping for it.
Its unused source and obsolete IniParser helper have been removed; bounded platform
XML/playlist parsing replaces them. Git history retains the original source; neither
parser is part of the emitted DLL.

**CVE-2025-64113 / GHSA-95fv-5gfj-2r84:** GitHub withdrew the NuGet advisory because it
incorrectly attributed an Emby Server flaw to the `MediaBrowser.Server.Core`
reference package. NVD still describes the underlying **server** vulnerability.
It affects stable hosts before 4.9.1.81 and beta hosts before 4.9.2.7. This plugin
requires the newer SDK-compatible stable **4.9.1.90+**, or compatible patched beta
**4.9.2.7+**, and rejects affected versions at load. Installing this DLL is **not**
a server security patch. Keep the actual Emby server and runtime updated.

Sources:
[withdrawn GitHub advisory](https://github.com/advisories/GHSA-95fv-5gfj-2r84),
[NVD CVE record](https://nvd.nist.gov/vuln/detail/CVE-2025-64113).

NuGet transitive auditing and exact-package GitHub advisory checks are release
gates. Active applicable findings, unavailable feeds and incomplete/failed audits
are not clean results. NVD is consulted for returned CVE identities; it is not a
claim to scan every CVE/CPE for the user's OS/server. Advisory age or dependency age
alone is not a verified vulnerability. Published evidence records the exact revision
and check state; rerun checks when rebuilding or receiving a new advisory.

## Practical NIST SSDF alignment (not certification)

The fork has an independent plugin ID, DLL/configuration filenames and dashboard/
API names. Upstream TuneIn updates must not replace its hardened implementation.
When migrating from v0.0.1, remove the original DLL and cancel pending upstream
updates; do not install both implementations together. The Advanced-menu link
uses the same main configuration page and does not relax administrator checks.

Remote OPML is validated against a trusted local structural schema before use.
DTD/entity resolution and remote/inline schema loading are disabled; only body
outline rows participate in selector discovery. Provider-specific attributes
remain data and are validated separately before constructing requests.

| SSDF practices | Repeatable evidence/control |
|---|---|
| PO.1 / PO.4 | This policy states trust boundaries, host prerequisites and release criteria. CI and the release verifier reject failed build/test/audit/quality gates; publication requires the tested revision. |
| PW.5 / PW.7 | Escaped fixed-host requests, signed navigation, pinned public-address transport, admin/local authorization, secure defaults; compiler analyzers and CodeQL C#/JavaScript security-extended checks plus focused security review. |
| PW.8 / PW.9 | Request-count, empty-selection, stale/signed-ID, coordinate, hierarchy, language, lazy pagination, deduplication, private/mapped IPv6, redirect, malicious XML/body faults, size limits, local authorization and text-only UI regression tests. |
| PS.2 / PS.3 | Locked restore/package hashes, warning-free build, exact source revision, embedded license, CycloneDX SBOM, SHA-256 manifest and release evidence. No host SDK libraries are distributed with the DLL. |
| RV.1 / RV.2 | Live NuGet/GHSA/NVD advisory cross-check, explicit withdrawn/advisory applicability records, failure-on-findings CI, and reporting/remediation procedure below. |

No Emby server runtime was available. Compilation against the current SDK and
automated tests are **not** proof of successful plugin installation, real host CSRF/
authorization enforcement, dashboard rendering, transcoding or station availability.
Manual tests in the README/INSTALL document are required. CI CodeQL status comes
from its actual revision run; a configured workflow alone is not a passed scan.
The checksum provides integrity matching, not an independent publisher signature.

## Reporting and remediation

Use this repository's GitHub **Security / Advisories / Report a vulnerability**
if private reporting is enabled. Otherwise contact the repository maintainer
privately before sharing exploit details; ordinary non-sensitive bugs can use Issues.
Do not publish configuration keys, account values, coordinates or full stream URLs.

For an applicable disclosure: confirm affected code/version and scope, disable
affected functionality or withhold the release, implement a minimal fix, add a
regression case, repeat build/test/advisory/CodeQL checks, then publish a new tag
and checksum/evidence. Never silently replace an existing release/tag or call a
failed/unavailable scanner clean. The upstream insecure DLL is not a safe security
rollback; disabling/removing the plugin is safer pending remediation.
