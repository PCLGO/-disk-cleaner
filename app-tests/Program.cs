using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using DiskCleanupAssistant.Cleanup;
using DiskCleanupAssistant.Models;
using DiskCleanupAssistant.Rules;
using DiskCleanupAssistant.Scanning;
using DiskCleanupAssistant.Uninstall;
using DiskCleanupAssistant.Updates;

namespace DiskCleanupAssistant.Tests
{
    internal static class Program
    {
        private static int _passed;
        private static int _failed;

        private static int Main()
        {
            AppContext.SetSwitch("Switch.System.IO.UseLegacyPathHandling", false);
            AppContext.SetSwitch("Switch.System.IO.BlockLongPaths", false);
            Run("内嵌规则可以加载", RulesLoad);
            Run("近期内容不会让明确缓存目录从快速扫描消失", WholeRootCacheIsReported);
            Run("快速扫描进度按规则数量真实推进", QuickScanReportsProgress);
            Run("快速扫描严格遵守所选磁盘", QuickScanHonorsSelectedDrive);
            Run("规则族可以展开多配置与通配目录", RuleFamiliesExpandProfiles);
            Run("关联软件运行时缓存不会进入保守选择", RunningOwnerBlocksConservativeSelection);
            Run("系统和仓库路径受保护", ProtectedPaths);
            Run("模型权重扩展名受保护", ModelFilesAreProtected);
            Run("保守选择仅选高置信可恢复垃圾", ConservativeSelection);
            Run("平衡选择不会选择残留和大文件", BalancedNeverSelectsHighRisk);
            Run("应用内置 node_modules 不作为项目缓存", BundledDependency);
            Run("卸载命令解析保留参数", UninstallCommandParsing);
            Run("重复文件必须通过完整内容哈希", ExactDuplicates);
            Run("扫描后变化的候选会被跳过", StaleCandidateIsSkipped);
            Run("所有新候选默认不勾选", CandidatesDefaultToUnselected);
            Run("判断依据明确标注本地证据来源", EvidenceSourceIsExplicit);
            Run("永久删除项永不自动勾选", PermanentItemsAreNeverAutoSelected);
            Run("暂停状态下取消能在两秒内生效", PausedScanCancelsPromptly);
            Run("未注入公钥时更新检查安全禁用", PlaceholderUpdateKeyDisablesChecks);
            Run("Win32枚举可处理超过260字符的路径", LongPathEnumeration);
            Run("占用中的文件会被跳过", LockedCandidateIsSkipped);
            Run("包含占用文件的目录会被跳过", DirectoryWithLockedFileIsSkipped);

            Console.WriteLine("通过: " + _passed + "  失败: " + _failed);
            return _failed == 0 ? 0 : 1;
        }

        private static void RulesLoad()
        {
            var rules = new RuleEngine();
            Assert(rules.Rules.Count >= 50, "离线规则族覆盖不足");
            Assert(rules.Rules.Sum(rule => (string.IsNullOrWhiteSpace(rule.PathTemplate) ? 0 : 1) +
                                           (rule.PathTemplates == null ? 0 : rule.PathTemplates.Count)) >= 130,
                "规则族展开模板覆盖不足");
            Assert(rules.Rules.Any(r => r.Id == "windows-update" && r.Action == "OfficialTool"), "系统托管规则缺失");
            Assert(rules.Rules.Any(r => r.Id == "chrome-cache" && r.ScanMode == "WholeRoot"), "Chrome 缓存规则缺失");
            Assert(rules.Rules.Any(r => r.Id == "directx-shader-cache"), "DirectX 缓存规则缺失");
            Assert(rules.Rules.Any(r => r.Id == "firefox-cache") && rules.Rules.Any(r => r.Id == "steam-web-cache"),
                "浏览器或游戏平台规则族缺失");
            Assert(rules.Rules.Any(r => r.Id == "gradle-cache" && r.SelectionTier == "Never") &&
                   rules.Rules.Any(r => r.Id == "maven-repository" && r.SelectionTier == "Never"),
                "开发依赖仓库没有保持人工判断策略");
            Assert(rules.Rules.All(r => !string.IsNullOrWhiteSpace(r.Identity) &&
                                        !string.IsNullOrWhiteSpace(r.Purpose) &&
                                        !string.IsNullOrWhiteSpace(r.Evidence) &&
                                        !string.IsNullOrWhiteSpace(r.Impact) &&
                                        !string.IsNullOrWhiteSpace(r.Recommendation)),
                "存在缺少身份、依据、用途、影响或建议的规则");
            Assert(rules.Rules.All(r => !string.IsNullOrWhiteSpace(r.PathTemplate) ||
                                        (r.PathTemplates != null && r.PathTemplates.Count > 0)),
                "存在没有扫描路径的规则族");
        }

        private static void RuleFamiliesExpandProfiles()
        {
            var root = NewTempDirectory();
            try
            {
                var templates = new List<string>
                {
                    Path.Combine(root, "Default", "Cache"),
                    Path.Combine(root, "Profile *", "Cache")
                };
                foreach (var profile in new[] { "Default", "Profile 1", "Profile 2" })
                {
                    var cache = Path.Combine(root, profile, "Cache");
                    Directory.CreateDirectory(cache);
                    File.WriteAllBytes(Path.Combine(cache, "data.bin"), new byte[4096]);
                }
                var rule = CacheRule("profile-family", templates[0]);
                rule.PathTemplate = null;
                rule.PathTemplates = templates;
                var engine = new RuleEngine(new[] { rule });
                var expanded = engine.ExpandPaths(rule);
                Assert(expanded.Count == 3, "通配规则没有展开全部浏览器配置");
                var result = new CandidateScanner(engine).QuickScanAsync(null, CancellationToken.None).GetAwaiter().GetResult();
                Assert(result.Candidates.Count == 3, "快速扫描没有为三个配置生成独立候选");
            }
            finally { Directory.Delete(root, true); }
        }

        private static void QuickScanHonorsSelectedDrive()
        {
            var root = NewTempDirectory();
            try
            {
                File.WriteAllBytes(Path.Combine(root, "cache.bin"), new byte[4096]);
                var rule = CacheRule("drive-filter", root);
                var scanner = new CandidateScanner(new RuleEngine(new[] { rule }));
                var actualDrive = Path.GetPathRoot(root);
                var included = scanner.QuickScanAsync(new[] { actualDrive }, null, null, CancellationToken.None).GetAwaiter().GetResult();
                var excluded = scanner.QuickScanAsync(new[] { "Z:\\" }, null, null, CancellationToken.None).GetAwaiter().GetResult();
                Assert(included.Candidates.Count == 1, "所选磁盘上的规则候选没有被扫描");
                Assert(excluded.Candidates.Count == 0, "未选择磁盘上的规则候选仍被扫描");
            }
            finally { Directory.Delete(root, true); }
        }

        private static void RunningOwnerBlocksConservativeSelection()
        {
            var root = NewTempDirectory();
            try
            {
                File.WriteAllBytes(Path.Combine(root, "cache.bin"), new byte[4096]);
                var rule = CacheRule("running-owner", root);
                rule.ProcessNames = new List<string> { Process.GetCurrentProcess().ProcessName };
                var engine = new RuleEngine(new[] { rule });
                var result = new CandidateScanner(engine).QuickScanAsync(null, CancellationToken.None).GetAwaiter().GetResult();
                Assert(result.Candidates.Count == 1 && result.Candidates[0].IsLocked, "关联运行进程没有标记候选为占用");
                Assert(!engine.MayAutoSelect(result.Candidates[0], SelectionTier.Conservative), "运行中的软件缓存进入了保守选择");
                var validation = new CleanupExecutor(engine).Validate(result.Candidates[0]);
                Assert(validation.Status == ActionStatus.Skipped && validation.Message.Contains("关联软件"), "执行前没有再次检查关联进程");
            }
            finally { Directory.Delete(root, true); }
        }

        private static void ModelFilesAreProtected()
        {
            string reason;
            var root = NewTempDirectory();
            try
            {
                Assert(new RuleEngine().IsProtectedForCleanup(Path.Combine(root, "model.gguf"), out reason), "GGUF 模型没有受保护");
                Assert(new RuleEngine().IsProtectedForCleanup(Path.Combine(root, "weights.safetensors"), out reason), "Safetensors 模型没有受保护");
            }
            finally { Directory.Delete(root, true); }
        }

        private static void WholeRootCacheIsReported()
        {
            var root = NewTempDirectory();
            try
            {
                File.WriteAllBytes(Path.Combine(root, "recent.cache"), Enumerable.Repeat((byte)5, 4096).ToArray());
                var rule = new CleanupRule
                {
                    Id = "test-whole-cache", PathTemplate = root, ScanMode = "WholeRoot", Owner = "测试软件",
                    Category = "RebuildableCache", MinimumAgeDays = 0, Risk = "Low", Confidence = "High",
                    SelectionTier = "Conservative", Action = "Recycle", Identity = "测试缓存",
                    Purpose = "测试", Evidence = "精确测试路径", Impact = "可重建", Recommendation = "可清理"
                };
                var engine = new RuleEngine(new[] { rule });
                var result = new CandidateScanner(engine).QuickScanAsync(null, CancellationToken.None).GetAwaiter().GetResult();
                Assert(result.Candidates.Count == 1, "近期缓存内容导致整个明确缓存目录被漏报");
                Assert(string.Equals(result.Candidates[0].Path, root, StringComparison.OrdinalIgnoreCase), "快速扫描没有按缓存根目录汇总");
                Assert(engine.MayAutoSelect(result.Candidates[0], SelectionTier.Conservative), "明确可重建缓存未进入安全选择");
            }
            finally { Directory.Delete(root, true); }
        }

        private static void QuickScanReportsProgress()
        {
            var root = NewTempDirectory();
            try
            {
                File.WriteAllBytes(Path.Combine(root, "cache.bin"), new byte[1024]);
                var rules = new[]
                {
                    CacheRule("progress-one", root),
                    CacheRule("progress-two", Path.Combine(root, "missing"))
                };
                var reports = new List<ScanProgress>();
                var progress = new InlineProgress<ScanProgress>(reports.Add);
                new CandidateScanner(new RuleEngine(rules)).QuickScanAsync(progress, CancellationToken.None).GetAwaiter().GetResult();

                Assert(reports.Count >= 3, "快速扫描没有持续报告进度");
                Assert(reports.First().CompletedSteps == 0 && reports.First().TotalSteps == 2, "快速扫描起始进度错误");
                Assert(reports.Any(p => p.CompletedSteps == 1 && p.TotalSteps == 2), "快速扫描没有报告中间进度");
                Assert(reports.Last().CompletedSteps == 2 && reports.Last().TotalSteps == 2, "快速扫描没有到达完成进度");
            }
            finally { Directory.Delete(root, true); }
        }

        private static CleanupRule CacheRule(string id, string path)
        {
            return new CleanupRule
            {
                Id = id, PathTemplate = path, ScanMode = "WholeRoot", Owner = "测试软件",
                Category = "RebuildableCache", MinimumAgeDays = 0, Risk = "Low", Confidence = "High",
                SelectionTier = "Conservative", Action = "Recycle", Identity = "测试缓存",
                Purpose = "测试", Evidence = "精确测试路径", Impact = "可重建", Recommendation = "可清理"
            };
        }

        private static void ProtectedPaths()
        {
            var rules = new RuleEngine();
            string reason;
            Assert(rules.IsProtectedForCleanup(@"C:\Windows\System32\config", out reason), "System32 未保护");
            Assert(rules.IsProtectedForCleanup(@"C:\ProgramData\Package Cache\abc", out reason), "Package Cache 未保护");
            Assert(rules.IsProtectedForCleanup(@"D:\Code\demo\.git", out reason), ".git 未保护");
        }

        private static void ConservativeSelection()
        {
            var rules = new RuleEngine();
            var candidate = SafeCandidate();
            Assert(rules.MayAutoSelect(candidate, SelectionTier.Conservative), "安全候选未被保守模式选择");
            candidate.SizeComplete = false;
            Assert(!rules.MayAutoSelect(candidate, SelectionTier.Conservative), "不完整候选不应自动选择");
        }

        private static void BalancedNeverSelectsHighRisk()
        {
            var rules = new RuleEngine();
            var candidate = SafeCandidate();
            candidate.Category = CandidateCategory.SoftwareResidual;
            candidate.SelectionTier = SelectionTier.Balanced;
            Assert(!rules.MayAutoSelect(candidate, SelectionTier.Balanced), "软件残留被自动选择");
            candidate.Category = CandidateCategory.LargeSuspicious;
            Assert(!rules.MayAutoSelect(candidate, SelectionTier.Balanced), "未知大文件被自动选择");
            candidate.Category = CandidateCategory.SystemManaged;
            Assert(!rules.MayAutoSelect(candidate, SelectionTier.Balanced), "系统托管项被自动选择");
        }

        private static void BundledDependency()
        {
            var rules = new RuleEngine();
            Assert(rules.IsBundledApplicationDependency(@"D:\cursor\resources\app\node_modules"), "应用依赖未识别");
            Assert(!rules.IsBundledApplicationDependency(@"D:\Code\demo\node_modules"), "源码项目被误判为应用组件");
        }

        private static void UninstallCommandParsing()
        {
            string exe, args;
            InstalledAppProvider.SplitCommandLine("\"C:\\Program Files\\Widget\\uninstall.exe\" /S /from=test", out exe, out args);
            Assert(exe.EndsWith("uninstall.exe", StringComparison.OrdinalIgnoreCase), "卸载EXE解析错误");
            Assert(args == "/S /from=test", "卸载参数丢失");
        }

        private static void ExactDuplicates()
        {
            var root = NewTempDirectory();
            try
            {
                File.WriteAllBytes(Path.Combine(root, "one.bin"), Enumerable.Repeat((byte)7, 8192).ToArray());
                File.WriteAllBytes(Path.Combine(root, "two.bin"), Enumerable.Repeat((byte)7, 8192).ToArray());
                File.WriteAllBytes(Path.Combine(root, "other.bin"), Enumerable.Repeat((byte)8, 8192).ToArray());
                var scanner = new DuplicateScanner(new RuleEngine());
                var result = scanner.ScanAsync(new[] { root }, 1024, null, CancellationToken.None).GetAwaiter().GetResult();
                Assert(result.Candidates.Count == 1, "重复组数量错误");
                Assert(result.Candidates[0].Paths.Count == 2, "重复路径数量错误");
                Assert(result.Candidates[0].SelectionTier == SelectionTier.Never, "重复项不应自动选择");
            }
            finally { Directory.Delete(root, true); }
        }

        private static void StaleCandidateIsSkipped()
        {
            var root = NewTempDirectory();
            try
            {
                var path = Path.Combine(root, "old.tmp");
                File.WriteAllText(path, "one");
                var info = new FileInfo(path);
                var item = SafeCandidate();
                item.Path = path;
                item.Kind = CandidateKind.File;
                item.SizeBytes = info.Length;
                item.ModifiedUtc = info.LastWriteTimeUtc;
                item.Fingerprint = CandidateScanner.BuildFingerprint(path, info.Length, info.LastWriteTimeUtc);
                Thread.Sleep(30);
                File.AppendAllText(path, "changed");
                var result = new CleanupExecutor(new RuleEngine()).Validate(item);
                Assert(result.Status == ActionStatus.Skipped, "变化候选没有被跳过");
            }
            finally { Directory.Delete(root, true); }
        }

        private static void CandidatesDefaultToUnselected()
        {
            Assert(!new CandidateRecord().IsSelected, "新候选不应默认勾选");
        }

        private static void EvidenceSourceIsExplicit()
        {
            var item = SafeCandidate();
            item.RuleId = "known-cache";
            item.Evidence = "命中明确缓存路径";
            Assert(item.EvidenceDisplay.StartsWith("本地规则 known-cache · "), "规则候选没有标注本地规则来源");

            item.RuleId = null;
            item.Category = CandidateCategory.LargeSuspicious;
            Assert(item.EvidenceDisplay.StartsWith("本地启发式判断 · "), "疑似候选没有标注启发式来源");
        }

        private static void PermanentItemsAreNeverAutoSelected()
        {
            var item = SafeCandidate();
            item.RecommendedAction = ActionKind.Permanent;
            Assert(!new RuleEngine().MayAutoSelect(item, SelectionTier.Balanced), "永久删除项被智能勾选");
        }

        private static void PausedScanCancelsPromptly()
        {
            using (var pause = new ScanPauseController())
            using (var cancel = new CancellationTokenSource())
            {
                pause.Pause();
                cancel.CancelAfter(100);
                var watch = Stopwatch.StartNew();
                try { pause.Wait(cancel.Token); throw new InvalidOperationException("取消未生效"); }
                catch (OperationCanceledException) { }
                watch.Stop();
                Assert(watch.Elapsed < TimeSpan.FromSeconds(2), "取消耗时超过两秒");
            }
        }

        private static void PlaceholderUpdateKeyDisablesChecks()
        {
            Assert(!new UpdateChecker().IsConfigured, "占位公钥不应启用联网更新检查");
        }

        private static void LongPathEnumeration()
        {
            var root = NewTempDirectory();
            try
            {
                var deep = root;
                for (var i = 0; i < 9; i++) deep = Path.Combine(deep, "segment_" + i + "_abcdefghijklmnopqrstuvwxyz");
                Directory.CreateDirectory(Extended(deep));
                File.WriteAllBytes(Extended(Path.Combine(deep, "one.bin")), Enumerable.Repeat((byte)3, 4096).ToArray());
                File.WriteAllBytes(Extended(Path.Combine(deep, "two.bin")), Enumerable.Repeat((byte)3, 4096).ToArray());
                var result = new DuplicateScanner(new RuleEngine()).ScanAsync(new[] { root }, 1024, null, CancellationToken.None).GetAwaiter().GetResult();
                Assert(result.Candidates.Count == 1, "长路径中的重复文件未被发现");
            }
            finally { Directory.Delete(Extended(root), true); }
        }

        private static void LockedCandidateIsSkipped()
        {
            var root = NewTempDirectory();
            try
            {
                var path = Path.Combine(root, "locked.tmp");
                File.WriteAllText(path, "locked");
                var info = new FileInfo(path);
                var item = SafeCandidate();
                item.Path = path;
                item.Kind = CandidateKind.File;
                item.SizeBytes = info.Length;
                item.ModifiedUtc = info.LastWriteTimeUtc;
                item.Fingerprint = CandidateScanner.BuildFingerprint(path, info.Length, info.LastWriteTimeUtc);
                using (File.Open(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                {
                    var result = new CleanupExecutor(new RuleEngine()).Validate(item);
                    Assert(result.Status == ActionStatus.Skipped && result.Message.Contains("使用"), "占用文件未被跳过");
                }
            }
            finally { Directory.Delete(root, true); }
        }

        private static void DirectoryWithLockedFileIsSkipped()
        {
            var root = NewTempDirectory();
            try
            {
                var path = Path.Combine(root, "child.tmp");
                File.WriteAllText(path, "locked child");
                long examined = 0;
                var denied = 0;
                var stats = CandidateScanner.MeasureTree(root, CancellationToken.None, ref examined, ref denied);
                var item = SafeCandidate();
                item.Path = root;
                item.Kind = CandidateKind.Directory;
                item.SizeBytes = stats.SizeBytes;
                item.ModifiedUtc = stats.LatestWriteUtc;
                item.Fingerprint = CandidateScanner.BuildFingerprint(root, stats.SizeBytes, stats.LatestWriteUtc);
                using (File.Open(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                {
                    var result = new CleanupExecutor(new RuleEngine()).Validate(item);
                    Assert(result.Status == ActionStatus.Skipped && result.Message.Contains("目标内"), "含占用文件的目录未被跳过");
                }
            }
            finally { Directory.Delete(root, true); }
        }

        private static CandidateRecord SafeCandidate()
        {
            return new CandidateRecord
            {
                Path = Path.Combine(Path.GetTempPath(), "DiskCleanupAssistantTest", "old.tmp"), Kind = CandidateKind.File,
                SizeBytes = 10, Category = CandidateCategory.ConfirmedGarbage, Risk = RiskLevel.Low,
                Confidence = ConfidenceLevel.High, SelectionTier = SelectionTier.Conservative,
                RecommendedAction = ActionKind.Recycle, SizeComplete = true
            };
        }

        private static string NewTempDirectory()
        {
            var path = Path.Combine(Path.GetTempPath(), "DiskCleanupAssistantTests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(path);
            return path;
        }

        private static string Extended(string path)
        {
            return path.StartsWith(@"\\?\", StringComparison.Ordinal) ? path : @"\\?\" + Path.GetFullPath(path);
        }

        private static void Assert(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
        }

        private static void Run(string name, Action test)
        {
            try { test(); _passed++; Console.WriteLine("[PASS] " + name); }
            catch (Exception ex) { _failed++; Console.WriteLine("[FAIL] " + name + ": " + ex.Message); }
        }

        private sealed class InlineProgress<T> : IProgress<T>
        {
            private readonly Action<T> _report;
            public InlineProgress(Action<T> report) { _report = report; }
            public void Report(T value) { _report(value); }
        }
    }
}
