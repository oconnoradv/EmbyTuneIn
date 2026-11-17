'use strict';
const { spawnSync } = require('node:child_process');
const fs = require('node:fs');
const path = require('node:path');
const crypto = require('node:crypto');
const https = require('node:https');

const root = path.resolve(__dirname, '..');
process.chdir(root);
const dotnet = process.env.DOTNET || 'dotnet';
const online = process.argv.includes('--online');
const live = process.argv.includes('--live');
const release = process.argv.includes('--release');
const out = path.join(root, 'artifacts');
const projectFile = path.join('MediaBrowser.Plugins.TuneIn', 'MediaBrowser.Plugins.TuneIn.csproj');
const versions = [...fs.readFileSync(projectFile, 'utf8').matchAll(/<Version>([^<]+)<\/Version>/g)];
if (versions.length !== 1 || !/^[0-9]+\.[0-9]+\.[0-9]+(?:-[0-9A-Za-z.-]+)?$/.test(versions[0][1]))
    throw new Error('The project must declare one valid release version.');
const version = versions[0][1];
const assemblyName = 'MediaBrowser.Plugins.TuneIn';
const pluginFile = assemblyName + '.dll';

function run(command, args, quiet = false) {
    const result = spawnSync(command, args, {
        cwd: root, encoding: 'utf8', timeout: 600000,
        env: { ...process.env, DOTNET_CLI_TELEMETRY_OPTOUT: '1' }
    });
    if (result.error || result.status !== 0) {
        process.stderr.write(result.stdout || '');
        process.stderr.write(result.stderr || '');
        throw new Error(`${command} ${args[0]} failed: ${result.error || result.status}`);
    }
    if (!quiet) process.stdout.write(result.stdout);
    return result.stdout.trim();
}

function json(file, value) {
    // Do not put local user paths or machine identifiers in public release evidence.
    const text = JSON.stringify(value, null, 2).split(root.replaceAll('\\', '/')).join('.')
        .split(root).join('.');
    fs.writeFileSync(path.join(out, file), text + '\n');
}

function fetchAdvisory(url) {
    return new Promise((resolve, reject) => {
        const request = https.get(url, response => {
            if (response.statusCode !== 200) {
                response.resume();
                reject(new Error(`Advisory HTTP status ${response.statusCode}`));
                return;
            }

            let body = '';
            response.on('data', data => {
                body += data;
                if (body.length > 4 * 1024 * 1024) request.destroy(new Error('Advisory response too large'));
            });
            response.on('end', () => {
                try { resolve(JSON.parse(body)); } catch (error) { reject(error); }
            });
            response.on('error', reject);
        });
        request.setTimeout(30000, () => request.destroy(new Error('Advisory request timed out')));
        request.on('error', reject);
    });
}

async function get(url) {
    for (let attempt = 0; attempt < 3; attempt++) {
        try { return await fetchAdvisory(url); } catch (error) {
            if (attempt === 2 || !/HTTP status (429|502|503|504)|timed out|ECONNRESET/.test(error.message)) throw error;
            console.error('Transient NVD failure; retrying in 6 seconds (no successful audit recorded yet).');
            await new Promise(resolve => setTimeout(resolve, 6000));
        }
    }
}

async function main() {
    fs.mkdirSync(out, { recursive: true });
    // Invalidate success-shaped outputs before any gate can fail.
    for (const file of ['release-evidence.json', 'MediaBrowser.Plugins.TuneIn.dll', pluginFile, 'SHA256SUMS.txt']) {
        const target = path.join(out, file);
        if (fs.existsSync(target)) fs.unlinkSync(target);
    }
    if (release && (!online || !live)) throw new Error('Release verification requires --online and --live.');
    const html = fs.readFileSync(path.join('MediaBrowser.Plugins.TuneIn', 'Configuration', 'configPage.html'), 'utf8');
    if (!html.includes(`TuneIn ${version}</h2>`)
        || !html.includes(`__plugin/tuneinforkjs${version.replaceAll('.', '').replaceAll('-', '')}`))
        throw new Error('Configuration page/controller version differs from the project.');
    if (fs.readFileSync('README.md', 'utf8').split(/\r?\n/)[0] !== `# TuneIn for Emby - fork v${version}`)
        throw new Error('Installation documentation version differs from the project.');
    const dirty = run('git', ['status', '--porcelain'], true);
    if (release && dirty) throw new Error('Commit all source changes before building a release.');
    const revision = run('git', ['rev-parse', 'HEAD'], true);
    if (!/^[0-9a-f]{40}$/.test(revision)) throw new Error('Invalid source revision.');
    process.env.SourceRevisionId = revision;
    run(dotnet, ['restore', 'MediaBrowser.Plugins.TuneIn.sln', '--locked-mode', '-v', 'quiet']);
    run(dotnet, ['build', 'MediaBrowser.Plugins.TuneIn.sln', '-c', 'Release', '--no-restore', '-t:Rebuild', '--nologo', '-v', 'quiet']);
    run(dotnet, ['format', 'MediaBrowser.Plugins.TuneIn.sln', 'whitespace', '--no-restore', '--verify-no-changes', '--verbosity', 'quiet']);
    run(dotnet, ['format', 'MediaBrowser.Plugins.TuneIn.sln', 'analyzers', '--no-restore', '--verify-no-changes', '--severity', 'warn', '--verbosity', 'quiet']);
    const testResult = run(dotnet, ['test', 'MediaBrowser.Plugins.TuneIn.sln', '-c', 'Release',
        '--no-build', '--no-restore', '--nologo', ...live ? [] : ['--filter', 'Category!=Live'], '-v', 'quiet']);
    run(process.execPath, ['--check', path.join('MediaBrowser.Plugins.TuneIn', 'Configuration', 'configPage.js')]);
    const jsResult = run(process.execPath, ['--test', path.join('MediaBrowser.Plugins.TuneIn.Tests', 'configPage.test.cjs')]);
    const jsCount = jsResult.match(/tests (\d+)/)?.[1];
    if (!jsCount || Number(jsCount) === 0 || !testResult.includes('Passed!'))
        throw new Error('No verified test result summary.');
    const inventory = JSON.parse(run(dotnet, ['list', 'MediaBrowser.Plugins.TuneIn.sln',
        'package', '--include-transitive', '--format', 'json'], true));
    const audit = JSON.parse(run(dotnet, ['list', 'MediaBrowser.Plugins.TuneIn.sln',
        'package', '--vulnerable', '--include-transitive', '--format', 'json'], true));
    if (!audit.sources?.length || audit.projects?.length !== 2 || audit.problems?.length)
        throw new Error('Incomplete or failed NuGet audit.');
    for (const project of audit.projects) {
        if (project.problems?.length) throw new Error('NuGet project audit failed.');
        for (const framework of project.frameworks || []) {
            for (const item of [...framework.topLevelPackages || [], ...framework.transitivePackages || []])
                if (item.vulnerabilities?.length) throw new Error(`Applicable package vulnerability: ${item.id}`);
        }
    }
    json('dependency-inventory.json', inventory);
    json('nuget-audit.json', audit);
    const lock = JSON.parse(fs.readFileSync(path.join('MediaBrowser.Plugins.TuneIn', 'packages.lock.json'), 'utf8'));
    const packages = Object.entries(Object.values(lock.dependencies)[0]).map(([name, value]) => ({ name, ...value }));
    const advisories = [];
    const nvd = [];
    if (online) {
        for (const item of packages) {
            const records = JSON.parse(run('gh', ['api', '-X', 'GET', 'advisories', '-f', 'ecosystem=nuget',
                '-f', `affects=${item.name}@${item.resolved}`, '-f', 'per_page=100'], true));
            if (!Array.isArray(records) || records.length >= 100) throw new Error('Incomplete GitHub advisory response.');
            for (const record of records) {
                advisories.push({ package: item.name, version: item.resolved, ghsa: record.ghsa_id,
                    cve: record.cve_id, withdrawn: record.withdrawn_at, summary: record.summary, url: record.html_url });
                if (!record.withdrawn_at) throw new Error(`Active applicable GitHub advisory: ${record.ghsa_id}`);
                if (record.cve_id && !nvd.some(entry => entry.id === record.cve_id)) {
                    if (!/^CVE-\d{4}-\d{4,}$/.test(record.cve_id)) throw new Error('Invalid advisory CVE identity.');
                    const result = await get(`https://services.nvd.nist.gov/rest/json/cves/2.0?cveId=${encodeURIComponent(record.cve_id)}`);
                    if (result.totalResults !== 1) throw new Error('NVD CVE verification incomplete.');
                    const cve = result.vulnerabilities[0].cve;
                    if (cve.id !== record.cve_id) throw new Error('NVD returned a different CVE identity.');
                    nvd.push({ id: cve.id, status: cve.vulnStatus,
                        descriptions: cve.descriptions.filter(entry => entry.lang === 'en'),
                        classification: 'Withdrawn NuGet attribution; underlying Emby Server advisory. See SECURITY.md host-version gate.' });
                }
            }
            const hostAdvisory = JSON.parse(run('gh', ['api', 'advisories/GHSA-95fv-5gfj-2r84'], true));
            if (hostAdvisory.cve_id !== 'CVE-2025-64113' || !hostAdvisory.withdrawn_at)
                throw new Error('Emby host/reference-package advisory attribution requires renewed review.');
            advisories.push({ package: 'Emby Server host (not bundled reference package)', ghsa: hostAdvisory.ghsa_id,
                cve: hostAdvisory.cve_id, withdrawn: hostAdvisory.withdrawn_at, summary: hostAdvisory.summary, url: hostAdvisory.html_url });
            if (!nvd.some(entry => entry.id === hostAdvisory.cve_id)) {
                const result = await get('https://services.nvd.nist.gov/rest/json/cves/2.0?cveId=CVE-2025-64113');
                if (result.totalResults !== 1) throw new Error('NVD host CVE verification incomplete.');
                const cve = result.vulnerabilities[0].cve;
                if (cve.id !== hostAdvisory.cve_id) throw new Error('NVD returned a different host CVE identity.');
                nvd.push({ id: cve.id, status: cve.vulnStatus,
                    descriptions: cve.descriptions.filter(entry => entry.lang === 'en'),
                    classification: 'Host vulnerability; patched host required. NuGet reference-package attribution withdrawn.' });
            }
        }
    }
    json('advisory-cross-check.json', { status: online ? 'completed' : 'not-run', advisories, nvd });
    const sourceDll = path.join('MediaBrowser.Plugins.TuneIn', 'bin', 'Release', 'netstandard2.0', pluginFile);
    const dll = fs.readFileSync(sourceDll);
    if (release && !dll.includes(Buffer.from(version + '+' + revision)))
        throw new Error('DLL informational version does not identify the verified source revision.');
    const sha = crypto.createHash('sha256').update(dll).digest('hex');
    const purl = item => `pkg:nuget/${item.name}@${item.resolved}`;
    json('sbom.cdx.json', {
        bomFormat: 'CycloneDX', specVersion: '1.6', serialNumber: `urn:uuid:${crypto.randomUUID()}`, version: 1,
        metadata: {
            timestamp: new Date().toISOString(),
            component: { type: 'application', name: assemblyName, version,
                'bom-ref': 'embytunein', hashes: [{ alg: 'SHA-256', content: sha }] },
            properties: [{ name: 'embytunein:source-revision', value: revision }]
        },
        components: packages.map(item => ({
            type: 'library', name: item.name, version: item.resolved, 'bom-ref': purl(item), purl: purl(item),
            scope: 'excluded',
            hashes: [{ alg: 'SHA-512', content: Buffer.from(item.contentHash, 'base64').toString('hex') }],
            properties: [{ name: 'embytunein:distribution', value: 'Host-provided assembly or build reference; not bundled in plugin DLL.' }]
        })),
        dependencies: [{ ref: 'embytunein', dependsOn: packages.filter(item => item.type === 'Direct').map(purl) },
        ...packages.map(item => ({ ref: purl(item), dependsOn: Object.keys(item.dependencies || {}).map(name => {
            const dependency = packages.find(entry => entry.name.toLowerCase() === name.toLowerCase());
            if (!dependency) throw new Error('Unresolved SBOM dependency.');
            return purl(dependency);
        }) }))]
    });
    fs.copyFileSync(sourceDll, path.join(out, pluginFile));
    fs.copyFileSync('LICENSE.md', path.join(out, 'LICENSE.md'));
    fs.copyFileSync('README.md', path.join(out, 'INSTALL.md'));
    fs.copyFileSync('SECURITY.md', path.join(out, 'SECURITY.md'));
    fs.copyFileSync('CONTRIBUTING.md', path.join(out, 'CONTRIBUTING.md'));
    fs.copyFileSync('CHANGELOG.md', path.join(out, 'CHANGELOG.md'));
    const evidence = {
        version, source_revision: revision, clean_source: !dirty,
        release_gates_passed: release, dotnet_sdk: run(dotnet, ['--version'], true), node: process.version,
        target: 'netstandard2.0', emby_reference_sdk: '4.9.1.90', minimum_stable_emby: '4.9.1.90',
        checksum_sha256: sha, tests: testResult.split(/\r?\n/).filter(line => line.includes('Passed!')),
        javascript_tests: `${jsCount} passed`, live_pinned_tls_api: live ? 'passed' : 'not-run',
        nuget_transitive_audit: 'completed; no active applicable advisories',
        ghsa_nvd_cross_check: online ? 'completed; see advisory-cross-check.json' : 'not-run',
        codeql: 'Separate CI gate; consult the source revision workflow run, not this local report.',
        emby_runtime: 'not-tested; manual installation, dashboard, auth and playback checks required',
        tls_online_revocation: 'not-required; no online revocation-clean claim; platform trust/hostname/expiry validated',
        html_agility_pack: 'Historical vendor source removed; bounded platform XML parsing used; not in DLL.',
        nist_ssdf: 'Practical alignment evidence, not certification; see SECURITY.md'
    };
    if (run('git', ['rev-parse', 'HEAD'], true) !== revision || (release && run('git', ['status', '--porcelain'], true)))
        throw new Error('Source revision changed while verifying.');
    json('release-evidence.json', evidence);
    const assets = [pluginFile, 'LICENSE.md', 'INSTALL.md', 'SECURITY.md', 'CONTRIBUTING.md',
        'CHANGELOG.md', 'dependency-inventory.json', 'nuget-audit.json', 'advisory-cross-check.json',
        'sbom.cdx.json', 'release-evidence.json'].sort();
    fs.writeFileSync(path.join(out, 'SHA256SUMS.txt'), assets.map(file =>
        `${crypto.createHash('sha256').update(fs.readFileSync(path.join(out, file))).digest('hex')}  ${file}`).join('\n') + '\n');
    console.log(`Verified ${revision}; DLL SHA-256 ${sha}`);
}

main().catch(error => { console.error(error.message); process.exitCode = 1; });
