define(['loading', 'jQuery', 'emby-input', 'emby-button', 'emby-checkbox', 'emby-scroller'], function (loading, $) {
    'use strict';
    var pluginId = 'd9314bc3-33e0-4080-88ef-8e7e718524f2';
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
                        selected(instance, spec, saved);
                    }
                }));
                if (spec[0] !== 'language')
                    row.appendChild(button('Expand', function () { load(item.Id, item.Path, false); }));
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
        ApiClient.updatePluginConfiguration(pluginId, config).then(function (result) {
            Dashboard.processPluginConfigurationUpdateResult(result);
            message(instance, 'Configuration saved. ' + browsingStatus(config) + ' No catalogue crawl was started. Use Scheduled Tasks / Internet Channels to refresh a group.');
        }).catch(function () {
            message(instance, 'Configuration was not saved. Check validation and administrator access.');
        }).then(function () { loading.hide(); });
    }

    function View(view, params) {
        this.view = view;
        view.querySelector('form').addEventListener('submit', onSubmit.bind(this));
        view.addEventListener('viewshow', onResume.bind(this));
        view.addEventListener('viewbeforehide', onPause.bind(this));
    }

    function onResume() {
        var instance = this;
        instance.viewActive = true;
        instance.viewEpoch = (instance.viewEpoch || 0) + 1;
        var epoch = instance.viewEpoch;
        loading.show();
        return ApiClient.getPluginConfiguration(pluginId).then(function (config) {
            if (!instance.viewActive || epoch !== instance.viewEpoch) return;
            instance.config = config;
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
            $(view).trigger('create');
            message(instance, browsingStatus(config) + ' Selectors contact TuneIn only when you explicitly load or expand them.');
        }).catch(function () {
            if (instance.viewActive && epoch === instance.viewEpoch)
                message(instance, 'Unable to load plugin configuration. Check administrator access.');
        }).then(function () {
            if (epoch === instance.viewEpoch) loading.hide();
        });
    }

    function onPause() {
        this.viewActive = false;
        this.viewEpoch = (this.viewEpoch || 0) + 1;
    }
    return View;
});
