using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using DiskCleanupAssistant.Models;
using DiskCleanupAssistant.Rules;
using DiskCleanupAssistant.Scanning;
using Microsoft.Win32;

namespace DiskCleanupAssistant.Uninstall
{
    public sealed class InstalledAppProvider
    {
        private const string UninstallPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";

        public List<InstalledAppRecord> GetInstalledApps()
        {
            var results = new List<InstalledAppRecord>();
            ReadHive(RegistryHive.LocalMachine, RegistryView.Registry64, results);
            ReadHive(RegistryHive.LocalMachine, RegistryView.Registry32, results);
            ReadHive(RegistryHive.CurrentUser, RegistryView.Registry64, results);
            ReadHive(RegistryHive.CurrentUser, RegistryView.Registry32, results);
            return results
                .Where(a => !string.IsNullOrWhiteSpace(a.DisplayName) && !string.IsNullOrWhiteSpace(a.UninstallString))
                .GroupBy(a => (a.DisplayName + "|" + a.DisplayVersion + "|" + a.UninstallString).ToLowerInvariant())
                .Select(g => g.First())
                .OrderByDescending(a => a.EstimatedSizeBytes)
                .ThenBy(a => a.DisplayName, StringComparer.CurrentCultureIgnoreCase)
                .ToList();
        }

        public Process StartUninstall(InstalledAppRecord app)
        {
            if (app == null) throw new ArgumentNullException("app");
            var raw = !string.IsNullOrWhiteSpace(app.QuietUninstallString) ? app.QuietUninstallString : app.UninstallString;
            if (string.IsNullOrWhiteSpace(raw)) throw new InvalidOperationException("软件没有登记卸载命令");

            string executable;
            string arguments;
            SplitCommandLine(raw, out executable, out arguments);
            if (app.WindowsInstaller || Path.GetFileName(executable).Equals("msiexec.exe", StringComparison.OrdinalIgnoreCase))
            {
                executable = Path.Combine(Environment.SystemDirectory, "msiexec.exe");
                arguments = Regex.Replace(arguments, @"(^|\s)/[iI](?=\s*\{)", "$1/X ");
            }

            if (!Path.IsPathRooted(executable))
            {
                var systemCandidate = Path.Combine(Environment.SystemDirectory, executable);
                if (File.Exists(systemCandidate)) executable = systemCandidate;
            }
            if (!File.Exists(executable)) throw new FileNotFoundException("卸载程序不存在", executable);

            return Process.Start(new ProcessStartInfo(executable, arguments)
            {
                UseShellExecute = true,
                WorkingDirectory = Path.GetDirectoryName(executable)
            });
        }

        public Task<List<CandidateRecord>> FindResidualsAsync(InstalledAppRecord app, CancellationToken token)
        {
            return Task.Run(() => FindResiduals(app, token), token);
        }

        private List<CandidateRecord> FindResiduals(InstalledAppRecord app, CancellationToken token)
        {
            var candidates = new List<CandidateRecord>();
            var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (!string.IsNullOrWhiteSpace(app.InstallLocation)) paths.Add(Environment.ExpandEnvironmentVariables(app.InstallLocation.Trim('"')));
            var names = BuildNameHints(app.DisplayName, app.Publisher);
            var parents = new[]
            {
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData)
            };
            foreach (var parent in parents)
            {
                if (!Directory.Exists(parent)) continue;
                foreach (var directory in Directory.EnumerateDirectories(parent))
                {
                    var normalized = NormalizeName(Path.GetFileName(directory));
                    if (names.Any(n => n.Length >= 4 && (normalized.Contains(n) || n.Contains(normalized)))) paths.Add(directory);
                }
            }

            foreach (var path in paths.Where(Directory.Exists))
            {
                token.ThrowIfCancellationRequested();
                long examined = 0;
                var denied = 0;
                var stats = CandidateScanner.MeasureTree(path, token, ref examined, ref denied);
                candidates.Add(new CandidateRecord
                {
                    Id = "R" + (candidates.Count + 1).ToString("000"), Path = path, Paths = new List<string>(), Kind = CandidateKind.Directory,
                    SizeBytes = stats.SizeBytes, Category = CandidateCategory.SoftwareResidual, Risk = RiskLevel.High,
                    Confidence = string.Equals(path, app.InstallLocation, StringComparison.OrdinalIgnoreCase) ? ConfidenceLevel.High : ConfidenceLevel.Medium,
                    SelectionTier = SelectionTier.Never, RecommendedAction = ActionKind.None, Owner = app.DisplayName,
                    Identity = app.DisplayName + " 的疑似卸载残留", SuspectedPurpose = "可能包含配置、缓存、更新器或用户数据",
                    Evidence = string.Equals(path, app.InstallLocation, StringComparison.OrdinalIgnoreCase) ? "软件登记的安装位置在卸载后仍存在" : "AppData目录名与软件或发布者名称相似",
                    DeletionImpact = "可能丢失设置、存档或用户数据，因此不会自动选择",
                    Recommendation = "先查看第一层内容，确认不再使用该软件后再判断", ModifiedUtc = stats.LatestWriteUtc,
                    SizeComplete = stats.Complete, Fingerprint = CandidateScanner.BuildFingerprint(path, stats.SizeBytes, stats.LatestWriteUtc), RuleId = "uninstall-residual"
                });
            }
            return candidates;
        }

        public static void SplitCommandLine(string command, out string executable, out string arguments)
        {
            command = command.Trim();
            if (command.StartsWith("\""))
            {
                var end = command.IndexOf('"', 1);
                if (end < 1) throw new FormatException("卸载命令引号不完整");
                executable = command.Substring(1, end - 1);
                arguments = command.Substring(end + 1).Trim();
            }
            else
            {
                var exeIndex = command.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
                if (exeIndex >= 0)
                {
                    executable = command.Substring(0, exeIndex + 4).Trim();
                    arguments = command.Substring(exeIndex + 4).Trim();
                }
                else
                {
                    var firstSpace = command.IndexOf(' ');
                    executable = firstSpace < 0 ? command : command.Substring(0, firstSpace);
                    arguments = firstSpace < 0 ? string.Empty : command.Substring(firstSpace + 1).Trim();
                }
            }
        }

        private static HashSet<string> BuildNameHints(string displayName, string publisher)
        {
            var values = new[] { displayName, publisher };
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var value in values)
            {
                var normalized = NormalizeName(value);
                if (normalized.Length >= 4) set.Add(normalized);
                foreach (var token in Regex.Split(value ?? string.Empty, @"[^\p{L}\p{N}]+"))
                {
                    var item = NormalizeName(token);
                    if (item.Length >= 5) set.Add(item);
                }
            }
            return set;
        }

        private static string NormalizeName(string value)
        {
            return Regex.Replace(value ?? string.Empty, @"[^\p{L}\p{N}]", string.Empty).ToLowerInvariant();
        }

        private static void ReadHive(RegistryHive hive, RegistryView view, List<InstalledAppRecord> output)
        {
            try
            {
                using (var baseKey = RegistryKey.OpenBaseKey(hive, view))
                using (var root = baseKey.OpenSubKey(UninstallPath))
                {
                    if (root == null) return;
                    foreach (var keyName in root.GetSubKeyNames())
                    {
                        using (var key = root.OpenSubKey(keyName))
                        {
                            if (key == null || Convert.ToInt32(key.GetValue("SystemComponent", 0)) == 1) continue;
                            long size = 0;
                            var rawSize = key.GetValue("EstimatedSize");
                            if (rawSize != null) long.TryParse(rawSize.ToString(), out size);
                            output.Add(new InstalledAppRecord
                            {
                                DisplayName = key.GetValue("DisplayName") as string,
                                DisplayVersion = key.GetValue("DisplayVersion") as string,
                                Publisher = key.GetValue("Publisher") as string,
                                InstallLocation = key.GetValue("InstallLocation") as string,
                                UninstallString = key.GetValue("UninstallString") as string,
                                QuietUninstallString = key.GetValue("QuietUninstallString") as string,
                                EstimatedSizeBytes = size * 1024,
                                WindowsInstaller = Convert.ToInt32(key.GetValue("WindowsInstaller", 0)) == 1,
                                RegistryKeyName = keyName
                            });
                        }
                    }
                }
            }
            catch (UnauthorizedAccessException) { }
            catch (System.Security.SecurityException) { }
        }
    }
}
