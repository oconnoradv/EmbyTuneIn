define(['baseView', 'loading', 'jQuery', 'emby-input', 'emby-button', 'emby-checkbox', 'emby-scroller', 'emby-select'], function (BaseView, loading, $) {
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
        return 'Browsing is enabled. Return to the channel root after saving.';
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
        var requests = new Map();
        function add(item, feedback) {
            if (epoch !== instance.viewEpoch || instance.saving) return;
            var list = instance.config[spec[1]] || [];
            var existing = list.find(function (entry) { return entry.Id === item.Id; });
            if (existing) existing.Enabled = true;
            else {
                if (list.length >= 100) {
                    feedback.textContent = 'Remove a saved choice before adding another (limit: 100).';
                    return;
                }
                list.push({ Id: item.Id, Name: item.Name, Path: item.Path, AncestorIds: item.AncestorIds, Enabled: true });
                instance.config[spec[1]] = list;
            }
            selected(instance, spec, saved);
            feedback.textContent = 'Selected: ' + item.Path;
        }

        function branch(parent, caption) {
            var details = document.createElement('details');
            var summary = document.createElement('summary');
            summary.textContent = caption;
            details.appendChild(summary);
            var content = document.createElement('div');
            details.appendChild(content);
            var status = document.createElement('p');
            status.setAttribute('role', 'status');
            content.appendChild(status);
            var results = document.createElement('ul');
            content.appendChild(results);
            var loaded = false;
            var pending;
            function load() {
                if (epoch !== instance.viewEpoch || loaded) return;
                if (pending) return pending;
                status.textContent = 'Loading choices...';
                results.textContent = '';
                if (!requests.has(parent)) requests.set(parent, ApiClient.ajax({
                    type: 'GET', dataType: 'json',
                    url: ApiClient.getUrl('TuneIn/Selectors', { Category: spec[0], Parent: parent })
                }).then(function (data) {
                    if (!data || !Array.isArray(data.Items)) throw new Error('Invalid selector response.');
                    return data;
                }));
                pending = requests.get(parent).then(function (data) {
                    if (epoch !== instance.viewEpoch) return;
                    loaded = true;
                    status.textContent = data.Warning || (data.Items.length ? '' : 'No further choices in this branch.');
                    data.Items.forEach(function (item) {
                        var row = document.createElement('li');
                        var name = item.Name || item.Id;
                        if (item.CanExpand) row.appendChild(branch(item.Id, name));
                        else {
                            var label = document.createElement('span');
                            label.textContent = name;
                            row.appendChild(label);
                        }
                        if (item.CanSelect) {
                            var select = button('Add', function () { add(item, status); });
                            select.setAttribute('aria-label', 'Add ' + (item.Path || name));
                            row.appendChild(select);
                        }
                        results.appendChild(row);
                    });
                    $(content).trigger('create');
                }).catch(function (error) {
                    requests.delete(parent);
                    if (epoch !== instance.viewEpoch) return;
                    console.error('Unable to load TuneIn selector.', error);
                    status.textContent = 'Could not load choices from TuneIn.';
                    var retry = document.createElement('li');
                    retry.appendChild(button('Retry', load));
                    results.appendChild(retry);
                    $(content).trigger('create');
                }).then(function () { pending = null; });
                return pending;
            }
            details.addEventListener('toggle', function () {
                if (details.open) return load();
            });
            return details;
        }
        section.appendChild(branch('', 'Browse ' + spec[2]));
        return section;
    }

    function editing(instance) {
        var disabled = !instance.config || instance.saving || instance.refreshBusy;
        instance.view.querySelector('.configurationFields').disabled = disabled;
        instance.view.querySelector('.button-submit').disabled = disabled;
    }

    function renderRefresh(instance, status) {
        instance.refreshBusy = !!status.Busy;
        var category = ({ local: 'Local Radio', music: 'Music', language: 'By Language', location: 'By Location',
            talk: 'Talk', sports: 'Sports', podcast: 'Podcasts', presets: 'My Favorites' })[status.Category];
        var description = status.State === 'NeverRun' ? 'No Control Panel refresh has run.' :
            [status.State, status.Phase, category].filter(Boolean).join(' - ');
        if (status.CategoriesTotal) description += '. ' + status.CategoriesCompleted + ' of ' + status.CategoriesTotal + ' categories checked';
        if (status.CategoriesSkipped) description += '; ' + status.CategoriesSkipped + ' unchanged';
        if (status.LastHttpStatus) description += '. TuneIn returned HTTP ' + status.LastHttpStatus;
        else if (status.ErrorCode) description += '. ' + status.ErrorCode;
        if (status.ImportMayHavePartiallyApplied) description += '. An interrupted import may have changed Emby metadata; run refresh again to reconcile.';
        instance.view.querySelector('.refreshStatus').textContent = description;
        refreshControls(instance, true);
    }

    function refreshControls(instance, available) {
        instance.view.querySelector('.refreshStart').disabled = !available || instance.refreshBusy || !!instance.refreshAction || instance.saving;
        instance.view.querySelector('.refreshCancel').disabled = !available || !instance.refreshBusy || !!instance.refreshAction;
        editing(instance);
    }

    function scheduleStatus(instance, epoch) {
        if (epoch !== instance.viewEpoch || !instance.refreshVisible) return;
        clearTimeout(instance.refreshTimer);
        instance.refreshTimer = setTimeout(function () { pollStatus(instance, epoch); }, instance.refreshBusy ? 1500 : 10000);
    }

    function pollStatus(instance, epoch) {
        if (epoch !== instance.viewEpoch || !instance.refreshVisible) return;
        clearTimeout(instance.refreshTimer);
        if (instance.refreshAction) return scheduleStatus(instance, epoch);
        if (instance.refreshPoll) return instance.refreshPoll.then(function () { return pollStatus(instance, epoch); });
        var generation = instance.refreshGeneration || 0;
        instance.refreshPoll = ApiClient.ajax({
            type: 'GET', dataType: 'json', url: ApiClient.getUrl('TuneIn/Refresh/Status')
        }).then(function (status) {
            if (epoch === instance.viewEpoch && generation === (instance.refreshGeneration || 0)) renderRefresh(instance, status);
        }).catch(function () {
            if (epoch === instance.viewEpoch && generation === (instance.refreshGeneration || 0)) {
                instance.view.querySelector('.refreshStatus').textContent = 'Could not read refresh status. Retrying automatically.';
                refreshControls(instance, false);
            }
        }).then(function () {
            instance.refreshPoll = null;
            scheduleStatus(instance, epoch);
        });
        return instance.refreshPoll;
    }

    function doRefresh(instance, action) {
        if (!instance.refreshVisible || instance.refreshAction) return;
        var epoch = instance.viewEpoch;
        instance.refreshGeneration = (instance.refreshGeneration || 0) + 1;
        instance.refreshAction = true;
        refreshControls(instance, false);
        instance.view.querySelector('.refreshError').textContent = '';
        return ApiClient.ajax({ type: 'POST', dataType: 'json', url: ApiClient.getUrl('TuneIn/Refresh/' + action) })
            .then(function (status) {
                if (epoch === instance.viewEpoch) renderRefresh(instance, status);
            }).catch(function () {
                if (epoch === instance.viewEpoch)
                    instance.view.querySelector('.refreshError').textContent = 'Could not ' + action.toLowerCase() + ' refresh. Check the current status before retrying.';
            }).then(function () {
                instance.refreshAction = false;
                if (instance.refreshVisible) pollStatus(instance, instance.viewEpoch);
            });
    }

    function onSubmit(event) {
        event.preventDefault();
        var instance = this;
        var epoch = instance.viewEpoch;
        if (!instance.config || instance.saving || instance.refreshBusy) return false;
        var config = instance.config;
        var coordinates = instance.view.querySelector('.txtLatLon').value.trim();
        var localEnabled = instance.view.querySelector('[data-category="EnableLocalRadio"]').checked;
        var source = instance.view.querySelector('.localSource').value;
        var useCoordinates = localEnabled && source === 'coordinates';
        if (localEnabled && source !== 'coordinates' && (source !== 'regions' || !(config.LocalCities || []).some(function (item) {
            return item && item.Enabled !== false && /^r[1-9][0-9]{0,9}$/.test(item.Id);
        }))) {
            message(instance, 'Choose coordinates or enable at least one saved city or region for Local Radio.');
            return false;
        }
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
        config.LocalRadioSource = source || null;
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
            message(instance, 'Configuration was not saved. Check validation, connectivity and administrator access, then retry.');
        }).then(function () {
            if (epoch !== instance.viewEpoch) return;
            instance.saving = false;
            editing(instance);
            loading.hide();
        });
        return false;
    }

    function View(view, params) {
        BaseView.apply(this, arguments);
        view.querySelector('form').addEventListener('submit', onSubmit.bind(this));
        view.querySelector('.refreshStart').addEventListener('click', doRefresh.bind(null, this, 'Start'));
        view.querySelector('.refreshCancel').addEventListener('click', doRefresh.bind(null, this, 'Cancel'));
    }

    Object.assign(View.prototype, BaseView.prototype);

    View.prototype.onResume = function () {
        BaseView.prototype.onResume.apply(this, arguments);
        var instance = this;
        var epoch = instance.viewEpoch = (instance.viewEpoch || 0) + 1;
        instance.refreshVisible = true;
        instance.view.querySelector('.refreshStatus').textContent = 'Reading refresh status...';
        instance.view.querySelector('.refreshError').textContent = '';
        instance.config = null;
        instance.saving = false;
        instance.loadingConfig = true;
        editing(instance);
        refreshControls(instance, false);
        pollStatus(instance, epoch);
        loading.show();
        return ApiClient.getPluginConfiguration(pluginId).then(function (config) {
            if (epoch !== instance.viewEpoch) return;
            instance.config = config;
            var view = instance.view;
            view.querySelector('.txtUsername').value = config.Username || '';
            view.querySelector('.txtLatLon').value = config.LatLon || '';
            var hasRegions = (config.LocalCities || []).some(function (item) {
                return item && item.Enabled !== false && /^r[1-9][0-9]{0,9}$/.test(item.Id);
            });
            var ambiguous = !config.LocalRadioSource && config.UseCoordinates && hasRegions;
            view.querySelector('.localSource').value = config.LocalRadioSource ||
                (ambiguous ? '' : config.UseCoordinates ? 'coordinates' : hasRegions ? 'regions' : '');
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
            message(instance, browsingStatus(config) + (ambiguous && config.EnableLocalRadio ?
                ' Local Radio is disabled until you choose one source and save.' : ''));
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
    };

    View.prototype.onPause = function () {
        this.viewEpoch = (this.viewEpoch || 0) + 1;
        this.refreshVisible = false;
        clearTimeout(this.refreshTimer);
        if (this.loadingConfig || this.saving) {
            this.loadingConfig = false;
            this.saving = false;
            loading.hide();
        }
        if (BaseView.prototype.onPause) return BaseView.prototype.onPause.apply(this, arguments);
    };
    return View;
});
