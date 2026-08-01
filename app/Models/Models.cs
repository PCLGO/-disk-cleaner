using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Runtime.Serialization;

namespace DiskCleanupAssistant.Models
{
    public enum CandidateCategory
    {
        ConfirmedGarbage,
        RebuildableCache,
        Diagnostics,
        SoftwareResidual,
        LargeSuspicious,
        ExactDuplicate,
        SystemManaged,
        Protected
    }

    public enum RiskLevel { Low, Medium, High }
    public enum ConfidenceLevel { Low, Medium, High }
    public enum SelectionTier { Never, Conservative, Balanced }
    public enum ActionKind { None, Recycle, Permanent, OfficialTool, Uninstall, OpenLocation }
    public enum CandidateKind { File, Directory, DuplicateGroup }
    public enum ActionStatus { Success, Skipped, Failed }

    [DataContract]
    public sealed class CandidateRecord : INotifyPropertyChanged
    {
        private bool _isSelected;

        [DataMember(Order = 1)] public string Id { get; set; }
        [DataMember(Order = 2)] public string Path { get; set; }
        [DataMember(Order = 3)] public List<string> Paths { get; set; }
        [DataMember(Order = 4)] public CandidateKind Kind { get; set; }
        [DataMember(Order = 5)] public long SizeBytes { get; set; }
        [DataMember(Order = 6)] public CandidateCategory Category { get; set; }
        [DataMember(Order = 7)] public RiskLevel Risk { get; set; }
        [DataMember(Order = 8)] public ConfidenceLevel Confidence { get; set; }
        [DataMember(Order = 9)] public SelectionTier SelectionTier { get; set; }
        [DataMember(Order = 10)] public ActionKind RecommendedAction { get; set; }
        [DataMember(Order = 11)] public string Owner { get; set; }
        [DataMember(Order = 12)] public string Identity { get; set; }
        [DataMember(Order = 13)] public string SuspectedPurpose { get; set; }
        [DataMember(Order = 14)] public string Evidence { get; set; }
        [DataMember(Order = 15)] public string DeletionImpact { get; set; }
        [DataMember(Order = 16)] public string Recommendation { get; set; }
        [DataMember(Order = 17)] public DateTime? ModifiedUtc { get; set; }
        [DataMember(Order = 18)] public bool SizeComplete { get; set; }
        [DataMember(Order = 19)] public bool RequiresElevation { get; set; }
        [DataMember(Order = 20)] public bool IsLocked { get; set; }
        [DataMember(Order = 21)] public string Fingerprint { get; set; }
        [DataMember(Order = 22)] public string RuleId { get; set; }
        [DataMember(Order = 23)]
        public bool IsSelected
        {
            get { return _isSelected; }
            set
            {
                if (_isSelected == value) return;
                _isSelected = value;
                OnPropertyChanged();
            }
        }
        public string SizeDisplay { get { return SizeFormatter.Format(SizeBytes); } }
        public string RiskDisplay { get { return Risk == RiskLevel.Low ? "低" : Risk == RiskLevel.Medium ? "中" : "高"; } }
        public string ConfidenceDisplay { get { return Confidence == ConfidenceLevel.High ? "高" : Confidence == ConfidenceLevel.Medium ? "中" : "低"; } }
        public string CategoryDisplay { get { return DisplayNames.Category(Category); } }
        public string ActionDisplay { get { return DisplayNames.Action(RecommendedAction); } }
        public string ModifiedDisplay { get { return ModifiedUtc.HasValue ? ModifiedUtc.Value.ToLocalTime().ToString("yyyy-MM-dd HH:mm") : "—"; } }
        public string EvidenceDisplay
        {
            get
            {
                var source = !string.IsNullOrWhiteSpace(RuleId) ? "本地规则 " + RuleId :
                    Category == CandidateCategory.ExactDuplicate ? "本地完整哈希" :
                    Category == CandidateCategory.SoftwareResidual ? "本地安装信息" :
                    Category == CandidateCategory.LargeSuspicious || Category == CandidateCategory.Protected ? "本地启发式判断" :
                    "本地扫描证据";
                return source + " · " + (string.IsNullOrWhiteSpace(Evidence) ? "没有足够依据" : Evidence.Trim());
            }
        }

        public event PropertyChangedEventHandler PropertyChanged;
        private void OnPropertyChanged([CallerMemberName] string propertyName = null)
        {
            var handler = PropertyChanged;
            if (handler != null) handler(this, new PropertyChangedEventArgs(propertyName));
        }
    }

    [DataContract]
    public sealed class ScanSnapshot
    {
        [DataMember(Order = 1)] public string ScanId { get; set; }
        [DataMember(Order = 2)] public DateTime GeneratedUtc { get; set; }
        [DataMember(Order = 3)] public List<string> Roots { get; set; }
        [DataMember(Order = 4)] public bool Complete { get; set; }
        [DataMember(Order = 5)] public int PermissionSkips { get; set; }
        [DataMember(Order = 6)] public long EntriesExamined { get; set; }
        [DataMember(Order = 7)] public double ElapsedSeconds { get; set; }
        [DataMember(Order = 8)] public List<CandidateRecord> Candidates { get; set; }
    }

    [DataContract]
    public sealed class CleanupPlan
    {
        [DataMember(Order = 1)] public string Nonce { get; set; }
        [DataMember(Order = 2)] public DateTime CreatedUtc { get; set; }
        [DataMember(Order = 3)] public List<CandidateRecord> Items { get; set; }
    }

    [DataContract]
    public sealed class ActionResult
    {
        [DataMember(Order = 1)] public string Path { get; set; }
        [DataMember(Order = 2)] public ActionStatus Status { get; set; }
        [DataMember(Order = 3)] public string Message { get; set; }
        [DataMember(Order = 4)] public long EstimatedBytes { get; set; }
    }

    [DataContract]
    public sealed class ActionResultEnvelope
    {
        [DataMember(Order = 1)] public string Nonce { get; set; }
        [DataMember(Order = 2)] public List<ActionResult> Results { get; set; }
    }

    public sealed class ScanProgress
    {
        public string CurrentPath { get; set; }
        public long EntriesExamined { get; set; }
        public int CandidatesFound { get; set; }
        public int PermissionSkips { get; set; }
        public CandidateRecord FoundCandidate { get; set; }
        public int CompletedSteps { get; set; }
        public int TotalSteps { get; set; }
    }

    [DataContract]
    public sealed class InstalledAppRecord
    {
        [DataMember] public string DisplayName { get; set; }
        [DataMember] public string DisplayVersion { get; set; }
        [DataMember] public string Publisher { get; set; }
        [DataMember] public string InstallLocation { get; set; }
        [DataMember] public string UninstallString { get; set; }
        [DataMember] public string QuietUninstallString { get; set; }
        [DataMember] public long EstimatedSizeBytes { get; set; }
        [DataMember] public bool WindowsInstaller { get; set; }
        [DataMember] public string RegistryKeyName { get; set; }
        public string SizeDisplay { get { return EstimatedSizeBytes > 0 ? SizeFormatter.Format(EstimatedSizeBytes) : "未知"; } }
        public string VersionDisplay { get { return string.IsNullOrWhiteSpace(DisplayVersion) ? "—" : DisplayVersion; } }
    }

    [DataContract]
    public sealed class CleanupRule
    {
        [DataMember] public string Id { get; set; }
        [DataMember] public string PathTemplate { get; set; }
        [DataMember] public List<string> PathTemplates { get; set; }
        [DataMember] public string ScanMode { get; set; }
        [DataMember] public string Owner { get; set; }
        [DataMember] public string Category { get; set; }
        [DataMember] public int MinimumAgeDays { get; set; }
        [DataMember] public string Risk { get; set; }
        [DataMember] public string Confidence { get; set; }
        [DataMember] public string SelectionTier { get; set; }
        [DataMember] public string Action { get; set; }
        [DataMember] public bool RequiresElevation { get; set; }
        [DataMember] public List<string> ProcessNames { get; set; }
        [DataMember] public string Identity { get; set; }
        [DataMember] public string Purpose { get; set; }
        [DataMember] public string Evidence { get; set; }
        [DataMember] public string Impact { get; set; }
        [DataMember] public string Recommendation { get; set; }
    }

    [DataContract]
    public sealed class CleanupHistoryEntry
    {
        [DataMember] public DateTime TimestampUtc { get; set; }
        [DataMember] public string Path { get; set; }
        [DataMember] public string Status { get; set; }
        [DataMember] public string Message { get; set; }
        [DataMember] public long EstimatedBytes { get; set; }
        public string TimeDisplay { get { return TimestampUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm"); } }
        public string SizeDisplay { get { return SizeFormatter.Format(EstimatedBytes); } }
    }

    [DataContract]
    public sealed class AppSettings
    {
        [DataMember] public long LargeFileThresholdBytes { get; set; }
        [DataMember] public long DuplicateFileThresholdBytes { get; set; }
        [DataMember] public int HistoryRetentionDays { get; set; }
        [DataMember] public bool UpdateChecksEnabled { get; set; }
        [DataMember] public DateTime? LastUpdateCheckUtc { get; set; }

        public static AppSettings CreateDefault()
        {
            return new AppSettings
            {
                LargeFileThresholdBytes = 512L * 1024 * 1024,
                DuplicateFileThresholdBytes = 50L * 1024 * 1024,
                HistoryRetentionDays = 30,
                UpdateChecksEnabled = true
            };
        }
    }

    public static class SizeFormatter
    {
        public static string Format(long bytes)
        {
            if (bytes < 1024) return bytes + " B";
            if (bytes < 1024L * 1024) return (bytes / 1024d).ToString("0.0") + " KB";
            if (bytes < 1024L * 1024 * 1024) return (bytes / 1024d / 1024d).ToString("0.0") + " MB";
            return (bytes / 1024d / 1024d / 1024d).ToString("0.00") + " GB";
        }
    }

    public static class DisplayNames
    {
        public static string Category(CandidateCategory value)
        {
            switch (value)
            {
                case CandidateCategory.ConfirmedGarbage: return "确定垃圾";
                case CandidateCategory.RebuildableCache: return "可重建缓存";
                case CandidateCategory.Diagnostics: return "诊断数据";
                case CandidateCategory.SoftwareResidual: return "软件残留";
                case CandidateCategory.LargeSuspicious: return "大文件/疑似项";
                case CandidateCategory.ExactDuplicate: return "精确重复项";
                case CandidateCategory.SystemManaged: return "系统托管项";
                default: return "受保护内容";
            }
        }

        public static string Action(ActionKind value)
        {
            switch (value)
            {
                case ActionKind.Recycle: return "移入回收站";
                case ActionKind.Permanent: return "永久删除";
                case ActionKind.OfficialTool: return "官方清理入口";
                case ActionKind.Uninstall: return "官方卸载";
                case ActionKind.OpenLocation: return "打开位置";
                default: return "仅查看";
            }
        }
    }
}
