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
    var groupNames = {
        local: 'Local Radio', music: 'Music', language: 'By Language', location: 'By Location',
        talk: 'Talk', sports: 'Sports', podcast: 'Podcasts', presets: 'My Favorites'
    };

    function nonnegativeInteger(value) {
        return typeof value === 'number' && isFinite(value) && value >= 0 && Math.floor(value) === value;
    }

    function message(instance, text) {
        instance.view.querySelector('.status').textContent = text;
    }

    function updateRefreshSection(instance) {
        var active = instance.refreshActive || instance.nativeActive;
        if (active && !instance.refreshWasActive)
            instance.view.querySelector('.channelRefreshSection').open = true;
        instance.refreshWasActive = active;
    }

    function updateEditingState(instance) {
        var unavailable = !instance.lockKnown || instance.lockBusy || instance.settingsLocked
            || instance.refreshActive || instance.nativeActive || instance.saving;
        instance.view.querySelector('.configurationFields').disabled = unavailable;
        instance.view.querySelector('.button-submit').disabled = unavailable;
        var toggle = instance.view.querySelector('.settingsLocked');
        toggle.disabled = !instance.lockKnown || instance.lockBusy;
        toggle.checked = !!instance.settingsLocked;
        instance.view.querySelector('.settingsLockMessage').textContent = !instance.lockKnown
            ? 'Unable to confirm settings lock and refresh state. Reload or retry status.'
            : instance.refreshActive || instance.nativeActive ? 'Settings cannot be saved while a refresh is active.'
                : instance.settingsLocked ? 'Settings are locked. Unlock them to make changes.'
                    : 'Settings are unlocked.';
    }

    function readSettingsLock(instance) {
        if (instance.lockBusy) return Promise.resolve();
        var epoch = instance.viewEpoch;
        var version = instance.lockVersion;
        return ApiClient.ajax({ type: 'GET', dataType: 'json', url: ApiClient.getUrl('TuneInFork/SettingsLock') })
            .then(function (state) {
                if (!instance.viewActive || epoch !== instance.viewEpoch || version !== instance.lockVersion) return;
                if (!state || typeof state.Locked !== 'boolean' || typeof state.RefreshActive !== 'boolean')
                    throw new Error('Invalid settings lock state.');
                instance.settingsLocked = state.Locked;
                instance.refreshActive = state.RefreshActive;
                instance.lockKnown = true;
                updateEditingState(instance);
                updateRefreshSection(instance);
            }).catch(function () {
                if (!instance.viewActive || epoch !== instance.viewEpoch || version !== instance.lockVersion) return;
                instance.lockKnown = false;
                updateEditingState(instance);
            });
    }

    function changeSettingsLock(instance) {
        if (!instance.lockKnown || instance.lockBusy) return;
        var epoch = instance.viewEpoch;
        var requested = instance.view.querySelector('.settingsLocked').checked;
        instance.lockVersion++;
        instance.lockBusy = true;
        updateEditingState(instance);
        return ApiClient.ajax({ type: 'POST', dataType: 'json', contentType: 'application/json',
            url: ApiClient.getUrl('TuneInFork/SettingsLock'), data: JSON.stringify({ Locked: requested }) })
            .then(function (state) {
                if (!instance.viewActive || epoch !== instance.viewEpoch) return;
                if (!state || state.Locked !== requested || typeof state.RefreshActive !== 'boolean')
                    throw new Error('Invalid settings lock state.');
                instance.settingsLocked = state.Locked;
                instance.refreshActive = state.RefreshActive;
                instance.lockKnown = true;
                updateRefreshSection(instance);
            }).catch(function () {
                if (!instance.viewActive || epoch !== instance.viewEpoch) return;
                instance.lockKnown = false;
                message(instance, 'Unable to change the settings lock. Reload or retry status.');
            }).then(function () {
                if (!instance.viewActive || epoch !== instance.viewEpoch) return;
                instance.lockBusy = false;
                updateEditingState(instance);
            });
    }

    function pollSettingsLock(instance) {
        if (!instance.viewActive) return;
        var epoch = instance.viewEpoch;
        var wasActive = instance.refreshActive || instance.nativeActive;
        readSettingsLock(instance).then(function () {
            if (instance.viewActive && epoch === instance.viewEpoch) {
                if (instance.refreshActive || instance.nativeActive || wasActive) {
                    updateRefreshStatus(instance, true);
                    updateCategoryStatuses(instance);
                }
                instance.lockTimer = setTimeout(function () { pollSettingsLock(instance); }, 5000);
            }
        });
    }

    function button(text, action) {
        var element = document.createElement('button');
        element.type = 'button';
        element.textContent = text;
        element.className = 'raised emby-button';
        element.addEventListener('click', action);
        return element;
    }

    function refreshControls(instance, busy) {
        instance.refreshBusy = busy;
        instance.view.querySelector('.channelRefreshUpdate').disabled = busy;
        instance.view.querySelector('.channelRefreshFailure').disabled = busy || !instance.failedAttempt;
    }

    function updateRefreshStatus(instance, automatic) {
        if (instance.refreshBusy) return Promise.resolve();
        var epoch = instance.viewEpoch;
        var view = instance.view;
        if (!automatic) {
            instance.failedAttempt = null;
            view.querySelector('.channelRefreshLog').textContent = '';
            view.querySelector('.channelRefreshLog').hidden = true;
            view.querySelector('.channelRefreshMessage').textContent = '';
            view.querySelector('.channelRefreshStatus').textContent = 'Loading refresh status...';
        }
        refreshControls(instance, true);
        return ApiClient.ajax({ type: 'GET', url: ApiClient.getUrl('TuneInFork/RefreshTuneIn'), dataType: 'json' }).then(function (status) {
            if (!instance.viewActive || epoch !== instance.viewEpoch) return;
            if (!status || ['Idle', 'Running', 'Cancelling'].indexOf(status.State) === -1
                || ['NeverRun', 'Completed', 'Failed', 'Cancelled', 'Aborted'].indexOf(status.LastResult) === -1
                || status.LastResult === 'Failed' && (typeof status.FailedAttempt !== 'string' || !status.FailedAttempt))
                throw new Error('Invalid task status.');
            instance.nativeActive = status.State === 'Running' || status.State === 'Cancelling';
            updateRefreshSection(instance);
            updateEditingState(instance);
            var text = 'Refresh TuneIn task: ' + status.State + '. Last result: '
                + (status.LastResult === 'NeverRun' ? 'Never run' : status.LastResult) + '.';
            if (status.State === 'Running' && typeof status.Progress === 'number' && isFinite(status.Progress))
                text += ' Progress: ' + Math.round(Math.max(0, Math.min(100, status.Progress))) + '%.';
            if (status.Outcome && status.Outcome !== 'Running' && status.Outcome !== 'NeverRun')
                text += ' Outcome: ' + status.Outcome + '.';
            if (status.State === 'Running' || status.State === 'Cancelling') {
                var current = status.Current;
                if (current) {
                    if (typeof current.Phase !== 'string' || !current.Phase || current.Phase.length > 200
                        || !nonnegativeInteger(current.CategoryIndex) || !nonnegativeInteger(current.CategoryCount)
                        || current.CategoryIndex > current.CategoryCount
                        || !nonnegativeInteger(current.CompletedRequests) || !nonnegativeInteger(current.StartedRequests)
                        || current.CompletedRequests > current.StartedRequests || !nonnegativeInteger(current.CacheHits)
                        || current.Category != null && !Object.prototype.hasOwnProperty.call(groupNames, current.Category))
                        throw new Error('Invalid refresh stage.');
                    if (current.Category != null)
                        text += ' Category: ' + groupNames[current.Category] + ' (' + current.CategoryIndex + ' of ' + current.CategoryCount + ').';
                    text += '\nStage: ' + current.Phase + '.';
                    text += '\nCatalogue requests completed/started: ' + current.CompletedRequests + '/' + current.StartedRequests
                        + '; cached pages reused: ' + current.CacheHits + '.';
                    if (typeof current.EstimatedProgress === 'number' && isFinite(current.EstimatedProgress)) {
                        text += '\nEstimated progress: ' + Math.round(current.EstimatedProgress) + '%';
                        if (nonnegativeInteger(current.EstimatedCompletedRequests) && nonnegativeInteger(current.EstimatedRequests))
                            text += ' (' + current.EstimatedCompletedRequests + '/' + current.EstimatedRequests + ' estimated requests)';
                        text += '.';
                    }
                } else {
                    text += ' Current stage unavailable; choose Update status to check again.';
                }
            }
            if (status.FinishedUtc) text += ' Last finished: ' + status.FinishedUtc + '.';
            if (typeof status.EstimateWarning === 'string' && status.EstimateWarning)
                text += '\n' + status.EstimateWarning;
            instance.failedAttempt = status.FailedAttempt || null;
            view.querySelector('.channelRefreshStatus').textContent = text;
        }).catch(function () {
            if (instance.viewActive && epoch === instance.viewEpoch)
                view.querySelector('.channelRefreshStatus').textContent = 'Unable to load refresh status. Check administrator access and the server log, then choose Update status.';
        }).then(function () {
            if (instance.viewActive && epoch === instance.viewEpoch) refreshControls(instance, false);
        });
    }

    function updateCategoryStatuses(instance) {
        if (instance.categoriesBusy) return Promise.resolve();
        var epoch = instance.viewEpoch;
        instance.categoriesBusy = true;
        return ApiClient.ajax({ type: 'GET', dataType: 'json',
            url: ApiClient.getUrl('TuneInFork/RefreshTuneIn/Categories') }).then(function (statuses) {
            if (!instance.viewActive || epoch !== instance.viewEpoch) return;
            if (!Array.isArray(statuses) || statuses.length !== Object.keys(groupNames).length)
                throw new Error('Invalid category refresh status list.');
            var seen = {};
            var container = instance.view.querySelector('.refreshGroups');
            container.textContent = '';
            statuses.forEach(function (status) {
                var category = status && status.Category;
                if (!status || !Object.prototype.hasOwnProperty.call(groupNames, category) || seen[category]
                    || typeof status.Enabled !== 'boolean'
                    || ['Idle', 'Queued', 'Running', 'Synchronizing', 'Completed', 'TimedOut', 'Failed', 'Cancelled'].indexOf(status.State) === -1
                    || !nonnegativeInteger(status.CompletedRequests) || !nonnegativeInteger(status.TotalRequests))
                    throw new Error('Invalid category refresh status.');
                seen[category] = true;
                var row = document.createElement('div');
                var label = document.createElement('p');
                var text = groupNames[category] + ': ' + status.State
                    + (status.Enabled ? '.' : ' (disabled or not configured).');
                if (status.Phase && typeof status.Phase === 'string') text += ' ' + status.Phase + '.';
                if (status.TotalRequests) text += ' Requests completed/started: ' + status.CompletedRequests + '/' + status.TotalRequests + '.';
                if (typeof status.EstimatedProgress === 'number' && isFinite(status.EstimatedProgress))
                    text += ' Estimated progress: ' + Math.round(status.EstimatedProgress) + '%.';
                if (status.Error && typeof status.Error === 'string') text += ' ' + status.Error;
                label.textContent = text;
                row.appendChild(label);
                var actionText = status.Resumable ? 'Resume ' : status.State === 'TimedOut' ? 'Restart ' : 'Refresh ';
                var action = button(actionText + groupNames[category], function () {
                    return startCategory(instance, category, status.Resumable ? status.Id : null);
                });
                action.disabled = !instance.lockKnown || !status.Enabled || instance.refreshActive || instance.nativeActive || instance.categoryActionBusy
                    || !!status.Resumable && (typeof status.Id !== 'string' || !status.Id);
                row.appendChild(action);
                if (status.Resumable) {
                    var restart = button('Restart ' + groupNames[category], function () {
                        return startCategory(instance, category, null);
                    });
                    restart.disabled = !instance.lockKnown || !status.Enabled || instance.refreshActive || instance.nativeActive || instance.categoryActionBusy;
                    row.appendChild(restart);
                }
                container.appendChild(row);
            });
        }).catch(function () {
            if (instance.viewActive && epoch === instance.viewEpoch)
                instance.view.querySelector('.refreshGroups').textContent = 'Unable to load category refresh status. Check administrator access and choose Update status.';
        }).then(function () {
            if (instance.viewActive && epoch === instance.viewEpoch) instance.categoriesBusy = false;
        });
    }

    function startCategory(instance, category, attempt) {
        if (instance.categoryActionBusy || instance.refreshActive || instance.nativeActive) return;
        var epoch = instance.viewEpoch;
        instance.categoryActionBusy = true;
        instance.view.querySelector('.channelRefreshMessage').textContent = 'Starting ' + groupNames[category] + ' refresh...';
        var route = 'TuneInFork/RefreshTuneIn/Category' + (attempt ? '/Resume' : '');
        var data = { Category: category };
        if (attempt) data.AttemptId = attempt;
        return ApiClient.ajax({ type: 'POST', dataType: 'json', contentType: 'application/json',
            url: ApiClient.getUrl(route), data: JSON.stringify(data) }).then(function (result) {
            if (!instance.viewActive || epoch !== instance.viewEpoch) return;
            if (!result || result.Category !== category || typeof result.Id !== 'string' || !result.Id)
                throw new Error('Invalid category refresh response.');
            instance.view.querySelector('.channelRefreshMessage').textContent = groupNames[category] + ' refresh started.';
        }).catch(function () {
            if (instance.viewActive && epoch === instance.viewEpoch)
                instance.view.querySelector('.channelRefreshMessage').textContent = 'Unable to start the refresh. Check selections, active jobs and administrator access.';
        }).then(function () {
            if (!instance.viewActive || epoch !== instance.viewEpoch) return;
            instance.categoryActionBusy = false;
            return readSettingsLock(instance).then(function () {
                return Promise.all([updateCategoryStatuses(instance), updateRefreshStatus(instance, true)]);
            });
        });
    }

    function retrieveRefreshFailure(instance) {
        if (instance.refreshBusy || !instance.failedAttempt) return Promise.resolve();
        var epoch = instance.viewEpoch;
        var view = instance.view;
        var attempt = instance.failedAttempt;
        view.querySelector('.channelRefreshMessage').textContent = 'Retrieving failed task report...';
        refreshControls(instance, true);
        return ApiClient.ajax({ type: 'GET', dataType: 'json',
            url: ApiClient.getUrl('TuneInFork/RefreshTuneIn/Failure', { Attempt: attempt }) }).then(function (report) {
            if (!instance.viewActive || epoch !== instance.viewEpoch) return;
            if (!report || report.Attempt !== attempt || typeof report.Text !== 'string' || !report.Text)
                throw new Error('Invalid task report.');
            view.querySelector('.channelRefreshLog').textContent = report.Text;
            view.querySelector('.channelRefreshLog').hidden = false;
            view.querySelector('.channelRefreshMessage').textContent = report.Truncated
                ? 'Report retrieved; limited to 65,536 characters. Consult the server log for the full details.'
                : 'Failed task report retrieved. Redact private details before sharing.';
        }).catch(function () {
            if (!instance.viewActive || epoch !== instance.viewEpoch) return;
            instance.failedAttempt = null;
            view.querySelector('.channelRefreshLog').textContent = '';
            view.querySelector('.channelRefreshLog').hidden = true;
            view.querySelector('.channelRefreshMessage').textContent = 'Unable to retrieve the report. The task result may have changed, or access failed. Choose Update status and retry.';
        }).then(function () {
            if (instance.viewActive && epoch === instance.viewEpoch) refreshControls(instance, false);
        });
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
        if (!instance.lockKnown || instance.lockBusy || instance.settingsLocked || instance.refreshActive || instance.nativeActive || instance.saving) {
            message(instance, 'Settings cannot be saved while locked or while a refresh is active.');
            return;
        }
        var epoch = instance.viewEpoch;
        var config = instance.config;
        if (!config) {
            message(instance, 'Configuration is unavailable. Reload this page before saving.');
            return;
        }
        var coordinates = instance.view.querySelector('.txtLatLon').value.trim();
        var useCoordinates = instance.view.querySelector('.useCoordinates').checked;
        var intervalText = instance.view.querySelector('.apiRequestInterval').value.trim();
        var interval = Number(intervalText);
        if (!/^\d+$/.test(intervalText) || interval < 1 || interval > 30) {
            message(instance, 'Enter a whole-number catalogue request interval between 1 and 30 seconds.');
            return;
        }
        var timeoutText = instance.view.querySelector('.refreshTimeout').value.trim();
        var timeout = Number(timeoutText);
        if (!/^\d+$/.test(timeoutText) || timeout < 1 || timeout > 1440) {
            message(instance, 'Enter a whole-number refresh timeout between 1 and 1440 minutes.');
            return;
        }
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
        config.ApiRequestIntervalSeconds = interval;
        config.RefreshTimeoutMinutes = timeout;
        categories.forEach(function (spec) {
            config[spec[0]] = instance.view.querySelector('[data-category="' + spec[0] + '"]').checked;
        });
        instance.saving = true;
        updateEditingState(instance);
        loading.show();
        ApiClient.updatePluginConfiguration(pluginId, config).then(function (result) {
            Dashboard.processPluginConfigurationUpdateResult(result);
            if (!instance.viewActive || epoch !== instance.viewEpoch) return;
            instance.view.querySelectorAll('.configurationFields .tuneInSettingsSection').forEach(function (section) {
                section.open = false;
            });
            message(instance, 'Configuration saved. ' + browsingStatus(config) + ' No catalogue crawl was started. Use Refresh TuneIn under Scheduled Tasks to refresh enabled TuneIn categories.');
        }).catch(function () {
            if (!instance.viewActive || epoch !== instance.viewEpoch) return;
            message(instance, 'Configuration was not saved. Check the settings lock, refresh state, validation and administrator access.');
            return readSettingsLock(instance);
        }).then(function () {
            if (instance.viewActive && epoch === instance.viewEpoch) {
                instance.saving = false;
                updateEditingState(instance);
                loading.hide();
            }
        });
    }

    function View(view, params) {
        this.view = view;
        view.querySelector('form').addEventListener('submit', onSubmit.bind(this));
        view.addEventListener('viewshow', onResume.bind(this));
        view.addEventListener('viewbeforehide', onPause.bind(this));
        view.querySelector('.channelRefreshUpdate').addEventListener('click', function () {
            var instance = this;
            return readSettingsLock(instance).then(function () {
                return Promise.all([updateRefreshStatus(instance), updateCategoryStatuses(instance)]);
            });
        }.bind(this));
        view.querySelector('.channelRefreshFailure').addEventListener('click', retrieveRefreshFailure.bind(null, this));
        view.querySelector('.settingsLocked').addEventListener('change', changeSettingsLock.bind(null, this));
    }

    function onResume() {
        var instance = this;
        instance.viewActive = true;
        instance.viewEpoch = (instance.viewEpoch || 0) + 1;
        var epoch = instance.viewEpoch;
        instance.refreshBusy = false;
        instance.categoriesBusy = false;
        instance.categoryActionBusy = false;
        instance.lockKnown = false;
        instance.lockBusy = false;
        instance.lockVersion = (instance.lockVersion || 0) + 1;
        instance.saving = false;
        updateEditingState(instance);
        instance.refreshWasActive = false;
        instance.nativeActive = false;
        instance.refreshActive = false;
        instance.view.querySelector('.channelRefreshSection').open = false;
        instance.failedAttempt = null;
        instance.view.querySelector('.channelRefreshLog').textContent = '';
        instance.view.querySelector('.channelRefreshLog').hidden = true;
        instance.view.querySelector('.channelRefreshMessage').textContent = '';
        instance.view.querySelector('.channelRefreshStatus').textContent = 'Refresh status has not been loaded.';
        refreshControls(instance, false);
        loading.show();
        return ApiClient.getPluginConfiguration(pluginId).then(function (config) {
            if (!instance.viewActive || epoch !== instance.viewEpoch) return;
            instance.config = config;
            var view = instance.view;
            view.querySelector('.txtUsername').value = config.Username || '';
            view.querySelector('.apiRequestInterval').value = config.ApiRequestIntervalSeconds == null ? '3' : String(config.ApiRequestIntervalSeconds);
            view.querySelector('.refreshTimeout').value = config.RefreshTimeoutMinutes == null ? '60' : String(config.RefreshTimeoutMinutes);
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
            return Promise.all([updateRefreshStatus(instance), readSettingsLock(instance)]).then(function () {
                return updateCategoryStatuses(instance);
            }).then(function () {
                if (instance.viewActive && epoch === instance.viewEpoch)
                    instance.lockTimer = setTimeout(function () { pollSettingsLock(instance); }, 5000);
            });
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
        clearTimeout(this.lockTimer);
    }
    return View;
});
