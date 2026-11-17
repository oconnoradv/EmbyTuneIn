'use strict';
const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');

class Element {
    constructor(tag) {
        this.tag = tag;
        this.children = [];
        this.listeners = {};
        this.attributes = {};
        this.value = '';
        this.checked = false;
        this.text = '';
    }
    appendChild(child) { child.parentNode = this; this.children.push(child); }
    addEventListener(event, handler) { this.listeners[event] = handler; }
    setAttribute(key, value) { this.attributes[key] = value; }
    set textContent(value) { this.text = value; this.children = []; }
    get textContent() { return this.text; }
    all() { return [this, ...this.children.flatMap(child => child.all())]; }
    querySelector(selector) {
        return this.all().find(element =>
            selector.startsWith('.') ? (element.className || '').split(' ').includes(selector.slice(1))
                : selector.startsWith('[data-category=') ? element.attributes['data-category'] === selector.split('"')[1]
                    : element.tag === selector);
    }
    querySelectorAll(selector) {
        if (selector === '.configurationFields .tuneInSettingsSection')
            return this.all().filter(element => (element.className || '').split(' ').includes('tuneInSettingsSection')
                && element !== this.querySelector('.channelRefreshSection'));
        throw new Error('Unsupported selector: ' + selector);
    }
}

function harness(config) {
    const view = new Element('view');
    for (const name of ['form', 'txtUsername', 'txtLatLon', 'apiRequestInterval', 'useCoordinates', 'configurationCompleted',
        'categories', 'selectors', 'status', 'channelRefreshUpdate', 'channelRefreshFailure',
        'channelRefreshStatus', 'channelRefreshMessage', 'channelRefreshLog', 'channelRefreshSection',
        'settingsLocked', 'settingsLockMessage', 'configurationFields', 'button-submit', 'refreshTimeout', 'refreshGroups']) {
        const element = new Element(name === 'form' ? 'form' : 'div');
        element.className = name;
        view.appendChild(element);
    }
    const calls = { catalogue: [], saves: [], configReads: [], configWrites: [], upgrades: [], diagnostics: [], locks: [], groups: [], batches: 0 };
    const timers = [];
    let View;
    function BaseView(element) { this.view = element; }
    BaseView.prototype.onResume = function () { return this.view.listeners.viewshow(); };
    BaseView.prototype.onPause = function () { this.view.listeners.viewbeforehide(); };
    BaseView.prototype.onBeginResume = function () { return 'native transition hook'; };
    const ApiClient = {
        getPluginConfiguration: async id => { calls.configReads.push(id); return JSON.parse(JSON.stringify(config)); },
        getUrl: (route, query) => ({ route, query }),
        ajax: async request => {
            if (request.url.route === 'TuneInFork/RefreshTuneIn') {
                calls.diagnostics.push(request);
                return { State: 'Idle', LastResult: 'NeverRun' };
            }
            assert.equal(request.url.route, 'TuneInFork/Selectors');
            calls.catalogue.push(request);
            return { Items: [{ Id: 'l1', Name: '<img src=x onerror=alert(1)>',
                Path: 'Example', AncestorIds: ['lang'] }], Warning: 'Incidental metadata warning.' };
        },
        updatePluginConfiguration: async (id, value) => {
            calls.configWrites.push(id);
            calls.saves.push(JSON.parse(JSON.stringify(value)));
            return {};
        }
    };
    let ajax = ApiClient.ajax;
    let lockState = { Locked: false, RefreshActive: false };
    let lockHandler = async request => {
        calls.locks.push(request);
        if (request.type === 'POST') lockState = { ...lockState, ...JSON.parse(request.data) };
        return { ...lockState };
    };
    let groupHandler = async request => {
        calls.groups.push(request);
        const category = request.type === 'GET' ? request.url.query.Category : JSON.parse(request.data).Category;
        return { Category: category, Enabled: category === 'talk', State: 'Idle',
            CompletedRequests: 0, TotalRequests: 0, Id: 'attempt1' };
    };
    Object.defineProperty(ApiClient, 'ajax', {
        get: () => request => request.url.route === 'TuneInFork/SettingsLock' ? lockHandler(request)
            : request.url.route === 'TuneInFork/RefreshTuneIn/Categories'
                ? (calls.batches++, Promise.all(['local', 'music', 'language', 'location', 'talk', 'sports', 'podcast', 'presets']
                    .map(category => groupHandler({ type: 'GET', url: { route: 'TuneInFork/RefreshTuneIn/Category', query: { Category: category } } }))))
            : request.url.route.startsWith('TuneInFork/RefreshTuneIn/Category') ? groupHandler(request) : ajax(request),
        set: handler => { ajax = handler; }
    });
    vm.runInNewContext(fs.readFileSync(path.join(__dirname, '..', 'MediaBrowser.Plugins.TuneIn',
        'Configuration', 'configPage.js'), 'utf8'), {
        define: (dependencies, factory) => {
            assert.equal(dependencies[0], 'loading');
            assert.equal(dependencies[1], 'jQuery');
            assert.equal(dependencies.includes('baseView'), false);
            const PluginView = factory({ show() {}, hide() {} },
                element => ({ trigger(event) { assert.equal(event, 'create'); calls.upgrades.push(element); } }));
            assert.equal(PluginView.prototype.onResume, undefined);
            assert.equal(PluginView.prototype.onPause, undefined);
            // Match Emby's plugin controller adapter, which owns native view transitions.
            View = function (element, params) {
                BaseView.call(this, element, params);
                PluginView.call(this, element, params);
            };
            Object.assign(View.prototype, BaseView.prototype, PluginView.prototype);
        },
        document: { createElement: tag => new Element(tag) },
        ApiClient,
        setTimeout(callback) { timers.push(callback); return timers.length; },
        clearTimeout() {},
        Dashboard: { processPluginConfigurationUpdateResult() {} }
    });
    return { instance: new View(view), view, calls, ApiClient, timers, setLockHandler(handler) { lockHandler = handler; },
        setGroupHandler(handler) { groupHandler = handler; } };
}

const settle = () => new Promise(resolve => setImmediate(resolve));
const html = () => fs.readFileSync(path.join(__dirname, '..', 'MediaBrowser.Plugins.TuneIn',
    'Configuration', 'configPage.html'), 'utf8');

test('settings expose native task status without schedule controls or automatic log retrieval', async () => {
    assert.doesNotMatch(html(), /data-section="schedules"|scheduleControls/);
    assert.match(html(), /Refresh TuneIn.*enabled TuneIn categories.*other channel providers are not refreshed/);
    const app = harness({ SchemaVersion: 1, ConfigurationCompleted: true, EnableTalk: true });
    const requests = [];
    app.ApiClient.ajax = async request => {
        requests.push(request);
        assert.equal(request.type, 'GET');
        assert.equal(request.url.route, 'TuneInFork/RefreshTuneIn');
        return { State: 'Idle', LastResult: 'NeverRun' };
    };
    await app.instance.onResume();
    app.view.querySelector('form').listeners.submit({ preventDefault() {} });
    await settle();
    app.instance.onPause();
    await app.instance.onResume();
    assert.equal(requests.length, 2);
    assert.equal(app.calls.saves.length, 1);
});

test('failed native task status enables explicit report retrieval and renders error text safely', async () => {
    const app = harness({});
    const requests = [];
    app.ApiClient.ajax = async request => {
        requests.push(request);
        if (request.url.route === 'TuneInFork/RefreshTuneIn')
            return { State: 'Running', Progress: 35, LastResult: 'Failed', FailedAttempt: 'attempt1', FinishedUtc: '2026-11-16T17:00:00Z' };
        assert.equal(request.url.route, 'TuneInFork/RefreshTuneIn/Failure');
        assert.equal(request.url.query.Attempt, 'attempt1');
        return { Attempt: 'attempt1', Text: '<script>private error</script>', Truncated: true };
    };
    await app.instance.onResume();
    assert.equal(requests.length, 1);
    assert.match(app.view.querySelector('.channelRefreshStatus').textContent, /Running.*Failed.*35%/);
    const button = app.view.querySelector('.channelRefreshFailure');
    assert.equal(button.disabled, false);
    await button.listeners.click();
    assert.equal(requests.length, 2);
    assert.equal(app.view.querySelector('.channelRefreshLog').textContent, '<script>private error</script>');
    assert.equal(app.view.querySelector('.channelRefreshLog').children.length, 0);
    assert.equal(app.view.querySelector('.channelRefreshLog').hidden, false);
    assert.match(app.view.querySelector('.channelRefreshMessage').textContent, /limited/);
});

test('running task shows its current category, phase and measured request counts', async () => {
    const app = harness({});
    app.ApiClient.ajax = async () => ({
        State: 'Running', Progress: 35, LastResult: 'NeverRun',
        Current: { Category: 'music', Phase: 'Discovering selected catalogue pages',
            CategoryIndex: 2, CategoryCount: 3, StartedRequests: 7, CompletedRequests: 6, CacheHits: 2 }
    });
    await app.instance.onResume();
    const status = app.view.querySelector('.channelRefreshStatus').textContent;
    assert.equal(app.view.querySelector('.channelRefreshSection').open, true);
    assert.match(status, /Category: Music \(2 of 3\)/);
    assert.match(status, /Stage: Discovering selected catalogue pages/);
    assert.match(status, /completed\/started: 6\/7; cached pages reused: 2/);
    assert.match(status, /\nStage:.*\nCatalogue requests/);
    app.ApiClient.ajax = async () => ({
        State: 'Running', LastResult: 'NeverRun',
        Current: { Category: 'music', Phase: '<script>Importing</script>',
            CategoryIndex: 2, CategoryCount: 3, StartedRequests: 7, CompletedRequests: 7, CacheHits: 2 }
    });
    await app.view.querySelector('.channelRefreshUpdate').listeners.click();
    assert.match(app.view.querySelector('.channelRefreshStatus').textContent, /<script>Importing<\/script>/);
    assert.equal(app.view.querySelector('.channelRefreshStatus').children.length, 0);
});

test('scheduled refresh shows timeout outcome and baseline estimate', async () => {
    const app = harness({});
    app.ApiClient.ajax = async () => ({
        State: 'Running', LastResult: 'NeverRun', Outcome: 'Running',
        Current: { Category: 'music', Phase: 'Importing staged section into Emby: 2/8 staged folders requested',
            CategoryIndex: 2, CategoryCount: 5, StartedRequests: 7, CompletedRequests: 7, CacheHits: 0,
            EstimatedProgress: 32, EstimatedCompletedRequests: 20, EstimatedRequests: 80 }
    });
    await app.instance.onResume();
    assert.match(app.view.querySelector('.channelRefreshStatus').textContent, /Estimated progress: 32% \(20\/80 estimated requests\)/);
    app.ApiClient.ajax = async () => ({ State: 'Idle', LastResult: 'Failed', FailedAttempt: 'attempt', Outcome: 'TimedOut' });
    await app.view.querySelector('.channelRefreshUpdate').listeners.click();
    assert.match(app.view.querySelector('.channelRefreshStatus').textContent, /Outcome: TimedOut/);
});

test('missing or malformed stage is not presented as current progress', async () => {
    const app = harness({});
    app.ApiClient.ajax = async () => ({ State: 'Running', LastResult: 'NeverRun' });
    await app.instance.onResume();
    assert.match(app.view.querySelector('.channelRefreshStatus').textContent, /Current stage unavailable/);
    app.ApiClient.ajax = async () => ({
        State: 'Running', LastResult: 'Failed', FailedAttempt: 'old',
        Current: { Category: 'music', Phase: 'Importing', CategoryIndex: 1, CategoryCount: 1,
            StartedRequests: 1, CompletedRequests: 2, CacheHits: 0 }
    });
    await app.view.querySelector('.channelRefreshUpdate').listeners.click();
    assert.match(app.view.querySelector('.channelRefreshStatus').textContent, /Unable to load/);
    assert.equal(app.view.querySelector('.channelRefreshFailure').disabled, true);
});

test('idle task does not display a stale stage', async () => {
    const app = harness({});
    app.ApiClient.ajax = async () => ({
        State: 'Idle', LastResult: 'Completed',
        Current: { Category: 'music', Phase: 'Old phase', CategoryIndex: 1, CategoryCount: 1,
            StartedRequests: 1, CompletedRequests: 1, CacheHits: 0 }
    });
    await app.instance.onResume();
    assert.doesNotMatch(app.view.querySelector('.channelRefreshStatus').textContent, /Old phase|Category: Music/);
});

test('never-run and successful task statuses disable failed-log retrieval', async () => {
    for (const result of ['NeverRun', 'Completed', 'Cancelled', 'Aborted']) {
        const app = harness({});
        app.ApiClient.ajax = async () => ({ State: 'Idle', LastResult: result });
        await app.instance.onResume();
        assert.equal(app.view.querySelector('.channelRefreshFailure').disabled, true);
        assert.match(app.view.querySelector('.channelRefreshStatus').textContent, /Refresh TuneIn task: Idle/);
    }
});

test('status failures and malformed responses show errors and allow retry without claiming success', async () => {
    const app = harness({});
    app.ApiClient.ajax = async () => { throw new Error('Denied'); };
    await app.instance.onResume();
    assert.match(app.view.querySelector('.channelRefreshStatus').textContent, /Unable to load/);
    assert.equal(app.view.querySelector('.channelRefreshUpdate').disabled, false);
    app.ApiClient.ajax = async () => ({ State: 'Idle', LastResult: 'Failed' });
    await app.view.querySelector('.channelRefreshUpdate').listeners.click();
    assert.match(app.view.querySelector('.channelRefreshStatus').textContent, /Unable to load/);
    assert.equal(app.view.querySelector('.channelRefreshFailure').disabled, true);
    app.ApiClient.ajax = async () => ({ State: 'Idle', LastResult: 'Completed' });
    await app.view.querySelector('.channelRefreshUpdate').listeners.click();
    assert.match(app.view.querySelector('.channelRefreshStatus').textContent, /Completed/);
});

test('changed or unavailable failed report clears stale content and requests a fresh status', async () => {
    const app = harness({});
    app.ApiClient.ajax = async () => ({ State: 'Idle', LastResult: 'Failed', FailedAttempt: 'old' });
    await app.instance.onResume();
    app.ApiClient.ajax = async () => ({ Attempt: 'new', Text: 'Wrong attempt' });
    await app.view.querySelector('.channelRefreshFailure').listeners.click();
    assert.match(app.view.querySelector('.channelRefreshMessage').textContent, /Update status/);
    assert.equal(app.view.querySelector('.channelRefreshLog').textContent, '');
    assert.equal(app.view.querySelector('.channelRefreshLog').hidden, true);
    assert.equal(app.view.querySelector('.channelRefreshFailure').disabled, true);
});

test('late native status and report responses cannot overwrite a hidden or reopened view', async () => {
    const app = harness({});
    let respond;
    app.ApiClient.ajax = () => new Promise(resolve => { respond = resolve; });
    const pending = app.instance.onResume();
    await settle();
    app.instance.onPause();
    respond({ State: 'Idle', LastResult: 'Failed', FailedAttempt: 'old' });
    await pending;
    assert.equal(app.instance.failedAttempt, null);
    app.ApiClient.ajax = async () => ({ State: 'Idle', LastResult: 'Failed', FailedAttempt: 'current' });
    await app.instance.onResume();
    app.ApiClient.ajax = () => new Promise(resolve => { respond = resolve; });
    const report = app.view.querySelector('.channelRefreshFailure').listeners.click();
    app.instance.onPause();
    respond({ Attempt: 'current', Text: 'Old report' });
    await report;
    assert.equal(app.view.querySelector('.channelRefreshLog').textContent, '');
});

test('settings preserve native view hooks and labeled inputs', async () => {
    const app = harness({});
    assert.equal(app.instance.onBeginResume(), 'native transition hook');
    await app.instance.onResume();
    assert.match(html(), /class="inputContainer"[\s\S]*class="txtUsername"[^>]+label=/);
    assert.match(html(), /class="txtLatLon"[^>]+label=/);
    assert.match(html(), /class="apiRequestInterval"[^>]+label=/);
    assert.match(html(), /class="refreshTimeout"[^>]+label=/);
});

test('a late configuration response cannot rebuild or upgrade a hidden settings view', async () => {
    const app = harness({});
    let respond;
    app.ApiClient.getPluginConfiguration = () => new Promise(resolve => { respond = resolve; });
    const pending = app.instance.onResume();
    app.instance.onPause();
    respond({ Username: 'outdated response' });
    await pending;
    assert.equal(app.instance.config, undefined);
    assert.equal(app.calls.upgrades.length, 0);
    assert.equal(app.calls.catalogue.length, 0);
});

test('settings sections and refresh controls start collapsed while Save remains accessible', () => {
    const sections = [...html().matchAll(/<details\b([^>]*)>([\s\S]*?)<\/details>/g)];
    assert.equal(sections.length, 5);
    assert.deepEqual(sections.map(section => section[2].match(/<summary[^>]*>([^<]+)<\/summary>/)[1]),
        ['Setup and account', 'Radio groups', 'Local Radio coordinates', 'Station selections', 'Refresh TuneIn']);
    for (const section of sections) assert.doesNotMatch(section[1], /\bopen\b/);
    for (const section of sections) assert.doesNotMatch(section[2], /type="submit"/);
    assert.match(html(), /<button[^>]*type="submit"[^>]*><span>Save configuration<\/span><\/button>/);
});

test('settings lock and an active refresh prevent edits and saving', async () => {
    const app = harness({});
    await app.instance.onResume();
    const toggle = app.view.querySelector('.settingsLocked');
    const fields = app.view.querySelector('.configurationFields');
    const save = app.view.querySelector('.button-submit');
    assert.equal(toggle.checked, false);
    assert.equal(fields.disabled, false);
    toggle.checked = true;
    await toggle.listeners.change();
    assert.equal(fields.disabled, true);
    assert.equal(save.disabled, true);
    app.view.querySelector('form').listeners.submit({ preventDefault() {} });
    await settle();
    assert.equal(app.calls.saves.length, 0);
    toggle.checked = false;
    await toggle.listeners.change();
    assert.equal(save.disabled, false);
    app.setLockHandler(async () => ({ Locked: false, RefreshActive: true }));
    await app.instance.onPause();
    await app.instance.onResume();
    assert.equal(fields.disabled, true);
    assert.equal(save.disabled, true);
    assert.match(app.view.querySelector('.settingsLockMessage').textContent, /refresh is active/);
    assert.equal(app.view.querySelector('.channelRefreshSection').open, true);
});

test('a successful save collapses settings sections', async () => {
    const app = harness({});
    const section = new Element('details');
    section.className = 'tuneInSettingsSection';
    section.open = true;
    app.view.appendChild(section);
    await app.instance.onResume();
    app.view.querySelector('form').listeners.submit({ preventDefault() {} });
    await settle();
    assert.equal(section.open, false);
});

test('unknown settings lock state fails closed and retries on next view', async () => {
    const app = harness({});
    app.setLockHandler(async () => { throw new Error('offline'); });
    await app.instance.onResume();
    assert.equal(app.view.querySelector('.button-submit').disabled, true);
    app.view.querySelector('form').listeners.submit({ preventDefault() {} });
    await settle();
    assert.equal(app.calls.saves.length, 0);
    app.setLockHandler(async () => ({ Locked: false, RefreshActive: false }));
    app.instance.onPause();
    await app.instance.onResume();
    assert.equal(app.view.querySelector('.button-submit').disabled, false);
});

test('a late lock-status response cannot undo a newer lock change', async () => {
    const app = harness({});
    await app.instance.onResume();
    let respond;
    app.setLockHandler(request => request.type === 'GET'
        ? new Promise(resolve => { respond = resolve; })
        : Promise.resolve({ Locked: true, RefreshActive: false }));
    const pending = app.view.querySelector('.channelRefreshUpdate').listeners.click();
    await settle();
    const toggle = app.view.querySelector('.settingsLocked');
    toggle.checked = true;
    await toggle.listeners.change();
    respond({ Locked: false, RefreshActive: false });
    await pending;
    assert.equal(toggle.checked, true);
    assert.equal(app.view.querySelector('.button-submit').disabled, true);
});

test('active refreshes update local status on the open page', async () => {
    const app = harness({});
    let active = false;
    app.setLockHandler(async () => ({ Locked: false, RefreshActive: active }));
    await app.instance.onResume();
    active = true;
    app.timers.shift()();
    await settle();
    assert.equal(app.view.querySelector('.button-submit').disabled, true);
    assert.equal(app.view.querySelector('.channelRefreshSection').open, true);
    assert.ok(app.calls.batches >= 2);
});

test('polling stops when the setup page is hidden', async () => {
    const app = harness({});
    await app.instance.onResume();
    const requests = app.calls.locks.length;
    app.instance.onPause();
    app.timers.shift()();
    await settle();
    assert.equal(app.calls.locks.length, requests);
});

test('resume and save do not crawl; setup preserves legacy account/coordinates and remains explicitly gated', async () => {
    const app = harness({ Username: 'existing-account', LatLon: '1,2', SchemaVersion: 0 });
    await app.instance.onResume();
    assert.equal(app.calls.catalogue.length, 0);
    assert.equal(app.view.querySelector('.txtUsername').value, 'existing-account');
    assert.equal(app.view.querySelector('.apiRequestInterval').value, '3');
    assert.equal(app.view.querySelector('.configurationCompleted').checked, false);
    app.view.querySelector('.configurationCompleted').checked = true;
    app.view.querySelector('form').listeners.submit({ preventDefault() {} });
    await settle();
    assert.equal(app.calls.saves.length, 1);
    assert.equal(app.calls.saves[0].SchemaVersion, 1);
    assert.equal(app.calls.saves[0].ConfigurationCompleted, true);
    assert.equal(app.calls.saves[0].LatLon, '1,2');
    assert.equal(app.calls.saves[0].ApiRequestIntervalSeconds, 3);
    assert.equal(app.calls.saves[0].RefreshTimeoutMinutes, 60);
    assert.equal(app.calls.catalogue.length, 0);
    assert.deepEqual(app.calls.configReads, ['10b82431-f939-4507-a8e2-9509dd3fd6ae']);
    assert.deepEqual(app.calls.configWrites, app.calls.configReads);
});

test('catalogue request interval is saved and rejects invalid values', async () => {
    const app = harness({ ApiRequestIntervalSeconds: 5 });
    await app.instance.onResume();
    const input = app.view.querySelector('.apiRequestInterval');
    assert.equal(input.value, '5');
    for (const value of ['', '0', '31', '1.5', '-1', 'abc']) {
        input.value = value;
        app.view.querySelector('form').listeners.submit({ preventDefault() {} });
        await settle();
        assert.equal(app.calls.saves.length, 0);
        assert.match(app.view.querySelector('.status').textContent, /between 1 and 30/);
    }
    input.value = '1';
    app.view.querySelector('form').listeners.submit({ preventDefault() {} });
    await settle();
    assert.equal(app.calls.saves[0].ApiRequestIntervalSeconds, 1);
});

test('refresh timeout defaults to one hour and rejects invalid values', async () => {
    const app = harness({});
    await app.instance.onResume();
    const timeout = app.view.querySelector('.refreshTimeout');
    assert.equal(timeout.value, '60');
    for (const value of ['', '0', '1441', '1.5', '-1', 'abc']) {
        timeout.value = value;
        app.view.querySelector('form').listeners.submit({ preventDefault() {} });
        await settle();
        assert.equal(app.calls.saves.length, 0);
        assert.match(app.view.querySelector('.status').textContent, /between 1 and 1440/);
    }
    timeout.value = '120';
    app.view.querySelector('form').listeners.submit({ preventDefault() {} });
    await settle();
    assert.equal(app.calls.saves[0].RefreshTimeoutMinutes, 120);
});

test('category actions start enabled groups and resume only timed-out attempts', async () => {
    const app = harness({});
    await app.instance.onResume();
    const findButton = text => app.view.all().find(element => element.tag === 'button' && element.textContent === text);
    assert.equal(findButton('Refresh Music').disabled, true);
    const talk = findButton('Refresh Talk');
    assert.equal(talk.disabled, false);
    await talk.listeners.click();
    const start = app.calls.groups.find(request => request.type === 'POST');
    assert.deepEqual(JSON.parse(start.data), { Category: 'talk' });
    app.setGroupHandler(async request => {
        const category = request.type === 'GET' ? request.url.query.Category : JSON.parse(request.data).Category;
        if (request.type === 'POST') app.calls.groups.push(request);
        return { Category: category, Enabled: category === 'talk', State: category === 'talk' ? 'TimedOut' : 'Idle',
            Resumable: category === 'talk', CompletedRequests: 0, TotalRequests: 0, Id: 'timed-out-1' };
    });
    await app.view.querySelector('.channelRefreshUpdate').listeners.click();
    assert.equal(findButton('Restart Talk').disabled, false);
    await findButton('Resume Talk').listeners.click();
    const resume = app.calls.groups.find(request => request.url.route.endsWith('/Resume'));
    assert.deepEqual(JSON.parse(resume.data), { Category: 'talk', AttemptId: 'timed-out-1' });
});

test('category failures and untrusted status details are shown as text without resume', async () => {
    const app = harness({});
    app.setGroupHandler(async request => ({
        Category: request.url.query.Category, Enabled: true,
        State: request.url.query.Category === 'talk' ? 'Failed' : 'Idle',
        Phase: request.url.query.Category === 'talk' ? '<script>unsafe</script>' : null,
        Error: request.url.query.Category === 'talk' ? 'Provider error' : null,
        CompletedRequests: 1, TotalRequests: 1
    }));
    await app.instance.onResume();
    const group = app.view.querySelector('.refreshGroups');
    assert.ok(group.all().some(element => element.tag === 'p' && element.textContent.includes('<script>unsafe</script>')));
    assert.equal(group.all().some(element => element.tag === 'script'), false);
    assert.equal(group.all().some(element => element.textContent === 'Resume Talk'), false);
});

test('a timeout without a resumable checkpoint offers restart only', async () => {
    const app = harness({});
    app.setGroupHandler(async request => ({
        Category: request.url.query.Category, Enabled: true,
        State: request.url.query.Category === 'talk' ? 'TimedOut' : 'Idle',
        Resumable: false, CompletedRequests: 1, TotalRequests: 1
    }));
    await app.instance.onResume();
    assert.ok(app.view.all().some(element => element.textContent === 'Restart Talk'));
    assert.equal(app.view.all().some(element => element.textContent === 'Resume Talk'), false);
});

test('catalogue loads explicitly; remote labels are text and selected stable ancestry is persisted', async () => {
    const app = harness({});
    await app.instance.onResume();
    app.view.all().find(element => element.tag === 'button' && element.textContent === 'Load root').listeners.click();
    await settle();
    assert.equal(app.calls.catalogue.length, 1);
    assert.equal(app.calls.catalogue[0].url.route, 'TuneInFork/Selectors');
    const label = app.view.all().find(element => element.tag === 'span' && element.textContent.startsWith('<img'));
    assert.equal(label.textContent, '<img src=x onerror=alert(1)> ');
    assert.equal(app.view.all().some(element => element.tag === 'img'), false);
    app.view.all().find(element => element.tag === 'button' && element.textContent === 'Add').listeners.click();
    assert.deepEqual(Array.from(app.instance.config.Languages[0].AncestorIds), ['lang']);
    assert.equal(app.instance.config.Languages[0].Enabled, true);
});

test('invalid coordinates prevent save and selector errors preserve existing choices visibly', async () => {
    const app = harness({ Languages: [{ Id: 'l1', Name: 'Saved', Enabled: true }] });
    await app.instance.onResume();
    app.view.querySelector('.useCoordinates').checked = true;
    app.view.querySelector('.txtLatLon').value = 'NaN,180';
    app.view.querySelector('form').listeners.submit({ preventDefault() {} });
    await settle();
    assert.equal(app.calls.saves.length, 0);
    assert.match(app.view.querySelector('.status').textContent, /finite latitude/);
    app.ApiClient.ajax = async () => { throw new Error('Network failed'); };
    app.view.all().find(element => element.tag === 'button' && element.textContent === 'Load root').listeners.click();
    await settle();
    assert.equal(app.instance.config.Languages.length, 1);
    assert.match(app.view.querySelector('.status').textContent, /request failed/);
});

test('malformed selector responses are errors rather than empty successful catalogues', async () => {
    const app = harness({ Languages: [{ Id: 'l1', Name: 'Saved', Enabled: true }] });
    await app.instance.onResume();
    app.ApiClient.ajax = async () => ({ Items: null });
    app.view.all().find(element => element.tag === 'button' && element.textContent === 'Load root').listeners.click();
    await settle();
    assert.equal(app.instance.config.Languages.length, 1);
    assert.match(app.view.querySelector('.status').textContent, /request failed/);
});

test('configuration controls use visible Emby checkbox labels and dynamic widget initialization', async () => {
    for (const name of ['configurationCompleted', 'useCoordinates']) {
        assert.match(html(), new RegExp('<label class="checkboxContainer emby-checkbox-label">\\s*'
            + '<input type="checkbox" is="emby-checkbox" class="' + name + '" />\\s*<span>[^<]+</span>\\s*</label>'));
    }
    const app = harness({ Languages: [{ Id: 'l1', Name: 'Saved language', Enabled: true }] });
    await app.instance.onResume();
    const labels = app.view.all().filter(element => element.tag === 'label'
        && element.children[0]?.type === 'checkbox');
    assert.equal(labels.length, 9);
    for (const label of labels) {
        assert.equal(label.className, 'checkboxContainer emby-checkbox-label');
        assert.equal(label.children[0].attributes.is, 'emby-checkbox');
        assert.equal(label.children[1].tag, 'span');
        assert.ok(label.children[1].textContent);
    }
    assert.ok(app.calls.upgrades.includes(app.view));
    const saved = labels.find(label => label.children[1].textContent === 'Saved language').children[0];
    saved.checked = false;
    saved.listeners.change();
    assert.equal(app.instance.config.Languages[0].Enabled, false);
});

test('disabled browsing and empty category selection are explicit; enabling Talk saves an available root category', async () => {
    const app = harness({ SchemaVersion: 1, ConfigurationCompleted: false });
    await app.instance.onResume();
    assert.match(app.view.querySelector('.status').textContent, /Browsing is disabled/);
    app.view.querySelector('.configurationCompleted').checked = true;
    app.view.querySelector('form').listeners.submit({ preventDefault() {} });
    await settle();
    assert.match(app.view.querySelector('.status').textContent, /No categories are enabled/);
    app.view.querySelector('[data-category="EnableTalk"]').checked = true;
    app.view.querySelector('form').listeners.submit({ preventDefault() {} });
    await settle();
    assert.equal(app.calls.saves[1].ConfigurationCompleted, true);
    assert.equal(app.calls.saves[1].EnableTalk, true);
    assert.match(app.view.querySelector('.status').textContent, /Browsing is enabled/);
    assert.equal(app.calls.catalogue.length, 0);
});
