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
slots, one active job/category, enabled saved starting pages and a 20-minute limit;
configuration/category invalidation cancels applicable requests. The response cache
is capped at 128 pages/8 MiB serialized XML and remains protected by the same schema.
Native scoped Emby synchronization is cache-only: requests outside that group's
refreshed first pages are rejected, and missing enumeration is not counted as success.
Status exposes response codes/counts/states, not request URLs or configuration secrets.

Navigation identifiers are bounded, signed payloads: HMAC-SHA256 binds category,
selection ancestry/navigation parameters and the entire configuration fingerprint.
The key is generated at runtime and persisted in administrator-only plugin
configuration. Signature checks precede parsing; XML DTD/entities are prohibited.
Identifiers survive unchanged-config restarts but are rejected after settings,
selection or category changes. Coordinates/account are derived from current
configuration, not embedded in navigation IDs. Emby's current `IHasChangeEvent`
notifies cache consumers; deprecated `IHasCacheKey` is not relied on.

Catalogue requests are rebuilt as escaped HTTPS queries to the exact
`opml.radiotime.com` host and approved Browse/Describe/Tune paths. Legacy HTTP links
are treated only as data from that trusted HTTPS response and rebuilt as HTTPS;
HTTP catalogue requests and arbitrary client/persisted URLs are never accepted.
Related paths are dropped, language scopes are reapplied and cross-node pagination
is rejected. Remote titles use text DOM rendering, not XPath or HTML interpolation.
Untrusted remote image URLs are not emitted to Emby.

The server-local relay performs radio and playlist I/O itself. Every target and
redirect is URI-validated and DNS-checked; a single non-public answer rejects the
destination. Connections use the validated IP directly while TLS uses the original
hostname and default certificate trust/name/expiry validation. There is no DNS
re-resolution at connection time. Loopback, private, link-local, multicast,
carrier-grade NAT, mapped IPv6, documentation and transition ranges are rejected.
Redirects are bounded and HTTPS downgrades are rejected. HTTP headers, metadata,
playlist sizes and nesting are bounded; API faults inside HTTP 200 are errors.
No unrestricted stream URLs reach Emby's HTTP client/transcoder. Relay access
requires a server-local socket, with Emby's local-only authentication attribute as
an additional host control. In-flight requests are cancelled after settings changes.

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
