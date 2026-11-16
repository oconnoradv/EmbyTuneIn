# Contributing and preparing an upstream pull request

The original repository is [MediaBrowser/TuneIn](https://github.com/MediaBrowser/TuneIn),
with `master` as its default branch. This fork retains upstream history and license.
No upstream pull request or merge is created by building or publishing this fork.

## Versioning and independent identity

Upstream currently declares assembly/file version `1.2.8.0`. The fork's new
configuration/refresh functionality uses SemVer `1.3.0-fork.11`: the prerelease
suffix identifies a fork test candidate, not an official upstream release.
The `.csproj` `<Version>` is the source of truth. The SDK derives assembly/file
version `1.3.0.0`; informational version includes the exact source commit.
Increment the fork suffix for another candidate, patch for compatible fixes,
minor for compatible features, and major for incompatible public behavior.
Upstream maintainers decide their own final release number and compatibility policy.

The HTTP user agent, configuration-controller name and release verifier derive the
version rather than maintaining independent constants. When changing `<Version>`,
update the HTML heading/controller reference and README release references.
Both main-page and controller names are derived from that version to avoid
restoring stale dashboard markup. Use plugin `viewshow`/`viewbeforehide` events;
let Emby's plugin adapter provide BaseView and native transition handling rather
than depending on a legacy `baseView` AMD alias.
Verification rejects inconsistent page/documentation versions. Never replace
published tags/assets. Superseded prerelease records/assets may be retired only
on the repository owner's explicit request, after replacement artifacts and
consolidated documentation are verified; preserve original source tags.

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

Inject service dependencies, including `IHttpResultFactory`, through constructors.
Do not depend on `IHasResultFactory` property population: an unset factory caused
relay HTTP 500 responses. Transfer audio-stream ownership to the host response
only on success; release it if response construction fails.

Pre-roll sessions use the host's `IFfmpegManager` encoder path and pipe-only audio
decoding. Keep the single fixed PCM/WAV output format for both probing and playback;
do not concatenate MP3/AAC or turn live alternatives into a sequential queue.
The optional real-codec/process regression tests require
`TUNEIN_TEST_FFMPEG` to point to a local FFmpeg executable (including MP3/AAC and
the test tone generator). Run them alongside the deterministic handoff tests:

```powershell
dotnet test .\MediaBrowser.Plugins.TuneIn.Tests --filter "(FullyQualifiedName~PlaybackTests|FullyQualifiedName~TransportTests)&Category!=Live"
```

Without that variable the real-codec tests are explicitly skipped, not counted
as codec validation. Passing them does not establish real Emby runtime behavior.
With that variable set, `dotnet test .\MediaBrowser.Plugins.TuneIn.Tests --filter
"FullyQualifiedName~LiveProviderPreRollHandsOff"` explicitly contacts four provider
stations through the pinned transport and checks decoded audio after the pre-roll.
These Live tests depend on provider availability and may consume required ads.

Item artwork URLs must stay on the server's loopback endpoint. Keep station logo
references separately signed so artwork updates cannot change playback/item IDs.
Never hand raw provider image URLs to Emby's downloader or the client.

Category PNGs are checked-in source assets. Regenerate the eight original icon
cards on Windows with `powershell.exe -NoProfile -ExecutionPolicy RemoteSigned -File .\scripts\New-CategoryArtwork.ps1`
(process-scoped policy only; System.Drawing and Segoe UI required). Inspect the
results before committing. The root library assets are an original futuristic
544x306 landscape design without a FORK subtitle; preserve that exact size.
Do not alter station logos when migrating bundled root/group artwork.

Keep refresh history and schedule/trigger backups in the independent state file,
not the signed station configuration. Schedule changes must be administrator-only,
explicitly applied and reversible; never change the built-in all-provider task's
triggers on startup or ordinary configuration Save. Its read-only description and
native execution result must not be modified through reflection or fabricated.
The settings page has no refresh/interval sections or polling. Native group tasks
remain the primary interface; compatibility refresh APIs must execute the real
native task via `ITaskManager`, so native Last ran remains genuine.
Do not implement `IHasChangeEvent` or queue the global task on install/update/Save:
configuration invalidation must stay local. Existing host triggers are independent.
Native channel import preserves existing primary images. Folder artwork upgrades
must update/persist the scoped library items' primary image references, retain
stable playback IDs, and use an artwork revision marker to avoid repeated resets.

## Upstream review checklist

Prepare the PR from the tested feature branch in `oconnoradv/EmbyTuneIn` to
`MediaBrowser/TuneIn:master`. The fork's `master` contains the consolidated
squash-merged implementation.
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
- Eight Internet Channels tasks and compatible administrator APIs populate configured sections
  via the host's eight-level traversal, with on-demand scoped requests and explicit
  request/time limits. Native registration, cancellation and actual station import
  must be exercised on the real server before acceptance.

Do not submit or advertise the PR as runtime-verified until the administrator has
completed the relevant Emby manual tests. See README.md and SECURITY.md.
