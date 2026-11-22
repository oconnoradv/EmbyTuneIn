'use strict';

const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');

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
    addEventListener(name, handler) { this.handlers[name] = handler; }
    showModal() { this.open = true; }
    focus() { this.focused = true; }
    close() { this.open = false; if (this.handlers.close) this.handlers.close(); }
    descendants() { return this.childNodes.flatMap(child => [child, ...child.descendants()]); }
}

function harness(manualStatus = false, manualSave = false, manualBackup = false) {
    const handlers = {};
    const formHandlers = {};
    const username = new Element('input');
    const latLon = new Element('input');
    const requestInterval = new Element('input');
    const form = { addEventListener: (event, handler) => { formHandlers[event] = handler; } };
    const elements = { form, '.txtUsername': username, '.txtLatLon': latLon,
        '.apiRequestIntervalSeconds': requestInterval, '.importDeadlineHours': new Element('input'),
        '.discoveryDeadlineHours': new Element('input'), '.maxStationCount': new Element('input'),
        '.traversalDepthLimit': new Element('input') };
    for (const name of ['.localSource', '.configurationCompleted', '.hideRegionalGenres', '.allowPartialCatalogImport', '.enableCatalogIdentityDiagnostics', '.categories', '.selectors',
        '.loginFields', '.globalFields', '.configurationFields', '.button-submit', '.status', '.refreshStatus', '.refreshError',
        '.catalogRefreshPrompt', '.stationDeletionNotice', '.channelScheduleDialog', '.channelScheduleText',
        '.channelScheduleConfirm', '.channelScheduleAdjust', '.tuneInPluginVersion',
        '.refreshElapsed', '.refreshWork', '.refreshLastProgress', '.refreshDiagnosticsText',
        '.refreshImportContents', '.refreshPendingFolders', '.refreshPendingFolderText',
        '.refreshStart', '.refreshCancel', '.refreshLogButton', '.refreshLogMessage', '.refreshFailureLog',
        '.tuneInRefreshCategories', '.refreshCategoryNote', '.settingsLocked',
        '.settingsLockState', '.settingsLockStatus', '.refreshQueueNote', '.tuneInRefreshRecoveryDialog', '.refreshRecoveryText',
        '.refreshResumeFirst', '.refreshNewFirst', '.cleanupStatus', '.cleanupError', '.cleanupPreview',
        '.cleanupConfirm', '.cleanupCancel', '.cleanupBack', '.cleanupConfirmation', '.cleanupPreviewText', '.cleanupScope', '.backupStart',
        '.backupStatus', '.cleanupBackupVerified', '.cleanupStationReference', '.cleanupStationFields', '.cleanupStationSearch', '.cleanupStationSearchStatus', '.cleanupStationResults', '#tuneInReportHint']) elements[name] = new Element();
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
    elements['.refreshSummaryDetails'] = elements['.refreshSummary'].appendChild(new Element('span'));
    elements['.refreshOverall'] = elements['.refreshSummary'].appendChild(new Element('span'));
    const enhanced = [];
    const view = {
        addEventListener(event, handler) { handlers[event] = handler; },
        querySelector(selector) {
            const category = /^\[data-category="(.+)"\]$/.exec(selector);
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
    const cleanup = [];
    const backup = [];
    const scheduledTasks = { tasks: [], requests: [] };
    const timers = new Map();
    let timerId = 0;
    let serverLocked = false;
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
                return request.promise;
            }
            writes.push({ id, config });
            return Promise.resolve('saved');
        },
        getUrl(route, params) { return { route, params }; },
        ajax(options) {
            const request = deferred();
            if (options.url.route === 'TuneIn/SettingsLock') {
                locks.push({ options, ...request });
                if (!manualStatus) {
                    serverLocked = JSON.parse(options.data).Locked;
                    request.resolve({ Locked: serverLocked });
                }
            }
            else if (options.url.route.startsWith('TuneIn/Refresh/')) {
                refresh.push({ options, ...request });
                if (!manualStatus && options.type === 'GET') request.resolve({ SettingsLocked: serverLocked });
            } else if (options.url.route.startsWith('TuneIn/Cleanup/')) cleanup.push({ options, ...request });
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
    vm.runInNewContext(fs.readFileSync(path.join(configDir, 'configPage.js'), 'utf8'), {
        define(dependencies, factory) {
            assert.equal(dependencies[0], 'baseView');
            PluginView = factory(BaseView, loader, element => ({ trigger(event) { enhanced.push({ element, event }); } }));
        },
        ApiClient, Dashboard, console: { error(...args) { errors.push(args); } },
        document: { createElement(tag) { return new Element(tag); } },
        setTimeout(handler, delay) { timers.set(++timerId, { handler, delay }); return timerId; },
        clearTimeout(id) { timers.delete(id); }
    });

    return { instance: new PluginView(view), username, latLon, requestInterval, formHandlers, reads, writes, notices, errors, loader,
        elements, selectors, enhanced, calls, handlers, refresh, locks, cleanup, backup, timers, scheduledTasks };
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
    assert.match(html, /<div class="auto-center tuneInPanelLayout">\s*<form>[\s\S]*?<\/form>\s*<details class="tuneInSection tuneInNeutralPanel tuneInRefresh"/);
    assert.match(html, /<\/details>\s*<details class="tuneInSection tuneInNeutralPanel">\s*<summary class="maintenanceSummary">/);
    assert.match(html, /\.tuneInPanelLayout\s*\{\s*max-width:\s*80ch/);
    assert.match(html, /\.tuneInActivity\s*\{[^}]*animation:\s*tuneInActivityPulse/);
    assert.match(html, /\.tuneInActivity\s*\{[^}]*background-color:\s*#fff0c2/);
    assert.match(html, /@keyframes tuneInActivityPulse\s*\{\s*50%\s*\{\s*background-color:\s*#ffcb71/);
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
    assert.match(html, /<fieldset class="globalFields"[\s\S]*<fieldset class="loginFields"[\s\S]*<legend>Your TuneIn Login<\/legend>[\s\S]*class="txtUsername"[^>]*label="TuneIn User:"[\s\S]*class="localSource"[\s\S]*class="txtLatLon"[\s\S]*class="configurationCompleted"[\s\S]*class="settingsLocked"/);
    assert.doesNotMatch(html, /<h2>Favorites<\/h2>/);
    assert.match(html, /class="txtLatLon"[^>]*label="Your LatLon:"/);
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
    assert.deepEqual(app.handlers, {});
});

test('save retains unrelated configuration and converts empty fields to null', async () => {
    const app = harness();
    const visit = app.instance.onResume();
    app.reads[0].resolve({ Username: 'old', LatLon: '1,2', Other: 42 });
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
    assert.equal(app.writes[0].config.SchemaVersion, 1);
    assert.equal(app.writes[0].config.ConfigurationCompleted, true);
    assert.equal(app.writes[0].config.ApiRequestIntervalSeconds, 3);
    assert.equal(app.writes[0].config.ImportDeadlineHours, 8);
    assert.equal(app.writes[0].config.DiscoveryDeadlineHours, 2);
    assert.equal(app.writes[0].config.EnableCatalogIdentityDiagnostics, false);
    assert.deepEqual(app.notices, ['saved']);
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

test('discovery controls distinguish legacy defaults from Not set and preserve saved limits', async () => {
    for (const [config, maximum, depth] of [[{}, 200, 7], [{ MaxStationCount: null }, '', 7],
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
    for (const value of ['0', '4001', '1.5', 'abc']) {
        cap.value = value;
        app.formHandlers.submit({ preventDefault() {} });
        assert.equal(app.writes.length, 0);
        assert.match(app.elements['.status'].textContent, /1 to 4000/);
    }
    cap.value = '';
    for (const value of ['', '2', '51', '3.5']) {
        depth.value = value;
        app.formHandlers.submit({ preventDefault() {} });
        assert.equal(app.writes.length, 0);
        assert.match(app.elements['.status'].textContent, /3 to 50/);
    }
    for (const [maximum, limit] of [['', 3], ['1', 50], ['4000', 5]]) {
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

test('limited discovery is not displayed as confirmed full success', async () => {
    const app = harness(true);
    const visit = app.instance.onResume();
    app.reads[0].resolve({ SchemaVersion: 1, ConfigurationCompleted: true, EnableTalk: true });
    app.refresh[0].resolve({ State: 'Limited', SettingsLocked: false,
        Categories: [{ Category: 'talk', Name: 'Talk', State: 'Limited (imported)' }] });
    await visit;
    await settle();
    assert.equal(app.elements['.tuneInRefresh'].attributes['data-outcome'], 'Partial');
    assert.match(app.elements['.refreshOverall'].textContent, /limited discovery/);
});

for (const setting of ['Import', 'Discovery']) {
test(`${setting.toLowerCase()} deadline loads saved values, validates whole hours, and saves both bounds`, async () => {
    const app = harness();
    const visit = app.instance.onResume();
    app.reads[0].resolve({ [setting + 'DeadlineHours']: 7 });
    await visit;
    const deadline = app.elements['.' + setting.toLowerCase() + 'DeadlineHours'];
    const maximum = setting === 'Import' ? 100 : 10;
    assert.equal(deadline.value, 7);
    for (const value of ['', '0', String(maximum + 1), '1.5', 'abc']) {
        deadline.value = value;
        app.formHandlers.submit({ preventDefault() {} });
        assert.equal(app.writes.length, 0);
        assert.ok(app.elements['.status'].textContent.includes('whole number from 1 to ' + maximum));
    }
    for (const value of [1, maximum]) {
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

test('advanced settings start collapsed and group the controls in order', () => {
    const html = fs.readFileSync(path.join(configDir, 'configPage.html'), 'utf8');
    const advanced = html.match(/<details class="tuneInAdvancedSettings">([\s\S]*?)<\/details>/);
    assert.ok(advanced);
    assert.match(advanced[1], /<summary>Advanced Settings<\/summary>/);
    assert.ok(advanced[1].indexOf('id="tuneInApiInterval"') < advanced[1].indexOf('id="tuneInDiscoveryDeadline"'));
    assert.ok(advanced[1].indexOf('id="tuneInDiscoveryDeadline"') < advanced[1].indexOf('id="tuneInImportDeadline"'));
    assert.ok(advanced[1].indexOf('id="tuneInImportDeadline"') < advanced[1].indexOf('class="enableCatalogIdentityDiagnostics"'));
    assert.match(advanced[1], /Off by default/);
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
    assert.equal(mode.value, 'all');
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
        assert.equal(mode.value, 'all');
        const [browseColumn, savedColumn] = columns.childNodes;
        assert.equal(browseColumn.hidden, true);
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

test('Local Radio All mode accepts the Regions source without saved regions', async () => {
    const app = harness();
    const visit = app.instance.onResume();
    app.reads[0].resolve({});
    await visit;
    enableBrowsing(app);
    app.instance.view.querySelector('[data-category="EnableLocalRadio"]').checked = true;
    app.elements['.localSource'].value = 'regions';
    app.formHandlers.submit({ preventDefault() {} });
    await settle();
    assert.equal(app.writes.length, 1);
    assert.equal(app.writes[0].config.LocalRadioSource, 'regions');
    assert.deepEqual(Array.from(app.writes[0].config.LocalCities || []), []);
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

test('category counts show only filtered availability and Emby inventory left of Refresh', async () => {
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
            StationCounts: { AvailableStations: 0, EmbyStations: 0 } },
        { Category: 'local', Name: 'Local Radio', State: 'No recorded refresh' },
        { Category: 'presets', Name: 'Favorites', State: 'Imported',
            StationCounts: { AvailableStations: 4, EmbyCountError: '<unsafe> inventory read failed' } },
        { Category: 'language', Name: 'By Language', State: 'Imported',
            StationCounts: { AvailableStations: 0, EmbyStations: 0 } }
    ] });
    await visit;
    await settle();
    const rows = app.elements['.tuneInRefreshCategories'].childNodes;
    assert.equal(rows[4].childNodes[0].textContent, 'By Language: Imported (TuneIn lists no stations for the selected languages)');
    assert.equal(rows[1].childNodes[0].textContent, 'Sports: Imported');
    const values = row => row.childNodes[1].childNodes[0].childNodes.map(group => group.childNodes[1]);
    assert.equal(rows[0].childNodes[1].childNodes[1].textContent, 'Refresh');
    assert.equal(rows[0].childNodes[1].childNodes[1].attributes.is, 'emby-button');
    assert.match(rows[0].childNodes[1].childNodes[1].className, /\braised\b/);
    assert.match(rows[0].childNodes[1].childNodes[1].className, /\bbutton-submit\b/);
    assert.deepEqual(values(rows[0]).map(value => value.textContent), ['123 (partial)', '145']);
    assert.match(values(rows[0])[0].attributes.title, /lower bound/);
    assert.deepEqual(values(rows[1]).map(value => value.textContent), ['0', '0']);
    assert.deepEqual(values(rows[2]).map(value => value.textContent), ['Not checked', 'Not checked']);
    assert.deepEqual(values(rows[3]).map(value => value.textContent), ['4', 'Unavailable']);
    assert.doesNotMatch(app.elements['.tuneInRefreshCategories'].textContent, /unfiltered|Not measured|Not applicable/i);
    assert.equal(values(rows[3])[1].childNodes.length, 0);
    assert.equal(app.selectors.length, 0);
    assert.equal(app.refresh.length, 1);
    app.instance.onPause();
});

test('In Emby keeps last known values and timestamps when the settings screen reopens', async () => {
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
        assert.match(value().attributes.title, /Last successful inventory check:/);
        assert.match(value().attributes.title, /inventory read failed/);
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
        const [local, favorites, sports] = rows.map(row => row.childNodes[1].childNodes[1]);
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

test('returning to saved settings opens the Control Panel with the category sections collapsed', async () => {
    const app = harness();
    const sections = () => [app.elements['.categoriesSection'], app.elements['.stationSelectionsSection'],
        ...app.elements['.selectors'].childNodes];
    const first = app.instance.onResume();
    app.reads[0].resolve({ SchemaVersion: 1, ConfigurationCompleted: false });
    await first;
    for (const section of sections()) {
        assert.equal(section.open, false);
        section.childNodes[0].handlers.click({ preventDefault() {} });
        assert.equal(section.open, true);
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
    assert.equal(global.open, true);
    assert.equal(global.attributes['data-saved'], 'false');
    app.elements['.configurationCompleted'].checked = false;
    app.elements['.configurationCompleted'].handlers.change();
    app.formHandlers.submit({ preventDefault() {} });
    app.writes[1].resolve('saved');
    await settle();
    assert.equal(global.open, false);
    assert.equal(global.attributes['data-saved'], 'true');
    assert.equal(app.elements['.browsingSection'].open, true);
    assert.ok(categorySections.filter(section => section !== app.elements['.browsingSection'])
        .concat(categoryPickers()).every(section => !section.open));
    assert.equal(app.writes[1].config.ConfigurationCompleted, false);
    assert.equal(app.elements['.globalFields'].disabled, true);
    app.instance.onPause();
    const next = app.instance.onResume();
    app.reads[1].resolve({ SettingsSaved: true, SchemaVersion: 1, ConfigurationCompleted: false });
    await next;
    assert.equal(global.open, false);
    assert.equal(app.elements['.browsingSection'].open, true);
    assert.ok(categorySections.filter(section => section !== app.elements['.browsingSection'])
        .concat(categoryPickers()).every(section => !section.open));
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
    assert.deepEqual(rows.map(row => row.attributes['data-outcome']), ['Completed', 'Partial', 'Error', '']);
    assert.deepEqual(rows.map(row => row.childNodes[0].textContent),
        ['Talk: Imported', 'Sports: Partial (applied)', 'Podcasts: Failed', 'Music: Queued']);
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
    [...app.timers.values()].find(timer => timer.delay === 10000).handler();
    app.refresh[2].resolve({ State: 'Completed', SettingsLocked: false,
        Categories: [{ Category: 'talk', Name: 'Talk', State: 'Partial (unchanged)' }] });
    await settle();
    assert.equal(app.elements['.tuneInRefresh'].attributes['data-outcome'], 'Partial');
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
    assert.equal(app.elements['.browsingSection'].open, true);
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

test('collapsed refresh summary and diagnostics expose live discovery metrics', async () => {
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
    assert.match(app.elements['.refreshSummary'].textContent,
        /Catalog Refresh Refreshing - Running - Discovering - Music - 1(?:,|\.|\s)?842 pages - progress \d+ sec ago/);
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

for (const outcome of ['locked', 'save failed', 'lock unconfirmed']) {
    test(`save turns on the lock only after configuration is persisted (${outcome})`, async () => {
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
            assert.deepEqual(JSON.parse(app.locks[0].options.data), { Locked: true });
            assert.equal(toggle.disabled, true);
            assert.equal(toggle.checked, false);
            if (outcome === 'locked') app.locks[0].resolve({ Locked: true });
            else app.locks[0].reject(new Error('response lost'));
            await settle();
            assert.equal(toggle.checked, outcome === 'locked');
            assert.match(app.elements['.status'].textContent, outcome === 'locked' ?
                /saved and locked/ : /saved, but the settings lock could not be confirmed/);
        }
        app.refresh[1].resolve({ SettingsLocked: outcome === 'locked' });
        await settle();
        assert.equal(save.disabled, outcome === 'locked');
    });
}
