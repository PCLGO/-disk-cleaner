using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using DiskCleanupAssistant.Models;
using DiskCleanupAssistant.Rules;
using DiskCleanupAssistant.WindowsIntegration;

namespace DiskCleanupAssistant.Scanning
{
    public sealed class CandidateScanner
    {
        private readonly RuleEngine _rules;

        public CandidateScanner(RuleEngine rules)
        {
            _rules = rules;
        }

        public Task<ScanSnapshot> QuickScanAsync(IProgress<ScanProgress> progress, CancellationToken token)
        {
            return QuickScanAsync(null, progress, null, token);
        }

        public Task<ScanSnapshot> QuickScanAsync(IProgress<ScanProgress> progress, ScanPauseController pause, CancellationToken token)
        {
            return QuickScanAsync(null, progress, pause, token);
        }

        public Task<ScanSnapshot> QuickScanAsync(IEnumerable<string> roots, IProgress<ScanProgress> progress,
            ScanPauseController pause, CancellationToken token)
        {
            var selectedRoots = roots == null ? null : roots.Select(NormalizeDriveRoot)
                .Where(root => !string.IsNullOrWhiteSpace(root))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            return Task.Run(() => QuickScan(selectedRoots, progress, pause, token), token);
        }

        public Task<ScanSnapshot> DeepScanAsync(IEnumerable<string> roots, long largeFileThreshold,
            IProgress<ScanProgress> progress, CancellationToken token)
        {
            return DeepScanAsync(roots, largeFileThreshold, progress, null, token);
        }

        public Task<ScanSnapshot> DeepScanAsync(IEnumerable<string> roots, long largeFileThreshold,
            IProgress<ScanProgress> progress, ScanPauseController pause, CancellationToken token)
        {
            return Task.Run(() => DeepScan(roots, largeFileThreshold, progress, pause, token), token);
        }

        private ScanSnapshot QuickScan(IReadOnlyList<string> selectedRoots, IProgress<ScanProgress> progress, ScanPauseController pause, CancellationToken token)
        {
            var watch = Stopwatch.StartNew();
            var candidates = new List<CandidateRecord>();
            long examined = 0;
            var denied = 0;
            var targets = _rules.Rules
                .SelectMany(rule => _rules.ExpandPaths(rule).Select(path => new KeyValuePair<CleanupRule, string>(rule, path)))
                .Where(target => selectedRoots == null || selectedRoots.Any(root => IsOnDrive(target.Value, root)))
                .ToList();
            var systemRoot = NormalizeDriveRoot(Environment.GetFolderPath(Environment.SpecialFolder.Windows));
            var discoveryRoots = selectedRoots == null
                ? new List<string>()
                : selectedRoots.Where(root => !string.Equals(root, systemRoot, StringComparison.OrdinalIgnoreCase)).ToList();
            var totalSteps = targets.Count + discoveryRoots.Count;
            if (progress != null) progress.Report(new ScanProgress { CurrentPath = "准备扫描", CompletedSteps = 0, TotalSteps = totalSteps });
            for (var targetIndex = 0; targetIndex < targets.Count; targetIndex++)
            {
                WaitIfPaused(pause, token);
                token.ThrowIfCancellationRequested();
                var rule = targets[targetIndex].Key;
                var root = targets[targetIndex].Value;
                try
                {
                    if (string.IsNullOrWhiteSpace(root) ||
                        (!NativeMethods.DirectoryExists(root) && !NativeMethods.FileExists(root))) continue;
                    if (NativeMethods.IsReparsePoint(root)) continue;

                    var wholeRoot = string.Equals(rule.ScanMode, "WholeRoot", StringComparison.OrdinalIgnoreCase);
                    if (wholeRoot || string.Equals(rule.Action, "OfficialTool", StringComparison.OrdinalIgnoreCase))
                    {
                        TreeStats rootStats;
                        try { rootStats = MeasureTree(root, token, pause, ref examined, ref denied); }
                        catch { denied++; continue; }
                        var cutoff = DateTime.UtcNow.AddDays(-Math.Max(0, rule.MinimumAgeDays));
                        var oldEnough = rule.MinimumAgeDays <= 0 ||
                                        (rootStats.LatestWriteUtc.HasValue && rootStats.LatestWriteUtc.Value <= cutoff);
                        if (rootStats.SizeBytes > 0 && oldEnough)
                        {
                            var found = CreateFromRule(rule, root, rootStats);
                            candidates.Add(found);
                            if (progress != null) progress.Report(new ScanProgress { CurrentPath = root, EntriesExamined = examined, CandidatesFound = candidates.Count, PermissionSkips = denied, FoundCandidate = found, CompletedSteps = targetIndex, TotalSteps = totalSteps });
                        }
                        continue;
                    }

                    IEnumerable<string> children;
                    try { children = NativeMethods.EnumerateFileSystemEntriesLongPath(root).ToArray(); }
                    catch (UnauthorizedAccessException) { denied++; continue; }
                    catch (IOException) { denied++; continue; }

                    foreach (var child in children)
                    {
                        WaitIfPaused(pause, token);
                        token.ThrowIfCancellationRequested();
                        examined++;
                        if (NativeMethods.IsReparsePoint(child)) continue;
                        TreeStats stats;
                        try { stats = MeasureTree(child, token, pause, ref examined, ref denied); }
                        catch { denied++; continue; }
                        if (stats.SizeBytes <= 0 || !stats.LatestWriteUtc.HasValue) continue;
                        var cutoff = DateTime.UtcNow.AddDays(-Math.Max(0, rule.MinimumAgeDays));
                        if (stats.LatestWriteUtc.Value > cutoff && rule.Category != "SystemManaged") continue;

                        var item = CreateFromRule(rule, child, stats);
                        candidates.Add(item);
                        if (progress != null) progress.Report(new ScanProgress
                        {
                            CurrentPath = child,
                            EntriesExamined = examined,
                            CandidatesFound = candidates.Count,
                            PermissionSkips = denied,
                            FoundCandidate = item,
                            CompletedSteps = targetIndex,
                            TotalSteps = totalSteps
                        });
                    }
                }
                finally
                {
                    if (progress != null) progress.Report(new ScanProgress
                    {
                        CurrentPath = string.IsNullOrWhiteSpace(root) ? rule.Id : root,
                        EntriesExamined = examined,
                        CandidatesFound = candidates.Count,
                        PermissionSkips = denied,
                        CompletedSteps = targetIndex + 1,
                        TotalSteps = totalSteps
                    });
                }
            }
            for (var rootIndex = 0; rootIndex < discoveryRoots.Count; rootIndex++)
            {
                ScanNonSystemDriveHints(discoveryRoots[rootIndex], candidates, progress, pause, token,
                    ref examined, ref denied, targets.Count + rootIndex, totalSteps);
                if (progress != null) progress.Report(new ScanProgress
                {
                    CurrentPath = discoveryRoots[rootIndex],
                    EntriesExamined = examined,
                    CandidatesFound = candidates.Count,
                    PermissionSkips = denied,
                    CompletedSteps = targets.Count + rootIndex + 1,
                    TotalSteps = totalSteps
                });
            }
            candidates = candidates.GroupBy(candidate => candidate.Path, StringComparer.OrdinalIgnoreCase).Select(group => group.First()).ToList();
            AssignIds(candidates);
            watch.Stop();
            return Snapshot(selectedRoots == null ? new[] { "已知缓存位置" } : selectedRoots, candidates, examined, denied, watch.Elapsed, true);
        }

        private void ScanNonSystemDriveHints(string root, List<CandidateRecord> candidates, IProgress<ScanProgress> progress,
            ScanPauseController pause, CancellationToken token, ref long examined, ref int denied, int completedSteps, int totalSteps)
        {
            var cacheNames = new HashSet<string>(new[]
            {
                "cache", "caches", "gpucache", "code cache", "shadercache", "dxcache", "glcache",
                "nv_cache", "logs", "crashdumps", "temp", "tmp"
            }, StringComparer.OrdinalIgnoreCase);
            var excludedTopLevel = new HashSet<string>(new[]
            {
                "$RECYCLE.BIN", "System Volume Information", "Recovery", "WindowsApps", "WpSystem",
                "Program Files", "Program Files (x86)", "SteamLibrary", "XboxGames", "Games", "ModifiableWindowsApps"
            }, StringComparer.OrdinalIgnoreCase);
            var stack = new Stack<KeyValuePair<string, int>>();
            stack.Push(new KeyValuePair<string, int>(root, 0));
            while (stack.Count > 0)
            {
                WaitIfPaused(pause, token);
                token.ThrowIfCancellationRequested();
                var current = stack.Pop();
                if (current.Value > 3 || NativeMethods.IsReparsePoint(current.Key)) continue;
                IEnumerable<string> entries;
                try { entries = NativeMethods.EnumerateFileSystemEntriesLongPath(current.Key).ToArray(); }
                catch (UnauthorizedAccessException) { denied++; continue; }
                catch (IOException) { denied++; continue; }

                foreach (var entry in entries)
                {
                    WaitIfPaused(pause, token);
                    token.ThrowIfCancellationRequested();
                    examined++;
                    try
                    {
                        if (NativeMethods.IsReparsePoint(entry)) continue;
                        if (NativeMethods.DirectoryExists(entry))
                        {
                            var name = Path.GetFileName(entry);
                            if (current.Value == 0 && excludedTopLevel.Contains(name)) continue;
                            string protectionReason;
                            var protectedPath = _rules.IsProtectedForCleanup(entry, out protectionReason) || _rules.IsUserLibrary(entry);
                            if (!protectedPath && cacheNames.Contains(name) && !_rules.IsBundledApplicationDependency(entry))
                            {
                                var localExamined = 0L;
                                var localDenied = 0;
                                var stats = MeasureTree(entry, token, pause, ref localExamined, ref localDenied);
                                examined += localExamined;
                                denied += localDenied;
                                if (stats.SizeBytes >= 10L * 1024 * 1024)
                                {
                                    var found = CreateGenericDirectory(entry, name, stats);
                                    candidates.Add(found);
                                    if (progress != null) progress.Report(new ScanProgress
                                    {
                                        CurrentPath = entry, EntriesExamined = examined, CandidatesFound = candidates.Count,
                                        PermissionSkips = denied, FoundCandidate = found, CompletedSteps = completedSteps, TotalSteps = totalSteps
                                    });
                                }
                                continue;
                            }
                            if (!protectedPath && current.Value < 3) stack.Push(new KeyValuePair<string, int>(entry, current.Value + 1));
                        }
                        else if (NativeMethods.FileExists(entry))
                        {
                            var info = NativeMethods.GetMetadata(entry);
                            if (info.Length < 512L * 1024 * 1024) continue;
                            string protectionReason;
                            var protectedFile = _rules.IsProtectedForCleanup(entry, out protectionReason) || _rules.IsUserLibrary(entry);
                            var found = CreateLargeFile(entry, info, protectedFile, protectionReason);
                            candidates.Add(found);
                            if (progress != null) progress.Report(new ScanProgress
                            {
                                CurrentPath = entry, EntriesExamined = examined, CandidatesFound = candidates.Count,
                                PermissionSkips = denied, FoundCandidate = found, CompletedSteps = completedSteps, TotalSteps = totalSteps
                            });
                        }
                    }
                    catch (UnauthorizedAccessException) { denied++; }
                    catch (IOException) { denied++; }
                }
            }
        }

        private static string NormalizeDriveRoot(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return null;
            try { return Path.GetPathRoot(Path.GetFullPath(path)); }
            catch { return null; }
        }

        private static bool IsOnDrive(string path, string root)
        {
            var pathRoot = NormalizeDriveRoot(path);
            return !string.IsNullOrWhiteSpace(pathRoot) && string.Equals(pathRoot, root, StringComparison.OrdinalIgnoreCase);
        }

        private ScanSnapshot DeepScan(IEnumerable<string> roots, long largeFileThreshold,
            IProgress<ScanProgress> progress, ScanPauseController pause, CancellationToken token)
        {
            var watch = Stopwatch.StartNew();
            var candidates = new List<CandidateRecord>();
            var rootList = roots.Where(NativeMethods.DirectoryExists).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            var stack = new Stack<string>(rootList.Reverse<string>());
            long examined = 0;
            var denied = 0;
            var cacheNames = new HashSet<string>(new[] { "cache", "caches", "gpucache", "code cache", "shadercache", "logs", "crashdumps" }, StringComparer.OrdinalIgnoreCase);

            while (stack.Count > 0)
            {
                WaitIfPaused(pause, token);
                token.ThrowIfCancellationRequested();
                var current = stack.Pop();
                if (NativeMethods.IsReparsePoint(current)) continue;
                string protectionReason;
                var protectedPath = _rules.IsProtectedForCleanup(current, out protectionReason);
                var isUserLibrary = _rules.IsUserLibrary(current);
                if (protectedPath && !isUserLibrary && !rootList.Contains(current, StringComparer.OrdinalIgnoreCase)) continue;

                IEnumerable<string> entries;
                try { entries = NativeMethods.EnumerateFileSystemEntriesLongPath(current).ToArray(); }
                catch (UnauthorizedAccessException) { denied++; continue; }
                catch (IOException) { denied++; continue; }

                foreach (var entry in entries)
                {
                    WaitIfPaused(pause, token);
                    token.ThrowIfCancellationRequested();
                    examined++;
                    try
                    {
                        if (NativeMethods.IsReparsePoint(entry)) continue;
                        if (NativeMethods.DirectoryExists(entry))
                        {
                            var name = Path.GetFileName(entry);
                            if (!protectedPath && cacheNames.Contains(name) && !_rules.IsBundledApplicationDependency(entry))
                            {
                                var localExamined = 0L;
                                var localDenied = 0;
                                var stats = MeasureTree(entry, token, pause, ref localExamined, ref localDenied);
                                examined += localExamined;
                                denied += localDenied;
                                if (stats.SizeBytes >= 10L * 1024 * 1024)
                                {
                                    var found = CreateGenericDirectory(entry, name, stats);
                                    candidates.Add(found);
                                    if (progress != null) progress.Report(new ScanProgress { CurrentPath = entry, EntriesExamined = examined, CandidatesFound = candidates.Count, PermissionSkips = denied, FoundCandidate = found });
                                }
                            }
                            stack.Push(entry);
                        }
                        else if (NativeMethods.FileExists(entry))
                        {
                            var info = NativeMethods.GetMetadata(entry);
                            if (info.Length >= largeFileThreshold)
                            {
                                var found = CreateLargeFile(entry, info, isUserLibrary || protectedPath, protectionReason);
                                candidates.Add(found);
                                if (progress != null) progress.Report(new ScanProgress { CurrentPath = entry, EntriesExamined = examined, CandidatesFound = candidates.Count, PermissionSkips = denied, FoundCandidate = found });
                            }
                        }
                    }
                    catch (UnauthorizedAccessException) { denied++; }
                    catch (IOException) { denied++; }

                    if (examined % 1000 == 0 && progress != null)
                        progress.Report(new ScanProgress { CurrentPath = entry, EntriesExamined = examined, CandidatesFound = candidates.Count, PermissionSkips = denied });
                }
            }

            candidates = candidates.GroupBy(c => c.Path, StringComparer.OrdinalIgnoreCase).Select(g => g.First()).ToList();
            AssignIds(candidates);
            watch.Stop();
            return Snapshot(rootList, candidates, examined, denied, watch.Elapsed, true);
        }

        private CandidateRecord CreateFromRule(CleanupRule rule, string path, TreeStats stats)
        {
            var kind = NativeMethods.DirectoryExists(path) ? CandidateKind.Directory : CandidateKind.File;
            var locked = stats.HasLockedFile || (kind == CandidateKind.File && NativeMethods.IsLocked(path)) || _rules.IsRelatedProcessRunning(rule.Id);
            return new CandidateRecord
            {
                Path = path,
                Paths = new List<string>(),
                Kind = kind,
                SizeBytes = stats.SizeBytes,
                Category = RuleEngine.ParseEnum(rule.Category, CandidateCategory.Protected),
                Risk = RuleEngine.ParseEnum(rule.Risk, RiskLevel.High),
                Confidence = RuleEngine.ParseEnum(rule.Confidence, ConfidenceLevel.Low),
                SelectionTier = RuleEngine.ParseEnum(rule.SelectionTier, SelectionTier.Never),
                RecommendedAction = RuleEngine.ParseEnum(rule.Action, ActionKind.None),
                Owner = rule.Owner,
                Identity = rule.Identity,
                SuspectedPurpose = rule.Purpose,
                Evidence = rule.Evidence,
                DeletionImpact = rule.Impact,
                Recommendation = rule.Recommendation,
                ModifiedUtc = stats.LatestWriteUtc,
                SizeComplete = stats.Complete,
                RequiresElevation = rule.RequiresElevation,
                IsLocked = locked,
                Fingerprint = BuildFingerprint(path, stats.SizeBytes, stats.LatestWriteUtc),
                RuleId = rule.Id,
                IsSelected = false
            };
        }

        private CandidateRecord CreateGenericDirectory(string path, string name, TreeStats stats)
        {
            var diagnostics = name.Equals("logs", StringComparison.OrdinalIgnoreCase) || name.Equals("crashdumps", StringComparison.OrdinalIgnoreCase);
            return new CandidateRecord
            {
                Path = path,
                Paths = new List<string>(),
                Kind = CandidateKind.Directory,
                SizeBytes = stats.SizeBytes,
                Category = diagnostics ? CandidateCategory.Diagnostics : CandidateCategory.RebuildableCache,
                Risk = RiskLevel.Medium,
                Confidence = ConfidenceLevel.Low,
                SelectionTier = SelectionTier.Never,
                RecommendedAction = ActionKind.None,
                Owner = GuessOwner(path),
                Identity = GuessOwner(path) + " 路径下的“" + name + "”目录",
                SuspectedPurpose = diagnostics ? "疑似日志或崩溃诊断数据" : "疑似应用缓存；仅凭目录名不能确认",
                Evidence = "目录名称匹配常见缓存/日志模式，尚未确认软件元数据",
                DeletionImpact = "用途尚未充分确认，可能丢失诊断资料或触发重新下载",
                Recommendation = "查看第一层内容和所属软件后再决定；不会自动选择",
                ModifiedUtc = stats.LatestWriteUtc,
                SizeComplete = stats.Complete,
                IsLocked = stats.HasLockedFile,
                Fingerprint = BuildFingerprint(path, stats.SizeBytes, stats.LatestWriteUtc)
            };
        }

        private CandidateRecord CreateLargeFile(string path, FileEntryMetadata info, bool protectedFile, string protectionReason)
        {
            var ext = Path.GetExtension(path).ToLowerInvariant();
            var known = new[] { ".iso", ".7z", ".zip", ".rar", ".msi", ".exe", ".bak", ".old", ".dmp", ".part", ".crdownload" }.Contains(ext);
            return new CandidateRecord
            {
                Path = path,
                Paths = new List<string>(),
                Kind = CandidateKind.File,
                SizeBytes = NativeMethods.GetAllocatedSize(path, info.Length),
                Category = protectedFile ? CandidateCategory.Protected : CandidateCategory.LargeSuspicious,
                Risk = RiskLevel.High,
                Confidence = known ? ConfidenceLevel.Medium : ConfidenceLevel.Low,
                SelectionTier = SelectionTier.Never,
                RecommendedAction = ActionKind.OpenLocation,
                Owner = GuessOwner(path),
                Identity = known ? ext + " 大文件" : "大型文件",
                SuspectedPurpose = known ? "可能是安装包、压缩包、备份或未完成下载" : "用途未知的大文件",
                Evidence = "文件大小达到阈值；扩展名为 " + (string.IsNullOrEmpty(ext) ? "无" : ext) + "，未读取私人内容",
                DeletionImpact = protectedFile ? "位于受保护或用户资料位置，只允许查看" : "可能是唯一副本，删除前必须确认来源和备份",
                Recommendation = protectedFile ? (protectionReason ?? "打开所在位置人工判断") : "打开所在位置并确认不再需要",
                ModifiedUtc = info.LastWriteUtc,
                SizeComplete = true,
                IsLocked = NativeMethods.IsLocked(path),
                Fingerprint = BuildFingerprint(path, info.Length, info.LastWriteUtc)
            };
        }

        public static TreeStats MeasureTree(string path, CancellationToken token, ref long examined, ref int denied)
        {
            return MeasureTree(path, token, null, ref examined, ref denied);
        }

        public static TreeStats MeasureTree(string path, CancellationToken token, ScanPauseController pause, ref long examined, ref int denied)
        {
            WaitIfPaused(pause, token);
            if (NativeMethods.FileExists(path))
            {
                var file = NativeMethods.GetMetadata(path);
                examined++;
                return new TreeStats { SizeBytes = NativeMethods.GetAllocatedSize(path, file.Length), LatestWriteUtc = file.LastWriteUtc, Complete = true, HasLockedFile = NativeMethods.IsLocked(path) };
            }

            long total = 0;
            DateTime? latest = null;
            var complete = true;
            var locked = false;
            var stack = new Stack<string>();
            stack.Push(path);
            while (stack.Count > 0)
            {
                WaitIfPaused(pause, token);
                token.ThrowIfCancellationRequested();
                var current = stack.Pop();
                if (NativeMethods.IsReparsePoint(current)) continue;
                try
                {
                    foreach (var entry in NativeMethods.EnumerateFileSystemEntriesLongPath(current))
                    {
                        WaitIfPaused(pause, token);
                        token.ThrowIfCancellationRequested();
                        examined++;
                        try
                        {
                            if (NativeMethods.IsReparsePoint(entry)) continue;
                            if (NativeMethods.DirectoryExists(entry)) stack.Push(entry);
                            else
                            {
                                var info = NativeMethods.GetMetadata(entry);
                                total += NativeMethods.GetAllocatedSize(entry, info.Length);
                                if (!locked && NativeMethods.IsLocked(entry)) locked = true;
                                if (!latest.HasValue || info.LastWriteUtc > latest.Value) latest = info.LastWriteUtc;
                            }
                        }
                        catch { denied++; complete = false; }
                    }
                }
                catch { denied++; complete = false; }
            }
            try
            {
                var dirTime = NativeMethods.GetMetadata(path).LastWriteUtc;
                if (!latest.HasValue || dirTime > latest.Value) latest = dirTime;
            }
            catch { }
            return new TreeStats { SizeBytes = total, LatestWriteUtc = latest, Complete = complete, HasLockedFile = locked };
        }

        public static string BuildFingerprint(string path, long size, DateTime? modifiedUtc)
        {
            var raw = Path.GetFullPath(path).ToUpperInvariant() + "|" + size + "|" + (modifiedUtc.HasValue ? modifiedUtc.Value.Ticks : 0);
            using (var sha = SHA256.Create())
                return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(raw))).Replace("-", string.Empty);
        }

        private static string GuessOwner(string path)
        {
            var ignored = new HashSet<string>(new[] { "cache", "caches", "logs", "temp", "tmp", "crashdumps", "appdata", "local", "roaming", "users" }, StringComparer.OrdinalIgnoreCase);
            var parts = path.Split(Path.DirectorySeparatorChar);
            for (var i = parts.Length - 2; i >= 0; i--)
                if (!string.IsNullOrWhiteSpace(parts[i]) && !ignored.Contains(parts[i]) && !parts[i].EndsWith(":")) return parts[i];
            return "未知软件";
        }

        private static void AssignIds(List<CandidateRecord> candidates)
        {
            candidates.Sort((a, b) => b.SizeBytes.CompareTo(a.SizeBytes));
            var counters = new Dictionary<char, int>();
            foreach (var item in candidates)
            {
                var root = Path.GetPathRoot(item.Path);
                var prefix = !string.IsNullOrWhiteSpace(root) ? char.ToUpperInvariant(root[0]) : 'X';
                if (!counters.ContainsKey(prefix)) counters[prefix] = 0;
                counters[prefix]++;
                item.Id = prefix + counters[prefix].ToString("000");
            }
        }

        private static ScanSnapshot Snapshot(IEnumerable<string> roots, List<CandidateRecord> candidates, long examined, int denied, TimeSpan elapsed, bool complete)
        {
            return new ScanSnapshot
            {
                ScanId = Guid.NewGuid().ToString("N"),
                GeneratedUtc = DateTime.UtcNow,
                Roots = roots.ToList(),
                Complete = complete,
                PermissionSkips = denied,
                EntriesExamined = examined,
                ElapsedSeconds = elapsed.TotalSeconds,
                Candidates = candidates
            };
        }

        private static void WaitIfPaused(ScanPauseController pause, CancellationToken token)
        {
            if (pause != null) pause.Wait(token);
        }
    }

    public sealed class TreeStats
    {
        public long SizeBytes { get; set; }
        public DateTime? LatestWriteUtc { get; set; }
        public bool Complete { get; set; }
        public bool HasLockedFile { get; set; }
    }
}
