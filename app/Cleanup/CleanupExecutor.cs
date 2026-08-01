using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Principal;
using System.Threading;
using System.Threading.Tasks;
using DiskCleanupAssistant.Models;
using DiskCleanupAssistant.Rules;
using DiskCleanupAssistant.Scanning;
using DiskCleanupAssistant.WindowsIntegration;
using Microsoft.VisualBasic.FileIO;

namespace DiskCleanupAssistant.Cleanup
{
    public sealed class CleanupExecutor
    {
        private readonly RuleEngine _rules;

        public CleanupExecutor(RuleEngine rules)
        {
            _rules = rules;
        }

        public Task<List<ActionResult>> ExecuteAsync(IEnumerable<CandidateRecord> items, CancellationToken token)
        {
            return Task.Run(() => items.Select(item => ExecuteOne(item, token)).ToList(), token);
        }

        public ActionResult Validate(CandidateRecord item)
        {
            if (item == null || string.IsNullOrWhiteSpace(item.Path)) return Skip(item, "候选路径为空");
            if (item.Kind == CandidateKind.DuplicateGroup) return Skip(item, "重复文件组必须逐个指定副本，首版不直接执行");
            if (item.RecommendedAction != ActionKind.Recycle && item.RecommendedAction != ActionKind.Permanent && item.RecommendedAction != ActionKind.OfficialTool)
                return Skip(item, "该候选没有可执行的安全动作");
            if (!NativeMethods.FileExists(item.Path) && !NativeMethods.DirectoryExists(item.Path)) return Skip(item, "目标已不存在");
            if (NativeMethods.IsReparsePoint(item.Path)) return Skip(item, "目标是重解析点、链接或挂载点");
            string reason;
            if (_rules.IsProtectedForCleanup(item.Path, out reason) && item.Category != CandidateCategory.SystemManaged)
                return Skip(item, reason);
            if (_rules.IsUserLibrary(item.Path)) return Skip(item, "用户资料库内容只能查看，不能由清理器执行");
            if (_rules.IsRelatedProcessRunning(item.RuleId)) return Skip(item, "关联软件正在运行，请关闭后重新扫描");
            if (item.RequiresElevation && !IsAdministrator()) return Skip(item, "该目标需要管理员执行器");
            if (NativeMethods.FileExists(item.Path) && NativeMethods.IsLocked(item.Path)) return Skip(item, "文件正在使用或无法独占读取");

            long examined = 0;
            var denied = 0;
            TreeStats stats;
            try { stats = CandidateScanner.MeasureTree(item.Path, CancellationToken.None, ref examined, ref denied); }
            catch (Exception ex) { return Skip(item, "即时复核失败：" + ex.Message); }
            if (stats.HasLockedFile) return Skip(item, "目标内有文件正在使用或无法独占读取");
            var fingerprint = CandidateScanner.BuildFingerprint(item.Path, stats.SizeBytes, stats.LatestWriteUtc);
            if (!string.Equals(fingerprint, item.Fingerprint, StringComparison.OrdinalIgnoreCase))
                return Skip(item, "扫描后目标发生变化，已安全跳过");
            return new ActionResult { Path = item.Path, Status = ActionStatus.Success, Message = "复核通过", EstimatedBytes = item.SizeBytes };
        }

        private ActionResult ExecuteOne(CandidateRecord item, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if (item.RecommendedAction == ActionKind.OfficialTool)
            {
                try
                {
                    Process.Start(new ProcessStartInfo("ms-settings:storagesense") { UseShellExecute = true });
                    return new ActionResult { Path = item.Path, Status = ActionStatus.Success, Message = "已打开 Windows 官方存储清理入口", EstimatedBytes = 0 };
                }
                catch (Exception ex) { return Fail(item, "无法打开官方入口：" + ex.Message); }
            }

            var validation = Validate(item);
            if (validation.Status != ActionStatus.Success) return validation;
            try
            {
                if (item.RecommendedAction == ActionKind.Permanent && NativeMethods.DirectoryExists(item.Path))
                    Directory.Delete(NativeMethods.ToExtendedPath(item.Path), true);
                else if (item.RecommendedAction == ActionKind.Permanent)
                    File.Delete(NativeMethods.ToExtendedPath(item.Path));
                else if (NativeMethods.DirectoryExists(item.Path))
                    FileSystem.DeleteDirectory(item.Path, UIOption.OnlyErrorDialogs, RecycleOption.SendToRecycleBin, UICancelOption.ThrowException);
                else
                    FileSystem.DeleteFile(item.Path, UIOption.OnlyErrorDialogs, RecycleOption.SendToRecycleBin, UICancelOption.ThrowException);
                return new ActionResult { Path = item.Path, Status = ActionStatus.Success,
                    Message = item.RecommendedAction == ActionKind.Permanent ? "已永久删除" : "已移入回收站", EstimatedBytes = item.SizeBytes };
            }
            catch (OperationCanceledException) { return Skip(item, "用户取消操作"); }
            catch (Exception ex) { return Fail(item, ex.Message); }
        }

        public static bool IsAdministrator()
        {
            using (var identity = WindowsIdentity.GetCurrent())
                return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        }

        private static ActionResult Skip(CandidateRecord item, string message)
        {
            return new ActionResult { Path = item == null ? string.Empty : item.Path, Status = ActionStatus.Skipped, Message = message, EstimatedBytes = item == null ? 0 : item.SizeBytes };
        }

        private static ActionResult Fail(CandidateRecord item, string message)
        {
            return new ActionResult { Path = item == null ? string.Empty : item.Path, Status = ActionStatus.Failed, Message = message, EstimatedBytes = item == null ? 0 : item.SizeBytes };
        }
    }
}
