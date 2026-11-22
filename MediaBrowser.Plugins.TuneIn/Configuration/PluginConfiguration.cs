using MediaBrowser.Model.Plugins;
using System;
using System.Collections.Generic;
using System.Security.Cryptography;

namespace MediaBrowser.Plugins.TuneIn.Configuration
{
    /// <summary>
    /// Class PluginConfiguration
    /// </summary>
    public class PluginConfiguration : BasePluginConfiguration
    {
        internal const int MinApiRequestIntervalSeconds = 1;
        internal const int MaxApiRequestIntervalSeconds = 30;
        internal const int MinDiscoveryDeadlineHours = 1;
        internal const int MaxDiscoveryDeadlineHours = 10;
        internal const int MinImportDeadlineHours = 1;
        internal const int MaxImportDeadlineHours = 100;

        public String Username { get; set; }
        public String LatLon { get; set; }
        public string NavigationSecret { get; set; } = CreateSecret();
        public int ApiRequestIntervalSeconds { get; set; } = 2;
        public int DiscoveryDeadlineHours { get; set; } = 3;
        public int ImportDeadlineHours { get; set; } = 24;
        public bool EnableCatalogIdentityDiagnostics { get; set; }
        public bool HideRegionalGenres { get; set; } = true;
        public bool AllowPartialCatalogImport { get; set; } = true;
        public List<CategoryRefreshSchedule> RefreshSchedules { get; set; } = new List<CategoryRefreshSchedule>();
        public int SchemaVersion { get; set; }
        public bool ConfigurationCompleted { get; set; }
        public bool HasStartedCatalogRefresh { get; set; }
        private bool? settingsSaved;
        public bool SettingsSaved
        {
            get => settingsSaved ?? (SchemaVersion == 1);
            set => settingsSaved = value;
        }
        public bool EnableLocalRadio { get; set; }
        public bool EnableMusic { get; set; }
        public bool EnableLanguage { get; set; }
        public bool EnableLocation { get; set; }
        public bool EnableTalk { get; set; }
        public bool EnableSports { get; set; }
        public bool EnablePodcasts { get; set; }
        public bool EnableFavorites { get; set; }
        public bool UseCoordinates { get; set; }
        public string LocalRadioSource { get; set; }
        public List<CatalogSelection> Languages { get; set; } = new List<CatalogSelection>();
        public List<CatalogSelection> Locations { get; set; } = new List<CatalogSelection>();
        public List<CatalogSelection> Music { get; set; } = new List<CatalogSelection>();
        public List<CatalogSelection> LocalCities { get; set; } = new List<CatalogSelection>();
        public string LanguageMode { get; set; }
        public string LocationMode { get; set; }
        public string MusicMode { get; set; }
        public string LocalMode { get; set; }
        public string TalkMode { get; set; } = "all";
        public List<string> TalkIds { get; set; } = new List<string>();
        public string SportsMode { get; set; } = "all";
        public List<string> SportsIds { get; set; } = new List<string>();
        public string PodcastsMode { get; set; } = "all";
        public List<string> PodcastIds { get; set; } = new List<string>();

        private static string CreateSecret()
        {
            var bytes = new byte[32];
            using (var random = RandomNumberGenerator.Create()) random.GetBytes(bytes);
            return Convert.ToBase64String(bytes);
        }
    }

    public class CategoryRefreshSchedule
    {
        public string Category { get; set; }
        public int IntervalHours { get; set; }
    }

    public class CatalogSelection
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public string Path { get; set; }
        public List<string> AncestorIds { get; set; } = new List<string>();
        public bool Enabled { get; set; } = true;
    }
}
