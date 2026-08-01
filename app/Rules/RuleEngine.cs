using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Serialization.Json;
using System.Text;
using System.Text.RegularExpressions;
using DiskCleanupAssistant.Models;

namespace DiskCleanupAssistant.Rules
{
    public sealed class RuleEngine
    {
        private static readonly string[] ProtectedFragments =
        {
            "\\windows\\system32", "\\windows\\winsxs", "\\windows\\installer",
            "\\system volume information", "\\programdata\\package cache", "\\.git\\"
        };

        private static readonly string[] SensitiveExtensions =
        {
            ".db", ".sqlite", ".pst", ".ost", ".vhd", ".vhdx", ".key", ".pem", ".kdbx",
            ".safetensors", ".gguf", ".ckpt", ".onnx", ".pt", ".pth"
        };

        private readonly List<CleanupRule> _rules;

        public RuleEngine()
        {
            _rules = LoadRules();
        }

        public RuleEngine(IEnumerable<CleanupRule> rules)
        {
            if (rules == null) throw new ArgumentNullException("rules");
            _rules = rules.ToList();
        }

        public IReadOnlyList<CleanupRule> Rules { get { return _rules; } }

        public string ExpandPath(CleanupRule rule)
        {
            return ExpandVariables(rule == null ? null : rule.PathTemplate);
        }

        public IReadOnlyList<string> ExpandPaths(CleanupRule rule)
        {
            if (rule == null) return new List<string>();
            var templates = new List<string>();
            if (!string.IsNullOrWhiteSpace(rule.PathTemplate)) templates.Add(rule.PathTemplate);
            if (rule.PathTemplates != null) templates.AddRange(rule.PathTemplates.Where(value => !string.IsNullOrWhiteSpace(value)));
            return templates.Select(ExpandVariables)
                .SelectMany(ExpandWildcardPath)
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        private static string ExpandVariables(string template)
        {
            var value = template ?? string.Empty;
            value = value.Replace("{TEMP}", Environment.GetEnvironmentVariable("TEMP") ?? string.Empty);
            value = value.Replace("{WINDOWS}", Environment.GetFolderPath(Environment.SpecialFolder.Windows));
            value = value.Replace("{LOCALAPPDATA}", Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData));
            value = value.Replace("{APPDATA}", Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData));
            value = value.Replace("{PROGRAMDATA}", Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData));
            value = value.Replace("{USERPROFILE}", Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
            value = value.Replace("{SYSTEMDRIVE}", Path.GetPathRoot(Environment.GetFolderPath(Environment.SpecialFolder.Windows)) == null
                ? "C:"
                : Path.GetPathRoot(Environment.GetFolderPath(Environment.SpecialFolder.Windows)).TrimEnd(Path.DirectorySeparatorChar));
            return Environment.ExpandEnvironmentVariables(value);
        }

        private static IEnumerable<string> ExpandWildcardPath(string pattern)
        {
            if (string.IsNullOrWhiteSpace(pattern)) yield break;
            var wildcardIndex = pattern.IndexOfAny(new[] { '*', '?' });
            if (wildcardIndex < 0)
            {
                yield return pattern;
                yield break;
            }

            var segmentStart = pattern.LastIndexOf(Path.DirectorySeparatorChar, wildcardIndex);
            if (segmentStart < 0) yield break;
            var segmentEnd = pattern.IndexOf(Path.DirectorySeparatorChar, wildcardIndex);
            var parent = pattern.Substring(0, segmentStart);
            var segment = segmentEnd < 0
                ? pattern.Substring(segmentStart + 1)
                : pattern.Substring(segmentStart + 1, segmentEnd - segmentStart - 1);
            var suffix = segmentEnd < 0 ? null : pattern.Substring(segmentEnd + 1);

            IEnumerable<string> entries;
            if (!Directory.Exists(parent)) yield break;
            try
            {
                entries = Directory.EnumerateFileSystemEntries(parent).ToArray();
            }
            catch (UnauthorizedAccessException) { yield break; }
            catch (IOException) { yield break; }

            foreach (var entry in entries)
            {
                if (!WildcardMatch(Path.GetFileName(entry), segment)) continue;
                if (string.IsNullOrEmpty(suffix))
                {
                    yield return entry;
                    continue;
                }
                if (!Directory.Exists(entry)) continue;
                foreach (var expanded in ExpandWildcardPath(Path.Combine(entry, suffix))) yield return expanded;
            }
        }

        private static bool WildcardMatch(string value, string pattern)
        {
            var expression = "^" + Regex.Escape(pattern).Replace("\\*", ".*").Replace("\\?", ".") + "$";
            return Regex.IsMatch(value ?? string.Empty, expression, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        }

        public bool IsProtectedForCleanup(string path, out string reason)
        {
            reason = null;
            if (string.IsNullOrWhiteSpace(path))
            {
                reason = "路径为空";
                return true;
            }

            string full;
            try { full = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar); }
            catch
            {
                reason = "路径无法规范化";
                return true;
            }

            var root = Path.GetPathRoot(full);
            if (string.Equals(root == null ? null : root.TrimEnd('\\'), full, StringComparison.OrdinalIgnoreCase))
            {
                reason = "禁止处理磁盘根目录";
                return true;
            }

            var lower = ("\\" + full.Replace('/', '\\')).ToLowerInvariant();
            if (full.Split(Path.DirectorySeparatorChar).Any(part => part.Equals(".git", StringComparison.OrdinalIgnoreCase)))
            {
                reason = "Git 仓库元数据受保护";
                return true;
            }
            foreach (var fragment in ProtectedFragments)
            {
                if (lower.Contains(fragment))
                {
                    reason = "命中系统或项目保护路径：" + fragment;
                    return true;
                }
            }

            var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            var programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
            if (IsSameOrChild(full, windows) && !IsKnownAllowedSystemPath(full))
            {
                reason = "Windows 目录仅允许规则明确列出的临时位置";
                return true;
            }
            if (IsSameOrChild(full, programFiles) || IsSameOrChild(full, programFilesX86))
            {
                reason = "程序安装目录不能作为普通清理目标";
                return true;
            }

            var extension = Path.GetExtension(full).ToLowerInvariant();
            if (SensitiveExtensions.Contains(extension))
            {
                reason = "数据库、凭据或虚拟磁盘类文件受保护";
                return true;
            }
            return false;
        }

        public bool IsUserLibrary(string path)
        {
            var libraries = new[]
            {
                Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
                Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                Environment.GetFolderPath(Environment.SpecialFolder.MyPictures),
                Environment.GetFolderPath(Environment.SpecialFolder.MyMusic),
                Environment.GetFolderPath(Environment.SpecialFolder.MyVideos),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "OneDrive")
            };
            return libraries.Any(p => !string.IsNullOrWhiteSpace(p) && IsSameOrChild(path, p));
        }

        public bool MayAutoSelect(CandidateRecord candidate, SelectionTier requested)
        {
            if (candidate == null || candidate.IsLocked || !candidate.SizeComplete) return false;
            if (candidate.RecommendedAction != ActionKind.Recycle) return false;
            if (candidate.Confidence != ConfidenceLevel.High) return false;
            if (candidate.Category == CandidateCategory.LargeSuspicious ||
                candidate.Category == CandidateCategory.SoftwareResidual ||
                candidate.Category == CandidateCategory.ExactDuplicate ||
                candidate.Category == CandidateCategory.SystemManaged ||
                candidate.Category == CandidateCategory.Protected) return false;
            if (IsUserLibrary(candidate.Path)) return false;
            string reason;
            if (IsProtectedForCleanup(candidate.Path, out reason)) return false;
            if (candidate.SelectionTier == SelectionTier.Never) return false;
            if (requested == SelectionTier.Conservative)
                return candidate.SelectionTier == SelectionTier.Conservative;
            return candidate.SelectionTier == SelectionTier.Conservative || candidate.SelectionTier == SelectionTier.Balanced;
        }

        public bool IsRelatedProcessRunning(string ruleId)
        {
            if (string.IsNullOrWhiteSpace(ruleId)) return false;
            var rule = _rules.FirstOrDefault(item => string.Equals(item.Id, ruleId, StringComparison.OrdinalIgnoreCase));
            if (rule == null || rule.ProcessNames == null) return false;
            foreach (var rawName in rule.ProcessNames.Where(name => !string.IsNullOrWhiteSpace(name)))
            {
                var name = rawName.Trim();
                if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) name = name.Substring(0, name.Length - 4);
                try
                {
                    var processes = Process.GetProcessesByName(name);
                    try { if (processes.Length > 0) return true; }
                    finally { foreach (var process in processes) process.Dispose(); }
                }
                catch { }
            }
            return false;
        }

        public bool IsBundledApplicationDependency(string path)
        {
            var lower = path.Replace('/', '\\').ToLowerInvariant();
            return lower.Contains("\\resources\\app\\node_modules") ||
                   lower.Contains("\\program files\\") || lower.Contains("\\program files (x86)\\");
        }

        public static T ParseEnum<T>(string value, T fallback) where T : struct
        {
            T result;
            return Enum.TryParse(value, true, out result) ? result : fallback;
        }

        private bool IsKnownAllowedSystemPath(string path)
        {
            foreach (var rule in _rules)
            {
                foreach (var expanded in ExpandPaths(rule))
                    if (!string.IsNullOrWhiteSpace(expanded) && IsSameOrChild(path, expanded)) return true;
            }
            return false;
        }

        public static bool IsSameOrChild(string candidate, string parent)
        {
            if (string.IsNullOrWhiteSpace(candidate) || string.IsNullOrWhiteSpace(parent)) return false;
            string childFull;
            string parentFull;
            try
            {
                childFull = Path.GetFullPath(candidate).TrimEnd('\\') + "\\";
                parentFull = Path.GetFullPath(parent).TrimEnd('\\') + "\\";
            }
            catch { return false; }
            return childFull.StartsWith(parentFull, StringComparison.OrdinalIgnoreCase);
        }

        private static List<CleanupRule> LoadRules()
        {
            var assembly = Assembly.GetExecutingAssembly();
            using (var stream = assembly.GetManifestResourceStream("DiskCleanupAssistant.Rules.cleanup-rules.json"))
            {
                if (stream == null) throw new InvalidOperationException("内嵌清理规则缺失");
                var serializer = new DataContractJsonSerializer(typeof(List<CleanupRule>));
                return (List<CleanupRule>)serializer.ReadObject(stream);
            }
        }
    }
}
