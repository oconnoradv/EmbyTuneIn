using System;
using System.Collections.Generic;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;
using MediaBrowser.Plugins.TuneIn.Configuration;
using System.IO;
using MediaBrowser.Model.Drawing;
using MediaBrowser.Controller;
using System.Reflection;

namespace MediaBrowser.Plugins.TuneIn
{
    /// <summary>
    /// Class Plugin
    /// </summary>
    public class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages, IHasThumbImage
    {
        internal static string ReleaseVersion =>
            typeof(Plugin).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>().InformationalVersion.Split('+')[0];
        internal static string ConfigurationControllerName => "tuneinforkjs" + ReleaseVersion.Replace(".", "").Replace("-", "");
        internal static string ConfigurationPageName => "tuneinfork" + ReleaseVersion.Replace(".", "").Replace("-", "");

        public Plugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer, IServerApplicationHost applicationHost)
            : base(applicationPaths, xmlSerializer)
        {
            if (!SupportsServerVersion(applicationHost.ApplicationVersion))
                throw new NotSupportedException("TuneIn requires Emby Server 4.9.1.90+ or patched beta 4.9.2.7+.");
            Instance = this;
            RefreshState = new TuneInRefreshStateStore(Path.Combine(applicationPaths.DataPath, "tunein-fork-refresh.xml"));
            SettingsLock = new TuneInSettingsLockStore(Path.Combine(applicationPaths.DataPath, "tunein-fork-settings-lock.xml"));
            TuneInRefresh.Instance.UseHistoryStore(RefreshState);
        }

        internal TuneInRefreshStateStore RefreshState { get; }
        internal TuneInSettingsLockStore SettingsLock { get; }

        internal static bool SupportsServerVersion(Version version) =>
            version != null && version >= new Version(4, 9, 1, 90)
                && (version < new Version(4, 9, 2, 0) || version >= new Version(4, 9, 2, 7));

        public override void UpdateConfiguration(BasePluginConfiguration configuration)
        {
            TuneInRefresh.Instance.UpdateConfiguration(() => SettingsLock.IsLocked, () =>
            {
                ConfigurationSnapshot.Validate((PluginConfiguration)configuration);
                base.UpdateConfiguration(configuration);
                TuneInDirectory.Instance.Reset();
            });
        }

        public IEnumerable<PluginPageInfo> GetPages()
        {
            return new[]
            {
                new PluginPageInfo
                {
                    Name = ConfigurationPageName,
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
                    Name = ConfigurationControllerName,
                    EmbeddedResourcePath = GetType().Namespace + ".Configuration.configPage.js",
                    IsMainConfigPage = false
                }
            };
        }

        private readonly Guid _id = new Guid("d9314bc3-33e0-4080-88ef-8e7e718524f2");
        public override string ConfigurationFileName => "MediaBrowser.Plugins.TuneIn.Fork.xml";
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
            get { return "TuneIn (oconnoradv fork)"; }
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

        public Stream GetThumbImage()
        {
            var type = GetType();
            return type.Assembly.GetManifestResourceStream(type.Namespace + ".Images.thumb.png");
        }

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
