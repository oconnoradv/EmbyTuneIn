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
        ['language', 'Languages', 'By Language', 'LanguageMode', true],
        ['location', 'Locations', 'By Location', 'LocationMode', true],
        ['music', 'Music', 'Music Genres', 'MusicMode', true],
        ['talk', 'TalkIds', 'Talk Topics', 'TalkMode'],
        ['sports', 'SportsIds', 'Sports & Leagues', 'SportsMode'],
        ['podcast', 'PodcastIds', 'Podcast Topics', 'PodcastsMode'],
        ['local', 'LocalCities', 'Local Cities and Regions', 'LocalMode', true]
    ];
    var filteringSettings = [
        'EnableLocalRadio', 'EnableMusic', 'EnableLanguage', 'EnableLocation', 'EnableTalk',
        'EnableSports', 'EnablePodcasts', 'EnableFavorites', 'Languages', 'Locations', 'Music',
        'LocalCities', 'LanguageMode', 'LocationMode', 'MusicMode', 'TalkMode', 'TalkIds',
        'SportsMode', 'SportsIds', 'PodcastsMode', 'PodcastIds', 'LocalMode'
    ];

    function filteringSnapshot(config) {
        return filteringSettings.map(function (key) { return config[key]; });
    }

    function selectorMode(instance, spec) {
        var mode = instance.config[spec[3]];
        if (typeof mode === 'string' && /^(all|selected)$/i.test(mode)) return mode.toLowerCase();
        if (spec[4] && mode) return 'selected';
        return spec[4] && (instance.config[spec[1]] || []).length ? 'selected' : 'all';
    }

    function message(instance, text, configurationRequired) {
        var status = instance.view.querySelector('.status');
        status.textContent = text;
        status.setAttribute('data-configuration-required', String(!!configurationRequired));
    }

    function button(text, action) {
        var element = document.createElement('button');
        element.type = 'button';
        element.setAttribute('is', 'emby-button');
        element.textContent = text;
        element.className = 'raised button-submit emby-button';
        element.addEventListener('click', action);
        return element;
    }

    function bindDisclosure(summary, onToggle) {
        summary.addEventListener('click', function (event) {
            event.preventDefault();
            if (onToggle) onToggle();
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

    function configurationRequired(config) {
        return config.SchemaVersion !== 1 || !config.ConfigurationCompleted;
    }

    function browsingStatus(config) {
        if (configurationRequired(config))
            return 'The TuneIn catalog refresh is unavailable until Global Settings have been reviewed and saved.';
        return '';
    }

    function renderCatalogRefreshPrompt(instance) {
        var element = instance.view.querySelector('.catalogRefreshPrompt');
        var status = instance.refreshStatus;
        var configured = instance.config && instance.config.SchemaVersion === 1 && instance.config.ConfigurationCompleted;
        var visible = !!configured && !!status && status.State === 'NeverRun'
            && status.HasRefreshBeenStarted === false && !status.StartedUtc;
        element.hidden = !visible;
        element.textContent = visible
            ? 'Please go to the Catalog Refresh section to refresh your library\'s TuneIn catalog'
            : '';
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
        var section = document.createElement('details');
        var heading = document.createElement('summary');
        heading.textContent = spec[2];
        section.appendChild(heading);
        section.className = 'tuneInSection tuneInSelectorCategory';
        heading.className = 'tuneInSectionLabel';
        section.open = openByDefault && !spec[3];
        var picker = document.createElement('div');
        picker.className = 'tuneInSelectorPicker';
        var columns = document.createElement('div');
        columns.className = 'tuneInSelectorColumns';
        columns.setAttribute('data-mode', spec[3] ? selectorMode(instance, spec) : 'selected');
        var savedColumn = document.createElement('div');
        savedColumn.className = 'tuneInSelectorSavedColumn';
        var savedHeading = document.createElement('h3');
        savedHeading.textContent = 'Saved choices';
        savedColumn.appendChild(savedHeading);
        var savedActions = document.createElement('div');
        savedActions.className = 'tuneInSelectorSavedActions';
        var choicesToRemove = new Set();
        var deleteChoices = button('Select Items for Delete', function () {
            if (epoch !== instance.viewEpoch || !choicesToRemove.size ||
                !browsingSelected(instance) || editingBlocked(instance) || deleteDialog.open) return;
            deleteDescription.textContent = 'Delete ' + choicesToRemove.size + ' selected ' +
                (choicesToRemove.size === 1 ? 'choice' : 'choices') + ' from ' + spec[2] +
                '? This change will be applied when you save the settings.';
            deleteDialog.showModal();
        });
        deleteChoices.className += ' tuneInSelectorDeleteTrigger';
        deleteChoices.disabled = true;
        savedActions.appendChild(deleteChoices);
        savedColumn.appendChild(savedActions);
        var saved = document.createElement('div');
        saved.className = 'tuneInSelectorSaved';
        savedColumn.appendChild(saved);
        var deleteDialog = document.createElement('dialog');
        deleteDialog.className = 'tuneInRecoveryDialog tuneInSelectorDeleteDialog';
        var deleteTitle = document.createElement('h3');
        deleteTitle.id = 'tuneInSelectorDeleteTitle-' + spec[0];
        deleteTitle.textContent = 'Delete selected choices?';
        deleteDialog.setAttribute('aria-labelledby', deleteTitle.id);
        var deleteDescription = document.createElement('p');
        deleteDescription.id = 'tuneInSelectorDeleteDescription-' + spec[0];
        deleteDialog.setAttribute('aria-describedby', deleteDescription.id);
        var deleteActions = document.createElement('div');
        deleteActions.className = 'tuneInRecoveryActions';
        var confirmDeletion = button('Delete Selected Items', function () {
            if (epoch !== instance.viewEpoch || !deleteDialog.open || !choicesToRemove.size ||
                !browsingSelected(instance) || editingBlocked(instance)) return;
            var removing = choicesToRemove;
            instance.config[spec[1]] = (instance.config[spec[1]] || []).filter(function (choice) {
                return !removing.has(choice);
            });
            choicesToRemove.clear();
            instance.selectionDeletionPending = true;
            deleteDialog.close();
            renderSaved();
        });
        confirmDeletion.className += ' tuneInSelectorConfirmDeletion';
        var cancelDeletion = button('Cancel', function () {
            if (epoch === instance.viewEpoch) deleteDialog.close();
        });
        deleteActions.appendChild(confirmDeletion);
        deleteActions.appendChild(cancelDeletion);
        deleteDialog.appendChild(deleteTitle);
        deleteDialog.appendChild(deleteDescription);
        deleteDialog.appendChild(deleteActions);
        savedColumn.appendChild(deleteDialog);
        var topicNames = new Map();
        var openRoot;
        function showChoices() {
            if (section.open && !picker.hidden && openRoot && !instance.saving && !instance.refreshBusy)
                return openRoot();
        }
        heading.addEventListener('click', function (event) {
            event.preventDefault();
            section.open = !section.open;
            if (section.open) return showChoices();
            root.open = false;
        });
        if (spec[3]) {
            var modeControl = document.createElement('select');
            modeControl.setAttribute('is', 'emby-select');
            modeControl.setAttribute('aria-label', spec[2] + ' coverage');
            modeControl.className = 'tuneInSelectorMode';
            [['all', 'All'], ['selected', 'Selected only']].forEach(function (choice) {
                var option = document.createElement('option');
                option.value = choice[0];
                option.textContent = choice[1];
                modeControl.appendChild(option);
            });
            modeControl.value = selectorMode(instance, spec);
            var hint = document.createElement('p');
            hint.className = 'fieldDescription';
            hint.textContent = 'Choose at least one option offered by TuneIn. If a saved choice is no longer available, review your selections before refreshing.';
            section.appendChild(modeControl);
            picker.appendChild(hint);
            modeControl.addEventListener('change', function () {
                if (epoch !== instance.viewEpoch || !browsingSelected(instance) || editingBlocked(instance)) {
                    modeControl.value = selectorMode(instance, spec);
                    return;
                }
                instance.config[spec[3]] = modeControl.value;
                picker.hidden = modeControl.value !== 'selected';
                columns.setAttribute('data-mode', modeControl.value);
                if (!picker.hidden) return showChoices();
                root.open = false;
            });
            picker.hidden = modeControl.value !== 'selected';
        }
        function savedCaption(choice) {
            return spec[4] ? choice.Path || choice.Name || choice.Id : topicNames.get(choice) || choice;
        }
        function updateDeleteButton() {
            var selectedCount = choicesToRemove.size;
            deleteChoices.textContent = selectedCount ? 'Delete Selected Items' : 'Select Items for Delete';
            deleteChoices.disabled = !selectedCount;
            deleteChoices.setAttribute('aria-label', selectedCount
                ? 'Delete selected items from ' + spec[2]
                : 'Select items for deletion from ' + spec[2]);
        }
        function renderSaved() {
            saved.textContent = '';
            var choices = instance.config[spec[1]] || [];
            choicesToRemove.forEach(function (choice) {
                if (!choices.includes(choice)) choicesToRemove.delete(choice);
            });
            updateDeleteButton();
            if (!choices.length) {
                var empty = document.createElement('p');
                empty.textContent = 'No choices saved.';
                saved.appendChild(empty);
                $(savedColumn).trigger('create');
                return;
            }
            choices.forEach(function (choice) {
                var row = document.createElement('div');
                row.className = 'tuneInSelectorSavedChoice';
                var text = savedCaption(choice);
                var control = checkbox(text, choicesToRemove.has(choice), function () {
                    if (epoch !== instance.viewEpoch || !browsingSelected(instance) || editingBlocked(instance)) return;
                    if (control.input.checked) choicesToRemove.add(choice);
                    else choicesToRemove.delete(choice);
                    updateDeleteButton();
                });
                control.input.setAttribute('aria-label', 'Select ' + text + ' for deletion');
                control.label.className += ' tuneInSelectorRemovalChoice';
                row.appendChild(control.label);
                saved.appendChild(row);
            });
            $(savedColumn).trigger('create');
        }
        var requests = new Map();
        function add(item, feedback) {
            if (epoch !== instance.viewEpoch || !browsingSelected(instance) || editingBlocked(instance)) return;
            var list = instance.config[spec[1]] || [];
            if (spec[3]) {
                if (selectorMode(instance, spec) !== 'selected') return;
                if (spec[4]) {
                    var existing = list.find(function (entry) { return entry && entry.Id === item.Id; });
                    if (existing) existing.Enabled = true;
                    else {
                        if (list.length >= 100) {
                            feedback.textContent = 'Remove a saved choice before adding another (limit: 100).';
                            return;
                        }
                        list.push({ Id: item.Id, Name: item.Name, Path: item.Path,
                            AncestorIds: item.AncestorIds, Enabled: true });
                    }
                    instance.config[spec[1]] = list;
                } else if (!list.includes(item.Id)) {
                    if (list.length >= 100) {
                        feedback.textContent = 'Remove a saved choice before adding another (limit: 100).';
                        return;
                    }
                    instance.config[spec[1]] = list.concat(item.Id);
                }
                renderSaved();
                feedback.textContent = 'Selected: ' + (item.Path || item.Name || item.Id);
                return;
            }
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
            renderSaved();
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
                if (epoch !== instance.viewEpoch || !browsingSelected(instance) || picker.hidden || loaded) return;
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
                        epoch !== instance.viewEpoch || !browsingSelected(instance) || picker.hidden || !details.open) throw error;
                    status.textContent = 'Still loading choices. Trying again...';
                    return new Promise(function (resolve) { setTimeout(resolve, 1000); }).then(function () {
                        if (epoch !== instance.viewEpoch || !browsingSelected(instance) || picker.hidden || !details.open) throw error;
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
                        if (spec[3]) topicNames.set(item.Id, name);
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
                    if (spec[3]) renderSaved();
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
                if (epoch !== instance.viewEpoch || !browsingSelected(instance) || picker.hidden || instance.saving || instance.refreshBusy) return;
                details.open = !details.open;
                if (details.open) return load();
            });
            details.addEventListener('toggle', function () {
                if (details.open) return load();
            });
            if (!parent) openRoot = function () {
                details.open = true;
                return load();
            };
            return details;
        }
        var root = branch('', 'Browse ' + spec[2]);
        root.className = 'tuneInSelectorRoot';
        picker.appendChild(root);
        columns.appendChild(picker);
        columns.appendChild(savedColumn);
        section.appendChild(columns);
        renderSaved();
        return section;
    }

    function editingBlocked(instance) {
        return !instance.config || instance.settingsLocked !== false || instance.lockChanging || instance.saving ||
            !instance.refreshAvailable || instance.refreshBusy || instance.refreshAction || instance.cleanupBusy || instance.cleanupAction;
    }

    function browsingSelected(instance) {
        return instance.view.querySelector('.configurationCompleted').checked;
    }

    function showSaveHistory(instance, saved, filteringConfigured) {
        var view = instance.view;
        view.querySelector('.globalSummary').parentNode.setAttribute('data-saved', String(saved));
        var browsing = view.querySelector('.browsingSummary').parentNode;
        browsing.setAttribute('data-filtering-configured', String(!!filteringConfigured));
        browsing.open = !filteringConfigured;
        ['.globalSummary', '.categoriesSummary', '.stationSelectionsSummary'].forEach(function (selector) {
            view.querySelector(selector).parentNode.open = !saved;
        });
        if (saved) {
            view.querySelector('.selectors').childNodes.forEach(function (section) { section.open = false; });
        }
    }

    function editing(instance) {
        var disabled = !!editingBlocked(instance);
        var catalogDisabled = disabled || !browsingSelected(instance);
        instance.view.querySelector('.loginFields').disabled = catalogDisabled;
        instance.view.querySelector('.configurationFields').disabled = catalogDisabled;
        instance.view.querySelector('.globalFields').disabled = disabled;
        instance.view.querySelector('.hideRegionalGenres').disabled = catalogDisabled;
        instance.view.querySelector('.allowPartialCatalogImport').disabled = catalogDisabled;
        instance.view.querySelector('.apiRequestIntervalSeconds').disabled = catalogDisabled;
        instance.view.querySelector('.importDeadlineHours').disabled = catalogDisabled;
        instance.view.querySelector('.discoveryDeadlineHours').disabled = catalogDisabled;
        instance.view.querySelector('.maxStationCount').disabled = catalogDisabled;
        instance.view.querySelector('.traversalDepthLimit').disabled = catalogDisabled;
        instance.view.querySelector('.enableCatalogIdentityDiagnostics').disabled = catalogDisabled;
        instance.view.querySelector('.tuneInSettingsSave').disabled = disabled;
        instance.refreshCategoryRows.forEach(function (row) { row.schedule.disabled = catalogDisabled; });
        var toggle = instance.view.querySelector('.settingsLocked');
        toggle.disabled = typeof instance.settingsLocked !== 'boolean' || !!instance.lockChanging || !!instance.saving ||
            !instance.refreshAvailable || !!instance.refreshBusy || !!instance.refreshAction || !!instance.cleanupBusy || !!instance.cleanupAction;
        toggle.checked = instance.lockChanging ? instance.lockRequested : instance.settingsLocked === true;
        var state = instance.view.querySelector('.settingsLockState');
        var confirmed = instance.settingsLocked;
        state.textContent = confirmed === true ? 'Locked' : confirmed === false ? 'Unlocked' : 'Checking';
        state.setAttribute('data-lock-state', confirmed === true ? 'locked' : confirmed === false ? 'unlocked' : 'unknown');
        var note = instance.view.querySelector('.settingsLockStatus');
        var text = instance.lockError || (instance.saving ? 'Saving and locking settings...' :
            instance.lockChanging ? 'Updating settings lock...' :
            instance.cleanupBusy || instance.cleanupAction ? 'Settings cannot be changed while maintenance is active.' :
            instance.refreshBusy || instance.refreshAction ? 'Settings cannot be changed while catalog refresh is active.' :
            !instance.refreshAvailable ? 'Settings are unavailable until refresh status is available.' :
            typeof confirmed === 'boolean' ? '' : 'Reading settings lock...');
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
            if (epoch === instance.viewEpoch) {
                instance.settingsLocked = locked;
                if (!locked) instance.view.querySelector('.browsingSummary').parentNode.open = true;
            }
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

    function isChannelRefreshTask(task) {
        return !!task && (task.Key === 'RefreshInternetChannels' || task.Name === 'Refresh Internet Channels');
    }

    function checkChannelRefreshSchedule(instance, epoch) {
        var notice = instance.view.querySelector('.channelScheduleNotice');
        return ApiClient.ajax({ type: 'GET', dataType: 'json', url: ApiClient.getUrl('ScheduledTasks') })
            .then(function (tasks) {
                if (epoch !== instance.viewEpoch) return;
                var scheduled = (tasks || []).filter(isChannelRefreshTask)
                    .some(function (task) { return (task.Triggers || []).length > 0; });
                notice.textContent = scheduled
                    ? 'Emby\'s "Refresh Internet Channels" scheduled task has a schedule. Remove its triggers in Advanced > Scheduled Tasks so TuneIn refreshes are scheduled only from this control panel.'
                    : '';
                notice.hidden = !scheduled;
            }, function (error) {
                console.error('Unable to check the Refresh Internet Channels schedule.', error);
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

    function categoryOutcome(state) {
        return /^(Imported|Unchanged|Completed)$/.test(state) ? 'Completed'
            : /^Limited\b/.test(state) ? 'Limited'
            : /^(Partial|Incomplete)\b/.test(state) ? 'Partial'
            : /^(Failed|Error|Timed out|TimedOut)$/.test(state) ? 'Error' : '';
    }

    function setRefreshOutcome(instance, outcome, text) {
        instance.refreshSection.setAttribute('data-outcome', outcome);
        instance.view.querySelector('.refreshOverall').textContent = ' - Overall: ' + text;
    }

    function renderRefresh(instance, status) {
        instance.refreshBusy = !!status.Busy;
        var version = instance.view.querySelector('.tuneInPluginVersion');
        if (version && status.PluginVersion) {
            version.textContent = 'TuneIn plugin ' + status.PluginVersion;
            version.hidden = false;
        }
        if (instance.refreshDefaultPending) {
            instance.refreshDefaultPending = false;
            instance.refreshSection.open = instance.refreshBusy ||
                status.State === 'NeverRun' && status.HasRefreshBeenStarted === false && !status.StartedUtc;
        }
        var outcomes = (status.Categories || []).map(function (category) { return categoryOutcome(category.State); });
        var overall = categoryOutcome(status.State);
        if (overall === 'Error' || outcomes.includes('Error')) overall = 'Error';
        else if (overall === 'Partial' || outcomes.includes('Partial')) overall = 'Partial';
        else if (overall === 'Limited' || outcomes.includes('Limited')) overall = 'Limited';
        else if (!status.Busy && /^(Completed|NeverRun)$/.test(status.State) &&
            outcomes.length && outcomes.every(function (outcome) { return outcome === 'Completed'; })) overall = 'Completed';
        else overall = '';
        setRefreshOutcome(instance, overall === 'Limited' ? 'Partial' : overall, overall === 'Error' ? 'error'
            : overall === 'Limited' ? 'limited discovery'
            : overall === 'Partial' ? 'partial' : overall === 'Completed' ? 'successful'
            : 'not all categories confirmed successful');
        instance.view.querySelector('.refreshIndicator').hidden = !instance.refreshBusy;
        renderCleanup(instance, status.Cleanup);
        if (typeof status.SettingsLocked === 'boolean' && !instance.lockChanging && !instance.saving) {
            instance.settingsLocked = status.SettingsLocked;
            instance.lockError = '';
        }
        var previousStatus = instance.refreshStatus;
        instance.refreshStatus = status;
        renderCatalogRefreshPrompt(instance);
        var category = ({ local: 'Local Radio', music: 'Music', language: 'By Language', location: 'By Location',
            talk: 'Talk', sports: 'Sports', podcast: 'Podcasts', presets: 'My Favorites' })[status.Category];
        var elapsed = formatDuration(elapsedSeconds(status));
        var progressAge = formatDuration(progressAgeSeconds(status));
        var pages = typeof status.CatalogPagesCached === 'number' ? status.CatalogPagesCached.toLocaleString() : null;
        var pageLimit = typeof status.CatalogPagesLimit === 'number' ? status.CatalogPagesLimit.toLocaleString() : null;
        var summary = [];
        if (status.State !== 'NeverRun') summary.push(status.State);
        if (status.Busy && status.Phase) summary.push(status.Phase);
        if (category) summary.push(category);
        if (status.Busy && status.Phase === 'Importing' && status.ImportFoldersTotal)
            summary.push(status.ImportFoldersServed.toLocaleString() + '/' + status.ImportFoldersTotal.toLocaleString() + ' folders');
        else if (status.Busy && status.Phase === 'Discovering' && pages != null)
            summary.push(pages + ' pages');
        if (status.Busy && progressAge) summary.push('progress ' + progressAge + ' ago');
        else if (!status.Busy && elapsed) summary.push('elapsed ' + elapsed);
        instance.view.querySelector('.refreshSummaryDetails').textContent = summary.length ? ' - ' + summary.join(' - ') : '';

        var description = status.State === 'NeverRun' ? 'No Control Panel refresh has run.' :
            [status.State, status.Phase !== status.State ? status.Phase : null, category].filter(Boolean).join(' - ');
        if (status.ActiveRequest && status.ActiveRequest !== status.Category)
            description += ' (requested: ' + refreshName(status, status.ActiveRequest) + ')';
        if (status.AutomaticRetry && status.AutomaticRetryLimit)
            description += '. Automatic retry ' + status.AutomaticRetry + ' of ' + status.AutomaticRetryLimit
                + ' after ' + (status.AutomaticRetryPhase === 'import' ? 'import' : 'discovery')
                + (status.AutomaticRetryReason === 'interruption' ? ' was interrupted' : ' timed out');
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

        var contents = status.ImportProgress;
        var contentsText = instance.view.querySelector('.refreshImportContents');
        contentsText.hidden = !contents;
        contentsText.textContent = contents
            ? 'Listings served: ' + contents.StationEntriesServed.toLocaleString() + ' / '
                + contents.StationEntriesTotal.toLocaleString() + ' station entries; '
                + contents.SubfolderEntriesServed.toLocaleString() + ' / '
                + contents.SubfolderEntriesTotal.toLocaleString() + ' subfolder entries. Still in unserved listings: '
                + (contents.StationEntriesTotal - contents.StationEntriesServed).toLocaleString() + ' station entries, '
                + (contents.SubfolderEntriesTotal - contents.SubfolderEntriesServed).toLocaleString() + ' subfolder entries.'
                + (typeof contents.LastFolderStationEntries === 'number'
                    ? ' Last newly served folder: ' + contents.LastFolderStationEntries.toLocaleString() + ' station entries, '
                        + contents.LastFolderSubfolderEntries.toLocaleString() + ' subfolder entries.' : '')
                + ' Entries can repeat across folders; served does not mean stored in Emby.'
            : '';
        var pending = contents && contents.PendingFolders;
        var pendingText = instance.view.querySelector('.refreshPendingFolderText');
        pendingText.textContent = (pending || []).map(function (folder) {
            return '[' + folder.Reference + '] ' + folder.Path + ': '
                + folder.StationEntries.toLocaleString() + ' station entries, '
                + folder.SubfolderEntries.toLocaleString() + ' subfolder entries';
        }).join('\n');
        if (contents && contents.PendingFoldersOmitted)
            pendingText.textContent += '\nAdditional folder details omitted: ' + contents.PendingFoldersOmitted.toLocaleString();
        instance.view.querySelector('.refreshPendingFolders').hidden =
            !pending || !pending.length && !contents.PendingFoldersOmitted;

        var diagnostics = [];
        if (typeof status.RequestsStarted === 'number' || typeof status.RequestsCompleted === 'number') {
            diagnostics.push('Provider requests: ' + (status.RequestsStarted || 0) + ' attempts, '
                + (status.RequestsCompleted || 0) + ' successful');
        }
        if (typeof status.CacheHits === 'number') diagnostics.push(status.CacheHits + ' cached page replays');
        if (typeof status.ActiveRequests === 'number')
            diagnostics.push(status.ActiveRequests + ' active HTTP ' + (status.ActiveRequests === 1 ? 'request' : 'requests'));
        if (typeof status.RateLimitRetries === 'number') diagnostics.push(status.RateLimitRetries + ' rate-limit retries');
        if (status.ImportTimeouts)
            diagnostics.push('Import timeouts: ' + status.ImportTimeouts
                + ((status.State === 'Completed' || status.State === 'Partial') && !status.ImportMayHavePartiallyApplied
                    ? ' (recovered)' : ''));
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
                description += '. To apply valid results, enable "Import Valid Results when catalog entries are skipped" in Global TuneIn Settings, save, then refresh again';
            diagnostics.push('Skipped malformed TuneIn catalog links: ' + status.SkippedBrowseLinks
                + (status.SkippedBrowseLinkDetails ? ' (' + status.SkippedBrowseLinkDetails + ')' : ''));
        }
        var diagnosticText = instance.view.querySelector('.refreshDiagnosticsText');
        diagnosticText.textContent = diagnostics.join('; ');
        diagnosticText.hidden = !diagnostics.length;
        instance.view.querySelector('#tuneInReportHint').hidden = !hasRefreshReport(status) || status.Busy;
        if (status.LastHttpStatus === 429 && status.State === 'Completed')
            description += '. TuneIn temporarily limited request speed (HTTP 429 Too Many Requests); '
                + 'the plugin waited, retried and finished normally';
        else if (status.LastHttpStatus) {
            var label = httpStatusLabels[status.LastHttpStatus] || 'Unexpected response';
            description += '. TuneIn returned HTTP ' + status.LastHttpStatus + ' (' + label + ')';
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
        if (!hasRefreshReport(status) || status.Busy || !previousStatus || status.FinishedUtc !== previousStatus.FinishedUtc) {
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
                var counts = document.createElement('dl');
                counts.className = 'tuneInStationCounts';
                counts.setAttribute('aria-label', item.Name + ' station counts');
                var countValues = {};
                [['available', 'Available (filtered)'], ['emby', 'In Emby']]
                    .forEach(function (metric) {
                        var group = document.createElement('div');
                        var label = document.createElement('dt');
                        label.textContent = metric[1];
                        var value = document.createElement('dd');
                        group.appendChild(label);
                        group.appendChild(value);
                        counts.appendChild(group);
                        countValues[metric[0]] = value;
                    });
                var schedule = document.createElement('select');
                schedule.setAttribute('is', 'emby-select');
                schedule.setAttribute('aria-label', item.Name + ' refresh interval');
                [['0', 'Schedule: Off'], ['6', 'Every 6 hours'], ['12', 'Every 12 hours'],
                    ['24', 'Daily'], ['168', 'Weekly'], ['720', 'Every 30 days']].forEach(function (choice) {
                    var option = document.createElement('option');
                    option.value = choice[0];
                    option.textContent = choice[1];
                    schedule.appendChild(option);
                });
                schedule.value = String(instance.refreshSchedules.get(item.Category) || 0);
                schedule.addEventListener('change', function () {
                    if (!editingBlocked(instance) && browsingSelected(instance))
                        instance.refreshSchedules.set(item.Category, Number(schedule.value));
                });
                row.appendChild(caption);
                actions.appendChild(counts);
                actions.appendChild(control);
                actions.appendChild(schedule);
                row.appendChild(actions);
                list.appendChild(row);
                rows.set(item.Category, { row: row, caption: caption, control: control, schedule: schedule, counts: countValues });
            });
            $(list).trigger('create');
        }
        categories.forEach(function (item) {
            var state = (status.QueuedRequests || []).includes(item.Category) ? 'Queued' : item.State;
            var row = rows.get(item.Category);
            row.row.setAttribute('data-outcome', categoryOutcome(state));
            var text = item.Name + ': ' + state;
            if (!status.Busy && item.Category === status.DirtyCategory) text += ' (reconciliation needed)';
            var metrics = item.StationCounts || {};
            if (item.Category === 'language' && state === 'Imported' && metrics.AvailableStations === 0 && !metrics.AvailablePartial)
                text += ' (TuneIn lists no stations for the selected languages)';
            var caption = rows.get(item.Category).caption;
            if (caption.textContent !== text) caption.textContent = text;
            var values = rows.get(item.Category).counts;
            function count(value) {
                return typeof value === 'number' && Number.isInteger(value) && value >= 0
                    ? value.toLocaleString() : 'Not checked';
            }
            values.available.textContent = count(metrics.AvailableStations)
                + (metrics.AvailablePartial && typeof metrics.AvailableStations === 'number' ? ' (partial)' : '');
            var knownInventory = typeof metrics.EmbyStations === 'number'
                && Number.isInteger(metrics.EmbyStations) && metrics.EmbyStations >= 0;
            values.emby.textContent = metrics.EmbyCountError
                ? (knownInventory ? count(metrics.EmbyStations) + ' (last known)' : 'Unavailable')
                : count(metrics.EmbyStations);
            values.available.setAttribute('title', metrics.AvailableCheckedUtc
                ? 'Discovery checked at ' + new Date(metrics.AvailableCheckedUtc).toLocaleString()
                    + (metrics.AvailablePartial ? '; skipped branches make this a lower bound.' : '')
                : 'Run a category refresh to count stations within the saved filters.');
            var inventoryTime = metrics.EmbyCheckedUtc
                ? new Date(metrics.EmbyCheckedUtc).toLocaleString() : null;
            values.emby.setAttribute('title', metrics.EmbyCountError
                ? (knownInventory && inventoryTime ? 'Last successful inventory check: ' + inventoryTime + '. ' : '')
                    + metrics.EmbyCountError
                : inventoryTime ? 'Emby inventory checked at ' + inventoryTime
                : 'Run a category refresh to check Emby inventory.');
        });
    }

    function backupInProgress(instance) {
        return instance.backupStarting || instance.backupState === 'Running' || instance.backupState === 'Busy';
    }

    function readBackup(instance, start) {
        if (!instance.refreshVisible || !instance.maintenanceSection.open) return;
        if (instance.backupRequest || (start && instance.view.querySelector('.backupStart').disabled)) return;
        var epoch = instance.viewEpoch;
        var request = {};
        instance.backupRequest = request;
        if (start) {
            instance.backupStarting = true;
            instance.view.querySelector('.backupStatus').textContent = 'Requesting Emby Server Backup...';
            refreshControls(instance, instance.refreshAvailable);
        }
        return ApiClient.ajax({
            type: start ? 'POST' : 'GET', dataType: 'json',
            url: ApiClient.getUrl('TuneIn/Backup/' + (start ? 'Start' : 'Status'))
        }).then(function (status) {
            if (epoch !== instance.viewEpoch || instance.backupRequest !== request) return;
            instance.backupState = status.State;
            var message = {
                Ready: 'No backup result is recorded. Run Backup Emby or verify a successful backup before cleanup.',
                Unavailable: 'Emby Server Backup is unavailable. Configure Backup & Restore with Emby Premiere, or verify another backup before cleanup.',
                Running: 'Backup requested. Wait for Emby to report a successful result before cleanup.',
                Busy: 'Emby Server Backup is already active. Wait for it to finish before cleanup.',
                Completed: 'Emby reported that its latest backup completed successfully.',
                Failed: 'The latest Emby backup did not complete successfully. Check Scheduled Tasks before cleanup.',
                Unconfirmed: 'Emby has not confirmed a new backup result. Check Scheduled Tasks before cleanup.'
            }[status.State];
            var target = instance.view.querySelector('.backupStatus');
            if (target.textContent !== message) target.textContent = message;
        }).catch(function () {
            if (epoch !== instance.viewEpoch || instance.backupRequest !== request) return;
            instance.view.querySelector('.backupStatus').textContent =
                'Could not verify the Emby backup status. Check Scheduled Tasks before cleanup.';
            // A failed start response does not prove that the server did not start the backup.
            if (start) instance.backupState = 'Running';
        }).finally(function () {
            if (instance.backupRequest !== request) return;
            instance.backupRequest = null;
            instance.backupStarting = false;
            if (epoch !== instance.viewEpoch) return;
            refreshControls(instance, instance.refreshAvailable);
            if (start) scheduleStatus(instance, epoch);
        });
    }

    function renderCleanup(instance, cleanup) {
        instance.cleanupBusy = !!cleanup && cleanup.State === 'Running';
        instance.view.querySelector('.maintenanceIndicator').hidden = !instance.cleanupBusy;
        var target = instance.view.querySelector('.cleanupStatus');
        var text = '';
        if (cleanup && cleanup.State !== 'Idle') {
            text = cleanup.Removed + ' of ' + cleanup.Total + ' entries removed.';
            if (cleanup.Failed) text += ' ' + cleanup.Failed + ' entries could not be removed.';
            if (cleanup.Message) text += ' ' + cleanup.Message;
        }
        if (target.textContent !== text) target.textContent = text;
    }

    function clearCleanupPreview(instance) {
        instance.cleanupPreview = null;
        instance.view.querySelector('.cleanupConfirmation').hidden = true;
        instance.view.querySelector('.cleanupBackupVerified').checked = false;
    }

    function clearStationMatches(instance) {
        instance.cleanupStation = null;
        instance.stationSearchGeneration = (instance.stationSearchGeneration || 0) + 1;
        instance.stationSearching = false;
        instance.view.querySelector('.cleanupStationResults').textContent = '';
        instance.view.querySelector('.cleanupStationSearchStatus').textContent = '';
    }

    function stationReferenceLike(text) {
        return /^\d+$/.test(text) || /^[0-9a-f]{8}-?([0-9a-f]{4}-?){3}[0-9a-f]{12}$/i.test(text) || /^https?:\/\//i.test(text);
    }

    function searchCleanupStations(instance) {
        var view = instance.view;
        var name = view.querySelector('.cleanupStationReference').value.trim();
        if (!instance.refreshVisible || !instance.maintenanceSection.open ||
            view.querySelector('.cleanupStationSearch').disabled) return;
        clearCleanupPreview(instance);
        clearStationMatches(instance);
        var epoch = instance.viewEpoch;
        var generation = instance.stationSearchGeneration;
        var status = view.querySelector('.cleanupStationSearchStatus');
        var results = view.querySelector('.cleanupStationResults');
        instance.stationSearching = true;
        status.textContent = 'Searching TuneIn station entries...';
        refreshControls(instance, instance.refreshAvailable);
        return ApiClient.ajax({
            type: 'GET', dataType: 'json', url: ApiClient.getUrl('TuneIn/Cleanup/Stations', { Name: name })
        }).then(function (result) {
            if (epoch !== instance.viewEpoch || generation !== instance.stationSearchGeneration) return;
            var matches = result && result.Matches || [];
            status.textContent = !matches.length ? 'No TuneIn station entries match "' + name + '".'
                : (result.More ? 'Showing the first ' + matches.length + ' matches; enter more of the name to narrow them.'
                    : matches.length + (matches.length === 1 ? ' match' : ' matches') + '. Choose the entry to delete.');
            var buttons = [];
            matches.forEach(function (match) {
                var button = document.createElement('button');
                button.type = 'button';
                button.setAttribute('is', 'emby-button');
                button.setAttribute('role', 'listitem');
                button.setAttribute('aria-pressed', 'false');
                button.className = 'raised cleanupStationMatch';
                var title = document.createElement('span');
                title.textContent = match.Name;
                var where = document.createElement('span');
                where.className = 'cleanupStationMatchPath';
                where.textContent = match.Path;
                button.appendChild(title);
                button.appendChild(where);
                button.addEventListener('click', function () {
                    if (epoch !== instance.viewEpoch || generation !== instance.stationSearchGeneration || button.disabled) return;
                    clearCleanupPreview(instance);
                    instance.cleanupStation = match;
                    buttons.forEach(function (other) {
                        other.setAttribute('aria-pressed', other === button ? 'true' : 'false');
                    });
                    status.textContent = 'Selected "' + match.Name + '" in ' + match.Path + '. Select Preview cleanup.';
                    refreshControls(instance, instance.refreshAvailable);
                });
                buttons.push(button);
                results.appendChild(button);
            });
            $(results).trigger('create');
        }).catch(function () {
            if (epoch === instance.viewEpoch && generation === instance.stationSearchGeneration)
                status.textContent = 'Could not search TuneIn station entries. Try again.';
        }).finally(function () {
            if (epoch !== instance.viewEpoch || generation !== instance.stationSearchGeneration) return;
            instance.stationSearching = false;
            refreshControls(instance, instance.refreshAvailable);
        });
    }

    function doCleanup(instance, start) {
        var view = instance.view;
        var summary = view.querySelector('.maintenanceSummary');
        var button = view.querySelector(start ? '.cleanupConfirm' : '.cleanupPreview');
        if (!instance.refreshVisible || !summary.parentNode.open || button.disabled || (start && !instance.cleanupPreview)) return;
        var epoch = instance.viewEpoch;
        var preview = instance.cleanupPreview;
        clearCleanupPreview(instance);
        instance.cleanupAction = true;
        instance.refreshGeneration = (instance.refreshGeneration || 0) + 1;
        var error = view.querySelector('.cleanupError');
        error.textContent = '';
        view.querySelector('.cleanupStatus').textContent = start ? 'Starting cleanup...' : 'Checking channel entries...';
        refreshControls(instance, instance.refreshAvailable);
        return ApiClient.ajax({
            type: 'POST', dataType: 'json',
            url: ApiClient.getUrl('TuneIn/Cleanup/' + (start ? 'Start' : 'Preview')),
            contentType: 'application/json',
            data: JSON.stringify(start ? { Token: preview.Token }
                : view.querySelector('.cleanupScope').value === 'station'
                    ? { StationReference: instance.cleanupStation ? instance.cleanupStation.Id
                        : view.querySelector('.cleanupStationReference').value.trim() }
                    : { Category: view.querySelector('.cleanupScope').value })
        }).then(function (result) {
            if (epoch !== instance.viewEpoch) return;
            if (start) {
                clearStationMatches(instance);
                renderRefresh(instance, result);
                return;
            }
            if (!summary.parentNode.open) return;
            if (!result.Token) {
                error.textContent = result.Message || 'Nothing to clean up.';
                return;
            }
            instance.cleanupPreview = result;
            view.querySelector('.cleanupPreviewText').textContent =
                (result.StationReference
                    ? 'Remove only "' + result.StationName + '" (Emby item ' + result.StationReference + ') from ' + result.Scope +
                        '? Other placements remain; future discovery may add this entry again.'
                    : 'Remove ' + result.Folders + ' folders and ' + result.Stations + ' station entries from ' + result.Scope + '?') +
                (result.Message ? ' ' + result.Message : '');
            view.querySelector('.cleanupConfirm').textContent = result.StationReference ? 'Confirm delete station' : 'Remove generated entries';
            view.querySelector('.cleanupStatus').textContent = '';
            view.querySelector('.cleanupConfirmation').hidden = false;
        }).catch(function () {
            if (epoch === instance.viewEpoch) error.textContent = start
                ? 'Could not confirm cleanup started. Check its status, then preview again if needed.'
                : 'Could not preview cleanup. Wait for any refresh or settings changes to finish, then try again.';
        }).finally(function () {
            instance.cleanupAction = false;
            if (epoch === instance.viewEpoch) {
                refreshControls(instance, instance.refreshAvailable);
                if (instance.cleanupPreview) view.querySelector('.cleanupBack').focus();
                if (start && summary.parentNode.open) summary.focus();
            }
            if (instance.refreshVisible) pollStatus(instance, instance.viewEpoch);
        });
    }

    function stopCleanup(instance) {
        var view = instance.view;
        if (!instance.refreshVisible || !instance.maintenanceSection.open ||
            view.querySelector('.cleanupCancel').disabled) return;
        var epoch = instance.viewEpoch;
        instance.cleanupAction = true;
        instance.refreshGeneration = (instance.refreshGeneration || 0) + 1;
        view.querySelector('.cleanupError').textContent = '';
        refreshControls(instance, instance.refreshAvailable);
        return ApiClient.ajax({
            type: 'POST', dataType: 'json', url: ApiClient.getUrl('TuneIn/Cleanup/Cancel')
        }).then(function (status) {
            if (epoch === instance.viewEpoch) renderRefresh(instance, status);
        }).catch(function () {
            if (epoch === instance.viewEpoch)
                view.querySelector('.cleanupError').textContent =
                    'Could not confirm cleanup stopped. Check its status and try again.';
        }).finally(function () {
            instance.cleanupAction = false;
            if (epoch === instance.viewEpoch) refreshControls(instance, instance.refreshAvailable);
            if (instance.refreshVisible) pollStatus(instance, instance.viewEpoch);
        });
    }

    function refreshControls(instance, available) {
        var cannotStart = !available || !instance.config || !!instance.refreshAction || instance.saving ||
            instance.cleanupBusy || instance.cleanupAction;
        var status = instance.refreshStatus;
        instance.refreshAvailable = available;
        var queued = status && status.QueuedRequests || [];
        function blocked(key) {
            return cannotStart || !status || status.QueueSlotsAvailable === 0 || status.Cancelling ||
                status.ActiveRequest === key || queued.includes(key);
        }
        instance.view.querySelector('.refreshStart').disabled = blocked('all') || !(status.Categories || []).length;
        instance.view.querySelector('.refreshCancel').disabled = !available || !instance.refreshBusy || !!instance.refreshAction;
        instance.view.querySelector('.refreshLogButton').disabled = !available || !hasRefreshReport(status)
            || status.Busy || !status.FinishedUtc || !!instance.refreshAction || !!instance.refreshLogAction;
        instance.refreshCategoryRows.forEach(function (row, category) {
            row.control.disabled = blocked(category);
        });
        var cannotClean = !available || !!instance.refreshBusy || !!instance.refreshAction || !!instance.saving ||
            !!instance.lockChanging || !!instance.cleanupBusy || !!instance.cleanupAction;
        instance.view.querySelector('.backupStart').disabled = !!instance.cleanupBusy || !!instance.cleanupAction ||
            !!backupInProgress(instance) || !!instance.backupRequest || !instance.backupState || instance.backupState === 'Unavailable';
        cannotClean = cannotClean || !!backupInProgress(instance);
        instance.view.querySelector('.cleanupScope').disabled = cannotClean;
        var stationMode = instance.view.querySelector('.cleanupScope').value === 'station';
        instance.view.querySelector('.cleanupStationFields').hidden = !stationMode;
        instance.view.querySelector('.cleanupStationReference').disabled = cannotClean || !stationMode;
        var stationText = instance.view.querySelector('.cleanupStationReference').value.trim();
        instance.view.querySelector('.cleanupStationSearch').disabled = cannotClean || !stationMode ||
            !!instance.stationSearching || stationText.length < 2 || stationText.length > 100;
        instance.view.querySelector('.cleanupStationResults').childNodes.forEach(function (button) {
            button.disabled = cannotClean || !stationMode;
        });
        instance.view.querySelector('.cleanupPreview').disabled = cannotClean || !instance.view.querySelector('.cleanupScope').value ||
            (stationMode && !instance.cleanupStation && !stationReferenceLike(stationText));
        instance.view.querySelector('.cleanupCancel').disabled = !available || !instance.cleanupBusy ||
            !!instance.cleanupAction || !!(status && status.Cleanup && status.Cleanup.Cancelling);
        instance.view.querySelector('.cleanupConfirm').disabled = cannotClean || !instance.cleanupPreview ||
            !instance.view.querySelector('.cleanupBackupVerified').checked;
        editing(instance);
    }

    function hasRefreshReport(status) {
        return !!status && (status.ReportAvailable === true || status.State === 'Error');
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
        instance.view.querySelector('.refreshLogMessage').textContent = 'Retrieving the latest refresh diagnostic report...';
        refreshControls(instance, instance.refreshAvailable);
        return ApiClient.ajax({
            type: 'GET', dataType: 'json', url: ApiClient.getUrl('TuneIn/Refresh/Log')
        }).then(function (result) {
            if (epoch !== instance.viewEpoch || generation !== (instance.refreshGeneration || 0)
                || !instance.refreshVisible || !hasRefreshReport(instance.refreshStatus) || instance.refreshStatus.Busy
                || instance.refreshStatus.FinishedUtc !== finishedUtc || result.FinishedUtc !== finishedUtc) return;
            var report = instance.view.querySelector('.refreshFailureLog');
            report.textContent = result.Text || '';
            report.hidden = false;
            instance.view.querySelector('.refreshLogMessage').textContent = result.Truncated
                ? 'Refresh report retrieved; some details were omitted or truncated.'
                : 'Refresh report retrieved.';
        }).catch(function () {
            if (epoch === instance.viewEpoch && generation === (instance.refreshGeneration || 0)
                && hasRefreshReport(instance.refreshStatus) && !instance.refreshStatus.Busy
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
        instance.refreshTimer = setTimeout(function () { pollStatus(instance, epoch); },
            instance.refreshBusy || instance.cleanupBusy || backupInProgress(instance) ? 1500 : 10000);
    }

    function pollStatus(instance, epoch) {
        if (epoch !== instance.viewEpoch || !instance.refreshVisible) return;
        clearTimeout(instance.refreshTimer);
        readBackup(instance, false);
        if (instance.refreshAction || instance.lockChanging || instance.cleanupAction) return scheduleStatus(instance, epoch);
        if (instance.refreshPoll) return instance.refreshPoll;
        var generation = instance.refreshGeneration || 0;
        instance.refreshPoll = ApiClient.ajax({
            type: 'GET', dataType: 'json', url: ApiClient.getUrl('TuneIn/Refresh/Status')
        }).then(function (status) {
            if (epoch === instance.viewEpoch && generation === (instance.refreshGeneration || 0)) renderRefresh(instance, status);
        }).catch(function () {
            if (epoch === instance.viewEpoch && generation === (instance.refreshGeneration || 0)) {
                setRefreshOutcome(instance, '', 'status unavailable');
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
        var dialog = instance.view.querySelector('.tuneInRefreshRecoveryDialog');
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
        var config = Object.assign({}, instance.config);
        var coordinates = instance.view.querySelector('.txtLatLon').value.trim();
        var requestInterval = String(instance.view.querySelector('.apiRequestIntervalSeconds').value || '').trim();
        if (!/^[0-9]+$/.test(requestInterval) || Number(requestInterval) < 1 || Number(requestInterval) > 30) {
            message(instance, 'Use a whole number from 1 to 30 for the API request interval.');
            return false;
        }
        var discoveryDeadline = String(instance.view.querySelector('.discoveryDeadlineHours').value || '').trim();
        if (!/^[0-9]+$/.test(discoveryDeadline) || Number(discoveryDeadline) < 1 || Number(discoveryDeadline) > 10) {
            message(instance, 'Use a whole number from 1 to 10 for the discovery deadline.');
            return false;
        }
        var importDeadline = String(instance.view.querySelector('.importDeadlineHours').value || '').trim();
        if (!/^[0-9]+$/.test(importDeadline) || Number(importDeadline) < 1 || Number(importDeadline) > 100) {
            message(instance, 'Use a whole number from 1 to 100 for the import deadline.');
            return false;
        }
        var maxStationCount = String(instance.view.querySelector('.maxStationCount').value || '').trim();
        if (maxStationCount !== '' && (!/^[0-9]+$/.test(maxStationCount) || Number(maxStationCount) < 1 || Number(maxStationCount) > 4000)) {
            message(instance, 'Use Not set (blank) or a whole number from 1 to 4000 for Max Station Count.');
            return false;
        }
        var traversalDepth = String(instance.view.querySelector('.traversalDepthLimit').value || '').trim();
        if (!/^[0-9]+$/.test(traversalDepth) || Number(traversalDepth) < 3 || Number(traversalDepth) > 50) {
            message(instance, 'Use a whole number from 3 to 50 for the traversal depth limit.');
            return false;
        }
        var localEnabled = instance.view.querySelector('[data-category="EnableLocalRadio"]').checked;
        var browsing = browsingSelected(instance);
        var source = instance.view.querySelector('.localSource').value;
        var useCoordinates = localEnabled && source === 'coordinates';
        var localSelectionMode = selectorMode(instance, selectors[6]);
        var hasSavedRegion = (config.LocalCities || []).some(function (item) {
            return item && item.Enabled !== false && /^r[1-9][0-9]{0,9}$/.test(item.Id);
        });
        if (browsing && localEnabled && source !== 'coordinates' && (source !== 'regions' ||
            localSelectionMode === 'selected' && !hasSavedRegion)) {
            message(instance, 'Choose coordinates or regions for Local Radio; Selected only requires at least one saved city or region.');
            return false;
        }
        var parts = coordinates.split(',');
        if (browsing && useCoordinates && (parts.length !== 2 || parts.some(function (part) {
            return !/^[+-]?(?:\d+(?:\.\d*)?|\.\d+)(?:e[+-]?\d+)?$/i.test(part.trim()) || !isFinite(Number(part));
        }) || Math.abs(Number(parts[0])) > 90 || Math.abs(Number(parts[1])) > 180)) {
            message(instance, 'Enter finite latitude (-90..90), longitude (-180..180), separated by a comma.');
            return false;
        }
        config.SchemaVersion = 1;
        config.ConfigurationCompleted = browsing;
        config.Username = instance.view.querySelector('.txtUsername').value.trim() || null;
        config.LatLon = coordinates || null;
        config.ApiRequestIntervalSeconds = Number(requestInterval);
        config.ImportDeadlineHours = Number(importDeadline);
        config.DiscoveryDeadlineHours = Number(discoveryDeadline);
        config.MaxStationCount = maxStationCount === '' ? null : Number(maxStationCount);
        config.TraversalDepthLimit = Number(traversalDepth);
        config.EnableCatalogIdentityDiagnostics = instance.view.querySelector('.enableCatalogIdentityDiagnostics').checked;
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
        var filteringChanged = JSON.stringify(filteringSnapshot(config)) !==
            JSON.stringify(filteringSnapshot(instance.savedFilteringConfig || {}));
        instance.saving = true;
        instance.lockError = '';
        refreshControls(instance, instance.refreshAvailable);
        loading.show();
        ApiClient.updatePluginConfiguration(pluginId, config).then(function (result) {
            if (epoch === instance.viewEpoch) {
                showSaveHistory(instance, true, !configurationRequired(config));
                instance.savedFilteringConfig = JSON.parse(JSON.stringify(config));
                if (filteringChanged) {
                    instance.refreshDefaultPending = false;
                    instance.refreshSection.open = true;
                }
                if (filteringChanged && instance.selectionDeletionPending) {
                    var deletionNotice = instance.view.querySelector('.stationDeletionNotice');
                    deletionNotice.textContent = 'Saved station selections are scheduled for deletion. Start Catalog Refresh to update the Emby library with these changes.';
                    deletionNotice.hidden = false;
                }
                instance.selectionDeletionPending = false;
                checkChannelRefreshSchedule(instance, epoch);
            }
            return writeSettingsLock(true).then(function () {
                instance.settingsLocked = true;
                if (epoch !== instance.viewEpoch) return;
                Dashboard.processPluginConfigurationUpdateResult(result);
                message(instance, ['Configuration saved and locked.', browsingStatus(config), 'No catalog crawl was started.']
                    .filter(Boolean).join(' '), configurationRequired(config));
                renderCatalogRefreshPrompt(instance);
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
        instance.maintenanceSection = view.querySelector('.maintenanceSummary').parentNode;
        instance.refreshCategoryRows = new Map();
        instance.refreshSchedules = new Map();
        ['.globalSummary', '.browsingSummary', '.categoriesSummary', '.stationSelectionsSummary', '.maintenanceSummary'].forEach(function (selector) {
            bindDisclosure(view.querySelector(selector));
        });
        bindDisclosure(view.querySelector('.refreshSummary'), function () { instance.refreshDefaultPending = false; });
        view.querySelector('.settingsLocked').addEventListener('change', function () { return changeSettingsLock(instance); });
        view.querySelector('.configurationCompleted').addEventListener('change', function () { editing(instance); });
        var dialog = view.querySelector('.tuneInRefreshRecoveryDialog');
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
        view.querySelector('.cleanupPreview').addEventListener('click', function () { return doCleanup(instance, false); });
        view.querySelector('.cleanupConfirm').addEventListener('click', function () { return doCleanup(instance, true); });
        view.querySelector('.cleanupCancel').addEventListener('click', function () { return stopCleanup(instance); });
        view.querySelector('.cleanupScope').addEventListener('change', function () {
            clearCleanupPreview(instance);
            clearStationMatches(instance);
            refreshControls(instance, instance.refreshAvailable);
        });
        view.querySelector('.cleanupStationReference').addEventListener('input', function () {
            clearCleanupPreview(instance);
            clearStationMatches(instance);
            refreshControls(instance, instance.refreshAvailable);
        });
        view.querySelector('.cleanupStationReference').addEventListener('keydown', function (event) {
            if (event.key !== 'Enter') return;
            event.preventDefault();
            if (stationReferenceLike(view.querySelector('.cleanupStationReference').value.trim())) return doCleanup(instance, false);
            return searchCleanupStations(instance);
        });
        view.querySelector('.cleanupStationSearch').addEventListener('click', function () { return searchCleanupStations(instance); });
        view.querySelector('.cleanupBackupVerified').addEventListener('change', function () {
            refreshControls(instance, instance.refreshAvailable);
        });
        view.querySelector('.backupStart').addEventListener('click', function () { return readBackup(instance, true); });
        view.querySelector('.cleanupBack').addEventListener('click', function () {
            clearCleanupPreview(instance);
            refreshControls(instance, instance.refreshAvailable);
            view.querySelector('.maintenanceSummary').focus();
        });
        instance.maintenanceSection.addEventListener('toggle', function () {
            if (!instance.maintenanceSection.open) { clearCleanupPreview(instance); clearStationMatches(instance); }
            else return readBackup(instance, false);
        });
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
        instance.refreshDefaultPending = true;
        instance.refreshSection.open = false;
        instance.selectionDeletionPending = false;
        setRefreshOutcome(instance, '', 'status unavailable');
        clearCleanupPreview(instance);
        instance.view.querySelector('.cleanupError').textContent = '';
        instance.view.querySelector('.refreshStatus').textContent = 'Reading refresh status...';
        instance.view.querySelector('.refreshError').textContent = '';
        instance.view.querySelector('.catalogRefreshPrompt').hidden = true;
        instance.view.querySelector('.catalogRefreshPrompt').textContent = '';
        instance.view.querySelector('.stationDeletionNotice').hidden = true;
        instance.view.querySelector('.stationDeletionNotice').textContent = '';
        instance.view.querySelector('.channelScheduleNotice').hidden = true;
        instance.view.querySelector('.channelScheduleNotice').textContent = '';
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
            instance.savedFilteringConfig = JSON.parse(JSON.stringify(config));
            renderCatalogRefreshPrompt(instance);
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
            view.querySelector('.importDeadlineHours').value =
                config.ImportDeadlineHours == null ? 8 : config.ImportDeadlineHours;
            view.querySelector('.discoveryDeadlineHours').value =
                config.DiscoveryDeadlineHours == null ? 2 : config.DiscoveryDeadlineHours;
            view.querySelector('.maxStationCount').value =
                config.MaxStationCount === undefined ? 200 : config.MaxStationCount == null ? '' : config.MaxStationCount;
            view.querySelector('.traversalDepthLimit').value =
                config.TraversalDepthLimit == null ? 7 : config.TraversalDepthLimit;
            view.querySelector('.enableCatalogIdentityDiagnostics').checked = config.EnableCatalogIdentityDiagnostics === true;
            view.querySelector('.hideRegionalGenres').checked = config.HideRegionalGenres !== false;
            view.querySelector('.allowPartialCatalogImport').checked = config.AllowPartialCatalogImport !== false;
            var hasRegions = (config.LocalCities || []).some(function (item) {
                return item && item.Enabled !== false && /^r[1-9][0-9]{0,9}$/.test(item.Id);
            });
            var ambiguous = !config.LocalRadioSource && config.UseCoordinates && hasRegions;
            view.querySelector('.localSource').value = config.LocalRadioSource ||
                (ambiguous ? '' : config.UseCoordinates ? 'coordinates' : hasRegions ? 'regions' : '');
            view.querySelector('.configurationCompleted').checked = config.SchemaVersion === 1 ?
                !!config.ConfigurationCompleted : config.SchemaVersion === 0 || config.SchemaVersion == null;
            var saved = config.SettingsSaved === true || (config.SettingsSaved == null && config.SchemaVersion === 1);
            showSaveHistory(instance, saved, saved && !configurationRequired(config));
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
            selectors.forEach(function (spec) {
                pickerList.appendChild(createSelector(instance, spec, !saved));
            });
            $(view).trigger('create');
            message(instance, browsingStatus(config) + (ambiguous && config.EnableLocalRadio ?
                ' Local Radio is disabled until you choose one source and save.' : ''), configurationRequired(config));
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
        this.backupRequest = null;
        this.backupStarting = false;
        clearCleanupPreview(this);
        this.refreshRecovery = null;
        var dialog = this.view.querySelector('.tuneInRefreshRecoveryDialog');
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
