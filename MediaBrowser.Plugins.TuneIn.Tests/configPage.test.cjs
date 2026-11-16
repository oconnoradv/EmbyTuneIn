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
}

function harness(config) {
    const view = new Element('view');
    for (const name of ['form', 'txtUsername', 'txtLatLon', 'useCoordinates', 'configurationCompleted',
        'categories', 'selectors', 'status', 'refreshGroups', 'refreshStatus']) {
        const element = new Element(name === 'form' ? 'form' : 'div');
        element.className = name;
        view.appendChild(element);
    }
    const calls = { catalogue: [], saves: [], configReads: [], configWrites: [], upgrades: [], refresh: [], timers: new Map() };
    const states = ['local', 'music', 'language', 'location', 'talk', 'sports', 'podcast', 'presets']
        .map(Category => ({ Category, Enabled: Category === 'talk', State: 'Idle', TotalRequests: 0, CompletedRequests: 0 }));
    let timerId = 0;
    let View;
    function BaseView(element) { this.view = element; }
    BaseView.prototype.onResume = function () {};
    const ApiClient = {
        getPluginConfiguration: async id => { calls.configReads.push(id); return JSON.parse(JSON.stringify(config)); },
        getUrl: (route, query) => ({ route, query }),
        ajax: async request => {
            if (request.url.route.startsWith('TuneInFork/Refresh')) {
                calls.refresh.push(request);
                return JSON.parse(JSON.stringify(states));
            }
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
    vm.runInNewContext(fs.readFileSync(path.join(__dirname, '..', 'MediaBrowser.Plugins.TuneIn',
        'Configuration', 'configPage.js'), 'utf8'), {
        define: (dependencies, factory) => {
            assert.equal(dependencies[2], 'jQuery');
            View = factory(BaseView, { show() {}, hide() {} },
                element => ({ trigger(event) { assert.equal(event, 'create'); calls.upgrades.push(element); } }));
        },
        document: {
            createElement: tag => new Element(tag),
            createTextNode: text => { const element = new Element('text'); element.textContent = text; return element; }
        },
        ApiClient,
        setTimeout: callback => { calls.timers.set(++timerId, callback); return timerId; },
        clearTimeout: id => calls.timers.delete(id),
        Dashboard: { processPluginConfigurationUpdateResult() {} }
    });
    return { instance: new View(view), view, calls, ApiClient, states };
}

const settle = () => new Promise(resolve => setImmediate(resolve));

test('resume and save do not crawl; setup preserves legacy account/coordinates and remains explicitly gated', async () => {
    const app = harness({ Username: 'existing-account', LatLon: '1,2', SchemaVersion: 0 });
    app.instance.onResume();
    await settle();
    assert.equal(app.calls.catalogue.length, 0);
    assert.equal(app.view.querySelector('.txtUsername').value, 'existing-account');
    assert.equal(app.view.querySelector('.configurationCompleted').checked, false);
    app.view.querySelector('.configurationCompleted').checked = true;
    app.view.querySelector('form').listeners.submit({ preventDefault() {} });
    await settle();
    assert.equal(app.calls.saves.length, 1);
    assert.equal(app.calls.saves[0].SchemaVersion, 1);
    assert.equal(app.calls.saves[0].ConfigurationCompleted, true);
    assert.equal(app.calls.saves[0].LatLon, '1,2');
    assert.equal(app.calls.catalogue.length, 0);
    assert.deepEqual(app.calls.configReads, ['d9314bc3-33e0-4080-88ef-8e7e718524f2']);
    assert.deepEqual(app.calls.configWrites, app.calls.configReads);
});

test('catalogue loads explicitly; remote labels are text and selected stable ancestry is persisted', async () => {
    const app = harness({});
    app.instance.onResume();
    await settle();
    const rootButton = app.view.all().find(element => element.tag === 'button' && element.textContent === 'Load root');
    rootButton.listeners.click();
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
    app.instance.onResume();
    await settle();
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
    app.instance.onResume();
    await settle();
    app.ApiClient.ajax = async () => ({ Items: null });
    app.view.all().find(element => element.tag === 'button' && element.textContent === 'Load root').listeners.click();
    await settle();
    assert.equal(app.instance.config.Languages.length, 1);
    assert.match(app.view.querySelector('.status').textContent, /request failed/);
});

test('configuration controls use visible Emby checkbox labels and dynamic widget initialization', async () => {
    const html = fs.readFileSync(path.join(__dirname, '..', 'MediaBrowser.Plugins.TuneIn',
        'Configuration', 'configPage.html'), 'utf8');
    for (const name of ['configurationCompleted', 'useCoordinates']) {
        assert.match(html, new RegExp('<label class="checkboxContainer emby-checkbox-label">\\s*'
            + '<input type="checkbox" is="emby-checkbox" class="' + name + '" />\\s*<span>[^<]+</span>\\s*</label>'));
    }
    const app = harness({ Languages: [{ Id: 'l1', Name: 'Saved language', Enabled: true }] });
    app.instance.onResume();
    await settle();
    const labels = app.view.all().filter(element => element.tag === 'label');
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
    app.instance.onResume();
    await settle();
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

test('group commands use saved configuration and authenticated host routes; API progress remains separate from host synchronization', async () => {
    const app = harness({ SchemaVersion: 1, ConfigurationCompleted: true, EnableTalk: true });
    app.instance.onResume();
    await settle();
    assert.equal(app.calls.catalogue.length, 0);
    const controls = app.instance.refreshControls.talk;
    app.view.querySelector('form').listeners.input();
    controls.start.listeners.click();
    await settle();
    assert.equal(app.calls.refresh.filter(call => call.type === 'POST').length, 0);
    assert.match(app.view.querySelector('.refreshStatus').textContent, /Save configuration/);
    app.instance.dirty = false;
    Object.assign(app.states.find(state => state.Category === 'talk'), {
        Id: 'opaque-job', State: 'Synchronizing', TotalRequests: 1, CompletedRequests: 1,
        LastHttpStatus: 200, LastApiResult: 'Validated catalogue', Phase: 'API complete; synchronizing Emby'
    });
    controls.start.listeners.click();
    await settle();
    assert.equal(app.calls.refresh.find(call => call.type === 'POST').url.route, 'TuneInFork/Refresh/talk');
    assert.equal(controls.progress.value, 100);
    assert.equal(controls.start.disabled, true);
    assert.equal(controls.cancel.hidden, false);
    assert.match(controls.text.textContent, /Synchronizing/);
    assert.match(controls.text.textContent, /HTTP: 200/);
    assert.equal(app.calls.timers.size, 1);
    controls.cancel.listeners.click();
    await settle();
    assert.equal(app.calls.refresh.filter(call => call.type === 'POST')[1].url.query.Id, 'opaque-job');
    app.instance.onPause();
    assert.equal(app.calls.timers.size, 0);
});

test('late status responses cannot restart polling after leaving the page; resume recovers server job status', async () => {
    const app = harness({});
    let resolve;
    app.ApiClient.ajax = () => new Promise(done => { resolve = done; });
    app.instance.onResume();
    await settle();
    app.instance.onPause();
    Object.assign(app.states[4], { State: 'Running', TotalRequests: 2, CompletedRequests: 1 });
    resolve(app.states);
    await settle();
    assert.equal(app.calls.timers.size, 0);
    app.ApiClient.ajax = async () => app.states;
    app.instance.onResume();
    await settle();
    assert.equal(app.instance.refreshControls.talk.progress.value, 50);
    assert.equal(app.calls.timers.size, 1);
    app.instance.onPause();
    assert.equal(app.calls.timers.size, 0);
});

test('status failures are visible and provider/error text cannot inject markup', async () => {
    const app = harness({});
    Object.assign(app.states[4], {
        State: 'Failed', TotalRequests: 1, CompletedRequests: 0, LastHttpStatus: 429,
        Error: '<img src=x onerror=alert(1)>'
    });
    app.instance.onResume();
    await settle();
    assert.match(app.instance.refreshControls.talk.text.textContent, /HTTP: 429/);
    assert.match(app.instance.refreshControls.talk.text.textContent, /<img/);
    assert.equal(app.view.all().some(element => element.tag === 'img'), false);
    app.ApiClient.ajax = async () => ({ Items: [] });
    app.view.all().find(element => element.textContent === 'Check refresh status').listeners.click();
    await settle();
    assert.match(app.view.querySelector('.refreshStatus').textContent, /status unavailable/);
});

test('new edits during a pending save remain unsaved and stale status responses do not replace current progress', async () => {
    const app = harness({ SchemaVersion: 1, ConfigurationCompleted: true, EnableTalk: true });
    app.instance.onResume();
    await settle();
    let save;
    app.ApiClient.updatePluginConfiguration = () => new Promise(done => { save = done; });
    app.view.querySelector('form').listeners.input();
    app.view.querySelector('form').listeners.submit({ preventDefault() {} });
    app.view.querySelector('form').listeners.change();
    save({});
    await settle();
    assert.equal(app.instance.dirty, true);
    const responses = [];
    app.ApiClient.ajax = () => new Promise(done => responses.push(done));
    const check = app.view.all().find(element => element.textContent === 'Check refresh status');
    check.listeners.click();
    check.listeners.click();
    const latest = JSON.parse(JSON.stringify(app.states));
    Object.assign(latest[4], { State: 'Running', TotalRequests: 2, CompletedRequests: 1 });
    responses[1](latest);
    await settle();
    responses[0](app.states);
    await settle();
    assert.equal(app.instance.refreshControls.talk.progress.value, 50);
    assert.equal(app.calls.timers.size, 1);
    app.instance.onPause();
});
