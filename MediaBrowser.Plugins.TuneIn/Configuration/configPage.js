define(['loading', 'jQuery', 'emby-input', 'emby-button', 'emby-checkbox', 'emby-scroller'], function (loading, $) {
    'use strict';
    var pluginId = '10b82431-f939-4507-a8e2-9509dd3fd6ae';
    var categories = [
        ['EnableLocalRadio', 'Local Radio'], ['EnableMusic', 'Music'],
        ['EnableLanguage', 'By Language'], ['EnableLocation', 'By Location'],
        ['EnableTalk', 'Talk'], ['EnableSports', 'Sports'],
        ['EnablePodcasts', 'Podcasts'], ['EnableFavorites', 'My Favorites']
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
        element.setAttribute('is', 'emby-button');
        element.textContent = text;
        element.className = 'raised emby-button';
        element.addEventListener('click', action);
        return element;
    }

    function checkbox(text, checked, action) {
        var label = document.createElement('label');
        label.className = 'emby-checkbox-label';
        var input = document.createElement('input');
        input.type = 'checkbox';
        input.setAttribute('is', 'emby-checkbox');
        input.checked = checked;
        if (action) input.addEventListener('change', action);
        var caption = document.createElement('span');
        caption.textContent = text;
        label.appendChild(input);
        label.appendChild(caption);
        return { label: label, input: input };
    }

    function browsingStatus(config) {
        if (config.SchemaVersion !== 1 || !config.ConfigurationCompleted)
            return 'Browsing is disabled. Check "Enable configured browsing after saving", enable categories and save.';
        return 'Browsing is enabled. Music, language, location and local radio need enabled selections (or local coordinates); favorites need a username. Return to the channel root after saving.';
    }

    function selected(instance, spec, container) {
        container.textContent = '';
        (instance.config[spec[1]] || []).forEach(function (item) {
            var row = document.createElement('div');
            var control = checkbox(item.Path || item.Name || item.Id, item.Enabled !== false,
                function () { item.Enabled = control.input.checked; });
            row.appendChild(control.label);
            row.appendChild(button('Remove', function () {
                instance.config[spec[1]] = instance.config[spec[1]].filter(function (entry) { return entry !== item; });
                selected(instance, spec, container);
            }));
            container.appendChild(row);
        });
        $(container).trigger('create');
    }

    function createSelector(instance, spec) {
        var epoch = instance.viewEpoch;
        var section = document.createElement('section');
        var heading = document.createElement('h3');
        heading.textContent = spec[2];
        section.appendChild(heading);
        var saved = document.createElement('div');
        section.appendChild(saved);
        selected(instance, spec, saved);
        var results = document.createElement('div');
        var search = document.createElement('input');
        search.type = 'search';
        search.placeholder = 'Filter loaded labels';
        search.setAttribute('is', 'emby-input');
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
                    if (epoch !== instance.viewEpoch) return;
                    var list = instance.config[spec[1]] || [];
                    if (!list.some(function (entry) { return entry.Id === item.Id; })) {
                        if (list.length >= 100) {
                            message(instance, 'Each selector supports up to 100 saved choices. Remove a choice before adding another.');
                            return;
                        }
                        list.push({ Id: item.Id, Name: item.Name, Path: item.Path, AncestorIds: item.AncestorIds, Enabled: true });
                        instance.config[spec[1]] = list;
                        selected(instance, spec, saved);
                    }
                }));
                if (spec[0] !== 'language')
                    row.appendChild(button('Expand', function () { load(item.Id, item.Path, false); }));
                results.appendChild(row);
            });
            if (!results.childNodes.length) results.textContent = 'No matching choices in this branch.';
            $(results).trigger('create');
        }

        function load(parent, path, refresh) {
            if (busy || epoch !== instance.viewEpoch) return;
            busy = true;
            message(instance, 'Loading ' + spec[2] + '...');
            return ApiClient.ajax({
                type: 'GET', dataType: 'json',
                url: ApiClient.getUrl('TuneIn/Selectors', { Category: spec[0], Parent: parent, Refresh: refresh })
            }).then(function (data) {
                if (epoch !== instance.viewEpoch) return;
                if (!data || !Array.isArray(data.Items)) throw new Error('Invalid selector response.');
                currentParent = parent;
                currentPath = path;
                rows = data.Items;
                render();
                message(instance, (path ? path + ': ' : '') + rows.length + ' choices. ' + (data.Warning || ''));
            }).catch(function (error) {
                if (epoch !== instance.viewEpoch) return;
                console.error('Unable to load TuneIn selector.', error);
                message(instance, 'Selector request failed. Check administrator access and connectivity, then retry Load root. Existing selections were preserved.');
            }).then(function () { busy = false; });
        }

        section.appendChild(button('Load root', function () { return load('', '', false); }));
        section.appendChild(button('Refresh current branch', function () { return load(currentParent, currentPath, true); }));
        section.appendChild(search);
        search.addEventListener('input', render);
        section.appendChild(results);
        return section;
    }

    function editing(instance) {
        var disabled = !instance.config || instance.saving || instance.refreshBusy;
        instance.view.querySelector('.configurationFields').disabled = disabled;
        instance.view.querySelector('.button-submit').disabled = disabled;
    }

    function onSubmit(event) {
        event.preventDefault();
        var instance = this;
        var epoch = instance.viewEpoch;
        if (!instance.config || instance.saving || instance.refreshBusy) return false;
        var config = instance.config;
        var limits = [
            ['.requestInterval', 'ApiRequestIntervalSeconds', 3, 60],
            ['.refreshTimeout', 'RefreshTimeoutMinutes', 1, 180],
            ['.cacheMinutes', 'CatalogueCacheMinutes', 1, 120]
        ];
        if (limits.some(function (limit) {
            var value = instance.view.querySelector(limit[0]).value;
            return !/^\d+$/.test(value) || Number(value) < limit[2] || Number(value) > limit[3];
        })) {
            message(instance, 'Use whole numbers: request interval 3..60 seconds, timeout 1..180 minutes, cache lifetime 1..120 minutes.');
            return false;
        }
        var coordinates = instance.view.querySelector('.txtLatLon').value.trim();
        var useCoordinates = instance.view.querySelector('.useCoordinates').checked;
        var parts = coordinates.split(',');
        if (useCoordinates && (parts.length !== 2 || parts.some(function (part) {
            return !/^[+-]?(?:\d+(?:\.\d*)?|\.\d+)(?:e[+-]?\d+)?$/i.test(part.trim()) || !isFinite(Number(part));
        }) || Math.abs(Number(parts[0])) > 90 || Math.abs(Number(parts[1])) > 180)) {
            message(instance, 'Enter finite latitude (-90..90), longitude (-180..180), separated by a comma.');
            return false;
        }
        config.SchemaVersion = 1;
        config.ConfigurationCompleted = instance.view.querySelector('.configurationCompleted').checked;
        config.Username = instance.view.querySelector('.txtUsername').value.trim() || null;
        config.LatLon = coordinates || null;
        config.UseCoordinates = useCoordinates;
        limits.forEach(function (limit) { config[limit[1]] = Number(instance.view.querySelector(limit[0]).value); });
        categories.forEach(function (spec) {
            config[spec[0]] = instance.view.querySelector('[data-category="' + spec[0] + '"]').checked;
        });
        instance.saving = true;
        editing(instance);
        loading.show();
        ApiClient.updatePluginConfiguration(pluginId, config).then(function (result) {
            if (epoch !== instance.viewEpoch) return;
            Dashboard.processPluginConfigurationUpdateResult(result);
            message(instance, 'Configuration saved. ' + browsingStatus(config) + ' No catalogue crawl was started.');
        }).catch(function (error) {
            if (epoch !== instance.viewEpoch) return;
            console.error('Unable to save TuneIn configuration.', error);
            message(instance, 'Save did not complete. Check refresh status, validation, connectivity and administrator access; reopen settings before retrying.');
        }).then(function () {
            if (epoch !== instance.viewEpoch) return;
            instance.saving = false;
            editing(instance);
            loading.hide();
        });
        return false;
    }

    function View(view, params) {
        this.view = view;
        view.querySelector('form').addEventListener('submit', onSubmit.bind(this));
        view.addEventListener('viewshow', onResume.bind(this));
        view.addEventListener('viewbeforehide', onPause.bind(this));
        view.querySelector('.checkRefresh').addEventListener('click', checkRefresh.bind(this));
    }

    function checkRefresh() {
        var instance = this;
        var epoch = instance.viewEpoch;
        var control = instance.view.querySelector('.checkRefresh');
        var output = instance.view.querySelector('.refreshStatus');
        control.disabled = true;
        output.textContent = 'Checking local refresh status...';
        return ApiClient.ajax({ type: 'GET', url: ApiClient.getUrl('TuneIn/Refresh/Status'), dataType: 'json' }).then(function (data) {
            if (epoch !== instance.viewEpoch) return;
            instance.refreshBusy = !!data.Busy;
            editing(instance);
            output.textContent = data.State + (data.Category ? ' - ' + data.Category : '')
                + (data.Phase ? ': ' + data.Phase : '') + '. Categories imported: '
                + data.CategoriesCompleted + '/' + data.CategoriesTotal
                + '. Requests completed/started: ' + data.RequestsCompleted + '/' + data.RequestsStarted
                + '; cache hits: ' + data.CacheHits + '. Staged folders served to Emby: '
                + data.ImportFoldersServed + '/' + data.ImportFoldersTotal + '.'
                + (data.LastHttpStatus == null ? '' : ' Last HTTP response: ' + data.LastHttpStatus + '.')
                + (data.Busy ? ' Work is active or draining; saving is unavailable. Check again after it stops.' : '')
                + (data.Resumable ? ' A compatible checkpoint is available for the next run.' : '')
                + (data.ImportMayHavePartiallyApplied ? ' Native import may have partially applied.' : '')
                + (data.ErrorCode ? ' Diagnostic: ' + data.ErrorCode + '.' : '');
        }).catch(function (error) {
            if (epoch !== instance.viewEpoch) return;
            console.error('Unable to read TuneIn refresh status.', error);
            output.textContent = 'Unable to read refresh status. Check administrator access and connectivity, then try again.';
        }).then(function () {
            if (epoch === instance.viewEpoch) control.disabled = false;
        });
    }

    function onResume() {
        var instance = this;
        var epoch = instance.viewEpoch = (instance.viewEpoch || 0) + 1;
        instance.config = null;
        instance.saving = false;
        instance.refreshBusy = false;
        instance.view.querySelector('.checkRefresh').disabled = false;
        instance.view.querySelector('.refreshStatus').textContent = 'Status has not been checked.';
        instance.loadingConfig = true;
        editing(instance);
        loading.show();
        return ApiClient.getPluginConfiguration(pluginId).then(function (config) {
            if (epoch !== instance.viewEpoch) return;
            instance.config = config;
            var view = instance.view;
            view.querySelector('.txtUsername').value = config.Username || '';
            view.querySelector('.txtLatLon').value = config.LatLon || '';
            view.querySelector('.useCoordinates').checked = !!config.UseCoordinates;
            view.querySelector('.configurationCompleted').checked = config.SchemaVersion === 1 && !!config.ConfigurationCompleted;
            view.querySelector('.requestInterval').value = config.ApiRequestIntervalSeconds == null ? 3 : config.ApiRequestIntervalSeconds;
            view.querySelector('.refreshTimeout').value = config.RefreshTimeoutMinutes == null ? 20 : config.RefreshTimeoutMinutes;
            view.querySelector('.cacheMinutes').value = config.CatalogueCacheMinutes == null ? 15 : config.CatalogueCacheMinutes;
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
            $(view).trigger('create');
            message(instance, browsingStatus(config) + ' Selectors contact TuneIn only when you explicitly load or expand them.');
        }).catch(function (error) {
            if (epoch !== instance.viewEpoch) return;
            instance.config = null;
            console.error('Unable to load TuneIn configuration.', error);
            Dashboard.alert('Unable to load TuneIn configuration.');
            message(instance, 'Unable to load plugin configuration. Check administrator access and reopen this page.');
        }).then(function () {
            if (epoch !== instance.viewEpoch) return;
            instance.loadingConfig = false;
            editing(instance);
            loading.hide();
        });
    }

    function onPause() {
        this.viewEpoch = (this.viewEpoch || 0) + 1;
        if (this.loadingConfig || this.saving) {
            this.loadingConfig = false;
            this.saving = false;
            loading.hide();
        }
    }
    return View;
});
