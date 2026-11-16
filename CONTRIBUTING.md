# Contributing and preparing an upstream pull request

The original repository is [MediaBrowser/TuneIn](https://github.com/MediaBrowser/TuneIn),
with `master` as its default branch. This fork retains upstream history and license.
No upstream pull request or merge is created by building or publishing this fork.

## Versioning and independent identity

Upstream currently declares assembly/file version `1.2.8.0`. The fork's new
configuration/refresh functionality uses SemVer `1.3.0-fork.1`: the prerelease
suffix identifies a fork test candidate, not an official upstream release.
The `.csproj` `<Version>` is the source of truth. The SDK derives assembly/file
version `1.3.0.0`; informational version includes the exact source commit.
Increment the fork suffix for another candidate, patch for compatible fixes,
minor for compatible features, and major for incompatible public behavior.
Upstream maintainers decide their own final release number and compatibility policy.

The HTTP user agent, configuration-controller name and release verifier derive the
version rather than maintaining independent constants. When changing `<Version>`,
update the HTML heading/controller reference and README release references.
Verification rejects inconsistent page/documentation versions. Never replace
published tags/assets or relabel v0.0.1-v0.0.3; they remain historical releases.

Keep the independent plugin GUID, `MediaBrowser.Plugins.TuneIn.Fork.dll` and XML
filename, and `TuneInFork` routes while manually testing this fork. Otherwise Emby
can replace it with upstream again. Upgrades from v0.0.2/v0.0.3 preserve settings.
Do not install both implementations together.

## Build and review gates

Use .NET SDK 8 and Node.js 24. No npm dependencies are required. Locked restore,
compiler warnings/analyzers, formatting, C#/JavaScript regression tests and
transitive dependency auditing run with:

```powershell
node .\scripts\verify-release.cjs --online --live
```

`--online` requires authenticated `gh` access for advisory reads; `--live` makes
the explicit pinned-DNS TLS catalogue test. Set `DOTNET` if the SDK executable
is not on PATH. For CI/offline development omit those flags; that is not a complete
release audit. Use `--online --live --release` only after committing a clean tree.
Hosted C#/JavaScript security-extended CodeQL must also pass for the exact revision.
An unavailable/failed audit is not a clean result.

No binaries, local tools, private XML configuration, credentials or generated
release evidence belong in the source commit. The removed HtmlAgilityPack/IniParser
sources are unused and replaced by bounded platform parsing; do not reintroduce them.
Retain the upstream license and editable/static assets that remain applicable.

## Upstream review checklist

Prepare the PR from the tested feature branch in `oconnoradv/EmbyTuneIn` to
`MediaBrowser/TuneIn:master`, not from this fork's unchanged baseline `master`.
Fetch the latest upstream `master` and inspect the comparison before submission;
do not force-push, discard work or merge upstream changes without reviewing them.

Explain configuration-first defaults, selection scope, bounded refresh/progress,
SSRF/OPML safeguards and tests. Include exact revision/check evidence and the manual
Emby checklist results; do not claim installation, CSRF, dashboard or playback
testing that has not actually occurred.

Call out these maintainer decisions rather than silently shipping fork identity
as an official upstream replacement:

- Independent fork GUID/assembly/config filenames and dashboard/API names. Upstream
  may choose its original identity, but must review configuration migration and
  ensure the secure-default browsing gate remains intact.
- SDK/minimum host change from 4.6.0.50 references to compatible patched 4.9.1.90+
  stable or 4.9.2.7+ beta; the DLL is not itself an Emby Server CVE patch.
- Explicit selector requests can include incidental station metadata, discarded
  during discovery; no automatic catalogue crawl or setup playback.
- Protected local relay and transcoding instead of handing untrusted stream URLs
  directly to Emby; supported/unsupported formats and network policies are documented.
- Per-group refresh covers saved first pages only. Native host synchronization is
  bounded/cache-only and must be exercised on the real server before acceptance.

Do not submit or advertise the PR as runtime-verified until the administrator has
completed the relevant Emby manual tests. See README.md and SECURITY.md.
