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
        public String Username { get; set; }
        public String LatLon { get; set; }
        public string NavigationSecret { get; set; } = CreateSecret();
        public int ApiRequestIntervalSeconds { get; set; } = 3;
        public List<CategoryRefreshSchedule> RefreshSchedules { get; set; } = new List<CategoryRefreshSchedule>();
        public int SchemaVersion { get; set; }
        public bool ConfigurationCompleted { get; set; }
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
