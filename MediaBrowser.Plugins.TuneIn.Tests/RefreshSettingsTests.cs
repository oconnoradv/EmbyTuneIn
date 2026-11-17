using MediaBrowser.Controller.Net;
using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using MediaBrowser.Model.Services;
using Xunit;

namespace MediaBrowser.Plugins.TuneIn.Tests
{
    public class RefreshSettingsTests
    {
        [Fact]
        public void SettingsLockPersistsSeparatelyAndDefaultsUnlocked()
        {
            var path = Path.Combine(Environment.CurrentDirectory, "tunein-lock-" + Guid.NewGuid().ToString("N") + ".xml");
            try
            {
                var store = new TuneInSettingsLockStore(path);
                Assert.False(store.IsLocked);
                store.Set(true, false);
                var reloaded = new TuneInSettingsLockStore(path);
                Assert.True(reloaded.IsLocked);
                Assert.True(reloaded.Read(true).RefreshActive);
                reloaded.Set(false, false);
                Assert.False(new TuneInSettingsLockStore(path).IsLocked);
            }
            finally
            {
                if (File.Exists(path)) File.Delete(path);
                if (File.Exists(path + ".tmp")) File.Delete(path + ".tmp");
            }
        }

        [Fact]
        public async Task StartingRefreshWaitsForConfigurationSaveToFinish()
        {
            var config = new Configuration.PluginConfiguration { SchemaVersion = 1, ConfigurationCompleted = true };
            var directory = new TuneInDirectory(() => config, (_, __) => Task.FromResult(
                System.Xml.Linq.XDocument.Parse("<opml><head><status>200</status></head><body/></opml>")));
            var refresh = new TuneInRefresh(directory);
            var channel = new TuneInChannel(ConfigurationAndNavigationTests.ArtworkHost(), null, directory);
            var enteredSave = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var finishSave = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var save = Task.Run(() => refresh.UpdateConfiguration(() => false, () =>
            {
                config.EnableTalk = true;
                enteredSave.TrySetResult(true);
                finishSave.Task.GetAwaiter().GetResult();
            }));
            await enteredSave.Task;
            var starting = Task.Run(() => refresh.Start("talk", (folder, depth, token) =>
                RadioGroupRefreshTests.Synchronize(channel, directory, "talk", folder, depth, token), null));
            await Task.Delay(20);
            Assert.False(starting.IsCompleted);
            finishSave.TrySetResult(true);
            await save;
            var job = await starting;
            await refresh.Completion("talk");
            Assert.Equal("Completed", refresh.CategoryStatus("talk").State);
            Assert.NotNull(job.Id);
        }

        [Fact]
        public void RefreshApiContractsAreAdminOnlyAndValidationFieldsAreStable()
        {
            var endpoints = new[]
            {
                typeof(GetTuneInSettingsLock), typeof(SetTuneInSettingsLock),
                typeof(StartTuneInCategoryRefresh), typeof(GetTuneInCategoryRefresh), typeof(GetTuneInCategoryRefreshes),
                typeof(ResumeTuneInCategoryRefresh)
            };
            var routes = endpoints.Select(type => type.GetCustomAttribute<RouteAttribute>()).ToArray();
            Assert.Equal(new[]
            {
                "/TuneInFork/SettingsLock", "/TuneInFork/SettingsLock",
                "/TuneInFork/RefreshTuneIn/Category", "/TuneInFork/RefreshTuneIn/Category",
                "/TuneInFork/RefreshTuneIn/Categories",
                "/TuneInFork/RefreshTuneIn/Category/Resume"
            }, routes.Select(route => route.Path));
            Assert.All(endpoints, type => Assert.Equal("Admin",
                type.GetCustomAttribute<AuthenticatedAttribute>().Roles));
            Assert.Equal(typeof(bool?), typeof(SetTuneInSettingsLock).GetProperty("Locked").PropertyType);
            Assert.NotNull(typeof(StartTuneInCategoryRefresh).GetProperty("Category"));
            Assert.NotNull(typeof(ResumeTuneInCategoryRefresh).GetProperty("AttemptId"));
            Assert.NotNull(typeof(RadioGroupRefreshStatus).GetProperty("EstimatedRequests"));
            Assert.NotNull(typeof(RadioGroupRefreshStatus).GetProperty("ImportFoldersRequested"));
            Assert.Equal(typeof(RadioGroupRefreshStatus[]),
                typeof(GetTuneInCategoryRefreshes).GetInterfaces()
                    .Single(type => type.IsGenericType && type.GetGenericTypeDefinition() == typeof(IReturn<>))
                    .GetGenericArguments()[0]);
        }
    }
}
