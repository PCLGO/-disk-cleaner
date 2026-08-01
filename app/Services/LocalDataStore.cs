using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Serialization.Json;
using DiskCleanupAssistant.Models;

namespace DiskCleanupAssistant.Services
{
    public sealed class LocalDataStore
    {
        public static readonly string Root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DiskCleanupAssistant");
        private static readonly string HistoryPath = Path.Combine(Root, "history.json");
        private static readonly string SettingsPath = Path.Combine(Root, "settings.json");

        public LocalDataStore()
        {
            Directory.CreateDirectory(Root);
        }

        public List<CleanupHistoryEntry> LoadHistory()
        {
            try
            {
                if (!File.Exists(HistoryPath)) return new List<CleanupHistoryEntry>();
                using (var stream = File.OpenRead(HistoryPath))
                    return ((List<CleanupHistoryEntry>)new DataContractJsonSerializer(typeof(List<CleanupHistoryEntry>)).ReadObject(stream))
                        .Where(x => x.TimestampUtc >= DateTime.UtcNow.AddDays(-LoadSettings().HistoryRetentionDays)).OrderByDescending(x => x.TimestampUtc).ToList();
            }
            catch { return new List<CleanupHistoryEntry>(); }
        }

        public void AppendResults(IEnumerable<ActionResult> results)
        {
            var history = LoadHistory();
            history.InsertRange(0, results.Select(r => new CleanupHistoryEntry
            {
                TimestampUtc = DateTime.UtcNow,
                Path = r.Path,
                Status = r.Status == ActionStatus.Success ? "成功" : r.Status == ActionStatus.Skipped ? "跳过" : "失败",
                Message = r.Message,
                EstimatedBytes = r.EstimatedBytes
            }));
            history = history.Where(x => x.TimestampUtc >= DateTime.UtcNow.AddDays(-LoadSettings().HistoryRetentionDays)).Take(1000).ToList();
            using (var stream = File.Create(HistoryPath))
                new DataContractJsonSerializer(typeof(List<CleanupHistoryEntry>)).WriteObject(stream, history);
        }

        public void SaveSnapshot(ScanSnapshot snapshot)
        {
            var dir = Path.Combine(Root, "Scans");
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, "latest.json");
            using (var stream = File.Create(path))
                new DataContractJsonSerializer(typeof(ScanSnapshot)).WriteObject(stream, snapshot);
        }

        public AppSettings LoadSettings()
        {
            try
            {
                if (!File.Exists(SettingsPath)) return AppSettings.CreateDefault();
                using (var stream = File.OpenRead(SettingsPath))
                {
                    var value = (AppSettings)new DataContractJsonSerializer(typeof(AppSettings)).ReadObject(stream);
                    if (value.LargeFileThresholdBytes <= 0) value.LargeFileThresholdBytes = 512L * 1024 * 1024;
                    if (value.DuplicateFileThresholdBytes <= 0) value.DuplicateFileThresholdBytes = 50L * 1024 * 1024;
                    if (value.HistoryRetentionDays < 1) value.HistoryRetentionDays = 30;
                    return value;
                }
            }
            catch { return AppSettings.CreateDefault(); }
        }

        public void SaveSettings(AppSettings settings)
        {
            Directory.CreateDirectory(Root);
            using (var stream = File.Create(SettingsPath))
                new DataContractJsonSerializer(typeof(AppSettings)).WriteObject(stream, settings);
        }

        public void ClearAll()
        {
            if (Directory.Exists(Root)) Directory.Delete(Root, true);
            Directory.CreateDirectory(Root);
        }
    }
}
