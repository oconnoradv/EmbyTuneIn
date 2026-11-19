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

    function bindDisclosure(summary) {
        summary.addEventListener('click', function (event) {
            event.preventDefault();
            summary.parentNode.open = !summary.parentNode.open;
        });
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
        var epoch = instance.viewEpoch;
        container.textContent = '';
        (instance.config[spec[1]] || []).forEach(function (item) {
            var row = document.createElement('div');
            var control = checkbox(item.Path || item.Name || item.Id, item.Enabled !== false,
                function () {
                    if (epoch === instance.viewEpoch && !editingBlocked(instance)) item.Enabled = control.input.checked;
                    else control.input.checked = item.Enabled !== false;
                });
            row.appendChild(control.label);
            row.appendChild(button('Remove', function () {
                if (epoch !== instance.viewEpoch || editingBlocked(instance)) return;
                instance.config[spec[1]] = instance.config[spec[1]].filter(function (entry) { return entry !== item; });
                selected(instance, spec, container);
            }));
            container.appendChild(row);
        });
        $(container).trigger('create');
    }

    function selectorStatus(error) {
        if (!error) return undefined;
        var code = error.status;
        if (code == null) code = error.statusCode;
        if (code == null && error.response) code = error.response.status;
        return (typeof code === 'number' || typeof code === 'string' && /^\d{3}$/.test(code))
            ? Number(code) : undefined;
    }

    function createSelector(instance, spec, openByDefault) {
        var epoch = instance.viewEpoch;
        var collapsible = spec[0] !== 'local';
        var section = document.createElement(collapsible ? 'details' : 'section');
        var heading = document.createElement(collapsible ? 'summary' : 'h3');
        heading.textContent = spec[2];
        section.appendChild(heading);
        if (collapsible) {
            section.className = 'tuneInSection';
            heading.className = 'tuneInSectionLabel';
            section.open = openByDefault;
            bindDisclosure(heading);
        }
        var saved = document.createElement('div');
        section.appendChild(saved);
        selected(instance, spec, saved);
        var requests = new Map();
        function add(item, feedback) {
            if (epoch !== instance.viewEpoch || editingBlocked(instance)) return;
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
            content.className = parent ? 'tuneInSelectorBranch' : 'tuneInSelectorWindow';
            if (!parent) {
                content.setAttribute('role', 'region');
                content.setAttribute('aria-label', spec[2] + ' choices');
                content.setAttribute('tabindex', '0');
            }
            details.appendChild(content);
            var status = document.createElement('p');
            status.className = 'tuneInSelectorStatus';
            status.setAttribute('role', 'status');
            content.appendChild(status);
            var results = document.createElement('ul');
            results.className = 'tuneInSelectorResults';
            content.appendChild(results);
            var loaded = false;
            var pending;
            function load() {
                if (epoch !== instance.viewEpoch || loaded) return;
                if (pending) return pending;
                status.textContent = 'Loading choices...';
                results.textContent = '';
                function fetchChoices() {
                    return ApiClient.ajax({
                        type: 'GET', dataType: 'json',
                        url: ApiClient.getUrl('TuneIn/Selectors', { Category: spec[0], Parent: parent })
                    });
                }
                if (!requests.has(parent)) requests.set(parent, fetchChoices().catch(function (error) {
                    var code = selectorStatus(error);
                    if ([0, 408, 500, 502, 503, 504].indexOf(code) === -1 ||
                        epoch !== instance.viewEpoch || !details.open) throw error;
                    status.textContent = 'Still loading choices. Trying again...';
                    return new Promise(function (resolve) { setTimeout(resolve, 1000); }).then(function () {
                        if (epoch !== instance.viewEpoch || !details.open) throw error;
                        return fetchChoices();
                    });
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
                        row.className = 'tuneInSelectorRow';
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
                            select.className += ' tuneInSelectorAdd';
                            row.appendChild(select);
                        }
                        results.appendChild(row);
                    });
                    $(content).trigger('create');
                }).catch(function (error) {
                    requests.delete(parent);
                    if (epoch !== instance.viewEpoch) return;
                    console.error('Unable to load TuneIn selector.', error);
                    var code = selectorStatus(error);
                    status.textContent = 'Could not load choices from TuneIn' +
                        (code ? ' (HTTP ' + code + ')' : '') + '. Retry this branch.';
                    var retry = document.createElement('li');
                    retry.appendChild(button('Retry', load));
                    results.appendChild(retry);
                    $(content).trigger('create');
                }).then(function () { pending = null; });
                return pending;
            }
            summary.addEventListener('click', function (event) {
                event.preventDefault();
                if (epoch !== instance.viewEpoch || instance.saving || instance.refreshBusy) return;
                details.open = !details.open;
                if (details.open) return load();
            });
            details.addEventListener('toggle', function () {
                if (details.open) return load();
            });
            return details;
        }
        section.appendChild(branch('', 'Browse ' + spec[2]));
        return section;
    }

    function editingBlocked(instance) {
        return !instance.config || instance.settingsLocked !== false || instance.lockChanging || instance.saving || instance.refreshBusy;
    }

    function editing(instance) {
        var disabled = !!editingBlocked(instance);
        instance.view.querySelector('.loginFields').disabled = disabled;
        instance.view.querySelector('.configurationFields').disabled = disabled;
        instance.view.querySelector('.button-submit').disabled = disabled;
        instance.refreshCategoryRows.forEach(function (row) { row.schedule.disabled = disabled; });
        var toggle = instance.view.querySelector('.settingsLocked');
        toggle.disabled = typeof instance.settingsLocked !== 'boolean' || !!instance.lockChanging || !!instance.saving;
        toggle.checked = instance.lockChanging ? instance.lockRequested : instance.settingsLocked === true;
        var note = instance.view.querySelector('.settingsLockStatus');
        var text = instance.lockError || (instance.saving ? 'Saving and locking settings...' :
            instance.lockChanging ? 'Updating settings lock...' :
            instance.settingsLocked === true ? 'Settings are locked. Unlock to make changes.' :
            instance.settingsLocked === false ? 'Settings are unlocked.' : 'Reading settings lock...');
        if (note.textContent !== text) note.textContent = text;
    }

    function changeSettingsLock(instance) {
        var toggle = instance.view.querySelector('.settingsLocked');
        if (toggle.disabled || !instance.refreshVisible) return;
        var epoch = instance.viewEpoch;
        instance.lockRequested = toggle.checked;
        instance.lockChanging = true;
        instance.lockError = '';
        instance.refreshGeneration = (instance.refreshGeneration || 0) + 1;
        editing(instance);
        return writeSettingsLock(instance.lockRequested).then(function (locked) {
            if (epoch === instance.viewEpoch) instance.settingsLocked = locked;
        }).catch(function () {
            if (epoch !== instance.viewEpoch) return;
            instance.settingsLocked = undefined;
            instance.lockError = 'Could not change the settings lock. Checking the server again.';
        }).then(function () {
            instance.lockChanging = false;
            if (!instance.refreshVisible) return;
            editing(instance);
            pollStatus(instance, instance.viewEpoch);
        });
    }

    function writeSettingsLock(locked) {
        return ApiClient.ajax({
            type: 'POST', dataType: 'json', contentType: 'application/json',
            url: ApiClient.getUrl('TuneIn/SettingsLock'),
            data: JSON.stringify({ Locked: locked })
        }).then(function (result) {
            if (result.Locked !== locked) throw new Error('TuneIn did not confirm the requested lock state.');
            return result.Locked;
        });
    }

    var httpStatusLabels = {
        400: 'Bad Request',
        401: 'Unauthorized',
        403: 'Forbidden',
        404: 'Not Found',
        408: 'Request Timeout',
        429: 'Too Many Requests',
        500: 'Internal Server Error',
        502: 'Bad Gateway',
        503: 'Service Unavailable',
        504: 'Gateway Timeout'
    };

    function formatDuration(seconds) {
        if (typeof seconds !== 'number' || !isFinite(seconds) || seconds < 0) return null;
        seconds = Math.floor(seconds);
        var hours = Math.floor(seconds / 3600);
        var minutes = Math.floor((seconds % 3600) / 60);
        var remaining = seconds % 60;
        if (hours) return hours + ' hr ' + String(minutes).padStart(2, '0') + ' min';
        if (minutes) return minutes + ' min ' + String(remaining).padStart(2, '0') + ' sec';
        return seconds + ' sec';
    }

    function elapsedSeconds(status) {
        if (!status.StartedUtc) return null;
        var started = new Date(status.StartedUtc).getTime();
        if (!isFinite(started)) return null;
        var ended = status.Busy ? Date.now() : new Date(status.FinishedUtc).getTime();
        return isFinite(started) && isFinite(ended) ? Math.max(0, (ended - started) / 1000) : null;
    }

    function progressAgeSeconds(status) {
        if (!status.LastProgressUtc) return null;
        var lastProgress = new Date(status.LastProgressUtc).getTime();
        return isFinite(lastProgress) ? Math.max(0, (Date.now() - lastProgress) / 1000) : null;
    }

    function renderRefresh(instance, status) {
        instance.refreshBusy = !!status.Busy;
        if (typeof status.SettingsLocked === 'boolean' && !instance.lockChanging && !instance.saving) {
            instance.settingsLocked = status.SettingsLocked;
            instance.lockError = '';
        }
        var previousStatus = instance.refreshStatus;
        instance.refreshStatus = status;
        var category = ({ local: 'Local Radio', music: 'Music', language: 'By Language', location: 'By Location',
            talk: 'Talk', sports: 'Sports', podcast: 'Podcasts', presets: 'My Favorites' })[status.Category];
        var elapsed = formatDuration(elapsedSeconds(status));
        var progressAge = formatDuration(progressAgeSeconds(status));
        var pages = typeof status.CatalogPagesCached === 'number' ? status.CatalogPagesCached.toLocaleString() : null;
        var pageLimit = typeof status.CatalogPagesLimit === 'number' ? status.CatalogPagesLimit.toLocaleString() : null;
        var summary = ['Catalog Refresh'];
        if (status.State !== 'NeverRun') summary.push(status.State);
        if (status.Busy && status.Phase) summary.push(status.Phase);
        if (category) summary.push(category);
        if (status.Busy && status.Phase === 'Importing' && status.ImportFoldersTotal)
            summary.push(status.ImportFoldersServed.toLocaleString() + '/' + status.ImportFoldersTotal.toLocaleString() + ' folders');
        else if (status.Busy && status.Phase === 'Discovering' && pages != null)
            summary.push(pages + ' pages');
        if (status.Busy && progressAge) summary.push('progress ' + progressAge + ' ago');
        else if (!status.Busy && elapsed) summary.push('elapsed ' + elapsed);
        instance.view.querySelector('.refreshSummary').textContent = summary.join(' - ');

        var description = status.State === 'NeverRun' ? 'No Control Panel refresh has run.' :
            [status.State, status.Phase, category].filter(Boolean).join(' - ');
        if (status.ActiveRequest && status.ActiveRequest !== status.Category)
            description += ' (requested: ' + refreshName(status, status.ActiveRequest) + ')';
        if (status.AutomaticRetry && status.AutomaticRetryLimit)
            description += '. Automatic retry ' + status.AutomaticRetry + ' of ' + status.AutomaticRetryLimit + ' after discovery timed out';
        if (status.Busy && status.Activity) description += '. ' + status.Activity;
        var work = '-';
        if (status.Busy && status.Phase === 'Importing' && status.ImportFoldersTotal)
            work = status.ImportFoldersServed.toLocaleString() + ' / ' + status.ImportFoldersTotal.toLocaleString() + ' folders served';
        else if (status.Busy && status.Phase === 'Discovering' && pages != null) {
            work = pages + (pageLimit == null ? ' catalog pages' : ' catalog pages (limit ' + pageLimit + ')');
            if (status.CategoriesTotal)
                work += '; ' + status.CategoriesCompleted + ' / ' + status.CategoriesTotal + ' categories checked';
        }
        else if (status.CategoriesTotal)
            work = status.CategoriesCompleted + ' / ' + status.CategoriesTotal + ' categories checked';
        instance.view.querySelector('.refreshElapsed').textContent = elapsed || '-';
        instance.view.querySelector('.refreshWork').textContent = work;
        instance.view.querySelector('.refreshLastProgress').textContent = progressAge
            ? progressAge + ' ago' : status.LastProgressUtc ? 'Finished' : '-';

        var diagnostics = [];
        if (typeof status.RequestsStarted === 'number' || typeof status.RequestsCompleted === 'number') {
            diagnostics.push('Provider requests: ' + (status.RequestsStarted || 0) + ' attempts, '
                + (status.RequestsCompleted || 0) + ' successful');
        }
        if (typeof status.CacheHits === 'number') diagnostics.push(status.CacheHits + ' cached page replays');
        if (typeof status.ActiveRequests === 'number')
            diagnostics.push(status.ActiveRequests + ' active HTTP ' + (status.ActiveRequests === 1 ? 'request' : 'requests'));
        if (typeof status.RateLimitRetries === 'number') diagnostics.push(status.RateLimitRetries + ' rate-limit retries');
        if (typeof status.PartialImportAllowed === 'boolean')
            diagnostics.push('Import valid results with exclusions: ' + (status.PartialImportAllowed ? 'allowed' : 'off'));
        if (status.Resumed) {
            var checkpoint = status.DiscoveryCheckpointUtc ? new Date(status.DiscoveryCheckpointUtc) : null;
            diagnostics.push('Resumed from checkpoint' + (checkpoint && !isNaN(checkpoint.getTime())
                ? ' saved at ' + checkpoint.toLocaleTimeString() : ''));
        }
        if (status.CategoriesSkipped) diagnostics.push(status.CategoriesSkipped + ' categories unchanged');
        if (status.SkippedBrowseLinks) {
            description += '. ' + status.SkippedBrowseLinks + ' malformed catalog links skipped';
            if (status.State === 'Partial' && status.PartialImportAllowed === false
                && status.Phase === 'Discovery incomplete; existing library left unchanged')
                description += '. To apply valid results, enable "Import valid results when catalog entries are skipped" in Catalog browsing, save, then refresh again';
            diagnostics.push('Skipped malformed TuneIn catalog links: ' + status.SkippedBrowseLinks
                + (status.SkippedBrowseLinkDetails ? ' (' + status.SkippedBrowseLinkDetails + ')' : ''));
        }
        instance.view.querySelector('.refreshDiagnosticsText').textContent =
            diagnostics.length ? diagnostics.join('; ') : 'No additional diagnostics available.';
        if (status.LastHttpStatus) {
            var label = httpStatusLabels[status.LastHttpStatus] || 'Unexpected response';
            description += '. TuneIn returned HTTP ' + status.LastHttpStatus + ' (' + label + ')';
            if (status.LastHttpStatus === 429 && status.State === 'Completed')
                description += ' earlier (recovered; refresh completed normally)';
        }
        else if (status.ErrorCode) description += '. ' + status.ErrorCode;
        if (status.ImportMayHavePartiallyApplied && status.Phase !== 'Importing')
            description += '. An interrupted import may have changed Emby metadata.';
        instance.view.querySelector('.refreshStatus').textContent = description;
        var queued = status.QueuedRequests || [];
        var queueNote = instance.view.querySelector('.refreshQueueNote');
        queueNote.hidden = !queued.length;
        queueNote.textContent = queued.length ?
            'Waiting to refresh (' + queued.length + '/2): ' + queued.map(function (key) {
                return refreshName(status, key);
            }).join(', ') : '';
        if (status.State !== 'Error' || !previousStatus || status.FinishedUtc !== previousStatus.FinishedUtc) {
            instance.view.querySelector('.refreshFailureLog').textContent = '';
            instance.view.querySelector('.refreshFailureLog').hidden = true;
            instance.view.querySelector('.refreshLogMessage').textContent = '';
        }
        if (instance.refreshSection.open) renderCategoryRefresh(instance, status);
        refreshControls(instance, true);
    }

    function renderCategoryRefresh(instance, status) {
        var list = instance.view.querySelector('.tuneInRefreshCategories');
        var categories = status.Categories || [];
        var note = '';
        if (!categories.length) note = 'No categories are enabled in the saved browsing settings.';
        else if (status.DirtyCategory && !status.Busy) {
            var dirty = categories.find(function (category) { return category.Category === status.DirtyCategory; });
            if (dirty) note = dirty.Name + ' was interrupted. You can retry it or prioritize another category.';
        }
        instance.view.querySelector('.refreshCategoryNote').textContent = note;
        var rows = instance.refreshCategoryRows;
        if (Array.from(rows.keys()).join(',') !== categories.map(function (item) { return item.Category; }).join(',')) {
            rows.clear();
            list.textContent = '';
            categories.forEach(function (item) {
                var row = document.createElement('li');
                var caption = document.createElement('span');
                var control = button('Refresh', function () { return doRefresh(instance, 'Start', item.Category); });
                control.setAttribute('aria-label', 'Refresh ' + item.Name);
                var actions = document.createElement('div');
                actions.className = 'tuneInRefreshCategoryActions';
                var schedule = document.createElement('select');
                schedule.setAttribute('aria-label', item.Name + ' refresh interval');
                [['0', 'Schedule: Off'], ['6', 'Every 6 hours'], ['12', 'Every 12 hours'],
                    ['24', 'Daily'], ['168', 'Weekly']].forEach(function (choice) {
                    var option = document.createElement('option');
                    option.value = choice[0];
                    option.textContent = choice[1];
                    schedule.appendChild(option);
                });
                schedule.value = String(instance.refreshSchedules.get(item.Category) || 0);
                schedule.addEventListener('change', function () {
                    if (!editingBlocked(instance)) instance.refreshSchedules.set(item.Category, Number(schedule.value));
                });
                row.appendChild(caption);
                actions.appendChild(control);
                actions.appendChild(schedule);
                row.appendChild(actions);
                list.appendChild(row);
                rows.set(item.Category, { caption: caption, control: control, schedule: schedule });
            });
            $(list).trigger('create');
        }
        categories.forEach(function (item) {
            var text = item.Name + ': ' + ((status.QueuedRequests || []).includes(item.Category) ? 'Queued' : item.State);
            if (!status.Busy && item.Category === status.DirtyCategory) text += ' (reconciliation needed)';
            var caption = rows.get(item.Category).caption;
            if (caption.textContent !== text) caption.textContent = text;
        });
    }

    function refreshControls(instance, available) {
        var cannotStart = !available || !instance.config || !!instance.refreshAction || instance.saving;
        var status = instance.refreshStatus;
        instance.refreshAvailable = available;
        var queued = status && status.QueuedRequests || [];
        function blocked(key) {
            return cannotStart || !status || status.QueueSlotsAvailable === 0 || status.Cancelling ||
                status.ActiveRequest === key || queued.includes(key);
        }
        instance.view.querySelector('.refreshStart').disabled = blocked('all') || !(status.Categories || []).length;
        instance.view.querySelector('.refreshCancel').disabled = !available || !instance.refreshBusy || !!instance.refreshAction;
        instance.view.querySelector('.refreshLogButton').disabled = !available || !status || status.State !== 'Error'
            || status.Busy || !status.FinishedUtc || !!instance.refreshAction || !!instance.refreshLogAction;
        instance.refreshCategoryRows.forEach(function (row, category) {
            row.control.disabled = blocked(category);
        });
        editing(instance);
    }

    function retrieveRefreshLog(instance) {
        if (!instance.refreshVisible || instance.refreshLogAction || !instance.refreshAvailable) return;
        var control = instance.view.querySelector('.refreshLogButton');
        if (control.disabled) return;
        var epoch = instance.viewEpoch;
        var generation = instance.refreshGeneration || 0;
        var finishedUtc = instance.refreshStatus.FinishedUtc;
        var requestToken = {};
        instance.refreshLogAction = requestToken;
        instance.view.querySelector('.refreshLogMessage').textContent = 'Retrieving the latest failed refresh report...';
        refreshControls(instance, instance.refreshAvailable);
        return ApiClient.ajax({
            type: 'GET', dataType: 'json', url: ApiClient.getUrl('TuneIn/Refresh/Log')
        }).then(function (result) {
            if (epoch !== instance.viewEpoch || generation !== (instance.refreshGeneration || 0)
                || !instance.refreshVisible || !instance.refreshStatus || instance.refreshStatus.State !== 'Error'
                || instance.refreshStatus.FinishedUtc !== finishedUtc || result.FinishedUtc !== finishedUtc) return;
            var report = instance.view.querySelector('.refreshFailureLog');
            report.textContent = result.Text || '';
            report.hidden = false;
            instance.view.querySelector('.refreshLogMessage').textContent = result.Truncated
                ? 'Refresh report retrieved; it was truncated to 64 KB.'
                : 'Refresh report retrieved.';
        }).catch(function () {
            if (epoch === instance.viewEpoch && generation === (instance.refreshGeneration || 0)
                && instance.refreshStatus && instance.refreshStatus.State === 'Error'
                && instance.refreshStatus.FinishedUtc === finishedUtc)
                instance.view.querySelector('.refreshLogMessage').textContent =
                    'Could not retrieve the latest refresh report. Check refresh status and try again.';
        }).then(function () {
            if (instance.refreshLogAction === requestToken) {
                instance.refreshLogAction = false;
                if (epoch === instance.viewEpoch) refreshControls(instance, instance.refreshAvailable);
            }
        });
    }

    function scheduleStatus(instance, epoch) {
        if (epoch !== instance.viewEpoch || !instance.refreshVisible) return;
        clearTimeout(instance.refreshTimer);
        instance.refreshTimer = setTimeout(function () { pollStatus(instance, epoch); }, instance.refreshBusy ? 1500 : 10000);
    }

    function pollStatus(instance, epoch) {
        if (epoch !== instance.viewEpoch || !instance.refreshVisible) return;
        clearTimeout(instance.refreshTimer);
        if (instance.refreshAction || instance.lockChanging) return scheduleStatus(instance, epoch);
        if (instance.refreshPoll) return instance.refreshPoll;
        var generation = instance.refreshGeneration || 0;
        instance.refreshPoll = ApiClient.ajax({
            type: 'GET', dataType: 'json', url: ApiClient.getUrl('TuneIn/Refresh/Status')
        }).then(function (status) {
            if (epoch === instance.viewEpoch && generation === (instance.refreshGeneration || 0)) renderRefresh(instance, status);
        }).catch(function () {
            if (epoch === instance.viewEpoch && generation === (instance.refreshGeneration || 0)) {
                instance.view.querySelector('.refreshStatus').textContent = 'Could not read refresh status. Retrying automatically.';
                instance.view.querySelector('.refreshCategoryNote').textContent = '';
                if (typeof instance.settingsLocked !== 'boolean')
                    instance.lockError = 'Could not read the settings lock. Retrying automatically.';
                refreshControls(instance, false);
            }
        }).then(function () {
            instance.refreshPoll = null;
            if (epoch !== instance.viewEpoch || generation !== (instance.refreshGeneration || 0)) {
                if (!instance.refreshAction) pollStatus(instance, instance.viewEpoch);
            } else scheduleStatus(instance, epoch);
        });
        return instance.refreshPoll;
    }

    function refreshName(status, key) {
        var category = (status.Categories || []).find(function (item) { return item.Category === key; });
        return category ? category.Name : key === 'all' ? 'All enabled categories' : key;
    }

    function showRecoveryChoice(instance, status, category) {
        var dialog = instance.view.querySelector('.tuneInRecoveryDialog');
        var previous = refreshName(status, status.RecoveryCategory);
        var requested = refreshName(status, category);
        instance.refreshRecovery = { category: category, id: status.RecoveryId };
        instance.view.querySelector('.refreshRecoveryText').textContent =
            previous + ' did not finish. Retry it first and queue ' + requested +
            ', or refresh ' + requested + ' first without queuing a retry.' +
            (status.DirtyCategory ? ' Emby still needs a complete catalog import to repair the interruption.' : '');
        if (!dialog.open) dialog.showModal();
    }

    function doRefresh(instance, action, category, recovery) {
        if (!instance.refreshVisible || !instance.refreshSection.open || instance.refreshAction || !instance.refreshAvailable) return;
        var row = category ? instance.refreshCategoryRows.get(category) : null;
        var control = action === 'Cancel' ? instance.view.querySelector('.refreshCancel') :
            category ? row && row.control : instance.view.querySelector('.refreshStart');
        if (!control || control.disabled) return;
        var epoch = instance.viewEpoch;
        instance.refreshGeneration = (instance.refreshGeneration || 0) + 1;
        instance.refreshAction = true;
        instance.view.querySelector('.refreshFailureLog').textContent = '';
        instance.view.querySelector('.refreshFailureLog').hidden = true;
        instance.view.querySelector('.refreshLogMessage').textContent = '';
        refreshControls(instance, false);
        instance.view.querySelector('.refreshError').textContent = '';
        var params = category ? { Category: category } : undefined;
        if (recovery) {
            params.ExpectedRecoveryId = recovery.id;
            params.ResumeFirst = recovery.resume;
        }
        return ApiClient.ajax({
            type: 'POST', dataType: 'json',
            url: ApiClient.getUrl('TuneIn/Refresh/' + action, params)
        })
            .then(function (status) {
                if (epoch === instance.viewEpoch) {
                    renderRefresh(instance, status);
                    if (status.RecoveryChoiceRequired && instance.refreshSection.open)
                        showRecoveryChoice(instance, status, category);
                }
            }).catch(function () {
                if (epoch === instance.viewEpoch)
                    instance.view.querySelector('.refreshError').textContent = 'Could not ' + action.toLowerCase() + ' refresh. Check the current status before retrying.';
            }).then(function () {
                instance.refreshAction = false;
                if (epoch === instance.viewEpoch) refreshControls(instance, instance.refreshAvailable);
                if (instance.refreshVisible) pollStatus(instance, instance.viewEpoch);
            });
    }

    function onSubmit(event) {
        event.preventDefault();
        var instance = this;
        var epoch = instance.viewEpoch;
        if (editingBlocked(instance)) return false;
        var config = instance.config;
        var coordinates = instance.view.querySelector('.txtLatLon').value.trim();
        var requestInterval = String(instance.view.querySelector('.apiRequestIntervalSeconds').value || '').trim();
        if (!/^[0-9]+$/.test(requestInterval) || Number(requestInterval) < 1 || Number(requestInterval) > 30) {
            message(instance, 'Use a whole number from 1 to 30 for the API request interval.');
            return false;
        }
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
        config.ApiRequestIntervalSeconds = Number(requestInterval);
        config.HideRegionalGenres = instance.view.querySelector('.hideRegionalGenres').checked;
        config.AllowPartialCatalogImport = instance.view.querySelector('.allowPartialCatalogImport').checked;
        config.RefreshSchedules = Array.from(instance.refreshSchedules)
            .filter(function (entry) { return entry[1] > 0; })
            .map(function (entry) { return { Category: entry[0], IntervalHours: entry[1] }; });
        config.UseCoordinates = useCoordinates;
        config.LocalRadioSource = source || null;
        categories.forEach(function (spec) {
            config[spec[0]] = instance.view.querySelector('[data-category="' + spec[0] + '"]').checked;
        });
        instance.saving = true;
        instance.lockError = '';
        refreshControls(instance, instance.refreshAvailable);
        loading.show();
        ApiClient.updatePluginConfiguration(pluginId, config).then(function (result) {
            return writeSettingsLock(true).then(function () {
                instance.settingsLocked = true;
                if (epoch !== instance.viewEpoch) return;
                Dashboard.processPluginConfigurationUpdateResult(result);
                message(instance, 'Configuration saved and locked. ' + browsingStatus(config) + ' No catalog crawl was started.');
            }, function (error) {
                instance.settingsLocked = undefined;
                console.error('Unable to lock TuneIn settings after saving.', error);
                if (epoch !== instance.viewEpoch) return;
                instance.lockError = 'Could not confirm the settings lock. Checking the server again.';
                message(instance, 'Configuration saved, but the settings lock could not be confirmed. Check the switch before leaving.');
            });
        }, function (error) {
            if (epoch !== instance.viewEpoch) return;
            console.error('Unable to save TuneIn configuration.', error);
            message(instance, 'Configuration was not saved. Check the settings lock, validation, connectivity and administrator access, then retry.');
        }).finally(function () {
            instance.saving = false;
            if (!instance.refreshVisible) return;
            instance.refreshGeneration = (instance.refreshGeneration || 0) + 1;
            refreshControls(instance, false);
            pollStatus(instance, instance.viewEpoch);
            if (epoch === instance.viewEpoch) loading.hide();
        });
        return false;
    }

    function View(view, params) {
        BaseView.apply(this, arguments);
        var instance = this;
        instance.refreshSection = view.querySelector('.tuneInRefresh');
        instance.refreshCategoryRows = new Map();
        instance.refreshSchedules = new Map();
        ['.categoriesSummary', '.localSummary', '.refreshSummary'].forEach(function (selector) {
            bindDisclosure(view.querySelector(selector));
        });
        view.querySelector('.settingsLocked').addEventListener('change', function () { return changeSettingsLock(instance); });
        var dialog = view.querySelector('.tuneInRecoveryDialog');
        instance.refreshSection.addEventListener('toggle', function () {
            if (!instance.refreshSection.open && dialog.open) dialog.close();
            if (!instance.refreshSection.open || !instance.refreshVisible) return;
            instance.refreshGeneration = (instance.refreshGeneration || 0) + 1;
            view.querySelector('.refreshStatus').textContent = 'Reading refresh status...';
            view.querySelector('.refreshCategoryNote').textContent = '';
            refreshControls(instance, false);
            pollStatus(instance, instance.viewEpoch);
        });
        view.querySelector('form').addEventListener('submit', onSubmit.bind(this));
        view.querySelector('.refreshStart').addEventListener('click', function () { return doRefresh(instance, 'Start'); });
        view.querySelector('.refreshCancel').addEventListener('click', function () { return doRefresh(instance, 'Cancel'); });
        view.querySelector('.refreshLogButton').addEventListener('click', function () { return retrieveRefreshLog(instance); });
        dialog.addEventListener('close', function () {
            if (!dialog.open) instance.refreshRecovery = null;
        });
        [['.refreshResumeFirst', true], ['.refreshNewFirst', false]].forEach(function (choice) {
            view.querySelector(choice[0]).addEventListener('click', function () {
                var recovery = instance.refreshRecovery;
                instance.refreshRecovery = null;
                dialog.close();
                if (recovery) return doRefresh(instance, 'Start', recovery.category,
                    { id: recovery.id, resume: choice[1] });
            });
        });
    }

    Object.assign(View.prototype, BaseView.prototype);

    View.prototype.onResume = function () {
        BaseView.prototype.onResume.apply(this, arguments);
        var instance = this;
        var epoch = instance.viewEpoch = (instance.viewEpoch || 0) + 1;
        instance.refreshVisible = true;
        instance.view.querySelector('.refreshStatus').textContent = 'Reading refresh status...';
        instance.view.querySelector('.refreshError').textContent = '';
        instance.view.querySelector('.refreshFailureLog').textContent = '';
        instance.view.querySelector('.refreshFailureLog').hidden = true;
        instance.view.querySelector('.refreshLogMessage').textContent = '';
        instance.refreshLogAction = null;
        instance.config = null;
        instance.settingsLocked = undefined;
        instance.lockError = '';
        instance.loadingConfig = true;
        editing(instance);
        refreshControls(instance, false);
        pollStatus(instance, epoch);
        loading.show();
        return ApiClient.getPluginConfiguration(pluginId).then(function (config) {
            if (epoch !== instance.viewEpoch) return;
            instance.config = config;
            instance.refreshSchedules = new Map((config.RefreshSchedules || []).map(function (schedule) {
                return [schedule.Category, schedule.IntervalHours];
            }));
            instance.refreshCategoryRows.forEach(function (row, category) {
                row.schedule.value = String(instance.refreshSchedules.get(category) || 0);
            });
            var view = instance.view;
            view.querySelector('.txtUsername').value = config.Username || '';
            view.querySelector('.txtLatLon').value = config.LatLon || '';
            view.querySelector('.apiRequestIntervalSeconds').value =
                config.ApiRequestIntervalSeconds == null ? 3 : Math.min(config.ApiRequestIntervalSeconds, 30);
            view.querySelector('.hideRegionalGenres').checked = !!config.HideRegionalGenres;
            view.querySelector('.allowPartialCatalogImport').checked = !!config.AllowPartialCatalogImport;
            var hasRegions = (config.LocalCities || []).some(function (item) {
                return item && item.Enabled !== false && /^r[1-9][0-9]{0,9}$/.test(item.Id);
            });
            var ambiguous = !config.LocalRadioSource && config.UseCoordinates && hasRegions;
            view.querySelector('.localSource').value = config.LocalRadioSource ||
                (ambiguous ? '' : config.UseCoordinates ? 'coordinates' : hasRegions ? 'regions' : '');
            view.querySelector('.configurationCompleted').checked = config.SchemaVersion === 1 && !!config.ConfigurationCompleted;
            var openByDefault = config.SchemaVersion !== 1;
            view.querySelector('.categoriesSummary').parentNode.open = openByDefault;
            view.querySelector('.localSummary').parentNode.open = openByDefault;
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
            var localPicker = view.querySelector('.localSelector');
            localPicker.textContent = '';
            selectors.forEach(function (spec) {
                (spec[0] === 'local' ? localPicker : pickerList).appendChild(createSelector(instance, spec, openByDefault));
            });
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
            refreshControls(instance, instance.refreshAvailable);
            loading.hide();
        });
    };

    View.prototype.onPause = function () {
        this.viewEpoch = (this.viewEpoch || 0) + 1;
        this.refreshVisible = false;
        this.refreshRecovery = null;
        var dialog = this.view.querySelector('.tuneInRecoveryDialog');
        if (dialog.open) dialog.close();
        clearTimeout(this.refreshTimer);
        this.view.querySelector('.refreshFailureLog').textContent = '';
        this.view.querySelector('.refreshFailureLog').hidden = true;
        this.view.querySelector('.refreshLogMessage').textContent = '';
        this.refreshLogAction = null;
        if (this.loadingConfig || this.saving) {
            this.loadingConfig = false;
            loading.hide();
        }
        if (BaseView.prototype.onPause) return BaseView.prototype.onPause.apply(this, arguments);
    };
    return View;
});
