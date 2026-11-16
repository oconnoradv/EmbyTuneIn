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
        'categories', 'selectors', 'status']) {
        const element = new Element(name === 'form' ? 'form' : 'div');
        element.className = name;
        view.appendChild(element);
    }
    const calls = { catalogue: [], saves: [], configReads: [], configWrites: [], upgrades: [] };
    let View;
    function BaseView(element) { this.view = element; }
    BaseView.prototype.onResume = function () { return this.view.listeners.viewshow(); };
    BaseView.prototype.onPause = function () { this.view.listeners.viewbeforehide(); };
    BaseView.prototype.onBeginResume = function () { return 'native transition hook'; };
    const ApiClient = {
        getPluginConfiguration: async id => { calls.configReads.push(id); return JSON.parse(JSON.stringify(config)); },
        getUrl: (route, query) => ({ route, query }),
        ajax: async request => {
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
        Dashboard: { processPluginConfigurationUpdateResult() {} }
    });
    return { instance: new View(view), view, calls, ApiClient };
}

const settle = () => new Promise(resolve => setImmediate(resolve));
const html = () => fs.readFileSync(path.join(__dirname, '..', 'MediaBrowser.Plugins.TuneIn',
    'Configuration', 'configPage.html'), 'utf8');

test('settings contain no refresh or interval controls and never call their APIs on resume or save', async () => {
    assert.doesNotMatch(html(), /data-section="(?:schedules|refresh)"|scheduleControls|refreshGroups/);
    assert.match(html(), /Scheduled Tasks \/ Internet Channels/);
    const app = harness({ SchemaVersion: 1, ConfigurationCompleted: true, EnableTalk: true });
    const requests = [];
    app.ApiClient.ajax = async request => { requests.push(request); throw new Error('Unexpected request'); };
    await app.instance.onResume();
    app.view.querySelector('form').listeners.submit({ preventDefault() {} });
    await settle();
    app.instance.onPause();
    await app.instance.onResume();
    assert.equal(requests.length, 0);
    assert.equal(app.calls.saves.length, 1);
    assert.equal(app.view.all().some(element => /^Refresh (?:Local Radio|Music|By Language|By Location|Talk|Sports|Podcasts|My Favorites)$/.test(element.textContent)), false);
});

test('settings preserve native view hooks and labeled inputs', async () => {
    const app = harness({});
    assert.equal(app.instance.onBeginResume(), 'native transition hook');
    await app.instance.onResume();
    assert.match(html(), /class="inputContainer"[\s\S]*class="txtUsername"[^>]+label=/);
    assert.match(html(), /class="txtLatLon"[^>]+label=/);
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

test('four settings sections collapse independently while setup and Save remain accessible', () => {
    const sections = [...html().matchAll(/<details\b([^>]*)>([\s\S]*?)<\/details>/g)];
    assert.equal(sections.length, 4);
    assert.deepEqual(sections.map(section => section[2].match(/<summary>([^<]+)<\/summary>/)[1]),
        ['Setup and account', 'Radio groups', 'Local Radio coordinates', 'Station selections']);
    assert.match(sections[0][1], /\bopen\b/);
    for (const section of sections.slice(1)) assert.doesNotMatch(section[1], /\bopen\b/);
    for (const section of sections) assert.doesNotMatch(section[2], /type="submit"/);
    assert.match(html(), /<button[^>]*type="submit"[^>]*><span>Save configuration<\/span><\/button>/);
});

test('resume and save do not crawl; setup preserves legacy account/coordinates and remains explicitly gated', async () => {
    const app = harness({ Username: 'existing-account', LatLon: '1,2', SchemaVersion: 0 });
    await app.instance.onResume();
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
