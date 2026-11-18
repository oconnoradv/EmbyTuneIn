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
    for (const name of ['.localSource', '.configurationCompleted', '.categories', '.selectors',
        '.configurationFields', '.button-submit', '.status', '.refreshStatus', '.refreshError',
        '.refreshStart', '.refreshCancel']) elements[name] = new Element();
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
    const refresh = [];
    const timers = new Map();
    let timerId = 0;
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
            (options.url.route.startsWith('TuneIn/Refresh/') ? refresh : selectors).push({ options, ...request });
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

    return { instance: new PluginView(view), username, latLon, formHandlers, reads, writes, notices, errors, loader,
        elements, selectors, enhanced, calls, handlers, refresh, timers };
}

function open(details) {
    details.open = true;
    return details.handlers.toggle();
}

test('first activation opens and loads Language and Location even when native click defaults are cancelled', async () => {
    const app = harness();
    const visit = app.instance.onResume();
    app.reads[0].resolve({});
    await visit;
    for (const index of [0, 1]) {
        const section = app.elements['.selectors'].childNodes[index];
        const root = section.childNodes.find(node => node.tagName === 'details');
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

test('an open selector retries a transient first failure once without a second click', async () => {
    const app = harness();
    const visit = app.instance.onResume();
    app.reads[0].resolve({});
    await visit;
    const root = app.elements['.selectors'].childNodes[1].childNodes.find(node => node.tagName === 'details');
    const loading = open(root);
    app.selectors[0].reject({ status: 503 });
    await settle();
    assert.match(root.textContent, /Still loading choices/);
    assert.equal(root.descendants().some(node => node.textContent === 'Retry' && node.tagName === 'button'), false);
    assert.equal(open(root), loading);
    assert.equal(app.selectors.length, 1);
    const [timer] = app.timers.values();
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
        const root = app.elements['.selectors'].childNodes[0].childNodes.find(node => node.tagName === 'details');
        const loading = open(root);
        app.selectors[0].reject({ response: { status: 502 } });
        await settle();
        if (leave) app.instance.onPause();
        const [timer] = app.timers.values();
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
    assert.match(html, /class="txtUsername"[^>]*label="Your Tune In username:"/);
    assert.match(html, /class="txtLatLon"[^>]*label="Your LatLon:"/);
    const app = harness();
    assert.equal(app.instance.onBeginResume(), 'native transition');
    const first = app.instance.onResume('first');
    app.reads[0].resolve({ Username: 'listener', LatLon: '1,2' });
    await first;
    assert.equal(app.username.value, 'listener');
    assert.equal(app.latLon.value, '1,2');
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
    const root = section.childNodes.find(node => node.tagName === 'details');
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
    assert.equal(app.writes[0].config.LocalCities[1].Enabled, false);
    assert.equal(app.selectors.length, 1);
});

test('invalid optional coordinates block save only when enabled', async () => {
    const app = harness();
    const visit = app.instance.onResume();
    app.reads[0].resolve({});
    await visit;
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
    const root = app.elements['.selectors'].childNodes[0].childNodes.find(node => node.tagName === 'details');
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
    const section = app.elements['.selectors'].childNodes[0];
    const root = section.childNodes.find(node => node.tagName === 'details');
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
    assert.match(section.childNodes[1].textContent, /Arabic/);
});

test('status polls once per visit, drains prior requests and updates only the current view', async () => {
    const app = harness();
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
    const app = harness();
    const visit = app.instance.onResume();
    app.reads[0].resolve({});
    app.refresh[0].resolve({ State: 'NeverRun', Busy: false });
    await visit;
    await settle();
    const idle = [...app.timers.values()][0];
    assert.equal(idle.delay, 10000);
    idle.handler();
    assert.equal(app.refresh[1].options.type, 'GET');
    const action = app.elements['.refreshStart'].handlers.click();
    assert.equal(app.refresh[2].options.type, 'POST');
    assert.equal(app.refresh[2].options.url.route, 'TuneIn/Refresh/Start');
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
