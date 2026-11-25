define(['baseView', 'loading', 'jQuery', 'emby-input', 'emby-button', 'emby-checkbox', 'emby-scroller', 'emby-select'], function (BaseView, loading, $) {
    'use strict';
    var pluginId = '10b82431-f939-4507-a8e2-9509dd3fd6ae';
    var categories = [
        ['EnableFavorites', 'My Favorites'],
        ['EnableLocalRadio', 'Local Radio'], ['EnableMusic', 'Music'],
        ['EnableLanguage', 'By Language'], ['EnableLocation', 'By Location'],
        ['EnableTalk', 'Talk'], ['EnableSports', 'Sports'],
        ['EnablePodcasts', 'Podcasts']
    ];
    var refreshCategoryOrder = ['presets', 'local', 'music', 'language', 'location', 'talk', 'sports', 'podcast'];
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
    var progressiveSettings = [
        ['ProgressiveFirstEntries', '.progressiveFirstEntries', 10000, 50, 'first pass entries'],
        ['ProgressiveMinimumNewEntries', '.progressiveMinimumNewEntries', 10000, 500, 'minimum new entries'],
        ['ProgressiveMinimumGrowthPercent', '.progressiveMinimumGrowthPercent', 100, 25, 'minimum growth percentage'],
        ['ProgressiveMinimumIntervalSeconds', '.progressiveMinimumIntervalSeconds', 3600, 45, 'minimum interval'],
        ['ProgressiveMaximumPasses', '.progressiveMaximumPasses', 100, 100, 'maximum passes']
    ];

    function showProgressivePacing(instance) {
        instance.view.querySelector('.progressivePacing').hidden =
            !instance.view.querySelector('.enableDiscoveryProgressiveImport').checked;
    }

    function filteringSnapshot(config) {
        return filteringSettings.map(function (key) { return config[key]; });
    }

    function normalizeLocalSource(source) {
        return typeof source === 'string' ? source.trim().toLowerCase() : '';
    }

    function selectorMode(instance, spec) {
        if (spec[0] === 'local') return 'selected';
        var mode = instance.config[spec[3]];
        if (typeof mode === 'string' && /^(all|selected)$/i.test(mode)) return mode.toLowerCase();
        if (spec[4] && mode) return 'selected';
        return spec[4] && (instance.config[spec[1]] || []).length ? 'selected' : 'all';
    }

    function feedbackMessage(element, text, error) {
        if (element.textContent !== text) element.textContent = text;
        element.setAttribute('data-error', String(!!error && !!text));
    }

    function diagnosticsRequest(route, data, params) {
        var options = { type: data ? 'POST' : 'GET', dataType: 'json',
            url: ApiClient.getUrl('TuneIn/Diagnostics' + route, params) };
        if (data) {
            options.contentType = 'application/json';
            options.data = JSON.stringify(data);
        }
        return ApiClient.ajax(options);
    }

    function diagnosticsCurrent(instance, state) {
        return instance.refreshVisible && instance.diagnostics === state && instance.viewEpoch === state.epoch;
    }

    function diagnosticsMessage(instance, text, error) {
        feedbackMessage(instance.view.querySelector('.diagnosticsMessage'), text, error);
    }

    function diagnosticsClearDraftLink(instance) {
        var link = instance.view.querySelector('.diagnosticsDraftLink');
        link.hidden = true;
        link.removeAttribute('href');
    }

    function diagnosticsResetReview(instance) {
        instance.view.querySelector('.diagnosticsReviewed').checked = false;
        diagnosticsClearDraftLink(instance);
        instance.diagnostics.reviewVersion++;
    }

    function diagnosticsControls(instance) {
        var state = instance.diagnostics, view = instance.view, pack = state.preview;
        var reporting = !!pack && !!pack.Repository && !pack.ReportingError && !!pack.DraftUrl &&
            pack.Repository === state.repository && !state.reportingError &&
            diagnosticsValidUrl(pack.DraftUrl, pack.Repository, pack);
        view.querySelector('.diagnosticsCapture').disabled = !!state.busy || !!state.storageError || !state.latest || !state.captureAvailable;
        view.querySelector('.diagnosticsPacks').disabled = !!state.busy || !state.packs.length;
        view.querySelector('.diagnosticsDownload').disabled = !!state.busy || !pack;
        view.querySelector('.diagnosticsDelete').disabled = !!state.busy || !pack;
        view.querySelector('.diagnosticsDeleteAll').disabled = !!state.busy || !state.hasStoredPacks;
        view.querySelector('.diagnosticsReviewed').disabled = !!state.busy || !reporting;
        view.querySelector('.diagnosticsOpen').disabled = !!state.busy || !reporting ||
            !view.querySelector('.diagnosticsReviewed').checked;
    }

    function diagnosticsClearPreview(instance) {
        var view = instance.view, state = instance.diagnostics;
        state.preview = null;
        state.previewVersion++;
        diagnosticsResetReview(instance);
        view.querySelector('.diagnosticsPreview').textContent = '';
        view.querySelector('.diagnosticsDraftTitle').textContent = '';
        view.querySelector('.diagnosticsDraftBody').textContent = '';
        view.querySelector('.diagnosticsPreviewDetails').hidden = true;
        view.querySelector('.diagnosticsDraftDetails').hidden = true;
        diagnosticsControls(instance);
    }

    function diagnosticsShowPreview(instance, pack) {
        var view = instance.view, state = instance.diagnostics;
        diagnosticsResetReview(instance);
        state.preview = pack;
        view.querySelector('.diagnosticsPreview').textContent = pack.PreviewText;
        view.querySelector('.diagnosticsDraftTitle').textContent = pack.DraftTitle || '';
        view.querySelector('.diagnosticsDraftBody').textContent = pack.DraftBody || '';
        view.querySelector('.diagnosticsPreviewDetails').hidden = false;
        view.querySelector('.diagnosticsDraftDetails').hidden = false;
        view.querySelector('.diagnosticsRepository').textContent = pack.Repository || state.repository || 'Unavailable';
        view.querySelector('.diagnosticsPackHint').textContent = pack.PublicNavigationIncluded
            ? 'This pack includes public navigation details.' : 'This pack excludes public navigation details.';
        if (pack.ReportingError || state.reportingError)
            diagnosticsMessage(instance, 'Reporting is unavailable for the configured repository. Capture and download remain available.', true);
        diagnosticsControls(instance);
    }

    function diagnosticsApplyList(instance, result) {
        var state = instance.diagnostics, view = instance.view, select = view.querySelector('.diagnosticsPacks');
        var oldRepository = state.repository;
        var oldStorageError = state.storageError;
        state.repository = result.Repository || null;
        state.reportingError = result.ReportingError || null;
        state.storageError = result.StorageError || null;
        state.latest = result.LatestRefresh || null;
        state.captureAvailable = result.CaptureAvailable === true;
        state.packs = Array.isArray(result.Packs) ? result.Packs : [];
        state.hasStoredPacks = result.HasStoredPacks === true || !!state.packs.length;
        if (oldRepository !== state.repository) diagnosticsResetReview(instance);
        var latest = state.latest;
        view.querySelector('.diagnosticsLatest').textContent = latest
            ? 'Latest refresh: ' + [latest.Category, latest.State, latest.StartedUtc].filter(Boolean).join(' - ') +
                (latest.Busy ? ' (active snapshot)' : '')
            : 'No refresh is available to capture.';
        var selected = state.preview ? state.preview.Id : select.value;
        var matching = state.packs.find(function (item) { return item.Id === selected; });
        if (state.preview && (!matching || matching.Revision !== state.preview.Revision ||
            state.preview.Repository !== state.repository)) {
            diagnosticsClearPreview(instance);
            selected = null;
        }
        if (!matching) selected = null;
        select.textContent = '';
        var placeholder = document.createElement('option');
        placeholder.value = '';
        placeholder.textContent = state.packs.length ? 'Select a saved pack' : 'No saved diagnostic packs';
        select.appendChild(placeholder);
        state.packs.forEach(function (item) {
            var option = document.createElement('option');
            option.value = item.Id;
            option.textContent = [item.CapturedUtc, item.Category, item.State, item.Bytes + ' bytes'].filter(Boolean).join(' - ');
            select.appendChild(option);
        });
        select.value = selected || '';
        view.querySelector('.diagnosticsRepository').textContent = state.repository || 'Unavailable';
        if (state.storageError)
            diagnosticsMessage(instance, state.storageError, true);
        else if (state.reportingError)
            diagnosticsMessage(instance, 'Reporting is unavailable for the configured repository. Capture and download remain available.', true);
        else if (oldStorageError) diagnosticsMessage(instance, '');
        diagnosticsControls(instance);
    }

    function diagnosticsLoad(instance) {
        var state = instance.diagnostics;
        if (!diagnosticsCurrent(instance, state)) return;
        if (state.listPending || state.busy) { state.listAgain = true; return; }
        state.listPending = true;
        var version = state.listVersion;
        return diagnosticsRequest('').then(function (result) {
            if (!diagnosticsCurrent(instance, state) || version !== state.listVersion) return;
            diagnosticsApplyList(instance, result);
        }).catch(function () {
            if (diagnosticsCurrent(instance, state) && version === state.listVersion)
                diagnosticsMessage(instance, 'Shareable diagnostics are unavailable. Check administrator access and try reopening the page.', true);
        }).finally(function () {
            if (!diagnosticsCurrent(instance, state)) return;
            state.listPending = false;
            if (state.listAgain) { state.listAgain = false; diagnosticsLoad(instance); }
        });
    }

    function diagnosticsSelect(instance) {
        var state = instance.diagnostics, id = instance.view.querySelector('.diagnosticsPacks').value;
        diagnosticsClearPreview(instance);
        var version = state.previewVersion;
        diagnosticsMessage(instance, '');
        if (!id) return;
        return diagnosticsRequest('/Pack', null, { Id: id }).then(function (pack) {
            if (!diagnosticsCurrent(instance, state) || version !== state.previewVersion ||
                instance.view.querySelector('.diagnosticsPacks').value !== id) return;
            var item = state.packs.find(function (entry) { return entry.Id === id; });
            if (!item || pack.Id !== id || pack.Revision !== item.Revision || pack.Repository !== state.repository) {
                diagnosticsMessage(instance, 'The pack or reporting destination changed. Refresh the list and review again.', true);
                diagnosticsLoad(instance);
                return;
            }
            diagnosticsShowPreview(instance, pack);
        }).catch(function () {
            if (diagnosticsCurrent(instance, state) && version === state.previewVersion)
                diagnosticsMessage(instance, 'Could not load the diagnostic pack. Select it again to retry.', true);
        });
    }

    function diagnosticsCapture(instance) {
        var state = instance.diagnostics;
        if (!diagnosticsCurrent(instance, state) || state.busy || state.storageError || !state.captureAvailable || !state.latest) return;
        state.busy = true;
        state.listVersion++;
        diagnosticsControls(instance);
        diagnosticsMessage(instance, 'Capturing a redacted snapshot...');
        var data = { RunId: state.latest.RunId, IncludePublicNavigation: instance.view.querySelector('.diagnosticsPublic').checked };
        return diagnosticsRequest('/Capture', data).then(function (pack) {
            if (!diagnosticsCurrent(instance, state)) return;
            state.packs = state.packs.filter(function (item) { return item.Id !== pack.Id; });
            state.packs.unshift(pack);
            state.hasStoredPacks = true;
            diagnosticsShowPreview(instance, pack);
            diagnosticsMessage(instance, pack.ReportingError || state.reportingError
                ? 'Redacted pack saved locally. Reporting is unavailable for the configured repository; download remains available.'
                : 'Redacted pack saved locally. Review it before sharing.', !!(pack.ReportingError || state.reportingError));
        }).catch(function () {
            if (diagnosticsCurrent(instance, state))
                diagnosticsMessage(instance, 'Could not capture the diagnostic pack. Check the latest refresh and server storage before trying again.', true);
        }).finally(function () {
            if (!diagnosticsCurrent(instance, state)) return;
            state.busy = false;
            diagnosticsControls(instance);
            diagnosticsLoad(instance);
        });
    }

    function diagnosticsValidUrl(url, repository, pack) {
        try {
            if (!/^[A-Za-z0-9_.-]+\/[A-Za-z0-9_.-]+$/.test(repository)) return false;
            var parsed = new URL(url);
            return parsed.protocol === 'https:' && parsed.hostname === 'github.com' && !parsed.port &&
                !parsed.username && !parsed.password && !parsed.hash &&
                parsed.pathname === '/' + repository + '/issues/new' &&
                Array.from(parsed.searchParams.keys()).length === 2 &&
                parsed.searchParams.get('title') === pack.DraftTitle &&
                parsed.searchParams.get('body') === pack.DraftBody;
        } catch (error) { return false; }
    }

    function diagnosticsDraft(instance) {
        var state = instance.diagnostics, view = instance.view, pack = state.preview;
        if (!diagnosticsCurrent(instance, state) || view.querySelector('.diagnosticsOpen').disabled ||
            !view.querySelector('.diagnosticsReviewed').checked || !pack) return;
        var review = state.reviewVersion, version = state.previewVersion;
        diagnosticsClearDraftLink(instance);
        state.busy = true;
        diagnosticsControls(instance);
        diagnosticsMessage(instance, 'Validating the reviewed pack and GitHub destination...');
        return diagnosticsRequest('/Draft', { Id: pack.Id, Revision: pack.Revision, Repository: pack.Repository })
            .then(function (result) {
                if (!diagnosticsCurrent(instance, state) || state.preview !== pack ||
                    state.reviewVersion !== review || state.previewVersion !== version ||
                    !view.querySelector('.diagnosticsReviewed').checked) return;
                if (result.Id !== pack.Id || result.Revision !== pack.Revision ||
                    result.Repository !== pack.Repository || result.Repository !== state.repository ||
                    result.Url !== pack.DraftUrl || !diagnosticsValidUrl(result.Url, result.Repository, pack)) {
                    diagnosticsResetReview(instance);
                    diagnosticsMessage(instance, 'The pack or reporting destination changed. Select the pack and review the draft again.', true);
                    diagnosticsLoad(instance);
                    return;
                }
                var link = view.querySelector('.diagnosticsDraftLink');
                link.href = result.Url;
                link.hidden = false;
                diagnosticsMessage(instance, 'Draft validated. If no tab opens, use the GitHub draft link below. Attach the downloaded JSON pack and submit it on GitHub; no issue has been created or pack uploaded by this page.');
                try {
                    // Opening a placeholder tab first can pause Emby and invalidate the pending validation.
                    window.open(result.Url, '_blank', 'noopener,noreferrer');
                } catch (error) {
                    diagnosticsMessage(instance, 'The browser could not open a new tab. Use the reviewed GitHub draft link below, then attach the downloaded JSON pack and submit the issue yourself.', true);
                }
            }).catch(function () {
                if (diagnosticsCurrent(instance, state)) {
                    diagnosticsResetReview(instance);
                    diagnosticsMessage(instance, 'Could not open the draft. The pack or repository may have changed; select and review it again.', true);
                }
            }).finally(function () {
                if (!diagnosticsCurrent(instance, state)) return;
                state.busy = false;
                diagnosticsControls(instance);
                if (state.listAgain) { state.listAgain = false; diagnosticsLoad(instance); }
            });
    }

    function diagnosticsDelete(instance, all) {
        var state = instance.diagnostics, view = instance.view, pack = state.preview;
        if (state.busy || (!all && !pack) || (all && !state.hasStoredPacks)) return;
        state.deleteTarget = all ? { All: true } : { Id: pack.Id, Revision: pack.Revision };
        state.deleteFocus = view.querySelector(all ? '.diagnosticsDeleteAll' : '.diagnosticsDelete');
        view.querySelector('.diagnosticsDeleteTitle').textContent = all ? 'Delete all saved diagnostic packs?' : 'Delete saved diagnostic pack?';
        view.querySelector('.diagnosticsDeleteConfirm').textContent = all ? 'Delete all packs' : 'Delete pack';
        view.querySelector('.diagnosticsDeleteDialog').showModal();
        view.querySelector('.diagnosticsDeleteCancel').focus();
    }

    function diagnosticsConfirmDelete(instance) {
        var state = instance.diagnostics, view = instance.view, target = state.deleteTarget;
        if (!diagnosticsCurrent(instance, state) || state.busy || !target) return;
        state.deleteTarget = null;
        view.querySelector('.diagnosticsDeleteDialog').close();
        state.busy = true;
        state.listVersion++;
        diagnosticsControls(instance);
        return diagnosticsRequest('/Delete', target).then(function (result) {
            if (!diagnosticsCurrent(instance, state)) return;
            diagnosticsApplyList(instance, result);
            if (!state.storageError && !state.reportingError)
                diagnosticsMessage(instance, 'Local diagnostic pack deletion completed.');
            view.querySelector('.diagnosticsCapture').focus();
        }).catch(function () {
            if (diagnosticsCurrent(instance, state))
                diagnosticsMessage(instance, 'Could not delete the diagnostic pack. Check the saved packs before trying again.', true);
        }).finally(function () {
            if (!diagnosticsCurrent(instance, state)) return;
            state.busy = false;
            diagnosticsControls(instance);
            if (state.listAgain) { state.listAgain = false; diagnosticsLoad(instance); }
        });
    }

    function message(instance, text, configurationRequired, isError) {
        var status = instance.view.querySelector('.settingsMessage');
        feedbackMessage(status, text, isError !== false);
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
            setupChanged(instance);
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
            (spec[0] === 'local' ? [['selected', 'Selected only']]
                : [['all', 'All'], ['selected', 'Selected only']]).forEach(function (choice) {
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
                setupChanged(instance);
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
                            feedbackMessage(feedback, 'Remove a saved choice before adding another (limit: 100).', true);
                            return;
                        }
                        list.push({ Id: item.Id, Name: item.Name, Path: item.Path,
                            AncestorIds: item.AncestorIds, Enabled: true });
                    }
                    instance.config[spec[1]] = list;
                } else if (!list.includes(item.Id)) {
                    if (list.length >= 100) {
                        feedbackMessage(feedback, 'Remove a saved choice before adding another (limit: 100).', true);
                        return;
                    }
                    instance.config[spec[1]] = list.concat(item.Id);
                }
                renderSaved();
                setupChanged(instance);
                feedbackMessage(feedback, 'Selected: ' + (item.Path || item.Name || item.Id));
                return;
            }
            var existing = list.find(function (entry) { return entry.Id === item.Id; });
            if (existing) existing.Enabled = true;
            else {
                if (list.length >= 100) {
                    feedbackMessage(feedback, 'Remove a saved choice before adding another (limit: 100).', true);
                    return;
                }
                list.push({ Id: item.Id, Name: item.Name, Path: item.Path, AncestorIds: item.AncestorIds, Enabled: true });
                instance.config[spec[1]] = list;
            }
            renderSaved();
            setupChanged(instance);
            feedbackMessage(feedback, 'Selected: ' + item.Path);
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
                feedbackMessage(status, 'Loading choices...');
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
                    feedbackMessage(status, 'Still loading choices. Trying again...');
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
                    feedbackMessage(status, data.Warning || (data.Items.length ? '' : 'No further choices in this branch.'));
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
                    feedbackMessage(status, 'Could not load choices from TuneIn' +
                        (code ? ' (HTTP ' + code + ')' : '') + '. Retry this branch.', true);
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
        var validation = document.createElement('span');
        validation.className = 'fieldDescription tuneInValidationError';
        validation.setAttribute('role', 'status');
        validation.hidden = true;
        heading.appendChild(validation);
        instance.selectorErrors.set(spec[0], validation);
        renderSaved();
        return section;
    }

    function editingBlocked(instance) {
        return !instance.config || instance.openingLock || instance.openLockFailed || instance.settingsLocked !== false || instance.lockChanging || instance.saving || instance.optionalSaving ||
            !instance.refreshAvailable || instance.refreshBusy || instance.refreshAction || maintenanceInProgress(instance);
    }

    function maintenanceInProgress(instance) {
        return instance.maintenanceBusy || instance.cleanupBusy || instance.cleanupAction ||
            backupInProgress(instance) || instance.artworkInstalling;
    }

    function browsingSelected(instance) {
        return instance.view.querySelector('.configurationCompleted').checked;
    }

    function setupErrors(instance) {
        var errors = {};
        if (!instance.config || !browsingSelected(instance)) return errors;
        var enabled = function (key) {
            var control = instance.view.querySelector('[data-category="' + key + '"]');
            return !!control && control.checked;
        };
        selectors.forEach(function (spec) {
            var key = { language: 'EnableLanguage', location: 'EnableLocation', music: 'EnableMusic',
                talk: 'EnableTalk', sports: 'EnableSports', podcast: 'EnablePodcasts', local: 'EnableLocalRadio' }[spec[0]];
            if (!enabled(key) || selectorMode(instance, spec) !== 'selected') return;
            if (spec[0] === 'local' && instance.view.querySelector('.localSource').value !== 'regions') return;
            var pattern = spec[0] === 'language' ? /^l[1-9][0-9]{0,9}$/
                : spec[0] === 'local' || spec[0] === 'location' ? /^r[1-9][0-9]{0,9}$/
                : /^[cg][1-9][0-9]{0,9}$/;
            var valid = (instance.config[spec[1]] || []).some(function (choice) {
                return spec[4] ? choice && choice.Enabled !== false && pattern.test(choice.Id)
                    : pattern.test(choice);
            });
            if (!valid) errors[spec[0]] = spec[0] === 'local'
                ? 'Add at least one enabled saved city or region for Local Radio, or choose coordinates.'
                : 'Add a valid ' + spec[2] + ' choice or choose All.';
        });
        if (enabled('EnableLocalRadio')) {
            var source = instance.view.querySelector('.localSource').value;
            var parts = instance.view.querySelector('.txtLatLon').value.trim().split(',');
            var validCoordinates = parts.length === 2 && parts.every(function (part) {
                return /^[+-]?(?:\d+(?:\.\d*)?|\.\d+)(?:e[+-]?\d+)?$/i.test(part.trim()) && isFinite(Number(part));
            }) && Math.abs(Number(parts[0])) <= 90 && Math.abs(Number(parts[1])) <= 180;
            if (source !== 'regions' && source !== 'coordinates')
                errors.local = 'Choose coordinates or regions for Local Radio.';
            else if (source === 'coordinates' && !validCoordinates)
                errors.local = 'Enter finite latitude (-90..90), longitude (-180..180), separated by a comma.';
        }
        if (enabled('EnableFavorites') && !instance.view.querySelector('.txtUsername').value.trim())
            errors.presets = 'Enter a TuneIn username for My Favorites.';
        return errors;
    }

    function renderReadiness(instance) {
        if (!instance.config || !instance.setupControlsReady) return;
        var view = instance.view;
        var status = instance.refreshStatus;
        var errors = setupErrors(instance);
        var enabledCount = categories.filter(function (spec) {
            return view.querySelector('[data-category="' + spec[0] + '"]').checked;
        }).length;
        var filtering = browsingSelected(instance);
        var eligible = filtering && enabledCount > Object.keys(errors).length;
        var requirements = filtering && enabledCount > 0 && !Object.keys(errors).length;
        var saved = instance.setupSaved && !instance.setupDirty &&
            instance.savedFilteringConfig && instance.savedFilteringConfig.SchemaVersion === 1;
        var readyItems = [];
        function item(selector, ready, text) {
            var element = view.querySelector(selector);
            var message = (ready ? 'Ready: ' : 'Needed: ') + text;
            if (element.textContent !== message) element.textContent = message;
            element.setAttribute('data-ready', String(!!ready));
            view.querySelector(selector + 'Check').checked = !!ready;
            readyItems.push(!!ready);
        }
        item('.readinessFiltering', filtering, filtering ? 'Station filtering enabled' : 'Enable TuneIn Station Filtering');
        item('.readinessCategory', eligible, eligible ? 'An eligible category is enabled' : 'Enable an eligible audio category');
        item('.readinessRequirements', requirements, requirements ? 'Category requirements configured' : 'Complete the category requirements below');
        item('.readinessSaved', saved, saved ? 'Settings saved' : 'Save your settings');
        var channel = status && status.ChannelRegistered;
        item('.readinessChannel', channel === true, channel === true ? 'TuneIn channel registered'
            : channel === false ? 'TuneIn channel not registered yet; restart Emby and check plugin registration'
            : 'Waiting to check TuneIn channel registration');
        var baseline = status && status.HasCompleteBaseline === true;
        item('.readinessBaseline', baseline, baseline ? 'First catalog refresh completed'
            : status ? 'Complete the first catalog refresh' : 'Waiting to check the first catalog refresh');
        var setupComplete = instance.setupSaved && instance.savedFilteringConfig &&
            instance.savedFilteringConfig.SchemaVersion === 1 && baseline && channel === true;
        view.querySelector('.tuneInReadiness').hidden = setupComplete || readyItems.every(function (ready) { return ready; });
        view.querySelector('.settingsChangeNotice').hidden = !setupComplete || !instance.setupDirty;
        instance.selectorErrors.forEach(function (element, key) {
            element.textContent = errors[key] ? ': ' + errors[key] : '';
            element.hidden = !errors[key];
        });
        [['.localValidation', 'local'], ['.favoritesValidation', 'presets']].forEach(function (entry) {
            var element = view.querySelector(entry[0]);
            element.textContent = errors[entry[1]] || '';
            element.hidden = !errors[entry[1]];
        });
    }

    function setupChanged(instance) {
        instance.setupDirty = true;
        renderReadiness(instance);
    }

    function expandFilteringGroups(instance) {
        ['.categoriesSummary', '.stationSelectionsSummary'].forEach(function (selector) {
            instance.view.querySelector(selector).parentNode.open = true;
        });
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
        if (browsing.open) expandFilteringGroups(instance);
        if (saved) {
            view.querySelector('.selectors').childNodes.forEach(function (section) { section.open = false; });
        }
    }

    function editing(instance) {
        var disabled = !!editingBlocked(instance);
        var catalogDisabled = disabled || !browsingSelected(instance);
        instance.view.querySelector('.loginFields').disabled = catalogDisabled;
        instance.view.querySelector('.configurationFields').disabled = catalogDisabled;
        instance.view.querySelectorAll('.globalFields').forEach(function (fields) { fields.disabled = disabled; });
        instance.view.querySelector('.artworkUpload').disabled = disabled || !!instance.artworkBusy;
        instance.view.querySelector('.artworkFile').disabled = disabled || !!instance.artworkBusy;
        instance.view.querySelector('.hideRegionalGenres').disabled = catalogDisabled;
        instance.view.querySelector('.allowPartialCatalogImport').disabled = catalogDisabled;
        instance.view.querySelector('.apiRequestIntervalSeconds').disabled = catalogDisabled;
        instance.view.querySelector('.maximumRateLimitWaitMinutes').disabled = catalogDisabled;
        instance.view.querySelector('.maximumConsecutiveRateLimitRetries').disabled = catalogDisabled;
        instance.view.querySelector('.importDeadlineHours').disabled = catalogDisabled;
        instance.view.querySelector('.discoveryDeadlineHours').disabled = catalogDisabled;
        instance.view.querySelector('.maxStationCount').disabled = catalogDisabled;
        instance.view.querySelector('.traversalDepthLimit').disabled = catalogDisabled;
        instance.view.querySelector('.enableCatalogIdentityDiagnostics').disabled = catalogDisabled;
        instance.view.querySelector('.allowDirectHlsPlayback').disabled = catalogDisabled;
        instance.view.querySelector('.enableDiscoveryProgressiveImport').disabled = catalogDisabled;
        progressiveSettings.forEach(function (setting) {
            instance.view.querySelector(setting[1]).disabled = catalogDisabled
                || !instance.view.querySelector('.enableDiscoveryProgressiveImport').checked;
        });
        instance.view.querySelector('.tuneInSettingsSave').disabled = disabled;
        instance.refreshCategoryRows.forEach(function (row) { row.schedule.disabled = catalogDisabled; });
        var toggle = instance.view.querySelector('.settingsLocked');
        var active = instance.refreshBusy || instance.refreshAction || maintenanceInProgress(instance);
        toggle.disabled = !!instance.openingLock || !!instance.openLockFailed || typeof instance.settingsLocked !== 'boolean' || !!instance.lockChanging || !!instance.saving || !!instance.optionalSaving ||
            !instance.refreshAvailable || !!active;
        var confirmed = !instance.refreshAvailable || instance.openingLock || instance.openLockFailed ? undefined : active ? true : instance.settingsLocked;
        toggle.checked = instance.lockChanging ? instance.lockRequested : confirmed === true;
        var state = instance.view.querySelector('.settingsLockState');
        state.textContent = confirmed === true ? 'Locked' : confirmed === false ? 'Unlocked' : 'Checking';
        state.setAttribute('data-lock-state', confirmed === true ? 'locked' : confirmed === false ? 'unlocked' : 'unknown');
        var note = instance.view.querySelector('.settingsLockStatus');
        var text = instance.lockError || (instance.saving ? 'Saving and locking settings...' :
            instance.lockChanging ? 'Updating settings lock...' :
            maintenanceInProgress(instance) ? 'Settings cannot be changed while maintenance is active.' :
            instance.refreshBusy || instance.refreshAction ? 'Settings cannot be changed while catalog refresh is active.' :
            !instance.refreshAvailable ? 'Settings are unavailable until refresh status is available.' :
            typeof confirmed === 'boolean' ? '' : 'Reading settings lock...');
        feedbackMessage(note, text, !!instance.lockError);
        renderOptionalChecks(instance, instance.refreshStatus);
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
        var write = writeSettingsLock(instance.lockRequested).then(function (locked) {
            if (epoch === instance.viewEpoch) {
                instance.settingsLocked = locked;
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
        instance.lockWriteQueue = write;
        return write;
    }

    function isChannelRefreshTask(task) {
        return !!task && (task.Key === 'RefreshInternetChannels' || task.Name === 'Refresh Internet Channels');
    }

    function readChannelRefreshScheduled() {
        return ApiClient.ajax({ type: 'GET', dataType: 'json', url: ApiClient.getUrl('ScheduledTasks') })
            .then(function (tasks) {
                return (tasks || []).filter(isChannelRefreshTask)
                    .some(function (task) { return (task.Triggers || []).length > 0; });
            });
    }

    function closeChannelScheduleDialog(instance) {
        instance.channelSchedulePending = null;
        var dialog = instance.view.querySelector('.channelScheduleDialog');
        if (dialog.open) dialog.close();
    }

    function confirmChannelSchedule(instance, name, category, schedule, previous) {
        var epoch = instance.viewEpoch;
        var requested = schedule.value;
        var label = (Array.prototype.find.call(schedule.childNodes || schedule.options || [], function (option) {
            return option.value === requested;
        }) || {}).textContent || '';
        return readChannelRefreshScheduled().then(function (scheduled) {
            if (!scheduled || epoch !== instance.viewEpoch || schedule.value !== requested) return;
            var dialog = instance.view.querySelector('.channelScheduleDialog');
            instance.channelSchedulePending = { category: category, schedule: schedule, previous: previous };
            instance.view.querySelector('.channelScheduleText').textContent = name + ' will refresh ' +
                label.toLowerCase() + ' after you save. Emby\'s "Refresh Internet Channels" scheduled task also has a schedule and will keep running; each time it runs it refreshes all TuneIn categories, not only the ones scheduled here. ' +
                'Select Keep Both Schedules if that is okay, or Adjust Schedule to change this setting (or change the task\'s triggers in Advanced > Scheduled Tasks). ' +
                'TuneIn refresh schedules do not refresh any other Internet Channel in Emby, so both schedules may be needed.';
            if (!dialog.open) dialog.showModal();
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

    function lockSettingsOnOpen(instance, epoch) {
        instance.openingLock = true;
        instance.openLockFailed = false;
        var previous = Promise.all([instance.openLockQueue || Promise.resolve(), instance.lockWriteQueue || Promise.resolve()]);
        var request = previous.then(function () {
            if (epoch !== instance.viewEpoch) return;
            return ApiClient.ajax({
                type: 'POST', dataType: 'json', url: ApiClient.getUrl('TuneIn/SettingsLock/OnOpen')
            }).then(function (result) {
                if (!result || typeof result.Locked !== 'boolean') throw new Error('Invalid settings lock response.');
                if (epoch !== instance.viewEpoch) return;
                instance.settingsLocked = result.Locked;
                instance.openingLock = false;
                editing(instance);
            });
        }).catch(function (error) {
            if (epoch !== instance.viewEpoch) return;
            console.error('Unable to lock TuneIn settings on opening.', error);
            instance.openingLock = false;
            instance.openLockFailed = true;
            instance.settingsLocked = undefined;
            instance.lockError = 'Could not confirm the settings lock. Reopen this page to retry.';
            editing(instance);
        });
        instance.openLockQueue = request;
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

    function renderOptionalChecks(instance, status) {
        var view = instance.view;
        var selected = status && status.SkipOptionalStartupChecks;
        var eligible = !!status && status.OptionalChecksEligible === true;
        var toggle = view.querySelector('.skipOptionalChecks');
        toggle.checked = selected === true;
        var blocked = !!editingBlocked(instance);
        toggle.disabled = blocked || (!eligible && selected !== true);
        view.querySelector('.optionalChecksUnlockHint').hidden = !eligible || !blocked;
    }

    function saveOptionalChecks(instance, skip) {
        if (editingBlocked(instance) || !instance.refreshStatus) return;
        var epoch = instance.viewEpoch;
        instance.optionalSaving = true;
        instance.refreshGeneration = (instance.refreshGeneration || 0) + 1;
        editing(instance);
        feedbackMessage(instance.view.querySelector('.optionalChecksMessage'), 'Saving artwork check preference...');
        return ApiClient.ajax({
            type: 'POST', dataType: 'json', contentType: 'application/json',
            url: ApiClient.getUrl('TuneIn/OptionalChecks/Preference'), data: JSON.stringify({ Skip: skip })
        }).then(function (result) {
            if (epoch !== instance.viewEpoch) return;
            instance.refreshStatus.SkipOptionalStartupChecks = result.SkipOptionalStartupChecks;
            feedbackMessage(instance.view.querySelector('.optionalChecksMessage'), skip
                ? 'Automatic artwork checks will be skipped.' : 'Automatic artwork checks are enabled.');
        }, function () {
            if (epoch === instance.viewEpoch)
                feedbackMessage(instance.view.querySelector('.optionalChecksMessage'),
                    'Could not confirm the artwork check preference. Checking the server again.', true);
        }).then(function () {
            instance.optionalSaving = false;
            if (epoch !== instance.viewEpoch) return;
            refreshControls(instance, false);
            pollStatus(instance, epoch);
        });
    }

    function runOptionalChecks(instance) {
        if (instance.optionalChecking) return;
        var epoch = instance.viewEpoch;
        instance.optionalChecking = true;
        instance.view.querySelector('.optionalChecksRun').disabled = true;
        feedbackMessage(instance.view.querySelector('.optionalChecksStatus'), 'Checking artwork packs...');
        instance.refreshGeneration = (instance.refreshGeneration || 0) + 1;
        var generation = instance.refreshGeneration;
        return ApiClient.ajax({
            type: 'GET', dataType: 'json', url: ApiClient.getUrl('TuneIn/Refresh/Status', { CheckOptional: true })
        }).then(function (status) {
            if (epoch === instance.viewEpoch && generation === instance.refreshGeneration) renderArtworkPacks(instance, status);
        }, function () {
            if (epoch === instance.viewEpoch && generation === instance.refreshGeneration) {
                renderArtworkPacks(instance, null);
                feedbackMessage(instance.view.querySelector('.optionalChecksStatus'), 'Artwork checks failed; status is unavailable. Try again.', true);
            }
        }).then(function () {
            instance.optionalChecking = false;
            instance.view.querySelector('.optionalChecksRun').disabled = false;
        });
    }

    function renderArtworkPacks(instance, status) {
        var packs = status && (status.artworkPacks || status.ArtworkPacks);
        if (!Array.isArray(packs)) packs = [];
        var dates = packs.map(function (pack) { return pack && (pack.CheckedUtc || pack.checkedUtc); })
            .filter(function (value) { return value && !isNaN(Date.parse(value)); });
        var deferred = !!status && status.ArtworkChecksDeferred === true;
        feedbackMessage(instance.view.querySelector('.optionalChecksStatus'), !status ? 'Artwork status unavailable.'
            : dates.length ? (deferred ? 'Automatic artwork checks deferred. ' : '')
                + 'Last checked: ' + new Date(Math.min.apply(null, dates.map(Date.parse))).toLocaleString()
                + '. Results may have changed since then.'
            : deferred ? 'Artwork checks deferred; run checks now to see pack status.'
            : 'Artwork check time unavailable.');
        function field(pack, name) {
            return pack && (pack[name] != null ? pack[name] : pack[name.charAt(0).toUpperCase() + name.slice(1)]);
        }
        function count(pack, name) {
            var value = field(pack, name);
            return typeof value === 'number' && isFinite(value) && value >= 0 && Math.floor(value) === value
                ? value : null;
        }
        [
            { selector: 'Genres', scopes: ['genres', 'genre', 'musicgenres', 'musicgenre'] },
            { selector: 'Languages', scopes: ['languages', 'language'] },
            { selector: 'Locations', scopes: ['locations', 'location'] },
            { selector: 'Categories', scopes: ['categories', 'category'] }
        ].forEach(function (scope) {
            var pack = packs.find(function (item) {
                var name = field(item, 'scope');
                return typeof name === 'string' && scope.scopes.includes(name.toLowerCase().replace(/[^a-z]/g, ''));
            });
            var state = field(pack, 'status');
            var installed = count(pack, 'installedCount');
            var matched = count(pack, 'matchedCount');
            var modified = state === 'identified' ? count(pack, 'modifiedCount') : null;
            var added = state === 'identified' ? count(pack, 'addedCount') : null;
            var name = field(pack, 'name');
            var path = field(pack, 'path');
            instance.view.querySelector('.artwork' + scope.selector + 'Name').textContent =
                state === 'identified' && typeof name === 'string' && name.trim() ? name
                : state === 'identified' ? 'Pack name unavailable'
                : state === 'missing' && installed === 0 ? 'Default artwork'
                : state === 'missing' ? 'No pack metadata'
                : state === 'invalid' ? 'Invalid pack metadata'
                : state === 'unsupported' ? 'Legacy or unsupported pack: install a schema 2 pack; existing files are unchanged'
                : state === 'deferred' ? 'Check deferred'
                : 'Status unavailable';
            var images = [];
            if (installed !== null) images.push(installed + ' installed');
            if (matched !== null) images.push(matched + ' matched');
            instance.view.querySelector('.artwork' + scope.selector + 'Images').textContent =
                images.length ? images.join(', ') : 'Unavailable';
            instance.view.querySelector('.artwork' + scope.selector + 'Path').textContent =
                typeof path === 'string' && path.trim() ? path : 'Folder unavailable';
            var changes = [];
            if (modified !== null) changes.push(modified + ' modified');
            if (added !== null) changes.push(added + ' added');
            instance.view.querySelector('.artwork' + scope.selector + 'Changes').textContent =
                changes.length ? '(' + changes.join(', ') + ')' : '(change counts unavailable)';
        });
    }

    function artworkError(error) {
        return error && error.responseJSON && error.responseJSON.ResponseStatus &&
            error.responseJSON.ResponseStatus.Message || error && error.message || 'Check the server logs and try again.';
    }

    function pollArtwork(instance, epoch) {
        if (!instance.artworkBusy || instance.artworkOperation || epoch !== instance.viewEpoch) return;
        ApiClient.ajax({ type: 'GET', dataType: 'json', url: ApiClient.getUrl('TuneIn/Artwork/Status') })
            .then(function (status) {
                if (!instance.artworkBusy || instance.artworkOperation || epoch !== instance.viewEpoch) return;
                var state = status.State || status.state;
                var labels = { uploading: 'Uploading artwork pack...', validating: 'Validating artwork pack...',
                    scanning: 'Scanning staged artwork...', installing: 'Installing artwork pack...' };
                if (labels[state]) feedbackMessage(instance.view.querySelector('.artworkUploadMessage'), labels[state]);
            }, function () {
                if (instance.artworkBusy && !instance.artworkOperation && epoch === instance.viewEpoch)
                    feedbackMessage(instance.view.querySelector('.artworkUploadMessage'), 'Waiting for artwork status...');
            }).then(function () {
                if (instance.artworkBusy && !instance.artworkOperation && epoch === instance.viewEpoch)
                    instance.artworkTimer = setTimeout(function () { pollArtwork(instance, epoch); }, 1500);
            });
    }

    function awaitArtworkResult(instance, operation, epoch) {
        return ApiClient.ajax({ type: 'GET', dataType: 'json',
            url: ApiClient.getUrl('TuneIn/Artwork/Status', { Operation: operation, t: Date.now() }) }).then(function (status) {
            var state = status.State || status.state;
            var result = status.Result || status.result;
            if (state === 'error') throw new Error(status.Message || status.message || 'Artwork validation failed.');
            if (state === 'awaiting confirmation' && result) return result;
            if (state !== 'uploading' && state !== 'validating' && state !== 'scanning' && state !== 'awaiting confirmation')
                throw new Error('Artwork processing status is unavailable. Upload the pack again.');
            if (epoch === instance.viewEpoch) {
                var labels = { uploading: 'Uploading artwork pack...', validating: 'Validating artwork pack...',
                    scanning: 'Scanning staged artwork...', 'awaiting confirmation': 'Finishing artwork verification...' };
                feedbackMessage(instance.view.querySelector('.artworkUploadMessage'), labels[state]);
            }
            return new Promise(function (resolve) { setTimeout(resolve, 1500); })
                .then(function () { return awaitArtworkResult(instance, operation, epoch); });
        });
    }

    function uploadArtwork(instance, file) {
        if (!file || instance.artworkBusy) return;
        if (editingBlocked(instance)) {
            feedbackMessage(instance.view.querySelector('.artworkUploadMessage'), 'Unlock settings and wait for active work before verifying artwork.', true);
            return;
        }
        var epoch = instance.viewEpoch;
        if (file.size > 768 * 1024 * 1024) {
            feedbackMessage(instance.view.querySelector('.artworkUploadMessage'), 'The ZIP exceeds the 768 MiB upload limit.', true);
            return;
        }
        instance.artworkBusy = true;
        instance.view.querySelector('.artworkUpload').disabled = true;
        feedbackMessage(instance.view.querySelector('.artworkUploadMessage'), 'Uploading artwork pack...');
        pollArtwork(instance, epoch);
        // Emby's ajax helper form-encodes non-string data, including File objects.
        var request = { url: ApiClient.getUrl('TuneIn/Artwork/Upload'), headers: { 'Content-Type': 'application/zip', accept: 'application/json' } };
        ApiClient.setAuthorizationInfoIntoRequest(request);
        return fetch(request.url, { method: 'POST', headers: request.headers, body: file, credentials: 'same-origin' })
            .then(function (response) {
                if (response.ok) return response.json();
                return response.text().then(function (body) {
                    var error = new Error(body.trim().slice(0, 500) || 'Artwork upload returned HTTP ' + response.status);
                    try { error.responseJSON = JSON.parse(body); } catch (parseError) { /* Emby can return plain text. */ }
                    throw error;
                });
            }).then(function (result) {
            if (!result.Operation) return result;
            instance.artworkOperation = result.Operation;
            clearTimeout(instance.artworkTimer);
            return awaitArtworkResult(instance, result.Operation, epoch);
        }).then(function (result) {
            if (epoch !== instance.viewEpoch) {
                return ApiClient.ajax({ type: 'POST', dataType: 'json', contentType: 'application/json',
                    url: ApiClient.getUrl('TuneIn/Artwork/Discard'), data: JSON.stringify({ Token: result.Token }) })
                    .catch(function (error) { console.error('Could not discard staged artwork; it will expire.', error); });
            }
            instance.artworkPending = result;
            if (result.ScanStatus !== 'Clean' && result.ScanStatus !== 'Not scanned') {
                discardArtwork(instance).then(function () {
                    if (epoch === instance.viewEpoch)
                        feedbackMessage(instance.view.querySelector('.artworkUploadMessage'), 'Artwork scan status was invalid. Upload the pack again.', true);
                });
                return;
            }
            var unscanned = result.ScanStatus === 'Not scanned';
            instance.view.querySelector('.artworkConfirmText').textContent =
                result.Name + ' — ' + result.Scope + ', ' + result.ImageCount + ' images. ' + result.ScanStatus +
                '. Installing replaces the existing pack in ' + result.Destination + '.';
            instance.view.querySelector('.artworkConfirmError').textContent = '';
            instance.view.querySelector('.artworkUnscannedLabel').hidden = !unscanned;
            instance.view.querySelector('.artworkApproveUnscanned').checked = false;
            instance.view.querySelector('.artworkInstall').disabled = unscanned;
            instance.view.querySelector('.artworkConfirmDialog').showModal();
            feedbackMessage(instance.view.querySelector('.artworkUploadMessage'), 'Validation complete. Confirm installation.');
        }).catch(function (error) {
            if (epoch === instance.viewEpoch)
                feedbackMessage(instance.view.querySelector('.artworkUploadMessage'), 'Artwork upload failed: ' + artworkError(error), true);
        }).then(function () {
            instance.artworkBusy = false;
            instance.artworkOperation = null;
            clearTimeout(instance.artworkTimer);
            if (instance.refreshVisible) {
                instance.view.querySelector('.artworkUpload').disabled = !!editingBlocked(instance);
                instance.view.querySelector('.artworkFile').disabled = !!editingBlocked(instance);
                if (epoch !== instance.viewEpoch)
                    feedbackMessage(instance.view.querySelector('.artworkUploadMessage'), 'Artwork operation finished while the page was away. Check the pack status.');
            }
        });
    }

    function discardArtwork(instance) {
        var epoch = instance.viewEpoch;
        var pending = instance.artworkPending;
        instance.artworkPending = null;
        var dialog = instance.view.querySelector('.artworkConfirmDialog');
        if (dialog.open) dialog.close();
        if (!pending) return;
        return ApiClient.ajax({ type: 'POST', dataType: 'json', contentType: 'application/json',
            url: ApiClient.getUrl('TuneIn/Artwork/Discard'), data: JSON.stringify({ Token: pending.Token }) })
            .then(function () {
                if (epoch === instance.viewEpoch) feedbackMessage(instance.view.querySelector('.artworkUploadMessage'), 'Artwork installation cancelled.');
            }, function () {
                if (epoch === instance.viewEpoch) feedbackMessage(instance.view.querySelector('.artworkUploadMessage'), 'Could not discard staged artwork; it will expire.', true);
            });
    }

    function installArtwork(instance) {
        var pending = instance.artworkPending;
        if (!pending || instance.artworkBusy) return;
        if (editingBlocked(instance)) {
            instance.view.querySelector('.artworkConfirmError').textContent = 'Unlock settings and wait for active work before installing artwork.';
            return;
        }
        var unscanned = pending.ScanStatus === 'Not scanned';
        if (unscanned && !instance.view.querySelector('.artworkApproveUnscanned').checked) return;
        var epoch = instance.viewEpoch;
        instance.artworkPending = null;
        instance.view.querySelector('.artworkConfirmDialog').close();
        instance.artworkBusy = true;
        instance.artworkInstalling = true;
        instance.refreshGeneration = (instance.refreshGeneration || 0) + 1;
        refreshControls(instance, instance.refreshAvailable);
        var statusConfirmed = false;
        instance.view.querySelector('.artworkUpload').disabled = true;
        feedbackMessage(instance.view.querySelector('.artworkUploadMessage'), 'Installing artwork pack...');
        return ApiClient.ajax({ type: 'POST', dataType: 'json', contentType: 'application/json',
            url: ApiClient.getUrl('TuneIn/Artwork/Install'),
            data: JSON.stringify({ Token: pending.Token, ApproveUnscanned: unscanned }) }).then(function (result) {
            if (epoch !== instance.viewEpoch) return;
            instance.artworkInstalled = result;
            feedbackMessage(instance.view.querySelector('.artworkUploadMessage'), result.CleanupPending
                ? 'Pack installed successfully; server cleanup is pending.' : 'Pack installed successfully.');
            return ApiClient.ajax({ type: 'GET', dataType: 'json', url: ApiClient.getUrl('TuneIn/Refresh/Status') })
                .then(function (status) {
                    if (epoch !== instance.viewEpoch) return;
                    renderRefresh(instance, status);
                    statusConfirmed = true;
                    var affected = (status.Categories || []).filter(function (category) {
                        return (result.AffectedCategories || []).includes(category.Category);
                    });
                    instance.artworkAffected = affected.map(function (category) { return category.Category; });
                    feedbackMessage(instance.view.querySelector('.artworkRefreshText'), affected.length
                        ? 'Affected enabled categories: ' + affected.map(function (category) { return category.Name; }).join(', ') + '.'
                        : 'No enabled categories are affected.');
                }, function () {
                    if (epoch !== instance.viewEpoch) return;
                    instance.artworkAffected = null;
                    feedbackMessage(instance.view.querySelector('.artworkRefreshText'), 'Could not load affected categories. Refresh status must be available before starting.', true);
                }).then(function () {
                    if (epoch === instance.viewEpoch) instance.view.querySelector('.artworkRefreshDialog').showModal();
                });
        }, function (error) {
            if (epoch === instance.viewEpoch)
                feedbackMessage(instance.view.querySelector('.artworkUploadMessage'), 'Could not confirm artwork installation: ' + artworkError(error), true);
        }).then(function () {
            instance.artworkBusy = false;
            instance.artworkInstalling = false;
            if (instance.refreshVisible) {
                if (epoch !== instance.viewEpoch)
                    feedbackMessage(instance.view.querySelector('.artworkUploadMessage'), 'Artwork operation finished while the page was away. Check the pack status.');
                refreshControls(instance, statusConfirmed && epoch === instance.viewEpoch);
                if (!statusConfirmed || epoch !== instance.viewEpoch) pollStatus(instance, instance.viewEpoch);
            }
        });
    }

    function refreshArtwork(instance) {
        var epoch = instance.viewEpoch;
        var affected = instance.artworkAffected;
        instance.view.querySelector('.artworkRefreshDialog').close();
        if (!affected) {
            feedbackMessage(instance.view.querySelector('.artworkUploadMessage'), 'Pack installed, but refresh status is unavailable. Use Catalog Refresh when it is available.', true);
            return;
        }
        if (!affected.length) return;
        instance.refreshSection.open = true;
        renderCategoryRefresh(instance, instance.refreshStatus);
        refreshControls(instance, instance.refreshAvailable);
        var summary = instance.view.querySelector('.refreshSummary');
        function reveal() {
            if (instance.headerResize) instance.headerResize();
            summary.scrollIntoView({ block: 'start' });
        }
        summary.focus();
        reveal();
        function remaining(index) {
            return affected.slice(index).map(function (key) { return refreshName(instance.refreshStatus || {}, key); }).join(', ');
        }
        function failed(index) {
            var text = 'Pack installed, but refresh could not start for: ' + remaining(index) + '.';
            var note = instance.view.querySelector('.refreshCategoryNote').textContent;
            if (note) text += ' ' + note;
            feedbackMessage(instance.view.querySelector('.artworkUploadMessage'), text, true);
            feedbackMessage(instance.view.querySelector('.refreshError'), text, true);
            if (document.activeElement === summary) reveal();
        }
        function next(index) {
            if (epoch !== instance.viewEpoch) return;
            if (index >= affected.length) {
                feedbackMessage(instance.view.querySelector('.artworkUploadMessage'), 'Pack installed; affected category refreshes started or queued.');
                if (document.activeElement === summary) reveal();
                return;
            }
            var key = affected[index];
            var action = doRefresh(instance, 'Start', key);
            if (!action) {
                failed(index);
                return;
            }
            return action.then(function (status) {
                if (epoch !== instance.viewEpoch) return;
                if (!status || status.RecoveryChoiceRequired) {
                    failed(index);
                    return;
                }
                return next(index + 1);
            });
        }
        return next(0);
    }

    function renderRefresh(instance, status, lockStatusCurrent) {
        instance.refreshBusy = !!status.Busy;
        var diagnostics = instance.diagnostics;
        if (diagnostics && diagnosticsCurrent(instance, diagnostics)) {
            var signature = JSON.stringify([status.RunId, status.State, status.Busy, status.StartedUtc, status.Category]);
            if (diagnostics.refreshSignature !== null && diagnostics.refreshSignature !== signature)
                diagnosticsLoad(instance);
            diagnostics.refreshSignature = signature;
        }
        var version = instance.view.querySelector('.tuneInPluginVersion');
        if (version && status.PluginVersion) {
            version.textContent = 'TuneIn plugin ' + status.PluginVersion;
            version.hidden = false;
        }
        if (typeof status.PlaybackConnectionPinningAvailable === 'boolean') {
            var playbackWarning = instance.view.querySelector('.playbackSecurityWarning');
            var blocked = !status.PlaybackConnectionPinningAvailable;
            feedbackMessage(playbackWarning, blocked
                ? 'TuneIn blocked playback because secure connection pinning could not be initialized. Update Emby Server or contact the TuneIn plugin maintainer.'
                : '', blocked);
            playbackWarning.hidden = !blocked;
        }
        renderArtworkPacks(instance, status);
        var refreshRequired = status.HasCompleteBaseline === false || !!status.RecoveryCategory || !!status.DirtyCategory ||
            status.State === 'NeverRun' && status.HasRefreshBeenStarted === false && !status.StartedUtc;
        if (instance.refreshDefaultPending) {
            instance.refreshDefaultPending = false;
            instance.refreshSection.open = instance.refreshBusy || refreshRequired;
        }
        var outcomes = (status.Categories || []).map(function (category) { return categoryOutcome(category.State); });
        var overall = categoryOutcome(status.State);
        if (overall === 'Error' || outcomes.includes('Error')) overall = 'Error';
        else if (overall === 'Partial' || outcomes.includes('Partial')) overall = 'Partial';
        else if (overall === 'Limited' || outcomes.includes('Limited')) overall = 'Limited';
        else if (!status.Busy && /^(Completed|NeverRun)$/.test(status.State) &&
            outcomes.length && outcomes.every(function (outcome) { return outcome === 'Completed'; })) overall = 'Completed';
        else overall = '';
        // Reaching a configured discovery limit completes per design, so it shares the success color.
        setRefreshOutcome(instance, overall === 'Limited' ? 'Completed' : overall, overall === 'Error' ? 'error'
            : overall === 'Limited' ? 'successful (limited by discovery settings)'
            : overall === 'Partial' ? 'partial' : overall === 'Completed' ? 'successful'
            : 'not all categories confirmed successful');
        instance.view.querySelector('.refreshIndicator').hidden = !instance.refreshBusy;
        renderCleanup(instance, status.Cleanup);
        instance.maintenanceBusy = status.MaintenanceBusy === true;
        if (lockStatusCurrent !== false && !instance.lockChanging && !instance.saving && !instance.openingLock && !instance.openLockFailed) {
            instance.settingsLocked = typeof status.SettingsLocked === 'boolean' ? status.SettingsLocked : undefined;
            instance.lockError = typeof instance.settingsLocked === 'boolean' ? '' : 'Could not read the settings lock. Retrying automatically.';
        }
        var previousStatus = instance.refreshStatus;
        instance.refreshStatus = status;
        renderOptionalChecks(instance, status);
        renderCatalogRefreshPrompt(instance);
        renderReadiness(instance);
        var category = ({ local: 'Local Radio', music: 'Music', language: 'By Language', location: 'By Location',
            talk: 'Talk', sports: 'Sports', podcast: 'Podcasts', presets: 'My Favorites' })[status.Category];
        var elapsed = formatDuration(elapsedSeconds(status));
        var progressAge = formatDuration(progressAgeSeconds(status));
        var pages = typeof status.CatalogPagesCached === 'number' ? status.CatalogPagesCached.toLocaleString() : null;
        var pageLimit = typeof status.CatalogPagesLimit === 'number' ? status.CatalogPagesLimit.toLocaleString() : null;
        var summary = [];
        instance.view.querySelector('.refreshSummaryState').textContent =
            status.State && status.State !== 'NeverRun' ? ' - ' + status.State : '';
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
        feedbackMessage(instance.view.querySelector('.refreshStatus'), description, categoryOutcome(status.State) === 'Error');
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
        var categories = (status.Categories || []).slice().sort(function (a, b) {
            var left = refreshCategoryOrder.indexOf(a.Category);
            var right = refreshCategoryOrder.indexOf(b.Category);
            return (left < 0 ? refreshCategoryOrder.length : left) -
                (right < 0 ? refreshCategoryOrder.length : right);
        });
        var note = '';
        if (!categories.length) note = 'No categories are enabled in the saved browsing settings.';
        else if (status.ChannelRegistered === false)
            note = 'TuneIn channel not registered yet. Restart Emby and check that the plugin is installed.';
        else if (categories.length > 1 && status.HasCompleteBaseline === false)
            note = 'Run Refresh all enabled categories to complete the initial baseline before refreshing a single category.';
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
                caption.className = 'tuneInRefreshCategoryCaption';
                var captionText = document.createElement('span');
                var indicator = document.createElement('span');
                indicator.className = 'tuneInActivity';
                indicator.hidden = true;
                indicator.setAttribute('role', 'status');
                caption.appendChild(captionText);
                caption.appendChild(indicator);
                var control = button('Refresh', function () { return doRefresh(instance, 'Start', item.Category); });
                control.setAttribute('aria-label', 'Refresh ' + item.Name);
                var actions = document.createElement('div');
                actions.className = 'tuneInRefreshCategoryActions';
                var counts = document.createElement('dl');
                counts.className = 'tuneInStationCounts';
                counts.setAttribute('aria-label', item.Name + ' station counts');
                var countValues = {};
                [['available', 'Available (filtered)'], ['emby', 'In Emby'], ['missing', 'Not in latest discovery']]
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
                    if (editingBlocked(instance) || !browsingSelected(instance)) return;
                    var previous = instance.refreshSchedules.get(item.Category) || 0;
                    instance.refreshSchedules.set(item.Category, Number(schedule.value));
                    if (Number(schedule.value) > 0)
                        return confirmChannelSchedule(instance, item.Name, item.Category, schedule, previous);
                });
                row.appendChild(caption);
                actions.appendChild(counts);
                actions.appendChild(control);
                actions.appendChild(schedule);
                row.appendChild(actions);
                list.appendChild(row);
                rows.set(item.Category, { row: row, caption: captionText, indicator: indicator, control: control, schedule: schedule, counts: countValues });
            });
            $(list).trigger('create');
        }
        categories.forEach(function (item) {
            var active = status.Busy && status.State === 'Running' && !status.SharedImport && status.Category === item.Category;
            var state = !active && (status.QueuedRequests || []).includes(item.Category) ? 'Queued' : item.State;
            var row = rows.get(item.Category);
            row.row.setAttribute('data-outcome', categoryOutcome(state));
            row.row.setAttribute('data-refreshing', String(!!active));
            row.indicator.hidden = !active;
            var indicatorText = active ? 'Refreshing' : '';
            if (row.indicator.textContent !== indicatorText) row.indicator.textContent = indicatorText;
            var text = item.Name + ': ' + state;
            if (item.LimitReason && /^Limited\b/.test(state)) text += ' - ' + item.LimitReason;
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
            feedbackMessage(values.emby, metrics.EmbyCountError
                ? (knownInventory ? count(metrics.EmbyStations) + ' (last known)' : 'Unavailable')
                : count(metrics.EmbyStations), !!metrics.EmbyCountError);
            values.missing.textContent = metrics.AvailablePartial || metrics.EmbyCountError
                ? 'Unavailable' : count(metrics.NotInLatestDiscovery);
            values.missing.setAttribute('title', 'Emby stations absent from the latest complete comparable discovery. Unavailable for partial discoveries or failed inventory checks.');
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
            feedbackMessage(instance.view.querySelector('.backupStatus'), 'Requesting Emby Server Backup...');
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
            feedbackMessage(target, message, status.State === 'Failed');
        }).catch(function () {
            if (epoch !== instance.viewEpoch || instance.backupRequest !== request) return;
            feedbackMessage(instance.view.querySelector('.backupStatus'),
                'Could not verify the Emby backup status. Check Scheduled Tasks before cleanup.', true);
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
        feedbackMessage(target, text, !!cleanup && (cleanup.State === 'Failed' || cleanup.State === 'Error' || !!cleanup.Failed));
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
        feedbackMessage(instance.view.querySelector('.cleanupStationSearchStatus'), '');
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
        feedbackMessage(status, 'Searching TuneIn station entries...');
        refreshControls(instance, instance.refreshAvailable);
        return ApiClient.ajax({
            type: 'GET', dataType: 'json', url: ApiClient.getUrl('TuneIn/Cleanup/Stations', { Name: name })
        }).then(function (result) {
            if (epoch !== instance.viewEpoch || generation !== instance.stationSearchGeneration) return;
            var matches = result && result.Matches || [];
            feedbackMessage(status, !matches.length ? 'No TuneIn station entries match "' + name + '".'
                : (result.More ? 'Showing the first ' + matches.length + ' matches; enter more of the name to narrow them.'
                    : matches.length + (matches.length === 1 ? ' match' : ' matches') + '. Choose the entry to delete.'));
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
                    feedbackMessage(status, 'Selected "' + match.Name + '" in ' + match.Path + '. Select Preview cleanup.');
                    refreshControls(instance, instance.refreshAvailable);
                });
                buttons.push(button);
                results.appendChild(button);
            });
            $(results).trigger('create');
        }).catch(function () {
            if (epoch === instance.viewEpoch && generation === instance.stationSearchGeneration)
                feedbackMessage(status, 'Could not search TuneIn station entries. Try again.', true);
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
        feedbackMessage(view.querySelector('.cleanupStatus'), start ? 'Starting cleanup...' : 'Checking channel entries...');
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
            feedbackMessage(view.querySelector('.cleanupStatus'), '');
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
            maintenanceInProgress(instance) || instance.optionalSaving;
        var status = instance.refreshStatus;
        cannotStart = cannotStart || !!(status && status.ChannelRegistered === false);
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
            row.control.disabled = blocked(category) || !!(status && status.HasCompleteBaseline === false
                && (status.Categories || []).length > 1);
        });
        var cannotClean = !available || !!instance.refreshBusy || !!instance.refreshAction || !!instance.saving ||
            !!instance.lockChanging || !!maintenanceInProgress(instance) || !!instance.optionalSaving;
        instance.view.querySelector('.backupStart').disabled = !available || !!instance.refreshBusy || !!instance.refreshAction ||
            !!instance.saving || !!instance.lockChanging || !!instance.optionalSaving || !!instance.maintenanceBusy ||
            !!instance.cleanupBusy || !!instance.cleanupAction || !!instance.artworkInstalling ||
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
        feedbackMessage(instance.view.querySelector('.refreshLogMessage'), 'Retrieving the latest refresh diagnostic report...');
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
            feedbackMessage(instance.view.querySelector('.refreshLogMessage'), result.Truncated
                ? 'Refresh report retrieved; some details were omitted or truncated.'
                : 'Refresh report retrieved.');
        }).catch(function () {
            if (epoch === instance.viewEpoch && generation === (instance.refreshGeneration || 0)
                && hasRefreshReport(instance.refreshStatus) && !instance.refreshStatus.Busy
                && instance.refreshStatus.FinishedUtc === finishedUtc)
                feedbackMessage(instance.view.querySelector('.refreshLogMessage'),
                    'Could not retrieve the latest refresh report. Check refresh status and try again.', true);
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
        if (instance.refreshAction || instance.lockChanging || instance.cleanupAction || instance.optionalSaving || instance.artworkInstalling)
            return scheduleStatus(instance, epoch);
        if (instance.refreshPoll) return instance.refreshPoll;
        var generation = instance.refreshGeneration || 0;
        var lockStatusCurrent = !instance.openingLock;
        instance.refreshPoll = ApiClient.ajax({
            type: 'GET', dataType: 'json', url: ApiClient.getUrl('TuneIn/Refresh/Status')
        }).then(function (status) {
            if (epoch === instance.viewEpoch && generation === (instance.refreshGeneration || 0)) renderRefresh(instance, status, lockStatusCurrent);
        }).catch(function () {
            if (epoch === instance.viewEpoch && generation === (instance.refreshGeneration || 0)) {
                instance.refreshStatus = null;
                instance.settingsLocked = undefined;
                renderOptionalChecks(instance, null);
                renderReadiness(instance);
                setRefreshOutcome(instance, '', 'status unavailable');
                feedbackMessage(instance.view.querySelector('.refreshStatus'), 'Could not read refresh status. Retrying automatically.', true);
                instance.view.querySelector('.refreshCategoryNote').textContent = '';
                renderArtworkPacks(instance, null);
                if (!instance.openLockFailed)
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
                return status;
            }).catch(function () {
                if (epoch === instance.viewEpoch)
                    instance.view.querySelector('.refreshError').textContent = 'Could not ' + action.toLowerCase() + ' refresh. Check the current status before retrying.';
                return null;
            }).then(function (status) {
                instance.refreshAction = false;
                if (epoch === instance.viewEpoch) refreshControls(instance, instance.refreshAvailable);
                if (instance.refreshVisible) pollStatus(instance, instance.viewEpoch);
                return status;
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
        var maximumRateLimitWait = String(instance.view.querySelector('.maximumRateLimitWaitMinutes').value || '').trim();
        if (!/^[0-9]+$/.test(maximumRateLimitWait) || Number(maximumRateLimitWait) < 5 || Number(maximumRateLimitWait) > 60) {
            message(instance, 'Use a whole number from 5 to 60 for the maximum rate-limit wait per refresh.');
            return false;
        }
        var rateLimitRetries = String(instance.view.querySelector('.maximumConsecutiveRateLimitRetries').value).trim();
        if (!/^[0-9]+$/.test(rateLimitRetries) || Number(rateLimitRetries) > 10) {
            message(instance, 'Use a whole number from 0 to 10 for consecutive HTTP 429 retries.');
            return false;
        }
        if (!/^[0-9]+$/.test(discoveryDeadline) || Number(discoveryDeadline) < 2 || Number(discoveryDeadline) > 48) {
            message(instance, 'Use a whole number from 2 to 48 for the discovery deadline.');
            return false;
        }
        var importDeadline = String(instance.view.querySelector('.importDeadlineHours').value || '').trim();
        if (!/^[0-9]+$/.test(importDeadline) || Number(importDeadline) < 1 || Number(importDeadline) > 100) {
            message(instance, 'Use a whole number from 1 to 100 for the import deadline.');
            return false;
        }
        var maxStationCount = String(instance.view.querySelector('.maxStationCount').value || '').trim();
        if (maxStationCount !== '' && (!/^[0-9]+$/.test(maxStationCount) || Number(maxStationCount) < 5 || Number(maxStationCount) > 10000)) {
            message(instance, 'Use unlimited (blank) or a whole number from 5 to 10000 for Max Station Count.');
            return false;
        }
        var traversalDepth = String(instance.view.querySelector('.traversalDepthLimit').value || '').trim();
        if (!/^[0-9]+$/.test(traversalDepth) || Number(traversalDepth) < 3 || Number(traversalDepth) > 50) {
            message(instance, 'Use a whole number from 3 to 50 for the traversal depth limit.');
            return false;
        }
        for (var i = 0; i < progressiveSettings.length; i++) {
            var setting = progressiveSettings[i];
            var value = String(instance.view.querySelector(setting[1]).value || '').trim();
            if (!/^[0-9]+$/.test(value) || Number(value) < 1 || Number(value) > setting[2]) {
                message(instance, 'Use a whole number from 1 to ' + setting[2] + ' for ' + setting[4] + '.');
                return false;
            }
            config[setting[0]] = Number(value);
        }
        var localEnabled = instance.view.querySelector('[data-category="EnableLocalRadio"]').checked;
        var browsing = browsingSelected(instance);
        var setupProblems = setupErrors(instance);
        renderReadiness(instance);
        if (browsing && Object.keys(setupProblems).length) {
            message(instance, Object.keys(setupProblems).map(function (key) { return setupProblems[key]; }).join(' '));
            return false;
        }
        var source = instance.view.querySelector('.localSource').value;
        var useCoordinates = localEnabled && source === 'coordinates';
        var hasSavedRegion = (config.LocalCities || []).some(function (item) {
            return item && item.Enabled !== false && /^r[1-9][0-9]{0,9}$/.test(item.Id);
        });
        if (browsing && localEnabled && source !== 'coordinates' && (source !== 'regions' ||
            !hasSavedRegion)) {
            message(instance, 'Choose valid coordinates or at least one enabled saved city or region for Local Radio.');
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
        config.MaximumRateLimitWaitMinutes = Number(maximumRateLimitWait);
        config.MaximumConsecutiveRateLimitRetries = Number(rateLimitRetries);
        config.ImportDeadlineHours = Number(importDeadline);
        config.DiscoveryDeadlineHours = Number(discoveryDeadline);
        config.MaxStationCount = maxStationCount === '' ? null : Number(maxStationCount);
        config.TraversalDepthLimit = Number(traversalDepth);
        config.EnableCatalogIdentityDiagnostics = instance.view.querySelector('.enableCatalogIdentityDiagnostics').checked;
        config.AllowDirectHlsPlayback = instance.view.querySelector('.allowDirectHlsPlayback').checked;
        config.HideRegionalGenres = instance.view.querySelector('.hideRegionalGenres').checked;
        config.AllowPartialCatalogImport = instance.view.querySelector('.allowPartialCatalogImport').checked;
        config.EnableDiscoveryProgressiveImport = instance.view.querySelector('.enableDiscoveryProgressiveImport').checked;
        config.DiscoveryImportRiskToken = null;
        config.RefreshSchedules = Array.from(instance.refreshSchedules)
            .filter(function (entry) { return entry[1] > 0; })
            .map(function (entry) { return { Category: entry[0], IntervalHours: entry[1] }; });
        config.UseCoordinates = useCoordinates;
        var savedSource = normalizeLocalSource(config.LocalRadioSource);
        config.LocalRadioSource = source === (savedSource === 'cities' ? 'regions' : savedSource)
            ? config.LocalRadioSource : source || null;
        if (localEnabled && source === 'regions') config.LocalMode = 'selected';
        categories.forEach(function (spec) {
            config[spec[0]] = instance.view.querySelector('[data-category="' + spec[0] + '"]').checked;
        });
        var filteringChanged = JSON.stringify(filteringSnapshot(config)) !==
            JSON.stringify(filteringSnapshot(instance.savedFilteringConfig || {}));
        instance.saving = true;
        instance.lockError = '';
        refreshControls(instance, instance.refreshAvailable);
        loading.show();
        var requiresRiskToken = config.EnableDiscoveryProgressiveImport &&
            !(instance.config.EnableDiscoveryProgressiveImport === true &&
                instance.config.DiscoveryImportRiskVersion === 1 && instance.config.DiscoveryImportRiskAcceptedUtc);
        var save = requiresRiskToken
            ? ApiClient.ajax({
                type: 'POST', dataType: 'json',
                url: ApiClient.getUrl('TuneIn/DiscoveryImport/Acknowledge', { Version: 1, Accepted: true })
            }).then(function (result) {
                if (!result || !result.Token) throw new Error('Risk acknowledgement was not recorded.');
                config.DiscoveryImportRiskToken = result.Token;
                return ApiClient.updatePluginConfiguration(pluginId, config);
            })
            : ApiClient.updatePluginConfiguration(pluginId, config);
        save.then(function (result) {
            if (epoch === instance.viewEpoch) {
                instance.setupSaved = true;
                instance.setupDirty = false;
                config.DiscoveryImportRiskToken = null;
                if (requiresRiskToken) {
                    config.DiscoveryImportRiskVersion = 1;
                    config.DiscoveryImportRiskAcceptedUtc = new Date().toISOString();
                }
                instance.view.querySelector('.discoveryRiskUpgradeNotice').hidden = true;
                instance.config = config;
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
                renderReadiness(instance);
            }
            instance.settingsLocked = true;
            if (epoch !== instance.viewEpoch) return;
            Dashboard.processPluginConfigurationUpdateResult(result);
            message(instance, browsingStatus(config), configurationRequired(config), false);
            renderCatalogRefreshPrompt(instance);
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
        instance.selectorErrors = new Map();
        instance.setupControlsReady = false;
        var helpStates = new WeakMap();
        view.querySelectorAll('.tuneInHelp, .tuneInArtworkHelp').forEach(function (help) {
            var state = { pinned: false, hovering: false, dismissed: false };
            helpStates.set(help, state);
            function positionHelp() {
                var popup = help.querySelector(':scope > :not(summary)');
                var anchor = help.getBoundingClientRect();
                var bounds = popup.getBoundingClientRect();
                var width = document.documentElement.clientWidth;
                var height = document.documentElement.clientHeight;
                var scale = anchor.width / help.offsetWidth || 1;
                var right = anchor.right + bounds.width <= width - 8;
                var left = right ? anchor.width : Math.max(8 - anchor.left, Math.min(0, width - 8 - bounds.width - anchor.left));
                var top = right ? Math.max(8 - anchor.top, Math.min(0, height - 8 - bounds.height - anchor.top))
                    : anchor.height;
                if (!right && anchor.bottom + bounds.height > height - 8 && anchor.top >= bounds.height + 8)
                    top = -bounds.height;
                help.style.setProperty('--tunein-help-left', left / scale + 'px');
                help.style.setProperty('--tunein-help-top', top / scale + 'px');
            }
            help.addEventListener('pointerenter', function (event) {
                if (event.pointerType !== 'mouse' && event.pointerType !== 'pen') return;
                state.hovering = true;
                state.dismissed = false;
                help.open = true;
                positionHelp();
            });
            help.addEventListener('pointerleave', function (event) {
                if (event.pointerType !== 'mouse' && event.pointerType !== 'pen') return;
                state.hovering = false;
                state.dismissed = false;
                help.open = state.pinned;
            });
            help.querySelector('summary').addEventListener('click', function (event) {
                event.preventDefault();
                state.pinned = !state.pinned;
                state.dismissed = !state.pinned;
                help.open = state.pinned || (state.hovering && !state.dismissed);
                if (help.open) positionHelp();
            });
        });
        view.addEventListener('keydown', function (event) {
            if (event.key !== 'Escape' || !event.target.closest) return;
            var help = event.target.closest('.tuneInHelp, .tuneInArtworkHelp');
            if (!help || !help.open) return;
            var state = helpStates.get(help);
            if (state) {
                state.pinned = false;
                state.dismissed = true;
            }
            help.open = false;
            help.querySelector('summary').focus();
            event.preventDefault();
            event.stopPropagation();
        });
        ['.globalSummary', '.categoriesSummary', '.stationSelectionsSummary', '.maintenanceSummary'].forEach(function (selector) {
            bindDisclosure(view.querySelector(selector));
        });
        bindDisclosure(view.querySelector('.browsingSummary'), function () {
            if (!view.querySelector('.browsingSummary').parentNode.open) expandFilteringGroups(instance);
        });
        bindDisclosure(view.querySelector('.refreshSummary'), function () { instance.refreshDefaultPending = false; });
        view.querySelector('.settingsLocked').addEventListener('change', function () { return changeSettingsLock(instance); });
        view.querySelector('.configurationCompleted').addEventListener('change', function () { editing(instance); setupChanged(instance); });
        view.querySelector('.tuneInAdvancedGuideLink').addEventListener('click', function (event) {
            event.preventDefault();
            event.stopPropagation();
            view.querySelector('.tuneInAdvancedGuide').showModal();
        });
        view.querySelector('.tuneInAdvancedGuideClose').addEventListener('click', function () {
            view.querySelector('.tuneInAdvancedGuide').close();
        });
        view.querySelector('.tuneInAdvancedGuide').addEventListener('close', function () {
            view.querySelector('.tuneInAdvancedGuideLink').focus();
        });
        view.querySelector('.enableDiscoveryProgressiveImport').addEventListener('change', function () {
            if (this.checked) {
                this.checked = false;
                instance.discoveryRiskAccepted = false;
                view.querySelector('.discoveryRiskAccepted').checked = false;
                view.querySelector('.discoveryRiskConfirm').disabled = true;
                var needsPartialImport = !view.querySelector('.allowPartialCatalogImport').checked;
                view.querySelector('.discoveryRiskPartialRequirement').hidden = !needsPartialImport;
                view.querySelector('.discoveryRiskConfirm').textContent =
                    needsPartialImport ? 'Accept and enable both' : 'Accept and continue';
                view.querySelector('.discoveryRiskDialog').setAttribute('aria-describedby', needsPartialImport
                    ? 'discoveryRiskDescription discoveryRiskPartialRequirement' : 'discoveryRiskDescription');
                view.querySelector('.discoveryRiskDialog').showModal();
            } else {
                instance.discoveryRiskAccepted = false;
            }
            showProgressivePacing(instance);
            editing(instance);
        });
        view.querySelector('.discoveryRiskAccepted').addEventListener('change', function () {
            view.querySelector('.discoveryRiskConfirm').disabled = !this.checked;
        });
        view.querySelector('.discoveryRiskCancel').addEventListener('click', function () {
            view.querySelector('.discoveryRiskDialog').close();
        });
        view.querySelector('.discoveryRiskConfirm').addEventListener('click', function () {
            if (!view.querySelector('.discoveryRiskAccepted').checked || editingBlocked(instance)) return;
            instance.discoveryRiskAccepted = true;
            if (!view.querySelector('.discoveryRiskPartialRequirement').hidden)
                view.querySelector('.allowPartialCatalogImport').checked = true;
            view.querySelector('.enableDiscoveryProgressiveImport').checked = true;
            view.querySelector('.discoveryRiskDialog').close();
            showProgressivePacing(instance);
            editing(instance);
            setupChanged(instance);
        });
        view.querySelector('.discoveryRiskDialog').addEventListener('close', function () {
            view.querySelector('.enableDiscoveryProgressiveImport').focus();
        });
        view.querySelector('.optionalChecksRun').addEventListener('click', function () { return runOptionalChecks(instance); });
        view.querySelector('.skipOptionalChecks').addEventListener('input', function (event) { event.stopPropagation(); });
        view.querySelector('.skipOptionalChecks').addEventListener('change', function (event) {
            event.stopPropagation();
            return saveOptionalChecks(instance, this.checked);
        });
        var dialog = view.querySelector('.tuneInRefreshRecoveryDialog');
        instance.refreshSection.addEventListener('toggle', function () {
            if (!instance.refreshSection.open && dialog.open) dialog.close();
            if (!instance.refreshSection.open || !instance.refreshVisible) return;
            instance.refreshGeneration = (instance.refreshGeneration || 0) + 1;
            feedbackMessage(view.querySelector('.refreshStatus'), 'Reading refresh status...');
            view.querySelector('.refreshCategoryNote').textContent = '';
            refreshControls(instance, false);
            pollStatus(instance, instance.viewEpoch);
        });
        view.querySelector('form').addEventListener('submit', onSubmit.bind(this));
        view.querySelector('form').addEventListener('invalid', function (event) {
            for (var parent = event.target.parentElement; parent && parent !== view; parent = parent.parentElement) {
                if (parent.tagName === 'DETAILS') parent.open = true;
            }
        }, true);
        function settingsChanged(event) {
            if (event && event.target === view.querySelector('.settingsLocked')) return;
            setupChanged(instance);
        }
        view.querySelector('form').addEventListener('input', settingsChanged);
        view.querySelector('form').addEventListener('change', settingsChanged);
        view.querySelector('.artworkUpload').addEventListener('click', function () {
            if (!instance.artworkBusy && !editingBlocked(instance)) view.querySelector('.artworkFile').click();
        });
        view.querySelector('.artworkFile').addEventListener('input', function (event) { event.stopPropagation(); });
        view.querySelector('.artworkFile').addEventListener('change', function (event) {
            event.stopPropagation();
            var file = this.files && this.files[0];
            this.value = '';
            if (!file || instance.artworkBusy) return;
            if (editingBlocked(instance)) {
                feedbackMessage(view.querySelector('.artworkUploadMessage'), 'Unlock settings and wait for active work before verifying artwork.', true);
                return;
            }
            instance.artworkSelectedFile = file;
            view.querySelector('.artworkVerifyText').textContent = 'Selected: ' + file.name + '.';
            view.querySelector('.artworkVerifyDialog').showModal();
        });
        view.querySelector('.artworkVerifyStart').addEventListener('click', function () {
            var file = instance.artworkSelectedFile;
            instance.artworkSelectedFile = null;
            view.querySelector('.artworkVerifyDialog').close();
            if (editingBlocked(instance)) {
                feedbackMessage(view.querySelector('.artworkUploadMessage'), 'Unlock settings and wait for active work before verifying artwork.', true);
                return;
            }
            return uploadArtwork(instance, file);
        });
        function cancelArtworkVerification() {
            instance.artworkSelectedFile = null;
            var verifyDialog = view.querySelector('.artworkVerifyDialog');
            if (verifyDialog.open) verifyDialog.close();
        }
        view.querySelector('.artworkVerifyCancel').addEventListener('click', cancelArtworkVerification);
        view.querySelector('.artworkVerifyDialog').addEventListener('cancel', cancelArtworkVerification);
        view.querySelector('.artworkApproveUnscanned').addEventListener('input', function (event) { event.stopPropagation(); });
        view.querySelector('.artworkApproveUnscanned').addEventListener('change', function (event) {
            event.stopPropagation();
            view.querySelector('.artworkInstall').disabled = !this.checked;
        });
        view.querySelector('.artworkInstall').addEventListener('click', function () { return installArtwork(instance); });
        view.querySelector('.artworkDiscard').addEventListener('click', function () { return discardArtwork(instance); });
        view.querySelector('.artworkConfirmDialog').addEventListener('cancel', function () { return discardArtwork(instance); });
        view.querySelector('.artworkRefreshNow').addEventListener('click', function () { return refreshArtwork(instance); });
        view.querySelector('.artworkRefreshLater').addEventListener('click', function () {
            view.querySelector('.artworkRefreshDialog').close();
            feedbackMessage(view.querySelector('.artworkUploadMessage'), 'Pack installed. No refresh was started.');
        });
        view.querySelector('.refreshStart').addEventListener('click', function () { return doRefresh(instance, 'Start'); });
        view.querySelector('.refreshCancel').addEventListener('click', function () { return doRefresh(instance, 'Cancel'); });
        view.querySelector('.refreshLogButton').addEventListener('click', function () { return retrieveRefreshLog(instance); });
        view.querySelector('.diagnosticsCapture').addEventListener('click', function () { return diagnosticsCapture(instance); });
        view.querySelector('.diagnosticsPacks').addEventListener('change', function () { return diagnosticsSelect(instance); });
        view.querySelector('.diagnosticsReviewed').addEventListener('change', function () {
            diagnosticsClearDraftLink(instance);
            instance.diagnostics.reviewVersion++;
            diagnosticsControls(instance);
        });
        view.querySelector('.diagnosticsDownload').addEventListener('click', function () {
            var pack = instance.diagnostics.preview;
            if (!pack || this.disabled) return;
            var url;
            try {
                url = URL.createObjectURL(new Blob([pack.PreviewText], { type: 'application/json' }));
                var link = document.createElement('a');
                link.href = url;
                link.download = pack.FileName;
                link.click();
                diagnosticsMessage(instance, 'Downloaded the exact redacted preview. Nothing was uploaded.');
            } catch (error) {
                diagnosticsMessage(instance, 'Could not download the diagnostic pack. Try again.', true);
            } finally {
                if (url) URL.revokeObjectURL(url);
            }
        });
        view.querySelector('.diagnosticsOpen').addEventListener('click', function (event) {
            if (event) { event.preventDefault(); event.stopPropagation(); }
            return diagnosticsDraft(instance);
        });
        view.querySelector('.diagnosticsDelete').addEventListener('click', function () { diagnosticsDelete(instance, false); });
        view.querySelector('.diagnosticsDeleteAll').addEventListener('click', function () { diagnosticsDelete(instance, true); });
        view.querySelector('.diagnosticsDeleteCancel').addEventListener('click', function () {
            instance.diagnostics.deleteTarget = null;
            view.querySelector('.diagnosticsDeleteDialog').close();
        });
        view.querySelector('.diagnosticsDeleteConfirm').addEventListener('click', function () { return diagnosticsConfirmDelete(instance); });
        view.querySelector('.diagnosticsDeleteDialog').addEventListener('close', function () {
            var state = instance.diagnostics;
            state.deleteTarget = null;
            if (state.deleteFocus && !state.deleteFocus.disabled) state.deleteFocus.focus();
            else view.querySelector('.diagnosticsCapture').focus();
        });
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
        view.querySelector('.channelScheduleConfirm').addEventListener('click', function () {
            closeChannelScheduleDialog(instance);
        });
        view.querySelector('.channelScheduleAdjust').addEventListener('click', function () {
            var pending = instance.channelSchedulePending;
            closeChannelScheduleDialog(instance);
            if (!pending || editingBlocked(instance)) return;
            pending.schedule.value = String(pending.previous);
            instance.refreshSchedules.set(pending.category, pending.previous);
            pending.schedule.focus();
        });
    }

    function stopPinnedHeader(instance) {
        if (instance.headerObserver) instance.headerObserver.disconnect();
        if (instance.headerResize) window.removeEventListener('resize', instance.headerResize);
        instance.headerObserver = null;
        instance.headerResize = null;
        var header = instance.view.querySelector('.tuneInPinned');
        if (header) {
            header.setAttribute('data-pinned', 'false');
            instance.view.style.setProperty('--tunein-header-height', '0px');
        }
    }

    function watchPinnedHeader(instance) {
        stopPinnedHeader(instance);
        var header = instance.view.querySelector('.tuneInPinned');
        if (!header || typeof ResizeObserver !== 'function') return;
        var update = function () {
            if (instance.headerResize !== update) return;
            var height = header.getBoundingClientRect().height;
            var pinned = height > 0 && height <= document.documentElement.clientHeight * .45;
            header.setAttribute('data-pinned', String(pinned));
            instance.view.style.setProperty('--tunein-header-height', pinned ? height + 'px' : '0px');
        };
        instance.headerResize = update;
        instance.headerObserver = new ResizeObserver(update);
        instance.headerObserver.observe(header);
        window.addEventListener('resize', update);
        update();
    }

    function showLibraryArtwork(view) {
        var hero = view.querySelector('.tuneInHero');
        var art = hero && hero.querySelector('.tuneInHeroArt');
        if (!art || art.getAttribute('src') || typeof ApiClient.accessToken !== 'function') return;
        art.onload = function () { hero.hidden = false; };
        art.onerror = function () { hero.hidden = true; };
        // An img request cannot send Emby's auth header, so pass the session token like Emby's own image URLs.
        art.src = ApiClient.getUrl('TuneIn/PanelArtwork', { v: '2', api_key: ApiClient.accessToken() });
    }

    Object.assign(View.prototype, BaseView.prototype);

    View.prototype.onResume = function () {
        BaseView.prototype.onResume.apply(this, arguments);
        var instance = this;
        var epoch = instance.viewEpoch = (instance.viewEpoch || 0) + 1;
        instance.refreshVisible = true;
        watchPinnedHeader(instance);
        instance.diagnostics = { epoch: epoch, packs: [], latest: null, captureAvailable: false,
            repository: null, reportingError: null, storageError: null, hasStoredPacks: false,
            preview: null, previewVersion: 0, reviewVersion: 0,
            listVersion: 0, listPending: false, listAgain: false, busy: false, refreshSignature: null };
        instance.view.querySelector('.diagnosticsPublic').checked = false;
        instance.view.querySelector('.diagnosticsPacks').value = '';
        instance.view.querySelector('.diagnosticsRepository').textContent = 'Unavailable';
        diagnosticsClearPreview(instance);
        diagnosticsMessage(instance, '');
        diagnosticsLoad(instance);
        instance.view.querySelector('.optionalChecksRun').disabled = !!instance.optionalChecking;
        instance.view.querySelector('.artworkUpload').disabled = true;
        instance.view.querySelector('.artworkFile').disabled = true;
        if (instance.artworkBusy) pollArtwork(instance, epoch);
        showLibraryArtwork(instance.view);
        instance.refreshDefaultPending = true;
        instance.refreshSection.open = false;
        instance.selectionDeletionPending = false;
        setRefreshOutcome(instance, '', 'status unavailable');
        clearCleanupPreview(instance);
        instance.view.querySelector('.cleanupError').textContent = '';
        feedbackMessage(instance.view.querySelector('.refreshStatus'), 'Reading refresh status...');
        instance.view.querySelector('.refreshError').textContent = '';
        instance.view.querySelector('.catalogRefreshPrompt').hidden = true;
        instance.view.querySelector('.catalogRefreshPrompt').textContent = '';
        instance.view.querySelector('.stationDeletionNotice').hidden = true;
        instance.view.querySelector('.stationDeletionNotice').textContent = '';
        closeChannelScheduleDialog(instance);
        instance.view.querySelector('.refreshFailureLog').textContent = '';
        instance.view.querySelector('.refreshFailureLog').hidden = true;
        instance.view.querySelector('.refreshLogMessage').textContent = '';
        instance.refreshLogAction = null;
        instance.config = null;
        instance.refreshStatus = null;
        renderOptionalChecks(instance, null);
        instance.view.querySelector('.tuneInReadiness').hidden = true;
        instance.view.querySelector('.settingsChangeNotice').hidden = true;
        instance.setupControlsReady = false;
        instance.selectorErrors.clear();
        instance.settingsLocked = undefined;
        instance.lockError = '';
        instance.loadingConfig = true;
        lockSettingsOnOpen(instance, epoch);
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
            view.querySelector('.maximumRateLimitWaitMinutes').value =
                config.MaximumRateLimitWaitMinutes == null ? 15 : config.MaximumRateLimitWaitMinutes;
            view.querySelector('.maximumConsecutiveRateLimitRetries').value =
                config.MaximumConsecutiveRateLimitRetries == null ? 6 : config.MaximumConsecutiveRateLimitRetries;
            view.querySelector('.importDeadlineHours').value =
                config.ImportDeadlineHours == null ? 24 : config.ImportDeadlineHours;
            view.querySelector('.discoveryDeadlineHours').value =
                config.DiscoveryDeadlineHours == null ? 12 : config.DiscoveryDeadlineHours;
            view.querySelector('.maxStationCount').value =
                config.MaxStationCount === undefined ? 500 : config.MaxStationCount == null ? '' : config.MaxStationCount;
            view.querySelector('.traversalDepthLimit').value =
                config.TraversalDepthLimit == null ? 10 : config.TraversalDepthLimit;
            view.querySelector('.enableCatalogIdentityDiagnostics').checked = config.EnableCatalogIdentityDiagnostics === true;
            view.querySelector('.allowDirectHlsPlayback').checked = config.AllowDirectHlsPlayback === true;
            view.querySelector('.hideRegionalGenres').checked = config.HideRegionalGenres !== false;
            view.querySelector('.allowPartialCatalogImport').checked = config.AllowPartialCatalogImport !== false;
            instance.discoveryRiskAccepted = config.EnableDiscoveryProgressiveImport === true &&
                config.DiscoveryImportRiskVersion === 1 && !!config.DiscoveryImportRiskAcceptedUtc;
            view.querySelector('.discoveryRiskUpgradeNotice').hidden =
                config.EnableDiscoveryProgressiveImport !== true || instance.discoveryRiskAccepted;
            view.querySelector('.enableDiscoveryProgressiveImport').checked = instance.discoveryRiskAccepted;
            progressiveSettings.forEach(function (setting) {
                view.querySelector(setting[1]).value = config[setting[0]] == null ? setting[3] : config[setting[0]];
            });
            showProgressivePacing(instance);
            var hasRegions = (config.LocalCities || []).some(function (item) {
                return item && item.Enabled !== false && /^r[1-9][0-9]{0,9}$/.test(item.Id);
            });
            var ambiguous = !config.LocalRadioSource && config.UseCoordinates && hasRegions;
            var localSource = normalizeLocalSource(config.LocalRadioSource);
            view.querySelector('.localSource').value = config.LocalRadioSource
                ? (localSource === 'cities' ? 'regions' : localSource)
                : (ambiguous ? '' : config.UseCoordinates ? 'coordinates' : hasRegions ? 'regions' : '');
            view.querySelector('.configurationCompleted').checked = config.SchemaVersion === 1 ?
                !!config.ConfigurationCompleted : config.SchemaVersion === 0 || config.SchemaVersion == null;
            var saved = config.SettingsSaved === true || (config.SettingsSaved == null && config.SchemaVersion === 1);
            instance.setupSaved = saved;
            instance.setupDirty = false;
            showSaveHistory(instance, saved, saved && !configurationRequired(config));
            var list = view.querySelector('.categories');
            list.textContent = '';
            categories.forEach(function (spec) {
                var row = document.createElement('div');
                var control = checkbox(spec[1], !!config[spec[0]], function () { setupChanged(instance); });
                control.input.setAttribute('data-category', spec[0]);
                row.appendChild(control.label);
                list.appendChild(row);
            });
            var pickerList = view.querySelector('.selectors');
            pickerList.textContent = '';
            selectors.forEach(function (spec) {
                pickerList.appendChild(createSelector(instance, spec, !saved));
            });
            instance.setupControlsReady = true;
            renderReadiness(instance);
            $(view).trigger('create');
            message(instance, browsingStatus(config) + (ambiguous && config.EnableLocalRadio ?
                ' Local Radio is disabled until you choose one source and save.' : ''), configurationRequired(config), false);
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
        stopPinnedHeader(this);
        if (this.diagnostics) diagnosticsResetReview(this);
        var diagnosticsDialog = this.view.querySelector('.diagnosticsDeleteDialog');
        if (diagnosticsDialog.open) diagnosticsDialog.close();
        var guide = this.view.querySelector('.tuneInAdvancedGuide');
        if (guide.open) guide.close();
        var discoveryRisk = this.view.querySelector('.discoveryRiskDialog');
        if (discoveryRisk.open) discoveryRisk.close();
        this.backupRequest = null;
        this.backupStarting = false;
        clearCleanupPreview(this);
        this.refreshRecovery = null;
        var dialog = this.view.querySelector('.tuneInRefreshRecoveryDialog');
        if (dialog.open) dialog.close();
        closeChannelScheduleDialog(this);
        clearTimeout(this.refreshTimer);
        clearTimeout(this.artworkTimer);
        this.artworkSelectedFile = null;
        var artworkVerifyDialog = this.view.querySelector('.artworkVerifyDialog');
        if (artworkVerifyDialog.open) artworkVerifyDialog.close();
        if (this.artworkPending) discardArtwork(this);
        var artworkRefreshDialog = this.view.querySelector('.artworkRefreshDialog');
        if (artworkRefreshDialog.open) artworkRefreshDialog.close();
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
