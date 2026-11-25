'use strict';

const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');
const { File } = require('node:buffer');

const configDir = path.join(__dirname, '..', 'MediaBrowser.Plugins.TuneIn', 'Configuration');
const pluginId = '10b82431-f939-4507-a8e2-9509dd3fd6ae';
const settle = () => new Promise(resolve => setImmediate(resolve));

function deferred() {
    let resolve, reject;
    const promise = new Promise((ok, fail) => { resolve = ok; reject = fail; });
    return { promise, resolve, reject };
}

class Element {
    constructor(tag = 'div') {
        this.tagName = tag;
        this.childNodes = [];
        this.attributes = {};
        this.handlers = {};
        this.value = '';
        this.checked = false;
        this.disabled = false;
        this._text = '';
    }
    set textContent(value) { this._text = value; this.childNodes = []; }
    get textContent() { return this._text + this.childNodes.map(child => child.textContent).join(''); }
    appendChild(child) { this.childNodes.push(child); child.parentNode = this; return child; }
    setAttribute(name, value) { this.attributes[name] = value; }
    removeAttribute(name) { delete this.attributes[name]; if (name === 'href') delete this.href; }
    addEventListener(name, handler) { this.handlers[name] = handler; }
    click() { if (!this.disabled && this.handlers.click) return this.handlers.click.call(this); }
    showModal() { this.open = true; }
    focus() { this.focused = true; if (this.ownerDocument) this.ownerDocument.activeElement = this; }
    scrollIntoView(options) { this.scrollOptions = options; this.scrollCount = (this.scrollCount || 0) + 1; }
    close() { this.open = false; if (this.handlers.close) this.handlers.close(); }
    descendants() { return this.childNodes.flatMap(child => [child, ...child.descendants()]); }
}

function harness(manualStatus = false, manualSave = false, manualBackup = false, manualOpen = false, manualDiagnostics = false, manualAcknowledgement = false) {
    const handlers = {};
    const formHandlers = {};
    const username = new Element('input');
    const latLon = new Element('input');
    const requestInterval = new Element('input');
    const form = { addEventListener: (event, handler) => { formHandlers[event] = handler; } };
    const elements = { form, '.txtUsername': username, '.txtLatLon': latLon,
        '.apiRequestIntervalSeconds': requestInterval, '.importDeadlineHours': new Element('input'),
        '.maximumRateLimitWaitMinutes': new Element('input'), '.maximumConsecutiveRateLimitRetries': new Element('input'),
        '.discoveryDeadlineHours': new Element('input'), '.maxStationCount': new Element('input'),
        '.traversalDepthLimit': new Element('input'),
        '.progressiveFirstEntries': new Element('input'), '.progressiveMinimumNewEntries': new Element('input'),
        '.progressiveMinimumGrowthPercent': new Element('input'), '.progressiveMinimumIntervalSeconds': new Element('input'),
        '.progressiveMaximumPasses': new Element('input') };
    for (const name of ['.localSource', '.configurationCompleted', '.hideRegionalGenres', '.allowPartialCatalogImport', '.enableCatalogIdentityDiagnostics', '.allowDirectHlsPlayback', '.enableDiscoveryProgressiveImport', '.progressivePacing', '.categories', '.selectors',
        '.loginFields', '.globalFields', '.advancedFields', '.configurationFields', '.button-submit', '.status', '.refreshStatus', '.refreshError',
        '.catalogRefreshPrompt', '.settingsChangeNotice', '.stationDeletionNotice', '.playbackSecurityWarning', '.channelScheduleDialog', '.channelScheduleText',
        '.discoveryRiskDialog', '.discoveryRiskAccepted', '.discoveryRiskConfirm', '.discoveryRiskCancel', '.discoveryRiskUpgradeNotice', '.discoveryRiskPartialRequirement',
        '.tuneInAdvancedGuideLink', '.tuneInAdvancedGuide', '.tuneInAdvancedGuideClose',
        '.optionalChecksRun', '.optionalChecksStatus', '.optionalChecksMessage',
        '.optionalChecksUnlockHint', '.skipOptionalChecks',
        '.tuneInReadiness', '.readinessFiltering', '.readinessCategory', '.readinessRequirements',
        '.readinessSaved', '.readinessChannel', '.readinessBaseline',
        ...['Filtering', 'Category', 'Requirements', 'Saved', 'Channel', 'Baseline'].map(name => `.readiness${name}Check`),
        '.favoritesValidation', '.localValidation',
        '.channelScheduleConfirm', '.channelScheduleAdjust', '.tuneInPluginVersion',
        '.artworkFile', '.artworkUpload', '.artworkUploadMessage', '.artworkVerifyDialog', '.artworkVerifyText',
        '.artworkVerifyStart', '.artworkVerifyCancel', '.artworkConfirmDialog', '.artworkConfirmText', '.artworkConfirmError',
        '.artworkUnscannedLabel', '.artworkApproveUnscanned', '.artworkInstall', '.artworkDiscard',
        '.artworkRefreshDialog', '.artworkRefreshText', '.artworkRefreshNow', '.artworkRefreshLater',
        ...['Genres', 'Languages', 'Locations', 'Categories'].flatMap(scope =>
            ['Name', 'Images', 'Path', 'Changes'].map(field => `.artwork${scope}${field}`)),
        '.refreshElapsed', '.refreshWork', '.refreshLastProgress', '.refreshDiagnosticsText',
        '.refreshImportContents', '.refreshPendingFolders', '.refreshPendingFolderText',
        '.refreshStart', '.refreshCancel', '.refreshLogButton', '.refreshLogMessage', '.refreshFailureLog',
        ...['Latest', 'Public', 'Capture', 'Packs', 'PackHint', 'PreviewDetails', 'Preview', 'Download',
            'Delete', 'DeleteAll', 'Repository', 'DraftDetails', 'DraftTitle', 'DraftBody', 'Reviewed', 'Open',
            'Message', 'DraftLink', 'DeleteDialog', 'DeleteTitle', 'DeleteCancel', 'DeleteConfirm'].map(name => `.diagnostics${name}`),
        '.tuneInRefreshCategories', '.refreshCategoryNote', '.settingsLocked',
        '.settingsLockState', '.settingsLockStatus', '.refreshQueueNote', '.tuneInRefreshRecoveryDialog', '.refreshRecoveryText',
        '.refreshResumeFirst', '.refreshNewFirst', '.cleanupStatus', '.cleanupError', '.cleanupPreview',
        '.cleanupConfirm', '.cleanupCancel', '.cleanupBack', '.cleanupConfirmation', '.cleanupPreviewText', '.cleanupScope', '.backupStart',
        '.backupStatus', '.cleanupBackupVerified', '.cleanupStationReference', '.cleanupStationFields', '.cleanupStationSearch', '.cleanupStationSearchStatus', '.cleanupStationResults', '#tuneInReportHint']) elements[name] = new Element();
    const html = fs.readFileSync(path.join(configDir, 'configPage.html'), 'utf8');
    elements['.playbackSecurityWarning'].hidden = true;
    const notice = html.match(/<p class="status settingsChangeNotice"[^>]*>([^<]+)<\/p>/);
    if (notice) elements['.settingsChangeNotice'].textContent = notice[1];
    elements['.settingsMessage'] = elements['.status'];
    for (const [section, summary, open] of [
        ['.globalSection', '.globalSummary', true],
        ['.categoriesSection', '.categoriesSummary', true],
        ['.browsingSection', '.browsingSummary', true],
        ['.stationSelectionsSection', '.stationSelectionsSummary', true],
        ['.maintenanceSection', '.maintenanceSummary', false],
        ['.tuneInRefresh', '.refreshSummary', false]
    ]) {
        elements[section] = new Element('details');
        elements[section].open = open;
        elements[summary] = elements[section].appendChild(new Element('summary'));
    }
    elements['.tuneInSettingsSave'] = elements['.button-submit'];
    for (const [summary, text, indicator] of [
        ['.refreshSummary', 'Catalog Refresh', '.refreshIndicator'],
        ['.maintenanceSummary', 'Maintenance', '.maintenanceIndicator']
    ]) {
        const title = elements[summary].appendChild(new Element('span'));
        title.textContent = text + ' ';
        elements[indicator] = elements[summary].appendChild(new Element('span'));
        elements[indicator].hidden = true;
    }
    elements['.refreshIndicator'].textContent = 'Refreshing';
    elements['.maintenanceIndicator'].textContent = 'Maintenance is active';
    elements['.refreshSummaryState'] = elements['.refreshSummary'].appendChild(new Element('span'));
    elements['.refreshSummaryDetails'] = elements['.refreshSummary'].appendChild(new Element('span'));
    elements['.refreshOverall'] = elements['.refreshSummary'].appendChild(new Element('span'));
    const enhanced = [];
    const help = new Element('details');
    const helpSummary = help.appendChild(new Element('summary'));
    const helpPopup = help.appendChild(new Element());
    help.querySelector = selector => selector === 'summary' ? helpSummary : helpPopup;
    help.getBoundingClientRect = () => ({ left: 100, right: 144, top: 100, bottom: 144, width: 44, height: 44 });
    helpPopup.getBoundingClientRect = () => ({ width: 300, height: 100 });
    help.style = { setProperty(name, value) { this[name] = value; } };
    const viewport = { clientWidth: 800, clientHeight: 600 };
    const resizeObservers = [];
    const windowHandlers = new Map();
    const header = elements['.tuneInPinned'] = new Element();
    header.height = 200;
    header.getBoundingClientRect = () => ({ height: header.height });
    const view = {
        style: { setProperty(name, value) { this[name] = value; } },
        addEventListener(event, handler) { handlers[event] = handler; },
        querySelectorAll(selector) {
            if (selector === '.globalFields') return [elements['.globalFields'], elements['.advancedFields']];
            return selector === '.tuneInHelp, .tuneInArtworkHelp' ? [help] : [];
        },
        querySelector(selector) {
            const category = /^\[data-category="(.+)"\]$/.exec(selector);
            if (selector === '.status' && notice) return elements['.settingsChangeNotice'];
            if (selector === '.tuneInRecoveryDialog') {
                return elements['.selectors'].descendants().find(item =>
                    (item.className || '').split(/\s+/).includes('tuneInRecoveryDialog'))
                    || elements['.tuneInRefreshRecoveryDialog'];
            }
            return category ? elements['.categories'].descendants().find(item => item.attributes['data-category'] === category[1])
                : elements[selector];
        }
    };
    const reads = [];
    const writes = [];
    const notices = [];
    const errors = [];
    const selectors = [];
    const refresh = [];
    const locks = [];
    const opens = [];
    const cleanup = [];
    const backup = [];
    const artwork = [];
    const optionalChecks = [];
    const diagnostics = [];
    const downloads = [];
    const popups = [];
    const popupControl = { blocked: false, throws: false };
    const scheduledTasks = { tasks: [], requests: [] };
    const timers = new Map();
    let timerId = 0;
    let serverLocked = false;
    let initialStatus;
    const loader = { shown: 0, hidden: 0, show() { this.shown++; }, hide() { this.hidden++; } };
    const ApiClient = {
        getPluginConfiguration(id) {
            assert.equal(id, pluginId);
            const request = deferred();
            reads.push(request);
            return request.promise;
        },
        updatePluginConfiguration(id, config) {
            if (manualSave) {
                const request = deferred();
                writes.push({ id, config, ...request });
                return request.promise.then(result => { serverLocked = true; return result; });
            }
            writes.push({ id, config });
            serverLocked = true;
            return Promise.resolve('saved');
        },
        getUrl(route, params) { return { route, params }; },
        setAuthorizationInfoIntoRequest(request) { request.headers.Authorization = 'test-session'; },
        ajax(options) {
            const request = deferred();
            if (options.url.route === 'TuneIn/SettingsLock/OnOpen') {
                opens.push({ options, ...request });
                if (!manualOpen) request.resolve({ Locked: initialStatus && typeof initialStatus.SettingsLocked === 'boolean'
                    ? initialStatus.SettingsLocked : serverLocked });
            }
            else if (options.url.route === 'TuneIn/SettingsLock') {
                locks.push({ options, ...request });
                if (!manualStatus) {
                    serverLocked = JSON.parse(options.data).Locked;
                    request.resolve({ Locked: serverLocked });
                }
            }
            else if (options.url.route.startsWith('TuneIn/Refresh/')) {
                if (options.type === 'GET') {
                    const resolve = request.resolve;
                    request.resolve = value => { initialStatus = value; resolve(value); };
                }
                refresh.push({ options, ...request });
                if (!manualStatus && options.type === 'GET') request.resolve({ SettingsLocked: serverLocked });
            }                         else if (options.url.route.startsWith('TuneIn/Diagnostics')) {
                diagnostics.push({ options, ...request });
                if (!manualDiagnostics && options.url.route === 'TuneIn/Diagnostics')
                    request.resolve({ CaptureAvailable: false, LatestRefresh: null, Packs: [], Repository: null });
            }
            else if (options.url.route === 'TuneIn/DiscoveryImport/Acknowledge') {
                selectors.push({ options, ...request });
                if (!manualAcknowledgement) request.resolve({ Token: 'test-risk-token' });
            }
            else if (options.url.route === 'TuneIn/OptionalChecks/Preference') optionalChecks.push({ options, ...request });
            else if (options.url.route.startsWith('TuneIn/Artwork/')) artwork.push({ options, ...request });
            else if (options.url.route.startsWith('TuneIn/Cleanup/')) cleanup.push({ options, ...request });
            else if (options.url.route.startsWith('TuneIn/Backup/')) {
                backup.push({ options, ...request });
                if (!manualBackup && options.type === 'GET') request.resolve({ State: 'Ready' });
            }
            else if (options.url.route === 'ScheduledTasks') {
                scheduledTasks.requests.push(options);
                request.resolve(scheduledTasks.tasks);
            }
            else selectors.push({ options, ...request });
            return request.promise;
        }
    };
    const Dashboard = {
        alert(message) { notices.push(message); },
        processPluginConfigurationUpdateResult(result) { notices.push(result); }
    };
    let PluginView;
    const calls = [];
    function BaseView(element) { this.view = element; calls.push('construct'); }
    BaseView.prototype.onResume = function (options) { calls.push(['resume', options]); };
    BaseView.prototype.onPause = function (options) { calls.push(['pause', options]); };
    BaseView.prototype.onBeginResume = function () { return 'native transition'; };
    const document = { createElement(tag) {
        const element = new Element(tag);
        element.ownerDocument = document;
        if (tag === 'a') element.click = () => downloads.push({ href: element.href, filename: element.download });
        return element;
    }, documentElement: viewport, activeElement: null };
    Object.values(elements).forEach(element => { element.ownerDocument = document; });
    vm.runInNewContext(fs.readFileSync(path.join(configDir, 'configPage.js'), 'utf8'), {
        define(dependencies, factory) {
            assert.equal(dependencies[0], 'baseView');
            PluginView = factory(BaseView, loader, element => ({ trigger(event) { enhanced.push({ element, event }); } }));
        },
        ApiClient, Dashboard, console: { error(...args) { errors.push(args); } },
        fetch(url, options) {
            const request = deferred();
            artwork.push({ options: { url, type: options.method, ...options }, ...request });
            return request.promise.then(result => result && result.__httpStatus
                ? { ok: false, status: result.__httpStatus, text: () => Promise.resolve(result.body) }
                : { ok: true, status: 200, json: () => Promise.resolve(result) });
        },
        document,
        ResizeObserver: class {
            constructor(callback) { this.callback = callback; resizeObservers.push(this); }
            observe(element) { this.element = element; }
            disconnect() { this.disconnected = true; }
        },
        Blob,
        URL: Object.assign(URL, {
            createObjectURL(blob) { downloads.push({ blob }); return 'blob:diagnostics'; },
            revokeObjectURL(url) { downloads.push({ revoked: url }); }
        }),
        window: {
            addEventListener(event, handler) { windowHandlers.set(event, handler); },
            removeEventListener(event, handler) {
                if (windowHandlers.get(event) === handler) windowHandlers.delete(event);
            },
            open(url, target, features) {
            if (popupControl.throws) throw new Error('Opening a tab failed');
            if (popupControl.blocked) return null;
            const popup = { url, target, features, closed: false, close() { this.closed = true; } };
            popups.push(popup);
            if (popupControl.onOpen) popupControl.onOpen();
            return popup;
        } },
        setTimeout(handler, delay) { timers.set(++timerId, { handler, delay }); return timerId; },
        clearTimeout(id) { timers.delete(id); }
    });

    return { instance: new PluginView(view), username, latLon, requestInterval, formHandlers, reads, writes, notices, errors, loader,
        elements, selectors, enhanced, calls, handlers, help, helpSummary, refresh, locks, opens, cleanup, backup, artwork, optionalChecks, diagnostics, downloads, popups, popupControl, timers, scheduledTasks,
        viewport, resizeObservers, windowHandlers, header };
}

function open(details) {
    details.open = true;
    return details.handlers.toggle();
}

function enableBrowsing(app) {
    const control = app.elements['.configurationCompleted'];
    control.checked = true;
    control.handlers.change();
}

function selectOnly(app, index) {
    const mode = app.elements['.selectors'].childNodes[index].childNodes[1];
    mode.value = 'selected';
    return mode.handlers.change();
}

function diagnosticPack(id = 'one', repository = 'owner/repo') {
    const DraftTitle = 'Title <script>not html</script>';
    const DraftBody = 'Body & details';
    const DraftUrl = `https://github.com/${repository}/issues/new?${new URLSearchParams({ title: DraftTitle, body: DraftBody })}`;
    return { Id: id, Revision: 'rev-' + id, CapturedUtc: '2026-11-24', Category: 'location', State: 'Partial',
        Bytes: 19, PublicNavigationIncluded: false, PreviewText: '{"unsafe":"<img src=x>"}\n',
        FileName: 'diagnostics.json', Repository: repository, DraftTitle, DraftBody, DraftUrl, ReportingError: null };
}

function diagnosticList(packs = [], repository = 'owner/repo') {
    return { CaptureAvailable: true, LatestRefresh: { RunId: 'run-1', State: 'Running', Category: 'location', Busy: true },
        Packs: packs, Repository: repository, ReportingError: null, StorageError: null, HasStoredPacks: !!packs.length };
}

async function diagnosticsVisit(packs = [], repository = 'owner/repo') {
    const app = harness(true, false, false, false, true);
    const visit = app.instance.onResume();
    app.reads[0].resolve({ SchemaVersion: 1, ConfigurationCompleted: true, EnableTalk: true, HiddenRepositoryOverride: 'preserve-me' });
    app.refresh[0].resolve({ SettingsLocked: true, State: 'Running', Busy: true });
    app.diagnostics[0].resolve(diagnosticList(packs, repository));
    await visit;
    await settle();
    return app;
}

async function diagnosticSelect(app, pack) {
    const select = app.elements['.diagnosticsPacks'];
    select.value = pack.Id;
    const request = select.handlers.change();
    app.diagnostics.at(-1).resolve(pack);
    await request;
}

test('diagnostics load only lists packs, capture is opt-in and ignores settings lock', async () => {
    const app = await diagnosticsVisit();
    assert.deepEqual(app.diagnostics.map(item => item.options.url.route), ['TuneIn/Diagnostics']);
    assert.equal(app.elements['.diagnosticsPublic'].checked, false);
    app.instance.settingsLocked = true;
    const capture = app.elements['.diagnosticsCapture'].click();
    assert.deepEqual(JSON.parse(app.diagnostics.at(-1).options.data), { RunId: 'run-1', IncludePublicNavigation: false });
    const pack = diagnosticPack();
    app.diagnostics.at(-1).resolve(pack);
    await capture;
    app.diagnostics.at(-1).resolve(diagnosticList([pack]));
    await settle();
    app.elements['.diagnosticsPublic'].checked = true;
    const second = app.elements['.diagnosticsCapture'].click();
    assert.equal(JSON.parse(app.diagnostics.at(-1).options.data).IncludePublicNavigation, true);
    app.diagnostics.at(-1).resolve(pack);
    await second;
    app.diagnostics.at(-1).resolve(diagnosticList([pack]));
});

test('diagnostic preview and download preserve exact redacted text without HTML interpretation', async () => {
    const pack = diagnosticPack();
    const app = await diagnosticsVisit([pack]);
    await diagnosticSelect(app, pack);
    assert.equal(app.elements['.diagnosticsPreview'].textContent, pack.PreviewText);
    assert.equal(app.elements['.diagnosticsPreview'].childNodes.length, 0);
    assert.equal(app.elements['.diagnosticsDraftTitle'].textContent, pack.DraftTitle);
    app.elements['.diagnosticsDownload'].click();
    assert.equal(await app.downloads[0].blob.text(), pack.PreviewText);
    assert.equal(app.downloads[1].filename, pack.FileName);
    assert.equal(app.downloads[2].revoked, 'blob:diagnostics');
    assert.equal(app.diagnostics.length, 2);
});

test('diagnostic review resets on selection, revision and repository changes', async () => {
    const pack = diagnosticPack(), other = diagnosticPack('two');
    const app = await diagnosticsVisit([pack, other]);
    await diagnosticSelect(app, pack);
    const reviewed = app.elements['.diagnosticsReviewed'];
    reviewed.checked = true; reviewed.handlers.change();
    assert.equal(app.elements['.diagnosticsOpen'].disabled, false);
    await diagnosticSelect(app, other);
    assert.equal(reviewed.checked, false);
    reviewed.checked = true; reviewed.handlers.change();
    app.timers.get(app.instance.refreshTimer).handler();
    app.refresh.at(-1).resolve({ SettingsLocked: true, State: 'Completed', Busy: false });
    await settle();
    assert.equal(app.diagnostics.at(-1).options.url.route, 'TuneIn/Diagnostics');
    app.diagnostics.at(-1).resolve(diagnosticList([{ ...other, Revision: 'new-revision' }]));
    await settle();
    assert.equal(reviewed.checked, false);
    assert.equal(app.instance.diagnostics.preview, null);
    const updated = { ...other, Revision: 'new-revision' };
    await diagnosticSelect(app, updated);
    reviewed.checked = true; reviewed.handlers.change();
    app.timers.get(app.instance.refreshTimer).handler();
    app.refresh.at(-1).resolve({ SettingsLocked: true, State: 'Running', Busy: true });
    await settle();
    app.diagnostics.at(-1).resolve(diagnosticList([updated], 'other/repo'));
    await settle();
    assert.equal(reviewed.checked, false);
    assert.equal(app.instance.diagnostics.preview, null);
    assert.equal(app.elements['.diagnosticsRepository'].textContent, 'other/repo');
});

test('refresh status changes update capture availability without replacing a selected preview', async () => {
    const pack = diagnosticPack();
    const app = await diagnosticsVisit([pack]);
    await diagnosticSelect(app, pack);
    const reviewed = app.elements['.diagnosticsReviewed'];
    reviewed.checked = true; reviewed.handlers.change();
    app.timers.get(app.instance.refreshTimer).handler();
    app.refresh.at(-1).resolve({ SettingsLocked: true, State: 'Completed', Busy: false });
    await settle();
    const next = diagnosticList([pack]);
    next.LatestRefresh = { RunId: 'run-2', Category: 'talk', State: 'Completed', Busy: false };
    app.diagnostics.at(-1).resolve(next);
    await settle();
    assert.equal(app.instance.diagnostics.preview.Id, pack.Id);
    assert.equal(reviewed.checked, true);
    assert.match(app.elements['.diagnosticsLatest'].textContent, /talk/);
    const capture = app.elements['.diagnosticsCapture'].click();
    assert.equal(JSON.parse(app.diagnostics.at(-1).options.data).RunId, 'run-2');
    app.diagnostics.at(-1).reject(new Error('race'));
    await capture;
    app.diagnostics.at(-1).resolve(next);
});

test('stale diagnostics list and pack responses cannot change a later visit', async () => {
    const pack = diagnosticPack();
    const app = await diagnosticsVisit([pack]);
    app.elements['.diagnosticsPacks'].value = pack.Id;
    const pending = app.elements['.diagnosticsPacks'].handlers.change();
    app.instance.onPause();
    const visit = app.instance.onResume();
    app.reads.at(-1).resolve({ SchemaVersion: 1, ConfigurationCompleted: false });
    app.diagnostics.at(-1).resolve(diagnosticList([]));
    await visit;
    app.diagnostics.find(item => item.options.url.route.endsWith('/Pack')).resolve(pack);
    await pending;
    assert.equal(app.elements['.diagnosticsPreview'].textContent, '');
    assert.equal(app.instance.diagnostics.preview, null);
});

test('a late list from a previous visit cannot overwrite the current list', async () => {
    const app = harness(false, false, false, false, true);
    const first = app.instance.onResume();
    app.reads[0].resolve({ SchemaVersion: 1 });
    await first;
    app.instance.onPause();
    const second = app.instance.onResume();
    app.reads[1].resolve({ SchemaVersion: 1 });
    app.diagnostics[1].resolve(diagnosticList([], 'new/repo'));
    await second; await settle();
    app.diagnostics[0].resolve(diagnosticList([], 'old/repo'));
    await settle();
    assert.equal(app.elements['.diagnosticsRepository'].textContent, 'new/repo');
});

test('draft requires review, retains a native link when popups are blocked and rejects destination mismatch', async () => {
    const pack = diagnosticPack();
    const app = await diagnosticsVisit([pack]);
    await diagnosticSelect(app, pack);
    app.elements['.diagnosticsOpen'].click();
    assert.equal(app.popups.length, 0);
    const reviewed = app.elements['.diagnosticsReviewed'];
    reviewed.checked = true; reviewed.handlers.change();
    app.popupControl.blocked = true;
    const blocked = app.elements['.diagnosticsOpen'].click();
    assert.equal(app.popups.length, 0);
    app.diagnostics.at(-1).resolve({ Id: pack.Id, Revision: pack.Revision, Repository: pack.Repository, Url: pack.DraftUrl });
    await blocked;
    assert.equal(app.popups.length, 0);
    assert.equal(app.elements['.diagnosticsDraftLink'].hidden, false);
    assert.equal(app.elements['.diagnosticsDraftLink'].href, pack.DraftUrl);
    assert.match(app.elements['.diagnosticsMessage'].textContent, /If no tab opens/);
    app.popupControl.blocked = false;
    const draft = app.elements['.diagnosticsOpen'].click();
    assert.deepEqual(JSON.parse(app.diagnostics.at(-1).options.data), { Id: pack.Id, Revision: pack.Revision, Repository: pack.Repository });
    app.diagnostics.at(-1).resolve({ Id: pack.Id, Revision: pack.Revision, Repository: 'wrong/repo', Url: pack.DraftUrl });
    await draft;
    assert.equal(app.popups.length, 0);
    assert.equal(reviewed.checked, false);
    assert.equal(app.elements['.diagnosticsDraftLink'].hidden, true);
    assert.equal(app.elements['.diagnosticsDraftLink'].href, undefined);
    assert.equal(app.elements['.diagnosticsMessage'].attributes['data-error'], 'true');
    app.diagnostics.at(-1).resolve(diagnosticList([pack]));
});

test('draft validates before opening GitHub with no opener/referrer and does not bubble into host actions', async () => {
    const pack = diagnosticPack();
    const app = await diagnosticsVisit([pack]);
    await diagnosticSelect(app, pack);
    const reviewed = app.elements['.diagnosticsReviewed'];
    reviewed.checked = true; reviewed.handlers.change();
    let prevented = false, stopped = false;
    const draft = app.elements['.diagnosticsOpen'].handlers.click({
        preventDefault() { prevented = true; }, stopPropagation() { stopped = true; }
    });
    assert.equal(prevented, true);
    assert.equal(stopped, true);
    assert.equal(app.popups.length, 0);
    app.diagnostics.at(-1).resolve({ Id: pack.Id, Revision: pack.Revision, Repository: pack.Repository, Url: pack.DraftUrl });
    await draft;
    assert.equal(app.popups[0].url, pack.DraftUrl);
    assert.equal(app.popups[0].target, '_blank');
    assert.equal(app.popups[0].features, 'noopener,noreferrer');
    assert.match(app.elements['.diagnosticsMessage'].textContent, /no issue has been created or pack uploaded/);
    assert.equal(app.writes.length, 0);
    const html = fs.readFileSync(path.join(configDir, 'configPage.html'), 'utf8');
    assert.match(html, /<a class="diagnosticsDraftLink" target="_blank" rel="noopener noreferrer" hidden>/);
    reviewed.checked = false; reviewed.handlers.change();
    assert.equal(app.elements['.diagnosticsDraftLink'].hidden, true);
    assert.equal(app.elements['.diagnosticsDraftLink'].href, undefined);
    reviewed.checked = true; reviewed.handlers.change();
    const stale = app.elements['.diagnosticsOpen'].click();
    app.instance.onPause();
    assert.equal(app.popups[0].closed, false);
    app.diagnostics.at(-1).resolve({ Id: pack.Id, Revision: pack.Revision, Repository: pack.Repository, Url: pack.DraftUrl });
    await stale;
    assert.equal(app.popups.length, 1);
    assert.equal(app.elements['.diagnosticsDraftLink'].hidden, true);
});

test('opening the validated GitHub draft survives the Emby pause caused by switching tabs', async () => {
    const pack = diagnosticPack();
    const app = await diagnosticsVisit([pack]);
    await diagnosticSelect(app, pack);
    const reviewed = app.elements['.diagnosticsReviewed'];
    reviewed.checked = true; reviewed.handlers.change();
    app.popupControl.onOpen = () => app.instance.onPause();
    const draft = app.elements['.diagnosticsOpen'].click();
    assert.equal(app.popups.length, 0);
    app.diagnostics.at(-1).resolve({ Id: pack.Id, Revision: pack.Revision, Repository: pack.Repository, Url: pack.DraftUrl });
    await draft;
    assert.equal(app.popups[0].url, pack.DraftUrl);
    assert.equal(app.popups[0].closed, false);
});

test('draft retains the reviewed native link when browser tab opening throws', async () => {
    const pack = diagnosticPack();
    const app = await diagnosticsVisit([pack]);
    await diagnosticSelect(app, pack);
    const reviewed = app.elements['.diagnosticsReviewed'];
    reviewed.checked = true; reviewed.handlers.change();
    app.popupControl.throws = true;
    const draft = app.elements['.diagnosticsOpen'].click();
    app.diagnostics.at(-1).resolve({ Id: pack.Id, Revision: pack.Revision, Repository: pack.Repository, Url: pack.DraftUrl });
    await draft;
    assert.equal(app.popups.length, 0);
    assert.equal(app.elements['.diagnosticsDraftLink'].hidden, false);
    assert.equal(app.elements['.diagnosticsDraftLink'].href, pack.DraftUrl);
    assert.equal(app.elements['.diagnosticsMessage'].attributes['data-error'], 'true');
    assert.match(app.elements['.diagnosticsMessage'].textContent, /browser could not open a new tab/);
});

test('invalid diagnostic repository disables reporting but retains download', async () => {
    const pack = diagnosticPack('one', 'invalid');
    pack.ReportingError = 'private server detail';
    const app = await diagnosticsVisit([pack], 'invalid');
    await diagnosticSelect(app, pack);
    assert.equal(app.elements['.diagnosticsReviewed'].disabled, true);
    assert.equal(app.elements['.diagnosticsOpen'].disabled, true);
    assert.equal(app.elements['.diagnosticsDownload'].disabled, false);
    assert.equal(app.elements['.diagnosticsMessage'].attributes['data-error'], 'true');
    assert.doesNotMatch(app.elements['.diagnosticsMessage'].textContent, /private server detail/);
});

test('capture success preserves a highlighted reporting warning for an invalid repository', async () => {
    const app = await diagnosticsVisit([], 'invalid');
    app.instance.diagnostics.reportingError = 'Reporting repository is invalid.';
    const capture = app.elements['.diagnosticsCapture'].click();
    const pack = diagnosticPack('one', 'invalid');
    pack.ReportingError = 'Reporting repository is invalid.';
    app.diagnostics.at(-1).resolve(pack);
    await capture;
    assert.match(app.elements['.diagnosticsMessage'].textContent, /saved locally.*Reporting is unavailable/);
    assert.equal(app.elements['.diagnosticsMessage'].attributes['data-error'], 'true');
    assert.equal(app.elements['.diagnosticsDownload'].disabled, false);
    const list = diagnosticList([pack], 'invalid');
    list.ReportingError = pack.ReportingError;
    app.diagnostics.at(-1).resolve(list);
    await settle();
    assert.equal(app.elements['.diagnosticsMessage'].attributes['data-error'], 'true');
});

test('unreadable diagnostic storage permits confirmed delete all and then restores capture', async () => {
    const app = harness(false, false, false, false, true);
    const visit = app.instance.onResume();
    app.reads[0].resolve({ SchemaVersion: 1 });
    const unreadable = { ...diagnosticList(), HasStoredPacks: true,
        StorageError: 'Saved diagnostic packs could not be read. Delete all packs to recover.' };
    app.diagnostics[0].resolve(unreadable);
    await visit; await settle();
    assert.equal(app.elements['.diagnosticsMessage'].textContent, unreadable.StorageError);
    assert.equal(app.elements['.diagnosticsMessage'].attributes['data-error'], 'true');
    assert.equal(app.elements['.diagnosticsCapture'].disabled, true);
    assert.equal(app.elements['.diagnosticsPacks'].disabled, true);
    assert.equal(app.elements['.diagnosticsDeleteAll'].disabled, false);
    assert.equal(app.instance.diagnostics.latest.RunId, 'run-1');
    app.elements['.diagnosticsDeleteAll'].click();
    assert.equal(app.elements['.diagnosticsDeleteDialog'].open, true);
    assert.equal(app.diagnostics.length, 1);
    const deletion = app.elements['.diagnosticsDeleteConfirm'].click();
    assert.deepEqual(JSON.parse(app.diagnostics.at(-1).options.data), { All: true });
    app.diagnostics.at(-1).resolve(diagnosticList());
    await deletion;
    assert.equal(app.instance.diagnostics.storageError, null);
    assert.equal(app.instance.diagnostics.hasStoredPacks, false);
    assert.equal(app.elements['.diagnosticsMessage'].attributes['data-error'], 'false');
    assert.equal(app.elements['.diagnosticsCapture'].disabled, false);
    assert.equal(app.elements['.diagnosticsDeleteAll'].disabled, true);
});

test('unavailable diagnostics endpoint is isolated and highlighted', async () => {
    const app = harness(false, false, false, false, true);
    const visit = app.instance.onResume();
    app.reads[0].resolve({ SchemaVersion: 1, ConfigurationCompleted: false });
    app.diagnostics[0].reject(new Error('private response'));
    await visit; await settle();
    assert.equal(app.elements['.diagnosticsMessage'].attributes['data-error'], 'true');
    assert.match(app.elements['.diagnosticsMessage'].textContent, /diagnostics are unavailable/i);
    assert.doesNotMatch(app.elements['.diagnosticsMessage'].textContent, /private response/);
    assert.ok(app.instance.config);
});

test('diagnostic deletion requires confirmation, cancel preserves data, failures are highlighted', async () => {
    const pack = diagnosticPack();
    const app = await diagnosticsVisit([pack]);
    await diagnosticSelect(app, pack);
    app.elements['.diagnosticsDelete'].click();
    assert.equal(app.elements['.diagnosticsDeleteDialog'].open, true);
    assert.equal(app.elements['.diagnosticsDeleteCancel'].focused, true);
    app.elements['.diagnosticsDeleteCancel'].click();
    assert.equal(app.diagnostics.length, 2);
    app.elements['.diagnosticsDeleteAll'].click();
    const deletion = app.elements['.diagnosticsDeleteConfirm'].click();
    assert.deepEqual(JSON.parse(app.diagnostics.at(-1).options.data), { All: true });
    app.diagnostics.at(-1).reject(new Error('private response'));
    await deletion;
    assert.equal(app.elements['.diagnosticsMessage'].attributes['data-error'], 'true');
    assert.doesNotMatch(app.elements['.diagnosticsMessage'].textContent, /private response/);
    assert.equal(app.instance.diagnostics.preview.Id, pack.Id);
    app.elements['.diagnosticsDelete'].click();
    const selected = app.elements['.diagnosticsDeleteConfirm'].click();
    assert.deepEqual(JSON.parse(app.diagnostics.at(-1).options.data), { Id: pack.Id, Revision: pack.Revision });
    app.diagnostics.at(-1).resolve(diagnosticList([]));
    await selected;
    assert.equal(app.instance.diagnostics.preview, null);
    assert.equal(app.elements['.diagnosticsMessage'].attributes['data-error'], 'false');
});

test('all dashboard actions use native Emby buttons without plugin color overrides', () => {
    const html = fs.readFileSync(path.join(configDir, 'configPage.html'), 'utf8');
    const buttons = [...html.matchAll(/<button\b([^>]*)>/g)].map(match => match[1]);
    assert.ok(buttons.length > 0);
    for (const attributes of buttons) {
        assert.match(attributes, /\bis="emby-button"/);
        assert.match(attributes, /\bclass="[^"]*\braised\b/);
        assert.match(attributes, /\bbutton-submit\b/);
        assert.doesNotMatch(attributes, /\bstyle=/);
    }
    const saveButtons = buttons.filter(attributes => /\btuneInSettingsSave\b/.test(attributes));
    assert.equal(saveButtons.length, 1);
    assert.match(saveButtons[0], /\btype="submit"/);
    const styles = html.match(/<style>([\s\S]*?)<\/style>/)[1];
    assert.doesNotMatch(styles, /[^{}]*(?:button|refreshCancel|refreshStart|refreshLogButton|refreshDismiss)[^{}]*\{/);
});

test('Catalog Refresh uses the native submit theme for its actions and inherits text color', () => {
    const html = fs.readFileSync(path.join(configDir, 'configPage.html'), 'utf8');
    for (const action of ['refreshStart', 'refreshCancel', 'refreshLogButton',
        'refreshResumeFirst', 'refreshNewFirst', 'refreshDismiss']) {
        const tag = html.match(new RegExp('<button\\b[^>]*\\b' + action + '\\b[^>]*>'))[0];
        assert.match(tag, /\bbutton-submit\b/);
    }
    assert.match(html, /\.tuneInRefresh\s*\{\s*color:\s*inherit;\s*\}/);
});

test('main settings, refresh, and maintenance share one width container', () => {
    const html = fs.readFileSync(path.join(configDir, 'configPage.html'), 'utf8');
    assert.match(html, /<div class="tuneInWorkspace">\s*<section class="tuneInReadiness"[\s\S]*?<\/section>\s*<div class="auto-center tuneInPanelLayout">\s*<div class="tuneInPinned"[^>]*>[\s\S]*?<div class="tuneInHero" hidden>[\s\S]*?<\/div>\s*<form>[\s\S]*?<\/form>\s*<details class="tuneInSection tuneInNeutralPanel tuneInRefresh"/);
    assert.match(html, /<\/details>\s*<details class="tuneInSection tuneInNeutralPanel">\s*<summary class="maintenanceSummary">/);
    assert.match(html, /\.tuneInWorkspace\s*\{[^}]*max-width:\s*80ch/);
    assert.match(html, /\[data-controller="__plugin\/tuneinjs"\]\s*>\s*\.scrollSlider\s*\{[^}]*position:\s*relative/);
    assert.match(html, /\.tuneInActivity\s*\{[^}]*animation:\s*tuneInActivityPulse/);
    assert.match(html, /\.tuneInActivity\s*\{[^}]*background-color:\s*#fff0c2/);
    assert.match(html, /@keyframes tuneInActivityPulse\s*\{\s*50%\s*\{\s*background-color:\s*#ffcb71/);
});

test('all control sections stay in the main column and settings stay in their form', () => {
    const html = fs.readFileSync(path.join(configDir, 'configPage.html'), 'utf8');
    const stack = [];
    const found = new Set();
    for (const match of html.matchAll(/<\/?(?:div|form|details|summary|button)\b[^>]*>/gi)) {
        const tag = match[0];
        const name = /^<\/?(\w+)/.exec(tag)[1].toLowerCase();
        if (tag.startsWith('</')) {
            if (name === 'div' || name === 'form') {
                const index = stack.findLastIndex(item => item.name === name);
                assert.notEqual(index, -1, `unexpected ${tag}`);
                stack.length = index;
            }
            continue;
        }
        for (const selector of ['browsingSummary', 'tuneInSettingsSave', 'tuneInRefresh', 'maintenanceSummary']) {
            if (!new RegExp(`\\b${selector}\\b`).test(tag)) continue;
            assert.ok(stack.some(item => item.main), `${selector} is outside the main panel`);
            if (selector === 'browsingSummary' || selector === 'tuneInSettingsSave')
                assert.ok(stack.some(item => item.name === 'form'), `${selector} is outside the settings form`);
            found.add(selector);
        }
        if (name === 'div' || name === 'form')
            stack.push({ name, main: /\btuneInPanelLayout\b/.test(tag) });
    }
    assert.deepEqual([...found].sort(), ['browsingSummary', 'maintenanceSummary', 'tuneInRefresh', 'tuneInSettingsSave'].sort());
    assert.equal(stack.length, 0);
    assert.match(html, /@media \(prefers-reduced-motion: reduce\)\s*\{\s*\.tuneInActivity\s*\{\s*animation:\s*none/);
});

test('expanded station selectors use two responsive columns without a category outline', () => {
    const html = fs.readFileSync(path.join(configDir, 'configPage.html'), 'utf8');
    const styles = html.match(/<style>([\s\S]*?)<\/style>/)[1];
    assert.match(styles, /\.tuneInSelectorCategory\[open\]\s*\{\s*border:\s*0;\s*padding:\s*0;/);
    assert.match(styles, /\.tuneInSelectorColumns\s*\{\s*display:\s*grid;\s*grid-template-columns:\s*repeat\(2,\s*minmax\(0,\s*1fr\)\)/);
    assert.match(styles, /@container \(max-width: 42em\)\s*\{\s*\.tuneInSelectorColumns\s*\{\s*grid-template-columns:\s*minmax\(0,\s*1fr\)/);
});

test('first activation opens and loads Language and Location even when native click defaults are cancelled', async () => {
    const app = harness();
    const visit = app.instance.onResume();
    app.reads[0].resolve({});
    await visit;
    enableBrowsing(app);
    for (const index of [0, 1]) {
        const section = app.elements['.selectors'].childNodes[index];
        selectOnly(app, index);
        const root = section.descendants().find(node => node.tagName === 'details');
        const summary = root.childNodes[0];
        let prevented = false;
        const loading = summary.handlers.click({ preventDefault() { prevented = true; } });
        assert.equal(prevented, true);
        assert.equal(root.open, true);
        const request = app.selectors.at(-1);
        assert.equal(request.options.url.params.Category, index === 0 ? 'language' : 'location');
        request.resolve({ Items: [{ Id: index === 0 ? 'l122' : 'r101218', Name: 'Choice',
            Path: 'Choice', CanExpand: true, CanSelect: true }] });
        await loading;
        const row = root.descendants().find(node => node.tagName === 'li');
        assert.equal(row.className, 'tuneInSelectorRow');
        assert.equal(row.childNodes[0].tagName, 'details');
        const add = row.childNodes[1];
        assert.match(add.className, /tuneInSelectorAdd/);
        add.handlers.click();
        assert.equal(row.childNodes[0].open, undefined);
        assert.equal(app.selectors.length, index + 1);
        const window = root.childNodes[1];
        assert.equal(window.className, 'tuneInSelectorWindow');
        assert.equal(window.attributes.role, 'region');
        assert.equal(window.attributes.tabindex, '0');
        summary.handlers.click({ preventDefault() {} });
        assert.equal(root.open, false);
        summary.handlers.click({ preventDefault() {} });
        assert.equal(root.open, true);
        assert.equal(app.selectors.length, index + 1);
    }
});

test('opening every selected category immediately loads its first level', async () => {
    const app = harness();
    const visit = app.instance.onResume();
    app.reads[0].resolve({ TalkMode: 'selected', SportsMode: 'selected', PodcastsMode: 'selected' });
    await visit;
    enableBrowsing(app);
    for (const [index, category] of ['language', 'location', 'music', 'talk', 'sports', 'podcast', 'local'].entries()) {
        const section = app.elements['.selectors'].childNodes[index];
        section.open = false;
        if ([0, 1, 2, 6].includes(index)) selectOnly(app, index);
        const root = section.descendants().find(node => node.tagName === 'details');
        const loading = section.childNodes[0].handlers.click({ preventDefault() {} });
        assert.equal(section.open, true);
        assert.equal(root.open, true);
        assert.equal(app.selectors.length, index + 1);
        assert.equal(app.selectors[index].options.url.params.Category, category);
        app.selectors[index].resolve({ Items: [{ Id: 'c1', Name: 'First level', CanSelect: true }] });
        await loading;
        assert.match(root.textContent, /First level/);
    }
});

test('choosing selected topics in an open category loads choices without another click', async () => {
    const app = harness();
    const visit = app.instance.onResume();
    app.reads[0].resolve({});
    await visit;
    enableBrowsing(app);
    const section = app.elements['.selectors'].childNodes[3];
    section.childNodes[0].handlers.click({ preventDefault() {} });
    const mode = section.childNodes[1];
    mode.value = 'selected';
    const loading = mode.handlers.change();
    assert.equal(app.selectors.length, 1);
    assert.equal(app.selectors[0].options.url.params.Category, 'talk');
    app.selectors[0].resolve({ Items: [{ Id: 'c1', Name: 'News', CanSelect: true }] });
    await loading;
    assert.match(section.textContent, /News/);
});

test('closing a category cancels its scheduled automatic retry', async () => {
    const app = harness();
    const visit = app.instance.onResume();
    app.reads[0].resolve({});
    await visit;
    enableBrowsing(app);
    const section = app.elements['.selectors'].childNodes[0];
    selectOnly(app, 0);
    section.open = false;
    const loading = section.childNodes[0].handlers.click({ preventDefault() {} });
    app.selectors[0].reject({ status: 503 });
    await settle();
    section.childNodes[0].handlers.click({ preventDefault() {} });
    [...app.timers.values()].find(item => item.delay === 1000).handler();
    await loading;
    assert.equal(app.selectors.length, 1);
});

test('a selector authorization failure remains visible without an automatic retry', async () => {
    const app = harness();
    const visit = app.instance.onResume();
    app.reads[0].resolve({});
    await visit;
    enableBrowsing(app);
    const section = app.elements['.selectors'].childNodes[2];
    selectOnly(app, 2);
    section.open = false;
    const loading = section.childNodes[0].handlers.click({ preventDefault() {} });
    app.selectors[0].reject({ status: 403 });
    await loading;
    assert.equal(app.selectors.length, 1);
    assert.match(section.textContent, /HTTP 403/);
    assert.ok(section.descendants().some(node => node.tagName === 'button' && node.textContent === 'Retry'));
    assert.equal([...app.timers.values()].some(timer => timer.delay === 1000), false);
});

test('an open selector retries a transient first failure once without a second click', async () => {
    const app = harness();
    const visit = app.instance.onResume();
    app.reads[0].resolve({});
    await visit;
    enableBrowsing(app);
    selectOnly(app, 1);
    const root = app.elements['.selectors'].childNodes[1].descendants().find(node => node.tagName === 'details');
    const loading = open(root);
    app.selectors[0].reject({ status: 503 });
    await settle();
    assert.match(root.textContent, /Still loading choices/);
    assert.equal(root.descendants().some(node => node.textContent === 'Retry' && node.tagName === 'button'), false);
    assert.equal(open(root), loading);
    assert.equal(app.selectors.length, 1);
    const timer = [...app.timers.values()].find(item => item.delay === 1000);
    timer.handler();
    await settle();
    assert.equal(app.selectors.length, 2);
    assert.deepEqual(app.selectors[1].options, app.selectors[0].options);
    app.selectors[1].resolve({ Items: [{ Id: 'r101218', Name: 'North America', CanExpand: true }] });
    await loading;
    assert.match(root.textContent, /North America/);
    assert.equal(app.errors.length, 0);
});

test('selector retry is bounded and does not send requests after leaving settings', async () => {
    for (const leave of [false, true]) {
        const app = harness();
        const visit = app.instance.onResume();
        app.reads[0].resolve({});
        await visit;
        enableBrowsing(app);
        selectOnly(app, 0);
        const root = app.elements['.selectors'].childNodes[0].descendants().find(node => node.tagName === 'details');
        const loading = open(root);
        app.selectors[0].reject({ response: { status: 502 } });
        await settle();
        if (leave) app.instance.onPause();
        const timer = [...app.timers.values()].find(item => item.delay === 1000);
        timer.handler();
        await settle();
        assert.equal(app.selectors.length, leave ? 1 : 2);
        if (!leave) app.selectors[1].reject({ status: 502 });
        await loading;
        assert.equal(app.errors.length, leave ? 0 : 1);
        assert.equal(root.descendants().some(node => node.textContent === 'Retry' && node.tagName === 'button'), !leave);
        if (!leave) assert.match(root.textContent, /HTTP 502/);
    }
});

test('native view lifecycle loads labeled upstream settings on each visit', async () => {
    const html = fs.readFileSync(path.join(configDir, 'configPage.html'), 'utf8');
    assert.match(html, /data-controller="__plugin\/tuneinjs"/);
    assert.match(html, /<fieldset class="globalFields"[\s\S]*<fieldset class="loginFields"[\s\S]*<legend>TuneIn Favorites and Location<\/legend>[\s\S]*<label for="tuneInUsername">TuneIn User:<\/label>[\s\S]*class="txtUsername"[\s\S]*class="localSource"[\s\S]*<label for="tuneInLatLon">Your LatLon:<\/label>[\s\S]*class="txtLatLon"[\s\S]*class="configurationCompleted"/);
    assert.doesNotMatch(html, /<h2>Favorites<\/h2>/);
    assert.doesNotMatch(html, /is="emby-input"[^>]*class="txt(?:Username|LatLon)"/);
    const app = harness();
    assert.equal(app.instance.onBeginResume(), 'native transition');
    const first = app.instance.onResume('first');
    app.reads[0].resolve({ Username: 'listener', LatLon: '1,2' });
    await first;
    assert.equal(app.username.value, 'listener');
    assert.equal(app.latLon.value, '1,2');
    assert.equal(String(app.requestInterval.value), '3');
    app.instance.onPause('away');
    const second = app.instance.onResume();
    app.reads[1].resolve({ Username: null, LatLon: null });
    await second;
    assert.equal(app.username.value, '');
    assert.equal(app.latLon.value, '');
    assert.equal(app.loader.shown, 2);
    assert.equal(app.loader.hidden, 2);
    assert.deepEqual(app.calls, ['construct', ['resume', 'first'], ['pause', 'away'], ['resume', undefined]]);
    assert.deepEqual(Object.keys(app.handlers), ['keydown']);
});

test('Local Radio retains the Emby select and an aligned, focusable field outline', () => {
    const html = fs.readFileSync(path.join(configDir, 'configPage.html'), 'utf8');
    assert.match(html, /<label for="tuneInLocalSource">Local Radio source:<\/label>/);
    assert.match(html, /<select is="emby-select" id="tuneInLocalSource" class="localSource"/);
    assert.match(html, /\.tuneInLoginRow input\[type="text"\],\s*\.tuneInLoginRow \.emby-select-wrapper,\s*\.tuneInLoginRow \.localSource:not\(\.emby-select\)\s*\{[^}]*block-size:\s*2\.75rem/s);
    assert.match(html, /\.tuneInLoginRow input\[type="text"\],\s*\.tuneInLoginRow \.emby-select-wrapper,[\s\S]*?border:\s*1px solid currentColor/);
    assert.match(html, /\.tuneInLoginRow \.emby-select-wrapper:focus-within\s*\{[^}]*outline:\s*2px solid currentColor/s);
    assert.match(html, /\.tuneInLoginRow \.selectLabelText:empty\s*\{[^}]*display:\s*none/s);
});

test('cleanup guidance and highlighted backup warning are visible without help', () => {
    const html = fs.readFileSync(path.join(configDir, 'configPage.html'), 'utf8');
    const section = html.slice(html.indexOf('<div class="tuneInCleanupWarning">'), html.indexOf('<label for="tuneInCleanupScope">'));
    assert.match(section, /<h3>Clean Up Library Folders<\/h3>\s*<p>Cleanup removes generated folders/);
    assert.match(section, /<p>For one category, compatible saved snapshots[\s\S]*?Disable a category if it should stay absent\.<\/p>/);
    assert.doesNotMatch(section, /<details|Help: library cleanup/);
    assert.match(section, /<p class="tuneInCleanupBackupWarning"><strong>Cleanup cannot be undone\.<\/strong> Back up Emby and wait for a successful backup before removing any entries\.<\/p>/);
    assert.match(html, /\.tuneInCleanupBackupWarning\s*\{[^}]*border-inline-start:\s*\.25em solid #d64545;[^}]*background:\s*color-mix\(in srgb, #d64545 12%, transparent\)/s);
});

test('error feedback uses a theme-independent highlight for explicit and inline errors', () => {
    const html = fs.readFileSync(path.join(configDir, 'configPage.html'), 'utf8');
    assert.match(html, /\[data-error="true"\]:not\(:empty\):not\(\[hidden\]\)/);
    assert.match(html, /\[role="alert"\]:not\(:empty\):not\(\[hidden\]\)/);
    assert.match(html, /\.tuneInValidationError:not\(:empty\):not\(\[hidden\]\)/);
    assert.match(html, /background: #fff2a8;\s*color: #332900;/);
});

test('save retains unrelated configuration and converts empty fields to null', async () => {
    const app = harness();
    const visit = app.instance.onResume();
    app.reads[0].resolve({ Username: 'old', LatLon: '1,2', Other: 42, DiagnosticsRepository: 'private/override' });
    await visit;
    app.username.value = 'listener';
    app.latLon.value = '';
    let prevented = false;
    assert.equal(app.formHandlers.submit({ preventDefault() { prevented = true; } }), false);
    assert.equal(prevented, true);
    await settle();
    assert.equal(app.writes.length, 1);
    assert.equal(app.writes[0].id, pluginId);
    assert.equal(app.writes[0].config.Username, 'listener');
    assert.equal(app.writes[0].config.LatLon, null);
    assert.equal(app.writes[0].config.Other, 42);
    assert.equal(app.writes[0].config.DiagnosticsRepository, 'private/override');
    assert.equal(app.writes[0].config.SchemaVersion, 1);
    assert.equal(app.writes[0].config.ConfigurationCompleted, true);
    assert.equal(app.writes[0].config.ApiRequestIntervalSeconds, 3);
    assert.equal(app.writes[0].config.MaximumRateLimitWaitMinutes, 15);
    assert.equal(app.writes[0].config.ImportDeadlineHours, 24);
    assert.equal(app.writes[0].config.DiscoveryDeadlineHours, 12);
    assert.equal(app.writes[0].config.EnableCatalogIdentityDiagnostics, false);
    assert.deepEqual(app.notices, ['saved']);
});

test('rate-limit wait validates and saves the configured budget', async () => {
    const app = harness();
    const visit = app.instance.onResume();
    app.reads[0].resolve({});
    await visit;
    const field = app.elements['.maximumRateLimitWaitMinutes'];
    assert.equal(field.value, 15);
    for (const value of ['4', '61', '5.5']) {
        field.value = value;
        app.formHandlers.submit({ preventDefault() {} });
        assert.equal(app.writes.length, 0);
        assert.match(app.elements['.status'].textContent, /whole number from 5 to 60/);
    }
    field.value = '60';
    app.formHandlers.submit({ preventDefault() {} });
    assert.equal(app.writes[0].config.MaximumRateLimitWaitMinutes, 60);
});

test('rate-limit retries accept zero and ten and reject values outside the range', async () => {
    const app = harness();
    const visit = app.instance.onResume();
    app.reads[0].resolve({ MaximumConsecutiveRateLimitRetries: 0 });
    await visit;
    const field = app.elements['.maximumConsecutiveRateLimitRetries'];
    assert.equal(field.value, 0);
    for (const value of ['-1', '11', '1.5', '']) {
        field.value = value;
        app.formHandlers.submit({ preventDefault() {} });
        assert.equal(app.writes.length, 0);
    }
    for (const value of ['0', '10']) {
        const toggle = app.elements['.settingsLocked'];
        if (toggle.checked) { toggle.checked = false; await toggle.handlers.change(); }
        field.value = value;
        app.formHandlers.submit({ preventDefault() {} });
        assert.equal(app.writes.at(-1).config.MaximumConsecutiveRateLimitRetries, Number(value));
        await settle();
    }
});

test('API request interval validates and saves the shared catalog pacing setting', async () => {
    const app = harness();
    const visit = app.instance.onResume();
    app.reads[0].resolve({ ApiRequestIntervalSeconds: 60 });
    await visit;
    assert.equal(app.requestInterval.value, 30);
    for (const value of ['0', '31', '1.5']) {
        app.requestInterval.value = value;
        app.formHandlers.submit({ preventDefault() {} });
        assert.equal(app.writes.length, 0);
        assert.match(app.elements['.status'].textContent, /whole number from 1 to 30/);
    }
    for (const value of [1, 30]) {
        const toggle = app.elements['.settingsLocked'];
        if (toggle.checked) {
            toggle.checked = false;
            await toggle.handlers.change();
        }
        app.requestInterval.value = String(value);
        app.formHandlers.submit({ preventDefault() {} });
        assert.equal(app.writes.at(-1).config.ApiRequestIntervalSeconds, value);
        await settle();
    }
    assert.equal(app.writes.length, 2);
});

test('discovery controls keep missing and null station limits Not set and preserve saved limits', async () => {
    for (const [config, maximum, depth] of [[{}, 500, 10], [{ MaxStationCount: null }, '', 10],
        [{ MaxStationCount: 4000, TraversalDepthLimit: 50 }, 4000, 50]]) {
        const app = harness();
        const visit = app.instance.onResume();
        app.reads[0].resolve(config);
        await visit;
        assert.equal(app.elements['.maxStationCount'].value, maximum);
        assert.equal(app.elements['.traversalDepthLimit'].value, depth);
        app.formHandlers.submit({ preventDefault() {} });
        assert.equal(app.writes[0].config.MaxStationCount, maximum === '' ? null : maximum);
        assert.equal(app.writes[0].config.TraversalDepthLimit, depth);
        await settle();
        assert.equal(app.elements['.maxStationCount'].disabled, true);
        assert.equal(app.elements['.traversalDepthLimit'].disabled, true);
    }
});

test('discovery controls reject invalid values and save inclusive boundaries and blank', async () => {
    const app = harness();
    const visit = app.instance.onResume();
    app.reads[0].resolve({});
    await visit;
    const cap = app.elements['.maxStationCount'];
    const depth = app.elements['.traversalDepthLimit'];
    for (const value of ['4', '10001', '1.5', 'abc']) {
        cap.value = value;
        app.formHandlers.submit({ preventDefault() {} });
        assert.equal(app.writes.length, 0);
        assert.match(app.elements['.status'].textContent, /5 to 10000/);
    }
    cap.value = '';
    for (const value of ['', '2', '51', '3.5']) {
        depth.value = value;
        app.formHandlers.submit({ preventDefault() {} });
        assert.equal(app.writes.length, 0);
        assert.match(app.elements['.status'].textContent, /3 to 50/);
    }
    for (const [maximum, limit] of [['', 3], ['5', 50], ['10000', 5]]) {
        const toggle = app.elements['.settingsLocked'];
        if (toggle.checked) { toggle.checked = false; await toggle.handlers.change(); }
        cap.value = maximum;
        depth.value = String(limit);
        app.formHandlers.submit({ preventDefault() {} });
        assert.equal(app.writes.at(-1).config.MaxStationCount, maximum === '' ? null : Number(maximum));
        assert.equal(app.writes.at(-1).config.TraversalDepthLimit, limit);
        await settle();
    }
});

test('station maintenance previews an exact reference and editing it invalidates confirmation', async () => {
    const app = harness(true);
    const visit = app.instance.onResume();
    app.reads[0].resolve({ SchemaVersion: 1, ConfigurationCompleted: true, EnableTalk: true });
    app.refresh[0].resolve({ SettingsLocked: false, Categories: [{ Category: 'talk', Name: 'Talk' }] });
    await visit;
    await settle();
    await open(app.elements['.maintenanceSection']);
    app.elements['.cleanupScope'].value = 'station';
    app.elements['.cleanupScope'].handlers.change();
    assert.equal(app.elements['.cleanupStationFields'].hidden, false);
    assert.equal(app.elements['.cleanupPreview'].disabled, true);
    const reference = app.elements['.cleanupStationReference'];
    reference.value = 'http://emby.test/web/index.html#!/item?id=3';
    reference.handlers.input();
    const preview = app.elements['.cleanupPreview'].handlers.click();
    assert.deepEqual(JSON.parse(app.cleanup[0].options.data), { StationReference: reference.value });
    app.cleanup[0].resolve({ Token: 'station-token', Scope: 'Talk', Folders: 0, Stations: 1,
        StationName: '<img src=x>', StationReference: '3' });
    await preview;
    assert.match(app.elements['.cleanupPreviewText'].textContent, /Remove only "<img src=x>" \(Emby item 3\)/);
    assert.equal(app.elements['.cleanupPreviewText'].childNodes.length, 0);
    assert.equal(app.elements['.cleanupConfirm'].textContent, 'Confirm delete station');
    reference.value = '4';
    reference.handlers.input();
    assert.equal(app.instance.cleanupPreview, null);
    assert.equal(app.elements['.cleanupConfirmation'].hidden, true);
    assert.equal(app.elements['.cleanupConfirm'].disabled, true);
});

test('station maintenance finds entries by name and previews the chosen placement', async () => {
    const app = harness(true);
    const visit = app.instance.onResume();
    app.reads[0].resolve({ SchemaVersion: 1, ConfigurationCompleted: true, EnableTalk: true });
    app.refresh[0].resolve({ SettingsLocked: false, Categories: [{ Category: 'talk', Name: 'Talk' }] });
    await visit;
    await settle();
    await open(app.elements['.maintenanceSection']);
    app.elements['.cleanupScope'].value = 'station';
    app.elements['.cleanupScope'].handlers.change();
    const reference = app.elements['.cleanupStationReference'];
    reference.value = 'KQED';
    reference.handlers.input();
    assert.equal(app.elements['.cleanupPreview'].disabled, true);
    assert.equal(app.elements['.cleanupStationSearch'].disabled, false);
    let prevented = false;
    const search = reference.handlers.keydown({ key: 'Enter', preventDefault() { prevented = true; } });
    assert.equal(prevented, true);
    assert.equal(app.cleanup[0].options.url.route, 'TuneIn/Cleanup/Stations');
    assert.equal(app.cleanup[0].options.url.params.Name, 'KQED');
    app.cleanup[0].resolve({ Matches: [
        { Id: '31', Name: 'KQED <b>', Path: 'Talk › News' },
        { Id: '32', Name: 'KQED', Path: 'Local Radio' }
    ] });
    await search;
    const matches = app.elements['.cleanupStationResults'].childNodes;
    assert.equal(matches.length, 2);
    assert.equal(matches[0].textContent, 'KQED <b>Talk › News');
    assert.equal(app.elements['.cleanupPreview'].disabled, true);
    matches[1].handlers.click();
    assert.equal(matches[1].attributes['aria-pressed'], 'true');
    assert.equal(matches[0].attributes['aria-pressed'], 'false');
    assert.equal(app.elements['.cleanupPreview'].disabled, false);
    const preview = app.elements['.cleanupPreview'].handlers.click();
    assert.deepEqual(JSON.parse(app.cleanup[1].options.data), { StationReference: '32' });
    app.cleanup[1].resolve({ Token: 't', Scope: 'Local Radio', Folders: 0, Stations: 1, StationName: 'KQED', StationReference: '32' });
    await preview;
    assert.equal(app.elements['.cleanupConfirmation'].hidden, false);
    reference.value = 'KQ';
    reference.handlers.input();
    assert.equal(app.elements['.cleanupStationResults'].childNodes.length, 0);
    assert.equal(app.elements['.cleanupConfirmation'].hidden, true);
    assert.equal(app.elements['.cleanupPreview'].disabled, true);
});

test('limited discovery completes per design and shares the success color', async () => {
    const app = harness(true);
    const visit = app.instance.onResume();
    app.reads[0].resolve({ SchemaVersion: 1, ConfigurationCompleted: true, EnableTalk: true });
    app.refresh[0].resolve({ State: 'Limited', SettingsLocked: false,
        Categories: [{ Category: 'talk', Name: 'Talk', State: 'Limited (imported)' }] });
    await visit;
    await settle();
    assert.equal(app.elements['.tuneInRefresh'].attributes['data-outcome'], 'Completed');
    assert.match(app.elements['.refreshOverall'].textContent, /successful \(limited by discovery settings\)/);
});

test('limited category rows are green and explain which discovery limit was reached', async () => {
    const app = harness(true);
    const visit = app.instance.onResume();
    app.elements['.refreshSummary'].handlers.click({ preventDefault() {} });
    app.reads[0].resolve({ SchemaVersion: 1, ConfigurationCompleted: true, EnableMusic: true });
    app.refresh[0].resolve({ State: 'Limited', SettingsLocked: false, Categories: [{ Category: 'music', Name: 'Music',
        State: 'Limited (imported)', LimitReason: 'TuneIn traversal depth limit 7 reached (including pagination)' }] });
    await visit;
    const row = app.elements['.tuneInRefreshCategories'].childNodes[0];
    assert.equal(row.attributes['data-outcome'], 'Limited');
    assert.equal(row.childNodes[0].textContent,
        'Music: Limited (imported) - TuneIn traversal depth limit 7 reached (including pagination)');
    const html = fs.readFileSync(path.join(__dirname, '..', 'MediaBrowser.Plugins.TuneIn', 'Configuration', 'configPage.html'), 'utf8');
    assert.ok(html.includes('.tuneInRefreshCategories [data-outcome="Completed"], .tuneInRefreshCategories [data-outcome="Limited"] {'));
});

for (const setting of ['Import', 'Discovery']) {
test(`${setting.toLowerCase()} deadline loads saved values, validates whole hours, and saves both bounds`, async () => {
    const app = harness();
    const visit = app.instance.onResume();
    app.reads[0].resolve({ [setting + 'DeadlineHours']: 7 });
    await visit;
    const deadline = app.elements['.' + setting.toLowerCase() + 'DeadlineHours'];
    const minimum = setting === 'Import' ? 1 : 2;
    const maximum = setting === 'Import' ? 100 : 48;
    assert.equal(deadline.value, 7);
    for (const value of ['', String(minimum - 1), String(maximum + 1), '1.5', 'abc']) {
        deadline.value = value;
        app.formHandlers.submit({ preventDefault() {} });
        assert.equal(app.writes.length, 0);
        assert.ok(app.elements['.status'].textContent.includes('whole number from ' + minimum + ' to ' + maximum));
    }
    for (const value of [minimum, maximum]) {
        const toggle = app.elements['.settingsLocked'];
        if (toggle.checked) {
            toggle.checked = false;
            await toggle.handlers.change();
        }
        deadline.value = String(value);
        app.formHandlers.submit({ preventDefault() {} });
        assert.equal(app.writes.at(-1).config[setting + 'DeadlineHours'], value);
        await settle();
        assert.equal(deadline.disabled, true);
    }
    assert.equal(app.writes.length, 2);
});
}

test('identity diagnostics load, save and lock with the advanced catalog settings', async () => {
    const app = harness();
    const visit = app.instance.onResume();
    app.reads[0].resolve({ EnableCatalogIdentityDiagnostics: true });
    await visit;
    const diagnostics = app.elements['.enableCatalogIdentityDiagnostics'];
    assert.equal(diagnostics.checked, true);
    assert.equal(diagnostics.disabled, false);
    diagnostics.checked = false;
    app.formHandlers.submit({ preventDefault() {} });
    assert.equal(app.writes[0].config.EnableCatalogIdentityDiagnostics, false);
    await settle();
    assert.equal(diagnostics.disabled, true);
    const toggle = app.elements['.settingsLocked'];
    toggle.checked = false;
    await toggle.handlers.change();
    diagnostics.checked = true;
    app.formHandlers.submit({ preventDefault() {} });
    assert.equal(app.writes[1].config.EnableCatalogIdentityDiagnostics, true);
    await settle();
    assert.equal(diagnostics.disabled, true);
});

test('direct HLS playback is off by default and loads, saves and locks with advanced settings', async () => {
    const app = harness();
    const visit = app.instance.onResume();
    app.reads[0].resolve({ AllowDirectHlsPlayback: true });
    await visit;
    const hls = app.elements['.allowDirectHlsPlayback'];
    assert.equal(hls.checked, true);
    assert.equal(hls.disabled, false);
    hls.checked = false;
    app.formHandlers.submit({ preventDefault() {} });
    assert.equal(app.writes[0].config.AllowDirectHlsPlayback, false);
    await settle();
    assert.equal(hls.disabled, true);
    const html = fs.readFileSync(path.join(__dirname, '..', 'MediaBrowser.Plugins.TuneIn', 'Configuration', 'configPage.html'), 'utf8');
    assert.match(html, /class="allowDirectHlsPlayback"[^>]*disabled/);
});

test('advanced settings start collapsed and group the controls in order', () => {
    const html = fs.readFileSync(path.join(configDir, 'configPage.html'), 'utf8');
    const start = html.indexOf('<details class="tuneInAdvancedSettings">');
    assert.ok(start >= 0);
    const advanced = html.slice(start, html.indexOf('</fieldset>', start));
    assert.match(advanced, /<summary><span>Advanced Settings<\/span> <a href="#tuneInAdvancedGuide" class="tuneInAdvancedGuideLink">Settings guide<\/a><\/summary>/);
    const groups = ['Discovery limits', 'Request pacing &amp; retry', 'Import behavior', 'Playback &amp; diagnostics'];
    assert.ok(groups.every((group, i) => advanced.indexOf(`<summary>${group}</summary>`) >= 0
        && (i === 0 || advanced.indexOf(`<summary>${groups[i - 1]}</summary>`) < advanced.indexOf(`<summary>${group}</summary>`))));
    for (const group of groups)
        assert.ok(advanced.includes(`<details>\n                            <summary>${group}</summary>`) ||
            advanced.includes(`<details>\r\n                            <summary>${group}</summary>`), `${group} should start collapsed`);
    assert.ok(advanced.indexOf('id="tuneInDiscoveryDeadline"') < advanced.indexOf('id="tuneInApiInterval"'));
    assert.ok(advanced.indexOf('id="tuneInApiInterval"') < advanced.indexOf('id="tuneInImportDeadline"'));
    assert.ok(advanced.indexOf('id="tuneInImportDeadline"') < advanced.indexOf('class="enableCatalogIdentityDiagnostics"'));
    assert.match(advanced, /Off by default/);
});

test('advanced settings guide opens without toggling settings and returns focus', async () => {
    const app = harness();
    const visit = app.instance.onResume();
    app.reads[0].resolve({});
    await visit;
    let prevented = false, stopped = false;
    app.elements['.tuneInAdvancedGuideLink'].handlers.click({
        preventDefault() { prevented = true; }, stopPropagation() { stopped = true; }
    });
    assert.equal(prevented && stopped, true);
    assert.equal(app.elements['.tuneInAdvancedGuide'].open, true);
    app.elements['.tuneInAdvancedGuideClose'].click();
    assert.equal(app.elements['.tuneInAdvancedGuide'].open, false);
    assert.equal(app.elements['.tuneInAdvancedGuideLink'].focused, true);
    const html = fs.readFileSync(path.join(configDir, 'configPage.html'), 'utf8');
    const guide = html.match(/<dialog id="tuneInAdvancedGuide"[\s\S]*?<\/dialog>/)[0];
    assert.match(guide, /<dt>Import deadline<\/dt>\s*<dd>[\s\S]*?<strong class="tuneInImportDeadlineWarning">If it expires, the refresh stops; a manual retry starts the transfer from the beginning\.<\/strong><\/dd>/);
    assert.match(guide, /<dt>Import during category discovery<\/dt>\s*<dd>[^]*?<p class="tuneInDiscoveryBackupWarning"><strong>Back up Emby, including library metadata and TuneIn configuration, before testing\.<\/strong><\/p>\s*<\/dd>/);
    assert.match(html, /\.tuneInAdvancedGuide dd \.tuneInDiscoveryBackupWarning\s*\{\s*margin-inline-start:\s*1\.25em/);
    for (const name of ['Max Station Count', 'Traversal depth limit', 'Discovery deadline',
        'API request interval', 'Maximum rate-limit wait per refresh', 'Consecutive HTTP 429 retries',
        'Import deadline', 'Import during category discovery', 'First pass after entries',
        'Minimum new entries between passes', 'Minimum growth between passes', 'Minimum time between passes',
        'Maximum discovery-time passes per category', 'Enable catalog identity diagnostics', 'Allow direct HLS playback'])
        assert.ok(guide.includes(`<dt>${name}</dt>`), name);
});

test('discovery import pacing is opt-in, editable, and validated', async () => {
    const app = harness();
    const visit = app.instance.onResume();
    app.reads[0].resolve({});
    await visit;
    const enabled = app.elements['.enableDiscoveryProgressiveImport'];
    const first = app.elements['.progressiveFirstEntries'];
    assert.equal(enabled.checked, false);
    assert.equal(app.elements['.progressivePacing'].hidden, true);
    assert.equal(first.value, 50);
    assert.equal(first.disabled, true);
    enabled.checked = true;
    enabled.handlers.change.call(enabled);
    assert.equal(enabled.checked, false);
    assert.equal(app.elements['.discoveryRiskDialog'].open, true);
    assert.equal(app.elements['.discoveryRiskConfirm'].disabled, true);
    assert.equal(app.elements['.discoveryRiskPartialRequirement'].hidden, true);
    assert.equal(app.elements['.discoveryRiskConfirm'].textContent, 'Accept and continue');
    assert.equal(app.elements['.discoveryRiskDialog'].attributes['aria-describedby'], 'discoveryRiskDescription');
    app.elements['.discoveryRiskCancel'].click();
    assert.equal(enabled.checked, false);
    enabled.checked = true;
    enabled.handlers.change.call(enabled);
    const acknowledgement = app.elements['.discoveryRiskAccepted'];
    acknowledgement.checked = true;
    acknowledgement.handlers.change.call(acknowledgement);
    app.elements['.discoveryRiskConfirm'].click();
    assert.equal(app.elements['.progressivePacing'].hidden, false);
    assert.equal(first.disabled, false);
    first.value = '0';
    app.formHandlers.submit({ preventDefault() {} });
    assert.equal(app.writes.length, 0);
    first.value = '60';
    app.formHandlers.submit({ preventDefault() {} });
    await settle();
    assert.equal(app.writes[0].config.EnableDiscoveryProgressiveImport, true);
    assert.equal(app.selectors.filter(item => item.options.url.route === 'TuneIn/DiscoveryImport/Acknowledge').length, 1);
    assert.equal(app.writes[0].config.ProgressiveFirstEntries, 60);
    assert.equal(app.writes[0].config.ProgressiveMinimumNewEntries, 500);
});

test('discovery import offers its disabled prerequisite without changing settings on cancel', async () => {
    const app = harness(false, true);
    const visit = app.instance.onResume();
    app.reads[0].resolve({ AllowPartialCatalogImport: false });
    await visit;
    const toggle = app.elements['.enableDiscoveryProgressiveImport'];
    const partial = app.elements['.allowPartialCatalogImport'];
    const accepted = app.elements['.discoveryRiskAccepted'];
    const confirm = app.elements['.discoveryRiskConfirm'];
    function openNotice() {
        toggle.checked = true;
        toggle.handlers.change.call(toggle);
    }
    openNotice();
    assert.equal(app.elements['.discoveryRiskPartialRequirement'].hidden, false);
    assert.equal(confirm.textContent, 'Accept and enable both');
    assert.equal(app.elements['.discoveryRiskDialog'].attributes['aria-describedby'], 'discoveryRiskDescription discoveryRiskPartialRequirement');
    assert.equal(confirm.disabled, true);
    app.elements['.discoveryRiskCancel'].click();
    assert.equal(toggle.checked, false);
    assert.equal(partial.checked, false);
    assert.equal(app.writes.length, 0);

    openNotice();
    accepted.checked = true;
    accepted.handlers.change.call(accepted);
    confirm.click();
    assert.equal(toggle.checked, true);
    assert.equal(partial.checked, true);
    assert.equal(app.writes.length, 0);
    app.formHandlers.submit({ preventDefault() {} });
    await settle();
    assert.equal(app.writes[0].config.EnableDiscoveryProgressiveImport, true);
    assert.equal(app.writes[0].config.AllowPartialCatalogImport, true);
    app.writes[0].reject(new Error('save failed'));
    await settle();
    assert.match(app.elements['.status'].textContent, /not saved/i);
});

test('failed discovery risk acknowledgement prevents saving', async () => {
    const app = harness(false, false, false, false, false, true);
    const visit = app.instance.onResume();
    app.reads[0].resolve({});
    await visit;
    const toggle = app.elements['.enableDiscoveryProgressiveImport'];
    toggle.checked = true;
    toggle.handlers.change.call(toggle);
    const accepted = app.elements['.discoveryRiskAccepted'];
    accepted.checked = true;
    accepted.handlers.change.call(accepted);
    app.elements['.discoveryRiskConfirm'].click();
    app.formHandlers.submit({ preventDefault() {} });
    const request = app.selectors.find(item => item.options.url.route === 'TuneIn/DiscoveryImport/Acknowledge');
    request.reject(new Error('unavailable'));
    await settle();
    assert.equal(app.writes.length, 0);
    assert.match(app.elements['.status'].textContent, /not saved/i);
});

test('previously enabled discovery imports require a fresh acknowledgement', async () => {
    const app = harness();
    const visit = app.instance.onResume();
    app.reads[0].resolve({ EnableDiscoveryProgressiveImport: true });
    await visit;
    assert.equal(app.elements['.enableDiscoveryProgressiveImport'].checked, false);
    assert.equal(app.elements['.discoveryRiskUpgradeNotice'].hidden, false);
});

test('artwork scan actions and preference are grouped beside artwork pack controls with help', () => {
    const html = fs.readFileSync(path.join(configDir, 'configPage.html'), 'utf8');
    const artworkStart = html.indexOf('<details class="tuneInArtworkPacks">');
    const advancedStart = html.indexOf('<details class="tuneInAdvancedSettings">');
    const artwork = artworkStart >= 0 && html.slice(artworkStart, advancedStart);
    const actions = artwork && artwork.match(/<div class="tuneInArtworkActions">([\s\S]*?)<\/div>/);
    const preference = artwork && artwork.match(/<div class="tuneInArtworkCheckPreference">([\s\S]*?)<\/div>/);
    assert.ok(artwork);
    assert.match(artwork, /<summary>Custom Artwork Packs<\/summary>/);
    assert.doesNotMatch(artwork.slice(0, artwork.indexOf('>') + 1), /\bopen\b/);
    assert.ok(actions);
    assert.ok(preference);
    assert.doesNotMatch(html, /optionalChecksPrompt|optionalChecksSkip|optionalChecksKeep/);
    assert.ok(actions[1].indexOf('class="raised button-submit emby-button artworkUpload"') <
        actions[1].indexOf('class="raised button-submit optionalChecksRun"'));
    assert.ok(artwork.indexOf(actions[0]) < artwork.indexOf(preference[0]));
    assert.match(preference[0], /Skip automatic artwork pack status checks/);
    assert.match(preference[0], /aria-describedby="tuneInOptionalChecksHint"/);
    assert.match(preference[0], /id="tuneInOptionalChecksHint" role="tooltip"/);
    assert.match(html, /\.tuneInArtworkHelp\[open\] \.tuneInArtworkTooltip/);
    assert.match(artwork, /class="optionalChecksStatus"/);
    assert.doesNotMatch(html.slice(advancedStart), /skipOptionalChecks|optionalChecksMessage|optionalChecksStatus|optionalChecksRun/);
    for (const selector of ['optionalChecksRun', 'optionalChecksStatus', 'artworkUpload'])
        assert.equal((html.match(new RegExp(`class="[^"]*\\b${selector}\\b[^"]*"`, 'g')) || []).length, 1);
});

test('help disclosure closes on Escape and returns focus to its summary', () => {
    const app = harness();
    const summary = new Element('summary');
    const help = { open: true, querySelector: () => summary };
    let prevented = false;
    let stopped = false;
    app.handlers.keydown({
        key: 'Escape', target: { closest: () => help },
        preventDefault: () => { prevented = true; },
        stopPropagation: () => { stopped = true; }
    });
    assert.equal(help.open, false);
    assert.equal(summary.focused, true);
    assert.ok(prevented && stopped);
});

test('help opens on pointer hover and can be pinned with a click or dismissed with Escape', () => {
    const { help, helpSummary, handlers } = harness();
    const click = () => helpSummary.handlers.click({ preventDefault() {} });
    const enter = type => help.handlers.pointerenter({ pointerType: type });
    const leave = type => help.handlers.pointerleave({ pointerType: type });
    enter('touch');
    assert.ok(!help.open);
    enter('mouse');
    assert.equal(help.open, true);
    assert.equal(help.style['--tunein-help-left'], '44px');
    click();
    leave('mouse');
    assert.equal(help.open, true);
    click();
    assert.equal(help.open, false);
    enter('mouse');
    handlers.keydown({ key: 'Escape', target: { closest: () => help }, preventDefault() {}, stopPropagation() {} });
    assert.equal(help.open, false);
    assert.equal(helpSummary.focused, true);
    leave('mouse');
    enter('mouse');
    assert.equal(help.open, true);
    leave('mouse');
    assert.equal(help.open, false);
    click();
    assert.equal(help.open, true);
    click();
    assert.equal(help.open, false);
});

test('help falls back below when there is not enough space to its right', () => {
    const { help } = harness();
    help.getBoundingClientRect = () => ({ left: 700, right: 744, top: 100, bottom: 144, width: 44, height: 44 });
    help.handlers.pointerenter({ pointerType: 'mouse' });
    assert.equal(help.style['--tunein-help-left'], '-208px');
    assert.equal(help.style['--tunein-help-top'], '44px');
});

test('help offsets account for page zoom', () => {
    const { help } = harness();
    help.offsetWidth = 44;
    help.getBoundingClientRect = () => ({ left: 500, right: 588, top: 100, bottom: 188, width: 88, height: 88 });
    help.querySelector(':scope > :not(summary)').getBoundingClientRect = () => ({ width: 600, height: 200 });
    help.handlers.pointerenter({ pointerType: 'mouse' });
    assert.equal(help.style['--tunein-help-left'], '-154px');
    assert.equal(help.style['--tunein-help-top'], '44px');
});

test('invalid advanced fields open their containing disclosures', () => {
    const app = harness();
    const outer = { tagName: 'DETAILS', open: false, parentElement: null };
    const group = { tagName: 'DETAILS', open: false, parentElement: outer };
    const wrapper = { tagName: 'DIV', parentElement: group };
    app.formHandlers.invalid({ target: { parentElement: wrapper } });
    assert.equal(outer.open, true);
    assert.equal(group.open, true);
});

test('cleanup categories follow the channel order', () => {
    const html = fs.readFileSync(path.join(configDir, 'configPage.html'), 'utf8');
    const select = html.match(/<select[^>]*id="tuneInCleanupScope"[^>]*>([\s\S]*?)<\/select>/);
    assert.ok(select);
    const values = [...select[1].matchAll(/<option value="([^"]*)"/g)].map(match => match[1]);
    assert.deepEqual(values, ['', 'station', 'all', 'presets', 'local', 'music', 'language', 'location', 'talk', 'sports', 'podcast']);
});

test('static admin controls have unique IDs and valid accessible description references', () => {
    const html = fs.readFileSync(path.join(configDir, 'configPage.html'), 'utf8');
    const ids = [...html.matchAll(/\sid="([^"]+)"/g)].map(match => match[1]);
    assert.equal(new Set(ids).size, ids.length);
    for (const match of html.matchAll(/\saria-(?:describedby|labelledby)="([^"]+)"/g)) {
        for (const id of match[1].split(/\s+/)) assert.ok(ids.includes(id), `Missing referenced ID: ${id}`);
    }
    for (const match of html.matchAll(/<details class="tuneInHelp">\s*<summary([^>]*)>/g))
        assert.match(match[1], /aria-label="Help: [^"]+"/);
    assert.match(html, /Warning: Skipped entries can represent whole branches/);
    assert.match(html, /Privacy warning: Diagnostic folder labels may be private/);
    assert.match(html, /Warning: Emby fetches direct HLS streams without/);
    assert.match(html, /class="enableDiscoveryProgressiveImport" aria-describedby="tuneInDiscoveryImportWarning tuneInDiscoveryImportHint"/);
    assert.match(html, /<p id="tuneInDiscoveryImportWarning"[^>]*>Experimental:[^<]*isolation risks/);
});

test('help icons share one visual design and sit alongside field labels', () => {
    const html = fs.readFileSync(path.join(configDir, 'configPage.html'), 'utf8');
    assert.match(html, /\.tuneInHelp > summary::before,\s*\.tuneInArtworkHelp > summary::before/);
    for (const [label, help] of [['tuneInUsername', 'TuneIn user'], ['tuneInLatLon', 'coordinates']]) {
        assert.match(html, new RegExp(`<div class="tuneInFieldHeading">\\s*<label for="${label}">[^<]+<\\/label>\\s*<details class="tuneInHelp"><summary aria-label="Help: ${help}">`));
    }
    for (const [label, help] of [
        ['tuneInMaxStationCount', 'Max Station Count'], ['tuneInDiscoveryDeadline', 'discovery deadline'],
        ['tuneInImportDeadline', 'import deadline'], ['tuneInApiInterval', 'API request interval']
    ]) {
        assert.match(html, new RegExp(`<label for="${label}">[^<]+<\\/label>\\s*<input id="${label}"[^>]+>\\s*<details class="tuneInHelp"><summary aria-label="Help: ${help}">`));
    }
    assert.match(html, /tuneInApiIntervalRow input\s*\{[^}]*inline-size:\s*6ch/);
    for (const text of ['Choose All or Selected only for each browser category.',
        'Refresh all resumes interrupted work first', 'Choose a refresh interval next to a category',
        'Distinct stations at the last category refresh']) {
        assert.match(html, new RegExp(`<p class="fieldDescription">${text.replace(/[.*+?^${}()|[\]\\]/g, '\\$&')}`));
    }
    assert.match(html, /<details>\s*<summary>Usability Guidance<\/summary>\s*<p class="fieldDescription">Choose a refresh interval next to a category,[\s\S]*?first complete manual baseline\.<\/p>\s*<p class="fieldDescription">Distinct stations at the last category refresh\.[\s\S]*?failed inventory checks\.<\/p>\s*<\/details>\s*<p class="refreshCategoryNote"/);
    const advanced = html.match(/\.tuneInAdvancedSettings\s*\{([^}]+)\}/)[1];
    assert.match(advanced, /background:\s*color-mix\(in srgb, #d9822b 14%, hsl\(var\(--background-hue/);
    assert.doesNotMatch(advanced, /\[open\]/);
});

test('refresh keeps status and actions visible before collapsed details and shareable diagnostics', () => {
    const html = fs.readFileSync(path.join(configDir, 'configPage.html'), 'utf8');
    const start = html.indexOf('<details class="tuneInSection tuneInNeutralPanel tuneInRefresh">');
    const categories = html.indexOf('<section class="tuneInSectionBox tuneInLibraryRefresh"', start);
    const details = html.indexOf('<details class="tuneInSectionBox tuneInRefreshDetails">', categories);
    const shareable = html.indexOf('<details class="tuneInSectionBox tuneInDiagnostics">', details);
    assert.ok(start >= 0 && categories > start && details > categories && shareable > details);
    const primary = html.slice(start, categories);
    for (const control of ['refreshStatus', 'refreshElapsed', 'refreshWork', 'refreshLastProgress',
        'refreshStart', 'refreshCancel', 'refreshQueueNote', 'refreshError'])
        assert.ok(primary.includes(control), control);
    const secondary = html.slice(details, shareable);
    for (const control of ['refreshImportContents', 'refreshPendingFolders', 'refreshDiagnosticsText',
        'refreshLogButton', 'refreshLogMessage', 'tuneInReportHint', 'refreshFailureLog'])
        assert.ok(secondary.includes(control), control);
    assert.match(secondary, /<summary>Refresh details and private log<\/summary>/);
    assert.match(html.slice(shareable), /<summary>TuneIn plugin support \(GitHub\)<\/summary>/);
    assert.match(html.slice(shareable), /not official TuneIn support/);
});

test('artwork tooltips use the host theme for both foreground and background', () => {
    const html = fs.readFileSync(path.join(configDir, 'configPage.html'), 'utf8');
    const rule = html.match(/\.tuneInHelp > :not\(summary\),\s*\.tuneInArtworkHelp > :not\(summary\)\s*\{([^}]+)\}/)[1];
    assert.match(rule, /background:\s*hsl\(var\(--background-hue/);
    assert.match(rule, /color:\s*hsl\(var\(--theme-text-color-hue/);
});

test('navigation discards stale loads without hiding the next visit loader', async () => {
    const app = harness();
    const first = app.instance.onResume();
    app.instance.onPause();
    assert.equal(app.loader.hidden, 1);
    const second = app.instance.onResume();
    app.reads[0].resolve({ Username: 'stale', LatLon: 'stale' });
    await first;
    assert.equal(app.username.value, '');
    assert.equal(app.loader.hidden, 1);
    app.reads[1].resolve({ Username: 'current', LatLon: '3,4' });
    await second;
    assert.equal(app.username.value, 'current');
    assert.equal(app.latLon.value, '3,4');
    assert.equal(app.loader.hidden, 2);
});

test('a failed active load reports the error and releases the loader', async () => {
    const app = harness();
    const loading = app.instance.onResume();
    app.reads[0].reject(new Error('offline'));
    await loading;
    assert.equal(app.loader.hidden, 1);
    assert.deepEqual(app.notices, ['Unable to load TuneIn configuration.']);
    assert.equal(app.errors.length, 1);
});

test('a failed stale load does not alert or disturb a later visit', async () => {
    const app = harness();
    const first = app.instance.onResume();
    app.instance.onPause();
    const second = app.instance.onResume();
    app.reads[0].reject(new Error('offline'));
    await first;
    assert.deepEqual(app.notices, []);
    assert.equal(app.errors.length, 0);
    assert.equal(app.loader.hidden, 1);
    app.reads[1].resolve({ Username: 'current', LatLon: '' });
    await second;
    assert.equal(app.username.value, 'current');
    assert.equal(app.loader.hidden, 2);
});

test('opening settings is lazy and native checkbox captions remain visible text', async () => {
    const app = harness();
    const visit = app.instance.onResume();
    app.reads[0].resolve({ Music: [{ Id: 'g1', Name: '<img src=x onerror=alert(1)>', Enabled: true }] });
    await visit;
    assert.equal(app.selectors.length, 0);
    const categories = app.elements['.categories'].descendants().filter(item => item.tagName === 'input');
    assert.equal(categories.length, 8);
    assert.ok(categories.every(input => input.attributes.is === 'emby-checkbox'));
    const labels = app.elements['.categories'].descendants().filter(item => item.tagName === 'label');
    assert.ok(labels.every(label => label.className === 'emby-checkbox-label' && label.childNodes[1].tagName === 'span'));
    const saved = app.elements['.selectors'].descendants().find(item => item.tagName === 'span' && item.textContent.startsWith('<img'));
    assert.equal(saved.textContent, '<img src=x onerror=alert(1)>');
    assert.equal(saved.childNodes.length, 0);
    assert.ok(app.enhanced.some(event => event.event === 'create'));
    assert.match(app.elements['.status'].textContent, /TuneIn catalog refresh is unavailable/);
    assert.equal(app.elements['.configurationCompleted'].checked, true);
    assert.equal(app.elements['.status'].textContent,
        'The TuneIn catalog refresh is unavailable until Global Settings have been reviewed and saved.');
    assert.equal(app.elements['.status'].attributes['data-configuration-required'], 'true');
    assert.equal(app.elements['.browsingSection'].attributes['data-filtering-configured'], 'false');
    assert.equal(app.elements['.browsingSection'].open, true);
});

test('saved filtering opt-out remains disabled', async () => {
    const app = harness();
    const visit = app.instance.onResume();
    app.reads[0].resolve({ SchemaVersion: 1, ConfigurationCompleted: false });
    await visit;
    assert.equal(app.elements['.configurationCompleted'].checked, false);
    assert.equal(app.elements['.browsingSection'].attributes['data-filtering-configured'], 'false');
    assert.equal(app.elements['.browsingSection'].open, true);
});

test('enabled filtering collapses the filtering section', async () => {
    const app = harness();
    const visit = app.instance.onResume();
    app.reads[0].resolve({ SchemaVersion: 1, ConfigurationCompleted: true });
    await visit;
    assert.equal(app.elements['.status'].attributes['data-configuration-required'], 'false');
    assert.equal(app.elements['.browsingSection'].attributes['data-filtering-configured'], 'true');
    assert.equal(app.elements['.browsingSection'].open, false);
});

test('station filtering categories follow the full refresh order', async () => {
    const app = harness();
    const visit = app.instance.onResume();
    app.reads[0].resolve({ SchemaVersion: 1, ConfigurationCompleted: true, EnableFavorites: true, EnableTalk: true });
    await visit;
    const controls = app.elements['.categories'].descendants()
        .filter(element => element.tagName === 'input');
    assert.deepEqual(controls.map(element => element.attributes['data-category']), [
        'EnableFavorites', 'EnableLocalRadio', 'EnableMusic', 'EnableLanguage',
        'EnableLocation', 'EnableTalk', 'EnableSports', 'EnablePodcasts'
    ]);
    assert.equal(controls[0].checked, true);
    assert.equal(controls[5].checked, true);
    assert.equal(controls[1].checked, false);
    app.instance.onPause();
});

test('locked station filtering remains inspectable without unlocking its fields', async () => {
    const app = harness(true);
    const visit = app.instance.onResume();
    app.refresh[0].resolve({ State: 'Completed', HasCompleteBaseline: true, SettingsLocked: true });
    app.reads[0].resolve({ SchemaVersion: 1, ConfigurationCompleted: true, SettingsSaved: true });
    await visit;
    await settle();
    assert.equal(app.elements['.configurationFields'].disabled, true);
    for (const selector of ['.browsingSummary', '.categoriesSummary', '.stationSelectionsSummary']) {
        const summary = app.elements[selector];
        const wasOpen = summary.parentNode.open;
        summary.handlers.click({ preventDefault() {} });
        assert.equal(summary.parentNode.open, !wasOpen);
        summary.handlers.click({ preventDefault() {} });
        assert.equal(summary.parentNode.open, wasOpen);
    }
    assert.equal(app.elements['.configurationFields'].disabled, true);
    const html = fs.readFileSync(path.join(configDir, 'configPage.html'), 'utf8');
    assert.doesNotMatch(html, /summary\.browsingSummary\s*,\s*\.tuneInSection\s*>\s*summary\.refreshSummary\s*\{\s*display:\s*inline-block/);
});

test('opening Station Filtering expands both groups but still allows independent collapse', async () => {
    for (const locked of [true, false]) {
        const app = harness(true);
        const visit = app.instance.onResume();
        app.refresh[0].resolve({ State: 'Completed', HasCompleteBaseline: true, SettingsLocked: locked });
        app.reads[0].resolve({ SchemaVersion: 1, ConfigurationCompleted: true, SettingsSaved: true });
        await visit;
        const parent = app.elements['.browsingSection'];
        const groups = ['.categoriesSummary', '.stationSelectionsSummary'].map(selector => app.elements[selector]);
        const click = summary => summary.handlers.click({ preventDefault() {} });
        assert.equal(parent.open, false);
        click(app.elements['.browsingSummary']);
        assert.equal(parent.open, true);
        assert.ok(groups.every(summary => summary.parentNode.open));
        click(groups[0]);
        assert.equal(groups[0].parentNode.open, false);
        assert.equal(groups[1].parentNode.open, true);
        click(groups[1]);
        assert.ok(groups.every(summary => !summary.parentNode.open));
        [...app.timers.values()][0].handler();
        app.refresh[1].resolve({ State: 'Completed', HasCompleteBaseline: true, SettingsLocked: locked });
        await settle();
        assert.ok(groups.every(summary => !summary.parentNode.open));
        click(app.elements['.browsingSummary']);
        assert.equal(parent.open, false);
        click(app.elements['.browsingSummary']);
        assert.ok(groups.every(summary => summary.parentNode.open));
        assert.equal(app.elements['.configurationFields'].disabled, locked);
        assert.equal(app.selectors.length, 0);
        app.instance.onPause();
    }
});

test('library refresh is an always-expanded section inside the collapsible Catalog Refresh', () => {
    const html = fs.readFileSync(path.join(configDir, 'configPage.html'), 'utf8');
    const js = fs.readFileSync(path.join(configDir, 'configPage.js'), 'utf8');
    assert.match(html, /<section class="tuneInSectionBox tuneInLibraryRefresh" aria-labelledby="tuneInLibraryRefreshTitle">/);
    assert.match(html, /<h3 class="libraryRefreshTitle" id="tuneInLibraryRefreshTitle">TuneIn Library Refresh<\/h3>/);
    assert.match(html, /<details class="tuneInSection tuneInNeutralPanel tuneInRefresh">/);
    assert.doesNotMatch(html, /<summary class="libraryRefresh/);
    assert.doesNotMatch(js, /libraryRefreshSection|libraryRefreshDefaultPending|libraryRefreshSummary/);
});

test('yellow first-refresh prompt appears only until refresh history exists', async () => {
    const app = harness(true);
    const visit = app.instance.onResume();
    app.refresh[0].resolve({ State: 'NeverRun', HasRefreshBeenStarted: false, SettingsLocked: false });
    app.reads[0].resolve({ SchemaVersion: 1, ConfigurationCompleted: true });
    await visit;
    await settle();

    const prompt = app.elements['.catalogRefreshPrompt'];
    assert.equal(prompt.hidden, false);
    assert.equal(prompt.textContent,
        "Please go to the Catalog Refresh section to refresh your library's TuneIn catalog");

    const html = fs.readFileSync(path.join(configDir, 'configPage.html'), 'utf8');
    assert.match(html, /\.catalogRefreshPrompt\s*\{[^}]*background:\s*#fff2a8/i);

    const timer = [...app.timers.values()].find(item => item.delay === 10000);
    assert.ok(timer);
    timer.handler();
    app.refresh[1].resolve({ State: 'NeverRun', HasRefreshBeenStarted: true, SettingsLocked: false });
    await settle();
    assert.equal(prompt.hidden, true);
    assert.equal(prompt.textContent, '');
});

test('only explicit selector actions fetch branches and save multiple local selections', async () => {
    const app = harness();
    const visit = app.instance.onResume();
    app.reads[0].resolve({});
    await visit;
    enableBrowsing(app);
    const section = app.elements['.selectors'].childNodes.find(node =>
        node.childNodes[0].textContent === 'Local Cities and Regions');
    assert.equal(section.tagName, 'details');
    const mode = section.childNodes[1];
    assert.equal(mode.value, 'selected');
    mode.value = 'selected';
    mode.handlers.change();
    const root = section.descendants().find(node => node.tagName === 'details');
    const loading = open(root);
    assert.equal(open(root), loading);
    assert.equal(app.selectors.length, 1);
    assert.equal(app.selectors[0].options.url.route, 'TuneIn/Selectors');
    assert.equal(app.selectors[0].options.url.params.Category, 'local');
    app.selectors[0].resolve({ Items: [
        { Id: 'r1', Name: 'First', Path: 'First', AncestorIds: ['r0'], CanExpand: true, CanSelect: true },
        { Id: 'r2', Name: 'Second', Path: 'Second', AncestorIds: ['r0'], CanExpand: true, CanSelect: true }
    ] });
    await loading;
    const buttons = section.descendants().filter(node => node.textContent === 'Add');
    assert.ok(buttons.every(button => /\bbutton-submit\b/.test(button.className)));
    buttons.forEach(button => button.handlers.click());
    const controls = section.descendants().filter(node => node.tagName === 'input' && node.type === 'checkbox');
    assert.equal(controls.length, 2);
    controls[1].checked = false;
    controls[1].handlers.change();
    app.elements['.configurationCompleted'].checked = true;
    app.instance.view.querySelector('[data-category="EnableLocalRadio"]').checked = true;
    app.elements['.localSource'].value = 'regions';
    app.formHandlers.submit({ preventDefault() {} });
    await settle();
    assert.equal(app.writes[0].config.ConfigurationCompleted, true);
    assert.equal(app.writes[0].config.EnableLocalRadio, true);
    assert.deepEqual(Array.from(app.writes[0].config.LocalCities, item => item.Id), ['r1', 'r2']);
    assert.equal(app.writes[0].config.LocalCities[1].Enabled, true);
    assert.equal(app.selectors.length, 1);
});

test('browser selectors use All or Selected only and save selected branches', async () => {
    const app = harness();
    const visit = app.instance.onResume();
    app.reads[0].resolve({});
    await visit;
    enableBrowsing(app);
    for (const [index, label, modeName, listName, id] of [
        [0, 'By Language', 'LanguageMode', 'Languages', 'l1'],
        [1, 'By Location', 'LocationMode', 'Locations', 'r1'],
        [2, 'Music Genres', 'MusicMode', 'Music', 'g1'],
        [6, 'Local Cities and Regions', 'LocalMode', 'LocalCities', 'r2']
    ]) {
        const section = app.elements['.selectors'].childNodes[index];
        const [heading, mode, columns] = section.childNodes;
        assert.equal(heading.textContent, label);
        assert.equal(mode.value, index === 6 ? 'selected' : 'all');
        const [browseColumn, savedColumn] = columns.childNodes;
        assert.equal(browseColumn.hidden, index !== 6);
        mode.value = 'selected';
        mode.handlers.change();
        const root = browseColumn.childNodes.find(node => node.tagName === 'details');
        const pending = open(root);
        const request = app.selectors.at(-1);
        assert.equal(request.options.url.params.Category,
            index === 0 ? 'language' : index === 1 ? 'location' : index === 2 ? 'music' : 'local');
        request.resolve({ Items: [{ Id: id, Name: label + ' choice', Path: label + ' choice',
            AncestorIds: [], CanSelect: true }] });
        await pending;
        root.descendants().find(node => node.textContent === 'Add').handlers.click();
        assert.match(savedColumn.childNodes.find(node => node.className === 'tuneInSelectorSaved').textContent, /choice/);
        assert.equal(app.instance.config[modeName], 'selected');
        assert.equal(app.instance.config[listName][0].Id, id);
    }
    app.formHandlers.submit({ preventDefault() {} });
    await settle();
    for (const key of ['LanguageMode', 'LocationMode', 'MusicMode', 'LocalMode'])
        assert.equal(app.writes[0].config[key], 'selected');
});

test('Local Radio rejects empty saved regions even with a persisted All mode', async () => {
    const app = harness();
    const visit = app.instance.onResume();
    app.reads[0].resolve({ LocalMode: 'all' });
    await visit;
    enableBrowsing(app);
    app.instance.view.querySelector('[data-category="EnableLocalRadio"]').checked = true;
    app.elements['.localSource'].value = 'regions';
    app.formHandlers.submit({ preventDefault() {} });
    await settle();
    assert.equal(app.writes.length, 0);
    assert.match(app.elements['.status'].textContent, /at least one enabled saved city or region/);
    assert.equal(app.elements['.status'].attributes['data-error'], 'true');
    const section = app.elements['.selectors'].childNodes[6];
    assert.deepEqual(section.childNodes[1].childNodes.map(option => option.value), ['selected']);
    app.instance.config.LocalCities = [{ Id: 'r1', Enabled: false }];
    app.formHandlers.submit({ preventDefault() {} });
    await settle();
    assert.equal(app.writes.length, 0);
    app.instance.config.LocalCities[0].Enabled = true;
    app.formHandlers.submit({ preventDefault() {} });
    await settle();
    assert.equal(app.writes.length, 1);
    assert.equal(app.writes[0].config.LocalRadioSource, 'regions');
    assert.equal(app.writes[0].config.LocalMode, 'selected');
});

for (const source of ['cities', ' Cities ', '\tCITIES\r\n', 'REGIONS', ' regions ', ' COORDINATES '])
test(`Local Radio source ${JSON.stringify(source)} loads and saves without rewriting its identity`, async () => {
    const app = harness();
    const visit = app.instance.onResume();
    const coordinates = source.trim().toLowerCase() === 'coordinates';
    app.reads[0].resolve({ LocalRadioSource: source, UseCoordinates: coordinates, LatLon: '10,20',
        LocalCities: [{ Id: 'r1', Enabled: true }], Other: 42 });
    await visit;
    assert.equal(app.elements['.localSource'].value, coordinates ? 'coordinates' : 'regions');
    assert.equal(app.instance.config.LocalRadioSource, source);
    assert.equal(app.writes.length, 0);
    enableBrowsing(app);
    app.instance.view.querySelector('[data-category="EnableLocalRadio"]').checked = true;
    app.formHandlers.submit({ preventDefault() {} });
    await settle();
    assert.equal(app.writes[0].config.LocalRadioSource, source);
    assert.equal(app.writes[0].config.UseCoordinates, coordinates);
    assert.equal(app.writes[0].config.Other, 42);
});

for (const source of ['   ', 'unknown'])
test(`unrecognized Local Radio source ${JSON.stringify(source)} blocks saving instead of inferring a source`, async () => {
    const app = harness();
    const visit = app.instance.onResume();
    app.reads[0].resolve({ LocalRadioSource: source, EnableLocalRadio: true, UseCoordinates: true,
        LatLon: '10,20', LocalCities: [{ Id: 'r1', Enabled: true }] });
    await visit;
    enableBrowsing(app);
    app.formHandlers.submit({ preventDefault() {} });
    await settle();
    assert.equal(app.writes.length, 0);
    assert.match(app.elements['.status'].textContent, /Choose coordinates or regions/);
    assert.equal(app.elements['.status'].attributes['data-error'], 'true');
    app.elements['.localSource'].value = 'regions';
    app.formHandlers.submit({ preventDefault() {} });
    await settle();
    assert.equal(app.writes[0].config.LocalRadioSource, 'regions');
    assert.equal(app.writes[0].config.UseCoordinates, false);
});

test('first-run checklist follows setup and saving without starting a refresh', async () => {
    const app = harness();
    const visit = app.instance.onResume();
    app.reads[0].resolve({ SchemaVersion: 1, ConfigurationCompleted: false, SettingsSaved: false });
    await visit;
    assert.equal(app.elements['.tuneInReadiness'].hidden, false);
    assert.equal(app.elements['.readinessFiltering'].attributes['data-ready'], 'false');
    assert.equal(app.elements['.readinessFilteringCheck'].checked, false);
    assert.equal(app.elements['.readinessCategory'].attributes['data-ready'], 'false');
    assert.equal(app.elements['.readinessSaved'].attributes['data-ready'], 'false');
    enableBrowsing(app);
    const talk = app.instance.view.querySelector('[data-category="EnableTalk"]');
    talk.checked = true;
    talk.handlers.change();
    assert.equal(app.elements['.readinessCategory'].attributes['data-ready'], 'true');
    assert.equal(app.elements['.readinessRequirements'].attributes['data-ready'], 'true');
    assert.equal(app.elements['.readinessCategoryCheck'].checked, true);
    assert.equal(app.elements['.readinessRequirementsCheck'].checked, true);
    app.formHandlers.submit({ preventDefault() {} });
    await settle();
    assert.equal(app.elements['.readinessSaved'].attributes['data-ready'], 'true');
    assert.equal(app.elements['.readinessSavedCheck'].checked, true);
    assert.equal(app.elements['.tuneInReadiness'].hidden, false);
    assert.ok(app.refresh.every(request => request.options.type === 'GET'));
    app.formHandlers.input();
    assert.equal(app.elements['.readinessSaved'].attributes['data-ready'], 'false');
});

test('eligible artwork preference saves a choice and permits re-enabling', async () => {
    const app = harness(true);
    const visit = app.instance.onResume();
    app.reads[0].resolve({ SchemaVersion: 1, ConfigurationCompleted: true });
    app.refresh[0].resolve({ State: 'Completed', SettingsLocked: false, OptionalChecksEligible: true,
        SkipOptionalStartupChecks: null });
    await visit;
    await settle();
    const toggle = app.elements['.skipOptionalChecks'];
    assert.equal(toggle.disabled, false);
    toggle.checked = true;
    const save = toggle.handlers.change.call(toggle, { stopPropagation() {} });
    assert.equal(JSON.parse(app.optionalChecks[0].options.data).Skip, true);
    app.optionalChecks[0].resolve({ SkipOptionalStartupChecks: true });
    await save;
    assert.equal(toggle.checked, true);
    assert.match(app.elements['.optionalChecksMessage'].textContent, /Automatic artwork checks will be skipped/);
    app.refresh[1].resolve({ State: 'Completed', SettingsLocked: false, OptionalChecksEligible: true,
        SkipOptionalStartupChecks: true });
    await settle();
    let inputStopped = false;
    toggle.handlers.input({ stopPropagation() { inputStopped = true; } });
    assert.equal(inputStopped, true);
    assert.equal(app.instance.setupDirty, false);
    toggle.checked = false;
    const reenable = toggle.handlers.change.call(toggle, { stopPropagation() {} });
    assert.equal(JSON.parse(app.optionalChecks[1].options.data).Skip, false);
    app.optionalChecks[1].resolve({ SkipOptionalStartupChecks: false });
    await reenable;
    app.refresh[2].resolve({ State: 'Completed', SettingsLocked: false, OptionalChecksEligible: true,
        SkipOptionalStartupChecks: false });
    await settle();
    assert.equal(toggle.checked, false);
});

test('artwork preference changes do not retain a catalog notice from an earlier settings save', async () => {
    const app = harness(true);
    const visit = app.instance.onResume();
    const status = { State: 'Completed', SettingsLocked: false, OptionalChecksEligible: true,
        HasCompleteBaseline: true, ChannelRegistered: true, SkipOptionalStartupChecks: false };
    app.reads[0].resolve({ SchemaVersion: 1, ConfigurationCompleted: true, SettingsSaved: true, EnableTalk: true });
    app.refresh[0].resolve(status);
    await visit;
    await settle();
    app.formHandlers.submit({ preventDefault() {} });
    await settle();
    app.refresh[1].resolve({ ...status, SettingsLocked: true });
    await settle();
    assert.deepEqual(app.notices, ['saved']);
    assert.equal(app.elements['.status'].textContent, '');

    const lock = app.elements['.settingsLocked'];
    lock.checked = false;
    const unlock = lock.handlers.change();
    app.locks[0].resolve({ Locked: false });
    await unlock;
    app.refresh[2].resolve(status);
    await settle();
    const toggle = app.elements['.skipOptionalChecks'];
    toggle.checked = true;
    const save = toggle.handlers.change.call(toggle, { stopPropagation() {} });
    app.optionalChecks[0].resolve({ SkipOptionalStartupChecks: true });
    await save;
    app.refresh[3].resolve({ ...status, SkipOptionalStartupChecks: true });
    await settle();
    assert.equal(app.elements['.status'].textContent, '');
    assert.equal(app.elements['.catalogRefreshPrompt'].hidden, true);
    assert.equal(app.elements['.settingsChangeNotice'].hidden, true);
    assert.equal(app.elements['.tuneInReadiness'].hidden, true);
    assert.equal(app.instance.setupDirty, false);
    assert.match(app.elements['.optionalChecksMessage'].textContent, /Automatic artwork checks will be skipped/);
    assert.equal(app.writes.length, 1);
    assert.ok(app.refresh.every(request => request.options.type === 'GET'));
    app.instance.onPause();
});

test('a failed optional preference write is not reported as saved', async () => {
    const app = harness(true);
    const visit = app.instance.onResume();
    app.reads[0].resolve({ SchemaVersion: 1, ConfigurationCompleted: true });
    app.refresh[0].resolve({ State: 'Completed', SettingsLocked: false, OptionalChecksEligible: true,
        SkipOptionalStartupChecks: null });
    await visit;
    await settle();
    const toggle = app.elements['.skipOptionalChecks'];
    toggle.checked = false;
    const save = toggle.handlers.change.call(toggle, { stopPropagation() {} });
    app.optionalChecks[0].reject(new Error('offline'));
    await save;
    assert.match(app.elements['.optionalChecksMessage'].textContent, /Could not confirm/);
    app.refresh[1].resolve({ State: 'Completed', SettingsLocked: false, OptionalChecksEligible: true,
        SkipOptionalStartupChecks: null });
    await settle();
    assert.equal(toggle.disabled, false);
});

test('skip-enabled reopening requires manual unlock for settings and optional preferences', async () => {
    const app = harness(true);
    const visit = app.instance.onResume();
    app.reads[0].resolve({ SettingsSaved: true, SchemaVersion: 1, ConfigurationCompleted: true });
    const locked = { State: 'Completed', SettingsLocked: true, OptionalChecksEligible: true,
        SkipOptionalStartupChecks: true, ArtworkChecksDeferred: true };
    app.refresh[0].resolve(locked);
    await visit;
    const toggle = app.elements['.skipOptionalChecks'];
    assert.equal(app.elements['.settingsLocked'].checked, true);
    assert.equal(app.elements['.globalFields'].disabled, true);
    assert.equal(toggle.disabled, true);
    toggle.checked = false;
    toggle.handlers.change.call(toggle, { stopPropagation() {} });
    assert.equal(app.optionalChecks.length, 0);
    const lock = app.elements['.settingsLocked'];
    lock.checked = false;
    const unlock = lock.handlers.change();
    app.locks[0].resolve({ Locked: false });
    await unlock;
    app.refresh[1].resolve({ ...locked, SettingsLocked: false });
    await settle();
    assert.equal(toggle.disabled, false);
    toggle.checked = false;
    const save = toggle.handlers.change.call(toggle, { stopPropagation() {} });
    assert.equal(app.elements['.globalFields'].disabled, true);
    assert.equal(lock.disabled, true);
    app.optionalChecks[0].resolve({ SkipOptionalStartupChecks: false });
    await save;
    app.refresh[2].resolve({ ...locked, SettingsLocked: true, SkipOptionalStartupChecks: false });
    await settle();
    assert.equal(lock.checked, true);
    assert.equal(app.elements['.globalFields'].disabled, true);
    app.instance.onPause();
    const reopen = app.instance.onResume();
    app.reads[1].resolve({ SettingsSaved: true, SchemaVersion: 1, ConfigurationCompleted: true });
    app.refresh[3].resolve({ ...locked, SkipOptionalStartupChecks: false });
    await reopen;
    await settle();
    assert.equal(lock.checked, true);
    assert.equal(app.elements['.globalFields'].disabled, true);
});

test('opening saved settings waits for server lock and ignores older unlocked status', async () => {
    const app = harness(true, false, false, true);
    const visit = app.instance.onResume();
    app.reads[0].resolve({ SettingsSaved: true, SchemaVersion: 1, ConfigurationCompleted: true });
    await settle();
    assert.equal(app.opens.length, 1);
    assert.equal(app.elements['.settingsLocked'].disabled, true);
    app.opens[0].resolve({ Locked: true });
    await settle();
    app.refresh[0].resolve({ SettingsLocked: false });
    await visit;
    await settle();
    assert.equal(app.elements['.settingsLocked'].checked, true);
    assert.equal(app.elements['.globalFields'].disabled, true);
    const toggle = app.elements['.settingsLocked'];
    toggle.checked = false;
    const unlocking = toggle.handlers.change();
    app.locks[0].resolve({ Locked: false });
    await unlocking;
    app.refresh[1].resolve({ SettingsLocked: false });
    await settle();
    assert.equal(app.elements['.globalFields'].disabled, false);
    app.instance.onPause();
    const reopen = app.instance.onResume();
    app.reads[1].resolve({ SettingsSaved: true, SchemaVersion: 1 });
    await settle();
    app.opens[1].resolve({ Locked: true });
    app.refresh[2].resolve({ SettingsLocked: false });
    await reopen;
    await settle();
    assert.equal(toggle.checked, true);
    assert.equal(app.elements['.globalFields'].disabled, true);
});

test('first setup stays editable and an open-lock failure fails closed', async () => {
    const app = harness(true, false, false, true);
    const visit = app.instance.onResume();
    app.reads[0].resolve({ SettingsSaved: false, SchemaVersion: 0 });
    app.refresh[0].resolve({ SettingsLocked: false });
    await settle();
    app.opens[0].resolve({ Locked: false });
    await visit;
    await settle();
    assert.equal(app.elements['.globalFields'].disabled, false);
    app.instance.onPause();
    const reopen = app.instance.onResume();
    app.reads[1].resolve({ SettingsSaved: true, SchemaVersion: 1 });
    app.refresh[1].resolve({ SettingsLocked: false });
    await settle();
    app.opens[1].reject(new Error('offline'));
    await reopen;
    await settle();
    assert.equal(app.elements['.settingsLocked'].disabled, true);
    assert.equal(app.elements['.globalFields'].disabled, true);
    assert.match(app.elements['.settingsLockStatus'].textContent, /Reopen this page to retry/);
});

test('reopening waits for an earlier unlock request before locking', async () => {
    const app = harness(true, false, false, true);
    const visit = app.instance.onResume();
    app.reads[0].resolve({ SettingsSaved: true, SchemaVersion: 1 });
    app.refresh[0].resolve({ SettingsLocked: true });
    await settle();
    app.opens[0].resolve({ Locked: true });
    await visit;
    await settle();
    const toggle = app.elements['.settingsLocked'];
    toggle.checked = false;
    const unlocking = toggle.handlers.change();
    app.instance.onPause();
    const reopen = app.instance.onResume();
    app.reads[1].resolve({ SettingsSaved: true, SchemaVersion: 1 });
    await settle();
    assert.equal(app.opens.length, 1);
    assert.equal(app.elements['.globalFields'].disabled, true);
    app.locks[0].resolve({ Locked: false });
    await unlocking;
    await settle();
    assert.equal(app.opens.length, 2);
    app.opens[1].resolve({ Locked: true });
    app.refresh.at(-1).resolve({ SettingsLocked: false });
    await reopen;
    await settle();
    assert.equal(toggle.checked, true);
    assert.equal(app.elements['.globalFields'].disabled, true);
});

test('settings lock and accessible help stay in the pinned header, with version beside Global Settings', () => {
    const html = fs.readFileSync(path.join(configDir, 'configPage.html'), 'utf8');
    const header = html.match(/<div class="tuneInPinned"[\s\S]*?(?=<form>)/)[0];
    const controls = header.match(/<div class="tuneInLockControls">([\s\S]*?)<p id="tuneInLockStatus"/)[1];
    assert.match(controls, /<p class="status settingsChangeNotice" role="status" aria-live="polite" hidden>[\s\S]*?<\/p>\s*<div class="tuneInLockActions">\s*<label class="tuneInLockToggle">/);
    assert.equal((header.match(/class="status settingsChangeNotice"/g) || []).length, 1);
    assert.match(header, /<p class="status settingsMessage" role="status" aria-live="polite"><\/p>/);
    assert.match(html, /\.tuneInPinned > \.tuneInLockControls\s*\{[^}]*flex-wrap:\s*wrap/);
    assert.match(html, /\.tuneInLockControls > \.settingsChangeNotice\s*\{[^}]*min-inline-size:\s*0/);
    assert.match(controls, /class="settingsLocked"/);
    assert.match(controls, /aria-labelledby="tuneInLockCaption" aria-describedby="tuneInLockStatus"/);
    assert.match(controls, /<summary aria-label="About the settings lock">\?<\/summary>/);
    assert.match(controls, /<div class="tuneInArtworkTooltip">Manually unlock to edit saved settings/);
    assert.match(header, /id="tuneInLockStatus" class="settingsLockStatus" role="status"/);
    assert.doesNotMatch(header, /class="tuneInPluginVersion"/);
    const globalPanel = html.match(/<div class="tuneInSection tuneInSectionBox tuneInGlobalPanel">([\s\S]*?)(?=<summary class="browsingSummary")/)[1];
    assert.match(globalPanel, /<p class="tuneInPluginVersion" hidden><\/p>/);
    assert.doesNotMatch(globalPanel, /class="settingsLocked"/);
    assert.equal((html.match(/class="settingsLocked"/g) || []).length, 1);
    assert.equal((html.match(/Manually unlock to edit saved settings/g) || []).length, 1);
});

test('header never creates an internal scroller when help or zoom exceeds its bounds', () => {
    const html = fs.readFileSync(path.join(configDir, 'configPage.html'), 'utf8');
    const header = html.match(/\.tuneInPinned \{([^}]+)\}/)[1];
    assert.match(header, /position: relative/);
    assert.match(header, /overflow: visible/);
    assert.doesNotMatch(header, /max-block-size|overflow-[xy]|overscroll-behavior/);
    assert.match(html, /\.tuneInPinned\[data-pinned="true"\] \{\s*position: sticky/);
    assert.match(html, /scroll-padding-block-start: calc\(var\(--tunein-header-height, 0px\) \+ \.25rem\)/);
});

test('header unpins long notices and short viewports, then resumes without stale layout observers', () => {
    const app = harness();
    app.instance.onResume();
    const observer = app.resizeObservers[0];
    assert.equal(observer.element, app.header);
    assert.equal(app.header.attributes['data-pinned'], 'true');
    assert.equal(app.instance.view.style['--tunein-header-height'], '200px');

    app.header.height = 271;
    observer.callback();
    assert.equal(app.header.attributes['data-pinned'], 'false');
    assert.equal(app.instance.view.style['--tunein-header-height'], '0px');
    app.header.height = 270;
    observer.callback();
    assert.equal(app.header.attributes['data-pinned'], 'true');

    app.viewport.clientHeight = 300;
    app.windowHandlers.get('resize')();
    assert.equal(app.header.attributes['data-pinned'], 'false');
    app.instance.onPause();
    assert.equal(observer.disconnected, true);
    assert.equal(app.windowHandlers.has('resize'), false);

    app.viewport.clientHeight = 800;
    app.instance.onResume();
    const replacement = app.resizeObservers[1];
    assert.equal(app.header.attributes['data-pinned'], 'true');
    app.header.height = 400;
    observer.callback();
    assert.equal(app.header.attributes['data-pinned'], 'true');
    replacement.callback();
    assert.equal(app.header.attributes['data-pinned'], 'false');
    app.instance.onPause();
});

test('artwork preference respects the settings lock and unknown lock status', async () => {
    const app = harness(true);
    const visit = app.instance.onResume();
    app.reads[0].resolve({ SettingsSaved: true, SchemaVersion: 1, ConfigurationCompleted: true });
    app.refresh[0].resolve({ SettingsLocked: true, OptionalChecksEligible: true, SkipOptionalStartupChecks: null });
    await visit;
    assert.equal(app.elements['.optionalChecksUnlockHint'].hidden, false);
    assert.equal(app.elements['.skipOptionalChecks'].disabled, true);
    app.elements['.skipOptionalChecks'].checked = true;
    app.elements['.skipOptionalChecks'].handlers.change.call(app.elements['.skipOptionalChecks'], { stopPropagation() {} });
    assert.equal(app.optionalChecks.length, 0);
    [...app.timers.values()][0].handler();
    app.refresh[1].resolve({ SettingsLocked: null, OptionalChecksEligible: true });
    await settle();
    assert.equal(app.elements['.settingsLockState'].textContent, 'Checking');
    assert.equal(app.elements['.settingsLocked'].disabled, true);
    assert.equal(app.elements['.skipOptionalChecks'].disabled, true);
});

test('manual artwork checks cannot replace newer live lock or maintenance state', async () => {
    const app = harness(true);
    const visit = app.instance.onResume();
    app.reads[0].resolve({ SchemaVersion: 1, ConfigurationCompleted: true });
    app.refresh[0].resolve({ State: 'Completed', SettingsLocked: false, SkipOptionalStartupChecks: true });
    await visit;
    const checking = app.elements['.optionalChecksRun'].click();
    [...app.timers.values()][0].handler();
    app.refresh[2].resolve({ State: 'Completed', SettingsLocked: true, MaintenanceBusy: true,
        SkipOptionalStartupChecks: true });
    await settle();
    app.refresh[1].resolve({ State: 'Completed', SettingsLocked: false, MaintenanceBusy: false,
        ArtworkPacks: [{ Scope: 'genres', Status: 'missing', InstalledCount: 0 }] });
    await checking;
    assert.equal(app.elements['.settingsLocked'].checked, true);
    assert.equal(app.elements['.settingsLocked'].disabled, true);
    assert.equal(app.elements['.globalFields'].disabled, true);
    assert.equal(app.elements['.skipOptionalChecks'].disabled, true);
    [...app.timers.values()][0].handler();
    app.refresh[3].resolve({ State: 'Completed', SettingsLocked: true, MaintenanceBusy: false,
        SkipOptionalStartupChecks: true });
    await settle();
    assert.equal(app.elements['.settingsLocked'].disabled, false);
    assert.equal(app.elements['.settingsLocked'].checked, true);
    assert.equal(app.elements['.globalFields'].disabled, true);
});

test('playback security warning appears only when pinning is unavailable', async () => {
    const app = harness(true);
    const visit = app.instance.onResume();
    app.reads[0].resolve({});
    app.refresh[0].resolve({ SettingsLocked: true, PlaybackConnectionPinningAvailable: false });
    await visit;
    const warning = app.elements['.playbackSecurityWarning'];
    assert.equal(warning.hidden, false);
    assert.match(warning.textContent, /blocked playback.*connection pinning.*Update Emby Server/);
    assert.equal(warning.attributes['data-error'], 'true');
    [...app.timers.values()][0].handler();
    app.refresh[1].resolve({ SettingsLocked: true, PlaybackConnectionPinningAvailable: true });
    await settle();
    assert.equal(warning.hidden, true);
    assert.equal(warning.textContent, '');
});

test('null and failed status reads discard a previously unlocked state until reconfirmed', async () => {
    const app = harness(true);
    const visit = app.instance.onResume();
    app.reads[0].resolve({});
    app.refresh[0].resolve({ SettingsLocked: false });
    await visit;
    assert.equal(app.elements['.globalFields'].disabled, false);
    [...app.timers.values()][0].handler();
    app.refresh[1].resolve({ SettingsLocked: null });
    await settle();
    assert.equal(app.elements['.settingsLockState'].textContent, 'Checking');
    assert.equal(app.elements['.globalFields'].disabled, true);
    [...app.timers.values()][0].handler();
    app.refresh[2].reject(new Error('offline'));
    await settle();
    assert.equal(app.elements['.settingsLocked'].disabled, true);
    assert.equal(app.elements['.globalFields'].disabled, true);
    assert.equal(app.elements['.refreshStatus'].attributes['data-error'], 'true');
    assert.equal(app.elements['.settingsLockStatus'].attributes['data-error'], 'true');
    [...app.timers.values()][0].handler();
    app.refresh[3].resolve({ SettingsLocked: true });
    await settle();
    assert.equal(app.elements['.settingsLocked'].checked, true);
    assert.equal(app.elements['.globalFields'].disabled, true);
    assert.equal(app.elements['.refreshStatus'].attributes['data-error'], 'false');
    assert.equal(app.elements['.settingsLockStatus'].attributes['data-error'], 'false');
});

test('optional checks are deferred until explicitly requested, and errors are reported', async () => {
    const app = harness(true);
    const visit = app.instance.onResume();
    app.reads[0].resolve({ SchemaVersion: 1, ConfigurationCompleted: true });
    app.refresh[0].resolve({ State: 'Completed', SettingsLocked: true, OptionalChecksEligible: true,
        SkipOptionalStartupChecks: true, ArtworkChecksDeferred: true, ArtworkPacks: [] });
    await visit;
    await settle();
    assert.equal(app.elements['.skipOptionalChecks'].checked, true);
    assert.equal(app.elements['.skipOptionalChecks'].disabled, true);
    assert.equal(app.elements['.globalFields'].disabled, true);
    assert.equal(app.elements['.advancedFields'].disabled, true);
    assert.equal(app.elements['.artworkUpload'].disabled, true);
    assert.match(app.elements['.optionalChecksStatus'].textContent, /deferred/);
    const checking = app.elements['.optionalChecksRun'].click();
    assert.equal(app.refresh[1].options.url.params.CheckOptional, true);
    app.refresh[1].resolve({ State: 'Completed', SettingsLocked: false, OptionalChecksEligible: true,
        SkipOptionalStartupChecks: true, ArtworkChecksDeferred: false,
        ArtworkPacks: [{ Scope: 'genres', Status: 'missing', InstalledCount: 0,
            CheckedUtc: '2026-01-01T00:00:00Z' }] });
    await checking;
    assert.match(app.elements['.optionalChecksStatus'].textContent, /Last checked:/);
    assert.equal(app.elements['.artworkGenresName'].textContent, 'Default artwork');
    assert.equal(app.instance.settingsLocked, true);
    assert.equal(app.elements['.globalFields'].disabled, true);
    assert.equal(app.elements['.advancedFields'].disabled, true);
    assert.equal(app.locks.length, 0);
    assert.equal(app.optionalChecks.length, 0);
    const retry = app.elements['.optionalChecksRun'].click();
    app.refresh[2].reject(new Error('offline'));
    await retry;
    assert.match(app.elements['.optionalChecksStatus'].textContent, /failed/);
    assert.equal(app.elements['.optionalChecksStatus'].attributes['data-error'], 'true');
    assert.equal(app.elements['.artworkGenresName'].textContent, 'Status unavailable');
    const retryAgain = app.elements['.optionalChecksRun'].click();
    assert.equal(app.elements['.optionalChecksStatus'].attributes['data-error'], 'false');
    app.refresh[3].resolve({ State: 'Completed', SettingsLocked: false, ArtworkPacks: [] });
    await retryAgain;
    assert.equal(app.elements['.optionalChecksStatus'].attributes['data-error'], 'false');
});

test('enabled Selected only categories require valid saved choices with inline guidance', async () => {
    for (const [category, mode, list, selector, valid] of [
        ['EnableLanguage', 'LanguageMode', 'Languages', 'language', { Id: 'l1', Enabled: true }],
        ['EnableLocation', 'LocationMode', 'Locations', 'location', { Id: 'r1', Enabled: true }],
        ['EnableMusic', 'MusicMode', 'Music', 'music', { Id: 'g1', Enabled: true }],
        ['EnableTalk', 'TalkMode', 'TalkIds', 'talk', 'c1'],
        ['EnableSports', 'SportsMode', 'SportsIds', 'sports', 'c1'],
        ['EnablePodcasts', 'PodcastsMode', 'PodcastIds', 'podcast', 'c1'],
        ['EnableLocalRadio', 'LocalMode', 'LocalCities', 'local', { Id: 'r1', Enabled: true }]
    ]) {
        const app = harness();
        const visit = app.instance.onResume();
        app.reads[0].resolve({ SchemaVersion: 1, ConfigurationCompleted: true, [category]: true,
            [mode]: 'selected', [list]: [], LocalRadioSource: 'regions' });
        await visit;
        const guidance = app.instance.selectorErrors.get(selector);
        assert.equal(guidance.hidden, false, category);
        assert.match(guidance.textContent, selector === 'local'
            ? /at least one enabled saved city or region/
            : /Add a valid .* choice or choose All/, category);
        app.formHandlers.submit({ preventDefault() {} });
        assert.equal(app.writes.length, 0, category);
        app.instance.config[list] = [valid];
        app.formHandlers.change();
        assert.equal(guidance.hidden, true, category);
        app.formHandlers.submit({ preventDefault() {} });
        await settle();
        assert.equal(app.writes.length, 1, category);
    }
});

test('Local Radio and Favorites show actionable source and username validation', async () => {
    const app = harness();
    const visit = app.instance.onResume();
    app.reads[0].resolve({ SchemaVersion: 1, ConfigurationCompleted: true,
        EnableLocalRadio: true, EnableFavorites: true });
    await visit;
    assert.match(app.elements['.localValidation'].textContent, /Choose coordinates or regions/);
    assert.match(app.elements['.favoritesValidation'].textContent, /username/);
    app.elements['.localSource'].value = 'coordinates';
    app.latLon.value = '91,0';
    app.formHandlers.input();
    assert.match(app.elements['.localValidation'].textContent, /latitude/);
    app.latLon.value = '45,-70';
    app.username.value = 'listener';
    app.formHandlers.input();
    assert.equal(app.elements['.localValidation'].hidden, true);
    assert.equal(app.elements['.favoritesValidation'].hidden, true);
});

test('first baseline gates individual refresh and distinguishes an unregistered channel', async () => {
    for (const [baseline, registered, multiple] of [[false, true, true], [true, true, true],
        [false, false, true], [false, true, false]]) {
        const app = harness(true);
        const visit = app.instance.onResume();
        app.elements['.refreshSummary'].handlers.click({ preventDefault() {} });
        app.reads[0].resolve({ SchemaVersion: 1, ConfigurationCompleted: true, SettingsSaved: true,
            EnableTalk: true, EnableSports: true });
        app.refresh[0].resolve({ State: 'NeverRun', Busy: false, SettingsLocked: false,
            HasCompleteBaseline: baseline, ChannelRegistered: registered,
            Categories: multiple ? [{ Category: 'talk', Name: 'Talk' }, { Category: 'sports', Name: 'Sports' }]
                : [{ Category: 'talk', Name: 'Talk' }] });
        await visit;
        await settle();
        assert.equal(app.elements['.tuneInReadiness'].hidden, baseline);
        assert.equal(app.instance.refreshCategoryRows.get('talk').control.disabled, (!baseline && multiple) || !registered);
        assert.equal(app.elements['.refreshStart'].disabled, !registered);
        if (!registered) {
            assert.match(app.elements['.readinessChannel'].textContent, /not registered yet/);
            assert.match(app.elements['.refreshCategoryNote'].textContent, /not registered yet/);
        } else if (!baseline && multiple) {
            assert.match(app.elements['.refreshCategoryNote'].textContent, /Refresh all enabled categories/);
        }
        assert.ok(app.refresh.every(request => request.options.type === 'GET'));
    }
});

test('completed setup stays hidden on unlock and shows an edit notice only after a settings change', async () => {
    const app = harness(true);
    const noticeText = app.elements['.settingsChangeNotice'].textContent;
    assert.match(noticeText, /^Unsaved settings changes\./);
    const visit = app.instance.onResume();
    app.reads[0].resolve({ SchemaVersion: 1, ConfigurationCompleted: true, SettingsSaved: true, EnableTalk: true });
    const status = { State: 'Completed', SettingsLocked: true,
        HasCompleteBaseline: true, ChannelRegistered: true };
    app.refresh[0].resolve(status);
    await visit;
    await settle();
    assert.equal(app.elements['.tuneInReadiness'].hidden, true);
    assert.equal(app.elements['.settingsChangeNotice'].hidden, true);

    const lock = app.elements['.settingsLocked'];
    lock.checked = false;
    const unlock = lock.handlers.change();
    // Native checkbox input and change events both bubble to the form.
    app.formHandlers.input({ target: lock });
    app.formHandlers.change({ target: lock });
    app.locks[0].resolve({ Locked: false });
    await unlock;
    app.refresh[1].resolve({ ...status, SettingsLocked: false });
    await settle();
    assert.equal(app.instance.setupDirty, false);
    assert.equal(app.elements['.tuneInReadiness'].hidden, true);
    assert.equal(app.elements['.settingsChangeNotice'].hidden, true);

    app.requestInterval.value = '4';
    app.formHandlers.input({ target: app.requestInterval });
    assert.equal(app.elements['.readinessSavedCheck'].checked, false);
    assert.equal(app.elements['.tuneInReadiness'].hidden, true);
    assert.equal(app.elements['.settingsChangeNotice'].hidden, false);
    assert.equal(app.elements['.settingsChangeNotice'].textContent, noticeText);
    app.formHandlers.submit({ preventDefault() {} });
    await settle();
    assert.equal(app.writes[0].config.ApiRequestIntervalSeconds, 4);
    assert.equal(app.elements['.settingsChangeNotice'].hidden, true);
    assert.equal(app.elements['.settingsChangeNotice'].textContent, noticeText);
    assert.equal(app.elements['.tuneInReadiness'].hidden, true);
    assert.ok(app.refresh.every(request => request.options.type === 'GET'));
    app.instance.onPause();
});

test('setup checklist tracks completion, reappears for unmet requirements, and honors schema upgrades', async () => {
    const app = harness(true);
    const visit = app.instance.onResume();
    app.reads[0].resolve({ SchemaVersion: 1, ConfigurationCompleted: true, SettingsSaved: true, EnableTalk: true });
    app.refresh[0].resolve({ State: 'Completed', SettingsLocked: true,
        HasCompleteBaseline: true, ChannelRegistered: true });
    await visit;
    await settle();
    for (const name of ['Filtering', 'Category', 'Requirements', 'Saved', 'Channel', 'Baseline'])
        assert.equal(app.elements[`.readiness${name}Check`].checked, true, name);
    assert.equal(app.elements['.tuneInReadiness'].hidden, true);

    const timer = app.timers.get(app.instance.refreshTimer);
    timer.handler();
    app.refresh[1].resolve({ State: 'Completed', SettingsLocked: true,
        HasCompleteBaseline: true, ChannelRegistered: false });
    await settle();
    assert.equal(app.elements['.tuneInReadiness'].hidden, false);
    assert.equal(app.elements['.readinessChannelCheck'].checked, false);
    assert.equal(app.elements['.readinessBaselineCheck'].checked, true);
    app.instance.onPause();

    const upgrade = harness(true);
    const upgradeVisit = upgrade.instance.onResume();
    upgrade.reads[0].resolve({ SchemaVersion: 0, SettingsSaved: true, EnableTalk: true });
    upgrade.refresh[0].resolve({ State: 'Completed', SettingsLocked: true,
        HasCompleteBaseline: true, ChannelRegistered: true });
    await upgradeVisit;
    await settle();
    assert.equal(upgrade.elements['.tuneInReadiness'].hidden, false);
    assert.equal(upgrade.elements['.readinessSavedCheck'].checked, false);
    assert.equal(upgrade.elements['.readinessBaselineCheck'].checked, true);
    upgrade.instance.onPause();
});

test('browsing options default checked and preserve saved choices without starting a refresh', async () => {
    for (const enabled of [undefined, false, true]) {
        const app = harness();
        const visit = app.instance.onResume();
        app.reads[0].resolve({ AllowPartialCatalogImport: enabled, HideRegionalGenres: enabled });
        await visit;
        const control = app.elements['.allowPartialCatalogImport'];
        assert.equal(control.checked, enabled !== false);
        const genres = app.elements['.hideRegionalGenres'];
        assert.equal(genres.checked, enabled !== false);
        control.checked = !(enabled !== false);
        genres.checked = !(enabled !== false);
        app.formHandlers.submit({ preventDefault() {} });
        await settle();
        assert.equal(app.writes[0].config.AllowPartialCatalogImport, enabled === false);
        assert.equal(app.writes[0].config.HideRegionalGenres, enabled === false);
        assert.ok(app.refresh.every(request => request.options.type === 'GET'));
    }
});

test('invalid optional coordinates block save only when enabled', async () => {
    const app = harness();
    const visit = app.instance.onResume();
    app.reads[0].resolve({});
    await visit;
    enableBrowsing(app);
    const local = app.instance.view.querySelector('[data-category="EnableLocalRadio"]');
    local.checked = true;
    app.elements['.localSource'].value = 'coordinates';
    for (const invalid of ['NaN,0', '91,0', '0,181', '1,', '1,2,3', '0x10,0']) {
        app.latLon.value = invalid;
        app.formHandlers.submit({ preventDefault() {} });
        assert.equal(app.writes.length, 0);
        assert.match(app.elements['.status'].textContent, /Enter finite latitude/);
    }
    local.checked = false;
    app.formHandlers.submit({ preventDefault() {} });
    await settle();
    assert.equal(app.writes.length, 1);
    assert.equal(app.writes[0].config.UseCoordinates, false);
});

test('stale selector results do not overwrite a later visit', async () => {
    const app = harness();
    const first = app.instance.onResume();
    app.reads[0].resolve({});
    await first;
    enableBrowsing(app);
    selectOnly(app, 0);
    const root = app.elements['.selectors'].childNodes[0].descendants().find(node => node.tagName === 'details');
    const loading = open(root);
    app.instance.onPause();
    const second = app.instance.onResume();
    app.reads[1].resolve({ Username: 'current' });
    await second;
    const status = app.elements['.status'].textContent;
    app.selectors[0].resolve({ Items: [{ Id: 'l1', Name: 'Stale' }] });
    await loading;
    assert.equal(app.elements['.status'].textContent, status);
    assert.equal(app.elements['.selectors'].textContent.includes('Stale'), false);
});

test('a language branch retries independently, caches on reopening, and offers only valid choices', async () => {
    const app = harness();
    const visit = app.instance.onResume();
    app.reads[0].resolve({});
    await visit;
    enableBrowsing(app);
    selectOnly(app, 0);
    const section = app.elements['.selectors'].childNodes[0];
    const root = section.descendants().find(node => node.tagName === 'details');
    const first = open(root);
    app.selectors[0].resolve({ Items: [{ Id: 'l122', Name: 'Arabic', Path: 'Arabic', CanExpand: true, CanSelect: true }] });
    await first;
    const child = root.descendants().find(node => node.tagName === 'details');
    assert.ok(child);
    assert.equal(app.selectors.length, 1);
    const childLoad = open(child);
    assert.equal(app.selectors[1].options.url.params.Parent, 'l122');
    app.selectors[1].reject(new Error('offline'));
    await childLoad;
    const retry = child.descendants().find(node => node.textContent === 'Retry' && node.tagName === 'button');
    const retryLoad = retry.handlers.click();
    app.selectors[2].resolve({ Items: [{ Name: 'Music', CanExpand: false, CanSelect: false }] });
    await retryLoad;
    assert.match(child.textContent, /Music/);
    assert.equal(child.descendants().filter(node => node.tagName === 'button').length, 0);
    open(child);
    assert.equal(app.selectors.length, 3);
    section.descendants().find(node => node.textContent === 'Add' && node.tagName === 'button').handlers.click();
    assert.match(section.childNodes[2].childNodes[1].childNodes.find(node => node.className === 'tuneInSelectorSaved').textContent, /Arabic/);
});

test('status polls once per visit, drains prior requests and updates only the current view', async () => {
    const app = harness(true);
    const first = app.instance.onResume();
    assert.equal(app.refresh[0].options.url.route, 'TuneIn/Refresh/Status');
    app.reads[0].resolve({});
    await first;
    app.instance.onPause();
    const second = app.instance.onResume();
    app.reads[1].resolve({});
    await second;
    assert.equal(app.refresh.length, 1);
    app.refresh[0].resolve({ State: 'Stale', Busy: true });
    await settle();
    assert.equal(app.refresh.length, 2);
    assert.doesNotMatch(app.elements['.refreshStatus'].textContent, /Stale/);
    app.refresh[1].resolve({ State: 'Running', Phase: 'Discovering', Busy: true });
    await settle();
    assert.match(app.elements['.refreshStatus'].textContent, /Discovering/);
    assert.equal(app.elements['.button-submit'].disabled, true);
    const tick = [...app.timers.values()][0];
    assert.equal(tick.delay, 1500);
    tick.handler();
    assert.equal(app.refresh.length, 3);
    app.instance.onPause();
    app.refresh[2].resolve({ State: 'Completed', Busy: false });
    await settle();
    assert.equal(app.timers.size, 0);
    assert.match(app.elements['.refreshStatus'].textContent, /Discovering/);
});

test('idle polling is slower, refresh actions are explicit, and old polls cannot overwrite action results', async () => {
    const app = harness(true);
    const visit = app.instance.onResume();
    app.elements['.refreshSummary'].handlers.click({ preventDefault() {} });
    app.reads[0].resolve({});
    app.refresh[0].resolve({ State: 'NeverRun', Busy: false,
        Categories: [{ Category: 'talk', Name: 'Talk', State: 'No recorded refresh' }] });
    await visit;
    await settle();
    const idle = [...app.timers.values()][0];
    assert.equal(idle.delay, 10000);
    idle.handler();
    assert.equal(app.refresh[1].options.type, 'GET');
    const action = app.elements['.refreshStart'].handlers.click();
    assert.equal(app.refresh[2].options.type, 'POST');
    assert.equal(app.refresh[2].options.url.route, 'TuneIn/Refresh/Start');
    assert.equal(app.refresh[2].options.url.params, undefined);
    app.refresh[2].resolve({ State: 'Running', Busy: true });
    await settle();
    app.refresh[1].resolve({ State: 'NeverRun', Busy: false });
    await settle();
    assert.match(app.elements['.refreshStatus'].textContent, /Running/);
    assert.equal(app.refresh[3].options.url.route, 'TuneIn/Refresh/Status');
    await action;
    const cancel = app.elements['.refreshCancel'].handlers.click();
    assert.equal(app.refresh[4].options.url.route, 'TuneIn/Refresh/Cancel');
    app.instance.onPause();
    app.refresh[4].resolve({ State: 'Cancelled' });
    app.refresh[3].resolve({ State: 'Cancelled' });
    await cancel;
    assert.match(app.elements['.refreshStatus'].textContent, /Running/);
    assert.equal(app.refresh.length, 5);
});

test('refresh report is retrievable only after the latest run fails and clears when a new run starts', async () => {
    const app = harness(true);
    app.elements['.tuneInRefresh'].open = true;
    const visit = app.instance.onResume();
    app.reads[0].resolve({});
    await visit;
    const failed = { State: 'Error', Busy: false, FinishedUtc: '2025-01-02T03:04:05Z',
        Categories: [{ Category: 'talk', Name: 'Talk', State: 'Failed' }] };
    app.refresh[0].resolve(failed);
    await settle();
    const button = app.elements['.refreshLogButton'];
    assert.equal(button.disabled, false);
    const retrieval = button.handlers.click();
    assert.equal(app.refresh[1].options.url.route, 'TuneIn/Refresh/Log');
    app.refresh[1].resolve({ Text: 'Error: provider unavailable', Truncated: false,
        FinishedUtc: failed.FinishedUtc });
    await retrieval;
    assert.equal(app.elements['.refreshFailureLog'].textContent, 'Error: provider unavailable');
    assert.equal(app.elements['.refreshFailureLog'].hidden, false);

    [...app.timers.values()][0].handler();
    assert.equal(app.refresh[2].options.url.route, 'TuneIn/Refresh/Status');
    app.refresh[2].resolve({ State: 'Running', Busy: true, Phase: 'Discovering' });
    await settle();
    assert.equal(button.disabled, true);
    assert.equal(app.elements['.refreshFailureLog'].hidden, true);
});

test('partial reports survive same-run polls and stale retrievals cannot replace a newer report', async () => {
    const app = harness(true);
    const visit = app.instance.onResume();
    app.reads[0].resolve({});
    const partial = { State: 'Partial', Busy: false, ReportAvailable: true, FinishedUtc: '2026-11-19T10:00:00Z' };
    app.refresh[0].resolve(partial);
    await visit;
    await settle();
    const button = app.elements['.refreshLogButton'];
    assert.equal(button.disabled, false);
    let retrieval = button.handlers.click();
    app.refresh[1].resolve({ Text: '<script>untrusted provider text</script>', FinishedUtc: partial.FinishedUtc });
    await retrieval;
    const report = app.elements['.refreshFailureLog'];
    assert.equal(report.textContent, '<script>untrusted provider text</script>');
    assert.equal(report.childNodes.length, 0);
    [...app.timers.values()][0].handler();
    app.refresh[2].resolve(partial);
    await settle();
    assert.equal(report.hidden, false);
    assert.equal(report.textContent, '<script>untrusted provider text</script>');
    retrieval = button.handlers.click();
    [...app.timers.values()][0].handler();
    app.refresh[4].resolve({ ...partial, FinishedUtc: '2026-11-19T11:00:00Z' });
    await settle();
    app.refresh[3].resolve({ Text: 'Stale report', FinishedUtc: partial.FinishedUtc });
    await retrieval;
    assert.equal(report.hidden, true);
    assert.equal(report.textContent, '');
    app.instance.onPause();
});

test('category counts show availability, inventory and missing discovery left of Refresh', async () => {
    const app = harness(true);
    const visit = app.instance.onResume();
    app.elements['.refreshSummary'].handlers.click({ preventDefault() {} });
    app.reads[0].resolve({});
    app.refresh[0].resolve({ State: 'Partial', Busy: false, Categories: [
        { Category: 'talk', Name: 'Talk', State: 'Partial (applied)', StationCounts: {
            AvailableStations: 123, AvailablePartial: true, EmbyStations: 145,
            AvailableCheckedUtc: '2026-11-19T10:00:00Z', EmbyCheckedUtc: '2026-11-19T10:05:00Z'
        } },
        { Category: 'sports', Name: 'Sports', State: 'Imported',
            StationCounts: { AvailableStations: 0, EmbyStations: 0, NotInLatestDiscovery: 0 } },
        { Category: 'local', Name: 'Local Radio', State: 'No recorded refresh' },
        { Category: 'presets', Name: 'Favorites', State: 'Imported',
            StationCounts: { AvailableStations: 4, EmbyCountError: '<unsafe> inventory read failed' } },
        { Category: 'language', Name: 'By Language', State: 'Imported',
            StationCounts: { AvailableStations: 0, EmbyStations: 0 } }
    ] });
    await visit;
    await settle();
    const rows = app.elements['.tuneInRefreshCategories'].childNodes;
    assert.equal(rows[2].childNodes[0].textContent, 'By Language: Imported (TuneIn lists no stations for the selected languages)');
    assert.equal(rows[4].childNodes[0].textContent, 'Sports: Imported');
    const values = row => row.childNodes[1].childNodes[0].childNodes.map(group => group.childNodes[1]);
    assert.equal(rows[0].childNodes[1].childNodes[1].textContent, 'Refresh');
    assert.equal(rows[0].childNodes[1].childNodes[1].attributes.is, 'emby-button');
    assert.match(rows[0].childNodes[1].childNodes[1].className, /\braised\b/);
    assert.match(rows[0].childNodes[1].childNodes[1].className, /\bbutton-submit\b/);
    assert.deepEqual(values(rows[3]).map(value => value.textContent), ['123 (partial)', '145', 'Unavailable']);
    assert.match(values(rows[3])[0].attributes.title, /lower bound/);
    assert.deepEqual(values(rows[4]).map(value => value.textContent), ['0', '0', '0']);
    assert.deepEqual(values(rows[1]).map(value => value.textContent), ['Not checked', 'Not checked', 'Not checked']);
    assert.deepEqual(values(rows[0]).map(value => value.textContent), ['4', 'Unavailable', 'Unavailable']);
    assert.doesNotMatch(app.elements['.tuneInRefreshCategories'].textContent, /unfiltered|Not measured|Not applicable/i);
    assert.equal(values(rows[0])[1].childNodes.length, 0);
    assert.equal(app.selectors.length, 0);
    assert.equal(app.refresh.length, 1);
    app.instance.onPause();
});

test('library refresh follows full-refresh order and marks only the current running category', async () => {
    const app = harness(true);
    const visit = app.instance.onResume();
    app.elements['.refreshSummary'].handlers.click({ preventDefault() {} });
    app.reads[0].resolve({});
    const categories = [
        ['podcast', 'Podcasts'], ['sports', 'Sports'], ['talk', 'Talk'], ['location', 'By Location'],
        ['language', 'By Language'], ['music', 'Music'], ['local', 'Local Radio'], ['presets', 'My Favorites']
    ].map(([Category, Name]) => ({ Category, Name, State: 'No recorded refresh' }));
    app.refresh[0].resolve({ State: 'Running', Busy: true, Category: 'local',
        QueuedRequests: ['local', 'music'], Categories: categories });
    await visit;
    await settle();
    const rows = app.instance.refreshCategoryRows;
    assert.deepEqual([...rows.keys()], ['presets', 'local', 'music', 'language', 'location', 'talk', 'sports', 'podcast']);
    const local = rows.get('local');
    const music = rows.get('music');
    assert.equal(local.row.attributes['data-refreshing'], 'true');
    assert.equal(local.indicator.hidden, false);
    assert.equal(local.indicator.textContent, 'Refreshing');
    assert.equal(local.indicator.attributes.role, 'status');
    assert.equal(local.caption.textContent, 'Local Radio: No recorded refresh');
    assert.equal(music.row.attributes['data-refreshing'], 'false');
    assert.equal(music.caption.textContent, 'Music: Queued');
    const originalLocal = local.row;
    const originalMusic = music.row;
    const poll = async status => {
        [...app.timers.values()][0].handler();
        app.refresh.at(-1).resolve(status);
        await settle();
    };
    await poll({ State: 'Running', Busy: true, Category: 'music',
        QueuedRequests: [], Categories: categories.slice().reverse() });
    assert.equal(rows.get('local').row, originalLocal);
    assert.equal(rows.get('music').row, originalMusic);
    assert.equal(local.indicator.hidden, true);
    assert.equal(local.indicator.textContent, '');
    assert.equal(music.indicator.hidden, false);
    assert.equal(music.row.attributes['data-refreshing'], 'true');
    await poll({ State: 'Running', Busy: true, Category: 'music', SharedImport: true, Categories: categories });
    assert.equal(music.indicator.hidden, true);
    await poll({ State: 'Running', Busy: true, Category: null, Categories: categories });
    assert.equal(music.indicator.hidden, true);
    await poll({ State: 'Completed', Busy: false, Category: 'music', Categories: categories });
    assert.equal(music.row.attributes['data-refreshing'], 'false');
    assert.equal(music.indicator.hidden, true);
    app.instance.onPause();
});

test('In Emby keeps last known values and timestamps when the settings screen reopens', async () => {
    const html = fs.readFileSync(path.join(configDir, 'configPage.html'), 'utf8');
    assert.match(html, /\.tuneInStationCounts \[data-error="true"\][^{]*\{[^}]*display:\s*inline-block;[^}]*border:\s*0;[^}]*background:\s*linear-gradient\(transparent 45%, color-mix\(in srgb, #ffe69b 40%, transparent\) 45%\)/);
    for (const inventory of [0, 7]) {
        const app = harness(true);
        const checkedAt = '2026-11-19T10:00:00Z';
        const status = { State: 'Completed', Busy: false, Categories: [
            { Category: 'talk', Name: 'Talk', State: 'Imported',
                StationCounts: { AvailableStations: 8, EmbyStations: inventory, EmbyCheckedUtc: checkedAt } }
        ] };
        let visit = app.instance.onResume();
        app.elements['.refreshSummary'].handlers.click({ preventDefault() {} });
        app.reads[0].resolve({});
        app.refresh[0].resolve(status);
        await visit;
        await settle();
        const value = () => app.instance.refreshCategoryRows.get('talk').counts.emby;
        assert.equal(value().textContent, String(inventory));
        app.instance.onPause();
        visit = app.instance.onResume();
        app.elements['.refreshSummary'].handlers.click({ preventDefault() {} });
        app.reads[1].resolve({});
        app.refresh[1].resolve(status);
        await visit;
        await settle();
        assert.equal(value().textContent, String(inventory));
        assert.match(value().attributes.title, /inventory checked at/i);
        [...app.timers.values()][0].handler();
        app.refresh[2].resolve({ ...status, Categories: [{ ...status.Categories[0],
            StationCounts: { ...status.Categories[0].StationCounts,
                EmbyCountError: 'Emby inventory read failed; check server log' } }] });
        await settle();
        assert.equal(value().textContent, inventory + ' (last known)');
        assert.equal(value().attributes['data-error'], 'true');
        assert.match(value().attributes.title, /Last successful inventory check:/);
        assert.match(value().attributes.title, /inventory read failed/);
        [...app.timers.values()][0].handler();
        app.refresh[3].resolve({ ...status, Categories: [{ ...status.Categories[0],
            StationCounts: { EmbyCountError: 'Emby inventory service unavailable' } }] });
        await settle();
        assert.equal(value().textContent, 'Unavailable');
        assert.equal(value().attributes['data-error'], 'true');
        [...app.timers.values()][0].handler();
        app.refresh[4].resolve(status);
        await settle();
        assert.equal(value().textContent, String(inventory));
        assert.equal(value().attributes['data-error'], 'false');
        app.instance.onPause();
        assert.equal(app.selectors.length, 0);
    }
});

test('collapsing browsing settings keeps choices without loading All-mode selectors', async () => {
    const app = harness();
    const visit = app.instance.onResume();
    app.reads[0].resolve({ EnableTalk: true, LatLon: '1,2', Music: [{ Id: 'g1', Name: 'Rock' }] });
    await visit;
    const music = app.elements['.selectors'].childNodes[2];
    assert.equal(music.childNodes[0].textContent, 'Music Genres');
    const summaries = [app.elements['.browsingSummary'], app.elements['.categoriesSummary'], app.elements['.stationSelectionsSummary'],
        ...app.elements['.selectors'].childNodes.filter(section => section.open).map(section => section.childNodes[0])];
    for (const summary of summaries) {
        assert.equal(summary.parentNode.open, true);
        const requestsBeforeCollapse = app.selectors.length;
        summary.handlers.click({ preventDefault() {} });
        assert.equal(summary.parentNode.open, false);
        assert.equal(app.selectors.length, requestsBeforeCollapse);
        summary.handlers.click({ preventDefault() {} });
        assert.equal(summary.parentNode.open, true);
    }
    assert.equal(app.selectors.length, 0);
    await settle();
    assert.match(music.textContent, /Rock/);
    assert.equal(app.latLon.value, '1,2');
    assert.ok(app.elements['.categories'].descendants().some(item => item.checked));
});

test('saving opens Catalog Refresh only when Station Filtering settings changed', async () => {
    const savedConfig = {
        SchemaVersion: 1, ConfigurationCompleted: true,
        EnableLocalRadio: false, EnableMusic: false, EnableLanguage: false, EnableLocation: false,
        EnableTalk: false, EnableSports: false, EnablePodcasts: false, EnableFavorites: false,
        Languages: [], Locations: [], Music: [], LocalCities: [],
        LanguageMode: 'all', LocationMode: 'all', MusicMode: 'all', LocalMode: 'all',
        TalkMode: 'all', TalkIds: [], SportsMode: 'all', SportsIds: [], PodcastsMode: 'all', PodcastIds: []
    };
    const unchanged = harness();
    let visit = unchanged.instance.onResume();
    unchanged.reads[0].resolve({ ...savedConfig });
    await visit;
    unchanged.username.value = 'listener';
    unchanged.formHandlers.submit({ preventDefault() {} });
    await settle();
    assert.equal(unchanged.elements['.tuneInRefresh'].open, false);

    const changed = harness();
    visit = changed.instance.onResume();
    changed.reads[0].resolve({ ...savedConfig });
    await visit;
    changed.instance.view.querySelector('[data-category="EnableTalk"]').checked = true;
    changed.formHandlers.submit({ preventDefault() {} });
    await settle();
    assert.equal(changed.elements['.tuneInRefresh'].open, true);
});

test('choosing a category schedule confirms when Emby also schedules Refresh Internet Channels', async () => {
    const html = fs.readFileSync(path.join(configDir, 'configPage.html'), 'utf8');
    assert.doesNotMatch(html, /channelScheduleNotice|#c62828/);
    assert.match(html, /<dialog class="tuneInRecoveryDialog channelScheduleDialog"/);

    const open = async tasks => {
        const app = harness(true);
        app.scheduledTasks.tasks = tasks;
        const visit = app.instance.onResume();
        app.elements['.refreshSummary'].handlers.click({ preventDefault() {} });
        app.reads[0].resolve({ SchemaVersion: 1, ConfigurationCompleted: true, EnableTalk: true,
            RefreshSchedules: [{ Category: 'talk', IntervalHours: 24 }] });
        app.refresh[0].resolve({ SettingsLocked: false, Categories: [{ Category: 'talk', Name: 'Talk' }] });
        await visit;
        const talk = app.elements['.tuneInRefreshCategories'].childNodes[0].childNodes[1].childNodes[2];
        return { app, talk };
    };
    const other = { Id: 'other', Key: 'RefreshLibrary', Name: 'Scan media library', Triggers: [{ Type: 'IntervalTrigger' }] };
    const channels = triggers => ({ Id: 'channels', Key: 'RefreshInternetChannels', Name: 'Refresh Internet Channels', Triggers: triggers });

    const quiet = await open([other, channels([])]);
    quiet.talk.value = '168';
    await quiet.talk.handlers.change();
    assert.equal(quiet.app.scheduledTasks.requests.length, 1);
    assert.notEqual(quiet.app.elements['.channelScheduleDialog'].open, true);

    const { app, talk } = await open([other, channels([{ Type: 'IntervalTrigger', IntervalTicks: 864000000000 }])]);
    talk.value = '0';
    await talk.handlers.change();
    assert.equal(app.scheduledTasks.requests.length, 0);

    const dialog = app.elements['.channelScheduleDialog'];
    talk.value = '168';
    await talk.handlers.change();
    assert.equal(dialog.open, true);
    const text = app.elements['.channelScheduleText'].textContent;
    assert.match(text, /^Talk will refresh weekly after you save/);
    assert.match(text, /refreshes all TuneIn categories/);
    assert.match(text, /do not refresh any other Internet Channel/);
    app.elements['.channelScheduleAdjust'].handlers.click();
    assert.equal(dialog.open, false);
    assert.equal(talk.value, '0');
    assert.equal(app.instance.refreshSchedules.get('talk'), 0);

    talk.value = '6';
    await talk.handlers.change();
    app.elements['.channelScheduleConfirm'].handlers.click();
    assert.equal(dialog.open, false);
    assert.equal(talk.value, '6');
    assert.equal(app.instance.refreshSchedules.get('talk'), 6);
    const requests = app.scheduledTasks.requests.length;
    app.formHandlers.submit({ preventDefault() {} });
    await settle();
    assert.equal(app.scheduledTasks.requests.length, requests);
});

test('maintenance previews before confirming and blocks settings and refresh until cleanup stops', async () => {
    const app = harness(true);
    const visit = app.instance.onResume();
    app.reads[0].resolve({ SchemaVersion: 1, ConfigurationCompleted: true, EnableTalk: true, ApiRequestIntervalSeconds: 3 });
    const live = { SettingsLocked: false, Categories: [{ Category: 'talk', Name: 'Talk' }] };
    app.refresh[0].resolve(live);
    await visit;
    await settle();
    assert.equal(app.requestInterval.value, 3);
    await open(app.elements['.maintenanceSection']);
    const click = name => app.elements[name].handlers.click();
    assert.equal(click('.cleanupConfirm'), undefined);
    assert.equal(app.cleanup.length, 0);
    assert.equal(app.elements['.cleanupPreview'].disabled, true);
    app.elements['.cleanupScope'].value = 'talk';
    app.elements['.cleanupScope'].handlers.change();
    const preview = click('.cleanupPreview');
    assert.equal(app.cleanup[0].options.url.route, 'TuneIn/Cleanup/Preview');
    assert.equal(JSON.parse(app.cleanup[0].options.data).Category, 'talk');
    app.cleanup[0].resolve({ Token: 'preview-token', Scope: 'Talk', Folders: 2, Stations: 7 });
    await preview;
    assert.match(app.elements['.cleanupPreviewText'].textContent, /2 folders and 7 station entries from Talk/);
    assert.equal(app.elements['.cleanupConfirmation'].hidden, false);
    assert.equal(app.elements['.cleanupBack'].focused, true);
    assert.equal(app.elements['.cleanupConfirm'].disabled, true);
    app.elements['.cleanupBackupVerified'].checked = true;
    app.elements['.cleanupBackupVerified'].handlers.change();
    const start = click('.cleanupConfirm');
    assert.equal(app.cleanup[1].options.url.route, 'TuneIn/Cleanup/Start');
    assert.equal(JSON.parse(app.cleanup[1].options.data).Token, 'preview-token');
    assert.equal(app.elements['.cleanupConfirmation'].hidden, true);
    assert.equal(app.elements['.button-submit'].disabled, true);
    app.refresh[1].resolve(live);
    app.cleanup[1].resolve({ ...live, Cleanup: { State: 'Running', Total: 9, Removed: 0 } });
    await start;
    assert.equal(app.elements['.settingsLocked'].disabled, true);
    assert.equal(app.elements['.globalFields'].disabled, true);
    assert.equal(app.elements['.configurationFields'].disabled, true);
    assert.equal(app.elements['.maintenanceIndicator'].hidden, false);
    assert.match(app.elements['.settingsLockStatus'].textContent, /maintenance is active/);
    assert.equal(app.elements['.refreshStart'].disabled, true);
    assert.equal(app.elements['.cleanupPreview'].disabled, true);
    assert.match(app.elements['.cleanupStatus'].textContent, /0 of 9/);
    app.refresh.at(-1).resolve({ ...live, Cleanup: { State: 'Partial', Total: 9, Removed: 8, Failed: 1 } });
    await settle();
    assert.match(app.elements['.cleanupStatus'].textContent, /8 of 9.*1 entries could not be removed/);
    assert.equal(app.elements['.settingsLocked'].disabled, false);
    assert.equal(app.elements['.maintenanceIndicator'].hidden, true);
    assert.equal(app.elements['.globalFields'].disabled, false);
    assert.equal(app.elements['.configurationFields'].disabled, false);
});

test('maintenance stop requests cancellation and keeps controls locked until it drains', async () => {
    const app = harness(true);
    const visit = app.instance.onResume();
    const running = { State: 'NeverRun', SettingsLocked: false, Categories: [],
        Cleanup: { State: 'Running', Removed: 3, Total: 9, Failed: 0 } };
    app.reads[0].resolve({ SchemaVersion: 1, ConfigurationCompleted: true });
    app.refresh[0].resolve(running);
    await visit;
    await settle();
    await open(app.elements['.maintenanceSection']);
    assert.equal(app.elements['.cleanupCancel'].disabled, false);
    const stop = app.elements['.cleanupCancel'].handlers.click();
    assert.equal(app.cleanup[0].options.url.route, 'TuneIn/Cleanup/Cancel');
    assert.equal(app.elements['.cleanupCancel'].disabled, true);
    app.elements['.cleanupCancel'].handlers.click();
    assert.equal(app.cleanup.length, 1);
    app.cleanup[0].resolve({ ...running, Cleanup: { ...running.Cleanup, Cancelling: true,
        Message: 'Stopping cleanup; the current removal must finish.' } });
    await stop;
    assert.equal(app.elements['.cleanupCancel'].disabled, true);
    assert.equal(app.elements['.maintenanceIndicator'].hidden, false);
    assert.equal(app.elements['.settingsLocked'].disabled, true);
    assert.match(app.elements['.cleanupStatus'].textContent, /Stopping cleanup/);
    app.refresh.at(-1).resolve({ ...running, Cleanup: { State: 'Stopped', Removed: 4, Total: 9,
        Failed: 0, Message: 'Cleanup stopped. Removed entries cannot be restored by stopping.' } });
    await settle();
    assert.equal(app.elements['.maintenanceIndicator'].hidden, true);
    assert.equal(app.elements['.cleanupCancel'].disabled, true);
    assert.equal(app.elements['.settingsLocked'].disabled, false);
    assert.match(app.elements['.cleanupStatus'].textContent, /4 of 9.*Cleanup stopped/);
});

test('maintenance stop failure is reported and may be retried', async () => {
    const app = harness(true);
    const visit = app.instance.onResume();
    app.reads[0].resolve({ SchemaVersion: 1, ConfigurationCompleted: true });
    app.refresh[0].resolve({ State: 'NeverRun', SettingsLocked: false, Categories: [],
        Cleanup: { State: 'Running', Removed: 0, Total: 3 } });
    await visit;
    await settle();
    await open(app.elements['.maintenanceSection']);
    const stop = app.elements['.cleanupCancel'].handlers.click();
    app.cleanup[0].reject(new Error('offline'));
    await stop;
    assert.match(app.elements['.cleanupError'].textContent, /Could not confirm cleanup stopped/);
    assert.equal(app.elements['.cleanupCancel'].disabled, false);
    const retry = app.elements['.cleanupCancel'].handlers.click();
    assert.equal(app.cleanup.length, 2);
    app.cleanup[1].resolve({ State: 'NeverRun', SettingsLocked: false, Categories: [],
        Cleanup: { State: 'Running', Cancelling: true, Removed: 0, Total: 3 } });
    await retry;
    assert.equal(app.elements['.cleanupCancel'].disabled, true);
    assert.equal(app.elements['.cleanupError'].textContent, '');
});

test('a stale cleanup status poll cannot overwrite a stop response', async () => {
    const app = harness(true);
    const visit = app.instance.onResume();
    const running = { State: 'NeverRun', SettingsLocked: false, Categories: [],
        Cleanup: { State: 'Running', Removed: 0, Total: 3 } };
    app.reads[0].resolve({ SchemaVersion: 1, ConfigurationCompleted: true });
    app.refresh[0].resolve(running);
    await visit;
    await settle();
    await open(app.elements['.maintenanceSection']);
    [...app.timers.values()].find(timer => timer.delay === 1500).handler();
    assert.equal(app.refresh.length, 2);
    const stop = app.elements['.cleanupCancel'].handlers.click();
    app.cleanup[0].resolve({ ...running, Cleanup: { ...running.Cleanup, Cancelling: true,
        Message: 'Stopping cleanup' } });
    await stop;
    app.refresh[1].resolve(running);
    await settle();
    assert.match(app.elements['.cleanupStatus'].textContent, /Stopping cleanup/);
    assert.equal(app.elements['.cleanupCancel'].disabled, true);
    assert.equal(app.refresh.length, 3);
    app.refresh[2].resolve({ ...running, Cleanup: { State: 'Stopped', Removed: 1, Total: 3 } });
    await settle();
    assert.equal(app.elements['.maintenanceIndicator'].hidden, true);
});

test('refresh locks settings immediately and shows activity even when the section is collapsed', async () => {
    const app = harness(true);
    const visit = app.instance.onResume();
    app.reads[0].resolve({ SchemaVersion: 1, ConfigurationCompleted: true });
    app.refresh[0].resolve({ State: 'Completed', Busy: false, SettingsLocked: false,
        Categories: [{ Category: 'talk', Name: 'Talk', State: 'Imported' }] });
    await visit;
    await settle();
    app.elements['.tuneInRefresh'].open = true;
    const start = app.elements['.refreshStart'].handlers.click();
    assert.equal(app.elements['.globalFields'].disabled, true);
    assert.equal(app.elements['.configurationFields'].disabled, true);
    assert.equal(app.elements['.settingsLocked'].disabled, true);
    app.elements['.tuneInRefresh'].open = false;
    app.refresh[1].resolve({ State: 'Running', Busy: true, Phase: 'Discovering', Category: 'talk',
        SettingsLocked: false, Categories: [{ Category: 'talk', Name: 'Talk', State: 'Discovering' }] });
    await start;
    assert.equal(app.elements['.refreshIndicator'].hidden, false);
    assert.equal(app.elements['.maintenanceIndicator'].hidden, true);
    assert.match(app.elements['.settingsLockStatus'].textContent, /catalog refresh is active/);
    app.formHandlers.submit({ preventDefault() {} });
    assert.equal(app.writes.length, 0);
    app.refresh[2].resolve({ State: 'Completed', Busy: false, SettingsLocked: false,
        Categories: [{ Category: 'talk', Name: 'Talk', State: 'Imported' }] });
    await settle();
    assert.equal(app.elements['.refreshIndicator'].hidden, true);
    assert.equal(app.elements['.globalFields'].disabled, false);
    assert.equal(app.elements['.configurationFields'].disabled, false);
    assert.equal(app.elements['.settingsLocked'].disabled, false);
});

test('settings remain disabled when refresh status is unavailable', async () => {
    const app = harness(true);
    const visit = app.instance.onResume();
    app.reads[0].resolve({ SchemaVersion: 1, ConfigurationCompleted: true });
    app.refresh[0].reject(new Error('status unavailable'));
    await visit;
    await settle();
    assert.equal(app.elements['.globalFields'].disabled, true);
    assert.equal(app.elements['.configurationFields'].disabled, true);
    assert.equal(app.elements['.button-submit'].disabled, true);
    assert.equal(app.elements['.settingsLocked'].disabled, true);
});

test('changing cleanup scope invalidates the preview and backup acknowledgement', async () => {
    const app = harness();
    const visit = app.instance.onResume();
    app.reads[0].resolve({});
    await visit;
    await open(app.elements['.maintenanceSection']);
    const scope = app.elements['.cleanupScope'];
    scope.value = 'talk';
    scope.handlers.change();
    const preview = app.elements['.cleanupPreview'].handlers.click();
    app.cleanup[0].resolve({ Token: 'first', Scope: 'Talk', Folders: 1, Stations: 2 });
    await preview;
    app.elements['.cleanupBackupVerified'].checked = true;
    app.elements['.cleanupBackupVerified'].handlers.change();
    scope.value = 'all';
    scope.handlers.change();
    assert.equal(app.elements['.cleanupConfirmation'].hidden, true);
    assert.equal(app.elements['.cleanupBackupVerified'].checked, false);
    assert.equal(app.elements['.cleanupConfirm'].disabled, true);
    const next = app.elements['.cleanupPreview'].handlers.click();
    assert.equal(JSON.parse(app.cleanup[1].options.data).Category, 'all');
    app.cleanup[1].resolve({ Token: 'second', Scope: 'All categories', Folders: 3, Stations: 4 });
    await next;
    assert.match(app.elements['.cleanupPreviewText'].textContent, /All categories/);
});

test('backup uses the dedicated action and cleanup stays disabled while Emby has not finished', async () => {
    const app = harness(false, false, true);
    const visit = app.instance.onResume();
    app.reads[0].resolve({});
    await visit;
    const opened = open(app.elements['.maintenanceSection']);
    app.backup[0].resolve({ State: 'Ready' });
    await opened;
    const start = app.elements['.backupStart'].handlers.click();
    assert.equal(app.elements['.settingsLocked'].disabled, true);
    assert.equal(app.elements['.settingsLocked'].checked, true);
    assert.equal(app.elements['.globalFields'].disabled, true);
    assert.deepEqual(app.backup[1].options.url, { route: 'TuneIn/Backup/Start', params: undefined });
    app.backup[1].resolve({ State: 'Running' });
    await start;
    assert.match(app.elements['.backupStatus'].textContent, /Wait for Emby/);
    assert.equal(app.elements['.cleanupPreview'].disabled, true);
    [...app.timers.values()][0].handler();
    app.backup[2].resolve({ State: 'Completed' });
    await settle();
    assert.match(app.elements['.backupStatus'].textContent, /completed successfully/);
    app.elements['.cleanupScope'].value = 'all';
    app.elements['.cleanupScope'].handlers.change();
    assert.equal(app.elements['.cleanupPreview'].disabled, false);
    app.instance.onPause();
});

test('Catalog Refresh defaults open only for the first refresh or an active refresh', async () => {
    for (const [status, expected] of [
        [{ State: 'NeverRun', HasRefreshBeenStarted: false }, true],
        [{ State: 'NeverRun', HasRefreshBeenStarted: true }, false],
        [{ State: 'NeverRun', HasRefreshBeenStarted: false, StartedUtc: '2026-11-20T00:00:00Z' }, false],
        [{ State: 'Completed', HasRefreshBeenStarted: true }, false],
        [{ State: 'Error', HasRefreshBeenStarted: true }, false],
        [{ State: 'Running', Busy: true, HasRefreshBeenStarted: true }, true]
    ]) {
        const app = harness(true);
        const visit = app.instance.onResume();
        assert.equal(app.elements['.tuneInRefresh'].open, false);
        app.reads[0].resolve({ SchemaVersion: 1, ConfigurationCompleted: true });
        app.refresh[0].resolve({ ...status, SettingsLocked: false });
        await visit;
        await settle();
        assert.equal(app.elements['.tuneInRefresh'].open, expected, JSON.stringify(status));
        app.instance.onPause();
    }
});

test('manual Catalog Refresh choice is preserved while status loads and while refreshing', async () => {
    const app = harness(true);
    const visit = app.instance.onResume();
    const section = app.elements['.tuneInRefresh'];
    app.elements['.refreshSummary'].handlers.click({ preventDefault() {} });
    app.reads[0].resolve({ SchemaVersion: 1, ConfigurationCompleted: true });
    app.refresh[0].resolve({ State: 'Completed', Busy: false, SettingsLocked: false });
    await visit;
    await settle();
    assert.equal(section.open, true);
    app.elements['.refreshSummary'].handlers.click({ preventDefault() {} });
    [...app.timers.values()].find(timer => timer.delay === 10000).handler();
    app.refresh[1].resolve({ State: 'Running', Busy: true, SettingsLocked: false });
    await settle();
    assert.equal(section.open, false);
    assert.equal(app.elements['.refreshIndicator'].hidden, false);
    app.instance.onPause();
    const next = app.instance.onResume();
    app.reads[1].resolve({ SchemaVersion: 1, ConfigurationCompleted: true });
    app.refresh[2].resolve({ State: 'Running', Busy: true, SettingsLocked: false });
    await next;
    await settle();
    assert.equal(section.open, true);
});

test('Catalog Refresh applies its default after a status read recovers', async () => {
    const app = harness(true);
    const visit = app.instance.onResume();
    app.reads[0].resolve({ SchemaVersion: 1, ConfigurationCompleted: true });
    app.refresh[0].reject(new Error('offline'));
    await visit;
    await settle();
    assert.equal(app.elements['.tuneInRefresh'].open, false);
    assert.match(app.elements['.refreshStatus'].textContent, /Retrying automatically/);
    [...app.timers.values()].find(timer => timer.delay === 10000).handler();
    app.refresh[1].resolve({ State: 'NeverRun', HasRefreshBeenStarted: false, Busy: false, SettingsLocked: false });
    await settle();
    assert.equal(app.elements['.tuneInRefresh'].open, true);
});

test('programmatic Catalog Refresh opening and stale visits do not override the current choice', async () => {
    const app = harness(true);
    let visit = app.instance.onResume();
    app.reads[0].resolve({ SchemaVersion: 1, ConfigurationCompleted: true });
    await visit;
    app.instance.onPause();
    visit = app.instance.onResume();
    app.reads[1].resolve({ SchemaVersion: 1, ConfigurationCompleted: true });
    app.refresh[0].resolve({ State: 'Completed', Busy: false, SettingsLocked: false });
    await visit;
    await settle();
    assert.equal(app.elements['.tuneInRefresh'].open, false);
    app.refresh[1].resolve({ State: 'Running', Busy: true, SettingsLocked: false });
    await settle();
    const section = app.elements['.tuneInRefresh'];
    assert.equal(section.open, true);
    section.handlers.toggle();
    app.elements['.refreshSummary'].handlers.click({ preventDefault() {} });
    app.refresh[2].resolve({ State: 'Running', Busy: true, SettingsLocked: false });
    await settle();
    assert.equal(section.open, false);
});

test('Catalog Refresh can be opened, collapsed and reopened with fresh status', async () => {
    const app = harness(true);
    const html = fs.readFileSync(path.join(configDir, 'configPage.html'), 'utf8');
    assert.match(html, /<details class="[^"]*\btuneInRefresh">/);
    const visit = app.instance.onResume();
    const section = app.elements['.tuneInRefresh'];
    const list = app.elements['.tuneInRefreshCategories'];
    const control = row => row.childNodes[1].childNodes[1];
    app.reads[0].resolve({});
    await visit;
    app.elements['.refreshSummary'].handlers.click({ preventDefault() {} });
    assert.equal(section.open, true);
    section.handlers.toggle();
    assert.equal(app.refresh.length, 1);
    app.refresh[0].resolve({ State: 'NeverRun' });
    await settle();
    assert.equal(app.refresh[1].options.url.route, 'TuneIn/Refresh/Status');
    assert.equal(list.childNodes.length, 0);
    const categories = [{ Category: 'talk', Name: 'Talk', State: 'Unchanged' },
        { Category: 'sports', Name: 'Sports', State: 'Cancelled' }];
    app.refresh[1].resolve({ State: 'Cancelled', DirtyCategory: 'sports', Categories: categories });
    await settle();
    const [talk, sports] = list.childNodes;
    assert.match(talk.textContent, /Talk.*Unchanged/);
    assert.match(sports.textContent, /Sports.*reconciliation needed/);
    assert.equal(control(talk).disabled, false);
    assert.equal(control(sports).disabled, false);
    const action = control(sports).handlers.click();
    assert.equal(app.refresh[2].options.type, 'POST');
    assert.equal(app.refresh[2].options.url.route, 'TuneIn/Refresh/Start');
    assert.equal(app.refresh[2].options.url.params.Category, 'sports');
    app.refresh[2].resolve({ State: 'Running', Busy: true, ActiveRequest: 'sports', Categories: categories });
    await action;
    assert.equal(control(sports).disabled, true);
    section.open = false;
    section.handlers.toggle();
    app.refresh[3].resolve({ State: 'Completed', Categories: categories });
    await settle();
    section.open = true;
    section.handlers.toggle();
    assert.equal(app.refresh[4].options.url.route, 'TuneIn/Refresh/Status');
    assert.equal(control(sports).disabled, true);
    app.refresh[4].resolve({ State: 'Completed', Categories: [categories[0], { ...categories[1], State: 'Imported' }] });
    await settle();
    assert.equal(list.childNodes[1], sports);
    assert.match(sports.textContent, /Sports.*Imported/);
    assert.equal(control(sports).disabled, false);
});

for (const resume of [true, false]) {
    test(`recovery prompt submits explicit choice (${resume ? 'retry' : 'prioritize'}) and respects queue capacity`, async () => {
        const app = harness(true);
        const section = app.elements['.tuneInRefresh'];
        const visit = app.instance.onResume();
        app.elements['.refreshSummary'].handlers.click({ preventDefault() {} });
        app.reads[0].resolve({});
        const categories = [
            { Category: 'local', Name: 'Local Radio' }, { Category: 'presets', Name: 'My Favorites' },
            { Category: 'sports', Name: 'Sports' }
        ];
        const failure = { State: 'Error', RecoveryCategory: 'local', RecoveryId: 'generation', Categories: categories };
        app.refresh[0].resolve(failure);
        await visit;
        const rows = app.elements['.tuneInRefreshCategories'].childNodes;
        const [favorites, local, sports] = rows.map(row => row.childNodes[1].childNodes[1]);
        const action = favorites.handlers.click();
        assert.equal(app.refresh[1].options.url.route, 'TuneIn/Refresh/Start');
        assert.equal(app.refresh[1].options.url.params.Category, 'presets');
        app.refresh[1].resolve({ ...failure, RecoveryChoiceRequired: true });
        await action;
        const dialog = app.elements['.tuneInRefreshRecoveryDialog'];
        const deletionDialog = app.elements['.selectors'].descendants().find(item =>
            (item.className || '').split(/\s+/).includes('tuneInSelectorDeleteDialog'));
        assert.ok(deletionDialog);
        assert.notEqual(deletionDialog.open, true);
        assert.equal(dialog.open, true);
        assert.match(app.elements['.refreshRecoveryText'].textContent, /Local Radio.*My Favorites/);
        assert.equal(app.refresh.filter(request => request.options.type === 'POST').length, 1);
        const confirm = app.elements[resume ? '.refreshResumeFirst' : '.refreshNewFirst'].handlers.click();
        const request = app.refresh.at(-1);
        assert.equal(request.options.url.params.Category, 'presets');
        assert.equal(request.options.url.params.ExpectedRecoveryId, 'generation');
        assert.equal(request.options.url.params.ResumeFirst, resume);
        request.resolve({ State: 'Running', Busy: true, ActiveRequest: resume ? 'local' : 'presets',
            QueuedRequests: resume ? ['presets'] : [], QueueSlotsAvailable: resume ? 1 : 2, Categories: categories });
        await confirm;
        assert.equal(dialog.open, false);
        assert.equal(local.disabled, resume);
        assert.equal(favorites.disabled, true);
        assert.equal(sports.disabled, false);
        // The outstanding poll predates the confirmation and cannot replace its response.
        app.refresh[2].resolve(failure);
        await settle();
        app.refresh.at(-1).resolve({ State: 'Running', Busy: true, ActiveRequest: 'local',
            QueuedRequests: ['presets', 'all'], QueueSlotsAvailable: 0, Categories: categories });
        await settle();
        assert.equal(sports.disabled, true);
        assert.match(app.elements['.refreshQueueNote'].textContent, /2\/2.*My Favorites.*All enabled categories/);
        app.instance.onPause();
    });
}

test('category interval dropdowns restore saved values and save changes without starting a refresh', async () => {
    const app = harness(true);
    const visit = app.instance.onResume();
    app.elements['.refreshSummary'].handlers.click({ preventDefault() {} });
    app.reads[0].resolve({ SchemaVersion: 1, ConfigurationCompleted: true, RefreshSchedules: [{ Category: 'talk', IntervalHours: 24 }] });
    app.refresh[0].resolve({ SettingsLocked: false, Categories: [
        { Category: 'talk', Name: 'Talk' }, { Category: 'sports', Name: 'Sports' }] });
    await visit;
    const [talk, sports] = app.elements['.tuneInRefreshCategories'].childNodes
        .map(row => row.childNodes[1].childNodes[2]);
    assert.equal(talk.attributes['aria-label'], 'Talk refresh interval');
    assert.equal(talk.value, '24');
    assert.equal(sports.value, '0');
    talk.value = '0';
    talk.handlers.change();
    assert.ok(sports.childNodes.some(option => option.value === '720' && option.textContent === 'Every 30 days'));
    sports.value = '720';
    sports.handlers.change();
    app.formHandlers.submit({ preventDefault() {} });
    assert.equal(JSON.stringify(app.writes[0].config.RefreshSchedules), '[{"Category":"sports","IntervalHours":720}]');
    assert.ok(app.refresh.every(request => request.options.type === 'GET'));
});

test('returning to configured filtering collapses groups while unfinished filtering opens them', async () => {
    const app = harness();
    const sections = () => [app.elements['.categoriesSection'], app.elements['.stationSelectionsSection'],
        ...app.elements['.selectors'].childNodes];
    const first = app.instance.onResume();
    app.reads[0].resolve({ SchemaVersion: 1, ConfigurationCompleted: false });
    await first;
    for (const section of sections()) {
        const group = section === app.elements['.categoriesSection'] || section === app.elements['.stationSelectionsSection'];
        assert.equal(section.open, group);
        section.childNodes[0].handlers.click({ preventDefault() {} });
        assert.equal(section.open, !group);
    }
    app.instance.onPause();
    const second = app.instance.onResume();
    app.reads[1].resolve({ SchemaVersion: 1, ConfigurationCompleted: true });
    await second;
    assert.ok(sections().every(section => section.open === false));
    assert.equal(app.selectors.length, 0);
});

test('first save history is independent of the browsing checkbox and the settings lock', async () => {
    const app = harness(false, true);
    const visit = app.instance.onResume();
    app.reads[0].resolve({ SchemaVersion: 1, ConfigurationCompleted: false, SettingsSaved: false });
    await visit;
    const global = app.elements['.globalSection'];
    const categorySections = ['.browsingSection', '.categoriesSection', '.stationSelectionsSection']
        .map(name => app.elements[name]);
    const categoryPickers = () => app.elements['.selectors'].childNodes;
    assert.ok(categorySections.every(section => section.open));
    assert.ok(categoryPickers().every(section => !section.open));
    assert.equal(global.open, true);
    assert.equal(global.attributes['data-saved'], 'false');
    assert.equal(app.elements['.configurationCompleted'].checked, false);
    assert.equal(app.elements['.configurationFields'].disabled, true);
    assert.equal(app.elements['.hideRegionalGenres'].disabled, true);
    assert.equal(app.requestInterval.disabled, true);
    assert.equal(app.elements['.importDeadlineHours'].disabled, true);
    assert.equal(app.elements['.discoveryDeadlineHours'].disabled, true);
    await open(app.elements['.selectors'].childNodes[0].descendants().find(node => node.tagName === 'details'));
    assert.equal(app.selectors.length, 0);
    enableBrowsing(app);
    assert.equal(app.elements['.configurationFields'].disabled, false);
    assert.equal(app.elements['.hideRegionalGenres'].disabled, false);
    assert.equal(app.requestInterval.disabled, false);
    assert.equal(app.elements['.importDeadlineHours'].disabled, false);
    assert.equal(app.elements['.discoveryDeadlineHours'].disabled, false);
    assert.equal(app.elements['.refreshStart'].disabled, true);
    app.formHandlers.submit({ preventDefault() {} });
    app.writes[0].reject(new Error('save failed'));
    await settle();
    assert.equal(app.elements['.status'].attributes['data-error'], 'true');
    assert.equal(global.open, true);
    assert.equal(global.attributes['data-saved'], 'false');
    app.elements['.configurationCompleted'].checked = false;
    app.elements['.configurationCompleted'].handlers.change();
    app.formHandlers.submit({ preventDefault() {} });
    app.writes[1].resolve('saved');
    await settle();
    assert.equal(app.elements['.status'].attributes['data-error'], 'false');
    assert.equal(global.open, false);
    assert.equal(global.attributes['data-saved'], 'true');
    assert.equal(app.elements['.browsingSection'].open, true);
    assert.ok(categorySections.every(section => section.open));
    assert.ok(categoryPickers().every(section => !section.open));
    assert.equal(app.writes[1].config.ConfigurationCompleted, false);
    assert.equal(app.elements['.globalFields'].disabled, true);
    app.instance.onPause();
    const next = app.instance.onResume();
    app.reads[1].resolve({ SettingsSaved: true, SchemaVersion: 1, ConfigurationCompleted: false });
    await next;
    assert.equal(global.open, false);
    assert.equal(app.elements['.browsingSection'].open, true);
    assert.ok(categorySections.every(section => section.open));
    assert.ok(categoryPickers().every(section => !section.open));
    assert.equal(app.elements['.configurationFields'].disabled, true);
});

test('topic filters load lazily and save independent modes and selected IDs', async () => {
    const app = harness();
    const visit = app.instance.onResume();
    app.reads[0].resolve({});
    await visit;
    enableBrowsing(app);
    for (const [i, category, label, id] of [
        [3, 'talk', 'Talk Topics', 'c1'], [4, 'sports', 'Sports & Leagues', 'c2'],
        [5, 'podcast', 'Podcast Topics', 'c3']
    ]) {
        const [heading, mode, columns] = app.elements['.selectors'].childNodes[i].childNodes;
        assert.equal(heading.textContent, label);
        assert.equal(mode.value, 'all');
        const [picker, savedColumn] = columns.childNodes;
        const root = picker.childNodes.find(node => node.tagName === 'details');
        const savedChoices = savedColumn.childNodes.find(node => node.className === 'tuneInSelectorSaved');
        assert.ok(savedChoices);
        await open(root);
        assert.equal(app.selectors.length, i - 3);
        mode.value = 'selected';
        mode.handlers.change();
        const pending = open(root);
        assert.equal(app.selectors.at(-1).options.url.params.Category, category);
        app.selectors.at(-1).resolve({ Items: [{ Id: id, Name: 'Provider choice', CanSelect: true }] });
        await pending;
        root.descendants().find(node => node.textContent === 'Add').handlers.click();
        assert.match(savedChoices.textContent, /Provider choice/);
    }
    app.formHandlers.submit({ preventDefault() {} });
    await settle();
    const saved = app.writes[0].config;
    for (const [mode, ids, id] of [
        ['TalkMode', 'TalkIds', 'c1'], ['SportsMode', 'SportsIds', 'c2'], ['PodcastsMode', 'PodcastIds', 'c3']
    ]) {
        assert.equal(saved[mode], 'selected');
        assert.deepEqual(Array.from(saved[ids]), [id]);
    }
});

test('saved station choices require selection and a confirmation dialog before deletion', async () => {
    const app = harness();
    const visit = app.instance.onResume();
    app.reads[0].resolve({ SchemaVersion: 1, ConfigurationCompleted: true, TalkMode: 'selected',
        TalkIds: ['first', 'second'] });
    await visit;
    enableBrowsing(app);
    const columns = app.elements['.selectors'].childNodes[3].childNodes[2];
    const savedColumn = columns.childNodes[1];
    const deleteChoices = savedColumn.descendants().find(node => node.tagName === 'button');
    const dialog = savedColumn.descendants().find(node => node.tagName === 'dialog');
    assert.ok(deleteChoices && dialog);
    assert.equal(deleteChoices.textContent, 'Select Items for Delete');
    assert.match(deleteChoices.className, /\bbutton-submit\b/);
    assert.equal(deleteChoices.disabled, true);
    const choices = savedColumn.descendants().filter(node => node.tagName === 'input');
    assert.equal(choices.length, 2);
    choices[1].checked = true;
    choices[1].handlers.change();
    assert.equal(deleteChoices.textContent, 'Delete Selected Items');
    assert.equal(deleteChoices.disabled, false);
    deleteChoices.handlers.click();
    assert.equal(dialog.open, true);
    assert.match(dialog.textContent, /Delete 1 selected choice from Talk Topics/);
    assert.deepEqual(Array.from(app.instance.config.TalkIds), ['first', 'second']);
    dialog.descendants().find(node => node.tagName === 'button' && node.textContent === 'Cancel').handlers.click();
    assert.equal(dialog.open, false);
    assert.deepEqual(Array.from(app.instance.config.TalkIds), ['first', 'second']);
    deleteChoices.handlers.click();
    dialog.descendants().find(node => node.tagName === 'button' && node.textContent === 'Delete Selected Items').handlers.click();
    assert.deepEqual(Array.from(app.instance.config.TalkIds), ['first']);
    assert.equal(dialog.open, false);
    assert.equal(deleteChoices.textContent, 'Select Items for Delete');
    assert.equal(deleteChoices.disabled, true);
    assert.equal(savedColumn.descendants().filter(node => node.tagName === 'input').length, 1);
    assert.equal(app.elements['.stationDeletionNotice'].hidden, true);
    app.formHandlers.submit({ preventDefault() {} });
    await settle();
    assert.equal(app.elements['.stationDeletionNotice'].hidden, false);
    assert.match(app.elements['.stationDeletionNotice'].textContent, /Saved station selections are scheduled for deletion/);
    assert.match(app.elements['.stationDeletionNotice'].textContent, /Catalog Refresh/);
    assert.equal(app.elements['.tuneInRefresh'].open, true);
});

test('category outcomes keep their status text as well as color, and queued rows are neutral', async () => {
    const app = harness(true);
    const visit = app.instance.onResume();
    app.elements['.refreshSummary'].handlers.click({ preventDefault() {} });
    app.reads[0].resolve({ SchemaVersion: 1, ConfigurationCompleted: true });
    app.refresh[0].resolve({ SettingsLocked: false, QueuedRequests: ['music'], Categories: [
        { Category: 'talk', Name: 'Talk', State: 'Imported' },
        { Category: 'sports', Name: 'Sports', State: 'Partial (applied)' },
        { Category: 'podcast', Name: 'Podcasts', State: 'Failed' },
        { Category: 'music', Name: 'Music', State: 'Imported' }
    ] });
    await visit;
    const rows = app.elements['.tuneInRefreshCategories'].childNodes;
    assert.deepEqual(rows.map(row => row.attributes['data-outcome']), ['', 'Completed', 'Partial', 'Error']);
    assert.deepEqual(rows.map(row => row.childNodes[0].textContent),
        ['Music: Queued', 'Talk: Imported', 'Sports: Partial (applied)', 'Podcasts: Failed']);
});

test('Catalog Refresh aggregates category outcomes even while collapsed', async () => {
    const categories = states => states.map((state, index) => ({ Category: String(index), Name: String(index), State: state }));
    for (const [status, outcome, text] of [
        [{ State: 'Completed', Categories: categories(['Imported', 'Unchanged']) }, 'Completed', 'successful'],
        [{ State: 'Completed', Categories: categories(['Imported', 'Partial (applied)']) }, 'Partial', 'partial'],
        [{ State: 'Completed', Categories: categories(['Incomplete (left unchanged)', 'Failed']) }, 'Error', 'error'],
        [{ State: 'Error', Categories: categories(['Imported']) }, 'Error', 'error'],
        [{ State: 'TimedOut', Categories: [] }, 'Error', 'error'],
        [{ State: 'Partial', Categories: categories(['Imported']) }, 'Partial', 'partial'],
        [{ State: 'Completed', Categories: categories(['Imported', 'No recorded refresh']) }, '', 'not all categories confirmed successful'],
        [{ State: 'Completed', Categories: [] }, '', 'not all categories confirmed successful'],
        [{ State: 'Cancelled', Categories: categories(['Imported']) }, '', 'not all categories confirmed successful'],
        [{ State: 'Running', Busy: true, Categories: categories(['Discovering', 'Imported']) }, '', 'not all categories confirmed successful'],
        [{ State: 'Running', Busy: true, Categories: categories(['Discovering', 'Failed']) }, 'Error', 'error']
    ]) {
        const app = harness(true);
        const visit = app.instance.onResume();
        app.reads[0].resolve({ SchemaVersion: 1, ConfigurationCompleted: true });
        app.refresh[0].resolve({ ...status, SettingsLocked: false });
        await visit;
        await settle();
        assert.equal(app.elements['.tuneInRefresh'].attributes['data-outcome'], outcome, JSON.stringify(status));
        assert.equal(app.elements['.refreshOverall'].textContent, ' - Overall: ' + text);
        app.instance.onPause();
    }
    const html = fs.readFileSync(path.join(configDir, 'configPage.html'), 'utf8');
    for (const outcome of ['Completed', 'Partial', 'Error'])
        assert.ok(html.includes(`.tuneInRefresh[data-outcome="${outcome}"]`));
});

test('Catalog Refresh clears stale overall success when status becomes unavailable', async () => {
    const app = harness(true);
    const visit = app.instance.onResume();
    app.reads[0].resolve({ SchemaVersion: 1, ConfigurationCompleted: true });
    app.refresh[0].resolve({ State: 'Completed', SettingsLocked: false,
        Categories: [{ Category: 'talk', Name: 'Talk', State: 'Imported' }] });
    await visit;
    await settle();
    assert.equal(app.elements['.tuneInRefresh'].attributes['data-outcome'], 'Completed');
    [...app.timers.values()].find(timer => timer.delay === 10000).handler();
    app.refresh[1].reject(new Error('offline'));
    await settle();
    assert.equal(app.elements['.tuneInRefresh'].attributes['data-outcome'], '');
    assert.equal(app.elements['.refreshOverall'].textContent, ' - Overall: status unavailable');
    assert.equal(app.elements['.artworkGenresName'].textContent, 'Status unavailable');
    assert.equal(app.elements['.artworkGenresImages'].textContent, 'Unavailable');
    [...app.timers.values()].find(timer => timer.delay === 10000).handler();
    app.refresh[2].resolve({ State: 'Completed', SettingsLocked: false,
        Categories: [{ Category: 'talk', Name: 'Talk', State: 'Partial (unchanged)' }] });
    await settle();
    assert.equal(app.elements['.tuneInRefresh'].attributes['data-outcome'], 'Partial');
});

test('unlocking and the following status poll preserve manual disclosure choices', async () => {
    const app = harness(true);
    const visit = app.instance.onResume();
    app.reads[0].resolve({ SchemaVersion: 1, ConfigurationCompleted: true, SettingsSaved: true });
    app.refresh[0].resolve({ SettingsLocked: true, Categories: [] });
    await visit;
    await settle();
    const global = app.elements['.globalSection'];
    const filtering = app.elements['.browsingSection'];
    const categories = app.elements['.categoriesSection'];
    const selections = app.elements['.stationSelectionsSection'];
    global.open = false;
    filtering.open = false;
    categories.open = true;
    selections.open = false;
    const toggle = app.elements['.settingsLocked'];
    toggle.checked = false;
    const unlocking = toggle.handlers.change();
    app.locks.at(-1).resolve({ Locked: false });
    await unlocking;
    assert.deepEqual([global.open, filtering.open, categories.open, selections.open], [false, false, true, false]);
    app.refresh.at(-1).resolve({ SettingsLocked: false, Categories: [] });
    await settle();
    assert.deepEqual([global.open, filtering.open, categories.open, selections.open], [false, false, true, false]);
    app.instance.onPause();
});

test('settings lock protects pending edits and saves without blocking catalog refresh', async () => {
    const app = harness(true);
    app.elements['.tuneInRefresh'].open = true;
    const toggle = app.elements['.settingsLocked'];
    const save = app.elements['.button-submit'];
    const visit = app.instance.onResume();
    const categories = [{ Category: 'talk', Name: 'Talk', State: 'Imported' }];
    app.reads[0].resolve({ SchemaVersion: 1, ConfigurationCompleted: true, Languages: [{ Id: 'l1', Name: 'English', Enabled: true }] });
    await visit;
    assert.equal(toggle.disabled, true);
    assert.equal(save.disabled, true);
    assert.equal(app.elements['.loginFields'].disabled, true);
    app.refresh[0].resolve({ SettingsLocked: false, Categories: categories });
    await settle();
    assert.equal(save.disabled, false);
    assert.equal(app.elements['.loginFields'].disabled, false);
    const language = app.elements['.selectors'].childNodes[0];
    const selection = language.childNodes[2];
    const languageChoice = selection.descendants().find(item => item.tagName === 'input');
    languageChoice.checked = true;
    languageChoice.handlers.change();
    app.username.value = 'unsaved';
    toggle.checked = true;
    const locking = toggle.handlers.change();
    assert.deepEqual(JSON.parse(app.locks[0].options.data), { Locked: true });
    assert.equal(save.disabled, true);
    assert.equal(app.elements['.loginFields'].disabled, true);
    app.locks[0].resolve({ Locked: true });
    await locking;
    app.formHandlers.submit({ preventDefault() {} });
    selection.descendants().find(item => item.className && item.className.includes('tuneInSelectorDeleteTrigger'))
        .handlers.click();
    assert.notEqual(selection.descendants().find(item => item.tagName === 'dialog').open, true);
    assert.equal(app.writes.length, 0);
    assert.match(selection.textContent, /English/);
    assert.equal(app.elements['.refreshStart'].disabled, false);
    assert.equal(app.username.value, 'unsaved');
    app.refresh[1].resolve({ SettingsLocked: true, Categories: categories });
    await settle();
    toggle.checked = false;
    assert.equal(app.elements['.browsingSection'].open, false);
    const unlocking = toggle.handlers.change();
    app.locks[1].resolve({ Locked: false });
    await unlocking;
    assert.equal(app.elements['.browsingSection'].open, false);
    assert.equal(app.elements['.loginFields'].disabled, false);
    app.formHandlers.submit({ preventDefault() {} });
    assert.equal(app.writes[0].config.Username, 'unsaved');
});

for (const phase of ['discovery', 'import']) test('refresh status identifies the automatic ' + phase + ' retry', async () => {
    const app = harness(true);
    const visit = app.instance.onResume();
    app.reads[0].resolve({});
    app.refresh[0].resolve({
        State: 'Running', Phase: 'Importing', Category: 'music', Busy: true,
        AutomaticRetry: 2, AutomaticRetryLimit: 3,
        AutomaticRetryPhase: phase, AutomaticRetryReason: 'interruption', ImportTimeouts: 0,
        ImportMayHavePartiallyApplied: true
    });
    await visit;
    const text = app.elements['.refreshStatus'].textContent;
    assert.ok(text.includes('Running - Importing - Music. Automatic retry 2 of '
        + '3 after ' + phase + ' was interrupted'));
    assert.doesNotMatch(text, /timed out/);
    app.instance.onPause();
});

test('refresh summary keeps the state separate from expanded live discovery details', async () => {
    const app = harness(true);
    const visit = app.instance.onResume();
    app.reads[0].resolve({});
    app.refresh[0].resolve({
        State: 'Running', Phase: 'Discovering', Category: 'music', Busy: true,
        StartedUtc: new Date(Date.now() - 42000).toISOString(),
        LastProgressUtc: new Date(Date.now() - 9000).toISOString(),
        CatalogPagesCached: 1842, CatalogPagesLimit: 5000,
        CategoriesCompleted: 2, CategoriesTotal: 8,
        RequestsStarted: 25, RequestsCompleted: 24, CacheHits: 8,
        ActiveRequests: 1, RateLimitRetries: 2, Resumed: true,
        DiscoveryCheckpointUtc: new Date(Date.now() - 60000).toISOString()
    });
    await visit;
    app.elements['.refreshSummary'].handlers.click({ preventDefault() {} });
    assert.equal(app.elements['.tuneInRefresh'].open, false);
    assert.equal(app.elements['.refreshSummaryState'].textContent, ' - Running');
    assert.match(app.elements['.refreshSummaryDetails'].textContent,
        / - Discovering - Music - 1(?:,|\.|\s)?842 pages - progress \d+ sec ago/);
    const html = fs.readFileSync(path.join(configDir, 'configPage.html'), 'utf8');
    assert.match(html, /\.tuneInRefresh:not\(\[open\]\)\s*>\s*\.refreshSummary\s+\.refreshSummaryDetails\s*\{\s*display:\s*none/);
    assert.match(app.elements['.refreshElapsed'].textContent, /sec/);
    assert.match(app.elements['.refreshWork'].textContent,
        /1(?:,|\.|\s)?842 catalog pages \(limit 5(?:,|\.|\s)?000\); 2 \/ 8 categories checked/);
    assert.match(app.elements['.refreshLastProgress'].textContent, /\d+ sec ago/);
    assert.match(app.elements['.refreshDiagnosticsText'].textContent,
        /Provider requests: 25 attempts, 24 successful; 8 cached page replays; 1 active HTTP request; 2 rate-limit retries; Resumed from checkpoint/);
    app.instance.onPause();
});

test('plugin version label shows the running build reported by refresh status', async () => {
    const app = harness(true);
    const label = app.elements['.tuneInPluginVersion'];
    label.hidden = true;
    const visit = app.instance.onResume();
    app.reads[0].resolve({});
    app.refresh[0].resolve({ State: 'NeverRun', PluginVersion: 'v1.2.8.0 (e045a80)' });
    await visit;
    assert.equal(label.hidden, false);
    assert.equal(label.textContent, 'TuneIn plugin v1.2.8.0 (e045a80)');
    app.instance.onPause();
});

test('unsupported artwork packs show migration guidance without claiming verified identity', async () => {
    const app = harness(true);
    const visit = app.instance.onResume();
    app.reads[0].resolve({});
    app.refresh[0].resolve({ State: 'NeverRun', ArtworkPacks: [
        { Scope: 'genres', Status: 'unsupported', Name: 'Old trusted name', InstalledCount: 123, Path: 'C:\\art\\genres' }
    ] });
    await visit;
    assert.equal(app.elements['.artworkGenresName'].textContent,
        'Legacy or unsupported pack: install a schema 2 pack; existing files are unchanged');
    assert.equal(app.elements['.artworkGenresImages'].textContent, '123 installed');
    assert.equal(app.elements['.artworkGenresChanges'].textContent, '(change counts unavailable)');
    assert.equal(app.elements['.artworkGenresPath'].textContent, 'C:\\art\\genres');
    app.instance.onPause();
});

test('scoped artwork packs show detected names, counts and paths safely', async () => {
    const app = harness(true);
    const visit = app.instance.onResume();
    app.reads[0].resolve({});
    app.refresh[0].resolve({ State: 'NeverRun', artworkPacks: [
        { scope: 'genres', status: 'identified', name: '<img src=x onerror=alert(1)>', installedCount: 123,
            matchedCount: 120, modifiedCount: 2, addedCount: 1, path: 'C:\\art\\genres' },
        { scope: 'languages', status: 'missing', installedCount: 0, matchedCount: 0,
            modifiedCount: 0, addedCount: 0, path: 'C:\\art\\languages' },
        { scope: 'locations', status: 'invalid', installedCount: 4, matchedCount: 3,
            modifiedCount: 1, addedCount: 1, path: 'C:\\art\\locations' },
        { scope: 'categories', status: 'identified', name: 'Category cards', installedCount: 8,
            matchedCount: 8, modifiedCount: 0, addedCount: 0, path: 'C:\\art\\categories' }
    ] });
    await visit;
    assert.equal(app.elements['.artworkGenresName'].textContent, '<img src=x onerror=alert(1)>');
    assert.equal(app.elements['.artworkGenresName'].childNodes.length, 0);
    assert.equal(app.elements['.artworkGenresImages'].textContent, '123 installed, 120 matched');
    assert.equal(app.elements['.artworkGenresPath'].textContent, 'C:\\art\\genres');
    assert.equal(app.elements['.artworkGenresChanges'].textContent, '(2 modified, 1 added)');
    assert.equal(app.elements['.artworkLanguagesName'].textContent, 'Default artwork');
    assert.equal(app.elements['.artworkLocationsName'].textContent, 'Invalid pack metadata');
    assert.equal(app.elements['.artworkLocationsImages'].textContent, '4 installed, 3 matched');
    assert.equal(app.elements['.artworkLocationsChanges'].textContent, '(change counts unavailable)');
    assert.equal(app.elements['.artworkLanguagesChanges'].textContent, '(change counts unavailable)');
    assert.equal(app.elements['.artworkCategoriesName'].textContent, 'Category cards');
    assert.equal(app.elements['.artworkCategoriesImages'].textContent, '8 installed, 8 matched');
    assert.equal(app.elements['.artworkCategoriesPath'].textContent, 'C:\\art\\categories');
    app.instance.onPause();
    const html = fs.readFileSync(path.join(configDir, 'configPage.html'), 'utf8');
    assert.match(html, /class="tuneInArtworkTableScroll" role="region" aria-label="[^"]+" tabindex="0">\s*<table/);
    assert.match(html, /\.tuneInArtworkTableScroll\s*\{[^}]*overflow-x:\s*auto/);
    assert.match(html, /\.tuneInArtworkPacks th,\s*\.tuneInArtworkPacks td\s*\{[^}]*white-space:\s*nowrap/);
    assert.match(html, /class="hideRegionalGenres"[\s\S]*?<details class="tuneInArtworkPacks">\s*<summary>Custom Artwork Packs<\/summary>[\s\S]*?<\/details>\s*<fieldset class="globalFields advancedFields"[^>]*>\s*<details class="tuneInAdvancedSettings">/);
    assert.equal((html.match(/<details class="tuneInArtworkPacks">/g) || []).length, 1);
    assert.match(html, /<fieldset class="loginFields" disabled>\s*<legend>TuneIn Favorites and Location<\/legend>/);
    assert.match(html, /\.loginFields\s*\{[^}]*border:\s*1px solid var\(--tunein-neutral-border\)/);
    const artworkStyle = html.match(/\.tuneInArtworkPacks\s*\{([^}]*)\}/)[1];
    assert.match(artworkStyle, /border-inline-start:\s*\.25em solid #39b9aa/);
    assert.doesNotMatch(artworkStyle, /\bbackground(?:-color)?\s*:/);
    assert.match(html, /<th scope="col">Artwork<\/th><th scope="col">Installed pack<\/th><th scope="col">Images<\/th><th scope="col">Server folder<\/th>/);
    for (const scope of ['Genres', 'Languages', 'Locations', 'Categories'])
        assert.match(html, new RegExp(`<strong class="artwork${scope}Name">Loading\\.\\.\\.<\\/strong>`));
    assert.match(html, /<summary>Details &amp; installation<\/summary>/);
    assert.match(html, /docs\/samples\/cartoon\.jpg/);
});

function dispatchFormControlEvent(app, control, type) {
    let stopped = false;
    const event = { target: control, stopPropagation() { stopped = true; } };
    const handler = control.handlers[type];
    if (handler) handler.call(control, event);
    if (!stopped) app.formHandlers[type](event);
}

async function prepareArtworkUpload(app, scanStatus = 'Not scanned', config = {}) {
    const categories = [
        { Category: 'music', Name: 'Music', State: 'Imported' },
        { Category: 'language', Name: 'By Language', State: 'Imported' },
        { Category: 'talk', Name: 'Talk', State: 'Imported' }
    ];
    const status = { State: 'Completed', Categories: categories, HasCompleteBaseline: true,
        ChannelRegistered: true, QueueSlotsAvailable: 2, QueuedRequests: [] };
    const visit = app.instance.onResume();
    app.reads[0].resolve(config);
    app.refresh[0].resolve(status);
    await visit;
    const file = app.elements['.artworkFile'];
    file.files = [new File([new Uint8Array([80, 75, 3, 4])], 'pack.zip', { type: 'application/zip' })];
    dispatchFormControlEvent(app, file, 'input');
    dispatchFormControlEvent(app, file, 'change');
    assert.equal(app.elements['.artworkVerifyDialog'].open, true);
    assert.match(app.elements['.artworkVerifyText'].textContent, /pack\.zip/);
    assert.equal(app.artwork.some(request => request.options.url.route === 'TuneIn/Artwork/Upload'), false);
    const upload = app.elements['.artworkVerifyStart'].click();
    const sent = app.artwork.find(request => request.options.url.route === 'TuneIn/Artwork/Upload').options;
    assert.equal(sent.body, file.files[0]);
    assert.equal(sent.headers.Authorization, 'test-session');
    assert.equal(sent.headers['Content-Type'], 'application/zip');
    assert.equal(sent.credentials, 'same-origin');
    app.artwork.find(request => request.options.url.route === 'TuneIn/Artwork/Status').resolve({ State: 'validating' });
    app.artwork.find(request => request.options.url.route === 'TuneIn/Artwork/Upload').resolve({
        Token: 'abc', Scope: 'genres', Name: 'Sample', ImageCount: 2, Destination: 'C:\\art\\genres', ScanStatus: scanStatus
    });
    await upload;
    return status;
}

test('artwork upload polls its background operation before offering installation', async () => {
    const app = harness(true);
    const visit = app.instance.onResume();
    app.reads[0].resolve({});
    app.refresh[0].resolve({ State: 'NeverRun', Categories: [] });
    await visit;
    const file = app.elements['.artworkFile'];
    file.files = [new File(['test'], 'pack.zip', { type: 'application/zip' })];
    file.handlers.change.call(file, { stopPropagation() {} });
    const upload = app.elements['.artworkVerifyStart'].click();
    app.artwork.find(request => request.options.url.route === 'TuneIn/Artwork/Status').resolve({ State: 'uploading' });
    app.artwork.find(request => request.options.url.route === 'TuneIn/Artwork/Upload').resolve({ Operation: 'operation1' });
    await settle();
    const first = app.artwork.filter(request => request.options.url.params && request.options.url.params.Operation === 'operation1');
    assert.equal(first.length, 1);
    first[0].resolve({ State: 'validating' });
    await settle();
    assert.match(app.elements['.artworkUploadMessage'].textContent, /Validating/);
    assert.notEqual(app.elements['.artworkConfirmDialog'].open, true);
    [...app.timers.values()].find(timer => timer.delay === 1500).handler();
    await settle();
    const second = app.artwork.filter(request => request.options.url.params && request.options.url.params.Operation === 'operation1');
    assert.equal(second.length, 2);
    second[1].resolve({ State: 'awaiting confirmation', Result: {
        Token: 'abc', Scope: 'locations', Name: 'Sample', ImageCount: 2032,
        Destination: 'C:\\art\\locations', ScanStatus: 'Not scanned'
    } });
    await upload;
    assert.equal(app.elements['.artworkConfirmDialog'].open, true);
    assert.equal(app.elements['.artworkInstall'].disabled, true);
    app.instance.onPause();
});

test('background artwork validation errors remain visible and do not offer installation', async () => {
    const app = harness(true);
    const visit = app.instance.onResume();
    app.reads[0].resolve({});
    app.refresh[0].resolve({ State: 'NeverRun', Categories: [] });
    await visit;
    const file = app.elements['.artworkFile'];
    file.files = [new File(['test'], 'bad.zip', { type: 'application/zip' })];
    file.handlers.change.call(file, { stopPropagation() {} });
    const upload = app.elements['.artworkVerifyStart'].click();
    app.artwork.find(request => request.options.url.route === 'TuneIn/Artwork/Status').resolve({ State: 'uploading' });
    app.artwork.find(request => request.options.url.route === 'TuneIn/Artwork/Upload').resolve({ Operation: 'operation2' });
    await settle();
    app.artwork.find(request => request.options.url.params && request.options.url.params.Operation === 'operation2')
        .resolve({ State: 'error', Message: 'Scanner failed.' });
    await upload;
    assert.match(app.elements['.artworkUploadMessage'].textContent, /Scanner failed/);
    assert.notEqual(app.elements['.artworkConfirmDialog'].open, true);
    assert.equal(app.elements['.artworkUpload'].disabled, false);
    app.instance.onPause();
});

test('leaving during background validation discards the completed candidate', async () => {
    const app = harness(true);
    const visit = app.instance.onResume();
    app.reads[0].resolve({});
    app.refresh[0].resolve({ State: 'NeverRun', Categories: [] });
    await visit;
    const file = app.elements['.artworkFile'];
    file.files = [new File(['test'], 'pack.zip', { type: 'application/zip' })];
    file.handlers.change.call(file, { stopPropagation() {} });
    const upload = app.elements['.artworkVerifyStart'].click();
    app.artwork.find(request => request.options.url.route === 'TuneIn/Artwork/Status').resolve({ State: 'uploading' });
    app.artwork.find(request => request.options.url.route === 'TuneIn/Artwork/Upload').resolve({ Operation: 'operation3' });
    await settle();
    const status = app.artwork.find(request => request.options.url.params && request.options.url.params.Operation === 'operation3');
    app.instance.onPause();
    status.resolve({ State: 'awaiting confirmation', Result: {
        Token: 'abc', Scope: 'locations', Name: 'Sample', ImageCount: 1, ScanStatus: 'Clean'
    } });
    await settle();
    const discard = app.artwork.find(request => request.options.url.route === 'TuneIn/Artwork/Discard');
    assert.equal(JSON.parse(discard.options.data).Token, 'abc');
    discard.resolve({});
    await upload;
    assert.notEqual(app.elements['.artworkConfirmDialog'].open, true);
});

test('artwork selection can be cancelled before upload and reports plain-text validation errors', async () => {
    const app = harness(true);
    const visit = app.instance.onResume();
    app.reads[0].resolve({});
    app.refresh[0].resolve({ State: 'NeverRun', Categories: [] });
    await visit;
    const file = app.elements['.artworkFile'];
    file.files = [new File(['test'], 'example.zip', { type: 'application/zip' })];
    file.handlers.change.call(file, { stopPropagation() {} });
    assert.equal(app.elements['.artworkVerifyDialog'].open, true);
    app.elements['.artworkVerifyCancel'].click();
    assert.equal(app.elements['.artworkVerifyDialog'].open, false);
    assert.equal(app.artwork.length, 0);
    file.handlers.change.call(file, { stopPropagation() {} });
    const upload = app.elements['.artworkVerifyStart'].click();
    app.artwork.find(request => request.options.url.route === 'TuneIn/Artwork/Status').resolve({ State: 'validating' });
    app.artwork.find(request => request.options.url.route === 'TuneIn/Artwork/Upload')
        .resolve({ __httpStatus: 500, body: 'Scanner unavailable; artwork was not scanned.' });
    await upload;
    assert.match(app.elements['.artworkUploadMessage'].textContent, /Scanner unavailable; artwork was not scanned/);
    assert.equal(app.elements['.artworkUploadMessage'].attributes['data-error'], 'true');
    assert.equal(app.elements['.artworkUpload'].disabled, false);
    file.handlers.change.call(file, { stopPropagation() {} });
    const retry = app.elements['.artworkVerifyStart'].click();
    assert.equal(app.elements['.artworkUploadMessage'].attributes['data-error'], 'false');
    app.artwork.filter(request => request.options.url.route === 'TuneIn/Artwork/Status').at(-1).resolve({ State: 'validating' });
    app.artwork.filter(request => request.options.url.route === 'TuneIn/Artwork/Upload').at(-1)
        .resolve({ __httpStatus: 500, body: 'Scanner unavailable.' });
    await retry;
    app.instance.onPause();
});

test('cancelling artwork verification does not create an unsaved settings warning', async () => {
    const app = harness(true);
    const visit = app.instance.onResume();
    app.reads[0].resolve({ SchemaVersion: 1, ConfigurationCompleted: true, SettingsSaved: true });
    app.refresh[0].resolve({ State: 'Completed', Categories: [], HasCompleteBaseline: true, ChannelRegistered: true });
    await visit;
    const file = app.elements['.artworkFile'];
    file.files = [new File(['test'], 'example.zip')];
    dispatchFormControlEvent(app, file, 'input');
    dispatchFormControlEvent(app, file, 'change');
    app.elements['.artworkVerifyCancel'].click();
    assert.equal(app.instance.setupDirty, false);
    assert.equal(app.elements['.settingsChangeNotice'].hidden, true);
    assert.equal(app.writes.length, 0);
    assert.equal(app.artwork.length, 0);
    app.instance.onPause();
});

for (const format of ['json', 'text']) {
    test(`schema-1 upload shows the newer-pack notice from a ${format} error without offering installation`, async () => {
        const app = harness(true);
        const visit = app.instance.onResume();
        app.reads[0].resolve({});
        app.refresh[0].resolve({ State: 'NeverRun', Categories: [] });
        await visit;
        const file = app.elements['.artworkFile'];
        file.files = [new File(['legacy'], 'old-pack.zip', { type: 'application/zip' })];
        file.handlers.change.call(file, { stopPropagation() {} });
        const upload = app.elements['.artworkVerifyStart'].click();
        const message = 'This artwork pack uses schema 1, which is no longer supported. Please regenerate or download a newer pack using schemaVersion 2. Existing installed artwork is unchanged.';
        app.artwork.find(request => request.options.url.route === 'TuneIn/Artwork/Status').resolve({ State: 'validating' });
        app.artwork.find(request => request.options.url.route === 'TuneIn/Artwork/Upload').resolve({
            __httpStatus: 400,
            body: format === 'json' ? JSON.stringify({ ResponseStatus: { ErrorCode: 'NotSupportedException', Message: message } }) : message
        });
        await upload;
        const notice = app.elements['.artworkUploadMessage'];
        assert.equal(notice.textContent, 'Artwork upload failed: ' + message);
        assert.equal(notice.attributes['data-error'], 'true');
        assert.equal(notice.childNodes.length, 0);
        assert.ok(!app.elements['.artworkConfirmDialog'].open);
        assert.ok(!app.instance.artworkPending);
        assert.equal(app.artwork.some(request => request.options.url.route === 'TuneIn/Artwork/Install'), false);
        assert.equal(app.elements['.artworkUpload'].disabled, false);
        assert.match(fs.readFileSync(path.join(configDir, 'configPage.html'), 'utf8'),
            /class="artworkUploadMessage" role="status" aria-live="polite"/);
        app.instance.onPause();
    });
}

test('artwork selection is blocked if refresh status becomes unavailable', async () => {
    const app = harness(true);
    const visit = app.instance.onResume();
    app.reads[0].resolve({});
    app.refresh[0].resolve({ State: 'NeverRun', Categories: [] });
    await visit;
    app.instance.refreshAvailable = false;
    const file = app.elements['.artworkFile'];
    file.files = [new File(['test'], 'example.zip')];
    file.handlers.change.call(file, { stopPropagation() {} });
    assert.ok(!app.elements['.artworkVerifyDialog'].open);
    assert.equal(app.artwork.length, 0);
    assert.match(app.elements['.artworkUploadMessage'].textContent, /Unlock settings and wait/);
    app.instance.onPause();
});

test('locked settings disable artwork selection and reject a stale file change', async () => {
    const app = harness(true);
    const visit = app.instance.onResume();
    app.reads[0].resolve({ SettingsSaved: true });
    app.refresh[0].resolve({ State: 'NeverRun', Categories: [], SettingsLocked: true });
    await visit;
    assert.equal(app.elements['.artworkUpload'].disabled, true);
    assert.equal(app.elements['.artworkFile'].disabled, true);
    const file = app.elements['.artworkFile'];
    file.files = [new File(['test'], 'example.zip')];
    file.handlers.change.call(file, { stopPropagation() {} });
    assert.ok(!app.elements['.artworkVerifyDialog'].open);
    assert.equal(app.artwork.length, 0);
    assert.match(app.elements['.artworkUploadMessage'].textContent, /Unlock settings/);
    app.instance.onPause();
});

test('leaving during artwork verification closes it without uploading', async () => {
    const app = harness(true);
    const visit = app.instance.onResume();
    app.reads[0].resolve({});
    app.refresh[0].resolve({ State: 'NeverRun', Categories: [] });
    await visit;
    const file = app.elements['.artworkFile'];
    file.files = [new File(['test'], 'example.zip')];
    file.handlers.change.call(file, { stopPropagation() {} });
    assert.equal(app.elements['.artworkVerifyDialog'].open, true);
    app.instance.onPause();
    assert.equal(app.elements['.artworkVerifyDialog'].open, false);
    assert.equal(app.artwork.length, 0);
});

test('read-only artwork checks cannot inherit disabled settings fieldsets', () => {
    const html = fs.readFileSync(path.join(configDir, 'configPage.html'), 'utf8');
    const fieldsets = [];
    const ancestry = new Map();
    for (const match of html.matchAll(/<\/?fieldset\b[^>]*>|<(?:button|input)\b[^>]*>/g)) {
        const tag = match[0];
        const classes = /class="([^"]*)"/.exec(tag);
        if (tag.startsWith('</fieldset')) fieldsets.pop();
        else if (tag.startsWith('<fieldset')) fieldsets.push(classes ? classes[1] : '');
        else if (classes) for (const name of classes[1].split(' ')) ancestry.set(name, [...fieldsets]);
    }
    assert.deepEqual(ancestry.get('optionalChecksRun'), []);
    assert.deepEqual(ancestry.get('txtUsername'), ['globalFields', 'loginFields']);
    assert.deepEqual(ancestry.get('configurationCompleted'), ['globalFields']);
    assert.deepEqual(ancestry.get('maxStationCount'), ['globalFields advancedFields']);
});

test('artwork dialogs remain actionable when settings are locked and approval uses a visible native checkbox', () => {
    const html = fs.readFileSync(path.join(configDir, 'configPage.html'), 'utf8');
    let fieldsetDepth = 0;
    const dialogs = [];
    for (const match of html.matchAll(/<\/?fieldset\b[^>]*>|<dialog\b[^>]*>/g)) {
        if (match[0].startsWith('</fieldset')) fieldsetDepth--;
        else if (match[0].startsWith('<fieldset')) fieldsetDepth++;
        else if (/class="tuneInRecoveryDialog artwork(?:Confirm|Refresh)Dialog"/.test(match[0])) {
            dialogs.push(match[0]);
            assert.equal(fieldsetDepth, 0, 'artwork dialogs must not inherit a disabled fieldset');
        }
    }
    assert.equal(dialogs.length, 2);
    assert.match(html, /<label class="artworkUnscannedLabel" hidden>\s*<input type="checkbox" class="artworkApproveUnscanned"\s*\/>/);
    assert.match(html, /\.artworkUnscannedLabel \.artworkApproveUnscanned\s*\{[^}]*appearance:\s*auto;[^}]*opacity:\s*1;/);
});

test('artwork installation requires unscanned approval and Later never starts refresh', async () => {
    const app = harness(true);
    const status = await prepareArtworkUpload(app);
    assert.equal(app.elements['.artworkConfirmDialog'].open, true);
    assert.match(app.elements['.artworkConfirmText'].textContent, /Sample.*genres.*2 images.*Not scanned/);
    assert.equal(app.elements['.artworkInstall'].disabled, true);
    app.elements['.artworkInstall'].click();
    assert.equal(app.artwork.some(value => value.options.url.route === 'TuneIn/Artwork/Install'), false);
    const approve = app.elements['.artworkApproveUnscanned'];
    approve.checked = true;
    dispatchFormControlEvent(app, approve, 'input');
    dispatchFormControlEvent(app, approve, 'change');
    const install = app.elements['.artworkInstall'].click();
    assert.equal(app.elements['.globalFields'].disabled, true);
    const request = app.artwork.find(value => value.options.url.route === 'TuneIn/Artwork/Install');
    assert.equal(JSON.parse(request.options.data).ApproveUnscanned, true);
    request.resolve({ Scope: 'genres', AffectedCategories: ['music', 'language'] });
    await settle();
    app.refresh[1].resolve({ ...status, SettingsLocked: true });
    await install;
    assert.match(app.elements['.artworkRefreshText'].textContent, /Music, By Language/);
    assert.equal(app.elements['.globalFields'].disabled, true);
    app.elements['.artworkRefreshLater'].click();
    assert.equal(app.elements['.artworkRefreshDialog'].open, false);
    assert.equal(app.refresh.filter(value => value.options.type === 'POST').length, 0);
    assert.match(app.elements['.artworkUploadMessage'].textContent, /No refresh was started/);
    app.instance.onPause();
});

for (const choice of ['Now', 'Later']) {
    for (const hasSettingsEdit of [false, true]) {
        test(`artwork refresh ${choice} ${hasSettingsEdit ? 'preserves genuine unsaved settings' : 'does not mark settings dirty'}`, async () => {
            const app = harness(true);
            const status = await prepareArtworkUpload(app, 'Not scanned',
                { SchemaVersion: 1, ConfigurationCompleted: true, SettingsSaved: true, EnableTalk: true });
            assert.equal(app.instance.setupDirty, false);
            assert.equal(app.elements['.settingsChangeNotice'].hidden, true);
            if (hasSettingsEdit) {
                app.requestInterval.value = '4';
                dispatchFormControlEvent(app, app.requestInterval, 'input');
            }
            const approve = app.elements['.artworkApproveUnscanned'];
            approve.checked = true;
            dispatchFormControlEvent(app, approve, 'input');
            dispatchFormControlEvent(app, approve, 'change');
            assert.equal(app.instance.setupDirty, hasSettingsEdit);
            assert.equal(app.elements['.settingsChangeNotice'].hidden, !hasSettingsEdit);
            const install = app.elements['.artworkInstall'].click();
            app.artwork.find(value => value.options.url.route === 'TuneIn/Artwork/Install')
                .resolve({ Scope: 'genres', AffectedCategories: ['music'] });
            await settle();
            app.refresh[1].resolve({ ...status, SettingsLocked: true });
            await install;
            assert.equal(app.elements['.artworkRefreshDialog'].open, true);
            assert.equal(app.instance.setupDirty, hasSettingsEdit);
            assert.equal(app.elements['.settingsChangeNotice'].hidden, !hasSettingsEdit);
            const refresh = app.elements['.artworkRefresh' + choice].click();
            const starts = app.refresh.filter(value => value.options.type === 'POST');
            if (choice === 'Now') {
                assert.equal(starts.length, 1);
                starts[0].resolve({ ...status, SettingsLocked: true, Busy: true, ActiveRequest: 'music' });
                await refresh;
            } else assert.equal(starts.length, 0);
            assert.equal(app.instance.setupDirty, hasSettingsEdit);
            assert.equal(app.elements['.settingsChangeNotice'].hidden, !hasSettingsEdit);
            assert.equal(app.writes.length, 0);
            if (hasSettingsEdit) assert.equal(app.requestInterval.value, '4');
            app.instance.onPause();
        });
    }
}

test('artwork installation is blocked if settings lock after verification', async () => {
    const app = harness(true);
    await prepareArtworkUpload(app, 'Clean');
    app.instance.settingsLocked = true;
    app.elements['.artworkInstall'].click();
    assert.equal(app.elements['.artworkConfirmDialog'].open, true);
    assert.match(app.elements['.artworkConfirmError'].textContent, /Unlock settings/);
    assert.equal(app.artwork.some(value => value.options.url.route === 'TuneIn/Artwork/Install'), false);
    app.instance.onPause();
});

test('artwork installation defers status polling and ignores an earlier failed poll', async () => {
    const app = harness(true);
    const status = await prepareArtworkUpload(app, 'Clean');
    const initialTimer = [...app.timers.values()].find(item => item.delay === 10000);
    assert.ok(initialTimer);
    initialTimer.handler();
    const stale = app.refresh[1];
    assert.equal(stale.options.type, 'GET');
    const install = app.elements['.artworkInstall'].click();
    stale.reject(new Error('stale status failure'));
    await settle();
    await settle();
    assert.notEqual(app.elements['.settingsLockStatus'].attributes['data-error'], 'true');
    assert.equal(app.refresh.length, 2);
    const timer = [...app.timers.values()].find(item => item.delay === 10000);
    assert.ok(timer);
    timer.handler();
    await settle();
    assert.equal(app.refresh.length, 2);
    app.artwork.find(value => value.options.url.route === 'TuneIn/Artwork/Install')
        .resolve({ Scope: 'genres', AffectedCategories: ['music'] });
    await settle();
    app.refresh[2].resolve({ ...status, SettingsLocked: true });
    await install;
    assert.equal(app.elements['.artworkRefreshDialog'].open, true);
    assert.notEqual(app.elements['.settingsLockStatus'].attributes['data-error'], 'true');
    app.instance.onPause();
});

test('artwork installation still reports and retries a post-install status failure', async () => {
    const app = harness(true);
    await prepareArtworkUpload(app, 'Clean');
    const install = app.elements['.artworkInstall'].click();
    app.artwork.find(value => value.options.url.route === 'TuneIn/Artwork/Install')
        .resolve({ Scope: 'genres', AffectedCategories: ['music'] });
    await settle();
    app.refresh[1].reject(new Error('status unavailable'));
    await install;
    assert.equal(app.elements['.artworkRefreshDialog'].open, true);
    assert.match(app.elements['.artworkRefreshText'].textContent, /Could not load affected categories/);
    assert.equal(app.elements['.artworkRefreshText'].attributes['data-error'], 'true');
    assert.equal(app.refresh[2].options.type, 'GET');
    app.refresh[2].reject(new Error('still unavailable'));
    await settle();
    await settle();
    assert.match(app.elements['.settingsLockStatus'].textContent, /Could not read the settings lock/);
    app.instance.onPause();
});

test('artwork refresh queues affected enabled categories including language for genres', async () => {
    const app = harness(true);
    const status = await prepareArtworkUpload(app, 'Clean');
    const install = app.elements['.artworkInstall'].click();
    app.artwork.find(value => value.options.url.route === 'TuneIn/Artwork/Install')
        .resolve({ Scope: 'genres', AffectedCategories: ['music', 'language'] });
    await settle();
    app.refresh[1].resolve({ ...status, SettingsLocked: true });
    await install;
    assert.equal(app.elements['.globalFields'].disabled, true);
    const refresh = app.elements['.artworkRefreshNow'].click();
    assert.equal(app.elements['.artworkRefreshDialog'].open, false);
    assert.equal(app.elements['.refreshSummary'].focused, true);
    assert.equal(app.elements['.refreshSummary'].scrollOptions.block, 'start');
    const starts = () => app.refresh.filter(value => value.options.type === 'POST');
    assert.equal(starts()[0].options.url.params.Category, 'music');
    starts()[0].resolve({ ...status, Busy: true, ActiveRequest: 'music', QueueSlotsAvailable: 2 });
    await settle();
    assert.equal(starts()[1].options.url.params.Category, 'language');
    starts()[1].resolve({ ...status, Busy: true, ActiveRequest: 'music', QueueSlotsAvailable: 1, QueuedRequests: ['language'] });
    await refresh;
    assert.match(app.elements['.artworkUploadMessage'].textContent, /started or queued/);
    assert.equal(starts().length, 2);
    assert.equal(app.elements['.refreshSummary'].scrollCount, 2);
    app.instance.onPause();
});

test('artwork refresh reports every affected category that could not start', async () => {
    const app = harness(true);
    const status = await prepareArtworkUpload(app, 'Clean');
    const install = app.elements['.artworkInstall'].handlers.click();
    app.artwork.find(value => value.options.url.route === 'TuneIn/Artwork/Install')
        .resolve({ Scope: 'genres', AffectedCategories: ['music', 'language'] });
    await settle();
    app.refresh[1].resolve(status);
    await install;
    const refresh = app.elements['.artworkRefreshNow'].handlers.click();
    app.elements['.maintenanceSummary'].focus();
    app.refresh.find(value => value.options.type === 'POST').reject(new Error('unavailable'));
    await refresh;
    assert.match(app.elements['.artworkUploadMessage'].textContent, /Pack installed.*Music, By Language/);
    assert.match(app.elements['.refreshError'].textContent, /Pack installed.*Music, By Language/);
    assert.equal(app.elements['.refreshError'].attributes['data-error'], 'true');
    assert.equal(app.elements['.refreshSummary'].scrollCount, 1);
    assert.equal(app.refresh.filter(value => value.options.type === 'POST').length, 1);
    app.instance.onPause();
});

test('artwork refresh moves to visible baseline requirements without bypassing the first-refresh gate', async () => {
    const app = harness(true);
    const status = await prepareArtworkUpload(app, 'Clean');
    const install = app.elements['.artworkInstall'].click();
    app.artwork.find(value => value.options.url.route === 'TuneIn/Artwork/Install')
        .resolve({ Scope: 'genres', AffectedCategories: ['music', 'language'] });
    await settle();
    app.refresh[1].resolve({ ...status, SettingsLocked: true, HasCompleteBaseline: false });
    await install;
    app.elements['.artworkRefreshNow'].click();
    assert.equal(app.instance.refreshSection.open, true);
    assert.equal(app.elements['.refreshSummary'].focused, true);
    assert.match(app.elements['.refreshError'].textContent, /Music, By Language.*Refresh all enabled categories.*baseline/);
    assert.equal(app.refresh.filter(value => value.options.type === 'POST').length, 0);
    assert.equal(app.instance.settingsLocked, true);
    app.instance.onPause();
});

test('upload completion after leaving the page discards staging and re-enables upload on return', async () => {
    const app = harness(true);
    const visit = app.instance.onResume();
    app.reads[0].resolve({});
    app.refresh[0].resolve({ State: 'NeverRun', Categories: [] });
    await visit;
    const file = app.elements['.artworkFile'];
    file.files = [{ name: 'pack.zip', size: 12 }];
    file.handlers.change.call(file, { stopPropagation() {} });
    const upload = app.elements['.artworkVerifyStart'].click();
    app.instance.onPause();
    const revisit = app.instance.onResume();
    app.reads[1].resolve({});
    app.refresh[1].resolve({ State: 'NeverRun', Categories: [] });
    await revisit;
    assert.equal(app.elements['.artworkUpload'].disabled, true);
    app.artwork.find(request => request.options.url.route === 'TuneIn/Artwork/Upload')
        .resolve({ Token: 'abc', ScanStatus: 'Clean' });
    await settle();
    app.artwork.find(request => request.options.url.route === 'TuneIn/Artwork/Discard').resolve({});
    await upload;
    assert.equal(app.elements['.artworkUpload'].disabled, false);
    assert.equal(app.elements['.artworkConfirmDialog'].open, undefined);
    app.instance.onPause();
});

test('artwork status handles missing, unavailable and incomplete server data without retaining stale values', async () => {
    const app = harness(true);
    const visit = app.instance.onResume();
    app.reads[0].resolve({});
    app.refresh[0].resolve({ State: 'NeverRun', ArtworkPacks: [
        { Scope: 'MusicGenres', Status: 'identified', Name: 'Cartoon', InstalledCount: 2,
            MatchedCount: null, ModifiedCount: null, AddedCount: null, Path: 'C:\\art' },
        { Scope: 'language', Status: 'missing', InstalledCount: 2, ModifiedCount: 0, AddedCount: 0 },
        { Scope: 'location', Status: 'unavailable', ModifiedCount: 0, AddedCount: 0 }
    ] });
    await visit;
    assert.equal(app.elements['.artworkGenresName'].textContent, 'Cartoon');
    assert.equal(app.elements['.artworkGenresImages'].textContent, '2 installed');
    assert.equal(app.elements['.artworkGenresChanges'].textContent, '(change counts unavailable)');
    assert.equal(app.elements['.artworkLanguagesName'].textContent, 'No pack metadata');
    assert.equal(app.elements['.artworkLanguagesChanges'].textContent, '(change counts unavailable)');
    assert.equal(app.elements['.artworkLocationsName'].textContent, 'Status unavailable');
    assert.equal(app.elements['.artworkLocationsImages'].textContent, 'Unavailable');
    assert.equal(app.elements['.artworkLocationsPath'].textContent, 'Folder unavailable');
    assert.equal(app.elements['.artworkLocationsChanges'].textContent, '(change counts unavailable)');
    app.instance.onPause();
    const again = app.instance.onResume();
    app.reads[1].resolve({});
    app.refresh[1].resolve({ State: 'NeverRun', ArtworkFolder: 'C:\\legacy', ArtworkCards: 0 });
    await again;
    assert.equal(app.elements['.artworkGenresName'].textContent, 'Status unavailable');
    assert.equal(app.elements['.artworkGenresPath'].textContent, 'Folder unavailable');
    assert.equal(app.elements['.artworkGenresImages'].textContent, 'Unavailable');
    app.instance.onPause();
});

test('refresh work metric switches to actual folder progress during import', async () => {
    const app = harness(true);
    const visit = app.instance.onResume();
    app.reads[0].resolve({});
    app.refresh[0].resolve({
        State: 'Running', Phase: 'Importing', Category: 'music', Busy: true,
        ImportFoldersServed: 248, ImportFoldersTotal: 1107
    });
    await visit;
    assert.match(app.elements['.refreshSummary'].textContent, /248\/1(?:,|\.|\s)?107 folders/);
    assert.match(app.elements['.refreshWork'].textContent, /248 \/ 1(?:,|\.|\s)?107 folders served/);
    app.instance.onPause();
});

test('import content counts and opt-in folder details survive timeout and render as text', async () => {
    const app = harness(true);
    const visit = app.instance.onResume();
    app.reads[0].resolve({});
    app.refresh[0].resolve({
        State: 'TimedOut', Phase: 'Import timed out', Busy: false,
        Categories: [{ Category: 'music', Name: 'Music', State: 'Timed out' }], QueueSlotsAvailable: 2,
        ImportFoldersServed: 484, ImportFoldersTotal: 486,
        ImportProgress: {
            StationEntriesServed: 8, StationEntriesTotal: 12,
            SubfolderEntriesServed: 5, SubfolderEntriesTotal: 6,
            LastFolderStationEntries: 3, LastFolderSubfolderEntries: 1,
            PendingFolders: [{ Reference: 'abc', Path: 'Music > <img onerror=bad> More Stations',
                StationEntries: 4, SubfolderEntries: 1 }],
            PendingFoldersOmitted: 1
        }
    });
    await visit;
    assert.equal(app.elements['.refreshImportContents'].hidden, false);
    assert.match(app.elements['.refreshImportContents'].textContent,
        /8 \/ 12 station entries; 5 \/ 6 subfolder entries/);
    assert.match(app.elements['.refreshImportContents'].textContent,
        /Still in unserved listings: 4 station entries, 1 subfolder entries/);
    assert.match(app.elements['.refreshImportContents'].textContent,
        /Last newly served folder: 3 station entries, 1 subfolder entries/);
    assert.match(app.elements['.refreshImportContents'].textContent, /served does not mean stored in Emby/);
    assert.equal(app.elements['.refreshPendingFolders'].hidden, false);
    assert.match(app.elements['.refreshPendingFolderText'].textContent,
        /\[abc\] Music > <img onerror=bad> More Stations: 4 station entries, 1 subfolder entries/);
    assert.match(app.elements['.refreshPendingFolderText'].textContent, /Additional folder details omitted: 1/);
    assert.equal(app.elements['.refreshPendingFolderText'].childNodes.length, 0);

    app.elements['.tuneInRefresh'].open = true;
    app.elements['.refreshStart'].handlers.click();
    app.refresh.at(-1).resolve({ State: 'Running', Phase: 'Discovering', Busy: true });
    await settle();
    assert.equal(app.elements['.refreshImportContents'].hidden, true);
    assert.equal(app.elements['.refreshImportContents'].textContent, '');
    assert.equal(app.elements['.refreshPendingFolders'].hidden, true);
    assert.equal(app.elements['.refreshPendingFolderText'].textContent, '');
    app.instance.onPause();
});

test('basic import counts remain available with verbose diagnostics off', async () => {
    const app = harness(true);
    const visit = app.instance.onResume();
    app.reads[0].resolve({});
    app.refresh[0].resolve({
        State: 'Running', Phase: 'Importing', Busy: true,
        ImportProgress: { StationEntriesServed: 0, StationEntriesTotal: 2,
            SubfolderEntriesServed: 0, SubfolderEntriesTotal: 1 }
    });
    await visit;
    assert.equal(app.elements['.refreshImportContents'].hidden, false);
    assert.match(app.elements['.refreshImportContents'].textContent, /0 \/ 2 station entries/);
    assert.doesNotMatch(app.elements['.refreshImportContents'].textContent, /Last newly served folder/);
    assert.equal(app.elements['.refreshPendingFolders'].hidden, true);
    app.instance.onPause();
});

test('partial discovery reports skipped links without implying an import', async () => {
    const app = harness(true);
    const visit = app.instance.onResume();
    app.reads[0].resolve({});
    app.refresh[0].resolve({
        State: 'Partial', Phase: 'Discovery incomplete; existing library left unchanged',
        PartialImportAllowed: false,
        CategoriesCompleted: 1, CategoriesTotal: 2, SkippedBrowseLinks: 7,
        SkippedBrowseLinkDetails: '4 x Music: Invalid catalog fragment; 3 x Music: Invalid catalog link parameter: filter',
        LastHttpStatus: 429
    });
    await visit;
    const text = app.elements['.refreshStatus'].textContent;
    assert.match(text, /Partial - Discovery incomplete; existing library left unchanged/);
    assert.match(app.elements['.refreshWork'].textContent, /1 \/ 2 categories checked/);
    assert.match(text, /7 malformed catalog links skipped/);
    assert.match(text, /enable "Import Valid Results when catalog entries are skipped" in Global TuneIn Settings, save, then refresh again/);
    assert.match(text, /TuneIn returned HTTP 429 \(Too Many Requests\)/);
    assert.match(app.elements['.refreshDiagnosticsText'].textContent,
        /Skipped malformed TuneIn catalog links: 7 \(4 x Music: Invalid catalog fragment; 3 x Music: Invalid catalog link parameter: filter\)/);
    assert.equal(app.elements['.refreshLogButton'].disabled, true);
    app.instance.onPause();
});

for (const state of ['Completed', 'Partial', 'Running', 'Error', 'Cancelled', 'TimedOut']) {
    test(`HTTP 429 is marked recovered only for a completed refresh (${state})`, async () => {
        const app = harness(true);
        const visit = app.instance.onResume();
        app.reads[0].resolve({});
        app.refresh[0].resolve({
            State: state, Phase: state === 'Running' ? 'Discovering' : state,
            Busy: state === 'Running', LastHttpStatus: 429
        });
        await visit;
        const text = app.elements['.refreshStatus'].textContent;
        if (state === 'Completed') {
            assert.equal(text, 'Completed. TuneIn temporarily limited request speed (HTTP 429 Too Many Requests); '
                + 'the plugin waited, retried and finished normally');
        }
        else {
            assert.match(text, /TuneIn returned HTTP 429 \(Too Many Requests\)/);
            assert.doesNotMatch(text, /retried|finished normally/);
        }
        app.instance.onPause();
    });
}

test('refresh errors show a readable HTTP reason and preserve unfamiliar codes', async () => {
    for (const [code, reason] of [[503, 'Service Unavailable'], [599, 'Unexpected response']]) {
        const app = harness(true);
        const visit = app.instance.onResume();
        app.reads[0].resolve({});
        app.refresh[0].resolve({ State: 'Error', LastHttpStatus: code });
        await visit;
        assert.ok(app.elements['.refreshStatus'].textContent.includes(`TuneIn returned HTTP ${code} (${reason})`));
        app.instance.onPause();
    }
});

test('a failed lock write leaves edits blocked until the server confirms the state', async () => {
    const app = harness(true);
    const visit = app.instance.onResume();
    app.reads[0].resolve({});
    app.refresh[0].resolve({ SettingsLocked: false });
    await visit;
    const toggle = app.elements['.settingsLocked'];
    const state = app.elements['.settingsLockState'];
    assert.equal(state.textContent, 'Unlocked');
    toggle.checked = true;
    const locking = toggle.handlers.change();
    app.locks[0].reject(new Error('response lost'));
    await locking;
    assert.equal(app.elements['.button-submit'].disabled, true);
    assert.match(app.elements['.settingsLockStatus'].textContent, /Checking the server/);
    app.refresh[1].resolve({ SettingsLocked: true });
    await settle();
    assert.equal(toggle.checked, true);
    assert.equal(toggle.disabled, false);
    assert.equal(app.elements['.button-submit'].disabled, true);
    assert.equal(state.textContent, 'Locked');
});

for (const outcome of ['locked', 'save failed']) {
    test(`saving uses the server-persisted lock without a second write (${outcome})`, async () => {
        const app = harness(true, true);
        const visit = app.instance.onResume();
        app.reads[0].resolve({});
        app.refresh[0].resolve({ SettingsLocked: false });
        await visit;
        const toggle = app.elements['.settingsLocked'];
        const save = app.elements['.button-submit'];
        app.formHandlers.submit({ preventDefault() {} });
        assert.equal(app.writes.length, 1);
        assert.equal(app.locks.length, 0);
        assert.equal(toggle.checked, false);
        assert.equal(save.disabled, true);
        if (outcome === 'save failed') {
            app.writes[0].reject(new Error('save failed'));
            await settle();
            assert.equal(app.locks.length, 0);
            assert.match(app.elements['.status'].textContent, /Configuration was not saved/);
        } else {
            app.writes[0].resolve('saved');
            await settle();
            assert.equal(app.locks.length, 0);
            assert.equal(toggle.disabled, true);
            assert.equal(app.elements['.settingsLockState'].textContent, 'Checking');
            assert.equal(app.elements['.status'].textContent, '');
            assert.deepEqual(app.notices, ['saved']);
        }
        app.refresh[1].resolve({ SettingsLocked: outcome === 'locked' });
        await settle();
        assert.equal(save.disabled, outcome === 'locked');
    });
}
