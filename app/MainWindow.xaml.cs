using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using DiskCleanupAssistant.Cleanup;
using DiskCleanupAssistant.Models;
using DiskCleanupAssistant.Rules;
using DiskCleanupAssistant.Scanning;
using DiskCleanupAssistant.Services;
using DiskCleanupAssistant.Uninstall;
using DiskCleanupAssistant.Updates;
using DiskCleanupAssistant.WindowsIntegration;

namespace DiskCleanupAssistant
{
    public partial class MainWindow : Window
    {
        private readonly RuleEngine _ruleEngine = new RuleEngine();
        private readonly CandidateScanner _scanner;
        private readonly DuplicateScanner _duplicateScanner;
        private readonly CleanupExecutor _cleanupExecutor;
        private readonly InstalledAppProvider _appsProvider = new InstalledAppProvider();
        private readonly LocalDataStore _store = new LocalDataStore();
        private readonly UpdateChecker _updateChecker = new UpdateChecker();
        private readonly ObservableCollection<CandidateRecord> _candidates = new ObservableCollection<CandidateRecord>();
        private readonly ObservableCollection<CandidateRecord> _duplicates = new ObservableCollection<CandidateRecord>();
        private readonly ObservableCollection<InstalledAppRecord> _apps = new ObservableCollection<InstalledAppRecord>();
        private readonly ObservableCollection<CleanupHistoryEntry> _history = new ObservableCollection<CleanupHistoryEntry>();
        private readonly string[] _titles = { "首页", "快速清理", "深度扫盘", "大文件与疑似项", "重复文件", "软件卸载", "清理记录", "设置" };
        private CancellationTokenSource _scanCancellation;
        private ScanPauseController _scanPause;
        private ICollectionView _largeView;
        private ICollectionView _appsView;
        private bool _busy;
        private AppSettings _settings;

        public MainWindow()
        {
            InitializeComponent();
            SetSelectedNavigation(0);
            _scanner = new CandidateScanner(_ruleEngine);
            _duplicateScanner = new DuplicateScanner(_ruleEngine);
            _cleanupExecutor = new CleanupExecutor(_ruleEngine);
            CandidateGrid.ItemsSource = _candidates;
            DeepGrid.ItemsSource = _candidates;
            DuplicateGrid.ItemsSource = _duplicates;
            AppsGrid.ItemsSource = _apps;
            HistoryGrid.ItemsSource = _history;
            _largeView = new ListCollectionView(_candidates);
            _largeView.Filter = IsLargeOrResidual;
            LargeGrid.ItemsSource = _largeView;
            _appsView = CollectionViewSource.GetDefaultView(_apps);
            _appsView.Filter = FilterApp;
            UpdateElevationState();
            DataPathText.Text = LocalDataStore.Root;
            _settings = _store.LoadSettings();
            ApplySettingsToUi();
            RefreshOverview();
            LoadHistory();
            RefreshApps();
            ContentRendered += async (sender, args) => await CheckUpdateInternalAsync(false);
        }

        private void Nav_Click(object sender, RoutedEventArgs e)
        {
            var button = sender as Button;
            int index;
            if (button == null || !int.TryParse(button.Tag.ToString(), out index)) return;
            NavigateToPage(index);
        }

        internal void NavigateToPage(int index)
        {
            if (index < 0 || index >= _titles.Length) index = 0;
            PageTabs.SelectedIndex = index;
            PageTitle.Text = _titles[index];
            SetSelectedNavigation(index);
        }

        private async void QuickScan_Click(object sender, RoutedEventArgs e)
        {
            await StartQuickScanAsync(SelectedQuickRoots());
        }

        private void DriveCard_Click(object sender, MouseButtonEventArgs e)
        {
            if (_busy) return;
            var card = sender as Border;
            var root = card == null ? null : Convert.ToString(card.Tag);
            if (string.IsNullOrWhiteSpace(root)) return;
            SelectDriveChoice(QuickDriveSelector, root);
            NavigateToPage(1);
            StatusText.Text = "已选择 " + root + "；点击“开始快速扫描”后进行只读扫描。";
        }

        private void QuickDriveSelector_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            var choice = QuickDriveSelector.SelectedItem as DriveChoice;
            UpdateDriveCardSelection(choice == null ? Enumerable.Empty<string>() : choice.Roots);
        }

        private async Task StartQuickScanAsync(List<string> roots)
        {
            if (_busy) return;
            if (roots == null || roots.Count == 0) { MessageBox.Show("没有可扫描的本地磁盘。", "磁盘清理助手"); return; }
            PageTabs.SelectedIndex = 1;
            PageTitle.Text = _titles[1];
            SetSelectedNavigation(1);
            await RunScanAsync((token, pause) => _scanner.QuickScanAsync(roots, CreateProgress(_candidates), pause, token),
                "正在扫描所选磁盘的已知垃圾与缓存…");
        }

        private async void DeepScan_Click(object sender, RoutedEventArgs e)
        {
            if (_busy) return;
            var roots = SelectedRoots();
            if (roots.Count == 0) { MessageBox.Show("没有可扫描的本地磁盘。", "磁盘清理助手"); return; }
            var threshold = SelectedLong(LargeThreshold, _settings.LargeFileThresholdBytes);
            await RunScanAsync((token, pause) => _scanner.DeepScanAsync(roots, threshold, CreateProgress(_candidates), pause, token), "正在深度扫描；候选会持续显示…");
        }

        private async void DuplicateScan_Click(object sender, RoutedEventArgs e)
        {
            if (_busy) return;
            var roots = SelectedRoots();
            if (roots.Count == 0) return;
            SetBusy(true, "正在查找大文件并计算重复哈希…");
            _scanCancellation = new CancellationTokenSource();
            _scanPause = new ScanPauseController();
            _duplicates.Clear();
            try
            {
                var snapshot = await _duplicateScanner.ScanAsync(roots, _settings.DuplicateFileThresholdBytes, CreateProgress(_duplicates), _scanPause, _scanCancellation.Token);
                ReplaceCollection(_duplicates, snapshot.Candidates);
                StatusText.Text = string.Format("重复扫描完成：{0} 组，检查 {1:N0} 项，权限跳过 {2} 项，耗时 {3:0.0} 秒。",
                    snapshot.Candidates.Count, snapshot.EntriesExamined, snapshot.PermissionSkips, snapshot.ElapsedSeconds);
                _store.SaveSnapshot(snapshot);
            }
            catch (OperationCanceledException) { StatusText.Text = "重复文件扫描已取消；当前显示的是不完整结果，未执行任何清理。"; }
            catch (Exception ex) { ShowError("重复文件扫描失败", ex); }
            finally { DisposeScanControl(); SetBusy(false, null); }
        }

        private async Task<bool> RunScanAsync(Func<CancellationToken, ScanPauseController, Task<ScanSnapshot>> action, string message)
        {
            SetBusy(true, message);
            _scanCancellation = new CancellationTokenSource();
            _scanPause = new ScanPauseController();
            ScanProgressBar.IsIndeterminate = false;
            ScanProgressBar.Maximum = 100;
            ScanProgressBar.Value = 0;
            _candidates.Clear();
            UpdateQuickSummary();
            UpdateSelectionControls();
            var completed = false;
            try
            {
                var snapshot = await action(_scanCancellation.Token, _scanPause);
                ReplaceCollection(_candidates, snapshot.Candidates);
                foreach (var candidate in _candidates) HookCandidate(candidate);
                _largeView.Refresh();
                UpdateQuickSummary();
                if (snapshot.Candidates.Count > 0)
                {
                    CandidateGrid.SelectedIndex = 0;
                    CandidateGrid.ScrollIntoView(snapshot.Candidates[0]);
                }
                else
                {
                    ShowCandidateDetails(null);
                }
                _store.SaveSnapshot(snapshot);
                StatusText.Text = string.Format("扫描完成：发现 {0} 项，检查 {1:N0} 项，权限跳过 {2} 项，耗时 {3:0.0} 秒。所有候选默认未选择。",
                    snapshot.Candidates.Count, snapshot.EntriesExamined, snapshot.PermissionSkips, snapshot.ElapsedSeconds);
                completed = true;
            }
            catch (OperationCanceledException) { StatusText.Text = "扫描已取消；当前显示的是不完整结果，未执行任何清理。"; }
            catch (Exception ex) { ShowError("扫描失败", ex); }
            finally { DisposeScanControl(); SetBusy(false, null); }
            return completed;
        }

        private IProgress<ScanProgress> CreateProgress(ObservableCollection<CandidateRecord> liveTarget)
        {
            return new Progress<ScanProgress>(p =>
            {
                if (p.FoundCandidate != null && !liveTarget.Any(c => string.Equals(c.Path, p.FoundCandidate.Path, StringComparison.OrdinalIgnoreCase)))
                {
                    liveTarget.Add(p.FoundCandidate);
                    if (ReferenceEquals(liveTarget, _candidates)) HookCandidate(p.FoundCandidate);
                }
                if (ReferenceEquals(liveTarget, _candidates))
                {
                    UpdateQuickSummary();
                    UpdateSelectionControls();
                }
                if (p.TotalSteps > 0)
                {
                    ScanProgressBar.IsIndeterminate = false;
                    ScanProgressBar.Maximum = p.TotalSteps;
                    ScanProgressBar.Value = Math.Min(p.CompletedSteps, p.TotalSteps);
                    StatusText.Text = string.Format("扫描进度 {0}/{1}：已检查 {2:N0} 项，发现 {3} 个候选，权限跳过 {4} 项：{5}",
                        p.CompletedSteps, p.TotalSteps, p.EntriesExamined, p.CandidatesFound, p.PermissionSkips, p.CurrentPath);
                }
                else
                {
                    ScanProgressBar.IsIndeterminate = true;
                    StatusText.Text = string.Format("已检查 {0:N0} 项，发现 {1} 个候选，权限跳过 {2} 项：{3}",
                        p.EntriesExamined, p.CandidatesFound, p.PermissionSkips, p.CurrentPath);
                }
            });
        }

        private void UpdateQuickSummary()
        {
            var foundBytes = _candidates.Sum(c => c.SizeBytes);
            var safe = _candidates.Where(c => _ruleEngine.MayAutoSelect(c, SelectionTier.Conservative)).ToList();
            var cacheBytes = _candidates.Where(c => c.Category == CandidateCategory.RebuildableCache).Sum(c => c.SizeBytes);
            var reviewCount = _candidates.Count(c => !_ruleEngine.MayAutoSelect(c, SelectionTier.Balanced));

            QuickFoundValue.Text = _candidates.Count == 0 ? "尚未发现" : SizeFormatter.Format(foundBytes) + " · " + _candidates.Count + " 项";
            QuickSafeValue.Text = safe.Count == 0 ? "0 B" : SizeFormatter.Format(safe.Sum(c => c.SizeBytes)) + " · " + safe.Count + " 项";
            QuickCacheValue.Text = cacheBytes == 0 ? "0 B" : SizeFormatter.Format(cacheBytes);
            QuickReviewValue.Text = reviewCount == 0 ? "0 项" : reviewCount + " 项";
        }

        private void ConservativeSelect_Click(object sender, RoutedEventArgs e)
        {
            foreach (var item in _candidates) item.IsSelected = _ruleEngine.MayAutoSelect(item, SelectionTier.Conservative);
            var selected = _candidates.Where(c => c.IsSelected).ToList();
            StatusText.Text = string.Format("保守选择了 {0} 项，预计 {1}。未知文件、个人资料、软件残留、重复项和系统托管项未选择。",
                selected.Count, SizeFormatter.Format(selected.Sum(c => c.SizeBytes)));
            UpdateSelectionControls();
        }

        private void ToggleSelectAll_Click(object sender, RoutedEventArgs e)
        {
            var selectable = _candidates.Where(IsSelectableForCleanup).ToList();
            var shouldSelect = selectable.Count > 0 && !selectable.All(c => c.IsSelected);
            foreach (var item in _candidates) item.IsSelected = shouldSelect && IsSelectableForCleanup(item);
            StatusText.Text = shouldSelect
                ? "已选择当前列表中所有可执行项目。请先阅读重要项目的说明，再点击清理已选。"
                : "已取消全选。";
            UpdateSelectionControls();
        }

        private static bool IsSelectableForCleanup(CandidateRecord item)
        {
            return item != null && (item.RecommendedAction == ActionKind.Recycle || item.RecommendedAction == ActionKind.OfficialTool);
        }

        private void HookCandidate(CandidateRecord item)
        {
            if (item == null) return;
            item.PropertyChanged -= CandidateSelectionChanged;
            item.PropertyChanged += CandidateSelectionChanged;
        }

        private void CandidateSelectionChanged(object sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == "IsSelected") UpdateSelectionControls();
        }

        private void UpdateSelectionControls()
        {
            if (QuickScanButton == null) return;
            var hasCandidates = _candidates.Count > 0;
            var selectedCount = _candidates.Count(c => c.IsSelected);
            var selectable = _candidates.Where(IsSelectableForCleanup).ToList();
            var allSelected = selectable.Count > 0 && selectable.All(c => c.IsSelected);
            var scanning = _scanCancellation != null;

            QuickScanButton.Content = hasCandidates ? "重新扫描" : "开始快速扫描";
            QuickScanButton.IsEnabled = !_busy;
            ConservativeSelectButton.IsEnabled = hasCandidates && !_busy;
            ToggleSelectAllButton.IsEnabled = selectable.Count > 0 && !_busy;
            ToggleSelectAllButton.Content = allSelected ? "取消全选" : "全部选择";
            CleanSelectedButton.IsEnabled = selectedCount > 0 && !_busy;
            CleanSelectedButton.Content = selectedCount > 0 ? "清理已选(" + selectedCount + ")" : "清理已选";
            QuickCancelScanButton.Visibility = scanning ? Visibility.Visible : Visibility.Collapsed;

            QuickScanButton.Style = hasCandidates ? (Style)FindResource(typeof(Button)) : (Style)FindResource("PrimaryButton");
            CleanSelectedButton.Style = hasCandidates ? (Style)FindResource("PrimaryButton") : (Style)FindResource(typeof(Button));
        }

        private async void ExecuteCleanup_Click(object sender, RoutedEventArgs e)
        {
            await ExecuteSelectedAsync(false);
        }

        private async void PermanentCleanup_Click(object sender, RoutedEventArgs e)
        {
            await ExecuteSelectedAsync(true);
        }

        private async Task ExecuteSelectedAsync(bool permanent)
        {
            if (_busy) return;
            var selected = _candidates.Where(c => c.IsSelected).ToList();
            if (selected.Count == 0) { MessageBox.Show("请先查看说明并主动选择候选项。", "没有已选项目"); return; }
            var unsupported = selected.Where(c => c.RecommendedAction != ActionKind.Recycle && (!(!permanent && c.RecommendedAction == ActionKind.OfficialTool))).ToList();
            if (unsupported.Count > 0)
            {
                MessageBox.Show("所选项目包含仅供查看的高风险内容，已停止执行。请取消这些项目后重试。", "安全边界", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var lines = selected.Take(12).Select(c => c.Id + "  " + c.SizeDisplay + "  " + c.Path).ToList();
            if (selected.Count > 12) lines.Add("……另有 " + (selected.Count - 12) + " 项");
            var prompt = "即将处理以下精确目标：\n\n" + string.Join("\n", lines) +
                         "\n\n预计大小：" + SizeFormatter.Format(selected.Sum(c => c.SizeBytes)) +
                         (permanent ? "\n这些目标将永久删除，无法从回收站恢复。是否继续？" : "\n普通文件将进入回收站；系统托管项只打开官方入口。是否继续？");
            if (MessageBox.Show(prompt, permanent ? "第一次确认：永久删除" : "确认清理计划", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
            if (permanent && MessageBox.Show("这是第二次确认。永久删除后无法由本软件恢复。确定执行上述精确路径吗？",
                "第二次确认：不可恢复", MessageBoxButton.YesNo, MessageBoxImage.Stop) != MessageBoxResult.Yes) return;

            var originalActions = selected.ToDictionary(c => c, c => c.RecommendedAction);
            if (permanent) foreach (var item in selected) item.RecommendedAction = ActionKind.Permanent;

            SetBusy(true, permanent ? "正在再次复核并永久删除已选项目…" : "正在即时复核并处理已选项目…");
            var before = TotalFreeBytes();
            var results = new List<ActionResult>();
            try
            {
                var normal = selected.Where(c => !c.RequiresElevation || CleanupExecutor.IsAdministrator()).ToList();
                var elevated = selected.Where(c => c.RequiresElevation && !CleanupExecutor.IsAdministrator()).ToList();
                if (normal.Count > 0) results.AddRange(await _cleanupExecutor.ExecuteAsync(normal, CancellationToken.None));
                if (elevated.Count > 0) results.AddRange(await ElevatedPipeProtocol.ExecuteElevatedAsync(elevated, CancellationToken.None));
                _store.AppendResults(results);
                LoadHistory();
                var after = TotalFreeBytes();
                foreach (var result in results.Where(r => r.Status == ActionStatus.Success))
                {
                    var candidate = _candidates.FirstOrDefault(c => string.Equals(c.Path, result.Path, StringComparison.OrdinalIgnoreCase));
                    if (candidate != null) candidate.IsSelected = false;
                }
                RefreshOverview();
                var success = results.Count(r => r.Status == ActionStatus.Success);
                var skipped = results.Count(r => r.Status == ActionStatus.Skipped);
                var failed = results.Count(r => r.Status == ActionStatus.Failed);
                var delta = Math.Max(0, after - before);
                StatusText.Text = string.Format("处理完成：成功 {0}，跳过 {1}，失败 {2}；磁盘可用空间实测增加 {3}。", success, skipped, failed, SizeFormatter.Format(delta));
                MessageBox.Show(StatusText.Text + "\n\n详细结果已写入“清理记录”。", "清理完成", MessageBoxButton.OK,
                    failed > 0 ? MessageBoxImage.Warning : MessageBoxImage.Information);
            }
            catch (Win32Exception ex) when (ex.NativeErrorCode == 1223) { StatusText.Text = "用户取消了管理员授权，未处理需要提权的项目。"; }
            catch (Exception ex) { ShowError("清理执行失败", ex); }
            finally
            {
                foreach (var pair in originalActions) pair.Key.RecommendedAction = pair.Value;
                SetBusy(false, null);
            }
        }

        private void EmptyRecycleBin_Click(object sender, RoutedEventArgs e)
        {
            if (_busy) return;
            if (MessageBox.Show("将永久清空所有本地磁盘的回收站。回收站中的个人文件也会被删除。是否继续？",
                "第一次确认：清空回收站", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
            if (MessageBox.Show("这是第二次确认。清空后无法由本软件恢复。确定继续吗？",
                "第二次确认：不可恢复", MessageBoxButton.YesNo, MessageBoxImage.Stop) != MessageBoxResult.Yes) return;
            var before = TotalFreeBytes();
            try
            {
                NativeMethods.EmptyRecycleBin();
                var delta = Math.Max(0, TotalFreeBytes() - before);
                var result = new ActionResult { Path = "所有本地磁盘回收站", Status = ActionStatus.Success, Message = "已永久清空回收站", EstimatedBytes = delta };
                _store.AppendResults(new[] { result });
                LoadHistory();
                RefreshOverview();
                StatusText.Text = "回收站已清空；磁盘可用空间实测增加 " + SizeFormatter.Format(delta) + "。";
            }
            catch (Exception ex) { ShowError("清空回收站失败", ex); }
        }

        private void CandidateGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            ShowCandidateDetails(CandidateGrid.SelectedItem as CandidateRecord);
        }

        private void ShowCandidateDetails(CandidateRecord item)
        {
            var hasItem = item != null;
            EvidenceText.Text = hasItem ? item.EvidenceDisplay : "请在上方单击一个项目，查看它为什么会被列出。";
            ImpactText.Text = hasItem ? DetailValue(item.DeletionImpact, "影响尚未确认，不建议清理。") : "选择项目后，说明清理后会发生什么。";
            RecommendationText.Text = hasItem ? DetailValue(item.Recommendation, "先打开所在位置人工确认") + "（" + item.ActionDisplay + "）" : "未确认用途的项目不会被保守选择。";

            var color = hasItem ? (Brush)FindResource("TextBrush") : (Brush)FindResource("MutedBrush");
            EvidenceText.Foreground = color;
            ImpactText.Foreground = color;
            RecommendationText.Foreground = color;
        }

        private static string DetailValue(string value, string fallback)
        {
            return string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();
        }

        private void ElevationButton_Click(object sender, RoutedEventArgs e)
        {
            if (CleanupExecutor.IsAdministrator())
            {
                StatusText.Text = "当前已经是管理员权限。";
                return;
            }

            try
            {
                var executable = Process.GetCurrentProcess().MainModule.FileName;
                var started = Process.Start(new ProcessStartInfo(executable, "--page " + PageTabs.SelectedIndex)
                {
                    UseShellExecute = true,
                    Verb = "runas",
                    WorkingDirectory = AppDomain.CurrentDomain.BaseDirectory
                });
                if (started == null) throw new InvalidOperationException("Windows 未能启动管理员实例");
                Close();
            }
            catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
            {
                StatusText.Text = "已取消管理员授权，仍以普通权限运行。";
            }
            catch (Exception ex)
            {
                ShowError("无法切换到管理员权限", ex);
            }
        }

        private void UpdateElevationState()
        {
            var elevated = CleanupExecutor.IsAdministrator();
            AdminBadge.Text = elevated ? "管理员权限" : "普通权限";
            ElevationButton.ToolTip = elevated ? "当前已是管理员权限" : "点击后由 Windows 请求管理员授权";
            ElevationButton.SetValue(AutomationProperties.NameProperty, elevated ? "管理员权限" : "切换到管理员权限");
            ElevationShieldGlyph.Foreground = (Brush)FindResource(elevated ? "AccentBrush" : "MutedBrush");
            AdminBadge.Foreground = (Brush)FindResource(elevated ? "AccentBrush" : "TextBrush");
        }

        private void DuplicateGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            var item = DuplicateGrid.SelectedItem as CandidateRecord;
            if (item != null) StatusText.Text = "该组全部路径：" + string.Join("  |  ", item.Paths);
        }

        private void OpenSelectedPath_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            var grid = sender as DataGrid;
            var item = grid == null ? null : grid.SelectedItem as CandidateRecord;
            if (item == null) return;
            try
            {
                var args = File.Exists(item.Path) ? "/select,\"" + item.Path + "\"" : "\"" + item.Path + "\"";
                Process.Start(new ProcessStartInfo("explorer.exe", args) { UseShellExecute = true });
            }
            catch (Exception ex) { ShowError("无法打开路径", ex); }
        }

        private void CancelScan_Click(object sender, RoutedEventArgs e)
        {
            if (_scanCancellation != null) _scanCancellation.Cancel();
        }

        private void PauseScan_Click(object sender, RoutedEventArgs e)
        {
            if (!_busy || _scanPause == null) return;
            if (_scanPause.IsPaused)
            {
                _scanPause.Resume();
                StatusText.Text = "扫描已继续。";
            }
            else
            {
                _scanPause.Pause();
                StatusText.Text = "扫描已暂停；可以继续或取消。";
            }
        }

        private void DisposeScanControl()
        {
            if (_scanPause != null) _scanPause.Dispose();
            if (_scanCancellation != null) _scanCancellation.Dispose();
            _scanPause = null;
            _scanCancellation = null;
        }

        private void RefreshOverview_Click(object sender, RoutedEventArgs e) { RefreshOverview(); }

        private void RefreshOverview()
        {
            var rows = DriveInfo.GetDrives().Where(d => d.DriveType == DriveType.Fixed && d.IsReady).Select(d => new DriveRow
            {
                Root = d.RootDirectory.FullName, Name = d.Name, Label = d.VolumeLabel, Total = SizeFormatter.Format(d.TotalSize), Free = SizeFormatter.Format(d.AvailableFreeSpace),
                Used = SizeFormatter.Format(d.TotalSize - d.AvailableFreeSpace),
                UsedPercent = d.TotalSize == 0 ? 0d : (d.TotalSize - d.AvailableFreeSpace) * 100d / d.TotalSize,
                Percent = d.TotalSize == 0 ? "0%" : ((d.TotalSize - d.AvailableFreeSpace) * 100d / d.TotalSize).ToString("0.0") + "%",
                IsCritical = d.TotalSize > 0 && (d.TotalSize - d.AvailableFreeSpace) * 100d / d.TotalSize >= 90d
            }).ToList();
            DriveGrid.ItemsSource = rows;
            var drives = DriveInfo.GetDrives().Where(d => d.DriveType == DriveType.Fixed && d.IsReady).ToList();
            var systemRoot = Path.GetPathRoot(Environment.GetFolderPath(Environment.SpecialFolder.Windows));
            PopulateDriveSelector(DriveSelector, drives, systemRoot);
            PopulateDriveSelector(QuickDriveSelector, drives, systemRoot);
            UpdateDriveCardSelection(SelectedQuickRoots());
        }

        private void UpdateDriveCardSelection(IEnumerable<string> roots)
        {
            var selectedRoots = new HashSet<string>(roots ?? Enumerable.Empty<string>(), StringComparer.OrdinalIgnoreCase);
            var rows = DriveGrid.ItemsSource as IEnumerable<DriveRow>;
            if (rows == null) return;
            foreach (var row in rows) row.IsSelected = selectedRoots.Contains(row.Root);
            DriveGrid.Items.Refresh();
        }

        private static void PopulateDriveSelector(ComboBox selector, IReadOnlyList<DriveInfo> drives, string defaultRoot)
        {
            var previous = selector.SelectedItem as DriveChoice;
            var previousRoot = previous != null && previous.Roots.Count == 1 ? previous.Roots[0] : null;
            selector.Items.Clear();
            selector.Items.Add(new DriveChoice
            {
                Label = "所有本地磁盘",
                Roots = drives.Select(drive => drive.RootDirectory.FullName).ToList()
            });
            foreach (var drive in drives)
                selector.Items.Add(new DriveChoice
                {
                    Label = drive.Name + (string.IsNullOrWhiteSpace(drive.VolumeLabel) ? string.Empty : "  " + drive.VolumeLabel),
                    Roots = new List<string> { drive.RootDirectory.FullName }
                });
            if (!string.IsNullOrWhiteSpace(previousRoot))
            {
                var match = selector.Items.Cast<DriveChoice>().FirstOrDefault(choice => choice.Roots.Count == 1 &&
                    string.Equals(choice.Roots[0], previousRoot, StringComparison.OrdinalIgnoreCase));
                selector.SelectedItem = match;
            }
            if (selector.SelectedIndex < 0 && !string.IsNullOrWhiteSpace(defaultRoot))
            {
                var defaultChoice = selector.Items.Cast<DriveChoice>().FirstOrDefault(choice => choice.Roots.Count == 1 &&
                    string.Equals(choice.Roots[0], defaultRoot, StringComparison.OrdinalIgnoreCase));
                selector.SelectedItem = defaultChoice;
            }
            if (selector.SelectedIndex < 0) selector.SelectedIndex = 0;
        }

        private static void SelectDriveChoice(ComboBox selector, string root)
        {
            var match = selector.Items.Cast<DriveChoice>().FirstOrDefault(choice => choice.Roots.Count == 1 &&
                string.Equals(choice.Roots[0], root, StringComparison.OrdinalIgnoreCase));
            if (match != null) selector.SelectedItem = match;
        }

        private List<string> SelectedRoots()
        {
            var choice = DriveSelector.SelectedItem as DriveChoice;
            return choice == null ? new List<string>() : choice.Roots;
        }

        private List<string> SelectedQuickRoots()
        {
            var choice = QuickDriveSelector.SelectedItem as DriveChoice;
            return choice == null ? new List<string>() : choice.Roots;
        }

        private void RefreshApps_Click(object sender, RoutedEventArgs e) { RefreshApps(); }
        private void RefreshApps()
        {
            ReplaceCollection(_apps, _appsProvider.GetInstalledApps());
            if (_appsView != null) _appsView.Refresh();
        }

        private async void UninstallSelected_Click(object sender, RoutedEventArgs e)
        {
            var app = AppsGrid.SelectedItem as InstalledAppRecord;
            if (app == null) { MessageBox.Show("请先选择一个软件。", "软件卸载"); return; }
            if (MessageBox.Show("将调用“" + app.DisplayName + "”登记的官方卸载程序。磁盘清理助手不会直接删除安装目录。是否继续？",
                "确认调用官方卸载", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
            try
            {
                var process = _appsProvider.StartUninstall(app);
                StatusText.Text = "已启动 " + app.DisplayName + " 的官方卸载程序；完成后可点击“检查所选软件残留”。";
                await Task.Delay(1500);
                RefreshApps();
            }
            catch (Exception ex) { ShowError("无法启动官方卸载程序", ex); }
        }

        private async void ScanResiduals_Click(object sender, RoutedEventArgs e)
        {
            var app = AppsGrid.SelectedItem as InstalledAppRecord;
            if (app == null) { MessageBox.Show("请先选择软件；如果卸载后列表已刷新，请在卸载前完成此检查。", "残留检查"); return; }
            SetBusy(true, "正在只读检查 " + app.DisplayName + " 的疑似残留…");
            try
            {
                var residuals = await _appsProvider.FindResidualsAsync(app, CancellationToken.None);
                foreach (var item in residuals) _candidates.Add(item);
                _largeView.Refresh();
                PageTabs.SelectedIndex = 3;
                PageTitle.Text = _titles[3];
                SetSelectedNavigation(3);
                StatusText.Text = "发现 " + residuals.Count + " 个疑似残留；全部默认未选择，只供进一步判断。";
            }
            catch (Exception ex) { ShowError("残留检查失败", ex); }
            finally { SetBusy(false, null); }
        }

        private void AppFilter_TextChanged(object sender, TextChangedEventArgs e) { if (_appsView != null) _appsView.Refresh(); }
        private bool FilterApp(object value)
        {
            var app = value as InstalledAppRecord;
            var filter = AppFilter == null ? string.Empty : AppFilter.Text.Trim();
            if (app == null || filter.Length == 0) return true;
            return (app.DisplayName ?? string.Empty).IndexOf(filter, StringComparison.CurrentCultureIgnoreCase) >= 0 ||
                   (app.Publisher ?? string.Empty).IndexOf(filter, StringComparison.CurrentCultureIgnoreCase) >= 0;
        }

        private bool IsLargeOrResidual(object value)
        {
            var item = value as CandidateRecord;
            return item != null && (item.Category == CandidateCategory.LargeSuspicious || item.Category == CandidateCategory.Protected || item.Category == CandidateCategory.SoftwareResidual);
        }

        private void LoadHistory()
        {
            ReplaceCollection(_history, _store.LoadHistory());
        }

        private async void CheckUpdate_Click(object sender, RoutedEventArgs e)
        {
            await CheckUpdateInternalAsync(true);
        }

        private async Task CheckUpdateInternalAsync(bool showNoUpdate)
        {
            if (!_updateChecker.IsConfigured)
            {
                if (showNoUpdate) MessageBox.Show("开发版尚未注入正式发布公钥。签名更新检查将在公开构建流水线配置后启用。", "更新检查");
                return;
            }
            if (!_settings.UpdateChecksEnabled && !showNoUpdate) return;
            if (_settings.LastUpdateCheckUtc.HasValue && DateTime.UtcNow - _settings.LastUpdateCheckUtc.Value < TimeSpan.FromDays(1))
            {
                if (showNoUpdate) MessageBox.Show("今天已经检查过更新，不会重复联网。", "更新检查");
                return;
            }
            _settings.LastUpdateCheckUtc = DateTime.UtcNow;
            _store.SaveSettings(_settings);
            try
            {
                var update = await _updateChecker.CheckAsync(CancellationToken.None);
                if (update == null)
                {
                    if (showNoUpdate) MessageBox.Show("当前已经是最新版本。", "更新检查");
                }
                else if (MessageBox.Show("发现新版本 " + update.Version + "，是否打开官方发布页？", "发现更新", MessageBoxButton.YesNo) == MessageBoxResult.Yes)
                    Process.Start(new ProcessStartInfo(update.ReleaseNotesUrl) { UseShellExecute = true });
            }
            catch (Exception ex) { ShowError("更新检查失败", ex); }
        }

        private void SaveSettings_Click(object sender, RoutedEventArgs e)
        {
            _settings.LargeFileThresholdBytes = SelectedLong(SettingsLargeThreshold, 512L * 1024 * 1024);
            _settings.DuplicateFileThresholdBytes = SelectedLong(DuplicateThreshold, 50L * 1024 * 1024);
            _settings.HistoryRetentionDays = (int)SelectedLong(HistoryRetention, 30);
            _settings.UpdateChecksEnabled = EnableUpdateChecks.IsChecked == true;
            _store.SaveSettings(_settings);
            SelectByTag(LargeThreshold, _settings.LargeFileThresholdBytes);
            LoadHistory();
            StatusText.Text = "设置已保存。";
        }

        private void ApplySettingsToUi()
        {
            EnableUpdateChecks.IsChecked = _settings.UpdateChecksEnabled;
            SelectByTag(LargeThreshold, _settings.LargeFileThresholdBytes);
            SelectByTag(SettingsLargeThreshold, _settings.LargeFileThresholdBytes);
            SelectByTag(DuplicateThreshold, _settings.DuplicateFileThresholdBytes);
            SelectByTag(HistoryRetention, _settings.HistoryRetentionDays);
        }

        private static void SelectByTag(ComboBox box, long value)
        {
            foreach (var raw in box.Items)
            {
                var item = raw as ComboBoxItem;
                long tag;
                if (item != null && long.TryParse(item.Tag.ToString(), out tag) && tag == value) { box.SelectedItem = item; return; }
            }
            if (box.Items.Count > 0) box.SelectedIndex = 0;
        }

        private static long SelectedLong(ComboBox box, long fallback)
        {
            var item = box.SelectedItem as ComboBoxItem;
            long value;
            return item != null && long.TryParse(item.Tag.ToString(), out value) ? value : fallback;
        }

        private void ClearLocalData_Click(object sender, RoutedEventArgs e)
        {
            if (MessageBox.Show("将删除本软件保存的扫描快照和最近30天清理记录，不会删除其他文件。是否继续？",
                "清除本地数据", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
            try { _store.ClearAll(); _history.Clear(); StatusText.Text = "已清除磁盘清理助手的本地数据。"; }
            catch (Exception ex) { ShowError("清除本地数据失败", ex); }
        }

        private void SetBusy(bool value, string message)
        {
            _busy = value;
            ScanProgressBar.Visibility = value ? Visibility.Visible : Visibility.Collapsed;
            ElevationButton.IsEnabled = !value;
            if (value)
            {
                ScanProgressBar.IsIndeterminate = true;
                ScanProgressBar.Value = 0;
            }
            if (!string.IsNullOrWhiteSpace(message)) StatusText.Text = message;
            UpdateSelectionControls();
        }

        private static void ReplaceCollection<T>(ObservableCollection<T> target, IEnumerable<T> values)
        {
            target.Clear();
            foreach (var value in values) target.Add(value);
        }

        private static long TotalFreeBytes()
        {
            return DriveInfo.GetDrives().Where(d => d.DriveType == DriveType.Fixed && d.IsReady).Sum(d => d.AvailableFreeSpace);
        }

        private void ShowError(string title, Exception ex)
        {
            StatusText.Text = title + "：" + ex.Message;
            MessageBox.Show(ex.Message, title, MessageBoxButton.OK, MessageBoxImage.Error);
        }

        private void SetSelectedNavigation(int index)
        {
            foreach (var button in NavPanel.Children.OfType<Button>())
            {
                int tag;
                var selected = int.TryParse(Convert.ToString(button.Tag), out tag) && tag == index;
                button.Style = (Style)FindResource(selected ? "NavButtonSelected" : "NavButton");
            }

            SettingsNavButton.Style = (Style)FindResource(index == 7 ? "NavButtonSelected" : "NavButton");
            PageHeader.Visibility = index == 0 ? Visibility.Collapsed : Visibility.Visible;
            PageHeaderRow.Height = index == 0 ? new GridLength(0) : new GridLength(46);
            StatusBar.Visibility = index == 0 ? Visibility.Collapsed : Visibility.Visible;
            StatusBarRow.Height = index == 0 ? new GridLength(0) : new GridLength(34);
        }

        private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton != MouseButton.Left) return;
            if (e.ClickCount == 2)
            {
                ToggleMaximize();
                return;
            }

            try { DragMove(); }
            catch (InvalidOperationException) { }
        }

        private void MinimizeWindow_Click(object sender, RoutedEventArgs e) { WindowState = WindowState.Minimized; }
        private void MaximizeWindow_Click(object sender, RoutedEventArgs e) { ToggleMaximize(); }
        private void CloseWindow_Click(object sender, RoutedEventArgs e) { Close(); }

        private void ToggleMaximize()
        {
            WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
        }

        private sealed class DriveRow
        {
            public string Root { get; set; } public string Name { get; set; } public string Label { get; set; } public string Total { get; set; }
            public string Used { get; set; } public string Free { get; set; } public string Percent { get; set; }
            public double UsedPercent { get; set; } public bool IsCritical { get; set; } public bool IsSelected { get; set; }
        }

        private sealed class DriveChoice
        {
            public string Label { get; set; } public List<string> Roots { get; set; }
            public override string ToString() { return Label; }
        }
    }
}
