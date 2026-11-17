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
    appendChild(child) { this.childNodes.push(child); return child; }
    setAttribute(name, value) { this.attributes[name] = value; }
    addEventListener(name, handler) { this.handlers[name] = handler; }
    descendants() { return this.childNodes.flatMap(child => [child, ...child.descendants()]); }
}

function harness() {
    const handlers = {};
    const formHandlers = {};
    const username = new Element('input');
    const latLon = new Element('input');
    const form = { addEventListener: (event, handler) => { formHandlers[event] = handler; } };
    const elements = { form, '.txtUsername': username, '.txtLatLon': latLon };
    for (const name of ['.useCoordinates', '.configurationCompleted', '.categories', '.selectors',
        '.configurationFields', '.button-submit', '.status']) elements[name] = new Element();
    const enhanced = [];
    const view = {
        addEventListener(event, handler) { handlers[event] = handler; },
        querySelector(selector) {
            const category = /^\[data-category="(.+)"\]$/.exec(selector);
            return category ? elements['.categories'].descendants().find(item => item.attributes['data-category'] === category[1])
                : elements[selector];
        }
    };
    const reads = [];
    const writes = [];
    const notices = [];
    const errors = [];
    const selectors = [];
    const loader = { shown: 0, hidden: 0, show() { this.shown++; }, hide() { this.hidden++; } };
    const ApiClient = {
        getPluginConfiguration(id) {
            assert.equal(id, pluginId);
            const request = deferred();
            reads.push(request);
            return request.promise;
        },
        updatePluginConfiguration(id, config) {
            writes.push({ id, config });
            return Promise.resolve('saved');
        },
        getUrl(route, params) { return { route, params }; },
        ajax(options) {
            const request = deferred();
            selectors.push({ options, ...request });
            return request.promise;
        }
    };
    const Dashboard = {
        alert(message) { notices.push(message); },
        processPluginConfigurationUpdateResult(result) { notices.push(result); }
    };
    let PluginView;
    vm.runInNewContext(fs.readFileSync(path.join(configDir, 'configPage.js'), 'utf8'), {
        define(dependencies, factory) {
            assert.equal(dependencies[0], 'loading');
            assert.equal(dependencies.includes('baseView'), false);
            PluginView = factory(loader, element => ({ trigger(event) { enhanced.push({ element, event }); } }));
        },
        ApiClient, Dashboard, console: { error(...args) { errors.push(args); } },
        document: { createElement(tag) { return new Element(tag); } }
    });

    function BaseView(element) { this.view = element; }
    BaseView.prototype.onResume = function () { return handlers.viewshow(); };
    BaseView.prototype.onPause = function () { return handlers.viewbeforehide(); };
    BaseView.prototype.onBeginResume = function () { return 'native transition'; };
    function Adapter(element) { BaseView.call(this, element); PluginView.call(this, element); }
    Object.assign(Adapter.prototype, BaseView.prototype, PluginView.prototype);
    return { instance: new Adapter(view), username, latLon, formHandlers, reads, writes, notices, errors, loader,
        elements, selectors, enhanced };
}

test('native view lifecycle loads labeled upstream settings on each visit', async () => {
    const html = fs.readFileSync(path.join(configDir, 'configPage.html'), 'utf8');
    assert.match(html, /data-controller="__plugin\/tuneinjs"/);
    assert.match(html, /class="txtUsername"[^>]*label="Your Tune In username:"/);
    assert.match(html, /class="txtLatLon"[^>]*label="Your LatLon:"/);
    const app = harness();
    assert.equal(app.instance.onBeginResume(), 'native transition');
    const first = app.instance.onResume();
    app.reads[0].resolve({ Username: 'listener', LatLon: '1,2' });
    await first;
    assert.equal(app.username.value, 'listener');
    assert.equal(app.latLon.value, '1,2');
    app.instance.onPause();
    const second = app.instance.onResume();
    app.reads[1].resolve({ Username: null, LatLon: null });
    await second;
    assert.equal(app.username.value, '');
    assert.equal(app.latLon.value, '');
    assert.equal(app.loader.shown, 2);
    assert.equal(app.loader.hidden, 2);
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
    assert.equal(app.writes[0].config.ConfigurationCompleted, false);
    assert.deepEqual(app.notices, ['saved']);
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
    assert.match(app.elements['.status'].textContent, /Browsing is disabled/);
});

test('only explicit selector actions fetch branches and save multiple local selections', async () => {
    const app = harness();
    const visit = app.instance.onResume();
    app.reads[0].resolve({});
    await visit;
    const section = app.elements['.selectors'].childNodes[2];
    const load = section.childNodes.find(node => node.textContent === 'Load root');
    const loading = load.handlers.click();
    assert.equal(app.selectors.length, 1);
    assert.equal(app.selectors[0].options.url.route, 'TuneIn/Selectors');
    assert.equal(app.selectors[0].options.url.params.Category, 'local');
    app.selectors[0].resolve({ Items: [
        { Id: 'r1', Name: 'First', Path: 'First', AncestorIds: ['r0'] },
        { Id: 'r2', Name: 'Second', Path: 'Second', AncestorIds: ['r0'] }
    ] });
    await loading;
    const buttons = section.descendants().filter(node => node.textContent === 'Add');
    buttons.forEach(button => button.handlers.click());
    const controls = section.descendants().filter(node => node.tagName === 'input' && node.type === 'checkbox');
    assert.equal(controls.length, 2);
    controls[1].checked = false;
    controls[1].handlers.change();
    app.elements['.configurationCompleted'].checked = true;
    app.instance.view.querySelector('[data-category="EnableLocalRadio"]').checked = true;
    app.formHandlers.submit({ preventDefault() {} });
    await settle();
    assert.equal(app.writes[0].config.ConfigurationCompleted, true);
    assert.equal(app.writes[0].config.EnableLocalRadio, true);
    assert.deepEqual(Array.from(app.writes[0].config.LocalCities, item => item.Id), ['r1', 'r2']);
    assert.equal(app.writes[0].config.LocalCities[1].Enabled, false);
    assert.equal(app.selectors.length, 1);
});

test('invalid optional coordinates block save only when enabled', async () => {
    const app = harness();
    const visit = app.instance.onResume();
    app.reads[0].resolve({});
    await visit;
    app.elements['.useCoordinates'].checked = true;
    for (const invalid of ['NaN,0', '91,0', '0,181', '1,', '1,2,3', '0x10,0']) {
        app.latLon.value = invalid;
        app.formHandlers.submit({ preventDefault() {} });
        assert.equal(app.writes.length, 0);
        assert.match(app.elements['.status'].textContent, /Enter finite latitude/);
    }
    app.elements['.useCoordinates'].checked = false;
    app.formHandlers.submit({ preventDefault() {} });
    await settle();
    assert.equal(app.writes.length, 1);
});

test('stale selector results do not overwrite a later visit', async () => {
    const app = harness();
    const first = app.instance.onResume();
    app.reads[0].resolve({});
    await first;
    const load = app.elements['.selectors'].childNodes[0].childNodes.find(node => node.textContent === 'Load root');
    const loading = load.handlers.click();
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
