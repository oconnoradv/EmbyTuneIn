define(['baseView', 'loading', 'jQuery', 'emby-input', 'emby-button', 'emby-checkbox', 'emby-scroller'], function (BaseView, loading, $) {
    'use strict';
    var pluginId = 'd9314bc3-33e0-4080-88ef-8e7e718524f2';
    var categories = [
        ['EnableLocalRadio', 'Local Radio', 'local'], ['EnableMusic', 'Music', 'music'],
        ['EnableLanguage', 'By Language', 'language'], ['EnableLocation', 'By Location', 'location'],
        ['EnableTalk', 'Talk', 'talk'], ['EnableSports', 'Sports', 'sports'],
        ['EnablePodcasts', 'Podcasts', 'podcast'], ['EnableFavorites', 'My Favorites', 'presets']
    ];
    var selectors = [
        ['language', 'Languages', 'By Language'], ['location', 'Locations', 'By Location'],
        ['local', 'LocalCities', 'Local Radio cities/regions'], ['music', 'Music', 'Music genres']
    ];

    function message(instance, text) {
        instance.view.querySelector('.status').textContent = text;
    }

    function button(text, action) {
        var element = document.createElement('button');
        element.type = 'button';
        element.textContent = text;
        element.className = 'raised emby-button';
        element.addEventListener('click', action);
        return element;
    }

    function checkbox(text, checked, onChange) {
        var label = document.createElement('label');
        label.className = 'checkboxContainer emby-checkbox-label';
        var input = document.createElement('input');
        input.type = 'checkbox';
        input.setAttribute('is', 'emby-checkbox');
        input.checked = checked;
        if (onChange) input.addEventListener('change', onChange);
        var caption = document.createElement('span');
        caption.textContent = text;
        label.appendChild(input);
        label.appendChild(caption);
        return { label: label, input: input };
    }

    function browsingStatus(config) {
        if (config.SchemaVersion !== 1 || !config.ConfigurationCompleted)
            return 'Browsing is disabled. Check "Enable configured browsing after saving", enable a category and save.';
        if (!categories.some(function (spec) { return config[spec[0]]; }))
            return 'No categories are enabled. Enable at least one category and supply its required selections, then save.';
        return 'Browsing is enabled. Categories needing selections remain unavailable until those selections are added and enabled. Return to the channel root to see available category folders.';
    }

    function activeRefresh(state) {
        return state === 'Queued' || state === 'Running' || state === 'Synchronizing';
    }

    function changed(instance) {
        instance.dirty = true;
        instance.editVersion = (instance.editVersion || 0) + 1;
    }

    function stopPolling(instance) {
        if (instance.refreshTimer != null) clearTimeout(instance.refreshTimer);
        instance.refreshTimer = null;
    }

    function renderRefresh(instance, data) {
        if (!Array.isArray(data) || data.length !== categories.length) throw new Error('Invalid refresh status.');
        var running = false;
        categories.forEach(function (spec) {
            var status = data.find(function (entry) { return entry.Category === spec[2]; });
            if (!status || ['Idle', 'Queued', 'Running', 'Synchronizing', 'Completed', 'Failed', 'Cancelled'].indexOf(status.State) === -1
                || !Number.isInteger(status.TotalRequests) || status.TotalRequests < 0 || status.TotalRequests > 101
                || !Number.isInteger(status.CompletedRequests) || status.CompletedRequests < 0 || status.CompletedRequests > status.TotalRequests)
                throw new Error('Invalid refresh status.');
            var controls = instance.refreshControls[spec[2]];
            controls.status = status;
            controls.start.disabled = !status.Enabled || activeRefresh(status.State);
            controls.cancel.hidden = !activeRefresh(status.State);
            controls.progress.hidden = status.TotalRequests === 0;
            controls.progress.value = status.TotalRequests ? 100 * status.CompletedRequests / status.TotalRequests : 0;
            controls.text.textContent = status.State + (status.Phase ? ': ' + status.Phase : '')
                + '. API requests: ' + status.CompletedRequests + '/' + status.TotalRequests
                + (status.TotalRequests ? ' (' + Math.floor(controls.progress.value) + '%)' : '')
                + '. HTTP: ' + (status.LastHttpStatus == null ? 'no response received yet' : status.LastHttpStatus)
                + (status.LastApiResult ? '; ' + status.LastApiResult : '')
                + '. Returned station rows: ' + (status.StationRows || 0) + '; links: ' + (status.FolderRows || 0)
                + (status.FinishedUtc ? '. Finished: ' + new Date(status.FinishedUtc).toLocaleString() : '')
                + (status.Error ? '. ' + status.Error : '')
                + (!status.Enabled ? '. Save and enable this group with its required selections first.' : '');
            running = running || activeRefresh(status.State);
        });
        return running;
    }

    function loadRefreshStatus(instance) {
        stopPolling(instance);
        if (!instance.refreshActive) return;
        var epoch = instance.refreshEpoch;
        var requestId = instance.refreshRequest = (instance.refreshRequest || 0) + 1;
        return ApiClient.ajax({ type: 'GET', dataType: 'json', url: ApiClient.getUrl('TuneInFork/Refresh') })
            .then(function (data) {
                if (!instance.refreshActive || epoch !== instance.refreshEpoch || requestId !== instance.refreshRequest) return;
                var running = renderRefresh(instance, data);
                instance.view.querySelector('.refreshStatus').textContent = 'Status checks do not contact TuneIn. Percentages measure planned API requests, not all stations or Emby import progress.';
                if (running) instance.refreshTimer = setTimeout(function () { loadRefreshStatus(instance); }, 1000);
            }).catch(function () {
                if (instance.refreshActive && epoch === instance.refreshEpoch && requestId === instance.refreshRequest)
                    instance.view.querySelector('.refreshStatus').textContent = 'Refresh status unavailable. Check administrator access/connectivity, then choose Check refresh status. Existing server jobs are not cancelled.';
            });
    }

    function createRefreshControls(instance) {
        var container = instance.view.querySelector('.refreshGroups');
        container.textContent = '';
        instance.refreshControls = {};
        categories.forEach(function (spec) {
            var section = document.createElement('section');
            var title = document.createElement('h4');
            title.textContent = spec[1];
            section.appendChild(title);
            var controls = {};
            function command(cancel) {
                if (!instance.refreshActive) return;
                if (!cancel && instance.dirty) {
                    instance.view.querySelector('.refreshStatus').textContent = 'Save configuration changes before refreshing a radio group.';
                    return;
                }
                controls.start.disabled = true;
                controls.cancel.disabled = true;
                ApiClient.ajax({
                    type: 'POST', dataType: 'json',
                    url: ApiClient.getUrl('TuneInFork/Refresh/' + spec[2] + (cancel ? '/Cancel' : ''),
                        cancel ? { Id: controls.status.Id } : {})
                }).then(function () { return loadRefreshStatus(instance); }).catch(function () {
                    if (instance.refreshActive)
                        instance.view.querySelector('.refreshStatus').textContent = 'Refresh command failed. Check saved configuration and administrator access, then check refresh status.';
                }).then(function () {
                    controls.cancel.disabled = false;
                    controls.start.disabled = !controls.status || !controls.status.Enabled || activeRefresh(controls.status.State);
                });
            }
            controls.start = button('Refresh ' + spec[1], function () { command(false); });
            controls.start.disabled = true;
            controls.cancel = button('Cancel refresh', function () { command(true); });
            controls.cancel.hidden = true;
            controls.progress = document.createElement('progress');
            controls.progress.max = 100;
            controls.progress.value = 0;
            controls.progress.hidden = true;
            controls.progress.setAttribute('aria-label', spec[1] + ' completed API requests');
            controls.text = document.createElement('p');
            controls.text.textContent = 'Checking administrator refresh status...';
            [controls.start, controls.cancel, controls.progress, controls.text].forEach(function (element) { section.appendChild(element); });
            instance.refreshControls[spec[2]] = controls;
            container.appendChild(section);
        });
        container.appendChild(button('Check refresh status', function () { loadRefreshStatus(instance); }));
    }

    function selected(instance, spec, container) {
        container.textContent = '';
        (instance.config[spec[1]] || []).forEach(function (item) {
            var row = document.createElement('div');
            var control = checkbox(item.Path || item.Name || item.Id, item.Enabled !== false,
                function () { item.Enabled = control.input.checked; changed(instance); });
            row.appendChild(control.label);
            row.appendChild(button('Remove', function () {
                instance.config[spec[1]] = instance.config[spec[1]].filter(function (entry) { return entry !== item; });
                changed(instance);
                selected(instance, spec, container);
            }));
            container.appendChild(row);
        });
        $(container).trigger('create');
    }

    function createSelector(instance, spec) {
        var section = document.createElement('section');
        var heading = document.createElement('h4');
        heading.textContent = spec[2];
        section.appendChild(heading);
        var saved = document.createElement('div');
        section.appendChild(saved);
        selected(instance, spec, saved);
        var results = document.createElement('div');
        var search = document.createElement('input');
        search.type = 'search';
        search.placeholder = 'Filter loaded labels';
        search.setAttribute('aria-label', 'Filter ' + spec[2]);
        var currentParent = '';
        var currentPath = '';
        var rows = [];
        var busy = false;

        function render() {
            results.textContent = '';
            rows.filter(function (item) {
                return (item.Name || '').toLowerCase().indexOf(search.value.toLowerCase()) !== -1;
            }).forEach(function (item) {
                var row = document.createElement('div');
                var text = document.createElement('span');
                text.textContent = (item.Name || item.Id) + ' ';
                row.appendChild(text);
                row.appendChild(button('Add', function () {
                    var list = instance.config[spec[1]] || [];
                    if (!list.some(function (entry) { return entry.Id === item.Id; })) {
                        list.push({ Id: item.Id, Name: item.Name, Path: item.Path, AncestorIds: item.AncestorIds, Enabled: true });
                        instance.config[spec[1]] = list;
                        changed(instance);
                        selected(instance, spec, saved);
                    }
                }));
                if (spec[0] !== 'language') {
                    row.appendChild(button('Expand', function () { load(item.Id, item.Path, false); }));
                }
                results.appendChild(row);
            });
        }

        function load(parent, path, refresh) {
            if (busy) return;
            busy = true;
            message(instance, 'Loading ' + spec[2] + '...');
            ApiClient.ajax({
                type: 'GET', dataType: 'json',
                url: ApiClient.getUrl('TuneInFork/Selectors', { Category: spec[0], Parent: parent, Path: path, Refresh: refresh })
            }).then(function (data) {
                if (!data || !Array.isArray(data.Items)) throw new Error('Invalid selector response.');
                currentParent = parent;
                currentPath = path;
                rows = data.Items;
                render();
                message(instance, (path ? path + ': ' : '') + rows.length + ' choices. ' + (data.Warning || ''));
            }).catch(function () {
                message(instance, 'Selector request failed. Check administrator access, connectivity and Emby server errors; existing selections were preserved.');
            }).then(function () { busy = false; });
        }

        section.appendChild(button('Load root', function () { load('', '', false); }));
        section.appendChild(button('Refresh current branch', function () { load(currentParent, currentPath, true); }));
        section.appendChild(search);
        search.addEventListener('input', render);
        section.appendChild(results);
        return section;
    }

    function onSubmit(event) {
        event.preventDefault();
        var instance = this;
        var config = instance.config;
        if (!config) {
            message(instance, 'Configuration is unavailable. Reload this page before saving.');
            return;
        }
        var coordinates = instance.view.querySelector('.txtLatLon').value.trim();
        var useCoordinates = instance.view.querySelector('.useCoordinates').checked;
        var parts = coordinates.split(',');
        if (useCoordinates && (parts.length !== 2 || parts.some(function (part) {
            return !part.trim() || !isFinite(Number(part));
        }) || Math.abs(Number(parts[0])) > 90 || Math.abs(Number(parts[1])) > 180)) {
            message(instance, 'Enter finite latitude (-90..90), longitude (-180..180), separated by a comma.');
            return;
        }
        config.SchemaVersion = 1;
        config.ConfigurationCompleted = instance.view.querySelector('.configurationCompleted').checked;
        config.Username = instance.view.querySelector('.txtUsername').value.trim() || null;
        config.LatLon = coordinates || null;
        config.UseCoordinates = useCoordinates;
        categories.forEach(function (spec) {
            config[spec[0]] = instance.view.querySelector('[data-category="' + spec[0] + '"]').checked;
        });
        loading.show();
        var savedEditVersion = instance.editVersion;
        ApiClient.updatePluginConfiguration(pluginId, config).then(function (result) {
            Dashboard.processPluginConfigurationUpdateResult(result);
            instance.dirty = instance.editVersion !== savedEditVersion;
            message(instance, 'Configuration saved. ' + browsingStatus(config) + ' No catalogue crawl was started.');
            loadRefreshStatus(instance);
        }).catch(function () {
            message(instance, 'Configuration was not saved. Check validation and administrator access.');
        }).then(function () { loading.hide(); });
    }

    function View(view, params) {
        BaseView.apply(this, arguments);
        view.querySelector('form').addEventListener('submit', onSubmit.bind(this));
        var instance = this;
        ['input', 'change'].forEach(function (event) {
            view.querySelector('form').addEventListener(event, function () { if (!instance.loadingConfig) changed(instance); });
        });
    }
    Object.assign(View.prototype, BaseView.prototype);

    View.prototype.onResume = function () {
        BaseView.prototype.onResume.apply(this, arguments);
        var instance = this;
        stopPolling(instance);
        instance.refreshActive = true;
        instance.refreshEpoch = (instance.refreshEpoch || 0) + 1;
        instance.loadingConfig = true;
        loading.show();
        ApiClient.getPluginConfiguration(pluginId).then(function (config) {
            instance.config = config;
            instance.dirty = false;
            var view = instance.view;
            view.querySelector('.txtUsername').value = config.Username || '';
            view.querySelector('.txtLatLon').value = config.LatLon || '';
            view.querySelector('.useCoordinates').checked = !!config.UseCoordinates;
            view.querySelector('.configurationCompleted').checked = config.SchemaVersion === 1 && !!config.ConfigurationCompleted;
            var list = view.querySelector('.categories');
            list.textContent = '';
            categories.forEach(function (spec) {
                var row = document.createElement('div');
                var control = checkbox(spec[1], !!config[spec[0]]);
                control.input.setAttribute('data-category', spec[0]);
                row.appendChild(control.label);
                list.appendChild(row);
            });
            var pickerList = view.querySelector('.selectors');
            pickerList.textContent = '';
            selectors.forEach(function (spec) { pickerList.appendChild(createSelector(instance, spec)); });
            createRefreshControls(instance);
            loadRefreshStatus(instance);
            $(view).trigger('create');
            message(instance, browsingStatus(config) + ' Selectors contact TuneIn only when you explicitly load or expand them.');
        }).catch(function () {
            message(instance, 'Unable to load plugin configuration. Check administrator access.');
        }).then(function () { instance.loadingConfig = false; loading.hide(); });
    };
    View.prototype.onPause = function () {
        this.refreshActive = false;
        this.refreshEpoch = (this.refreshEpoch || 0) + 1;
        stopPolling(this);
        if (BaseView.prototype.onPause) BaseView.prototype.onPause.apply(this, arguments);
    };
    return View;
});
