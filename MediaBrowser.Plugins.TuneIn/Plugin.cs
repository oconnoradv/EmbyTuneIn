using System;
using System.Collections.Generic;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;
using MediaBrowser.Plugins.TuneIn.Configuration;
using System.IO;
using MediaBrowser.Model.Drawing;

namespace MediaBrowser.Plugins.TuneIn
{
    /// <summary>
    /// Class Plugin
    /// </summary>
    public class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages, IHasThumbImage
    {
        private readonly object configurationGate = new object();
        
        public Plugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer)
            : base(applicationPaths, xmlSerializer)
        {
            SettingsLock = new TuneInSettingsLockStore(string.IsNullOrEmpty(applicationPaths.DataPath)
                ? null : Path.Combine(applicationPaths.DataPath, "tunein-fork-settings-lock.xml"));
            var artworkRoot = string.IsNullOrEmpty(applicationPaths.PluginConfigurationsPath)
                ? null : Path.Combine(applicationPaths.PluginConfigurationsPath, "TuneInArtwork");
            TuneInCategoryArtwork.GenreFolder = artworkRoot == null ? null : Path.Combine(artworkRoot, "genres");
            TuneInCategoryArtwork.LanguageFolder = artworkRoot == null ? null : Path.Combine(artworkRoot, "languages");
            TuneInCategoryArtwork.LocationFolder = artworkRoot == null ? null : Path.Combine(artworkRoot, "locations");
            TuneInCategoryArtwork.CategoryFolder = artworkRoot == null ? null : Path.Combine(artworkRoot, "categories");
            ArtworkRoot = artworkRoot;
            if (artworkRoot != null)
                foreach (var scope in TuneInCategoryArtwork.Scopes) TuneInArtworkInstall.Recover(artworkRoot, scope);
            Instance = this;
        }

        internal TuneInSettingsLockStore SettingsLock { get; }
        internal string ArtworkRoot { get; }

        public IEnumerable<PluginPageInfo> GetPages()
        {
            return new[]
            {
                new PluginPageInfo
                {
                    Name = "tunein",
                    DisplayName = "TuneIn",
                    EmbeddedResourcePath = GetType().Namespace + ".Configuration.configPage.html",
                    IsMainConfigPage = true,
                    EnableInMainMenu = true,
                    EnableInUserMenu = false,
                    MenuSection = "advanced",
                    MenuIcon = "radio"
                },
                new PluginPageInfo
                {
                    Name = "tuneinjs",
                    EmbeddedResourcePath = GetType().Namespace + ".Configuration.configPage.js"
                }
            };
        }

        public override void UpdateConfiguration(BasePluginConfiguration configuration)
        {
            var value = (PluginConfiguration)configuration;
            ConfigurationSnapshot.Validate(value);
            lock (configurationGate)
            {
                // These values are server-owned; clients cannot replace them when saving settings.
                value.NavigationSecret = Configuration.NavigationSecret;
                value.HasStartedCatalogRefresh = Configuration.HasStartedCatalogRefresh;
                TuneInRefreshService.UpdateConfiguration(() => SettingsLock.SaveConfiguration(() =>
                {
                    var previousSaved = value.SettingsSaved;
                    value.SettingsSaved = true;
                    try { base.UpdateConfiguration(value); }
                    catch
                    {
                        value.SettingsSaved = previousSaved;
                        throw;
                    }
                    TuneInPlayback.CancelChangedScopes();
                }));
            }
        }

        internal void MarkCatalogRefreshStarted()
        {
            lock (configurationGate)
            {
                if (Configuration.HasStartedCatalogRefresh) return;
                var value = Configuration;
                value.HasStartedCatalogRefresh = true;
                // Persist server-owned history without resetting active refresh state or requiring unlocked settings.
                base.UpdateConfiguration(value);
            }
        }

        private Guid _id = new Guid("10b82431-f939-4507-a8e2-9509dd3fd6ae");
        public override Guid Id
        {
            get { return _id; }
        }

        /// <summary>
        /// Gets the name of the plugin
        /// </summary>
        /// <value>The name.</value>
        public override string Name
        {
            get { return "TuneIn"; }
        }

        /// <summary>
        /// Gets the description.
        /// </summary>
        /// <value>The description.</value>
        public override string Description
        {
            get
            {
                return "TuneIn streams in your collection.";
            }
        }

        public Stream GetThumbImage() => TuneInCategoryArtwork.Open(TuneInCategoryArtwork.ChannelArtwork);

        public ImageFormat ThumbImageFormat
        {
            get
            {
                return ImageFormat.Png;
            }
        }

        /// <summary>
        /// Gets the instance.
        /// </summary>
        /// <value>The instance.</value>
        public static Plugin Instance { get; private set; }
    }
}
