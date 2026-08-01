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
    public sealed class DuplicateScanner
    {
        private readonly RuleEngine _rules;

        public DuplicateScanner(RuleEngine rules)
        {
            _rules = rules;
        }

        public Task<ScanSnapshot> ScanAsync(IEnumerable<string> roots, long minimumBytes,
            IProgress<ScanProgress> progress, CancellationToken token)
        {
            return ScanAsync(roots, minimumBytes, progress, null, token);
        }

        public Task<ScanSnapshot> ScanAsync(IEnumerable<string> roots, long minimumBytes,
            IProgress<ScanProgress> progress, ScanPauseController pause, CancellationToken token)
        {
            return Task.Run(() => Scan(roots, minimumBytes, progress, pause, token), token);
        }

        private ScanSnapshot Scan(IEnumerable<string> roots, long minimumBytes, IProgress<ScanProgress> progress, ScanPauseController pause, CancellationToken token)
        {
            var watch = Stopwatch.StartNew();
            var rootList = roots.Where(NativeMethods.DirectoryExists).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            var bySize = new Dictionary<long, List<string>>();
            var stack = new Stack<string>(rootList.Reverse<string>());
            long examined = 0;
            var denied = 0;

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
                        if (NativeMethods.IsReparsePoint(entry)) continue;
                        if (NativeMethods.DirectoryExists(entry)) stack.Push(entry);
                        else
                        {
                            var info = NativeMethods.GetMetadata(entry);
                            if (info.Length < minimumBytes) continue;
                            List<string> group;
                            if (!bySize.TryGetValue(info.Length, out group))
                            {
                                group = new List<string>();
                                bySize[info.Length] = group;
                            }
                            group.Add(entry);
                        }
                        if (examined % 1000 == 0 && progress != null)
                            progress.Report(new ScanProgress { CurrentPath = entry, EntriesExamined = examined, CandidatesFound = 0, PermissionSkips = denied });
                    }
                }
                catch (UnauthorizedAccessException) { denied++; }
                catch (IOException) { denied++; }
            }

            var candidates = new List<CandidateRecord>();
            foreach (var sizeGroup in bySize.Where(p => p.Value.Count > 1).OrderByDescending(p => p.Key))
            {
                WaitIfPaused(pause, token);
                token.ThrowIfCancellationRequested();
                var byPartial = new Dictionary<string, List<string>>(StringComparer.Ordinal);
                foreach (var path in RemoveHardLinkAliases(sizeGroup.Value))
                {
                    var hash = HashPartial(path, token);
                    if (hash == null) { denied++; continue; }
                    List<string> list;
                    if (!byPartial.TryGetValue(hash, out list)) { list = new List<string>(); byPartial[hash] = list; }
                    list.Add(path);
                }

                foreach (var partialGroup in byPartial.Values.Where(v => v.Count > 1))
                {
                    var byFull = new Dictionary<string, List<string>>(StringComparer.Ordinal);
                    foreach (var path in partialGroup)
                    {
                        WaitIfPaused(pause, token);
                        var hash = HashFull(path, token);
                        if (hash == null) { denied++; continue; }
                        List<string> list;
                        if (!byFull.TryGetValue(hash, out list)) { list = new List<string>(); byFull[hash] = list; }
                        list.Add(path);
                    }

                    foreach (var exact in byFull.Where(p => p.Value.Count > 1))
                    {
                        var paths = exact.Value.OrderBy(p => p, StringComparer.OrdinalIgnoreCase).ToList();
                        var allocated = NativeMethods.GetAllocatedSize(paths[0], sizeGroup.Key);
                        var found = new CandidateRecord
                        {
                            Path = paths[0],
                            Paths = paths,
                            Kind = CandidateKind.DuplicateGroup,
                            SizeBytes = allocated * (paths.Count - 1),
                            Category = CandidateCategory.ExactDuplicate,
                            Risk = RiskLevel.High,
                            Confidence = ConfidenceLevel.High,
                            SelectionTier = SelectionTier.Never,
                            RecommendedAction = ActionKind.None,
                            Owner = "多个位置",
                            Identity = "内容完全一致的大文件组",
                            SuspectedPurpose = "可能是重复下载、复制或备份，但不同路径可能承担不同用途",
                            Evidence = "大小、首尾抽样和完整 SHA-256 均一致：" + exact.Key,
                            DeletionImpact = "只能由用户明确指定保留及删除哪个副本",
                            Recommendation = "逐一查看所有路径；软件不会自动选择任何副本",
                            SizeComplete = true,
                            Fingerprint = exact.Key,
                            RuleId = "exact-duplicate"
                        };
                        candidates.Add(found);
                        if (progress != null) progress.Report(new ScanProgress { CurrentPath = found.Path, EntriesExamined = examined, CandidatesFound = candidates.Count, PermissionSkips = denied, FoundCandidate = found });
                    }
                }
            }

            candidates = candidates.OrderByDescending(c => c.SizeBytes).ToList();
            for (var i = 0; i < candidates.Count; i++) candidates[i].Id = "X" + (i + 1).ToString("000");
            watch.Stop();
            return new ScanSnapshot
            {
                ScanId = Guid.NewGuid().ToString("N"), GeneratedUtc = DateTime.UtcNow, Roots = rootList,
                Complete = true, PermissionSkips = denied, EntriesExamined = examined,
                ElapsedSeconds = watch.Elapsed.TotalSeconds, Candidates = candidates
            };
        }

        private static IEnumerable<string> RemoveHardLinkAliases(IEnumerable<string> paths)
        {
            var identities = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var path in paths)
            {
                var identity = NativeMethods.GetFileIdentity(path);
                var key = identity ?? path;
                if (identities.Add(key)) yield return path;
            }
        }

        private static string HashPartial(string path, CancellationToken token)
        {
            try
            {
                using (var sha = SHA256.Create())
                using (var stream = new FileStream(NativeMethods.ToExtendedPath(path), FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024, FileOptions.SequentialScan))
                {
                    var buffer = new byte[1024 * 1024];
                    var read = stream.Read(buffer, 0, buffer.Length);
                    sha.TransformBlock(buffer, 0, read, null, 0);
                    if (stream.Length > buffer.Length)
                    {
                        stream.Seek(Math.Max(0, stream.Length - buffer.Length), SeekOrigin.Begin);
                        read = stream.Read(buffer, 0, buffer.Length);
                        sha.TransformBlock(buffer, 0, read, null, 0);
                    }
                    token.ThrowIfCancellationRequested();
                    var size = BitConverter.GetBytes(stream.Length);
                    sha.TransformFinalBlock(size, 0, size.Length);
                    return ToHex(sha.Hash);
                }
            }
            catch (IOException) { return null; }
            catch (UnauthorizedAccessException) { return null; }
        }

        private static string HashFull(string path, CancellationToken token)
        {
            try
            {
                using (var sha = SHA256.Create())
                using (var stream = new FileStream(NativeMethods.ToExtendedPath(path), FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024, FileOptions.SequentialScan))
                {
                    var buffer = new byte[1024 * 1024];
                    int read;
                    while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
                    {
                        token.ThrowIfCancellationRequested();
                        sha.TransformBlock(buffer, 0, read, null, 0);
                    }
                    sha.TransformFinalBlock(new byte[0], 0, 0);
                    return ToHex(sha.Hash);
                }
            }
            catch (IOException) { return null; }
            catch (UnauthorizedAccessException) { return null; }
        }

        private static string ToHex(byte[] bytes)
        {
            return BitConverter.ToString(bytes).Replace("-", string.Empty);
        }

        private static void WaitIfPaused(ScanPauseController pause, CancellationToken token)
        {
            if (pause != null) pause.Wait(token);
        }
    }
}
