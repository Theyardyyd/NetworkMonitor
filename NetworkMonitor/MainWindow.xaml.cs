
using Microsoft.Diagnostics.Tracing.Parsers;
using Microsoft.Diagnostics.Tracing.Session;
using Microsoft.VisualBasic.Logging;
using Microsoft.Win32; // 用于操作注册表开机自启
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing.Drawing2D;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Ink;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Media.Media3D;
using System.Windows.Shapes;
using System.Windows.Threading;
using static NetworkMonitor.DashboardSaveData;
using static NetworkMonitor.MainWindow;
using Forms = System.Windows.Forms;

namespace NetworkMonitor
{


    public partial class MainWindow : Window
    {


        public static int TooltipMode = 2; // 全局进程悬停解释模式 (0=不显示, 1=仅名称, 2=名称+描述)
        private bool _isChartDragPaused = false;
        private bool _isMarkerPaused = false;
        private bool _isChartPaused => _isChartDragPaused || _isMarkerPaused; // 联合状态机控制冻结
        // 飞线动画时间计数器
        private double _flyingLineTime = 0;

        // 3D & 2D 地图交互参数
        private bool _isMap2DDragging = false;
        private Point _map2DLastMousePos;

        private bool _isEarthRotating = false;
        private bool _isEarthPanning = false;
        private Point _earthLastMousePos;
        private double _earthManualRotX = 0; // 上下俯仰角
        private double _cameraX = 0;
        private double _cameraY = 0;
        private double _cameraDistance = 3.2;// 相机深度(缩放)
        private string? _customMapPath = null; // ★ 新增：记录自定义地图路径
        private DispatcherTimer _mapLodTimer;
        private double _currentBattery = 100;
        private bool _isCharging = false;
        // --- 资源监控数据数组 (N 是你代码中定义的常量，通常是 60 或 120) ---
        private double[] cpuData = new double[60];
        private double[] ramData = new double[60];
        private double[] batData = new double[60];
        private double[] gpuData = new double[60];  // ★ 新增：GPU 历史数据
        private double[] diskData = new double[60]; // ★ 新增：磁盘历史数据

        // --- 性能计数器 (用于获取硬件实时数据) ---
        private PerformanceCounter _diskCounter;
        private Dictionary<string, PerformanceCounter> _gpuCounters = new Dictionary<string, PerformanceCounter>();
        private Queue<double> _batSec = new Queue<double>(Enumerable.Repeat(100.0, 305));
        private Queue<double> _batMin = new Queue<double>(Enumerable.Repeat(100.0, 185));
        private Queue<double> _batHour = new Queue<double>(Enumerable.Repeat(100.0, 175));
        private Queue<double> _batDay = new Queue<double>(Enumerable.Repeat(100.0, 35));


        private MiniWindow _miniWindow;
        public ObservableCollection<DictItem> DictItemsList { get; set; } = new ObservableCollection<DictItem>();
        // --- 获取 GPU 核心利用率 (遍历3D引擎累加) ---
        private float GetGpuUsage()
        {
            try
            {
                var category = new PerformanceCounterCategory("GPU Engine");
                var instanceNames = category.GetInstanceNames();
                var currentInstances = new HashSet<string>();
                float total = 0;

                foreach (var name in instanceNames)
                {
                    if (name.EndsWith("engtype_3D", StringComparison.OrdinalIgnoreCase))
                    {
                        currentInstances.Add(name);
                        if (!_gpuCounters.TryGetValue(name, out var pc))
                        {
                            pc = new PerformanceCounter("GPU Engine", "Utilization Percentage", name);
                            pc.NextValue(); // 激活预热
                            _gpuCounters[name] = pc;
                        }
                        else total += pc.NextValue();
                    }
                }
                // 清理死掉的进程实例
                var dead = _gpuCounters.Keys.Except(currentInstances).ToList();
                foreach (var d in dead) { _gpuCounters[d].Dispose(); _gpuCounters.Remove(d); }

                return Math.Min(100f, total);
            }
            catch { return 0f; }
        }

        // --- 字典修改后同步覆盖至外部 JSON ---
        // --- 字典修改后同步覆盖至外部 JSON ---
        private void UpdateCustomDict()
        {
            var newDict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var item in DictItemsList)
            {
                if (!string.IsNullOrWhiteSpace(item.Key))
                    newDict[item.Key.Trim().ToLower().Replace(".exe", "")] = item.Value?.Trim() ?? "";
            }
            ProcessDictionary.LoadCustomDict(newDict);

            string dictPath = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "process_dict.json");
            File.WriteAllText(dictPath, JsonSerializer.Serialize(newDict, new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
        }

        // --- 新增：字典导出功能 ---
        private void BtnExportDict_Click(object sender, RoutedEventArgs e)
        {
            var sfd = new Microsoft.Win32.SaveFileDialog { Filter = "JSON 字典文件 (*.json)|*.json", FileName = "process_dict_export.json" };
            if (sfd.ShowDialog() == true)
            {
                string dictPath = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "process_dict.json");
                if (File.Exists(dictPath)) File.Copy(dictPath, sfd.FileName, true);
                MessageBox.Show("字典导出成功！", "成功", MessageBoxButton.OK, MessageBoxImage.Information);
            }
        }

        // --- 新增：面板显隐切换 ---
        private void ResToggle_Changed(object sender, RoutedEventArgs e)
        {
            if (!IsLoaded) return;
            _savedData.ShowCpu = ChkShowCpu.IsChecked == true;
            _savedData.ShowRam = ChkShowRam.IsChecked == true;
            _savedData.ShowBat = ChkShowBat.IsChecked == true;
            _savedData.ShowGpu = ChkShowGpu.IsChecked == true;
            _savedData.ShowDisk = ChkShowDisk.IsChecked == true;

            TxtCpu.Visibility = LineCpu.Visibility = _savedData.ShowCpu ? Visibility.Visible : Visibility.Collapsed;
            TxtRam.Visibility = LineRam.Visibility = _savedData.ShowRam ? Visibility.Visible : Visibility.Collapsed;
            TxtBattery.Visibility = LineBattery.Visibility = _savedData.ShowBat ? Visibility.Visible : Visibility.Collapsed;
            TxtGpu.Visibility = LineGpu.Visibility = _savedData.ShowGpu ? Visibility.Visible : Visibility.Collapsed;
            TxtDisk.Visibility = LineDisk.Visibility = _savedData.ShowDisk ? Visibility.Visible : Visibility.Collapsed;
        }

        private void DictDataGrid_CellEditEnding(object sender, DataGridCellEditEndingEventArgs e)
        {
            // 延迟执行以等待绑定值更新完成
            Dispatcher.InvokeAsync(() => UpdateCustomDict());
        }


        private void BtnImportDict_Click(object sender, RoutedEventArgs e)
        {
            var ofd = new Microsoft.Win32.OpenFileDialog { Filter = "JSON 字典文件 (*.json)|*.json" };
            if (ofd.ShowDialog() == true)
            {
                try
                {
                    string json = File.ReadAllText(ofd.FileName);
                    var imported = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(json);
                    if (imported != null)
                    {
                        DictItemsList.Clear();
                        foreach (var kvp in imported) DictItemsList.Add(new DictItem { Key = kvp.Key, Value = kvp.Value });
                        UpdateCustomDict();
                        MessageBox.Show("字典导入成功！", "成功", MessageBoxButton.OK, MessageBoxImage.Information);
                    }
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"导入失败，请检查文件格式。\n错误信息: {ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
        }

        private void BtnResetDict_Click(object sender, RoutedEventArgs e)
        {
            if (MessageBox.Show("确定要清空自定义字典吗？(系统自带的默认字典仍会生效)", "警告", MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes)
            {
                DictItemsList.Clear();
                UpdateCustomDict();
            }
        }

        private bool IsAdministrator()
        {
            try
            {
                var identity = WindowsIdentity.GetCurrent();
                var principal = new WindowsPrincipal(identity);
                return principal.IsInRole(WindowsBuiltInRole.Administrator);
            }
            catch
            {
                return false;
            }
        }

        private void BtnResetRot_Click(object sender, RoutedEventArgs e) { if (SliderRotationSpeed != null) SliderRotationSpeed.Value = 0.3; }
        private void BtnStopRot_Click(object sender, RoutedEventArgs e) { if (SliderRotationSpeed != null) SliderRotationSpeed.Value = 0; }

        private void BtnImportMap_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new Microsoft.Win32.OpenFileDialog();
            dlg.Filter = "Image Files|*.jpg;*.jpeg;*.png;*.bmp";
            dlg.Title = "选择自定义地球贴图 (推荐 2:1 比例的高清平面地图)";
            if (dlg.ShowDialog() == true)
            {
                _customMapPath = dlg.FileName;
                InitEarth(); // 选择图片后强制重新加载3D材质和2D底图
            }
        }
        private void ChkHighResMap_CheckedChanged(object sender, RoutedEventArgs e)
        {
            // 切换模式时，如果有地球在显示，则立刻重新加载材质
            if (ViewEarth != null && ViewEarth.Visibility == Visibility.Visible)
            {
                InitEarth();
            }
        }
        private void TxtMaxRes_TextChanged(object sender, TextChangedEventArgs e)
        {
            // 只有当高清模式勾选，且地球视图可见时，才在修改数字后重新加载贴图
            if (ChkHighResMap != null && ChkHighResMap.IsChecked == true &&
                ViewEarth != null && ViewEarth.Visibility == Visibility.Visible)
            {
                // 简单防抖判断：如果输入的是合法数字，则重新初始化地球材质
                if (int.TryParse(TxtMaxRes.Text, out _))
                {
                    InitEarth();
                }
            }
        }
        private void SetWindowIconSafely()
        {
            try
            {
                // 尝试从资源中加载图标给窗口
                var iconUri = new Uri("pack://application:,,,/logo.ico");
                var resourceInfo = Application.GetResourceStream(iconUri);
                if (resourceInfo != null)
                {
                    this.Icon = System.Windows.Media.Imaging.BitmapFrame.Create(resourceInfo.Stream);
                }
            }
            catch
            {
                // 如果失败，窗口将显示 WPF 默认图标，程序不会崩溃
            }
        }

        // ===== 新增: 窗口活动圆环图逻辑 =====
        private bool _isWindowDonutView = false;


        private void WindowTabControl_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            // 当切换 Tab 标签时，同步刷新对应分类的饼图
            if (_isWindowDonutView) DrawWindowDonutChart();
        }

        private void BtnToggleWindowView_Click(object sender, RoutedEventArgs e)
        {
            _isWindowDonutView = !_isWindowDonutView;
            // 控制列表隐藏，但保留 Tab 头可点击
            GridPrimary.Visibility = GridSecondary.Visibility = GridBackground.Visibility = _isWindowDonutView ? Visibility.Hidden : Visibility.Visible;
            WindowDonutCanvas.Visibility = _isWindowDonutView ? Visibility.Visible : Visibility.Collapsed;
            if (_isWindowDonutView) DrawWindowDonutChart();
        }

        private void DrawWindowDonutChart()
        {
            if (WindowDonutCanvas == null || !_isWindowDonutView) return;
            if (WindowDonutCanvas.ActualWidth == 0) return;

            var targetData = new Dictionary<string, long>();

            if (BtnBackToLive.Visibility == Visibility.Visible && !string.IsNullOrEmpty(_currentHistoryDate))
            {
                int idx = WindowTabControl.SelectedIndex;
                if (idx == 0) targetData = _histFilteredPri ?? new Dictionary<string, long>();
                else if (idx == 1) targetData = _histFilteredSec ?? new Dictionary<string, long>();
                else if (idx == 2) targetData = _histFilteredBg ?? new Dictionary<string, long>();
            }
            else
            {
                int idx = WindowTabControl.SelectedIndex;
                if (idx == 0) targetData = _savedData.PrimaryWindowTimes;
                else if (idx == 1) targetData = _savedData.SecondaryWindowTimes;
                else if (idx == 2) targetData = _savedData.BackgroundProcessTimes;
            }

            var topApps = targetData.OrderByDescending(x => x.Value).Take(8).ToList();

            if (topApps.Count == 0)
            {
                WindowDonutCanvas.Children.Clear();
                WindowDonutCanvas.Uid = "";
                TextBlock tbEmpty = new TextBlock
                {
                    Text = "📭 该日无数据记录",
                    Foreground = (Brush)this.Resources["TextDimBrush"],
                    FontSize = 14,
                    FontWeight = FontWeights.Bold
                };
                tbEmpty.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                Canvas.SetLeft(tbEmpty, (WindowDonutCanvas.ActualWidth - tbEmpty.DesiredSize.Width) / 2);
                Canvas.SetTop(tbEmpty, (WindowDonutCanvas.ActualHeight - tbEmpty.DesiredSize.Height) / 2);
                WindowDonutCanvas.Children.Add(tbEmpty);
                return;
            }

            string currentHash = string.Join(",", topApps.Select(x => x.Key + x.Value));
            if (WindowDonutCanvas.Uid == currentHash) return;

            WindowDonutCanvas.Uid = currentHash;
            WindowDonutCanvas.Children.Clear();
            WindowDonutCanvas.Tag = topApps;

            long total = topApps.Sum(x => x.Value);
            double curAngle = 0;
            Point center = new Point(WindowDonutCanvas.ActualWidth / 2, WindowDonutCanvas.ActualHeight / 2);
            double outerR = 60, innerR = 35;
            Color[] colors = { Color.FromRgb(66, 133, 244), Color.FromRgb(219, 68, 55), Color.FromRgb(244, 180, 0), Color.FromRgb(15, 157, 88), Color.FromRgb(171, 71, 188), Color.FromRgb(0, 172, 193), Color.FromRgb(255, 152, 0), Color.FromRgb(103, 58, 183) };

            List<double> rightYs = new List<double>(), leftYs = new List<double>();

            for (int i = 0; i < topApps.Count; i++)
            {
                double percentage = topApps[i].Value / (double)total;
                if (percentage < 0.001) continue;
                double sweep = percentage * 360; if (sweep >= 360) sweep = 359.9;
                double startRad = curAngle * Math.PI / 180, endRad = (curAngle + sweep) * Math.PI / 180;

                var pg = new PathGeometry();
                var pf = new PathFigure { IsClosed = true, StartPoint = new Point(center.X + Math.Cos(startRad) * innerR, center.Y + Math.Sin(startRad) * innerR) };
                pf.Segments.Add(new LineSegment(new Point(center.X + Math.Cos(startRad) * outerR, center.Y + Math.Sin(startRad) * outerR), true));
                pf.Segments.Add(new ArcSegment(new Point(center.X + Math.Cos(endRad) * outerR, center.Y + Math.Sin(endRad) * outerR), new Size(outerR, outerR), sweep, sweep > 180, SweepDirection.Clockwise, true));
                pf.Segments.Add(new LineSegment(new Point(center.X + Math.Cos(endRad) * innerR, center.Y + Math.Sin(endRad) * innerR), true));
                pf.Segments.Add(new ArcSegment(new Point(center.X + Math.Cos(startRad) * innerR, center.Y + Math.Sin(startRad) * innerR), new Size(innerR, innerR), sweep, sweep > 180, SweepDirection.Counterclockwise, true));
                pg.Figures.Add(pf);

                var p = new System.Windows.Shapes.Path { Fill = new SolidColorBrush(colors[i % colors.Length]), Opacity = 0.85, Data = pg, Stroke = Brushes.Transparent, StrokeThickness = 2 };

                string displayName = topApps[i].Key;
                string displayVal = FormatActiveTime(topApps[i].Value);
                string tooltipText = ProcessDictionary.GetTooltip(displayName);
                //p.ToolTip = new ToolTip
                //{
                //    Content = $"{(tooltipText != null ? tooltipText + "\n" : "")}占比: {percentage:P1} ({displayVal})",
                //    Background = (Brush)this.Resources["BgCardBrush"],
                //    Foreground = (Brush)this.Resources["TextMainBrush"]
                //};
                //ToolTipService.SetInitialShowDelay(p, 0);

                p.MouseEnter += (s, e) => { p.Opacity = 1.0; p.Stroke = Brushes.White; Panel.SetZIndex(p, 100); };
                p.MouseLeave += (s, e) => { p.Opacity = 0.85; p.Stroke = Brushes.Transparent; Panel.SetZIndex(p, 0); };
                WindowDonutCanvas.Children.Add(p);

                double midAngle = startRad + (sweep * Math.PI / 180) / 2;
                bool isRight = Math.Cos(midAngle) > 0;
                double labelY = center.Y + Math.Sin(midAngle) * (outerR + 20);
                var yList = isRight ? rightYs : leftYs;
                int maxIter = 10; bool collision = true;
                while (collision && maxIter-- > 0) { collision = false; foreach (var y in yList) { if (Math.Abs(y - labelY) < 16) { labelY += 16; collision = true; break; } } }
                yList.Add(labelY);

                Point p1 = new Point(center.X + Math.Cos(midAngle) * (outerR - 5), center.Y + Math.Sin(midAngle) * (outerR - 5));
                Point p2 = new Point(center.X + (isRight ? 1 : -1) * (outerR + 15), labelY);
                double lineEndX = isRight ? p2.X + 25 : p2.X - 25;
                WindowDonutCanvas.Children.Add(new Polyline { Points = new PointCollection { p1, p2, new Point(lineEndX, p2.Y) }, Stroke = p.Fill, StrokeThickness = 1.5, IsHitTestVisible = false });

                var tb = new TextBlock { Text = $"{displayName} {percentage:P1}", Foreground = p.Fill, FontSize = 10, FontWeight = FontWeights.Bold, IsHitTestVisible = false };
                tb.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                Canvas.SetLeft(tb, isRight ? lineEndX + 3 : lineEndX - tb.DesiredSize.Width - 3); Canvas.SetTop(tb, p2.Y - 7);
                WindowDonutCanvas.Children.Add(tb);

                curAngle += sweep;
            }
        }
        // 窗口饼图的鼠标悬浮事件
        private void WindowDonutCanvas_MouseMove(object sender, MouseEventArgs e)
        {
            if (WindowDonutCanvas.Tag is not List<KeyValuePair<string, long>> topApps || topApps.Count == 0) return;
            // 修复：使用画布真实宽高计算中心点，解决不同分辨率下悬停错位
            double dx = e.GetPosition(WindowDonutCanvas).X - (WindowDonutCanvas.ActualWidth / 2);
            double dy = e.GetPosition(WindowDonutCanvas).Y - (WindowDonutCanvas.ActualHeight / 2);
            double dist = Math.Sqrt(dx * dx + dy * dy);

            // 修复：修正判断半径边界 (根据内径35和外径60)
            if (dist < 35 || dist > 60) { if (WindowDonutCanvas.ToolTip is System.Windows.Controls.ToolTip t) t.IsOpen = false; return; }

            double angle = Math.Atan2(dy, dx) * 180 / Math.PI;
            if (angle < 0) angle += 360;

            long total = topApps.Sum(x => x.Value);
            double currentAngle = 0;
            for (int i = 0; i < topApps.Count; i++)
            {
                double sweep = (topApps[i].Value / (double)total) * 360;
                if (angle >= currentAngle && angle <= currentAngle + sweep)
                {
                    if (WindowDonutCanvas.ToolTip is not ToolTip tt)
                    {
                        tt = new ToolTip
                        {
                            Background = (Brush)this.Resources["BgCardBrush"],
                            Foreground = (Brush)this.Resources["TextMainBrush"],
                            BorderBrush = (Brush)this.Resources["BorderMainBrush"],
                            BorderThickness = new Thickness(1),
                            Placement = System.Windows.Controls.Primitives.PlacementMode.Mouse, // ★ 解决悬停跑到右下角的问题
                            VerticalOffset = 15,
                            FontWeight = FontWeights.Bold
                        };
                        WindowDonutCanvas.ToolTip = tt;
                    }
                    tt.Content = $"{topApps[i].Key}\n{(topApps[i].Value / (double)total):P1} ({FormatActiveTime(topApps[i].Value)})";
                    tt.IsOpen = true;
                    return;
                }
                currentAngle += sweep;
            }
        }
        private void WindowDonutCanvas_MouseLeave(object sender, MouseEventArgs e) { if (WindowDonutCanvas.ToolTip is ToolTip tt) tt.IsOpen = false; }

        // 在 MainWindow 类内部添加
        private void SetQuickToolTip(FrameworkElement element, object content)
        {
            var tt = new System.Windows.Controls.ToolTip
            {
                Content = content,
                FontWeight = FontWeights.Bold,
                BorderThickness = new Thickness(1)
            };
            // 修复: 强制绑定动态画刷资源，使其完全跟随主题颜色
            tt.SetResourceReference(System.Windows.Controls.ToolTip.BackgroundProperty, "BgCardBrush");
            tt.SetResourceReference(System.Windows.Controls.ToolTip.ForegroundProperty, "TextMainBrush");
            tt.SetResourceReference(System.Windows.Controls.ToolTip.BorderBrushProperty, "BorderMainBrush");

            element.ToolTip = tt;
            ToolTipService.SetInitialShowDelay(element, 0);
            ToolTipService.SetBetweenShowDelay(element, 0);
            ToolTipService.SetPlacement(element, System.Windows.Controls.Primitives.PlacementMode.Mouse);
            ToolTipService.SetVerticalOffset(element, 15);
            ToolTipService.SetHorizontalOffset(element, 15);
        }

        // ================= 新增：图表拖拽选择时间范围逻辑 =================
        private double _dragStartX = -1;
        private bool _isDraggingChart = false;
        private double _selectedTimeStartRatio = 0;
        private double _selectedTimeEndRatio = 1;

        private void MainCanvas_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            _isDraggingChart = true;
            _dragStartX = e.GetPosition(MainCanvas).X;
            ChartSelectionRect.Visibility = Visibility.Visible;
            Canvas.SetLeft(ChartSelectionRect, _dragStartX);
            ChartSelectionRect.Width = 0;
            MainCanvas.CaptureMouse();
        }

        private void MainCanvas_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (_isDraggingChart)
            {
                _isDraggingChart = false;
                MainCanvas.ReleaseMouseCapture();
                double endX = e.GetPosition(MainCanvas).X;
                double w = MainCanvas.ActualWidth;

                double leftX = Math.Min(_dragStartX, endX);
                double rightX = Math.Max(_dragStartX, endX);

                if (rightX - leftX > 5) // 防误触，拉出有效范围则截取时间
                {
                    _selectedTimeStartRatio = 1.0 - (rightX / w);
                    _selectedTimeEndRatio = 1.0 - (leftX / w);
                    _isChartDragPaused = true; // 独立控制拖拽冻结
                    AddLogEvent("Chart", "已选择流量分析时段", $"成功圈定过去 {(_selectedTimeEndRatio * _currentViewMode):F0}秒 ~ {(_selectedTimeStartRatio * _currentViewMode):F0}秒 的区间，请展开特定进程加载详情。", "#00E5FF");
                }
                else
                {
                    ChartSelectionRect.Visibility = Visibility.Hidden;
                    _selectedTimeStartRatio = 0; _selectedTimeEndRatio = 1; // 恢复全量
                    _isChartDragPaused = false; // 解除拖拽冻结
                }
            }
        }
        private const string AppName = "DashBoard";
        private const string AppVersion = "v2.7 (Time Tracking Fixed)";
        private const string SaveFilePath = "dashboard_data.json";

        private DashboardSaveData _savedData = new DashboardSaveData();
        private Forms.NotifyIcon? _trayIcon;
        private bool _isRealExit = false;
        private bool _isNetworkAvailable = true; // 默认在线
        private ObservableCollection<ProcessNetworkInfo> _processTrafficList = new ObservableCollection<ProcessNetworkInfo>();

        private ObservableCollection<WindowActivityInfo> _primaryWindowList = new ObservableCollection<WindowActivityInfo>();
        private ObservableCollection<WindowActivityInfo> _secondaryWindowList = new ObservableCollection<WindowActivityInfo>();
        private ObservableCollection<WindowActivityInfo> _backgroundWindowList = new ObservableCollection<WindowActivityInfo>();
        private ObservableCollection<AppDistItem> _appDistList = new ObservableCollection<AppDistItem>();
        //  用于独立追踪每个网卡的增量，解决网卡启停导致的流量暴增或叠加计算BUG
        private Dictionary<string, (long Recv, long Sent)> _lastInterfaceStats = new Dictionary<string, (long, long)>();


        private double _targetDownSpeed, _targetUpSpeed;
        private double _currentCpu, _currentRam, _currentGpu, _currentDisk;
        private double _displayMaxScale = 1024 * 100;
        private bool _isBitMode = false;
        private bool _showAppByTime = false;

        // 数据队列缓存
        private Queue<double> _secDown = new Queue<double>(Enumerable.Repeat(0.0, 305));
        private Queue<double> _secUp = new Queue<double>(Enumerable.Repeat(0.0, 305));
        private Queue<double> _minDown = new Queue<double>(Enumerable.Repeat(0.0, 185));
        private Queue<double> _minUp = new Queue<double>(Enumerable.Repeat(0.0, 185));
        private Queue<double> _hourDown = new Queue<double>(Enumerable.Repeat(0.0, 175));
        private Queue<double> _hourUp = new Queue<double>(Enumerable.Repeat(0.0, 175));
        private Queue<double> _dayDown = new Queue<double>(Enumerable.Repeat(0.0, 35));
        private Queue<double> _dayUp = new Queue<double>(Enumerable.Repeat(0.0, 35));

        private Queue<double> _secLanDown = new Queue<double>(Enumerable.Repeat(0.0, 305));
        private Queue<double> _secLanUp = new Queue<double>(Enumerable.Repeat(0.0, 305));
        private Queue<double> _minLanDown = new Queue<double>(Enumerable.Repeat(0.0, 185));
        private Queue<double> _minLanUp = new Queue<double>(Enumerable.Repeat(0.0, 185));
        private Queue<double> _hourLanDown = new Queue<double>(Enumerable.Repeat(0.0, 175));
        private Queue<double> _hourLanUp = new Queue<double>(Enumerable.Repeat(0.0, 175));
        private Queue<double> _dayLanDown = new Queue<double>(Enumerable.Repeat(0.0, 35));
        private Queue<double> _dayLanUp = new Queue<double>(Enumerable.Repeat(0.0, 35));
        private ulong _lastLanDown = 0, _lastLanUp = 0;
        private double _targetLanDownSpeed = 0, _targetLanUpSpeed = 0;
        private bool _isLanInit = false;


        private Queue<double> _cpuSec = new Queue<double>(Enumerable.Repeat(0.0, 305));
        private Queue<double> _cpuMin = new Queue<double>(Enumerable.Repeat(0.0, 185));
        private Queue<double> _cpuHour = new Queue<double>(Enumerable.Repeat(0.0, 175));
        private Queue<double> _cpuDay = new Queue<double>(Enumerable.Repeat(0.0, 35));
        private Queue<double> _ramSec = new Queue<double>(Enumerable.Repeat(0.0, 305)), _ramMin = new Queue<double>(Enumerable.Repeat(0.0, 185)), _ramHour = new Queue<double>(Enumerable.Repeat(0.0, 175)), _ramDay = new Queue<double>(Enumerable.Repeat(0.0, 35));
        private Queue<double> _gpuSec = new Queue<double>(Enumerable.Repeat(0.0, 305)), _gpuMin = new Queue<double>(Enumerable.Repeat(0.0, 185)), _gpuHour = new Queue<double>(Enumerable.Repeat(0.0, 175)), _gpuDay = new Queue<double>(Enumerable.Repeat(0.0, 35));
        private Queue<double> _diskSec = new Queue<double>(Enumerable.Repeat(0.0, 305)), _diskMin = new Queue<double>(Enumerable.Repeat(0.0, 185)), _diskHour = new Queue<double>(Enumerable.Repeat(0.0, 175)), _diskDay = new Queue<double>(Enumerable.Repeat(0.0, 35));

        private ulong _lastSysIdle, _lastSysTotal;
        private int _tickSec = 0, _tickMin = 0, _tickHour = 0;
        private int _currentViewMode = 60;
        private DateTime _lastSecTick = DateTime.Now, _lastMinTick = DateTime.Now, _lastHourTick = DateTime.Now, _lastDayTick = DateTime.Now;

        private List<TextBlock> _timeLabels = new List<TextBlock>();
        private List<Line> _gridLines = new List<Line>();
        private List<UIElement> _resAxisElements = new List<UIElement>();


        // 原有变量替换为：
        private ConcurrentDictionary<int, (ulong Read, ulong Write)> _pidInitialIo = new ConcurrentDictionary<int, (ulong Read, ulong Write)>();
        private ConcurrentDictionary<int, (ulong Read, ulong Write)> _pidCurrentDelta = new ConcurrentDictionary<int, (ulong Read, ulong Write)>(); 
        private ConcurrentDictionary<int, string> _pidNameCache = new ConcurrentDictionary<int, string>();
        private ConcurrentDictionary<int, long> _pidLastRecordedDaily = new ConcurrentDictionary<int, long>();
        // 用于实时计算图表的缓存快照
        private Dictionary<string, long> _liveSessionTraffic = new Dictionary<string, long>();
        private Dictionary<string, long> _liveSessionTime = new Dictionary<string, long>();

        private bool _isProcessingProcesses = false;
        private readonly object _saveDataLock = new object();

        private int _selectedHeatmapYear = DateTime.Today.Year;
        private string _activeTab = "Traffic";
        private SolidColorBrush _activeBrush = new SolidColorBrush(Color.FromRgb(0, 229, 255));
        private SolidColorBrush _inactiveBrush = new SolidColorBrush(Colors.Transparent);
        private ObservableCollection<AppLogEvent> _uiLogs = new ObservableCollection<AppLogEvent>();
        private ObservableCollection<FileIoEvent> _uiFileLogs = new ObservableCollection<FileIoEvent>();
        private Dictionary<AppLogEvent, UIElement> _chartEventMarkers = new Dictionary<AppLogEvent, UIElement>();
        // ★ 应用时光机运行时变量
        private Dictionary<int, AppSessionInfo> _activeAppSessions = new Dictionary<int, AppSessionInfo>();
        private ObservableCollection<AppSessionInfo> _uiAppLogs = new ObservableCollection<AppSessionInfo>();
        private ObservableCollection<AppSessionInfo> _uiSnapshotApps = new ObservableCollection<AppSessionInfo>();

        // ★ 核心方法：追踪有界面的软件的启动和关闭
        private void TrackAppLifecycles()
        {
            var currentPids = new HashSet<int>();
            var processes = Process.GetProcesses();

            foreach (var p in processes)
            {
                try
                {
                    // 只追踪有主窗口(GUI)的用户软件，忽略后台纯服务进程
                    if (p.MainWindowHandle == IntPtr.Zero) continue;

                    string pName = p.ProcessName;
                    if (pName == "Idle" || pName == "System" || pName == Process.GetCurrentProcess().ProcessName) continue;

                    currentPids.Add(p.Id);

                    // 发现新启动的软件
                    if (!_activeAppSessions.ContainsKey(p.Id))
                    {
                        string path = "";
                        try { path = p.MainModule?.FileName ?? ""; } catch { } // 获取绝对路径(需权限)

                        if (!string.IsNullOrEmpty(path))
                        {
                            // 应用版本变更检测
                            try
                            {
                                var verInfo = FileVersionInfo.GetVersionInfo(path);
                                string currentVer = verInfo.FileVersion ?? verInfo.ProductVersion ?? "";
                                if (!string.IsNullOrEmpty(currentVer))
                                {
                                    lock (_saveDataLock)
                                    {
                                        if (_savedData.AppVersions.TryGetValue(path, out string oldVer))
                                        {
                                            if (oldVer != currentVer)
                                            {
                                                _savedData.AppVersions[path] = currentVer; // 更新为新版本
                                                string appDisplayName = ProcessDictionary.GetWithDesc(pName);
                                                Dispatcher.InvokeAsync(() => {
                                                    AddLogEvent("AppUpdate", "应用版本已变更", $"{appDisplayName} 的版本已从 {oldVer} 变更为 {currentVer}。", "#FF9800"); // 橙色警告
                                                });
                                            }
                                        }
                                        else
                                        {
                                            _savedData.AppVersions[path] = currentVer; // 第一次记录该软件版本
                                        }
                                    }
                                }
                            }
                            catch { /* 忽略无法获取版本的程序 */ }
                            // =============================
                            var session = new AppSessionInfo
                            {
                                ProcessName = pName,
                                ExePath = path,
                                StartTime = DateTime.Now,
                                Icon = GetIcon(path) // ★ 提取图标
                            };
                            _activeAppSessions[p.Id] = session;

                            lock (_saveDataLock)
                            {
                                _savedData.AppSessions.Insert(0, session);
                                // 最多保存 2000 条记录防止文件过大
                                if (_savedData.AppSessions.Count > 2000) _savedData.AppSessions.RemoveAt(_savedData.AppSessions.Count - 1);
                            }
                            Dispatcher.InvokeAsync(() => {
                                _uiAppLogs.Insert(0, session);
                            });
                        }
                    }
                }
                catch { }
                finally { p.Dispose(); }
            }

            // 结算已经关闭的软件
            var deadPids = _activeAppSessions.Keys.Except(currentPids).ToList();
            foreach (var dead in deadPids)
            {
                var session = _activeAppSessions[dead];
                session.EndTime = DateTime.Now; // 记录关闭时间
                _activeAppSessions.Remove(dead);
            }
        }


        // 辅助方法：添加新日志
        public void AddLogEvent(string type, string title, string msg, string color)
        {
            var log = new AppLogEvent { Timestamp = DateTime.Now, Type = type, Title = title, Message = msg, ColorHex = color };
            lock (_saveDataLock)
            {
                _savedData.AppLogs.Insert(0, log);
                // 限制最多保存 1000 条记录防止文件过大
                if (_savedData.AppLogs.Count > 1000) _savedData.AppLogs.RemoveAt(_savedData.AppLogs.Count - 1);
            }
            Dispatcher.InvokeAsync(() => _uiLogs.Insert(0, log));
        }

        public MainWindow()
        {
            // 强制设置：只有显式调用 Shutdown() 时才退出程序
            // 这解决了隐藏窗口后 WPF 自动结束进程的问题
            if (Application.Current != null)
            {
                Application.Current.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            }

            InitializeComponent();
            // ★ BugFix 2: 提升隐式样式作用域，解决 ToolTip 等弹窗控件脱离窗口视觉树导致不随主题变色的问题
            if (Application.Current != null)
            {
                Application.Current.Resources[typeof(ToolTip)] = this.Resources[typeof(ToolTip)];
                Application.Current.Resources[typeof(ComboBoxItem)] = this.Resources[typeof(ComboBoxItem)];
                Application.Current.Resources[typeof(System.Windows.Controls.Primitives.DataGridColumnHeader)] = this.Resources[typeof(System.Windows.Controls.Primitives.DataGridColumnHeader)];
            }
            // 安全设置窗口图标 ---
            SetWindowIconSafely();
            // 实例化磁盘性能计数器
            _diskCounter = new PerformanceCounter("PhysicalDisk", "% Disk Time", "_Total");
            // 拦截普通权限启动，强制要求提权以激活 ETW 内核抓包
            // --- 修改后的权限检查：异步触发，不阻塞主程序运行 ---
            if (!IsAdministrator())
            {
                // 使用 Dispatcher 异步弹出，确保主窗体和托盘图标先加载完成
                Dispatcher.BeginInvoke(new Action(() => {
                    var result = MessageBox.Show(
                        "检测到程序未以管理员身份运行！\n\n" +
                        "【当前状态】程序将继续运行，但「进程详情」中的流量会显示为 0。\n" +
                        "【原因】进程级网络抓包 (ETW) 必须拥有管理员权限。\n\n" +
                        "是否立即自动以管理员权限重启 DashBoard？",
                        "受限模式运行中",
                        MessageBoxButton.YesNo,
                        MessageBoxImage.Warning);

                    if (result == MessageBoxResult.Yes)
                    {
                        try
                        {
                            var exeName = Environment.ProcessPath;
                            if (!string.IsNullOrEmpty(exeName))
                            {
                                ProcessStartInfo startInfo = new ProcessStartInfo(exeName)
                                {
                                    UseShellExecute = true,
                                    Verb = "runas"
                                };
                                Process.Start(startInfo);
                                Application.Current.Shutdown(); // 只有确认重启时才关闭当前实例
                            }
                        }
                        catch { /* 用户取消了 UAC 授权 */ }
                    }
                    else
                    {
                        // 用户选择“否”：继续运行，并在日志中记录状态
                        AddLogEvent("System", "受限模式启动", "程序未获得管理员权限，进程级流量统计已禁用，仅开启基础监控。", "#FF9800");
                    }
                }), System.Windows.Threading.DispatcherPriority.ContextIdle);
            }
            // ------------------------------------------------
            // 开机自启的静默启动参数
            string[] args = Environment.GetCommandLineArgs();
            if (args.Contains("--minimized"))
            {
                this.Loaded += (s, e) => this.Hide();
            }

            ProcessGrid.ItemsSource = _processTrafficList;

            GridPrimary.ItemsSource = _primaryWindowList;
            GridSecondary.ItemsSource = _secondaryWindowList;
            GridBackground.ItemsSource = _backgroundWindowList;
            if (this.FindName("GridAppDistribution") is DataGrid gad) gad.ItemsSource = _appDistList;
            LoadData();

            // 初始化日志 UI 列表
            _uiLogs = new ObservableCollection<AppLogEvent>(_savedData.AppLogs);

            if (this.FindName("LogListBox") is ListBox logBox) logBox.ItemsSource = _uiLogs;
            _uiAppLogs = new ObservableCollection<AppSessionInfo>(_savedData.AppSessions);
            if (this.FindName("GridAppLogs") is DataGrid logsGrid) logsGrid.ItemsSource = _uiAppLogs;
            if (this.FindName("DatePickerSnapshot") is DatePicker dpInit) dpInit.SelectedDate = DateTime.Now.Date;
            if (this.FindName("TxtSnapshotTime") is TextBox txtTimeInit) txtTimeInit.Text = DateTime.Now.ToString("HH:mm:ss");
            // 异步加载历史图标，防止启动卡顿
            Task.Run(() => {
                foreach (var session in _savedData.AppSessions)
                {
                    if (session.Icon == null && !string.IsNullOrEmpty(session.ExePath))
                    {
                        var icon = GetIcon(session.ExePath);

                    }
                }
            });
            InitSystemTray();
            UpdateNavStyle(NavTraffic);

            // 使用新的隔离算法初始化基准线，防止启动第一秒出现负数网速
            GetNetworkTrafficDeltas(out _, out _, out _, out _);
            this.Closing += MainWindow_Closing;

            CompositionTarget.Rendering += OnRender;

            // 初始化定时器并启动时间数据采集
            DispatcherTimer processTimer = new DispatcherTimer();
            processTimer.Interval = TimeSpan.FromSeconds(2);
            processTimer.Tick += ProcessTimer_Tick;
            processTimer.Start();
            // 监听网络状态改变
            NetworkChange.NetworkAvailabilityChanged += (s, e) => {
                _isNetworkAvailable = e.IsAvailable;
                string status = e.IsAvailable ? "系统网络连接已恢复 (Internet access now available)" : "系统网络连接已断开 (Internet connection lost)";
                string color = e.IsAvailable ? "#32CD32" : "#FF3D71";
                AddLogEvent("Network", "网络状态变更", status, color);
            };
            EtwNetworkTracker.StartTracking();

            _isNetworkAvailable = NetworkInterface.GetIsNetworkAvailable();

            // ★ 新增：初始化网络扫描器的自动后台定时器
            DispatcherTimer scannerTimer = new DispatcherTimer();
            scannerTimer.Tick += (s, e) => {
                if (_savedData.ScannerAutoScanMinutes > 0 && !_isScanning)
                    BtnScan_Click(null, null);
            };
            scannerTimer.Interval = TimeSpan.FromMinutes(Math.Max(1, _savedData.ScannerAutoScanMinutes));
            scannerTimer.Start();
            this.Tag = scannerTimer; // 将计时器挂载到窗口属性上供配置面板随时修改
            // 绑定实时预览事件
            SliderFontSize.ValueChanged += (s, e) => PreviewTheme();
            EditColorDown.TextChanged += (s, e) => PreviewTheme();
            EditColorUp.TextChanged += (s, e) => PreviewTheme();
            EditColorCpu.TextChanged += (s, e) => PreviewTheme();
            EditColorRam.TextChanged += (s, e) => PreviewTheme();

            DonutCanvas.SizeChanged += (s, e) => { if (_activeTab == "Resources") DrawDonutChart(); };
            // ★ 强迫窗口环形图在拉伸时重新计算自身半径和圆心矩阵
            if (WindowDonutCanvas != null) WindowDonutCanvas.SizeChanged += (s, e) => { if (_isWindowDonutView) DrawWindowDonutChart(); };
            if (EditColorBgMain != null) EditColorBgMain.TextChanged += (s, e) => PreviewTheme();

            if (EditColorBgCard != null) EditColorBgCard.TextChanged += (s, e) => PreviewTheme();
            _mapLodTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
            _mapLodTimer.Tick += MapLodTimer_Tick;


        }
        private void PreviewTheme()
        {
            // 检查 UI 元素是否已初始化
            if (EditColorDown == null || TxtDown == null || PreviewBoxDown == null) return;

            try
            {

                // ★ 新增：即点即用，直接写入实时配置对象
                _savedData.ColorDown = EditColorDown.Text;
                _savedData.ColorUp = EditColorUp.Text;
                _savedData.ColorCpu = EditColorCpu.Text;
                _savedData.ColorRam = EditColorRam.Text;
                if (EditColorBgMain != null) _savedData.ColorBgMain = EditColorBgMain.Text;
                if (EditColorBgCard != null) _savedData.ColorBgCard = EditColorBgCard.Text;
                _savedData.GlobalFontSize = SliderFontSize.Value;

                var brushDown = new SolidColorBrush((Color)ColorConverter.ConvertFromString(EditColorDown.Text));
                var brushUp = new SolidColorBrush((Color)ColorConverter.ConvertFromString(EditColorUp.Text));
                var brushCpu = new SolidColorBrush((Color)ColorConverter.ConvertFromString(EditColorCpu.Text));
                var brushRam = new SolidColorBrush((Color)ColorConverter.ConvertFromString(EditColorRam.Text));
                // 新增背景预览色块
                if (EditColorBgMain != null && PreviewBoxBgMain != null)
                {
                    var brushBgMain = new SolidColorBrush((Color)ColorConverter.ConvertFromString(EditColorBgMain.Text));
                    PreviewBoxBgMain.Background = EditColorBgMain.Foreground = brushBgMain;
                }
                if (EditColorBgCard != null && PreviewBoxBgCard != null)
                {
                    var brushBgCard = new SolidColorBrush((Color)ColorConverter.ConvertFromString(EditColorBgCard.Text));
                    PreviewBoxBgCard.Background = EditColorBgCard.Foreground = brushBgCard;
                }
                // 1. 更新设置面板输入框预览
                PreviewBoxDown.Background = EditColorDown.Foreground = brushDown;
                PreviewBoxUp.Background = EditColorUp.Foreground = brushUp;
                PreviewBoxCpu.Background = EditColorCpu.Foreground = brushCpu;
                PreviewBoxRam.Background = EditColorRam.Foreground = brushRam;

                // 2. 更新专门的预览区域 (ThemePreviewArea)
                PreviewLabelDown.Foreground = brushDown;
                PreviewLabelUp.Foreground = brushUp;
                PreviewLabelCpu.Foreground = brushCpu;
                PreviewLabelRam.Foreground = brushRam;
                // 应用字号预览到预览区域的所有文本
                double previewSize = SliderFontSize.Value;
                PreviewLabelDown.FontSize = PreviewLabelUp.FontSize = previewSize;
                PreviewLabelCpu.FontSize = PreviewLabelRam.FontSize = previewSize;
                PreviewTextSample.FontSize = previewSize * 0.8; // 辅助文本稍小一点

                // 3. 实时应用到全局 UI
                TxtDown.Foreground = brushDown;
                TxtUp.Foreground = brushUp;
                PolyDown.Stroke = brushDown;
                PolyDown.Fill = new SolidColorBrush(Color.FromArgb(21, brushDown.Color.R, brushDown.Color.G, brushDown.Color.B));
                PolyUp.Stroke = brushUp;
                PolyUp.Fill = new SolidColorBrush(Color.FromArgb(21, brushUp.Color.R, brushUp.Color.G, brushUp.Color.B));

                LineCpu.Stroke = brushCpu;
                LineRam.Stroke = brushRam;
                this.FontSize = previewSize;
                // ★ 触发全局样式更新，抛弃手动保存按钮
                ApplyTheme();
            }
            catch { /* 忽略非法 HEX */ }
        }
        // ★ 新增：悬停解释模式即点即用
        private void ComboTooltipMode_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!IsLoaded) return;
            if (sender is ComboBox cb)
            {
                _savedData.TooltipMode = cb.SelectedIndex;
                MainWindow.TooltipMode = cb.SelectedIndex;
            }
        }
        private void ThemePreset_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Tag is string colors)
            {
                var parts = colors.Split(',');

                // 1. 如果只传了 2 个颜色：说明是“深浅背景主题”，仅调整背景，不影响当前线条
                if (parts.Length == 2)
                {
                    if (EditColorBgMain != null) EditColorBgMain.Text = parts[0];
                    if (EditColorBgCard != null) EditColorBgCard.Text = parts[1];
                }
                // 2. 如果传了 4 个颜色：说明是带“&”的色彩主题，仅调整线条，不影响当前背景
                else if (parts.Length == 4)
                {
                    EditColorDown.Text = parts[0];
                    EditColorUp.Text = parts[1];
                    EditColorCpu.Text = parts[2];
                    EditColorRam.Text = parts[3];
                }
                // 3. 如果传了 6 个颜色：全部覆盖（如 Classic 经典恢复）
                else if (parts.Length >= 6)
                {
                    EditColorDown.Text = parts[0];
                    EditColorUp.Text = parts[1];
                    EditColorCpu.Text = parts[2];
                    EditColorRam.Text = parts[3];
                    if (EditColorBgMain != null) EditColorBgMain.Text = parts[4];
                    if (EditColorBgCard != null) EditColorBgCard.Text = parts[5];
                }
            }
        }
        // ==========================================
        // 1. 系统、存储与生命周期
        // ==========================================
        private void InitSystemTray()
        {
            _trayIcon = new Forms.NotifyIcon();
            bool iconLoaded = false;

            try
            {
                // 1. 尝试从嵌入资源读取
                var resourceUri = new Uri("pack://application:,,,/logo.ico");
                var resourceInfo = Application.GetResourceStream(resourceUri);
                if (resourceInfo != null)
                {
                    using (var stream = resourceInfo.Stream)
                    {
                        _trayIcon.Icon = new System.Drawing.Icon(stream);
                        iconLoaded = true;
                    }
                }
            }
            catch
            {
                /* 资源加载失败 */
            }
            // 2. 如果资源加载失败，尝试从可执行文件提取图标作为备份
            if (!iconLoaded)
            {
                try
                {
                    string? loc = Environment.ProcessPath;
                    if (!string.IsNullOrEmpty(loc))
                    {
                        _trayIcon.Icon = System.Drawing.Icon.ExtractAssociatedIcon(loc);
                        iconLoaded = true;
                    }
                }
                catch { }
            }

            // 3. 如果还是失败，使用 Windows 系统默认图标（兜底方案）
            if (!iconLoaded)
            {
                _trayIcon.Icon = System.Drawing.SystemIcons.Application;
            }

            _trayIcon.Text = AppName;
            _trayIcon.Visible = true;
            _trayIcon.DoubleClick += (s, e) => ShowWindow();


            if (_trayIcon.Icon == null) _trayIcon.Icon = System.Drawing.SystemIcons.Application;

            _trayIcon.Text = AppName;
            _trayIcon.Visible = true;
            _trayIcon.DoubleClick += (s, e) => ShowWindow();

            // 右键菜单与开机自启选项
            var menu = new Forms.ContextMenuStrip();
            menu.Items.Add("显示主面板", null, (s, e) => ShowWindow());

            var autoStartItem = new Forms.ToolStripMenuItem("开机自启 (注册表)");
            autoStartItem.CheckOnClick = true;
            autoStartItem.Checked = CheckAutoStart();
            autoStartItem.CheckedChanged += (s, e) => ToggleAutoStart(autoStartItem.Checked);
            menu.Items.Add(autoStartItem);

            menu.Items.Add("完全退出", null, (s, e) => {
                _isRealExit = true;
                SaveData();
                _trayIcon.Visible = false;
                _trayIcon.Dispose();
                Application.Current.Shutdown();
            });
            menu.Items.Add("显示桌面悬浮窗", null, (s, e) => {
                if (_miniWindow == null) _miniWindow = new MiniWindow(this);
                _miniWindow.Show();
            });
            _trayIcon.ContextMenuStrip = menu;




        }
        // ★ 开机自启注册表控制逻辑
        private void ToggleAutoStart(bool enable)
        {
            try
            {
                string path = Environment.ProcessPath;
                using RegistryKey key = Registry.CurrentUser.OpenSubKey("SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\Run", true);
                if (enable) key?.SetValue(AppName, $"\"{path}\" --minimized");
                else key?.DeleteValue(AppName, false);
            }
            catch (Exception ex) { MessageBox.Show("设置开机自启失败，请尝试以管理员身份运行。\n错误: " + ex.Message); }
        }

        private bool CheckAutoStart()
        {
            try
            {
                using RegistryKey key = Registry.CurrentUser.OpenSubKey("SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\Run", false);
                return key?.GetValue(AppName) != null;
            }
            catch { return false; }
        }
        private void ShowWindow() { this.Show(); this.WindowState = WindowState.Normal; this.Activate(); }

        private void MainWindow_Closing(object? sender, CancelEventArgs e)
        {
            if (!_isRealExit)
            {
                e.Cancel = true; // 拦截关闭信号

                if (!_savedData.HasAskedMinimize)
                {
                    var result = MessageBox.Show("是否让 DashBoard 在后台继续运行以监控流量？\n\n选择【是】：最小化到系统托盘\n选择【否】：完全退出程序", "后台运行提示", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);

                    if (result == MessageBoxResult.Yes)
                    {
                        _savedData.HasAskedMinimize = true;
                        _savedData.MinimizeToTray = true;
                        SaveData();
                        this.Hide();
                        _trayIcon?.ShowBalloonTip(2000, "后台运行中", "DashBoard 将在后台持续监控网络流量。", Forms.ToolTipIcon.Info);
                    }
                    else if (result == MessageBoxResult.No)
                    {
                        _savedData.HasAskedMinimize = true;
                        _savedData.MinimizeToTray = false;
                        SaveData();
                        _isRealExit = true;
                        _trayIcon?.Dispose();
                        Application.Current.Shutdown();
                    }
                }
                else
                {
                    if (_savedData.MinimizeToTray)
                    {
                        this.Hide();
                        _trayIcon?.ShowBalloonTip(2000, "后台运行中", "程序已最小化到系统托盘。", Forms.ToolTipIcon.Info);
                    }
                    else
                    {
                        _isRealExit = true;
                        SaveData();
                        _trayIcon?.Dispose();
                        Application.Current.Shutdown();
                        EtwNetworkTracker.StopTracking();
                    }
                }
            }
        }
        private void SaveData()
        {
            lock (_saveDataLock)
            {
                _savedData.LastSavedTime = DateTime.Now; // ★ 记录关机时间
                _savedData.TickSec = _tickSec;
                _savedData.TickMin = _tickMin;
                _savedData.TickHour = _tickHour;

                _savedData.SavedMinDown = _minDown.ToList();
                _savedData.SavedMinUp = _minUp.ToList();
                _savedData.SavedSecDown = _secDown.ToList();
                _savedData.SavedSecUp = _secUp.ToList();
                _savedData.SavedCpuSec = _cpuSec.ToList();
                _savedData.SavedRamSec = _ramSec.ToList();
                _savedData.SavedHourDown = _hourDown.ToList();
                _savedData.SavedHourUp = _hourUp.ToList();
                _savedData.SavedDayDown = _dayDown.ToList();
                _savedData.SavedDayUp = _dayUp.ToList();
                _savedData.SavedCpuMin = _cpuMin.ToList();
                _savedData.SavedRamMin = _ramMin.ToList();
                _savedData.SavedCpuHour = _cpuHour.ToList();
                _savedData.SavedRamHour = _ramHour.ToList();
                _savedData.SavedCpuDay = _cpuDay.ToList();
                _savedData.SavedRamDay = _ramDay.ToList();

                try { File.WriteAllText(SaveFilePath, JsonSerializer.Serialize(_savedData, new JsonSerializerOptions { WriteIndented = true })); } catch { }
            }
        }

        private void LoadData()
        {
            try
            {
                if (File.Exists(SaveFilePath))
                {
                    _savedData = JsonSerializer.Deserialize<DashboardSaveData>(File.ReadAllText(SaveFilePath)) ?? new DashboardSaveData();
                    _savedData.ProcessDownloadTraffic ??= new Dictionary<string, ulong>();
                    _savedData.ProcessUploadTraffic ??= new Dictionary<string, ulong>();
                    _savedData.DailyAppTraffic ??= new Dictionary<string, Dictionary<string, long>>();
                    _savedData.DailyPrimaryTime ??= new Dictionary<string, Dictionary<string, long>>();
                    _savedData.DailySecondaryTime ??= new Dictionary<string, Dictionary<string, long>>();
                    _savedData.DailyBackgroundTime ??= new Dictionary<string, Dictionary<string, long>>();
                    _savedData.AppVersions ??= new Dictionary<string, string>();

                    _minDown = new Queue<double>(_savedData.SavedMinDown?.Count > 0 ? _savedData.SavedMinDown : Enumerable.Repeat(0.0, 185));
                    _minUp = new Queue<double>(_savedData.SavedMinUp?.Count > 0 ? _savedData.SavedMinUp : Enumerable.Repeat(0.0, 185));
                    _secDown = new Queue<double>(_savedData.SavedSecDown?.Count > 0 ? _savedData.SavedSecDown : Enumerable.Repeat(0.0, 305));
                    _secUp = new Queue<double>(_savedData.SavedSecUp?.Count > 0 ? _savedData.SavedSecUp : Enumerable.Repeat(0.0, 305));
                    _cpuSec = new Queue<double>(_savedData.SavedCpuSec?.Count > 0 ? _savedData.SavedCpuSec : Enumerable.Repeat(0.0, 305));
                    _ramSec = new Queue<double>(_savedData.SavedRamSec?.Count > 0 ? _savedData.SavedRamSec : Enumerable.Repeat(0.0, 305));

                    // ▼ 添加这几行 ▼
                    _hourDown = new Queue<double>(_savedData.SavedHourDown?.Count > 0 ? _savedData.SavedHourDown : Enumerable.Repeat(0.0, 175));
                    _hourUp = new Queue<double>(_savedData.SavedHourUp?.Count > 0 ? _savedData.SavedHourUp : Enumerable.Repeat(0.0, 175));
                    _dayDown = new Queue<double>(_savedData.SavedDayDown?.Count > 0 ? _savedData.SavedDayDown : Enumerable.Repeat(0.0, 35));
                    _dayUp = new Queue<double>(_savedData.SavedDayUp?.Count > 0 ? _savedData.SavedDayUp : Enumerable.Repeat(0.0, 35));
                    _cpuMin = new Queue<double>(_savedData.SavedCpuMin?.Count > 0 ? _savedData.SavedCpuMin : Enumerable.Repeat(0.0, 185));
                    _ramMin = new Queue<double>(_savedData.SavedRamMin?.Count > 0 ? _savedData.SavedRamMin : Enumerable.Repeat(0.0, 185));
                    _cpuHour = new Queue<double>(_savedData.SavedCpuHour?.Count > 0 ? _savedData.SavedCpuHour : Enumerable.Repeat(0.0, 175));
                    _ramHour = new Queue<double>(_savedData.SavedRamHour?.Count > 0 ? _savedData.SavedRamHour : Enumerable.Repeat(0.0, 175));
                    _cpuDay = new Queue<double>(_savedData.SavedCpuDay?.Count > 0 ? _savedData.SavedCpuDay : Enumerable.Repeat(0.0, 35));
                    _ramDay = new Queue<double>(_savedData.SavedRamDay?.Count > 0 ? _savedData.SavedRamDay : Enumerable.Repeat(0.0, 35));
                    _tickSec = _savedData.TickSec;
                    _tickMin = _savedData.TickMin;
                    _tickHour = _savedData.TickHour;

                    // ★ 核心修复：离线时间补偿模拟，解决重启图表缺失断层
                    DateTime last = _savedData.LastSavedTime;
                    if (last != default && last < DateTime.Now && (DateTime.Now - last).TotalDays < 1)
                    {
                        int missedSeconds = (int)(DateTime.Now - last).TotalSeconds;
                        for (int i = 0; i < missedSeconds; i++)
                        {
                            _secDown.Enqueue(0); if (_secDown.Count > 305) _secDown.Dequeue();
                            _secUp.Enqueue(0); if (_secUp.Count > 305) _secUp.Dequeue();
                            _cpuSec.Enqueue(0); if (_cpuSec.Count > 305) _cpuSec.Dequeue();
                            _ramSec.Enqueue(0); if (_ramSec.Count > 305) _ramSec.Dequeue();
                            _gpuSec.Enqueue(0); if (_gpuSec.Count > 305) _gpuSec.Dequeue();
                            _diskSec.Enqueue(0); if (_diskSec.Count > 305) _diskSec.Dequeue();
                            _tickSec++;
                            if (_tickSec >= 60)
                            {
                                _tickSec = 0;
                                _minDown.Enqueue(0); if (_minDown.Count > 185) _minDown.Dequeue();
                                _minUp.Enqueue(0); if (_minUp.Count > 185) _minUp.Dequeue();
                                _cpuMin.Enqueue(0); if (_cpuMin.Count > 185) _cpuMin.Dequeue();
                                _ramMin.Enqueue(0); if (_ramMin.Count > 185) _ramMin.Dequeue();
                                _gpuMin.Enqueue(0); if (_gpuMin.Count > 185) _gpuMin.Dequeue();
                                _diskMin.Enqueue(0); if (_diskMin.Count > 185) _diskMin.Dequeue();
                                _tickMin++;
                                if (_tickMin >= 60)
                                {
                                    _tickMin = 0;
                                    _hourDown.Enqueue(0); if (_hourDown.Count > 175) _hourDown.Dequeue();
                                    _hourUp.Enqueue(0); if (_hourUp.Count > 175) _hourUp.Dequeue();
                                    _cpuHour.Enqueue(0); if (_cpuHour.Count > 175) _cpuHour.Dequeue();
                                    _ramHour.Enqueue(0); if (_ramHour.Count > 175) _ramHour.Dequeue();
                                    _gpuHour.Enqueue(0); if (_gpuHour.Count > 175) _gpuHour.Dequeue();
                                    _diskHour.Enqueue(0); if (_diskHour.Count > 175) _diskHour.Dequeue();
                                }
                            }
                        }
                    }
                }
                ApplyTheme();
            }
            catch { _savedData = new DashboardSaveData(); }
            _savedData.FileLogs ??= new List<FileIoEvent>();
            _uiFileLogs = new ObservableCollection<FileIoEvent>(_savedData.FileLogs);
            if (this.FindName("GridFileLogs") is DataGrid fileGrid) fileGrid.ItemsSource = _uiFileLogs;

            Task.Run(() => {
                foreach (var log in _savedData.FileLogs)
                {
                    if (log.Icon == null && !string.IsNullOrEmpty(log.FilePath))
                    {
                        var icon = GetIcon(log.FilePath);
                        Dispatcher.InvokeAsync(() => log.Icon = icon);
                    }
                }
            });
            MainWindow.TooltipMode = _savedData.TooltipMode;

            // --- ★ 字典外部 JSON 生成与加载逻辑 ---
            string dictPath = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "process_dict.json");
            Dictionary<string, string> activeDict;

            if (!File.Exists(dictPath))
            {
                // 如果没有字典文件，直接把自带的默认字典写入创建
                activeDict = new Dictionary<string, string>(ProcessDictionary.DefaultDict, StringComparer.OrdinalIgnoreCase);
                string json = JsonSerializer.Serialize(activeDict, new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
                File.WriteAllText(dictPath, json);
            }
            else
            {
                // 有就直接读取
                string json = File.ReadAllText(dictPath);
                activeDict = JsonSerializer.Deserialize<Dictionary<string, string>>(json) ?? new Dictionary<string, string>();
            }

            ProcessDictionary.LoadCustomDict(activeDict);

            Application.Current.Dispatcher.Invoke(() => {
                DictItemsList.Clear();
                foreach (var kvp in activeDict) DictItemsList.Add(new DictItem { Key = kvp.Key, Value = kvp.Value });
                DictItemsList.CollectionChanged += (s, e) => UpdateCustomDict();
            });
            // --------------------------------------
        }

        // ==========================================
        // 2. 界面与图表绘制逻辑 
        // ==========================================
        private void Nav_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn)
            {
                UpdateNavStyle(btn);

                // ViewSettings 的隐藏逻辑
                ViewTraffic.Visibility = Visibility.Collapsed;
                ViewSystem.Visibility = Visibility.Collapsed;
                ViewResources.Visibility = Visibility.Collapsed;
                ViewSettings.Visibility = Visibility.Collapsed;
                ViewEarth.Visibility = Visibility.Collapsed;
                // 检查动态查找的视图
                if (this.FindName("ViewLog") is Grid vLog) vLog.Visibility = Visibility.Collapsed;
                if (this.FindName("ViewLauncher") is Grid vLaunch) vLaunch.Visibility = Visibility.Collapsed;
                if (this.FindName("ViewScanner") is Grid vScan) vScan.Visibility = Visibility.Collapsed; // 确保这行存在 [cite: 1]
                FrameworkElement targetView = null; // 修复：使用基类 FrameworkElement 以兼容 Grid 和 ScrollViewer
                // 根据点击的按钮显示对应视图
                if (btn.Name == "NavTraffic")
                {
                    targetView = ViewTraffic;
                    _activeTab = "Traffic";
                }
                else if (btn.Name == "NavSystem")
                {
                    targetView = ViewSystem;
                    _activeTab = "System";
                    RefreshDashboards();
                }
                else if (btn.Name == "NavResources")
                {
                    targetView = ViewResources;
                    _activeTab = "Resources";
                    RefreshDashboards();
                    DrawDonutChart();
                }
                else if (btn.Name == "NavSettings")
                {
                    targetView = ViewSettings;
                    _activeTab = "Settings";
                    SyncThemeUI();
                }
                else if (btn.Name == "NavLog")
                {
                    if (this.FindName("ViewLog") is Grid vLogShow) targetView = vLogShow;
                    _activeTab = "Log";
                    InitializeLogCategories(); // 初始化侧边栏
                    if (this.FindName("LogCategoryList") is ListBox lb && lb.SelectedIndex == -1) lb.SelectedIndex = 0;
                }
                else if (btn.Name == "NavLauncher")
                {
                    if (this.FindName("ViewLauncher") is Grid vLaunchShow) targetView = vLaunchShow;
                    _activeTab = "Launcher";
                }
                else if (btn.Name == "NavScanner")
                {
                    if (this.FindName("ViewScanner") is Grid vScanShow) targetView = vScanShow;
                    _activeTab = "Scanner";
                    //点击时不再执行耗时的并发深度探测，而是瞬间获取本地缓存的旧ARP表供展示
                    if (_scannerInterfaces.Count == 0) QuickLoadArpTable();
                }

                // 为目标视图应用带阻尼感的上浮淡入翻页动画
                if (targetView != null)
                {
                    targetView.Visibility = Visibility.Visible;

                    targetView.Opacity = 0;
                    var transform = new TranslateTransform(0, 15);
                    targetView.RenderTransform = transform;

                    var fade = new System.Windows.Media.Animation.DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(250));
                    var slide = new System.Windows.Media.Animation.DoubleAnimation(15, 0, TimeSpan.FromMilliseconds(250))
                    {
                        EasingFunction = new System.Windows.Media.Animation.CubicEase { EasingMode = System.Windows.Media.Animation.EasingMode.EaseOut }
                    };

                    targetView.BeginAnimation(UIElement.OpacityProperty, fade);
                    transform.BeginAnimation(TranslateTransform.YProperty, slide);
                }
            }

        }
        private void UpdateNavStyle(Button activeBtn)
        {
            // 1. 定义所有参与导航的按钮集合
            var navButtons = new List<Button> { NavTraffic, NavSystem, NavResources, NavLog, NavSettings, NavLauncher, NavScanner };
            // 2. 统一重置所有按钮样式为“未选中”
            foreach (var b in navButtons)
            {
                if (b == null) continue;
                b.BorderBrush = _inactiveBrush; // 透明底边
                b.Foreground = (Brush)this.Resources["TextDimBrush"]; // 使用动态变暗文字画刷
            }

            // 3. 激活当前选中的按钮
            activeBtn.BorderBrush = _activeBrush; // 蓝色底边
            activeBtn.Foreground = (Brush)this.Resources["TextMainBrush"]; // 使用动态主文字画刷
        }
        private void BtnToggleDonutMode_Click(object sender, RoutedEventArgs e)
        {
            _showAppByTime = !_showAppByTime;
            BtnToggleDonutMode.Content = _showAppByTime ? "当前展示: 按连接时长 (Time) 🔄" : "当前展示: 按产生流量 (Traffic) 🔄";

            // ★ 修复 2：如果是在历史模式，重新触发过滤函数，而不要直接画图
            if (BtnBackToLive.Visibility == Visibility.Visible && !string.IsNullOrEmpty(_currentHistoryDate))
            {
                if (TimeSpan.TryParse(TxtHistoryStart.Text, out TimeSpan start) && TimeSpan.TryParse(TxtHistoryEnd.Text, out TimeSpan end))
                {
                    UpdateHistoricalViewsForTimeRange(_currentHistoryDate, start, end);
                }
            }
            else
            {
                DrawDonutChart(); // 实时模式正常重绘
            }
        }
        private bool _isAppDistListView = false;
        private void BtnToggleAppDistView_Click(object sender, RoutedEventArgs e)
        {
            _isAppDistListView = !_isAppDistListView;
            DonutCanvas.Visibility = _isAppDistListView ? Visibility.Collapsed : Visibility.Visible;
            if (this.FindName("GridAppDistribution") is DataGrid grid)
                grid.Visibility = _isAppDistListView ? Visibility.Visible : Visibility.Collapsed;

            DrawDonutChart(); // 重新走一边渲染逻辑分发数据
        }
        private void RefreshDashboards()
        {
            DateTime today = DateTime.Today;
            long todayBytes = _savedData.DailyTraffic.GetValueOrDefault(today.ToString("yyyy-MM-dd"), 0);
            long todayVpn = _savedData.DailyVpnTraffic.GetValueOrDefault(today.ToString("yyyy-MM-dd"), 0);
            long weekBytes = 0, monthBytes = 0, weekVpn = 0, monthVpn = 0;

            for (int i = 0; i < 30; i++)
            {
                string dStr = today.AddDays(-i).ToString("yyyy-MM-dd");
                long val = _savedData.DailyTraffic.GetValueOrDefault(dStr, 0);
                long vpnVal = _savedData.DailyVpnTraffic.GetValueOrDefault(dStr, 0);
                if (i < 7) { weekBytes += val; weekVpn += vpnVal; }
                monthBytes += val; monthVpn += vpnVal;
            }

            TxtToday.Text = FormatAdaptiveTotal(todayBytes);
            TxtWeek.Text = FormatAdaptiveTotal(weekBytes);
            TxtMonth.Text = FormatAdaptiveTotal(monthBytes);

            // 显示 VPN 数据
            if (this.FindName("TxtTodayVpn") is TextBlock t1) t1.Text = $"VPN: {FormatAdaptiveTotal(todayVpn)}";
            if (this.FindName("TxtWeekVpn") is TextBlock t2) t2.Text = $"VPN: {FormatAdaptiveTotal(weekVpn)}";
            if (this.FindName("TxtMonthVpn") is TextBlock t3) t3.Text = $"VPN: {FormatAdaptiveTotal(monthVpn)}";

            DrawHeatmap(); DrawBarChart(); DrawDonutChart(); DrawHourlyChart(); DrawResourceHistoryHeatmap();
        }
        private void DrawHeatmap()
        {
            HeatmapCanvas.Children.Clear();
            double rectSize = 12, spacing = 3;
            double offsetX = 35, offsetY = 25; // 预留左侧星期和上方月份的空间

            // 更新右上角的年份文本
            if (this.FindName("HeatmapYearText") is TextBlock yearText)
            {
                yearText.Text = _selectedHeatmapYear.ToString();
            }

            // 获取选中年份的第一天，并回退到最近的星期日作为矩阵起始点
            DateTime firstDayOfYear = new DateTime(_selectedHeatmapYear, 1, 1);
            DateTime startDate = firstDayOfYear.AddDays(-(int)firstDayOfYear.DayOfWeek);

            long maxDaily = _savedData.DailyTraffic.Values.DefaultIfEmpty(1).Max();

            // 绘制左侧星期标签
            string[] weekDays = { "Sun", "Mon", "Tue", "Wed", "Thu", "Fri", "Sat" };
            for (int i = 0; i < 7; i++)
            {
                TextBlock tb = new TextBlock { Text = weekDays[i], Foreground = Brushes.Gray, FontSize = 10 };
                Canvas.SetLeft(tb, 5);
                // ★ 修复 1：去掉 i*2+1，直接用 i 对齐当前 row
                Canvas.SetTop(tb, offsetY + i * (rectSize + spacing) - 2);
                HeatmapCanvas.Children.Add(tb);
            }

            int lastMonth = -1;
            for (int col = 0; col < 54; col++) // 包含跨年的边缘情况
            {
                for (int row = 0; row < 7; row++)
                {
                    DateTime cellDate = startDate.AddDays(col * 7 + row);

                    // 如果方块代表的日期不属于当前所选年份，则跳过不渲染，呈现 GitHub 的对齐效果
                    if (cellDate.Year != _selectedHeatmapYear) continue;

                    // 绘制上方月份标签
                    if (cellDate.Month != lastMonth)
                    {
                        TextBlock tbMonth = new TextBlock { Text = cellDate.ToString("MMM"), Foreground = Brushes.Gray, FontSize = 10 };
                        Canvas.SetLeft(tbMonth, offsetX + col * (rectSize + spacing));
                        Canvas.SetTop(tbMonth, 5);
                        HeatmapCanvas.Children.Add(tbMonth);
                        lastMonth = cellDate.Month;
                    }

                    long val = _savedData.DailyTraffic.GetValueOrDefault(cellDate.ToString("yyyy-MM-dd"), 0);
                    byte intensity = 30;
                    if (val > 0) intensity = (byte)(50 + (val / (double)maxDaily) * 205);

                    Rectangle r = new Rectangle { Width = rectSize, Height = rectSize, RadiusX = 2, RadiusY = 2, Fill = val > 0 ? new SolidColorBrush(Color.FromRgb(0, intensity, (byte)(intensity / 2))) : (Brush)this.Resources["HeatmapEmptyBrush"] };
                    string tipContent = $"{cellDate:yyyy-MM-dd}\n流量: {FormatAdaptiveTotal(val)}"; // 或对应的资源描述
                    SetQuickToolTip(r, tipContent); 

                    // 附加偏移量
                    Canvas.SetLeft(r, offsetX + col * (rectSize + spacing));
                    Canvas.SetTop(r, offsetY + row * (rectSize + spacing));

                    // 绑定点击事件，依然保留原本点击弹出单日窗口明细的功能
                    r.Cursor = Cursors.Hand;
                    DateTime captureDate = cellDate;
                    r.MouseLeftButtonUp += (s, e) => ShowHeatmapPopup(captureDate);

                    HeatmapCanvas.Children.Add(r);
                }
            }
        }

        // 新增的年份切换按钮事件
        private void BtnPrevYear_Click(object sender, RoutedEventArgs e)
        {
            _selectedHeatmapYear--;
            DrawHeatmap();
        }

        private void BtnNextYear_Click(object sender, RoutedEventArgs e)
        {
            // 限制不能向右查看到尚未到达的未来年份
            if (_selectedHeatmapYear < DateTime.Today.Year)
            {
                _selectedHeatmapYear++;
                DrawHeatmap();
            }
        }


        // ★ 热力图点击弹出窗口逻辑
        private void ShowHeatmapPopup(DateTime date)
        {
            string dateStr = date.ToString("yyyy-MM-dd");
            HeatmapPopupDate.Text = $"{dateStr} 活动窗口统计";

            if (_savedData.DailyAppActiveTime != null &&
                _savedData.DailyAppActiveTime.TryGetValue(dateStr, out var dailyData) &&
                dailyData.Count > 0)
            {
                var displayList = dailyData.OrderByDescending(x => x.Value)
                                           .Take(15) // 取前 15 名
                                           .Select(x => new { Key = x.Key, Value = FormatActiveTime(x.Value) })
                                           .ToList();
                HeatmapPopupList.ItemsSource = displayList;
            }
            else
            {
                HeatmapPopupList.ItemsSource = new List<dynamic> { new { Key = "暂无该日的窗口活动记录", Value = "" } };
            }

            HeatmapPopup.Visibility = Visibility.Visible;
        }

        private void CloseHeatmapPopup_Click(object sender, RoutedEventArgs e)
        {
            HeatmapPopup.Visibility = Visibility.Collapsed;
        }

        private void DrawBarChart()
        {
            if (BarChartCanvas == null) return;
            BarChartCanvas.Children.Clear();

            double w = BarChartCanvas.ActualWidth > 0 ? BarChartCanvas.ActualWidth : 800;
            double h = BarChartCanvas.ActualHeight > 0 ? BarChartCanvas.ActualHeight : 150;

            if (BarChartCanvas.ActualWidth <= 0 && w > 800) w -= 30;

            // ★ 修复：逆向侦测过去30天内，最老的一笔数据存在于哪一天
            int oldestDataIndex = 0;
            for (int i = 29; i >= 0; i--)
            {
                if (_savedData.DailyTraffic.GetValueOrDefault(DateTime.Today.AddDays(-i).ToString("yyyy-MM-dd"), 0) > 0)
                {
                    oldestDataIndex = i;
                    break;
                }
            }
            // 计算需要被渲染的有效天数（保证最少展示5天的槽位，避免单日数据占据整个屏幕导致柱子极度臃肿）
            int days = oldestDataIndex + 1;
            if (days < 5) days = 5;

            double leftMargin = 35; // 为左侧 Y 轴预留空间
            double topOffset = 25;
            double chartHeight = h - 20 - topOffset;
            double yAxisY = topOffset + chartHeight; // X轴所处的绝对Y坐标

            double barAreaWidth = w - leftMargin;

            // 动态计算柱子宽度
            double maxBarStep = 55; // 限制每个柱子占用的最大步长(放宽限制，让少数据时可以变宽)
            double actualBarStep = Math.Min(barAreaWidth / days, maxBarStep);
            double actualBarWidth = Math.Max(1, actualBarStep - 6);

            // 计算实际占用的绘图总跨度，并使其在画布中绝对居中
            double totalDrawWidth = days * actualBarStep;
            double xOffset = leftMargin + (barAreaWidth - totalDrawWidth) / 2.0;

            // 提取数据并寻找最大值
            long maxVal = 1;
            var data = new long[days];
            for (int i = 0; i < days; i++)
            {
                data[days - 1 - i] = _savedData.DailyTraffic.GetValueOrDefault(DateTime.Today.AddDays(-(days - 1 - i)).ToString("yyyy-MM-dd"), 0);
                if (data[days - 1 - i] > maxVal) maxVal = data[days - 1 - i];
            }
            // ================= 新增：动态计算 Y 轴单位和刻度 =================
            double div = 1;
            string unit = "B";
            if (maxVal >= 1073741824) { div = 1073741824.0; unit = "GB"; }
            else if (maxVal >= 1048576) { div = 1048576.0; unit = "MB"; }
            else if (maxVal >= 1024) { div = 1024.0; unit = "KB"; }

            double maxScale = maxVal / div;

            // 决定步长 (尽量分成 4~5 个刻度段落)
            double rawStep = maxScale / 4.0;
            double step = 1;
            if (rawStep <= 0.1) step = 0.1;
            else if (rawStep <= 0.25) step = 0.25;
            else if (rawStep <= 0.5) step = 0.5;
            else if (rawStep <= 1) step = 1;
            else if (rawStep <= 2) step = 2;
            else if (rawStep <= 5) step = 5;
            else if (rawStep <= 10) step = 10;
            else step = Math.Ceiling(rawStep / 10.0) * 10;

            double topValue = step * 5; // 最高到 5 个步长
            long chartMaxVal = (long)(topValue * div); // 获取新的逻辑上限值

            // 绘制 Y 轴单位标签 (如 "GB")
            TextBlock unitLbl = new TextBlock { Text = unit, Foreground = Brushes.Gray, FontSize = 10, FontWeight = FontWeights.Bold };
            Canvas.SetLeft(unitLbl, leftMargin - 15);
            Canvas.SetTop(unitLbl, 0); // 贴在最顶端
            BarChartCanvas.Children.Add(unitLbl);
            // 绘制 Y 轴横向网格线和刻度值
            for (int i = 0; i <= 5; i++)
            {
                double val = step * i;
                double y = topOffset + chartHeight - (val / topValue) * chartHeight; // ★ 加上 topOffset

                if (y < topOffset - 5) continue; // 超出图表顶部则忽略

                Line gLine = new Line { X1 = leftMargin, X2 = w, Y1 = y, Y2 = y, Stroke = (Brush)this.Resources["GridLineBrush"], StrokeThickness = 1 };
                BarChartCanvas.Children.Add(gLine);

                TextBlock lbl = new TextBlock { Text = val.ToString("0.##"), Foreground = Brushes.Gray, FontSize = 10 };
                lbl.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                Canvas.SetLeft(lbl, leftMargin - lbl.DesiredSize.Width - 8);
                Canvas.SetTop(lbl, y - 7);
                BarChartCanvas.Children.Add(lbl);
            }
            // =================================================================

            // 绘制基础横坐标轴 (X轴)
            Line xAxis = new Line { X1 = leftMargin, X2 = w, Y1 = yAxisY, Y2 = yAxisY, Stroke = new SolidColorBrush(Color.FromRgb(60, 60, 65)), StrokeThickness = 1 };
            BarChartCanvas.Children.Add(xAxis);

            // 绘制柱状图
            // ▼ 2. 补全缺失的画柱子代码！直接粘贴到方法最底部 ▼
            // 绘制基础横坐标轴 (X轴) ...下方替换：
            for (int i = 0; i < days; i++)
            {
                string dateStr = DateTime.Today.AddDays(-(days - 1 - i)).ToString("yyyy-MM-dd");
                long val = data[i];
                long vpnVal = _savedData.DailyVpnTraffic.GetValueOrDefault(dateStr, 0);

                double barH = val > 0 ? Math.Max(1, (val / (double)chartMaxVal) * chartHeight) : 0;
                double vpnH = vpnVal > 0 ? Math.Max(1, (vpnVal / (double)chartMaxVal) * chartHeight) : 0;
                if (vpnH > barH) vpnH = barH; // 防止 VPN 计算溢出超过总高度

                if (barH > 0)
                {
                    double bx = xOffset + i * actualBarStep;

                    // 1. 画主流量柱
                    Rectangle r = new Rectangle { Width = actualBarWidth, Height = barH, Fill = new SolidColorBrush(Color.FromRgb(0, 229, 255)), RadiusX = 2, RadiusY = 2, Opacity = 0.8 };

                    SetQuickToolTip(r, $"{dateStr}\n总流量: {FormatAdaptiveTotal(val)}\nVPN: {FormatAdaptiveTotal(vpnVal)}");
                    r.MouseEnter += (s, e) => { r.Opacity = 1.0; };
                    r.MouseLeave += (s, e) => { r.Opacity = 0.8; };
                    Canvas.SetLeft(r, bx); Canvas.SetTop(r, yAxisY - barH);
                    BarChartCanvas.Children.Add(r);

                    // 2. 画 VPN 流量叠加柱（橘色，紧贴底部）
                    if (vpnH > 0)
                    {
                        Rectangle vR = new Rectangle { Width = actualBarWidth, Height = vpnH, Fill = new SolidColorBrush(Color.FromRgb(255, 152, 0)), RadiusX = 2, RadiusY = 2, Opacity = 0.9, IsHitTestVisible = false };
                        Canvas.SetLeft(vR, bx); Canvas.SetTop(vR, yAxisY - vpnH);
                        BarChartCanvas.Children.Add(vR);
                    }

                    // 3. 画顶部文字 (简化单位以防拥挤)
                    TextBlock valTxt = new TextBlock
                    {
                        Text = val < 1048576 ? $"{val / 1024}K" : val < 1073741824 ? $"{val / 1048576}M" : $"{(val / 1073741824.0):F1}G",
                        Foreground = Brushes.Gray,
                        FontSize = 8,
                        IsHitTestVisible = false
                    };
                    valTxt.Measure(new Size(999, 999));
                    Canvas.SetLeft(valTxt, bx + (actualBarWidth - valTxt.DesiredSize.Width) / 2);
                    Canvas.SetTop(valTxt, yAxisY - barH - 12);
                    BarChartCanvas.Children.Add(valTxt);
                }
            }
        }

        private void DrawHourlyChart(Dictionary<int, long> sourceData = null, int startHour = 0, int endHour = 24)
        {
            if (HourlyChartCanvas == null) return;
            HourlyChartCanvas.Children.Clear();

            string todayStr = DateTime.Today.ToString("yyyy-MM-dd");
            var defaultData = _savedData.DailyHourlyActive.GetValueOrDefault(todayStr, new Dictionary<int, long>());
            var data = sourceData ?? defaultData;

            double w = HourlyChartCanvas.ActualWidth > 0 ? HourlyChartCanvas.ActualWidth : 400;
            double h = HourlyChartCanvas.ActualHeight > 0 ? HourlyChartCanvas.ActualHeight : 150;

            double marginL = 30, marginB = 20;
            double chartW = w - marginL, chartH = h - marginB;

            int totalHours = endHour - startHour;
            if (totalHours <= 0) return;
            double barWidth = (chartW / totalHours) - 2;

            long maxVal = 1;
            for (int i = startHour; i < endHour; i++)
            {
                long v = data.GetValueOrDefault(i, 0);
                if (v > maxVal) maxVal = v;
            }

            for (int i = startHour; i < endHour; i++)
            {
                long val = data.GetValueOrDefault(i, 0);
                double barH = (val / (double)maxVal) * chartH;
                if (barH < 1 && val > 0) barH = 1;

                Rectangle r = new Rectangle { Width = Math.Max(1, barWidth), Height = Math.Max(1, barH), Fill = new SolidColorBrush(Color.FromRgb(0, 229, 255)), RadiusX = 1, RadiusY = 1, Opacity = 0.8 };
                SetQuickToolTip(r, $"{i}:00 - {i + 1}:00\n活跃度: {val}");
                double displayVal = Math.Round((double)val, 0);
                r.ToolTip = new ToolTip
                {
                    Content = $"{i:00}:00 - {i + 1:00}:00\n累计运行: {displayVal} 分钟",

                    Background = (Brush)this.Resources["BgCardBrush"],
                    Foreground = (Brush)this.Resources["TextMainBrush"],
                    BorderBrush = (Brush)this.Resources["BorderMainBrush"],

                    Padding = new Thickness(8, 5, 8, 5),
                    BorderThickness = new Thickness(1)
                };
                Canvas.SetLeft(r, marginL + (i - startHour) * (chartW / totalHours));
                Canvas.SetBottom(r, marginB);
                HourlyChartCanvas.Children.Add(r);

                int labelStep = totalHours <= 6 ? 1 : (totalHours <= 12 ? 2 : 4);
                if ((i - startHour) % labelStep == 0)
                {
                    TextBlock tb = new TextBlock { Text = $"{i:D2}:00", Foreground = Brushes.Gray, FontSize = 9 };
                    Canvas.SetLeft(tb, marginL + (i - startHour) * (chartW / totalHours));
                    Canvas.SetBottom(tb, 2);
                    HourlyChartCanvas.Children.Add(tb);
                }
            }
        }

        private void DrawDonutChart(Dictionary<string, long>? customData = null)
        {
            if (DonutCanvas == null) return;
            // 修复：如果是在列表模式下，即便宽度是 0 也应该刷新列表
            if (!_isAppDistListView && DonutCanvas.ActualWidth == 0) return;

            var combinedData = new Dictionary<string, long>();
            if (customData != null)
            {
                foreach (var kv in customData) combinedData[kv.Key] = kv.Value;
            }
            else
            {
                string todayStr = DateTime.Today.ToString("yyyy-MM-dd");
                if (_showAppByTime)
                {
                    var todayTime = _savedData.DailyAppActiveTime.GetValueOrDefault(todayStr, new Dictionary<string, long>());
                    foreach (var kv in todayTime) combinedData[kv.Key] = kv.Value;
                }
                else
                {
                    var todayTraffic = _savedData.DailyAppTraffic.GetValueOrDefault(todayStr, new Dictionary<string, long>());
                    foreach (var kv in todayTraffic) combinedData[kv.Key] = kv.Value;
                }
            }

            var allApps = combinedData.OrderByDescending(x => x.Value).ToList();
            if (this.FindName("TxtDonutLoading") is TextBlock tbLoading) tbLoading.Visibility = Visibility.Collapsed;
            // ★ 修复：给隐藏的列表智能注入数据，比对涨幅以显示三角形
            if (this.FindName("GridAppDistribution") is DataGrid grid)
            {
                // 移除已经不存在的进程
                var toRemove = _appDistList.Where(x => !allApps.Any(s => ProcessDictionary.GetWithDesc(s.Key) == x.Name)).ToList();
                foreach (var item in toRemove) _appDistList.Remove(item);

                foreach (var kvp in allApps)
                {
                    string dName = ProcessDictionary.GetWithDesc(kvp.Key);
                    string vDisp = _showAppByTime ? FormatActiveTime(kvp.Value) : FormatAdaptiveTotal(kvp.Value);

                    var existing = _appDistList.FirstOrDefault(x => x.Name == dName);
                    if (existing != null)
                    {
                        // 如果值比上一次大（说明2秒内有流量产生），则显示红三角
                        existing.IsUpdating = kvp.Value > existing.RawValue;
                        existing.RawValue = kvp.Value;
                        existing.ValueDisplay = vDisp;
                    }
                    else
                    {
                        _appDistList.Add(new AppDistItem { Name = dName, ValueDisplay = vDisp, RawValue = kvp.Value, IsUpdating = true });
                    }
                }

                // 冒泡排序法保持界面元素的平滑移动（避免重建导致列表闪烁）
                for (int i = 0; i < _appDistList.Count - 1; i++)
                {
                    for (int j = 0; j < _appDistList.Count - i - 1; j++)
                    {
                        if (_appDistList[j].RawValue < _appDistList[j + 1].RawValue)
                        {
                            _appDistList.Move(j, j + 1);
                        }
                    }
                }
            }
            // 饼图只取前 6 名以保证界面美观
            var topApps = allApps.Take(6).ToList();

            if (topApps.Count == 0)
            {
                DonutCanvas.Children.Clear();
                DonutCanvas.Uid = "";
                if (!_isAppDistListView)
                {
                    TextBlock tbEmpty = new TextBlock
                    {
                        Text = _showAppByTime ? "📭 暂无产生时长的进程" : "📭 暂无产生流量的进程",
                        Foreground = new SolidColorBrush(Color.FromRgb(100, 100, 110)),
                        FontSize = 14,
                        FontWeight = FontWeights.Bold
                    };
                    tbEmpty.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                    Canvas.SetLeft(tbEmpty, (DonutCanvas.ActualWidth - tbEmpty.DesiredSize.Width) / 2);
                    Canvas.SetTop(tbEmpty, (DonutCanvas.ActualHeight - tbEmpty.DesiredSize.Height) / 2);
                    DonutCanvas.Children.Add(tbEmpty);
                }
                return;
            }

            // 如果处于列表模式，不画饼图直接返回即可
            if (_isAppDistListView) return;

            string currentHash = string.Join(",", topApps.Select(x => x.Key + x.Value));
            if (DonutCanvas.Uid == currentHash) return;
            DonutCanvas.Uid = currentHash;
            DonutCanvas.Children.Clear();
            DonutCanvas.Tag = topApps; // ★ 必须写入 Tag，否则 MouseMove 将因获取不到数据而强行 return 导致失效！


            long total = topApps.Sum(x => x.Value);
            double curAngle = 0;
            Point center = new Point(DonutCanvas.ActualWidth / 2, DonutCanvas.ActualHeight / 2);
            double outerR = 60, innerR = 35;
            Color[] colors = { Color.FromRgb(66, 133, 244), Color.FromRgb(219, 68, 55), Color.FromRgb(244, 180, 0), Color.FromRgb(15, 157, 88), Color.FromRgb(171, 71, 188), Color.FromRgb(0, 172, 193) };

            List<double> rightYs = new List<double>(), leftYs = new List<double>();

            for (int i = 0; i < topApps.Count; i++)
            {
                double percentage = topApps[i].Value / (double)total;
                if (percentage < 0.001) continue;
                double sweep = percentage * 360; if (sweep >= 360) sweep = 359.9;
                double startRad = curAngle * Math.PI / 180, endRad = (curAngle + sweep) * Math.PI / 180;

                var pg = new PathGeometry();
                var pf = new PathFigure { IsClosed = true, StartPoint = new Point(center.X + Math.Cos(startRad) * innerR, center.Y + Math.Sin(startRad) * innerR) };
                pf.Segments.Add(new LineSegment(new Point(center.X + Math.Cos(startRad) * outerR, center.Y + Math.Sin(startRad) * outerR), true));
                pf.Segments.Add(new ArcSegment(new Point(center.X + Math.Cos(endRad) * outerR, center.Y + Math.Sin(endRad) * outerR), new Size(outerR, outerR), sweep, sweep > 180, SweepDirection.Clockwise, true));
                pf.Segments.Add(new LineSegment(new Point(center.X + Math.Cos(endRad) * innerR, center.Y + Math.Sin(endRad) * innerR), true));
                pf.Segments.Add(new ArcSegment(new Point(center.X + Math.Cos(startRad) * innerR, center.Y + Math.Sin(startRad) * innerR), new Size(innerR, innerR), sweep, sweep > 180, SweepDirection.Counterclockwise, true));
                pg.Figures.Add(pf);

                var p = new System.Windows.Shapes.Path { Fill = new SolidColorBrush(colors[i % colors.Length]), Opacity = 0.85, Data = pg, Stroke = Brushes.Transparent, StrokeThickness = 2 };

                string displayName = topApps[i].Key;
                string displayVal = _showAppByTime ? FormatActiveTime(topApps[i].Value) : FormatAdaptiveTotal(topApps[i].Value);

                string tooltipText = ProcessDictionary.GetTooltip(displayName);
                //p.ToolTip = new ToolTip
                //{
                //    Content = $"{(tooltipText != null ? tooltipText + "\n" : "")}占比: {percentage:P1} ({displayVal})",
                //    Background = (Brush)this.Resources["BgCardBrush"],
                //    Foreground = (Brush)this.Resources["TextMainBrush"]
                //};
                //ToolTipService.SetInitialShowDelay(p, 0);

                p.MouseEnter += (s, e) => { p.Opacity = 1.0; p.Stroke = Brushes.White; Panel.SetZIndex(p, 100); };
                p.MouseLeave += (s, e) => { p.Opacity = 0.85; p.Stroke = Brushes.Transparent; Panel.SetZIndex(p, 0); };
                DonutCanvas.Children.Add(p);

                double midAngle = startRad + (sweep * Math.PI / 180) / 2;
                bool isRight = Math.Cos(midAngle) > 0;
                double labelY = center.Y + Math.Sin(midAngle) * (outerR + 20);
                var yList = isRight ? rightYs : leftYs;
                int maxIter = 10; bool collision = true;
                while (collision && maxIter-- > 0) { collision = false; foreach (var y in yList) { if (Math.Abs(y - labelY) < 16) { labelY += 16; collision = true; break; } } }
                yList.Add(labelY);

                Point p1 = new Point(center.X + Math.Cos(midAngle) * (outerR - 5), center.Y + Math.Sin(midAngle) * (outerR - 5));
                Point p2 = new Point(center.X + (isRight ? 1 : -1) * (outerR + 15), labelY);
                double lineEndX = isRight ? p2.X + 25 : p2.X - 25;
                DonutCanvas.Children.Add(new Polyline { Points = new PointCollection { p1, p2, new Point(lineEndX, p2.Y) }, Stroke = p.Fill, StrokeThickness = 1.5, IsHitTestVisible = false });

                var tb = new TextBlock { Text = $"{displayName} {percentage:P1}", Foreground = p.Fill, FontSize = 10, FontWeight = FontWeights.Bold, IsHitTestVisible = false };
                tb.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                Canvas.SetLeft(tb, isRight ? lineEndX + 3 : lineEndX - tb.DesiredSize.Width - 3); Canvas.SetTop(tb, p2.Y - 7);
                DonutCanvas.Children.Add(tb);

                curAngle += sweep;
            }
        }
        // ★ 新增自适应尺寸刷新事件
        private void BarChartCanvas_SizeChanged(object sender, SizeChangedEventArgs e) { if (_activeTab == "System") DrawBarChart(); }
        private void HourlyChartCanvas_SizeChanged(object sender, SizeChangedEventArgs e) { if (_activeTab == "Resources") DrawHourlyChart(); }
        // ★ 修复 Bug 3：利用原生事件在行展开瞬间立即抓取基础进程信息
        private void ProcessGrid_LoadingRowDetails(object sender, DataGridRowDetailsEventArgs e)
        {
            if (e.Row.Item is ProcessNetworkInfo rowData)
            {
                Task.Run(() => {
                    try
                    {
                        string pName = rowData.ProcessName;
                        var procs = Process.GetProcessesByName(pName.Replace(".exe", ""));
                        if (procs.Length > 0)
                        {
                            string path = procs[0].MainModule?.FileName;
                            var ver = FileVersionInfo.GetVersionInfo(path);
                            // 切回主线程刷新UI
                            Application.Current.Dispatcher.InvokeAsync(() => {
                                rowData.ProcessPath = "路径: " + path;
                                rowData.ProcessVersion = "版本: " + (ver.FileVersion ?? "未知");
                                rowData.ProcessPublisher = "发布者: " + (ver.CompanyName ?? "未知");
                            });
                        }
                        else
                        {
                            Application.Current.Dispatcher.InvokeAsync(() => {
                                rowData.ProcessPath = "路径: 进程已退出或被系统隐藏，无法获取";
                                rowData.ProcessVersion = "版本: 未知";
                                rowData.ProcessPublisher = "发布者: 未知";
                            });
                        }
                    }
                    catch
                    {
                        Application.Current.Dispatcher.InvokeAsync(() => {
                            rowData.ProcessPath = "路径: 权限不足，无法访问此系统级或受保护进程";
                            rowData.ProcessVersion = "版本: 权限受限";
                            rowData.ProcessPublisher = "发布者: 权限受限";
                        });
                    }
                });
            }
        }

        // ★ 修复 Bug 2：移除 dynamic 采用强类型，不再承担获取基础信息的职责
        private async void BtnShowIPs_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.DataContext is ProcessNetworkInfo rowData)
            {
                rowData.LoadingVisibility = Visibility.Visible;
                rowData.IpGridVisibility = Visibility.Collapsed;

                await Task.Delay(800); // 模拟耗时的定位和查IP库时间

                var tempConnections = new List<IPConnectionInfo>();
                foreach (var c in rowData.Connections) tempConnections.Add(c);

                foreach (IPConnectionInfo conn in tempConnections)
                {
                    conn.Region = "Unknown / ISP";
                }

                rowData.LoadingVisibility = Visibility.Collapsed;
                rowData.IpGridVisibility = Visibility.Visible;
            }
        }
        private void DonutCanvas_MouseMove(object sender, MouseEventArgs e)
        {
            if (DonutCanvas.Tag is not List<KeyValuePair<string, long>> topApps || topApps.Count == 0) return;

            // 修复：使用画布真实宽高计算中心点，解决悬浮不触发
            double dx = e.GetPosition(DonutCanvas).X - (DonutCanvas.ActualWidth / 2);
            double dy = e.GetPosition(DonutCanvas).Y - (DonutCanvas.ActualHeight / 2);
            double dist = Math.Sqrt(dx * dx + dy * dy);

            // 修复：修正判断半径边界
            if (dist < 35 || dist > 60)
            {
                if (DonutCanvas.ToolTip is System.Windows.Controls.ToolTip t) t.IsOpen = false;
                return;
            }

            double angle = Math.Atan2(dy, dx) * 180 / Math.PI;
            if (angle < 0) angle += 360;

            long total = topApps.Sum(x => x.Value);
            double currentAngle = 0;

            for (int i = 0; i < topApps.Count; i++)
            {
                double sweep = (topApps[i].Value / (double)total) * 360;
                if (angle >= currentAngle && angle <= currentAngle + sweep)
                {
                    string displayVal = _showAppByTime ? FormatActiveTime(topApps[i].Value) : FormatAdaptiveTotal(topApps[i].Value);
                    if (DonutCanvas.ToolTip is not System.Windows.Controls.ToolTip tt)
                    {
                        tt = new System.Windows.Controls.ToolTip { FontWeight = FontWeights.Bold, BorderThickness = new Thickness(1) };
                        tt.SetResourceReference(System.Windows.Controls.ToolTip.BackgroundProperty, "BgCardBrush");
                        tt.SetResourceReference(System.Windows.Controls.ToolTip.ForegroundProperty, "TextMainBrush");
                        tt.SetResourceReference(System.Windows.Controls.ToolTip.BorderBrushProperty, "BorderMainBrush");
                        tt.Placement = System.Windows.Controls.Primitives.PlacementMode.Mouse; // ★ 强制 Tooltip 跟随真实的鼠标指针，杜绝相对位移
                        tt.VerticalOffset = 15;
                        DonutCanvas.ToolTip = tt;
                    }
                    tt.Content = $"{topApps[i].Key}\n{(topApps[i].Value / (double)total):P1} ({displayVal})";
                    tt.IsOpen = true;

                    return;
                }
                currentAngle += sweep;
            }
        }

        private void DonutCanvas_MouseLeave(object sender, MouseEventArgs e)
        {
            if (DonutCanvas.ToolTip is ToolTip tt) tt.IsOpen = false;
        }
        private void BtnToggleProcess_Click(object sender, RoutedEventArgs e)
        {
            if (ProcessListRow.Height.Value == 0)
            {
                // 修复 3：不再使用 1.5* 挤压曲线图，而是给定固定高度并依赖外层 ScrollViewer 滚动
                ProcessListRow.Height = new GridLength(450);
                BtnToggleProcess.Content = "▲ 收起进程详情";
            }
            else
            {
                ProcessListRow.Height = new GridLength(0);
                BtnToggleProcess.Content = "▼ 展开进程详情";
            }
        }

        // ==========================================
        // 3. 辅助渲染计算 (视口偏移算法与悬浮同步)
        // ==========================================
        private double[] GetLastNElements(Queue<double> q, int targetN, double? curVal = null)
        {
            var list = q.ToList();
            if (curVal.HasValue) list.Add(curVal.Value);

            double[] res = new double[targetN];
            int copyCount = Math.Min(targetN, list.Count); int srcStart = list.Count - copyCount; int dstStart = targetN - copyCount;
            for (int i = 0; i < copyCount; i++) res[dstStart + i] = list[srcStart + i];
            return res;
        }

        // 根据用户任意的 _currentViewMode (秒) 自动匹配最佳的存储颗粒度和点数
        // 根据用户任意的 _currentViewMode (秒) 自动匹配最佳的存储颗粒度和点数
        private void SetupChartParams(out Queue<double> rD, out Queue<double> rU, out Queue<double> rC, out Queue<double> rR, out Queue<double> rB, out Queue<double> rG, out Queue<double> rDisk, out int pointCount, out double offsetRatio, out double tickSec, double elapsed, out double cD, out double cU, out double cC, out double cR, out double cB, out double cG, out double cDisk)
        {
            DateTime now = DateTime.Now;
            // 默认值为秒级
            rD = _secDown; rU = _secUp; rC = _cpuSec; rR = _ramSec; rB = _batSec; rG = _gpuSec; rDisk = _diskSec;
            cD = _targetDownSpeed; cU = _targetUpSpeed; cC = _currentCpu; cR = _currentRam; cB = _currentBattery; cG = _currentGpu; cDisk = _currentDisk;
            tickSec = 1;
            offsetRatio = Math.Max(0, Math.Min(1.0, (now - _lastSecTick).TotalSeconds));

            if (_currentViewMode <= 300) // 1 到 5 分钟
            {
                rB = _batSec; cB = _currentBattery;
                rD = _secDown; rU = _secUp; rC = _cpuSec; rR = _ramSec; rG = _gpuSec; rDisk = _diskSec;
                tickSec = 1;
                pointCount = Math.Min(302, (_currentViewMode / 1) + 2);
                offsetRatio = Math.Max(0, Math.Min(1.0, (now - _lastSecTick).TotalSeconds));
                cD = _targetDownSpeed; cU = _targetUpSpeed; cC = _currentCpu; cR = _currentRam; cG = _currentGpu; cDisk = _currentDisk;
            }
            else if (_currentViewMode <= 10800) // 半小时到 3 小时
            {
                rB = _batMin; cB = _batSec.Skip(Math.Max(0, _batSec.Count - _tickSec)).DefaultIfEmpty(100.0).Average();
                rD = _minDown; rU = _minUp; rC = _cpuMin; rR = _ramMin; rG = _gpuMin; rDisk = _diskMin;
                tickSec = 60;
                pointCount = Math.Min(182, (_currentViewMode / 60) + 2);
                offsetRatio = Math.Max(0, Math.Min(1.0, (now - _lastMinTick).TotalSeconds / 60.0));
                cD = _secDown.Skip(Math.Max(0, _secDown.Count - _tickSec)).DefaultIfEmpty(0).Average();
                cU = _secUp.Skip(Math.Max(0, _secUp.Count - _tickSec)).DefaultIfEmpty(0).Average();
                cC = _cpuSec.Skip(Math.Max(0, _cpuSec.Count - _tickSec)).DefaultIfEmpty(0).Average();
                cR = _ramSec.Skip(Math.Max(0, _ramSec.Count - _tickSec)).DefaultIfEmpty(0).Average();
                cG = _gpuSec.Skip(Math.Max(0, _gpuSec.Count - _tickSec)).DefaultIfEmpty(0).Average();
                cDisk = _diskSec.Skip(Math.Max(0, _diskSec.Count - _tickSec)).DefaultIfEmpty(0).Average();
            }
            else if (_currentViewMode <= 604800) // 24小时 到 7 天
            {
                rB = _batHour; cB = _batMin.Skip(Math.Max(0, _batMin.Count - _tickMin)).DefaultIfEmpty(100.0).Average();
                rD = _hourDown; rU = _hourUp; rC = _cpuHour; rR = _ramHour; rG = _gpuHour; rDisk = _diskHour;
                tickSec = 3600;
                pointCount = Math.Min(170, (_currentViewMode / 3600) + 2);
                offsetRatio = Math.Max(0, Math.Min(1.0, (now - _lastHourTick).TotalSeconds / 3600.0));
                cD = _minDown.Skip(Math.Max(0, _minDown.Count - _tickMin)).DefaultIfEmpty(0).Average();
                cU = _minUp.Skip(Math.Max(0, _minUp.Count - _tickMin)).DefaultIfEmpty(0).Average();
                cC = _cpuMin.Skip(Math.Max(0, _cpuMin.Count - _tickMin)).DefaultIfEmpty(0).Average();
                cR = _ramMin.Skip(Math.Max(0, _ramMin.Count - _tickMin)).DefaultIfEmpty(0).Average();
                cG = _gpuMin.Skip(Math.Max(0, _gpuMin.Count - _tickMin)).DefaultIfEmpty(0).Average();
                cDisk = _diskMin.Skip(Math.Max(0, _diskMin.Count - _tickMin)).DefaultIfEmpty(0).Average();
            }
            else // 30 天以上
            {
                rB = _batDay; cB = _batHour.Skip(Math.Max(0, _batHour.Count - _tickHour)).DefaultIfEmpty(100.0).Average();
                rD = _dayDown; rU = _dayUp; rC = _cpuDay; rR = _ramDay; rG = _gpuDay; rDisk = _diskDay;
                tickSec = 86400;
                pointCount = Math.Min(32, (_currentViewMode / 86400) + 2);
                offsetRatio = Math.Max(0, Math.Min(1.0, (now - _lastDayTick).TotalSeconds / 86400.0));
                cD = _hourDown.Skip(Math.Max(0, _hourDown.Count - _tickHour)).DefaultIfEmpty(0).Average();
                cU = _hourUp.Skip(Math.Max(0, _hourUp.Count - _tickHour)).DefaultIfEmpty(0).Average();
                cC = _cpuHour.Skip(Math.Max(0, _cpuHour.Count - _tickHour)).DefaultIfEmpty(0).Average();
                cR = _ramHour.Skip(Math.Max(0, _ramHour.Count - _tickHour)).DefaultIfEmpty(0).Average();
                cG = _gpuHour.Skip(Math.Max(0, _gpuHour.Count - _tickHour)).DefaultIfEmpty(0).Average();
                cDisk = _diskHour.Skip(Math.Max(0, _diskHour.Count - _tickHour)).DefaultIfEmpty(0).Average();
            }
            if (pointCount < 2) pointCount = 2; // 容错
        }

        private void MainCanvas_MouseMove(object sender, MouseEventArgs e)
        {
            // 补充拖拽的UI更新
            if (_isDraggingChart)
            {
                double currentX = e.GetPosition(MainCanvas).X;
                Canvas.SetLeft(ChartSelectionRect, Math.Min(_dragStartX, currentX));
                ChartSelectionRect.Width = Math.Abs(currentX - _dragStartX);
            }

            if (MainCanvas.ActualWidth <= 0 || _isChartPaused) return;
            SetupChartParams(out var qD, out var qU, out _, out _, out _, out _, out _, out int N,
                                     out double offsetRatio, out double tickSec, 0,
                                     out double cD, out double cU, out _, out _, out _, out _, out _); // 补充额外的占位符
            double visualOff = offsetRatio - 1.0;
            double wx = MainCanvas.ActualWidth, hx = MainCanvas.ActualHeight;
            double px = e.GetPosition(MainCanvas).X, stepX = wx / (N - 2);

            int i = Math.Max(0, Math.Min(N - 1, (int)Math.Round((N - 2) + visualOff - (wx - px) / stepX)));
            double[] dArr = GetLastNElements(qD, N, cD), uArr = GetLastNElements(qU, N, cU);
            double ptX = wx - (N - 2 - i + visualOff) * stepX;
            double downY = hx - (dArr[i] / _displayMaxScale * hx), upY = hx - (uArr[i] / _displayMaxScale * hx);

            MainHoverLine.Visibility = MainHoverDotDown.Visibility = MainHoverDotUp.Visibility = MainHoverPopup.Visibility = Visibility.Visible;
            Canvas.SetLeft(MainHoverLine, ptX); MainHoverLine.Y2 = hx;
            Canvas.SetLeft(MainHoverDotDown, ptX); Canvas.SetTop(MainHoverDotDown, downY);
            Canvas.SetLeft(MainHoverDotUp, ptX); Canvas.SetTop(MainHoverDotUp, upY);

            double pX = ptX + 15; if (pX + 140 > wx) pX = ptX - 155;
            double pY = Math.Max(0, Math.Min(downY, upY) - 60); if (pY > hx - 90) pY = hx - 90;
            Canvas.SetLeft(MainHoverPopup, pX); Canvas.SetTop(MainHoverPopup, pY);

            MainHoverDownText.Text = FormatAdaptiveRate(dArr[i], _isBitMode);
            MainHoverUpText.Text = FormatAdaptiveRate(uArr[i], _isBitMode);
            DateTime hoverTime = DateTime.Now.AddSeconds(-(N - 2 - i + offsetRatio) * tickSec);
            MainHoverTime.Text = hoverTime.ToString(tickSec <= 3600 ? "HH:mm:ss" : "MM-dd HH:mm");
        }

        private void ResCanvas_MouseMove(object sender, MouseEventArgs e)
        {
            if (ResCanvas.ActualWidth <= 0) return;
            double wx = ResCanvas.ActualWidth, hx = ResCanvas.ActualHeight, px = e.GetPosition(ResCanvas).X;
            double targetMinY = hx; // 统一变量名，避免冲突

            if (BtnBackToLive.Visibility == Visibility.Visible)
            {
                int slot = _histStartSlot + Math.Clamp((int)Math.Round((px / wx) * _histTotalSlots), 0, _histTotalSlots);
                int oldSlot = slot / 15;

                if (_savedData.DailyResourceHistory.TryGetValue(_currentHistoryDate, out var histData))
                {
                    ResourceSnap snap = null;
                    if (histData.TryGetValue(slot, out var s1)) snap = s1;
                    else if (histData.TryGetValue(oldSlot, out var s2)) snap = s2;

                    if (snap != null)
                    {
                        double ptX = ((slot - _histStartSlot) / (double)_histTotalSlots) * wx;
                        double cpuY = hx - (snap.cpu / 100.0 * hx);
                        double ramY = hx - (snap.ram / 100.0 * hx);

                        ResHoverDotBat.Visibility = Visibility.Hidden;
                        ResHoverBatText.Visibility = Visibility.Collapsed;
                        ResHoverDotGpu.Visibility = Visibility.Hidden;
                        ResHoverGpuText.Visibility = Visibility.Collapsed;
                        ResHoverDotDisk.Visibility = Visibility.Hidden;
                        ResHoverDiskText.Visibility = Visibility.Collapsed;

                        ResHoverLine.Visibility = ResHoverPopup.Visibility = Visibility.Visible;

                        ResHoverDotCpu.Visibility = _savedData.ShowCpu ? Visibility.Visible : Visibility.Hidden;
                        ResHoverCpuText.Visibility = _savedData.ShowCpu ? Visibility.Visible : Visibility.Collapsed;
                        if (_savedData.ShowCpu)
                        {
                            Canvas.SetLeft(ResHoverDotCpu, ptX); Canvas.SetTop(ResHoverDotCpu, cpuY);
                            ResHoverCpuText.Text = $"CPU: {snap.cpu:F1} %";
                            targetMinY = Math.Min(targetMinY, cpuY);
                        }

                        ResHoverDotRam.Visibility = _savedData.ShowRam ? Visibility.Visible : Visibility.Hidden;
                        ResHoverRamText.Visibility = _savedData.ShowRam ? Visibility.Visible : Visibility.Collapsed;
                        if (_savedData.ShowRam)
                        {
                            Canvas.SetLeft(ResHoverDotRam, ptX); Canvas.SetTop(ResHoverDotRam, ramY);
                            ResHoverRamText.Text = $"RAM: {snap.ram:F1} %";
                            targetMinY = Math.Min(targetMinY, ramY);
                        }

                        Canvas.SetLeft(ResHoverLine, ptX); ResHoverLine.Y2 = hx;
                        double popX = ptX + 15; if (popX + 120 > wx) popX = ptX - 135;
                        double popY = targetMinY - 40; if (popY < 10) popY = 10; if (popY > hx - 90) popY = hx - 90;
                        Canvas.SetLeft(ResHoverPopup, popX); Canvas.SetTop(ResHoverPopup, popY);

                        ResHoverTime.Text = $"{_currentHistoryDate} {(slot / 60):D2}:{(slot % 60):D2}";
                        return;
                    }
                }
                ResHoverLine.Visibility = ResHoverPopup.Visibility = Visibility.Hidden;
                return;
            }

            // 实时模式
            SetupChartParams(out _, out _, out var qC, out var qR, out var qB, out var qG, out var qDisk, out int N,
                                     out double offsetRatio, out double tickSec, 0,
                                     out _, out _, out double cC, out double cR, out double cB, out double cG, out double cDisk);

            double visualOff = offsetRatio - 1.0;
            double stepX = wx / (N - 2);
            int idx = Math.Max(0, Math.Min(N - 1, (int)Math.Round((N - 2) + visualOff - (wx - px) / stepX)));
            double[] cArr = GetLastNElements(qC, N, cC), rArr = GetLastNElements(qR, N, cR), bArr = GetLastNElements(qB, N, cB);
            double[] gArr = GetLastNElements(qG, N, cG), dArr = GetLastNElements(qDisk, N, cDisk);

            double livePtX = Math.Min(wx, wx - (N - 2 - idx + visualOff) * stepX);
            double liveCpuY = hx - (cArr[idx] / 100.0 * hx);
            double liveRamY = hx - (rArr[idx] / 100.0 * hx);
            double liveBatY = hx - (bArr[idx] / 100.0 * hx);
            double liveGpuY = hx - (gArr[idx] / 100.0 * hx);
            double liveDiskY = hx - (dArr[idx] / 100.0 * hx);

            ResHoverLine.Visibility = ResHoverPopup.Visibility = Visibility.Visible;
            Canvas.SetLeft(ResHoverLine, livePtX); ResHoverLine.Y2 = hx;

            if (_savedData.ShowCpu)
            {
                ResHoverDotCpu.Visibility = Visibility.Visible; ResHoverCpuText.Visibility = Visibility.Visible;
                Canvas.SetLeft(ResHoverDotCpu, livePtX); Canvas.SetTop(ResHoverDotCpu, liveCpuY); ResHoverCpuText.Text = $"CPU: {cArr[idx]:F1} %";
                targetMinY = Math.Min(targetMinY, liveCpuY);
            }
            else { ResHoverDotCpu.Visibility = Visibility.Hidden; ResHoverCpuText.Visibility = Visibility.Collapsed; }

            if (_savedData.ShowRam)
            {
                ResHoverDotRam.Visibility = Visibility.Visible; ResHoverRamText.Visibility = Visibility.Visible;
                Canvas.SetLeft(ResHoverDotRam, livePtX); Canvas.SetTop(ResHoverDotRam, liveRamY); ResHoverRamText.Text = $"RAM: {rArr[idx]:F1} %";
                targetMinY = Math.Min(targetMinY, liveRamY);
            }
            else { ResHoverDotRam.Visibility = Visibility.Hidden; ResHoverRamText.Visibility = Visibility.Collapsed; }

            if (_savedData.ShowGpu)
            {
                ResHoverDotGpu.Visibility = Visibility.Visible; ResHoverGpuText.Visibility = Visibility.Visible;
                Canvas.SetLeft(ResHoverDotGpu, livePtX); Canvas.SetTop(ResHoverDotGpu, liveGpuY); ResHoverGpuText.Text = $"GPU: {gArr[idx]:F1} %";
                targetMinY = Math.Min(targetMinY, liveGpuY);
            }
            else { ResHoverDotGpu.Visibility = Visibility.Hidden; ResHoverGpuText.Visibility = Visibility.Collapsed; }

            if (_savedData.ShowDisk)
            {
                ResHoverDotDisk.Visibility = Visibility.Visible; ResHoverDiskText.Visibility = Visibility.Visible;
                Canvas.SetLeft(ResHoverDotDisk, livePtX); Canvas.SetTop(ResHoverDotDisk, liveDiskY); ResHoverDiskText.Text = $"DISK: {dArr[idx]:F1} %";
                targetMinY = Math.Min(targetMinY, liveDiskY);
            }
            else { ResHoverDotDisk.Visibility = Visibility.Hidden; ResHoverDiskText.Visibility = Visibility.Collapsed; }

            if (_savedData.ShowBat)
            {
                ResHoverDotBat.Visibility = Visibility.Visible; ResHoverBatText.Visibility = Visibility.Visible;
                Canvas.SetLeft(ResHoverDotBat, livePtX); Canvas.SetTop(ResHoverDotBat, liveBatY); ResHoverBatText.Text = $"BAT: {bArr[idx]:F1} %" + (_isCharging ? " ⚡" : "");
                targetMinY = Math.Min(targetMinY, liveBatY);
            }
            else { ResHoverDotBat.Visibility = Visibility.Hidden; ResHoverBatText.Visibility = Visibility.Collapsed; }

            double pX = livePtX + 15; if (pX + 140 > wx) pX = livePtX - 155;
            double pY = targetMinY - 50; if (pY < 10) pY = 10; if (pY > hx - 90) pY = hx - 90;

            Canvas.SetLeft(ResHoverPopup, pX); Canvas.SetTop(ResHoverPopup, pY);

            DateTime hoverTime = DateTime.Now.AddSeconds(-(N - 2 - idx + offsetRatio) * tickSec);
            ResHoverTime.Text = hoverTime.ToString(tickSec <= 3600 ? "HH:mm:ss" : "MM-dd HH:mm");
        }
        private void MainCanvas_MouseLeave(object sender, MouseEventArgs e)
        {
            MainHoverLine.Visibility = MainHoverDotDown.Visibility = MainHoverDotUp.Visibility = MainHoverPopup.Visibility = Visibility.Hidden;
        }

        private void ResCanvas_MouseLeave(object sender, MouseEventArgs e)
        {
            ResHoverLine.Visibility = ResHoverPopup.Visibility = Visibility.Hidden;
            ResHoverDotCpu.Visibility = ResHoverDotRam.Visibility = ResHoverDotGpu.Visibility = ResHoverDotDisk.Visibility = ResHoverDotBat.Visibility = Visibility.Hidden;
        }


        private async void FetchNetworkAndResourceDataAsync(double elapsedSec)
        {
            await Task.Run(() => {
                // A. 网络流量采集

                GetNetworkTrafficDeltas(out long curRecvDelta, out long curSentDelta, out long vpnRecvDelta, out long vpnSentDelta);

                _targetDownSpeed = curRecvDelta / elapsedSec;
                _targetUpSpeed = curSentDelta / elapsedSec;

                // ★ 降噪处理 1：物理网卡极低流量（低于 500 字节）的系统 ARP 杂音直接静默
                if (_targetDownSpeed < 500 && curRecvDelta < 500) _targetDownSpeed = 0;
                if (_targetUpSpeed < 500 && curSentDelta < 500) _targetUpSpeed = 0;

                long vpnDelta = vpnRecvDelta + vpnSentDelta;

                // ★ 降噪处理 2：过滤 VPN 虚拟网卡未代理时的底层探测噪音，彻底解决“没开代理还有流量”的问题
                if (vpnDelta < 500) vpnDelta = 0;

                ulong curLanD = EtwNetworkTracker.GlobalLanDownloadBytes;
                ulong curLanU = EtwNetworkTracker.GlobalLanUploadBytes;

                if (_isLanInit)
                {
                    if (curLanD >= _lastLanDown) _targetLanDownSpeed = (curLanD - _lastLanDown) / elapsedSec;
                    if (curLanU >= _lastLanUp) _targetLanUpSpeed = (curLanU - _lastLanUp) / elapsedSec;
                }
                else
                {
                    _isLanInit = true; // 第一秒跑完后解锁拦截
                }
                _lastLanDown = curLanD;
                _lastLanUp = curLanU;


                string today = DateTime.Today.ToString("yyyy-MM-dd");
                lock (_saveDataLock)
                {
                    _savedData.DailyTraffic[today] = _savedData.DailyTraffic.GetValueOrDefault(today, 0) + (long)((_targetDownSpeed + _targetUpSpeed) * elapsedSec);
                    if (vpnDelta > 0) _savedData.DailyVpnTraffic[today] = _savedData.DailyVpnTraffic.GetValueOrDefault(today, 0) + vpnDelta;
                }

                // B. CPU 采集 (使用 GetSystemTimes)
                if (GetSystemTimes(out FILETIME idle, out FILETIME kernel, out FILETIME user))
                {
                    ulong uIdle = ((ulong)idle.dwHighDateTime << 32) | idle.dwLowDateTime;
                    ulong uKernel = ((ulong)kernel.dwHighDateTime << 32) | kernel.dwLowDateTime;
                    ulong uUser = ((ulong)user.dwHighDateTime << 32) | user.dwLowDateTime;
                    ulong total = uKernel + uUser;
                    if (_lastSysTotal > 0)
                    {
                        ulong diffTotal = total - _lastSysTotal;
                        ulong diffIdle = uIdle - _lastSysIdle;
                        if (diffTotal > 0) _currentCpu = 100.0 - (diffIdle * 100.0 / diffTotal);
                    }
                    _lastSysTotal = total; _lastSysIdle = uIdle;
                }

                // C. RAM 采集
                MEMORYSTATUSEX mem = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf(typeof(MEMORYSTATUSEX)) };
                if (GlobalMemoryStatusEx(ref mem))
                {
                    _currentRam = mem.dwMemoryLoad;
                }
                // D. 电池采集
                if (GetSystemPowerStatus(out SYSTEM_POWER_STATUS sps))
                {
                    // 如果没有电池（台式机），值为 255
                    if (sps.BatteryLifePercent != 255)
                    {
                        _currentBattery = sps.BatteryLifePercent;
                        _isCharging = sps.ACLineStatus == 1;
                    }
                }

                // E. GPU 和 Disk 采集
                _currentGpu = GetGpuUsage();
                try { _currentDisk = _diskCounter.NextValue(); } catch { _currentDisk = 0; }
            });
        }

        private void OnRender(object? sender, EventArgs e)
        {
            DateTime now = DateTime.Now; double elapsed = (now - _lastSecTick).TotalSeconds;
            if (elapsed >= 1.0)
            {
                int missed = (int)elapsed; _lastSecTick = _lastSecTick.AddSeconds(missed);
                for (int i = 0; i < missed; i++)
                {
                    _secDown.Enqueue(_targetDownSpeed); if (_secDown.Count > 305) _secDown.Dequeue();
                    _secUp.Enqueue(_targetUpSpeed); if (_secUp.Count > 305) _secUp.Dequeue();
                    _cpuSec.Enqueue(_currentCpu); if (_cpuSec.Count > 305) _cpuSec.Dequeue();
                    _ramSec.Enqueue(_currentRam); if (_ramSec.Count > 305) _ramSec.Dequeue();
                    _gpuSec.Enqueue(_currentGpu); if (_gpuSec.Count > 305) _gpuSec.Dequeue();
                    _diskSec.Enqueue(_currentDisk); if (_diskSec.Count > 305) _diskSec.Dequeue();

                    _secLanDown.Enqueue(_targetLanDownSpeed); if (_secLanDown.Count > 305) _secLanDown.Dequeue();
                    _secLanUp.Enqueue(_targetLanUpSpeed); if (_secLanUp.Count > 305) _secLanUp.Dequeue();
                    _batSec.Enqueue(_currentBattery); if (_batSec.Count > 305) _batSec.Dequeue();


                    _tickSec++;
                    if (_tickSec >= 60)
                    {
                        _tickSec = 0; _lastMinTick = now;
                        _minDown.Enqueue(_secDown.Skip(_secDown.Count - 60).Average()); if (_minDown.Count > 185) _minDown.Dequeue();
                        _minUp.Enqueue(_secUp.Skip(_secUp.Count - 60).Average()); if (_minUp.Count > 185) _minUp.Dequeue();

                        _cpuMin.Enqueue(_cpuSec.Skip(_cpuSec.Count - 60).Average()); if (_cpuMin.Count > 185) _cpuMin.Dequeue();
                        _ramMin.Enqueue(_ramSec.Skip(_ramSec.Count - 60).Average()); if (_ramMin.Count > 185) _ramMin.Dequeue();
                        _gpuMin.Enqueue(_gpuSec.Skip(_gpuSec.Count - 60).Average()); if (_gpuMin.Count > 185) _gpuMin.Dequeue();
                        _diskMin.Enqueue(_diskSec.Skip(_diskSec.Count - 60).Average()); if (_diskMin.Count > 185) _diskMin.Dequeue();
                        _minLanDown.Enqueue(_secLanDown.Skip(_secLanDown.Count - 60).Average()); if (_minLanDown.Count > 185) _minLanDown.Dequeue();
                        _minLanUp.Enqueue(_secLanUp.Skip(_secLanUp.Count - 60).Average()); if (_minLanUp.Count > 185) _minLanUp.Dequeue();
                        _batMin.Enqueue(_batSec.Skip(_batSec.Count - 60).Average()); if (_batMin.Count > 185) _batMin.Dequeue();

                        _tickMin++;
                        if (_tickMin >= 60)
                        {
                            _tickMin = 0; _lastHourTick = now;
                            _hourDown.Enqueue(_minDown.Skip(_minDown.Count - 60).Average()); if (_hourDown.Count > 175) _hourDown.Dequeue();
                            _hourUp.Enqueue(_minUp.Skip(_minUp.Count - 60).Average()); if (_hourUp.Count > 175) _hourUp.Dequeue();
                            _cpuHour.Enqueue(_cpuMin.Skip(_cpuMin.Count - 60).Average()); if (_cpuHour.Count > 175) _cpuHour.Dequeue();
                            _ramHour.Enqueue(_ramMin.Skip(_ramMin.Count - 60).Average()); if (_ramHour.Count > 175) _ramHour.Dequeue();
                            _gpuHour.Enqueue(_gpuMin.Skip(_gpuMin.Count - 60).Average()); if (_gpuHour.Count > 175) _gpuHour.Dequeue();
                            _diskHour.Enqueue(_diskMin.Skip(_diskMin.Count - 60).Average()); if (_diskHour.Count > 175) _diskHour.Dequeue();
                            _tickHour++;

                            _hourLanDown.Enqueue(_minLanDown.Skip(_minLanDown.Count - 60).Average()); if (_hourLanDown.Count > 175) _hourLanDown.Dequeue();
                            _hourLanUp.Enqueue(_minLanUp.Skip(_minLanUp.Count - 60).Average()); if (_hourLanUp.Count > 175) _hourLanUp.Dequeue();
                            _batHour.Enqueue(_batMin.Skip(_batMin.Count - 60).Average()); if (_batHour.Count > 175) _batHour.Dequeue();

                            if (_tickHour >= 24)
                            {
                                _tickHour = 0; _lastDayTick = now;
                                _dayDown.Enqueue(_hourDown.Skip(_hourDown.Count - 24).Average()); if (_dayDown.Count > 35) _dayDown.Dequeue();
                                _dayUp.Enqueue(_hourUp.Skip(_hourUp.Count - 24).Average()); if (_dayUp.Count > 35) _dayUp.Dequeue();
                                _cpuDay.Enqueue(_cpuHour.Skip(_cpuHour.Count - 24).Average()); if (_cpuDay.Count > 35) _cpuDay.Dequeue();
                                _ramDay.Enqueue(_ramHour.Skip(_ramHour.Count - 24).Average()); if (_ramDay.Count > 35) _ramDay.Dequeue();
                                _gpuDay.Enqueue(_gpuHour.Skip(_gpuHour.Count - 24).Average()); if (_gpuDay.Count > 35) _gpuDay.Dequeue();
                                _diskDay.Enqueue(_diskHour.Skip(_diskHour.Count - 24).Average()); if (_diskDay.Count > 35) _diskDay.Dequeue();
                                _dayLanDown.Enqueue(_hourLanDown.Skip(_hourLanDown.Count - 24).Average()); if (_dayLanDown.Count > 35) _dayLanDown.Dequeue();
                                _dayLanUp.Enqueue(_hourLanUp.Skip(_hourLanUp.Count - 24).Average()); if (_dayLanUp.Count > 35) _dayLanUp.Dequeue();
                                _batDay.Enqueue(_batHour.Skip(_batHour.Count - 24).Average()); if (_batDay.Count > 35) _batDay.Dequeue();

                            }
                        }
                    }
                }
                SaveDailyResourceSnapshot(_currentCpu, _currentRam);

                FetchNetworkAndResourceDataAsync(missed);
                // ▼ 添加一行防丢数据的自动保存：每10秒自动持久化一次 ▼
                if (_tickSec % 10 == 0) SaveData();
            }

            // 声明 cD, cU, cC, cR, cB, cG, cDisk
            SetupChartParams(out var rD, out var rU, out var rC, out var rR, out var rB, out var rG, out var rDisk,
                             out int N, out double off, out double tS,
                             (now - _lastSecTick).TotalSeconds,
                             out double cD, out double cU, out double cC, out double cR, out double cB, out double cG, out double cDisk);
            // 视觉偏移 -1.0，把正在变动的点推到屏幕右侧隐藏
            double visualOff = off - 1.0;
            // --- 驱动桌面悬浮小窗的画面渲染 ---
            if (_miniWindow != null && _miniWindow.Visibility == Visibility.Visible && _miniWindow.MiniCanvas.ActualWidth > 0)
            {
                var brushBgMain = (SolidColorBrush)this.Resources["BgMainBrush"];
                var brushDown = (SolidColorBrush)TxtDown.Foreground;
                var brushUp = (SolidColorBrush)TxtUp.Foreground;

                // 利用与主图表相同的平滑函数，但是传入小窗的微型分辨率 (Width/Height)
                var miniPtsD = GetSmoothPoints(rD, N, _miniWindow.MiniCanvas.ActualWidth, _miniWindow.MiniCanvas.ActualHeight, visualOff, _displayMaxScale, true, cD);
                var miniPtsU = GetSmoothPoints(rU, N, _miniWindow.MiniCanvas.ActualWidth, _miniWindow.MiniCanvas.ActualHeight, visualOff, _displayMaxScale, true, cU);

                _miniWindow.UpdateData(miniPtsD, miniPtsU, FormatAdaptiveRate(cD, _isBitMode), FormatAdaptiveRate(cU, _isBitMode), brushBgMain, brushDown, brushUp);
            }
            // -----------------------------------

            // 主面板的数字
            if (_activeTab == "Traffic")
            {
                TxtDown.Text = $"下载: {FormatAdaptiveRate(cD, _isBitMode)}";
                TxtUp.Text = $"上传: {FormatAdaptiveRate(cU, _isBitMode)}";
            }
            if (_activeTab == "Traffic" && MainCanvas.ActualWidth > 0 && N > 1)
            {
                if (!_isChartPaused) // ★ 如果用户鼠标悬停在标记上，不更新坐标，画面瞬间凝固！
                {

                    double mX = Math.Max(GetLastNElements(rD, N - 1, cD).Max(), GetLastNElements(rU, N - 1, cU).Max());
                    _displayMaxScale += (Math.Max(mX, 1024) * 1.2 - _displayMaxScale) * 0.1;

                    // ★ 传入 cD 和 cU，修复 24h 没数据的问题
                    
                    PolyDown.Points = GetSmoothPoints(rD, N, MainCanvas.ActualWidth, MainCanvas.ActualHeight, visualOff, _displayMaxScale, true, cD);
                    PolyUp.Points = GetSmoothPoints(rU, N, MainCanvas.ActualWidth, MainCanvas.ActualHeight, visualOff, _displayMaxScale, true, cU);

                    if (this.FindName("LabelMax") is TextBlock lbl) lbl.Text = FormatAdaptiveRate(_displayMaxScale, _isBitMode);
                    UpdateXAxisTimeLabels(MainCanvas.ActualWidth, MainCanvas.ActualHeight, visualOff, tS);
                    // 在流量波浪图上同步绘制并刷新事件追踪锚点！
                    UpdateChartEventMarkers(MainCanvas.ActualWidth, MainCanvas.ActualHeight, tS);

                    Queue<double> rLanD, rLanU; double cLanD, cLanU;

                    if (_currentViewMode <= 300) { rLanD = _secLanDown; rLanU = _secLanUp; cLanD = _targetLanDownSpeed; cLanU = _targetLanUpSpeed; }
                    else if (_currentViewMode <= 10800) { rLanD = _minLanDown; rLanU = _minLanUp; cLanD = _secLanDown.Skip(Math.Max(0, _secLanDown.Count - _tickSec)).DefaultIfEmpty(0).Average(); cLanU = _secLanUp.Skip(Math.Max(0, _secLanUp.Count - _tickSec)).DefaultIfEmpty(0).Average(); }
                    else if (_currentViewMode <= 604800) { rLanD = _hourLanDown; rLanU = _hourLanUp; cLanD = _minLanDown.Skip(Math.Max(0, _minLanDown.Count - _tickMin)).DefaultIfEmpty(0).Average(); cLanU = _minLanUp.Skip(Math.Max(0, _minLanUp.Count - _tickMin)).DefaultIfEmpty(0).Average(); }
                    else { rLanD = _dayLanDown; rLanU = _dayLanUp; cLanD = _hourLanDown.Skip(Math.Max(0, _hourLanDown.Count - _tickHour)).DefaultIfEmpty(0).Average(); cLanU = _hourLanUp.Skip(Math.Max(0, _hourLanUp.Count - _tickHour)).DefaultIfEmpty(0).Average(); }


                    double[] dArr = GetLastNElements(rD, N - 1, cD);
                    double[] uArr = GetLastNElements(rU, N - 1, cU);
                    double[] lanDArr = GetLastNElements(rLanD, N - 1, cLanD);
                    double[] lanUArr = GetLastNElements(rLanU, N - 1, cLanU);

                    // 计算视窗内的绝对总字节数
                    double winDownTotal = dArr.Sum() * tS;
                    double winUpTotal = uArr.Sum() * tS;
                    double winTotal = winDownTotal + winUpTotal;

                    TxtWinDownTotal.Text = FormatAdaptiveTotal(winDownTotal);
                    TxtWinUpTotal.Text = FormatAdaptiveTotal(winUpTotal);
                    TxtWinTotal.Text = FormatAdaptiveTotal(winTotal);
                    // LAN 字节数 (为了防止网卡计算误差溢出，限制它不超过总流量)
                    double winLanDownTotal = Math.Min(lanDArr.Sum() * tS, winDownTotal);
                    double winLanUpTotal = Math.Min(lanUArr.Sum() * tS, winUpTotal);
                    // WAN 就是剩余的部分
                    double winWanDownTotal = Math.Max(0, winDownTotal - winLanDownTotal);
                    double winWanUpTotal = Math.Max(0, winUpTotal - winLanUpTotal);

                    TxtWinDownTotal.Text = FormatAdaptiveTotal(winDownTotal);
                    TxtWinUpTotal.Text = FormatAdaptiveTotal(winUpTotal);
                    TxtWinTotal.Text = FormatAdaptiveTotal(winTotal);
                    TxtWinDownSpeed.Text = FormatAdaptiveRate(cD, _isBitMode);
                    TxtWinUpSpeed.Text = FormatAdaptiveRate(cU, _isBitMode);

                    if (winTotal > 0)
                    {
                        // 动态计算半圆弧度 (0 到 180度之间分配)
                        double downAngle = (winDownTotal / winTotal) * 180.0;
                        double upAngle = (winUpTotal / winTotal) * 180.0;

                        // 防断层：给个极小值保证有颜色显示
                        if (downAngle > 0 && downAngle < 2) downAngle = 2;
                        if (upAngle > 0 && upAngle < 2) upAngle = 2;
                        if (downAngle > 178) downAngle = 178;
                        if (upAngle > 178) upAngle = 178;

                        // 从左侧 (180度) 顺时针向上画
                        PathWinDown.Data = Geometry.Parse(GetArcData(70, 65, 50, 180, downAngle));
                        // 从右侧 (360度) 逆时针向上画 (参数为负代表逆时针)
                        PathWinUp.Data = Geometry.Parse(GetArcData(70, 65, 50, 360, -upAngle));

                        // 动态分配 WAN 和 LAN 的双色进度条
                        ColWanDown.Width = new GridLength(winWanDownTotal, GridUnitType.Star);
                        ColWanUp.Width = new GridLength(winWanUpTotal, GridUnitType.Star);
                        ColLanDown.Width = new GridLength(winLanDownTotal, GridUnitType.Star);
                        ColLanUp.Width = new GridLength(winLanUpTotal, GridUnitType.Star);
                    }
                    else
                    {
                        PathWinDown.Data = null; PathWinUp.Data = null;
                        ColWanDown.Width = new GridLength(0); ColWanUp.Width = new GridLength(0);
                        ColLanDown.Width = new GridLength(0); ColLanUp.Width = new GridLength(0);
                    }

                    // 更新槽位总量
                    TxtWanTotal.Text = FormatAdaptiveTotal(winWanDownTotal + winWanUpTotal);
                    TxtLanTotal.Text = FormatAdaptiveTotal(winLanDownTotal + winLanUpTotal);


                }

            }

            if (_activeTab == "Resources" && ResCanvas.ActualWidth > 0 && N > 1)
            {
                if (this.FindName("TxtCpu") is TextBlock tCpu) tCpu.Text = $"CPU: {cC:F1}%";
                if (this.FindName("TxtRam") is TextBlock tRam) tRam.Text = $"RAM: {cR:F1}%";
                if (this.FindName("TxtGpu") is TextBlock tGpu) tGpu.Text = $"GPU: {cG:F1}%";
                if (this.FindName("TxtDisk") is TextBlock tDisk) tDisk.Text = $"DISK: {cDisk:F1}%";
                // ★ 更新电池 UI，充电时带个小闪电
                if (this.FindName("TxtBattery") is TextBlock tBat) tBat.Text = $"BAT: {_currentBattery}%" + (_isCharging ? " ⚡" : "");

                // 根据用户的显示偏好动态控制曲线的显隐
                LineCpu.Visibility = _savedData.ShowCpu ? Visibility.Visible : Visibility.Collapsed;
                LineRam.Visibility = _savedData.ShowRam ? Visibility.Visible : Visibility.Collapsed;
                LineGpu.Visibility = _savedData.ShowGpu ? Visibility.Visible : Visibility.Collapsed;
                LineDisk.Visibility = _savedData.ShowDisk ? Visibility.Visible : Visibility.Collapsed;
                LineBattery.Visibility = _savedData.ShowBat ? Visibility.Visible : Visibility.Collapsed;

                if (_savedData.ShowCpu) LineCpu.Points = GetSmoothPoints(rC, N, ResCanvas.ActualWidth, ResCanvas.ActualHeight, visualOff, 100.0, false, cC);
                if (_savedData.ShowRam) LineRam.Points = GetSmoothPoints(rR, N, ResCanvas.ActualWidth, ResCanvas.ActualHeight, visualOff, 100.0, false, cR);
                if (_savedData.ShowGpu) LineGpu.Points = GetSmoothPoints(rG, N, ResCanvas.ActualWidth, ResCanvas.ActualHeight, visualOff, 100.0, false, cG);
                if (_savedData.ShowDisk) LineDisk.Points = GetSmoothPoints(rDisk, N, ResCanvas.ActualWidth, ResCanvas.ActualHeight, visualOff, 100.0, false, cDisk);
                if (_savedData.ShowBat) LineBattery.Points = GetSmoothPoints(rB, N, ResCanvas.ActualWidth, ResCanvas.ActualHeight, visualOff, 100.0, false, cB);


                UpdateResourceGridLines(ResCanvas.ActualWidth, ResCanvas.ActualHeight);
            }
            EarthRenderTick(); // ★ 驱动 3D 地球和覆盖层的渲染运算

        }

        private void UpdateResourceGridLines(double w, double h)
        {
            if (_resAxisElements.Count == 0)
            {
                int[] pArr = { 100, 50, 0 };
                foreach (int p in pArr)
                {
                    Line ln = new Line { Stroke = (Brush)this.Resources["GridLineBrush"], StrokeThickness = 1, StrokeDashArray = new DoubleCollection(new double[] { 4, 4 }), IsHitTestVisible = false };
                    TextBlock tb = new TextBlock { Text = $"{p}%", Foreground = Brushes.Gray, FontSize = 10, IsHitTestVisible = false };
                    ResCanvas.Children.Add(ln); ResCanvas.Children.Add(tb); Panel.SetZIndex(ln, 0); Panel.SetZIndex(tb, 0); _resAxisElements.Add(ln); _resAxisElements.Add(tb);
                }
            }
            for (int i = 0; i < 3; i++) { double y = i == 0 ? 10 : (i == 1 ? h / 2 : h - 10); if (y < 0) y = 0; if (_resAxisElements[i * 2] is Line ln) { ln.X1 = 0; ln.X2 = w; ln.Y1 = y; ln.Y2 = y; } if (_resAxisElements[i * 2 + 1] is TextBlock tb) { Canvas.SetLeft(tb, 5); Canvas.SetTop(tb, y - 15); } }
        }
        private void UpdateChartEventMarkers(double w, double h, double tS)
        {
            double windowSec = _currentViewMode;
            // 获取图表右边缘距离当前时间的滞后秒数 (即渲染延迟差)
            double chartRightEdgeAgo = tS;
            double chartLeftEdgeAgo = windowSec + tS;

            // 过滤出当前时间窗口内可见的事件
            var visibleLogs = _savedData.AppLogs.Where((AppLogEvent l) =>
            {
                double secondsAgo = (DateTime.Now - l.Timestamp).TotalSeconds;
                return secondsAgo >= chartRightEdgeAgo && secondsAgo <= chartLeftEdgeAgo;
            }).ToList();

            // 移除已经滚出屏幕外的标记点
            var toRemove = _chartEventMarkers.Keys.Except(visibleLogs).ToList();
            foreach (var log in toRemove)
            {
                MainCanvas.Children.Remove(_chartEventMarkers[log]);
                _chartEventMarkers.Remove(log);
            }

            // 绘制/更新还在屏幕内的标记点位置
            foreach (var log in visibleLogs)
            {
                double secondsAgo = (DateTime.Now - log.Timestamp).TotalSeconds;
                double secondsFromRightEdge = secondsAgo - chartRightEdgeAgo;
                double x = w - (secondsFromRightEdge / windowSec) * w;

                if (!_chartEventMarkers.TryGetValue(log, out UIElement marker))
                {
                    // 1. 将边框加粗
                    var border = new Border
                    {
                        Width = 16, 
                        Height = h,
                        Background = new SolidColorBrush(Color.FromArgb(40, log.ColorBrush.Color.R, log.ColorBrush.Color.G, log.ColorBrush.Color.B)),
                        BorderBrush = log.ColorBrush,
                        BorderThickness = new Thickness(2, 0, 2, 0), 
                        Cursor = Cursors.Hand,
                        VerticalAlignment = VerticalAlignment.Stretch
                    };

                    // 2. 补全鼠标事件
                    border.MouseEnter += (s, e) => {
                        border.Background = new SolidColorBrush(Color.FromArgb(120, log.ColorBrush.Color.R, log.ColorBrush.Color.G, log.ColorBrush.Color.B));
                        _isMarkerPaused = true; // ★ 独立控制锚点冻结
                        MainHoverLine.Visibility = MainHoverDotDown.Visibility = MainHoverDotUp.Visibility = MainHoverPopup.Visibility = Visibility.Hidden;
                    };
                    border.MouseLeave += (s, e) => {
                        border.Background = new SolidColorBrush(Color.FromArgb(40, log.ColorBrush.Color.R, log.ColorBrush.Color.G, log.ColorBrush.Color.B));
                        _isMarkerPaused = false; // ★ 解除锚点冻结，不影响拖拽的冻结状态
                    };
                    border.MouseLeftButtonUp += (s, e) => {
                        _isMarkerPaused = false;
                    };

                    // 悬浮变深色
                    border.MouseEnter += (s, e) => border.Background = new SolidColorBrush(Color.FromArgb(120, log.ColorBrush.Color.R, log.ColorBrush.Color.G, log.ColorBrush.Color.B));
                    border.MouseLeave += (s, e) => border.Background = new SolidColorBrush(Color.FromArgb(40, log.ColorBrush.Color.R, log.ColorBrush.Color.G, log.ColorBrush.Color.B));

                    // 顶端显示 ToolTip (带 Logo 的定制化气泡)
                    var tt = new ToolTip
                    {
                        Background = (Brush)this.Resources["BgCardBrush"],
                        Foreground = (Brush)this.Resources["TextMainBrush"],
                        BorderBrush = log.ColorBrush,
                        BorderThickness = new Thickness(1),
                        Placement = System.Windows.Controls.Primitives.PlacementMode.Top, // 强制置顶显示
                        VerticalOffset = -5
                    };

                    var sp = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(5) };
                    var iconTxt = new TextBlock { Text = log.IconText, FontSize = 26, Margin = new Thickness(0, 0, 12, 0), VerticalAlignment = VerticalAlignment.Center };
                    var textSp = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
                    textSp.Children.Add(new TextBlock { Text = log.TimeDisplay, Foreground = Brushes.Gray, FontSize = 12 });
                    textSp.Children.Add(new TextBlock { Text = log.Title, FontWeight = FontWeights.Bold, FontSize = 14, Margin = new Thickness(0, 2, 0, 2) });
                    textSp.Children.Add(new TextBlock { Text = log.Message, TextWrapping = TextWrapping.Wrap, MaxWidth = 250, Foreground = (Brush)this.Resources["TextDimBrush"] });

                    sp.Children.Add(iconTxt);
                    sp.Children.Add(textSp);
                    tt.Content = sp;

                    border.ToolTip = tt;
                    ToolTipService.SetInitialShowDelay(border, 0);

                    // 点击跳转日志页面
                    border.MouseLeftButtonUp += (s, e) => {
                        if (this.FindName("NavLog") is Button navLog) Nav_Click(navLog, null);
                        if (this.FindName("LogListBox") is ListBox logBox)
                        {
                            logBox.SelectedItem = log;
                            logBox.ScrollIntoView(log);
                        }
                    };

                    MainCanvas.Children.Add(border);
                    Panel.SetZIndex(border, 150);
                    _chartEventMarkers[log] = border;
                    marker = border;
                }

                // 更新 X 轴坐标 (纵向条贯穿整个图表，贴着顶部Y=0)
                Canvas.SetLeft(marker, x - 4);
                Canvas.SetTop(marker, 0);
            }
        }
        private void SaveDailyResourceSnapshot(double cpu, double ram)
        {
            string today = DateTime.Today.ToString("yyyy-MM-dd");
            if (!_savedData.DailyResourceAverages.ContainsKey(today))
                _savedData.DailyResourceAverages[today] = new ResourceSnap { cpu = cpu, ram = ram };

            // 1. 每日平均值计算
            var snap = _savedData.DailyResourceAverages[today];
            snap.cpu = snap.cpu * 0.9 + cpu * 0.1;
            snap.ram = snap.ram * 0.9 + ram * 0.1;

            // 2. 小时活跃度计算 (热力图)
            if (!_savedData.DailyHourlyActive.ContainsKey(today)) _savedData.DailyHourlyActive[today] = new Dictionary<int, long>();
            int hour = DateTime.Now.Hour;
            if (cpu > 5 || _targetDownSpeed > 1024)
                _savedData.DailyHourlyActive[today][hour] = _savedData.DailyHourlyActive[today].GetValueOrDefault(hour, 0) + 2;

            // ★ 修复 1 & 3: 24h 波动记录 (每0.25h / 15分钟一段 = 每天 96 段)
            if (!_savedData.DailyResourceHistory.ContainsKey(today)) _savedData.DailyResourceHistory[today] = new Dictionary<int, ResourceSnap>();
            int timeSlot = DateTime.Now.Hour * 60 + DateTime.Now.Minute;
            var hist = _savedData.DailyResourceHistory[today];

            if (!hist.ContainsKey(timeSlot))
                hist[timeSlot] = new ResourceSnap { cpu = cpu, ram = ram };
            else
            {
                hist[timeSlot].cpu = hist[timeSlot].cpu * 0.95 + cpu * 0.05;
                hist[timeSlot].ram = hist[timeSlot].ram * 0.95 + ram * 0.05;
            }
        }
        private void DrawResourceHistoryHeatmap()
        {
            ResourceHeatmapCanvas.Children.Clear();
            if (this.FindName("ResHeatmapYearText") is TextBlock yearText) yearText.Text = _selectedHeatmapYear.ToString();

            double rectSize = 12, spacing = 3;
            double offsetX = 35, offsetY = 25;

            DateTime firstDayOfYear = new DateTime(_selectedHeatmapYear, 1, 1);
            DateTime startDate = firstDayOfYear.AddDays(-(int)firstDayOfYear.DayOfWeek);


            // ★ 仅基于主活动窗口的时长计算最大值
            long maxActive = 1;
            foreach (var daily in _savedData.DailyPrimaryTime.Values)
            {
                long sum = daily.Values.Sum();
                if (sum > maxActive) maxActive = sum;
            }
            // 绘制左侧星期标签
            string[] weekDays = { "Sun", "Mon", "Tue", "Wed", "Thu", "Fri", "Sat" };
            for (int i = 0; i < 7; i++)
            {
                TextBlock tb = new TextBlock { Text = weekDays[i], Foreground = Brushes.Gray, FontSize = 10 };
                Canvas.SetLeft(tb, 5);
                Canvas.SetTop(tb, offsetY + i * (rectSize + spacing) - 2);
                ResourceHeatmapCanvas.Children.Add(tb);
            }

            int lastMonth = -1;
            for (int col = 0; col < 54; col++)
            {
                for (int row = 0; row < 7; row++)
                {
                    DateTime cellDate = startDate.AddDays(col * 7 + row);
                    if (cellDate.Year != _selectedHeatmapYear) continue;
                    if (cellDate.Month != lastMonth)
                    {
                        TextBlock tbMonth = new TextBlock { Text = cellDate.ToString("MMM"), Foreground = Brushes.Gray, FontSize = 10 };
                        Canvas.SetLeft(tbMonth, offsetX + col * (rectSize + spacing));
                        Canvas.SetTop(tbMonth, 5);
                        ResourceHeatmapCanvas.Children.Add(tbMonth);
                        lastMonth = cellDate.Month;
                    }
                    string dateStr = cellDate.ToString("yyyy-MM-dd");
                    long val = 0;
                    if (_savedData.DailyPrimaryTime.TryGetValue(dateStr, out var appsData))
                    {
                        val = appsData.Values.Sum();
                    }

                    byte intensity = 30;
                    if (val > 0) intensity = (byte)(50 + (val / (double)maxActive) * 205);
                    // 资源热力图使用橙/黄色基调以区分流量图的蓝色
                    Brush fillBrush;
                    if (val > 0)
                    {
                        fillBrush = new SolidColorBrush(Color.FromRgb((byte)intensity, (byte)(intensity * 0.65), 0));
                    }
                    else
                    {
                        fillBrush = (Brush)this.Resources["HeatmapEmptyBrush"]; // 2. 变量类型改为 Brush，解决 CS0266 转换报错
                    }
                    Rectangle r = new Rectangle { Width = rectSize, Height = rectSize, RadiusX = 2, RadiusY = 2, Fill = fillBrush };
                    SetQuickToolTip(r, $"{dateStr}\n前台使用时长: {FormatActiveTime(val)}\n点击查看该日各项详情");
                    // 绑定点击事件，进入历史视图
                    r.Cursor = Cursors.Hand;
                    Canvas.SetLeft(r, offsetX + col * (rectSize + spacing));
                    Canvas.SetTop(r, offsetY + row * (rectSize + spacing));
                    r.MouseLeftButtonUp += (s, e) => LoadHistoricalResourceView(dateStr);

                    ResourceHeatmapCanvas.Children.Add(r);
                }
            }
        }
        // 增加一个字段记录当前查看的历史日期，供鼠标悬浮读取
        private string _currentHistoryDate = "";
        private int _histStartSlot = 0;
        private int _histTotalSlots = 96;
        private Dictionary<string, long>? _histFilteredPri;
        private Dictionary<string, long>? _histFilteredSec;
        private Dictionary<string, long>? _histFilteredBg;

        private Dictionary<string, long> AggregateHourlyData(Dictionary<string, Dictionary<string, long>> hourlyDb, string dateStr, int startHour, int endHour, Dictionary<string, Dictionary<string, long>> fallbackDailyDb)
        {
            var result = new Dictionary<string, long>();
            bool hasAnyHourlyData = false;

            for (int h = startHour; h < endHour; h++)
            {
                if (hourlyDb.TryGetValue($"{dateStr}_{h:D2}", out var hourData))
                {
                    hasAnyHourlyData = true;
                    foreach (var kv in hourData) result[kv.Key] = result.GetValueOrDefault(kv.Key, 0) + kv.Value;
                }
            }
            // 兼容旧数据：如果那天完全没有小时级记录，回退显示全天数据
            if (!hasAnyHourlyData)
            {
                bool dayHasData = false;
                for (int h = 0; h < 24; h++) if (hourlyDb.ContainsKey($"{dateStr}_{h:D2}")) { dayHasData = true; break; }
                if (!dayHasData) return fallbackDailyDb.GetValueOrDefault(dateStr, new Dictionary<string, long>());
            }
            return result;
        }
        private Dictionary<string, long> CombineAppTimes(params Dictionary<string, long>[] dicts)
        {
            var res = new Dictionary<string, long>();
            foreach (var d in dicts) { foreach (var kv in d) res[kv.Key] = res.GetValueOrDefault(kv.Key, 0) + kv.Value; }
            return res;
        }


        private Dictionary<string, long> AggregateMinuteData(Dictionary<string, Dictionary<string, long>> db, string dateStr, TimeSpan start, TimeSpan end, Dictionary<string, Dictionary<string, long>> fallbackDailyDb)
        {
            var result = new Dictionary<string, long>();
            bool hasData = false;
            int startMin = (int)start.TotalMinutes;
            int endMin = (int)end.TotalMinutes;

            for (int m = startMin; m <= endMin; m++)
            {
                string mKey = $"{dateStr}_{m / 60:D2}{m % 60:D2}"; // 新版: 1430
                string oldHKey = $"{dateStr}_{m / 60:D2}";         // 旧版: 14

                if (db.TryGetValue(mKey, out var minData))
                {
                    hasData = true;
                    foreach (var kv in minData) result[kv.Key] = result.GetValueOrDefault(kv.Key, 0) + kv.Value;
                }
                // 兼容昨日遗留的按小时保存的数据
                else if (m % 60 == 0 && db.TryGetValue(oldHKey, out var hourData))
                {
                    hasData = true;
                    foreach (var kv in hourData) result[kv.Key] = result.GetValueOrDefault(kv.Key, 0) + kv.Value;
                }
            }
            if (!hasData) return fallbackDailyDb.GetValueOrDefault(dateStr, new Dictionary<string, long>());
            return result;
        }
        // ★ 统一协调过滤更新所有组件
        private void UpdateHistoricalViewsForTimeRange(string dateStr, TimeSpan start, TimeSpan end)
        {
            _histFilteredPri = AggregateMinuteData(_savedData.HourlyPrimaryTime, dateStr, start, end, _savedData.DailyPrimaryTime);
            _histFilteredSec = AggregateMinuteData(_savedData.HourlySecondaryTime, dateStr, start, end, _savedData.DailySecondaryTime);
            _histFilteredBg = AggregateMinuteData(_savedData.HourlyBackgroundTime, dateStr, start, end, _savedData.DailyBackgroundTime);
            var filteredTraffic = AggregateMinuteData(_savedData.HourlyAppTraffic, dateStr, start, end, _savedData.DailyAppTraffic);

            var donutData = _showAppByTime ? CombineAppTimes(_histFilteredPri, _histFilteredSec, _histFilteredBg) : filteredTraffic;
            DrawDonutChart(donutData);

            DrawStaticResourceLines(dateStr, start, end);

            // 柱状图暂保持小时级显示轮廓
            var historyHourly = _savedData.DailyHourlyActive.GetValueOrDefault(dateStr, new Dictionary<int, long>());
            DrawHourlyChart(historyHourly, start.Hours, end.Hours == 0 && end.Minutes == 0 ? 24 : end.Hours + 1);

            TabPrimary.Header = "主活动";
            TabSecondary.Visibility = Visibility.Visible;
            TabBackground.Visibility = Visibility.Visible;

            UpdateDataGrid(_histFilteredPri, _primaryWindowList, "主活动");
            UpdateDataGrid(_histFilteredSec, _secondaryWindowList, "次要活动");
            UpdateDataGrid(_histFilteredBg, _backgroundWindowList, "后台驻留");

            if (_isWindowDonutView) DrawWindowDonutChart();
        }
        // 精确到秒的筛选按钮事件
        private void BtnApplyTimeFilter_Click(object sender, RoutedEventArgs e)
        {
            if (TimeSpan.TryParse(TxtHistoryStart.Text, out TimeSpan start) && TimeSpan.TryParse(TxtHistoryEnd.Text, out TimeSpan end))
            {
                if (start >= end) { MessageBox.Show("起始时间必须早于结束时间！", "逻辑错误"); return; }
                if (!string.IsNullOrEmpty(_currentHistoryDate)) UpdateHistoricalViewsForTimeRange(_currentHistoryDate, start, end);
            }
            else { MessageBox.Show("时间格式输入错误！\n请使用格式：HH:mm:ss (例如 14:30:00)", "格式错误"); }
        }
        private void LoadHistoricalResourceView(string dateStr)
        {
            _currentHistoryDate = dateStr;
            if (BtnBackToLive != null) BtnBackToLive.Visibility = Visibility.Visible;
            if (HistoryTimePanel != null) HistoryTimePanel.Visibility = Visibility.Visible;

            if (ResourceHeatmapTitle != null)
            {
                ResourceHeatmapTitle.Text = $"📅 历史详情: {dateStr} (已暂停实时更新)";
                ResourceHeatmapTitle.Foreground = new SolidColorBrush(Color.FromRgb(0, 229, 255));
            }

            // 初始化重置为 00:00:00 ~ 23:59:59
            TxtHistoryStart.Text = "00:00:00";
            TxtHistoryEnd.Text = "23:59:59";
            UpdateHistoricalViewsForTimeRange(dateStr, TimeSpan.Zero, new TimeSpan(23, 59, 59));
        }
        // ★ 修复：修改方法签名以接收时间范围参数
        private void DrawStaticResourceLines(string dateStr, TimeSpan start, TimeSpan end)
        {
            if (ResCanvas == null) return;
            UIElement[] liveComponents = { LineCpu, LineRam, LineGpu, LineDisk, LineBattery, ResHoverLine, ResHoverDotCpu, ResHoverDotRam, ResHoverDotGpu, ResHoverDotDisk, ResHoverDotBat, ResHoverPopup }; foreach (var comp in liveComponents) { if (comp != null && ResCanvas.Children.Contains(comp)) ResCanvas.Children.Remove(comp); }
            ResCanvas.Children.Clear();

            double w = ResCanvas.ActualWidth > 0 ? ResCanvas.ActualWidth : 800;
            double h = ResCanvas.ActualHeight > 0 ? ResCanvas.ActualHeight : 150;
            _resAxisElements.Clear(); UpdateResourceGridLines(w, h);
            _histStartSlot = (int)start.TotalMinutes;
            int endSlot = (int)end.TotalMinutes;
            _histTotalSlots = endSlot - _histStartSlot;
            if (_histTotalSlots <= 0) return;

            double stepX = w / _histTotalSlots;
            double sumCpu = 0, sumRam = 0; int count = 0;

            if (_savedData.DailyResourceHistory.TryGetValue(dateStr, out var histData))
            {
                PointCollection ptsCpu = new PointCollection(); PointCollection ptsRam = new PointCollection();
                for (int i = _histStartSlot; i <= endSlot; i++)
                {
                    double cx = (i - _histStartSlot) * stepX;
                    if (histData.TryGetValue(i, out var s))
                    {
                        ptsCpu.Add(new Point(cx, Math.Clamp(h - (s.cpu / 100.0 * h), 0, h)));
                        ptsRam.Add(new Point(cx, Math.Clamp(h - (s.ram / 100.0 * h), 0, h)));
                        if (i < endSlot) { sumCpu += s.cpu; sumRam += s.ram; count++; } // 计算缩放区间内的真实平均值
                    }
                    else { ptsCpu.Add(new Point(cx, h)); ptsRam.Add(new Point(cx, h)); }
                }
                ResCanvas.Children.Add(new Polyline { Points = ptsCpu, Stroke = new SolidColorBrush((Color)ColorConverter.ConvertFromString(_savedData.ColorCpu)), StrokeThickness = 1.5, Opacity = 0.5 });
                ResCanvas.Children.Add(new Polyline { Points = ptsRam, Stroke = new SolidColorBrush((Color)ColorConverter.ConvertFromString(_savedData.ColorRam)), StrokeThickness = 1.5, Opacity = 0.5 });
            }

            double avgCpu = count > 0 ? sumCpu / count : 0;
            double avgRam = count > 0 ? sumRam / count : 0;
            // 画出 24 小时的波动平滑曲线
            // 画平均虚线和字
            double cpuY = h - (avgCpu / 100.0 * h);
            Line cpuLine = new Line { X1 = 0, X2 = w, Y1 = cpuY, Y2 = cpuY, Stroke = new SolidColorBrush((Color)ColorConverter.ConvertFromString(_savedData.ColorCpu)), StrokeThickness = 2, StrokeDashArray = new DoubleCollection(new double[] { 5, 5 }) };
            ResCanvas.Children.Add(cpuLine);

            double ramY = h - (avgRam / 100.0 * h);
            Line ramLine = new Line { X1 = 0, X2 = w, Y1 = ramY, Y2 = ramY, Stroke = new SolidColorBrush((Color)ColorConverter.ConvertFromString(_savedData.ColorRam)), StrokeThickness = 2, StrokeDashArray = new DoubleCollection(new double[] { 5, 5 }) };
            ResCanvas.Children.Add(ramLine);

            double cpuLabelY = cpuY > 20 ? cpuY - 20 : cpuY + 5;
            double ramLabelY = ramY > 20 ? ramY - 20 : ramY + 5;
            if (Math.Abs(cpuLabelY - ramLabelY) < 30)
            {
                if (cpuLabelY < ramLabelY) { cpuLabelY -= 15; ramLabelY += 15; }
                else { cpuLabelY += 15; ramLabelY -= 15; }
                cpuLabelY = Math.Clamp(cpuLabelY, 0, h - 20); ramLabelY = Math.Clamp(ramLabelY, 0, h - 20);
            }

            TextBlock lblCpu = new TextBlock { Text = $"区间平均 CPU: {avgCpu:F1}%", Foreground = cpuLine.Stroke, FontWeight = FontWeights.Bold, IsHitTestVisible = false };
            Canvas.SetLeft(lblCpu, 10); Canvas.SetTop(lblCpu, cpuLabelY);
            ResCanvas.Children.Add(lblCpu);

            TextBlock lblRam = new TextBlock { Text = $"区间平均 RAM: {avgRam:F1}%", Foreground = ramLine.Stroke, FontWeight = FontWeights.Bold, IsHitTestVisible = false };
            Canvas.SetLeft(lblRam, 10); Canvas.SetTop(lblRam, ramLabelY);
            ResCanvas.Children.Add(lblRam);

            ResCanvas.Children.Add(ResHoverLine); ResCanvas.Children.Add(ResHoverDotCpu);
            ResCanvas.Children.Add(ResHoverDotRam); ResCanvas.Children.Add(ResHoverPopup);
        }
        private void BtnBackToLive_Click(object sender, RoutedEventArgs e)
        {
            BtnBackToLive.Visibility = Visibility.Collapsed;
            ResourceHeatmapTitle.Text = "资源活跃历史 (Historical Contributions) - 点击砖块查看历史详情";
            ResourceHeatmapTitle.Foreground = Brushes.White;

            // 1. 彻底清理画布
            ResCanvas.Children.Clear();
            _resAxisElements.Clear();

            // 2. 重新初始化网格线
            UpdateResourceGridLines(ResCanvas.ActualWidth, ResCanvas.ActualHeight);

            // 3. 安全地添加实时组件 
            UIElement[] liveComponents = { LineCpu, LineRam, LineGpu, LineDisk, LineBattery, ResHoverLine, ResHoverDotCpu, ResHoverDotRam, ResHoverDotGpu, ResHoverDotDisk, ResHoverDotBat, ResHoverPopup };
            foreach (var comp in liveComponents)
            {
                if (comp == null) continue;

                // 如果该元素当前还属于某个 Canvas (或者上一次的视觉残留)
                DependencyObject parent = VisualTreeHelper.GetParent(comp);
                if (parent is Panel oldPanel)
                {
                    oldPanel.Children.Remove(comp);
                }

                // 现在可以安全添加了
                if (!ResCanvas.Children.Contains(comp))
                {
                    ResCanvas.Children.Add(comp);
                }
            }
            // ★ 恢复实时模式下的 Tab 状态
            TabPrimary.Header = "主活动";
            TabSecondary.Visibility = Visibility.Visible;
            TabBackground.Visibility = Visibility.Visible;
            if (HistoryTimePanel != null) HistoryTimePanel.Visibility = Visibility.Collapsed;
            // 4. 触发立即重绘
            DrawHourlyChart();
            DrawDonutChart();
            UpdateWindowActivityUI();
        }
        // 补充 XAML 中绑定的资源热力图年份切换按钮事件
        private void BtnResPrevYear_Click(object sender, RoutedEventArgs e)
        {
            _selectedHeatmapYear--;
            DrawResourceHistoryHeatmap();
        }

        private void BtnResNextYear_Click(object sender, RoutedEventArgs e)
        {
            if (_selectedHeatmapYear < DateTime.Today.Year)
            {
                _selectedHeatmapYear++;
                DrawResourceHistoryHeatmap();
            }
        }



        private PointCollection GetSmoothPoints(Queue<double> data, int N, double w, double h, double off, double sc, bool fill, double? curVal = null)
        {
            var pts = new PointCollection();
            if (fill) pts.Add(new Point(-200, h));

            // 将当前未走完的碎片数据传递给 GetLastNElements 补全到最后
            double[] arr = GetLastNElements(data, N, curVal);
            double stepX = w / (N - 2);
            var sPts = new Point[N];

            for (int i = 0; i < N; i++)
            {
                // 关键修复 1: 限制基础点 Y 坐标
                double calculatedY = h - (arr[i] / sc * h);
                sPts[i] = new Point(w - (N - 2 - i + off) * stepX, Math.Max(0, Math.Min(h, calculatedY)));
            }

            if (fill) pts.Add(new Point(-200, sPts[0].Y));
            pts.Add(sPts[0]);

            for (int i = 0; i < N - 1; i++)
            {
                Point p0 = i == 0 ? sPts[0] : sPts[i - 1];
                Point p1 = sPts[i];
                Point p2 = sPts[i + 1];
                Point p3 = i + 2 < N ? sPts[i + 2] : sPts[i + 1];

                double t = 0.15;
                Point c1 = new Point(p1.X + (p2.X - p0.X) * t, p1.Y + (p2.Y - p0.Y) * t);
                Point c2 = new Point(p2.X - (p3.X - p1.X) * t, p2.Y - (p3.Y - p1.Y) * t);

                // 关键修复 2: 限制贝塞尔控制点 Y 坐标
                c1.Y = Math.Clamp(c1.Y, 0, h);
                c2.Y = Math.Clamp(c2.Y, 0, h);

                // 找到 GetSmoothPoints 中计算控制点的那个 for (int j = 0; j <= 6; j++)，替换为：
                int steps = N > 100 ? 2 : 6; // ★ 核心优化：超过 100 个点时，减少贝塞尔平滑插值，降低 3 倍性能开销
                for (int j = 0; j <= steps; j++)
                {
                    double s = j / (double)steps, u = 1 - s;
                    double finalX = Math.Pow(u, 3) * p1.X + 3 * Math.Pow(u, 2) * s * c1.X + 3 * u * Math.Pow(s, 2) * c2.X + Math.Pow(s, 3) * p2.X;
                    double finalY = Math.Pow(u, 3) * p1.Y + 3 * Math.Pow(u, 2) * s * c1.Y + 3 * u * Math.Pow(s, 2) * c2.Y + Math.Pow(s, 3) * p2.Y;
                    pts.Add(new Point(finalX, Math.Clamp(finalY, 0, h)));
                }
            }
            if (fill) { pts.Add(new Point(w + 200, sPts[N - 1].Y)); pts.Add(new Point(w + 200, h)); }
            return pts;
        }
        private string GetArcData(double centerX, double centerY, double radius, double startAngle, double sweepAngle)
        {
            if (Math.Abs(sweepAngle) < 0.01) return "";
            double startRad = startAngle * Math.PI / 180.0;
            double endRad = (startAngle + sweepAngle) * Math.PI / 180.0;

            // 注意：WPF 的 Y 轴是向下生长的
            double startX = centerX + radius * Math.Cos(startRad);
            double startY = centerY + radius * Math.Sin(startRad);

            double endX = centerX + radius * Math.Cos(endRad);
            double endY = centerY + radius * Math.Sin(endRad);

            int isLargeArc = Math.Abs(sweepAngle) > 180 ? 1 : 0;
            int sweepDirection = sweepAngle > 0 ? 1 : 0; // 1 = 顺时针

            // 返回 Geometry.Parse 可读的 SVG 路径格式
            return string.Format(System.Globalization.CultureInfo.InvariantCulture,
                "M {0:F2},{1:F2} A {2:F2},{3:F2} 0 {4} {5} {6:F2},{7:F2}",
                startX, startY, radius, radius, isLargeArc, sweepDirection, endX, endY);
        }
        private void UpdateXAxisTimeLabels(double w, double h, double off, double tS)
        {
            if (_timeLabels.Count == 0)
            {
                for (int i = 0; i < 7; i++)
                {
                    var tb = new TextBlock { Foreground = new SolidColorBrush(Color.FromArgb(120, 255, 255, 255)), FontSize = 10, IsHitTestVisible = false };
                    Panel.SetZIndex(tb, 10); MainCanvas.Children.Add(tb); _timeLabels.Add(tb);
                    var ln = new Line { Stroke = (Brush)this.Resources["GridLineBrush"], StrokeThickness = 1, IsHitTestVisible = false };
                    MainCanvas.Children.Add(ln); _gridLines.Add(ln);
                }
            }

            double windowSec = _currentViewMode;
            double gapSec;
            if (windowSec <= 60) gapSec = 10;
            else if (windowSec <= 300) gapSec = 60;
            else if (windowSec <= 1800) gapSec = 300;
            else if (windowSec <= 10800) gapSec = 1800;
            else if (windowSec <= 86400) gapSec = 14400; // 24小时->每4小时一条线
            else gapSec = 86400 * 5;

            // ★ 核心修复：引入带毫秒精度的绝对时间
            DateTime now = DateTime.Now;
            double exactUnixTime = DateTimeOffset.Now.ToUnixTimeMilliseconds() / 1000.0;
            double localOffset = TimeZoneInfo.Local.GetUtcOffset(now).TotalSeconds;
            double localExactTime = exactUnixTime + localOffset;

            // 折线图的视觉右边缘(x=w)实际上为了平滑隐藏，滞后了刚好一个刻度(tS)
            double chartRightEdgeLocalUnix = localExactTime - tS;

            // 根据 gapSec 将时间规整，算出屏幕内第一根线的绝对时间戳
            long firstLineUnix = (long)(Math.Floor((chartRightEdgeLocalUnix - windowSec) / gapSec) * gapSec);

            int lIdx = 0;
            // 多循环一次(加gapSec)以确保左右边缘滑动时依然有线
            for (long t = firstLineUnix; t <= (long)(chartRightEdgeLocalUnix + gapSec); t += (long)gapSec)
            {
                if (lIdx >= _timeLabels.Count) break;

                // 算出该时间线距离“图表右边缘”差了多少秒
                double secondsFromRightEdge = chartRightEdgeLocalUnix - t;
                double x = w - (secondsFromRightEdge / windowSec) * w;

                if (x < -50 || x > w + 50)
                {
                    _timeLabels[lIdx].Visibility = _gridLines[lIdx].Visibility = Visibility.Hidden;
                }
                else
                {
                    _timeLabels[lIdx].Visibility = _gridLines[lIdx].Visibility = Visibility.Visible;

                    // 将 Unix 时间戳转回当前本地时区的时间用于显示文本
                    DateTime labelTime = DateTimeOffset.FromUnixTimeSeconds(t - (long)localOffset).LocalDateTime;
                    _timeLabels[lIdx].Text = windowSec <= 3600 ? labelTime.ToString("HH:mm:ss") : labelTime.ToString("MM-dd HH:mm");

                    _timeLabels[lIdx].Measure(new Size(999, 999));
                    Canvas.SetLeft(_timeLabels[lIdx], x - _timeLabels[lIdx].DesiredSize.Width / 2);
                    Canvas.SetBottom(_timeLabels[lIdx], 2);
                    _gridLines[lIdx].X1 = x; _gridLines[lIdx].X2 = x; _gridLines[lIdx].Y1 = 0; _gridLines[lIdx].Y2 = h;
                }
                lIdx++;
            }
            for (; lIdx < _timeLabels.Count; lIdx++) { _timeLabels[lIdx].Visibility = _gridLines[lIdx].Visibility = Visibility.Hidden; }
        }
        

        private string FormatActiveTime(long seconds)
        {
            if (seconds < 60) return $"{seconds} 秒";
            if (seconds < 3600) return $"{seconds / 60.0:F1} 分钟";
            return $"{seconds / 3600.0:F1} 小时";
        }

        private bool _isCurrentlyInactive = false;
        private async void ProcessTimer_Tick(object? sender, EventArgs e)
        {
            // ===== 插入闲置状态检测 =====
            bool isInactive = SystemMonitor.IsSystemInactive(60); // 1分钟不动即为不活跃
            if (isInactive != _isCurrentlyInactive)
            {
                _isCurrentlyInactive = isInactive;
                if (isInactive)
                    AddLogEvent("System", "系统进入不活跃状态", "检测到鼠标超1分钟无操作且无全屏程序，暂停活跃时长统计。", "#888888");
                else
                    AddLogEvent("System", "系统恢复活跃", "检测到用户恢复操作，继续活跃时长记录。", "#32CD32");
            }

            // 如果是不活跃状态，则不再执行下方原本的 TrackWindowActivity() 时间累加
            if (!isInactive)
            {
                TrackWindowActivity();
            }
            TrackWindowActivity(); // 执行窗口追踪逻辑
            TrackAppLifecycles(); // 执行应用生命周期(启动/关闭)追踪
            if (_isProcessingProcesses) return;
            _isProcessingProcesses = true;
            try
            {
                var uiDict = await Task.Run(() => {
                    var conns = GetAllTcpConnections();
                    var pAggs = new Dictionary<string, ProcessAggregateInfo>();
                    var pidIPs = new Dictionary<int, HashSet<string>>();

                    Action<int, string> addPidIp = (pid, ip) => {
                        if (!pidIPs.ContainsKey(pid)) pidIPs[pid] = new HashSet<string>();
                        if (ip != "0.0.0.0" && ip != "127.0.0.1")
                        {
                            pidIPs[pid].Add(ip);
                            // 实时维护 "连接对端 IP -> 真实应用进程 ID" 的身份账本
                            string cleanIp = ip.StartsWith("::ffff:", StringComparison.OrdinalIgnoreCase) ? ip.Substring(7) : ip;
                            EtwNetworkTracker.IpToPidMap[cleanIp] = pid;
                        }
                    };

                    // 1. TCP 监听
                    foreach (var c in conns)
                    {
                        if (c.RemoteAddress == null) continue;
                        addPidIp(c.ProcessId, c.RemoteAddress.ToString());
                    }

                    // 2. UDP 监听
                    int udpSize = 0;
                    GetExtendedUdpTable(IntPtr.Zero, ref udpSize, true, 2, 5, 0);
                    IntPtr udpPtr = Marshal.AllocHGlobal(udpSize);
                    try
                    {
                        if (GetExtendedUdpTable(udpPtr, ref udpSize, true, 2, 5, 0) == 0)
                        {
                            int cnt = Marshal.ReadInt32(udpPtr);
                            IntPtr rPtr = (IntPtr)((long)udpPtr + 4);
                            for (int i = 0; i < cnt; i++)
                            {
                                var r = Marshal.PtrToStructure<MIB_UDPROW_OWNER_PID>(rPtr);
                                if (!pidIPs.ContainsKey(r.owningPid)) pidIPs[r.owningPid] = new HashSet<string>();
                                rPtr = (IntPtr)((long)rPtr + Marshal.SizeOf<MIB_UDPROW_OWNER_PID>());
                            }
                        }
                    }
                    finally { Marshal.FreeHGlobal(udpPtr); }

                    // ★ 核心重构 1：不再仅仅依赖套接字，而是把 ETW 捕获过的所有进程都纳入检测池
                    var allTrackedPids = new HashSet<int>(EtwNetworkTracker.ProcessDownloadBytes.Keys);
                    allTrackedPids.UnionWith(EtwNetworkTracker.ProcessUploadBytes.Keys);
                    allTrackedPids.UnionWith(pidIPs.Keys);
                    allTrackedPids.UnionWith(_pidInitialIo.Keys);

                    var activePids = new HashSet<int>();
                    var deadPids = new List<int>();

                    // ★ 核心重构 2：真正通过系统进程状态来判断死活，而不是它有没有开着套接字
                    foreach (int pid in allTrackedPids)
                    {
                        if (pid == 0 || pid == 4) { activePids.Add(pid); continue; } // System / Idle
                        try
                        {
                            using var p = Process.GetProcessById(pid);
                            activePids.Add(pid);
                        }
                        catch (ArgumentException)
                        {
                            deadPids.Add(pid); // 报错说明进程确实已经被用户关闭死亡
                        }
                        catch
                        {
                            activePids.Add(pid); // 拒绝访问等无权限情况，默认它还活着
                        }
                    }

                    // 3. 计算所有活着进程的流量
                    foreach (int pid in activePids)
                    {
                        if (!_pidNameCache.TryGetValue(pid, out string? pName))
                        {
                            try { using var p = Process.GetProcessById(pid); pName = p.ProcessName; }
                            catch { pName = "System"; }
                            _pidNameCache[pid] = pName;
                        }
                        // 首次联网活动日志检测
                        if (pName != "System" && pName != "Idle" && !_savedData.KnownNetworkApps.Contains(pName))
                        {
                            _savedData.KnownNetworkApps.Add(pName);
                            string logMsg = $"{pName} 发起了它的首次网络连接。";
                            // 若能拿到具体的 IP 就更好了
                            if (pidIPs.TryGetValue(pid, out var ips) && ips.Count > 0)
                                logMsg = $"{pName} 首次连接到了外部网络，目标节点包含 {ips.First()} 等。";
                            AddLogEvent("App", "首次网络活动 (First network activity)", logMsg, "#32CD32"); // 采用 GlassWire 同款绿色
                        }
                        ulong cR = EtwNetworkTracker.ProcessDownloadBytes.GetValueOrDefault(pid, 0UL);
                        ulong cW = EtwNetworkTracker.ProcessUploadBytes.GetValueOrDefault(pid, 0UL);

                        if (!_pidInitialIo.ContainsKey(pid)) _pidInitialIo.TryAdd(pid, (cR, cW));
                        var init = _pidInitialIo[pid];

                        // 计算自进程追踪以来的纯增量 (加入防回滚容错)
                        ulong readDelta = cR >= init.Read ? cR - init.Read : cR;
                        ulong writeDelta = cW >= init.Write ? cW - init.Write : cW;

                        _pidCurrentDelta[pid] = (readDelta, writeDelta);

                        long currentPidTotal = (long)(readDelta + writeDelta);
                        long lastRec = _pidLastRecordedDaily.GetValueOrDefault(pid, 0);
                        long tickDelta = currentPidTotal - lastRec;

                        if (tickDelta > 0)
                        {
                            string dateKey = DateTime.Today.ToString("yyyy-MM-dd");
                            string hKey = $"{dateKey}_{DateTime.Now.Hour:D2}{DateTime.Now.Minute:D2}";

                            // ★ 智能识别常见代理软件内核
                            string lowerName = pName.ToLower();
                            bool isProxyApp = lowerName.Contains("clash") || lowerName.Contains("mihomo") || lowerName.Contains("v2ray") ||
                                              lowerName.Contains("sing-box") || lowerName.Contains("tailscale") || lowerName.Contains("wireguard") ||
                                              lowerName.Contains("netch") || lowerName.Contains("shadowsocks") || lowerName.Contains("nekoray") ||
                                              lowerName.Contains("qv2ray") || lowerName.Contains("xray");

                            lock (_saveDataLock)
                            {
                                if (!_savedData.DailyAppTraffic.ContainsKey(dateKey)) _savedData.DailyAppTraffic[dateKey] = new Dictionary<string, long>();
                                _savedData.DailyAppTraffic[dateKey][pName] = _savedData.DailyAppTraffic[dateKey].GetValueOrDefault(pName, 0) + tickDelta;

                                if (!_savedData.HourlyAppTraffic.ContainsKey(hKey)) _savedData.HourlyAppTraffic[hKey] = new Dictionary<string, long>();
                                _savedData.HourlyAppTraffic[hKey][pName] = _savedData.HourlyAppTraffic[hKey].GetValueOrDefault(pName, 0) + tickDelta;

                                // ★ 将代理进程自身跑出的流量，精确合并入全局 VPN 统计中！
                                if (isProxyApp)
                                {
                                    _savedData.DailyVpnTraffic[dateKey] = _savedData.DailyVpnTraffic.GetValueOrDefault(dateKey, 0) + tickDelta;
                                }
                            }
                            _pidLastRecordedDaily[pid] = currentPidTotal;
                        }
                        if (!pAggs.ContainsKey(pName)) pAggs[pName] = new ProcessAggregateInfo { ProcessName = pName };
                        pAggs[pName].SessionDownload += readDelta;
                        pAggs[pName].SessionUpload += writeDelta;
                        if (pidIPs.ContainsKey(pid))
                        {
                            foreach (var ip in pidIPs[pid]) pAggs[pName].ConnectedIPs.Add(ip);
                        }
                    }

                    var tempUI = new Dictionary<string, ProcessNetworkInfo>();
                    lock (_saveDataLock)
                    {
                        // 4. ★ 核心重构 3：结算死亡进程，并从 ETW 中彻底摘除，防止 ETW 字典体积无限膨胀导致内存泄漏
                        foreach (var dPid in deadPids)
                        {
                            if (_pidNameCache.TryGetValue(dPid, out string? name))
                            {
                                ulong finalR = EtwNetworkTracker.ProcessDownloadBytes.GetValueOrDefault(dPid, 0UL);
                                ulong finalW = EtwNetworkTracker.ProcessUploadBytes.GetValueOrDefault(dPid, 0UL);
                                var init = _pidInitialIo.GetValueOrDefault(dPid, (Read: 0UL, Write: 0UL));

                                ulong readDelta = finalR >= init.Read ? finalR - init.Read : finalR;
                                ulong writeDelta = finalW >= init.Write ? finalW - init.Write : finalW;

                                _savedData.ProcessDownloadTraffic[name] = _savedData.ProcessDownloadTraffic.GetValueOrDefault(name, 0UL) + readDelta;
                                _savedData.ProcessUploadTraffic[name] = _savedData.ProcessUploadTraffic.GetValueOrDefault(name, 0UL) + writeDelta;
                                _savedData.ProcessTotalTraffic[name] = (long)(_savedData.ProcessDownloadTraffic[name] + _savedData.ProcessUploadTraffic[name]);
                            }
                            _pidInitialIo.TryRemove(dPid, out _);
                            _pidCurrentDelta.TryRemove(dPid, out _);
                            _pidNameCache.TryRemove(dPid, out _);
                            _pidLastRecordedDaily.TryRemove(dPid, out _);
                            EtwNetworkTracker.ProcessDownloadBytes.TryRemove(dPid, out _);
                            EtwNetworkTracker.ProcessUploadBytes.TryRemove(dPid, out _);
                        }
                        // 5. 生成最终的界面绑定数据
                        HashSet<string> targetProcesses = new HashSet<string>(pAggs.Keys);
                        DateTime now = DateTime.Now;
                        Dictionary<string, long> windowTotals = new Dictionary<string, long>();

                        // ★ 修复 BUG 3：根据用户拉拽选择的窗口范围（或默认完整时间），精确提取流量
                        double windowSec = _currentViewMode;
                        DateTime selectionEnd = now.AddSeconds(-windowSec * _selectedTimeStartRatio);
                        DateTime selectionStart = now.AddSeconds(-windowSec * _selectedTimeEndRatio);

                        // ★ 根据用户选择的下拉时间窗口，动态提取时间切片内的历史流量
                        if (_currentViewMode <= 86400) // 24小时以内查分钟级日志
                        {
                            int mins = Math.Max(1, _currentViewMode / 60);
                            for (int i = 0; i <= mins; i++)
                            {
                                DateTime t = now.AddMinutes(-i);
                                string key = $"{t:yyyy-MM-dd}_{t.Hour:D2}{t.Minute:D2}";
                                if (_savedData.HourlyAppTraffic.TryGetValue(key, out var dict))
                                {
                                    foreach (var kvp in dict)
                                    {
                                        targetProcesses.Add(kvp.Key);
                                        windowTotals[kvp.Key] = windowTotals.GetValueOrDefault(kvp.Key, 0) + kvp.Value;
                                    }
                                }
                            }
                        }
                        else // 超过24小时查天级日志
                        {
                            int days = _currentViewMode / 86400;
                            for (int i = 0; i <= days; i++)
                            {
                                string key = now.AddDays(-i).ToString("yyyy-MM-dd");
                                if (_savedData.DailyAppTraffic.TryGetValue(key, out var dict))
                                {
                                    foreach (var kvp in dict)
                                    {
                                        targetProcesses.Add(kvp.Key);
                                        windowTotals[kvp.Key] = windowTotals.GetValueOrDefault(kvp.Key, 0) + kvp.Value;
                                    }
                                }
                            }
                        }

                        // 将动态计算出的窗口流量组合并输出
                        foreach (string name in targetProcesses)
                        {
                            long windowTotal = windowTotals.GetValueOrDefault(name, 0L);
                            bool isActive = pAggs.ContainsKey(name);

                            // 过滤掉在这个时间窗口内完全没流量、且当前也不活跃的幽灵进程
                            if (windowTotal == 0 && !isActive) continue;

                            ulong sessionDown = isActive ? pAggs[name].SessionDownload : 0;
                            ulong sessionUp = isActive ? pAggs[name].SessionUpload : 0;

                            // 提取历史总流量作为权重来估算 Up/Down 的比例分布
                            ulong allDown = _savedData.ProcessDownloadTraffic.GetValueOrDefault(name, 0UL) + sessionDown;
                            ulong allUp = _savedData.ProcessUploadTraffic.GetValueOrDefault(name, 0UL) + sessionUp;

                            ulong allSum = allDown + allUp;
                            long dispDown = 0, dispUp = 0;

                            if (allSum > 0)
                            {
                                dispDown = (long)(windowTotal * ((double)allDown / allSum));
                                dispUp = windowTotal - dispDown;
                            }

                            var info = new ProcessNetworkInfo
                            {
                                ProcessName = name,
                                DownloadDisplay = FormatAdaptiveTotal(dispDown),
                                UploadDisplay = FormatAdaptiveTotal(dispUp),
                                RawDownload = dispDown,
                                RawUpload = dispUp,
                                ConnectionCount = isActive ? pAggs[name].ConnectedIPs.Count : 0,
                                State = isActive && pAggs[name].ConnectedIPs.Count > 0 ? "正在通信" : (isActive ? "后台驻留" : "未活跃"),
                                RawTotal = windowTotal
                            };

                            if (isActive)
                            {
                                foreach (var ip in pAggs[name].ConnectedIPs) info.Connections.Add(new IPConnectionInfo { IP = ip });
                            }
                            tempUI[name] = info;
                        }

                        return tempUI;
                    }
                });

                // 6. 避免闪烁的平滑 UI 更新
                var toRem = _processTrafficList.Where(x => !uiDict.ContainsKey(x.ProcessName)).ToList();
                foreach (var r in toRem) _processTrafficList.Remove(r);

                foreach (var kv in uiDict.OrderByDescending(x => x.Value.RawTotal))
                {
                    var ex = _processTrafficList.FirstOrDefault(x => x.ProcessName == kv.Key);
                    if (ex != null)
                    {
                        ex.DownloadDisplay = kv.Value.DownloadDisplay;
                        ex.UploadDisplay = kv.Value.UploadDisplay;
                        ex.RawDownload = kv.Value.RawDownload; 
                        ex.RawUpload = kv.Value.RawUpload;     
                        ex.ConnectionCount = kv.Value.ConnectionCount;
                        ex.State = kv.Value.State;
                        ex.RawTotal = kv.Value.RawTotal;
                        var newIPs = kv.Value.Connections.Select(c => c.IP).ToList();
                        var oldIPs = ex.Connections.Select(c => c.IP).ToList();
                        foreach (var ip in oldIPs.Except(newIPs).ToList()) ex.Connections.Remove(ex.Connections.First(c => c.IP == ip));
                        foreach (var ip in newIPs.Except(oldIPs).ToList()) ex.Connections.Add(new IPConnectionInfo { IP = ip });
                    }
                    else _processTrafficList.Add(kv.Value);
                }

                if (_activeTab == "Resources" && BtnBackToLive.Visibility != Visibility.Visible)
                {
                    DrawDonutChart();
                }
            }
            catch { }
            finally { _isProcessingProcesses = false; }
        }
        // ★ 修复 1：严格遵守网络进制标准，bps 用 1000 除，B/s 用 1024 除
        private string FormatAdaptiveRate(double bytes, bool isBitMode)
        {
            if (isBitMode)
            {
                double val = bytes * 8.0;
                if (val < 1000) return $"{val:F0} bps";
                if (val < 1000000) return $"{val / 1000:F1} Kbps";
                if (val < 1000000000) return $"{val / 1000000:F2} Mbps";
                return $"{val / 1000000000:F2} Gbps";
            }
            else
            {
                if (bytes < 1024) return $"{bytes:F0} B/s";
                if (bytes < 1048576) return $"{bytes / 1024:F1} KB/s";
                if (bytes < 1073741824) return $"{bytes / 1048576:F2} MB/s";
                return $"{bytes / 1073741824:F2} GB/s";
            }
        }

        // ★ 修复 2：加入 B (字节) 的显示，优化极小流量的单位
        private string FormatAdaptiveTotal(double b) =>
            b < 1024 ? $"{b:F0} B" :
            b < 1048576 ? $"{(b / 1024):F1} KB" :
            b < 1073741824 ? $"{(b / 1048576):F2} MB" :
            $"{(b / 1073741824):F2} GB";

        private void BtnToggleUnit_Click(object? s, RoutedEventArgs e) => _isBitMode = !_isBitMode;

        private void TimeWindowCombo_SelectionChanged(object? s, SelectionChangedEventArgs e)
        {
            if (TimeWindowCombo?.SelectedItem is ComboBoxItem i && int.TryParse(i.Tag?.ToString(), out int m))
            {
                if (m == -1) // 选中了自定义
                {
                    if (CustomTimePanel != null) CustomTimePanel.Visibility = Visibility.Visible;
                }
                else
                {
                    if (CustomTimePanel != null) CustomTimePanel.Visibility = Visibility.Collapsed;
                    _currentViewMode = m;
                }
            }
        }

        private void BtnApplyCustomTime_Click(object sender, RoutedEventArgs e)
        {
            if (int.TryParse(TxtCustomTime.Text, out int customSec) && customSec >= 60)
            {
                _currentViewMode = Math.Min(customSec, 2592000); // 限制最大值为 30 天的秒数
                MessageBox.Show($"已应用自定义视图窗口：{customSec} 秒", "应用成功");
            }
            else
            {
                MessageBox.Show("请输入至少 60 秒的有效时间跨度！", "错误", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        // 废弃旧的单行 GetTotalBytes，替换为这个全新方法以识别 VPN
        // ★ 重构：基于网关的终极过滤方案，彻底消灭虚拟网卡的双重/多重统计
        private void GetNetworkTraffic(out long curRecv, out long curSent, out long vpnRecv, out long vpnSent)
        {
            curRecv = 0; curSent = 0; vpnRecv = 0; vpnSent = 0;

            var interfaces = NetworkInterface.GetAllNetworkInterfaces()
                .Where(n => n.OperationalStatus == OperationalStatus.Up && 
                            n.NetworkInterfaceType != NetworkInterfaceType.Loopback);

            foreach (var n in interfaces)
            {
                var stats = n.GetIPStatistics();
                var props = n.GetIPProperties();
                string desc = n.Description.ToLower();
                string name = n.Name.ToLower();

                // ★ 核心杀手锏：真正的外网物理网卡通常都配置了默认网关
                // 这能一击秒杀 99% 的 VMware、WSL、Hyper-V 内部虚拟网卡和 Npcap 嗅探网卡！
                bool hasGateway = props.GatewayAddresses.Count > 0;

                // 识别 VPN 和隧道
                bool isVpn = n.NetworkInterfaceType == NetworkInterfaceType.Ppp ||
                             n.NetworkInterfaceType == NetworkInterfaceType.Tunnel ||
                             desc.Contains("vpn") || desc.Contains("wireguard") ||
                             desc.Contains("tailscale") || desc.Contains("tap-windows") ||
                             desc.Contains("cisco anyconnect") || desc.Contains("clash") ||
                             desc.Contains("v2ray") || desc.Contains("netch");

                if (isVpn)
                {
                    vpnRecv += stats.BytesReceived;
                    vpnSent += stats.BytesSent;
                }
                // 必须拥有网关，且排除极个别强行注入网关的本地 Loopback 伪装
                else if (hasGateway && !desc.Contains("loopback") && !name.Contains("loopback") && !desc.Contains("npcap"))
                {
                    curRecv += stats.BytesReceived;
                    curSent += stats.BytesSent;
                }
            }
        }
        // ★ 重构：基于单网卡独立状态的终极增量方案，彻底消灭虚拟网卡启停导致的数据爆炸
        private void GetNetworkTrafficDeltas(out long deltaRecv, out long deltaSent, out long deltaVpnRecv, out long deltaVpnSent)
        {
            deltaRecv = 0; deltaSent = 0; deltaVpnRecv = 0; deltaVpnSent = 0;

            var interfaces = NetworkInterface.GetAllNetworkInterfaces()
                .Where(n => n.OperationalStatus == OperationalStatus.Up &&
                            n.NetworkInterfaceType != NetworkInterfaceType.Loopback);

            var currentIfaces = new HashSet<string>();

            foreach (var n in interfaces)
            {
                var stats = n.GetIPStatistics();
                var props = n.GetIPProperties();
                string desc = n.Description.ToLower();
                string name = n.Name.ToLower();
                string id = n.Id;
                currentIfaces.Add(id);

                bool hasGateway = props.GatewayAddresses.Count > 0;

                // ★ 修复：精准识别更多现代内核的虚拟网卡 (加入 wintun, mihomo, sing-box)
                bool isVpn = n.NetworkInterfaceType == NetworkInterfaceType.Ppp ||
                             n.NetworkInterfaceType == NetworkInterfaceType.Tunnel ||
                             desc.Contains("vpn") || desc.Contains("cisco anyconnect");

                long bytesRecv = stats.BytesReceived;
                long bytesSent = stats.BytesSent;

                long dR = 0;
                long dS = 0;

                // 核心修复：只计算单网卡自己和自己上一秒的差值
                if (_lastInterfaceStats.TryGetValue(id, out var lastStats))
                {
                    if (bytesRecv >= lastStats.Recv) dR = bytesRecv - lastStats.Recv;
                    if (bytesSent >= lastStats.Sent) dS = bytesSent - lastStats.Sent;
                }

                _lastInterfaceStats[id] = (bytesRecv, bytesSent); // 更新该网卡的最新快照

                if (isVpn)
                {
                    deltaVpnRecv += dR;
                    deltaVpnSent += dS;
                }
                else if (hasGateway && !desc.Contains("loopback") && !name.Contains("loopback") && !desc.Contains("npcap"))
                {
                    deltaRecv += dR;
                    deltaSent += dS;
                }
            }

            // 清理已经断开的网卡，防止网卡重新启用时用 0 减去旧值产生的异常
            var keysToRemove = _lastInterfaceStats.Keys.Where(k => !currentIfaces.Contains(k)).ToList();
            foreach (var k in keysToRemove) _lastInterfaceStats.Remove(k);
        }

        private List<TcpConnection> GetAllTcpConnections()
        {
            var res = new List<TcpConnection>(); int size = 0; GetExtendedTcpTable(IntPtr.Zero, ref size, true, 2, 5, 0); IntPtr ptr = Marshal.AllocHGlobal(size);
            try { if (GetExtendedTcpTable(ptr, ref size, true, 2, 5, 0) == 0) { int cnt = Marshal.ReadInt32(ptr); IntPtr rPtr = (IntPtr)((long)ptr + 4); for (int i = 0; i < cnt; i++) { var r = Marshal.PtrToStructure<MIB_TCPROW_OWNER_PID>(rPtr); res.Add(new TcpConnection { State = r.state, RemoteAddress = new IPAddress(r.remoteAddr), RemotePort = (ushort)((r.remotePort & 0xff) << 8 | (r.remotePort >> 8) & 0xff), ProcessId = r.owningPid }); rPtr = (IntPtr)((long)rPtr + Marshal.SizeOf<MIB_TCPROW_OWNER_PID>()); } } } finally { Marshal.FreeHGlobal(ptr); }
            return res;
        }
        // ==========================================
        // 4. 3D 地球与路由追踪引擎
        // ==========================================
        private static readonly HttpClient _httpClient = new HttpClient();
        private double _earthAngle = 0;
        private List<HopData> _currentHops = new List<HopData>();
        public class HopData
        {
            public int Index { get; set; }
            public string IP { get; set; }
            public Point3D OriginalPoint { get; set; }
            public string Location { get; set; }
            // 增加经纬度属性供 2D 地图使用
            public double Lat { get; set; }
            public double Lon { get; set; }
        }

        // ★ 修复：去掉了 async Task，改为普通的 void 方法
        private void InitEarth()
        {
            var mesh = new MeshGeometry3D();
            int slices = 64, stacks = 64; double radius = 1.0; // 严格固定半径为 1.0

            for (int stack = 0; stack <= stacks; stack++)
            {
                double phi = (double)stack / stacks * Math.PI;
                double y = radius * Math.Cos(phi);
                double scale = radius * Math.Sin(phi);

                for (int slice = 0; slice <= slices; slice++)
                {
                    double theta = (double)slice / slices * 2 * Math.PI;
                    mesh.Positions.Add(new Point3D(scale * Math.Sin(theta), y, scale * Math.Cos(theta)));
                    double u = (double)slice / slices;

                    double v = (double)stack / stacks;
                    mesh.TextureCoordinates.Add(new Point(u, v));
                }
            }
            for (int stack = 0; stack < stacks; stack++)
            {
                for (int slice = 0; slice < slices; slice++)
                {
                    int p1 = stack * (slices + 1) + slice, p2 = p1 + 1, p3 = (stack + 1) * (slices + 1) + slice, p4 = p3 + 1;
                    mesh.TriangleIndices.Add(p1); mesh.TriangleIndices.Add(p3); mesh.TriangleIndices.Add(p2);
                    mesh.TriangleIndices.Add(p3); mesh.TriangleIndices.Add(p4); mesh.TriangleIndices.Add(p2);
                }
            }

            // 从内部资源或本地文件读取
            try
            {
                var earthBitmap = new BitmapImage();
                earthBitmap.BeginInit();

                // ★ 优先使用用户导入的地图，否则使用默认资源
                if (!string.IsNullOrEmpty(_customMapPath) && File.Exists(_customMapPath))
                {
                    earthBitmap.UriSource = new Uri(_customMapPath, UriKind.Absolute);
                }
                else
                {
                    var resourceUri = new Uri("pack://application:,,,/earth_map.png");
                    var resourceStream = Application.GetResourceStream(resourceUri);
                    if (resourceStream != null) earthBitmap.StreamSource = resourceStream.Stream;
                }

                earthBitmap.CacheOption = BitmapCacheOption.OnLoad;
                // 获取用户定义的高清极限
                int userMaxRes = 8192;
                Application.Current.Dispatcher.Invoke(() => {
                    if (int.TryParse(TxtMaxRes.Text, out int val)) userMaxRes = val;
                });

                // 获取用户是否开启了高清模式
                bool isHighRes = false;
                Application.Current.Dispatcher.Invoke(() => {
                    isHighRes = ChkHighResMap != null && ChkHighResMap.IsChecked == true;
                    if (Map2DPatchContainer != null) Map2DPatchContainer.Children.Clear(); // 切换时清空旧补丁
                });
                if (!isHighRes)
                {
                    earthBitmap.DecodePixelWidth = 2048; // 默认降采样，防止低配电脑内存溢出
                }
                else
                {
                    // 使用用户输入的数值作为高清模式的解码宽度
                    earthBitmap.DecodePixelWidth = userMaxRes;

                    // 强制开启 3D 材质的高质量采样模式，打破 WPF 默认的模糊抗锯齿
                    RenderOptions.SetBitmapScalingMode(EarthModel, BitmapScalingMode.HighQuality);
                }
                earthBitmap.EndInit();
                earthBitmap.Freeze(); // 冻结对象，优化多线程性能

                Map2DBackground.Source = earthBitmap;
                var imgBrush = new ImageBrush(earthBitmap) { Opacity = SliderEarthOpacity?.Value ?? 0.9 };
                EarthModel.Content = new GeometryModel3D(mesh, new DiffuseMaterial(imgBrush));
                return;
            }
            catch { }
            // 兜底方案：如果忘记放图片，显示纯色
            EarthModel.Content = new GeometryModel3D(mesh, new DiffuseMaterial(new SolidColorBrush(Color.FromRgb(10, 40, 80))));
        }
        private void MapLodTimer_Tick(object? sender, EventArgs e)
        {
            _mapLodTimer.Stop();
            if (ChkHighResMap.IsChecked != true)
            {
                Map2DPatchContainer.Children.Clear();
                return;
            }

            try
            {
                string mapPath = "pack://application:,,,/earth_map.png";

                if (!string.IsNullOrEmpty(_customMapPath) && File.Exists(_customMapPath))
                    mapPath = _customMapPath;

                double cw = Map2DContainer.ActualWidth;
                double ch = Map2DContainer.ActualHeight;
                if (cw <= 0 || ch <= 0) return;

                double scale = Map2DScale.ScaleX;
                if (scale < 1.2) // 如果没怎么放大，不需要额外渲染
                {
                    Map2DPatchContainer.Children.Clear();
                    return;
                }

                // 1. 逆向推导当前屏幕在未缩放网格中的绝对坐标区域
                double tx = Map2DTranslate.X;
                double ty = Map2DTranslate.Y;
                double unscaledX = -tx / scale;
                double unscaledY = -ty / scale;
                double unscaledW = cw / scale;
                double unscaledH = ch / scale;

                double cx = Math.Max(0, unscaledX);
                double cy = Math.Max(0, unscaledY);
                double cRight = Math.Min(cw, unscaledX + unscaledW);
                double cBot = Math.Min(ch, unscaledY + unscaledH);

                if (cRight <= cx || cBot <= cy) return;

                // 2. 高效读取原始图片的真实分辨率
                Uri uri = mapPath.StartsWith("pack") ? new Uri(mapPath) : new Uri(mapPath, UriKind.Absolute);
                BitmapDecoder decoder = BitmapDecoder.Create(uri, BitmapCreateOptions.DelayCreation, BitmapCacheOption.None);
                BitmapFrame frame = decoder.Frames[0];
                int imgW = frame.PixelWidth;
                int imgH = frame.PixelHeight;

                // 3. 将网格区域映射到原图的像素系并裁剪
                int px = (int)(cx / cw * imgW);
                int py = (int)(cy / ch * imgH);
                int pw = (int)((cRight - cx) / cw * imgW);
                int ph = (int)((cBot - cy) / ch * imgH);

                px = Math.Max(0, px); py = Math.Max(0, py);
                pw = Math.Min(imgW - px, pw); ph = Math.Min(imgH - py, ph);

                if (pw <= 0 || ph <= 0 || pw > 16384 || ph > 16384) return; // 容错保护

                CroppedBitmap cropped = new CroppedBitmap(frame, new Int32Rect(px, py, pw, ph));
                cropped.Freeze(); // 冻结对象以优化性能

                // 4. 创建高清补丁图层，并精确贴附在当前视野位置
                Image patch = new Image
                {
                    Source = cropped,
                    Width = cRight - cx,
                    Height = cBot - cy,
                    Stretch = Stretch.Fill,
                    HorizontalAlignment = HorizontalAlignment.Left,
                    VerticalAlignment = VerticalAlignment.Top,
                    Margin = new Thickness(cx, cy, 0, 0),
                    IsHitTestVisible = false
                };

                Map2DPatchContainer.Children.Clear();
                Map2DPatchContainer.Children.Add(patch);
            }
            catch { }
        }
        private async void BtnTraceIP_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Tag is string ip)
            {
                // 初始化重置相机和2D画布状态
                _cameraX = 0; _cameraY = 0; _cameraDistance = 3.2; _earthManualRotX = 0;
                if (EarthCam != null) EarthCam.Position = new Point3D(_cameraX, _cameraY, _cameraDistance);
                if (this.FindName("Map2DScale") is ScaleTransform st) { st.ScaleX = 1; st.ScaleY = 1; }
                if (this.FindName("Map2DTranslate") is TranslateTransform tt) { tt.X = 0; tt.Y = 0; }
                // 切换视图

                ViewTraffic.Visibility = ViewSystem.Visibility = ViewResources.Visibility = ViewSettings.Visibility = Visibility.Collapsed;
                ViewEarth.Visibility = Visibility.Visible;
                if (EarthModel.Content == null) InitEarth();

                TraceListPanel.Children.Clear();
                _currentHops.Clear();
                EarthOverlayCanvas.Children.Clear();
                TraceListPanel.Children.Add(new TextBlock { Text = $"🚀 开始追踪目标: {ip}\n", Foreground = Brushes.Yellow });

                Ping ping = new Ping();
                byte[] buffer = new byte[32];

                // 本机作为起点
                _currentHops.Add(new HopData { Index = 0, IP = "Localhost", Location = "Your Device", OriginalPoint = LatLonToPoint3D(39.9, 116.4), Lat = 39.9, Lon = 116.4 });
                for (int ttl = 1; ttl <= 30; ttl++)
                {
                    try
                    {
                        PingOptions options = new PingOptions(ttl, true);
                        PingReply reply = await ping.SendPingAsync(ip, 1000, buffer, options);

                        if (reply.Status == IPStatus.TtlExpired || reply.Status == IPStatus.Success)
                        {
                            string hopIp = reply.Address.ToString();
                            var geo = await GetGeoInfo(hopIp);

                            string loc = string.IsNullOrEmpty(geo.City) ? "Unknown/Private" : $"{geo.City}, {geo.Country}";
                            TraceListPanel.Children.Add(new TextBlock { Text = $"[{ttl}] {hopIp} - {loc}", Foreground = Brushes.Cyan, Margin = new Thickness(0, 0, 0, 5) });

                            if (geo.Lat != 0 && geo.Lon != 0)
                            {
                                _currentHops.Add(new HopData
                                {
                                    Index = ttl,
                                    IP = hopIp,
                                    Location = loc,
                                    OriginalPoint = LatLonToPoint3D(geo.Lat, geo.Lon),
                                    Lat = geo.Lat,  // ★ 核心修复：把漏掉的 Lat 加进来！
                                    Lon = geo.Lon   // ★ 核心修复：把漏掉的 Lon 加进来！
                                });
                            }

                            if (reply.Status == IPStatus.Success)
                            {
                                TraceListPanel.Children.Add(new TextBlock { Text = $"\n✅ 追踪到达目标！", Foreground = Brushes.LimeGreen });
                                break;
                            }
                        }
                        else
                        {
                            TraceListPanel.Children.Add(new TextBlock { Text = $"[{ttl}] Request Timed Out", Foreground = Brushes.Gray, Margin = new Thickness(0, 0, 0, 5) });
                        }
                    }
                    catch { }
                }
            }
        }

        private void BtnCloseEarth_Click(object sender, RoutedEventArgs e)
        {
            ViewEarth.Visibility = Visibility.Collapsed;
            ViewTraffic.Visibility = Visibility.Visible; // 退回流量监控
            _activeTab = "Traffic";
        }

        private async Task<(double Lat, double Lon, string City, string Country)> GetGeoInfo(string ip)
        {
            try
            {
                // 使用免费地理位置 API (有速率限制，普通追踪够用)
                string json = await _httpClient.GetStringAsync($"http://ip-api.com/json/{ip}?fields=lat,lon,city,country,status");
                using JsonDocument doc = JsonDocument.Parse(json);
                var root = doc.RootElement;
                if (root.GetProperty("status").GetString() == "success")
                {
                    return (root.GetProperty("lat").GetDouble(), root.GetProperty("lon").GetDouble(), root.GetProperty("city").GetString(), root.GetProperty("country").GetString());
                }
            }
            catch { }
            return (0, 0, "", "");
        }

        private Point3D LatLonToPoint3D(double lat, double lon)
        {
            double phi = (90 - lat) * Math.PI / 180.0;
            double theta = (lon + 180) * Math.PI / 180.0;

            double radius = 1.0;
            return new Point3D(radius * Math.Sin(phi) * Math.Sin(theta), radius * Math.Cos(phi), radius * Math.Sin(phi) * Math.Cos(theta));
        }

        // 地球自转和投影计算
        private void EarthRenderTick()
        {
            if (ViewEarth.Visibility != Visibility.Visible) return;

            if (EarthModel.Content is GeometryModel3D geom && geom.Material is DiffuseMaterial mat && mat.Brush is ImageBrush br)
            {
                br.Opacity = SliderEarthOpacity?.Value ?? 0.9;
            }
            // 读取滑块中的自转速度，当用户不在拖拽 3D 地球时应用自转
            double speed = SliderRotationSpeed?.Value ?? 0.3;
            if (!_isEarthRotating)
            {
                _earthAngle += speed;
            }
            _flyingLineTime += 0.05;
            // 结合自转与用户的上下俯仰操作进行矩阵运算
            var transformGroup = new Transform3DGroup();
            transformGroup.Children.Add(new RotateTransform3D(new AxisAngleRotation3D(new Vector3D(0, 1, 0), _earthAngle)));
            transformGroup.Children.Add(new RotateTransform3D(new AxisAngleRotation3D(new Vector3D(1, 0, 0), _earthManualRotX)));
            EarthModel.Transform = transformGroup;


            EarthOverlayCanvas.Children.Clear();
            Map2DCanvas.Children.Clear();

            if (_currentHops.Count == 0 || EarthOverlayCanvas.ActualWidth == 0) return;
            // ★ 核心黑科技：获取真正的 3D 到 2D 投影转换矩阵
            GeneralTransform3DTo2D transform3DTo2D;
            try { transform3DTo2D = EarthModel.TransformToAncestor(EarthViewport); }
            catch { return; } // 视图尚未准备好时防崩

            // ★ 获取当前 2D 视图的缩放比例，计算反比例的物理尺寸，确保点和线不会在放大时变得巨大！
            double currentScale2D = 1.0;
            if (this.FindName("Map2DScale") is ScaleTransform st) currentScale2D = st.ScaleX;
            if (currentScale2D < 0.1) currentScale2D = 1;

            double dotSize2D = 6.0 / currentScale2D;
            double lineThick2D = 1.2 / currentScale2D;
            double pulseThick2D = 1.8 / currentScale2D;
            double fontSize2D = 10.0 / currentScale2D;

            Point? lastPos2d = null;
            double w2d = Map2DCanvas.ActualWidth, h2d = Map2DCanvas.ActualHeight;
            DoubleCollection dashArray = new DoubleCollection { 4, 3 };
            HopData lastHop = null;
            //  第 1 遍循环：按顺序严格绘制所有连线
            // ================== 第 1 遍循环：按顺序严格绘制所有连线 ==================
            foreach (var hop in _currentHops)
            {
                double x2d = (hop.Lon + 180.0) / 360.0 * w2d;
                double y2d = (90.0 - hop.Lat) / 180.0 * h2d;
                Point curPos2d = new Point(x2d, y2d);

                // 绘制 3D 飞线 (★应用了 Slerp 球面线性插值，绘制弧形测地线)
                if (lastHop != null)
                {
                    Point3D p1 = lastHop.OriginalPoint;
                    Point3D p2 = hop.OriginalPoint;

                    // 计算两个三维向量的点积求夹角
                    double dot = p1.X * p2.X + p1.Y * p2.Y + p1.Z * p2.Z;
                    dot = Math.Clamp(dot, -1.0, 1.0);
                    double theta = Math.Acos(dot);

                    // 根据跨度动态决定线段精度
                    int segments = Math.Max(2, (int)(theta * 30));
                    bool? prevIsFront = null;
                    PointCollection curBgPoints = new PointCollection();
                    bool curStateIsFront = false;

                    // 定义绘制分段曲线的局部函数
                    Action<PointCollection, bool> FlushLine = (pts, front) =>
                    {
                        if (pts.Count < 2) return;
                        Polyline bg = new Polyline { Points = pts, Stroke = Brushes.Cyan, StrokeThickness = 1.0, StrokeDashArray = dashArray, StrokeDashOffset = _flyingLineTime * 5.0 };
                        if (!front) bg.Opacity = 0.15;
                        EarthOverlayCanvas.Children.Add(bg);

                        if (front)
                        {
                            Polyline hd = new Polyline { Points = pts, Stroke = Brushes.White, StrokeThickness = 1.5, StrokeDashArray = new DoubleCollection { 1, 10 }, StrokeDashOffset = _flyingLineTime * 15.0 };
                            EarthOverlayCanvas.Children.Add(hd);
                        }
                    };

                    for (int i = 0; i <= segments; i++)
                    {
                        double t = (double)i / segments;
                        Point3D interp;
                        if (theta < 0.0001) interp = p1; // 原地不动
                        else
                        {
                            // 球面线性插值
                            double sinTheta = Math.Sin(theta);
                            double w1 = Math.Sin((1 - t) * theta) / sinTheta;
                            double w2 = Math.Sin(t * theta) / sinTheta;
                            interp = new Point3D(p1.X * w1 + p2.X * w2, p1.Y * w1 + p2.Y * w2, p1.Z * w1 + p2.Z * w2);
                        }

                        // ★ 增加一条抛物线高度差，让航线“飞出”地表更立体
                        double altitude = 1.0 + Math.Sin(t * Math.PI) * (theta * 0.15);
                        interp.X *= altitude; interp.Y *= altitude; interp.Z *= altitude;

                        Point curProj = transform3DTo2D.Transform(interp);
                        bool isFront = (transformGroup.Transform(interp)).Z > 0.05;

                        // 根据是否在地球正面，切断线条分段渲染
                        if (prevIsFront == null)
                        {
                            curStateIsFront = isFront;
                            curBgPoints.Add(curProj);
                        }
                        else
                        {
                            if (isFront == curStateIsFront) curBgPoints.Add(curProj);
                            else
                            {
                                curBgPoints.Add(curProj); // 加入交界点防止断层
                                FlushLine(curBgPoints, curStateIsFront);
                                curStateIsFront = isFront;
                                curBgPoints = new PointCollection { curProj };
                            }
                        }
                        prevIsFront = isFront;
                    }
                    if (curBgPoints.Count > 1) FlushLine(curBgPoints, curStateIsFront);

                    // 绘制 2D 飞线 (并智能处理跨越太平洋/国际日期变更线的折返)
                    if (lastPos2d.HasValue)
                    {
                        double x1 = lastPos2d.Value.X, y1 = lastPos2d.Value.Y;
                        double x2 = curPos2d.X, y2 = curPos2d.Y;
                        double dx = x2 - x1;

                        // ★ 核心修复：如果横向跨度超过地图宽度的一半，说明在绕地球背面走，应该分为左右两截来画！
                        if (Math.Abs(dx) > w2d / 2.0)
                        {
                            // 计算反向穿透出屏幕边界的假点坐标
                            double x2_alt = dx > 0 ? x2 - w2d : x2 + w2d;
                            double x1_alt = dx > 0 ? x1 + w2d : x1 - w2d;

                            // 线段 1 (从起点走向最近的边界)
                            Line bgLine2d_1 = new Line { X1 = x1, Y1 = y1, X2 = x2_alt, Y2 = y2, Stroke = Brushes.Cyan, StrokeThickness = lineThick2D, StrokeDashArray = dashArray, StrokeDashOffset = _flyingLineTime * 5.0 };
                            Line pulseLine2d_1 = new Line { X1 = x1, Y1 = y1, X2 = x2_alt, Y2 = y2, Stroke = Brushes.White, StrokeThickness = pulseThick2D, StrokeDashArray = new DoubleCollection { 1, 15 }, StrokeDashOffset = _flyingLineTime * 20.0 };
                            Map2DCanvas.Children.Add(bgLine2d_1); Map2DCanvas.Children.Add(pulseLine2d_1);

                            // 线段 2 (从另一端边界冒出来连向终点)
                            Line bgLine2d_2 = new Line { X1 = x1_alt, Y1 = y1, X2 = x2, Y2 = y2, Stroke = Brushes.Cyan, StrokeThickness = lineThick2D, StrokeDashArray = dashArray, StrokeDashOffset = _flyingLineTime * 5.0 };
                            Line pulseLine2d_2 = new Line { X1 = x1_alt, Y1 = y1, X2 = x2, Y2 = y2, Stroke = Brushes.White, StrokeThickness = pulseThick2D, StrokeDashArray = new DoubleCollection { 1, 15 }, StrokeDashOffset = _flyingLineTime * 20.0 };
                            Map2DCanvas.Children.Add(bgLine2d_2); Map2DCanvas.Children.Add(pulseLine2d_2);
                        }
                        else
                        {
                            // 正常连线
                            Line bgLine2d = new Line { X1 = x1, Y1 = y1, X2 = x2, Y2 = y2, Stroke = Brushes.Cyan, StrokeThickness = lineThick2D, StrokeDashArray = dashArray, StrokeDashOffset = _flyingLineTime * 5.0 };
                            Map2DCanvas.Children.Add(bgLine2d);
                            Line pulseLine2d = new Line { X1 = x1, Y1 = y1, X2 = x2, Y2 = y2, Stroke = Brushes.White, StrokeThickness = pulseThick2D, StrokeDashArray = new DoubleCollection { 1, 15 }, StrokeDashOffset = _flyingLineTime * 20.0 };
                            Map2DCanvas.Children.Add(pulseLine2d);
                        }
                    }
                }
                lastHop = hop;
                lastPos2d = curPos2d;
            }
            // ================== 第 2 遍循环：按地理坐标分组绘制点和文字，防止文字重叠 ==================
            // 确保距离在200公里以内的节点完全合并在一起，避免视觉拥挤
            var groupedHops = _currentHops.GroupBy(h => $"{Math.Round(h.Lat / 2.0) * 2.0},{Math.Round(h.Lon / 2.0) * 2.0}").ToList();
            foreach (var group in groupedHops)
            {
                var firstHop = group.First();
                string indices = string.Join(", ", group.Select(h => h.Index));

                // ★ 修复：合并该坐标下的所有名称，防止 Your Device 覆盖真实地址
                string locName = string.Join(" / ", group.Select(h => h.Location).Distinct());

                Point curPos3d = transform3DTo2D.Transform(firstHop.OriginalPoint);
                bool isFront = (transformGroup.Transform(firstHop.OriginalPoint)).Z > 0.15;

                double x2d = (firstHop.Lon + 180.0) / 360.0 * w2d;
                double y2d = (90.0 - firstHop.Lat) / 180.0 * h2d;
                Point curPos2d = new Point(x2d, y2d);

                // --- 绘制 3D 点与文字 ---
                Ellipse dot = new Ellipse { Width = 8, Height = 8, Fill = isFront ? Brushes.Yellow : Brushes.DarkGoldenrod };
                if (!isFront) dot.Opacity = 0.15;
                Canvas.SetLeft(dot, curPos3d.X - 4); Canvas.SetTop(dot, curPos3d.Y - 4);
                EarthOverlayCanvas.Children.Add(dot);

                if (isFront)
                {
                    TextBlock tb = new TextBlock { Text = $"{locName}\n[{indices}]", Foreground = Brushes.White, FontSize = 10, FontWeight = FontWeights.Bold, TextAlignment = TextAlignment.Center };
                    tb.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                    Canvas.SetLeft(tb, curPos3d.X - tb.DesiredSize.Width / 2); // 水平居中
                    Canvas.SetTop(tb, curPos3d.Y + 6); // 在点的正下方
                    EarthOverlayCanvas.Children.Add(tb);
                }

                // --- 绘制 2D 点与文字 (应用反比例恒定大小) ---
                Ellipse dot2 = new Ellipse { Width = dotSize2D, Height = dotSize2D, Fill = Brushes.Yellow };
                Canvas.SetLeft(dot2, curPos2d.X - dotSize2D / 2); Canvas.SetTop(dot2, curPos2d.Y - dotSize2D / 2);
                Map2DCanvas.Children.Add(dot2);

                TextBlock tb2d = new TextBlock { Text = $"{locName}\n[{indices}]", Foreground = Brushes.Yellow, FontSize = fontSize2D, FontWeight = FontWeights.Bold, TextAlignment = TextAlignment.Center };
                tb2d.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                Canvas.SetLeft(tb2d, curPos2d.X - tb2d.DesiredSize.Width / 2);
                Canvas.SetTop(tb2d, curPos2d.Y + dotSize2D / 2 + (2.0 / currentScale2D));
                Map2DCanvas.Children.Add(tb2d);
            }

        }


        [DllImport("iphlpapi.dll")] static extern uint GetExtendedTcpTable(IntPtr p, ref int s, bool b, int v, int c, uint r);
        [DllImport("iphlpapi.dll")] static extern uint GetExtendedUdpTable(IntPtr p, ref int s, bool b, int v, int c, uint r);
        [DllImport("kernel32.dll")] static extern bool GetProcessIoCounters(IntPtr h, out IO_COUNTERS c);
        [DllImport("kernel32.dll")] static extern bool GetSystemTimes(out FILETIME i, out FILETIME k, out FILETIME u);
        [StructLayout(LayoutKind.Sequential)]
        public struct SYSTEM_POWER_STATUS
        {
            public byte ACLineStatus;
            public byte BatteryFlag;
            public byte BatteryLifePercent;
            public byte SystemStatusFlag;
            public int BatteryLifeTime;
            public int BatteryFullLifeTime;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool GetSystemPowerStatus(out SYSTEM_POWER_STATUS sps);
        [DllImport("kernel32.dll")] static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX m);

        [DllImport("user32.dll")]
        static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        static extern bool IsWindowVisible(IntPtr hWnd);

        [DllImport("user32.dll")]
        static extern bool IsIconic(IntPtr hWnd);

        [DllImport("user32.dll")]
        static extern int GetWindowThreadProcessId(IntPtr hWnd, out int lpdwProcessId);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

        [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        static extern int GetWindowTextLength(IntPtr hWnd);

        delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

        [DllImport("user32.dll")]
        static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

        [DllImport("dwmapi.dll")]
        static extern int DwmGetWindowAttribute(IntPtr hwnd, int dwAttribute, out int pvAttribute, int cbAttribute);

        [DllImport("user32.dll")]
        static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

        [DllImport("user32.dll")]
        static extern IntPtr GetShellWindow();





        [StructLayout(LayoutKind.Sequential)]
        public struct RECT { public int Left, Top, Right, Bottom; }

        private void TrackWindowActivity()
        {
            IntPtr foregroundHWnd = GetForegroundWindow();
            IntPtr shellHWnd = GetShellWindow(); // 获取系统壳程序窗口
            HashSet<string> secondaryApps = new HashSet<string>();
            string primaryApp = "";

            string todayStr = DateTime.Today.ToString("yyyy-MM-dd");
            string timeKey = $"{todayStr}_{DateTime.Now.Hour:D2}{DateTime.Now.Minute:D2}";

            if (!_savedData.HourlyPrimaryTime.ContainsKey(timeKey)) _savedData.HourlyPrimaryTime[timeKey] = new Dictionary<string, long>();
            if (!_savedData.HourlySecondaryTime.ContainsKey(timeKey)) _savedData.HourlySecondaryTime[timeKey] = new Dictionary<string, long>();
            if (!_savedData.HourlyBackgroundTime.ContainsKey(timeKey)) _savedData.HourlyBackgroundTime[timeKey] = new Dictionary<string, long>();

            if (!_savedData.DailyAppActiveTime.ContainsKey(todayStr)) _savedData.DailyAppActiveTime[todayStr] = new Dictionary<string, long>();
            if (!_savedData.DailyPrimaryTime.ContainsKey(todayStr)) _savedData.DailyPrimaryTime[todayStr] = new Dictionary<string, long>();
            if (!_savedData.DailySecondaryTime.ContainsKey(todayStr)) _savedData.DailySecondaryTime[todayStr] = new Dictionary<string, long>();
            if (!_savedData.DailyBackgroundTime.ContainsKey(todayStr)) _savedData.DailyBackgroundTime[todayStr] = new Dictionary<string, long>();

            // 1. 获取主活动窗口
            if (foregroundHWnd != IntPtr.Zero)
            {
                GetWindowThreadProcessId(foregroundHWnd, out int fgPid);
                EtwNetworkTracker.CurrentForegroundPid = fgPid; // ★ 核心：实时同步前台焦点 PID 供文件引擎判断

                primaryApp = GetProcessNameFromHWnd(foregroundHWnd);
                if (!string.IsNullOrEmpty(primaryApp))
                {
                    _savedData.PrimaryWindowTimes[primaryApp] = _savedData.PrimaryWindowTimes.GetValueOrDefault(primaryApp, 0) + 2;
                    // 将主活动窗口记入当日时间字典中（为热力图弹窗提供数据）
                    _savedData.DailyPrimaryTime[todayStr][primaryApp] = _savedData.DailyPrimaryTime[todayStr].GetValueOrDefault(primaryApp, 0) + 2;
                    // 记录小时级数据
                    _savedData.HourlyPrimaryTime[timeKey][primaryApp] = _savedData.HourlyPrimaryTime[timeKey].GetValueOrDefault(primaryApp, 0) + 2;
                    // 同步累加到总活跃时间字典
                    _savedData.DailyAppActiveTime[todayStr][primaryApp] = _savedData.DailyAppActiveTime[todayStr].GetValueOrDefault(primaryApp, 0) + 2;
                }
            }

            // 2. 次要窗口判定： Cloaked(隐形) 检查
            EnumWindows((hWnd, lParam) =>
            {
                DwmGetWindowAttribute(hWnd, 14, out int isCloaked, 4);

                GetWindowRect(hWnd, out RECT rect);
                bool hasSize = (rect.Right - rect.Left > 0) && (rect.Bottom - rect.Top > 0);

                if (hWnd != foregroundHWnd && hWnd != shellHWnd &&
                    IsWindowVisible(hWnd) && !IsIconic(hWnd) &&
                    GetWindowTextLength(hWnd) > 0 && isCloaked == 0 && hasSize)
                {
                    string pName = GetProcessNameFromHWnd(hWnd);
                    if (!string.IsNullOrEmpty(pName) && pName != primaryApp)
                    {
                        secondaryApps.Add(pName);
                    }
                }
                return true;
            }, IntPtr.Zero);

            foreach (var app in secondaryApps)
            {
                _savedData.SecondaryWindowTimes[app] = _savedData.SecondaryWindowTimes.GetValueOrDefault(app, 0) + 2;
                _savedData.DailySecondaryTime[todayStr][app] = _savedData.DailySecondaryTime[todayStr].GetValueOrDefault(app, 0) + 2;
                _savedData.DailyAppActiveTime[todayStr][app] = _savedData.DailyAppActiveTime[todayStr].GetValueOrDefault(app, 0) + 2;
            }

            // 3. 记录后台活动 (Background)
            var allProcesses = Process.GetProcesses();
            HashSet<string> uniqueBgApps = new HashSet<string>();

            foreach (var p in allProcesses)
            {
                try
                {
                    string pName = p.ProcessName;
                    string[] systemExcludes = { "svchost", "RuntimeBroker", "dllhost", "conhost", "SearchHost", "SystemSettings", "sihost", "taskhostw", "explorer", "ApplicationFrameHost" };

                    if (pName != primaryApp &&
                        !secondaryApps.Contains(pName) &&
                        !systemExcludes.Contains(pName) &&
                        pName != "Idle" && pName != "System")
                    {
                        uniqueBgApps.Add(pName);
                    }
                }
                catch { }
                finally { p.Dispose(); }
            }

            foreach (var app in uniqueBgApps)
            {
                _savedData.BackgroundProcessTimes[app] = _savedData.BackgroundProcessTimes.GetValueOrDefault(app, 0) + 2;
                _savedData.DailyBackgroundTime[todayStr][app] = _savedData.DailyBackgroundTime[todayStr].GetValueOrDefault(app, 0) + 2;
                _savedData.DailyAppActiveTime[todayStr][app] = _savedData.DailyAppActiveTime[todayStr].GetValueOrDefault(app, 0) + 2;
            }

            UpdateWindowActivityUI();
        }

        // 辅助方法：通过句柄获取进程名
        private string GetProcessNameFromHWnd(IntPtr hWnd)
        {
            try
            {
                GetWindowThreadProcessId(hWnd, out int pid);
                if (!_pidNameCache.TryGetValue(pid, out string? name))
                {
                    using var p = Process.GetProcessById(pid);
                    name = p.ProcessName;
                    _pidNameCache[pid] = name;
                }
                return name;
            }
            catch { return ""; }
        }

        private void UpdateWindowActivityUI()
        {
            // 如果用户没在看资源分配面板，跳过 UI 刷新
            if (_activeTab != "Resources") return;

            // ★ 修复 1：如果处于历史模式，禁止定时器用实时数据覆盖历史视图
            if (BtnBackToLive.Visibility == Visibility.Visible) return;

            UpdateDataGrid(_savedData.PrimaryWindowTimes, _primaryWindowList);
            UpdateDataGrid(_savedData.SecondaryWindowTimes, _secondaryWindowList);
            UpdateDataGrid(_savedData.BackgroundProcessTimes, _backgroundWindowList);

            if (_isWindowDonutView) DrawWindowDonutChart();
        }

        private void UpdateDataGrid(Dictionary<string, long> sourceData, ObservableCollection<WindowActivityInfo> targetList, string emptyName = "")
        {
            var sortedData = sourceData.OrderByDescending(x => x.Value).Take(50).ToList();

            if (sortedData.Count == 0 && !string.IsNullOrEmpty(emptyName))
            {
                targetList.Clear();
                targetList.Add(new WindowActivityInfo { Name = $"该时段无{emptyName}", TimeDisplay = "-", RawSeconds = 0 });
                return;
            }



            var toRemove = targetList.Where(x => !sortedData.Any(s => s.Key == x.Name)).ToList();
            foreach (var item in toRemove) targetList.Remove(item);
            foreach (var kvp in sortedData)
            {
                var existing = targetList.FirstOrDefault(x => x.Name == kvp.Key);
                if (existing != null) { existing.IsUpdating = kvp.Value > existing.RawSeconds; existing.TimeDisplay = FormatActiveTime(kvp.Value); existing.RawSeconds = kvp.Value; }
                else { targetList.Add(new WindowActivityInfo { Name = kvp.Key, TimeDisplay = FormatActiveTime(kvp.Value), RawSeconds = kvp.Value, IsUpdating = true }); }
            }

            for (int i = 0; i < targetList.Count - 1; i++)
            {
                for (int j = 0; j < targetList.Count - i - 1; j++)
                {
                    if (targetList[j].RawSeconds < targetList[j + 1].RawSeconds)
                    {
                        targetList.Move(j, j + 1);
                    }
                }
            }
        }
        // ★ v0.5：将保存的数据同步到设置面板 UI
        private void SyncThemeUI()
        {
            EditColorDown.Text = _savedData.ColorDown;
            EditColorUp.Text = _savedData.ColorUp;
            EditColorCpu.Text = _savedData.ColorCpu;
            EditColorRam.Text = _savedData.ColorRam;
            ChkShowCpu.IsChecked = _savedData.ShowCpu;
            ChkShowRam.IsChecked = _savedData.ShowRam;
            ChkShowBat.IsChecked = _savedData.ShowBat;
            ChkShowGpu.IsChecked = _savedData.ShowGpu;
            ChkShowDisk.IsChecked = _savedData.ShowDisk;
            // 同步
            if (EditColorBgMain != null) EditColorBgMain.Text = _savedData.ColorBgMain;
            if (EditColorBgCard != null) EditColorBgCard.Text = _savedData.ColorBgCard;
            SliderFontSize.Value = _savedData.GlobalFontSize;

            // 初始化预览色块颜色
            try
            {
                if (PreviewBoxDown != null) PreviewBoxDown.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString(_savedData.ColorDown));
                if (PreviewBoxUp != null) PreviewBoxUp.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString(_savedData.ColorUp));
                if (PreviewBoxCpu != null) PreviewBoxCpu.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString(_savedData.ColorCpu));
                if (PreviewBoxRam != null) PreviewBoxRam.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString(_savedData.ColorRam));
            }
            catch { /* 忽略格式错误 */ }
            if (this.FindName("ComboTooltipMode") is ComboBox cb) cb.SelectedIndex = MainWindow.TooltipMode;


        }

        // 将颜色配置应用到全局 UI 元素
        private void ApplyTheme()
        {
            try
            {
                var brushDown = new SolidColorBrush((Color)ColorConverter.ConvertFromString(_savedData.ColorDown));
                var brushUp = new SolidColorBrush((Color)ColorConverter.ConvertFromString(_savedData.ColorUp));
                var brushCpu = new SolidColorBrush((Color)ColorConverter.ConvertFromString(_savedData.ColorCpu));
                var brushRam = new SolidColorBrush((Color)ColorConverter.ConvertFromString(_savedData.ColorRam));
                var brushBgMain = new SolidColorBrush((Color)ColorConverter.ConvertFromString(_savedData.ColorBgMain));
                var brushBgCard = new SolidColorBrush((Color)ColorConverter.ConvertFromString(_savedData.ColorBgCard));
                
                // 计算主背景的亮度 (Luminance)，智能决定全局文本是黑还是白 公式: 0.299*R + 0.587*G + 0.114*B
                double luminance = 0.299 * brushBgMain.Color.R + 0.587 * brushBgMain.Color.G + 0.114 * brushBgMain.Color.B;
                bool isLightBg = luminance > 140; // 如果背景极度鲜艳明亮，则切换为深色字
                Color mainTextColor = isLightBg ? Color.FromRgb(20, 20, 20) : Colors.White;
                Color dimTextColor = isLightBg ? Color.FromRgb(80, 80, 80) : Color.FromRgb(180, 180, 180);

                Color borderColor = isLightBg ? Color.FromRgb(210, 210, 215) : Color.FromRgb(51, 51, 51);
                Color inputBgColor = isLightBg
                    ? Color.FromRgb((byte)Math.Max(0, brushBgCard.Color.R - 15), (byte)Math.Max(0, brushBgCard.Color.G - 15), (byte)Math.Max(0, brushBgCard.Color.B - 15))
                    : Color.FromRgb((byte)Math.Min(255, brushBgCard.Color.R + 15), (byte)Math.Min(255, brushBgCard.Color.G + 15), (byte)Math.Min(255, brushBgCard.Color.B + 15));
                Color heatmapEmptyColor = isLightBg ? Color.FromRgb(225, 225, 230) : Color.FromRgb(40, 40, 45);
                Color gridLineColor = isLightBg ? Color.FromArgb(30, 0, 0, 0) : Color.FromArgb(30, 255, 255, 255);
                var brushMainText = new SolidColorBrush(mainTextColor);
                var brushDimText = new SolidColorBrush(dimTextColor);
                var brushInputBg = new SolidColorBrush(inputBgColor);
                var brushBorder = new SolidColorBrush(borderColor);

                // ★ 极限界定：解决弹出层(Popup)、下拉菜单、滚动条等脱离当前 Window 视觉树的控件不随着背景变色的问题。
                // 必须将画刷从 Window 级贯穿提升至 Application 全局字典级，让所有游离 HWND 的组件被动接收更新通知！
                var navBrush = new SolidColorBrush(Color.FromArgb(255,
                    (byte)Math.Clamp(brushBgCard.Color.R + (isLightBg ? -15 : 15), 0, 255),
                    (byte)Math.Clamp(brushBgCard.Color.G + (isLightBg ? -15 : 15), 0, 255),
                    (byte)Math.Clamp(brushBgCard.Color.B + (isLightBg ? -15 : 15), 0, 255)));

                string[] keys = { "TextMainBrush", "TextDimBrush", "BorderMainBrush", "BgInputBrush", "HeatmapEmptyBrush", "GridLineBrush", "BgMainBrush", "BgCardBrush", "BgNavBrush" };
                object[] values = { brushMainText, brushDimText, brushBorder, brushInputBg, new SolidColorBrush(heatmapEmptyColor), new SolidColorBrush(gridLineColor), brushBgMain, brushBgCard, navBrush };

                for (int i = 0; i < keys.Length; i++)
                {
                    this.Resources[keys[i]] = values[i];
                    Application.Current.Resources[keys[i]] = values[i];
                }

                object[] sysKeys = { SystemColors.WindowBrushKey, SystemColors.ControlBrushKey, SystemColors.WindowTextBrushKey, SystemColors.ControlTextBrushKey, SystemColors.HighlightBrushKey, SystemColors.HighlightTextBrushKey };
                object[] sysValues = { brushBgCard, brushInputBg, brushMainText, brushMainText, brushInputBg, brushMainText };

                for (int i = 0; i < sysKeys.Length; i++)
                {
                    this.Resources[sysKeys[i]] = sysValues[i];
                    Application.Current.Resources[sysKeys[i]] = sysValues[i];
                }
                // 主题切换时，通知日志侧边栏的所有文字立刻更新颜色
                foreach (var cat in _logCategories) cat.NotifyColorChange();
                // 核显式强制转换以解决 CS0266
                TxtDown.Foreground = brushDown;
                TxtUp.Foreground = brushUp;
                PolyDown.Stroke = brushDown;
                PolyDown.Fill = new SolidColorBrush(Color.FromArgb(21, brushDown.Color.R, brushDown.Color.G, brushDown.Color.B));
                PolyUp.Stroke = brushUp;
                PolyUp.Fill = new SolidColorBrush(Color.FromArgb(21, brushUp.Color.R, brushUp.Color.G, brushUp.Color.B));



                // 1. 流量监控颜色应用
                TxtDown.Foreground = brushDown;
                TxtUp.Foreground = brushUp;
                PolyDown.Stroke = brushDown;
                PolyDown.Fill = new SolidColorBrush(Color.FromArgb(21, brushDown.Color.R, brushDown.Color.G, brushDown.Color.B));
                PolyUp.Stroke = brushUp;
                PolyUp.Fill = new SolidColorBrush(Color.FromArgb(21, brushUp.Color.R, brushUp.Color.G, brushUp.Color.B));
                MainHoverDotDown.Fill = brushDown;
                MainHoverDotUp.Fill = brushUp;

                // 2. 资源监控颜色应用
                LineCpu.Stroke = brushCpu;
                LineRam.Stroke = brushRam;
                ResHoverDotCpu.Fill = brushCpu;
                ResHoverDotRam.Fill = brushRam;
                // 找到资源面板顶部的图例小圆圈（通过索引或在XAML命名）
                // 这里为了简单，你可以给XAML里的图例Ellipse命名并在此修改，或者暂略。
                // ★ 新增：同步半圆环与进度条的主题颜色
                if (this.FindName("ArrowWinDown") is System.Windows.Shapes.Path aDown) aDown.Stroke = brushDown;
                if (this.FindName("ArrowWinUp") is System.Windows.Shapes.Path aUp) aUp.Stroke = brushUp;
                if (this.FindName("PathWinDown") is System.Windows.Shapes.Path pDown) pDown.Stroke = brushDown;
                if (this.FindName("PathWinUp") is System.Windows.Shapes.Path pUp) pUp.Stroke = brushUp;
                if (this.FindName("RectWanDown") is Rectangle rWanD) rWanD.Fill = brushDown;
                if (this.FindName("RectWanUp") is Rectangle rWanU) rWanU.Fill = brushUp;
                if (this.FindName("RectLanDown") is Rectangle rLanD) rLanD.Fill = brushDown;
                if (this.FindName("RectLanUp") is Rectangle rLanU) rLanU.Fill = brushUp;
                // 3. 全局字体
                this.FontSize = _savedData.GlobalFontSize;
                // 4. 动态覆盖底层背景资源 (瞬间全局变色)
                this.Resources["BgMainBrush"] = brushBgMain;


                this.Resources["BgCardBrush"] = brushBgCard;
                // 利用卡片底色稍微提亮 2 点，生成导航栏和表格行的高级底色
                this.Resources["BgNavBrush"] = new SolidColorBrush(Color.FromArgb(255,
                    (byte)Math.Min(255, brushBgCard.Color.R + 2),
                    (byte)Math.Min(255, brushBgCard.Color.G + 2),
                    (byte)Math.Min(255, brushBgCard.Color.B + 2)));
            }
            catch { /* 忽略 HEX 格式错误导致的异常 */ }
        }

        // ★ v0.5：保存按钮事件
        private void PickColor_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn)
            {
                var dialog = new Forms.ColorDialog();
                // 设置当前颜色为初始值
                if (dialog.ShowDialog() == Forms.DialogResult.OK)
                {
                    // 将 System.Drawing.Color 转换为 HEX 字符串
                    string hex = $"#{dialog.Color.R:X2}{dialog.Color.G:X2}{dialog.Color.B:X2}";

                    // 根据按钮的 Tag 更新对应的 TextBox
                    switch (btn.Tag.ToString())
                    {
                        case "Down": EditColorDown.Text = hex; break;
                        case "Up": EditColorUp.Text = hex; break;
                        case "Cpu": EditColorCpu.Text = hex; break;
                        case "Ram": EditColorRam.Text = hex; break;
                    }
                }
            }
        }

        // 2. 恢复默认设置逻辑
        private void BtnResetDefault_Click(object sender, RoutedEventArgs e)
        {
            var result = MessageBox.Show("确定要将所有主题设置恢复为默认值吗？", "恢复确认", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (result == MessageBoxResult.Yes)
            {
                // 重新初始化一份默认数据
                _savedData.ColorDown = "#FF2277"; 
                _savedData.ColorUp = "#00E5FF";
                _savedData.ColorCpu = "#ff3d71"; 
                _savedData.ColorRam = "#A142F4";
                _savedData.ColorBgMain = "#121216"; // 恢复默认背景
                _savedData.ColorBgCard = "#18181e";
                _savedData.GlobalFontSize = 14;

                SyncThemeUI();   // 同步到控件
                ApplyTheme();    // 立即应用效果
                SaveData();      // 保存到文件
            }
        }

        // ==========================================
        // 5. 新增：2D/3D 交互控制事件逻辑
        // ==========================================

        // --- 2D 地图事件 ---
        private void Map2D_MouseWheel(object sender, MouseWheelEventArgs e)
        {
            _mapLodTimer.Stop();
            if (ChkHighResMap != null && ChkHighResMap.IsChecked == true) _mapLodTimer.Start();
            e.Handled = true;
            double zoom = e.Delta > 0 ? 1.2 : 0.8;
            Point mousePos = e.GetPosition(Map2DContainer);

            if (this.FindName("Map2DScale") is ScaleTransform scale && this.FindName("Map2DTranslate") is TranslateTransform translate)
            {
                double oldScaleX = scale.ScaleX;
                double oldScaleY = scale.ScaleY;
                double newScaleX = oldScaleX * zoom;
                double newScaleY = oldScaleY * zoom;

                // 限制边界
                if (newScaleX < 0.5) newScaleX = 0.5; if (newScaleY < 0.5) newScaleY = 0.5;
                if (newScaleX > 15) newScaleX = 15; if (newScaleY > 15) newScaleY = 15;

                // ★ 核心修复：算出【真正发生】的缩放率，而不是期望的缩放率
                double actualZoomX = newScaleX / oldScaleX;
                double actualZoomY = newScaleY / oldScaleY;

                scale.ScaleX = newScaleX;
                scale.ScaleY = newScaleY;

                // 使用实际缩放率进行反推，确保原点绝对不跑偏
                translate.X = mousePos.X - (mousePos.X - translate.X) * actualZoomX;
                translate.Y = mousePos.Y - (mousePos.Y - translate.Y) * actualZoomY;
            }
        }
        private void Map2D_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            _isMap2DDragging = true;
            _map2DLastMousePos = e.GetPosition(Map2DContainer);
            Map2DContainer.CaptureMouse();
        }

        private void Map2D_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            _isMap2DDragging = false;
            Map2DContainer.ReleaseMouseCapture();
        }

        private void Map2D_MouseMove(object sender, MouseEventArgs e)
        {
            _mapLodTimer.Stop();
            if (ChkHighResMap != null && ChkHighResMap.IsChecked == true) _mapLodTimer.Start();
            if (_isMap2DDragging && this.FindName("Map2DTranslate") is TranslateTransform translate)
            {
                Point currentPos = e.GetPosition(Map2DContainer);
                translate.X += (currentPos.X - _map2DLastMousePos.X);
                translate.Y += (currentPos.Y - _map2DLastMousePos.Y);
                _map2DLastMousePos = currentPos;
            }
        }

        // --- 3D 地球事件 ---
        private void Earth3D_MouseWheel(object sender, MouseWheelEventArgs e)
        {
            e.Handled = true; // 拦截滚轮事件，防止外层 ScrollViewer 跟着乱跑
            double zoom = e.Delta > 0 ? -0.3 : 0.3; // 滚轮向上拉近
            _cameraDistance += zoom;
            if (_cameraDistance < 1.3) _cameraDistance = 1.3;
            if (_cameraDistance > 10.0) _cameraDistance = 10.0;

            if (EarthCam != null) EarthCam.Position = new Point3D(_cameraX, _cameraY, _cameraDistance);
        }

        private void Earth3D_MouseRightButtonDown(object sender, MouseButtonEventArgs e)
        {
            _isEarthRotating = true;
            if (this.FindName("EarthContainer") is UIElement container)
            {
                _earthLastMousePos = e.GetPosition(container);
                container.CaptureMouse();
            }
        }

        private void Earth3D_MouseRightButtonUp(object sender, MouseButtonEventArgs e)
        {
            _isEarthRotating = false;
            if (this.FindName("EarthContainer") is UIElement container) container.ReleaseMouseCapture();
        }

        private void Earth3D_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            _isEarthPanning = true;
            if (this.FindName("EarthContainer") is UIElement container)
            {
                _earthLastMousePos = e.GetPosition(container);
                container.CaptureMouse();
            }
        }

        private void Earth3D_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            _isEarthPanning = false;
            if (this.FindName("EarthContainer") is UIElement container) container.ReleaseMouseCapture();
        }

        private void Earth3D_MouseMove(object sender, MouseEventArgs e)
        {
            if (this.FindName("EarthContainer") is UIElement container)
            {
                Point currentPos = e.GetPosition(container);
                double deltaX = currentPos.X - _earthLastMousePos.X;
                double deltaY = currentPos.Y - _earthLastMousePos.Y;

                if (_isEarthRotating)
                {
                    _earthAngle += deltaX * 0.5;
                    _earthManualRotX += deltaY * 0.5;
                    // 限制俯仰角，防止画面倒转崩塌
                    if (_earthManualRotX > 80) _earthManualRotX = 80;
                    if (_earthManualRotX < -80) _earthManualRotX = -80;
                }
                else if (_isEarthPanning)
                {
                    // 平移相机位置
                    _cameraX -= deltaX * 0.005;
                    _cameraY += deltaY * 0.005;
                    if (EarthCam != null) EarthCam.Position = new Point3D(_cameraX, _cameraY, _cameraDistance);
                }
                _earthLastMousePos = currentPos;
            }
        }

        // 数据导入、导出与清除功能
        private void BtnExportData_Click(object sender, RoutedEventArgs e)
        {
            SaveData(); // 导出前确保最新状态已保存
            var dlg = new Microsoft.Win32.SaveFileDialog
            {
                FileName = $"DashBoard_Backup_{DateTime.Now:yyyyMMdd_HHmmss}.json",
                Filter = "JSON Files|*.json",
                Title = "选择保存备份的位置"
            };
            if (dlg.ShowDialog() == true)
            {
                try
                {
                    File.Copy(SaveFilePath, dlg.FileName, true);
                    MessageBox.Show("数据导出备份成功！\n请妥善保管您的 json 文件。", "导出完成", MessageBoxButton.OK, MessageBoxImage.Information);
                }
                catch (Exception ex) { MessageBox.Show("导出失败: " + ex.Message, "错误", MessageBoxButton.OK, MessageBoxImage.Error); }
            }
        }

        private void BtnImportData_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new Microsoft.Win32.OpenFileDialog
            {
                Filter = "JSON Files|*.json",
                Title = "选择要导入的备份文件"
            };
            if (dlg.ShowDialog() == true)
            {
                if (MessageBox.Show("警告：导入数据将完全覆盖当前的全部历史记录和设置，确定要继续吗？", "导入确认", MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes)
                {
                    try
                    {
                        File.Copy(dlg.FileName, SaveFilePath, true);
                        LoadData();         // 重新加载本地文件到内存
                        SyncThemeUI();      // 刷新偏好设置面板的参数
                        RefreshDashboards();// 强制刷新统计UI
                        MessageBox.Show("数据导入成功并已应用！", "导入完成", MessageBoxButton.OK, MessageBoxImage.Information);
                    }
                    catch (Exception ex) { MessageBox.Show("导入失败，文件可能已损坏: " + ex.Message, "错误", MessageBoxButton.OK, MessageBoxImage.Error); }
                }
            }
        }

        private void BtnDeleteData_Click(object sender, RoutedEventArgs e)
        {
            var res = MessageBox.Show("警告：此操作将永久删除所有网络流量、应用活动等历史记录！\n\n是否保留您的个性化【主题和颜色设置】？\n\n[是] 保留主题，仅清除数据\n[否] 恢复出厂状态 (同时清除数据和主题)\n[取消] 取消操作", "危险操作", MessageBoxButton.YesNoCancel, MessageBoxImage.Error);

            if (res == MessageBoxResult.Cancel) return;

            try
            {
                // 1. 暂存当前的主题数据 (如果选择保留)
                string cDown = _savedData.ColorDown, cUp = _savedData.ColorUp, cCpu = _savedData.ColorCpu, cRam = _savedData.ColorRam;
                string cBgM = _savedData.ColorBgMain, cBgC = _savedData.ColorBgCard;
                double font = _savedData.GlobalFontSize;

                // 2. 彻底重置整个持久化数据对象
                _savedData = new DashboardSaveData();

                // 3. 恢复主题或应用默认主题
                if (res == MessageBoxResult.Yes)
                {
                    _savedData.ColorDown = cDown; _savedData.ColorUp = cUp; _savedData.ColorCpu = cCpu; _savedData.ColorRam = cRam;
                    _savedData.ColorBgMain = cBgM; _savedData.ColorBgCard = cBgC; _savedData.GlobalFontSize = font;
                }
                else
                {
                    _savedData.ColorDown = "#FF2277"; _savedData.ColorUp = "#00E5FF"; _savedData.ColorCpu = "#ff3d71"; _savedData.ColorRam = "#A142F4";
                    _savedData.ColorBgMain = "#121216"; _savedData.ColorBgCard = "#18181e"; _savedData.GlobalFontSize = 14;
                }

                // 4. 重置所有运行时绘制折线图的队列
                _minDown = new Queue<double>(Enumerable.Repeat(0.0, 185)); _minUp = new Queue<double>(Enumerable.Repeat(0.0, 185));
                _secDown = new Queue<double>(Enumerable.Repeat(0.0, 305)); _secUp = new Queue<double>(Enumerable.Repeat(0.0, 305));
                _hourDown = new Queue<double>(Enumerable.Repeat(0.0, 175)); _hourUp = new Queue<double>(Enumerable.Repeat(0.0, 175));
                _dayDown = new Queue<double>(Enumerable.Repeat(0.0, 35)); _dayUp = new Queue<double>(Enumerable.Repeat(0.0, 35));

                _cpuSec = new Queue<double>(Enumerable.Repeat(0.0, 305)); _ramSec = new Queue<double>(Enumerable.Repeat(0.0, 305));
                _cpuMin = new Queue<double>(Enumerable.Repeat(0.0, 185)); _ramMin = new Queue<double>(Enumerable.Repeat(0.0, 185));
                _cpuHour = new Queue<double>(Enumerable.Repeat(0.0, 175)); _ramHour = new Queue<double>(Enumerable.Repeat(0.0, 175));
                _cpuDay = new Queue<double>(Enumerable.Repeat(0.0, 35)); _ramDay = new Queue<double>(Enumerable.Repeat(0.0, 35));

                _tickSec = 0; _tickMin = 0; _tickHour = 0;

                // 5. 清空 UI 集合
                _processTrafficList.Clear();
                _primaryWindowList.Clear();
                _secondaryWindowList.Clear();
                _backgroundWindowList.Clear();
                _uiLogs.Clear();
                _uiFileLogs.Clear();
                if (this.FindName("LogListBox") is ListBox logBox) logBox.ItemsSource = null;
                if (this.FindName("GridFileLogs") is DataGrid fileGrid) fileGrid.ItemsSource = null;
                _scannerInterfaces.Clear();
                if (this.FindName("RadioFilterAll") is RadioButton rAll) rAll.Content = "All 0";
                if (this.FindName("RadioFilterActive") is RadioButton rAct) rAct.Content = "Active 0";
                UpdateLogCategoryCounts();

                // 6. 覆盖写入空白文件以销毁磁盘数据
                SaveData();
                SyncThemeUI();
                ApplyTheme();
                RefreshDashboards();
                if (this.FindName("LogListBox") is ListBox logBox2) logBox2.ItemsSource = _uiLogs;
                if (this.FindName("GridFileLogs") is DataGrid fileGrid2) fileGrid2.ItemsSource = _uiFileLogs;
                MessageBox.Show("历史数据已全部清除！系统已恢复清爽状态。", "清除成功", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex) { MessageBox.Show("清除过程出现问题: " + ex.Message, "错误", MessageBoxButton.OK, MessageBoxImage.Error); }
        }
        private void BtnLaunchApp_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Tag is string path && File.Exists(path))
            {
                // ★ 智能防多开检测：提取当前正在运行的应用路径
                var runningPaths = new HashSet<string>(_activeAppSessions.Values.Select(v => v.ExePath.ToLower()));
                if (runningPaths.Contains(path.ToLower()))
                {
                    MessageBox.Show("该软件目前已经在运行中，无需重复打开。", "智能跳过", MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }

                try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); }
                catch (Exception ex) { MessageBox.Show("启动失败: " + ex.Message); }
            }
            else { MessageBox.Show("无法定位该程序的绝对路径或文件已被删除。"); }
        }
        private void BtnQuerySnapshot_Click(object sender, RoutedEventArgs e)
        {
            if (this.FindName("DatePickerSnapshot") is DatePicker dp && this.FindName("TxtSnapshotTime") is TextBox txtTime)
            {
                if (dp.SelectedDate.HasValue && TimeSpan.TryParse(txtTime.Text, out TimeSpan time))
                {
                    DateTime targetTime = dp.SelectedDate.Value.Date + time;
                    _uiSnapshotApps.Clear();

                    // 查询并在主屏幕显示快照
                    var snapshot = _savedData.AppSessions
                        .Where(s => s.StartTime <= targetTime && (!s.EndTime.HasValue || s.EndTime.Value >= targetTime))
                        .GroupBy(s => s.ExePath).Select(g => g.First()).ToList();

                    foreach (var app in snapshot) _uiSnapshotApps.Add(app);

                    // ★ 将结果直接丢给主屏幕的数据表
                    if (this.FindName("GridAppLogs") is DataGrid grid) grid.ItemsSource = _uiSnapshotApps;
                    if (this.FindName("TxtLauncherListTitle") is TextBlock title) title.Text = $"🔍 快照结果: {targetTime:yyyy-MM-dd HH:mm:ss} (共 {snapshot.Count} 个软件)";

                    if (snapshot.Count == 0) MessageBox.Show("在该时间点没有追踪到任何活动的软件。");
                }
                else MessageBox.Show("时间格式错误，请检查日期和时间(如 14:30:00)是否合法。");
            }
        }
        private void BtnLaunchAll_Click(object sender, RoutedEventArgs e)
        {
            // 智能识别当前展示的是【快照】还是【全量流水】
            IEnumerable<AppSessionInfo> targetList;
            if (this.FindName("GridAppLogs") is DataGrid grid && grid.ItemsSource == _uiSnapshotApps)
                targetList = _uiSnapshotApps; // 启动历史快照
            else
                targetList = _uiAppLogs.GroupBy(s => s.ExePath).Select(g => g.First()); // 如果在全量流水，去重后准备启动

            // 过滤掉文件已经被删除的无效路径
            var listToLaunch = targetList.Where(app => !string.IsNullOrEmpty(app.ExePath) && File.Exists(app.ExePath)).ToList();
            if (listToLaunch.Count == 0) return;

            var res = MessageBox.Show($"确认要一键唤醒当前列表中的 {listToLaunch.Count} 个软件吗？\n\n💡 系统将自动检测并跳过已经在运行的程序。", "智能一键启动", MessageBoxButton.YesNo, MessageBoxImage.Information);
            if (res == MessageBoxResult.Yes)
            {
                List<string> launched = new List<string>();
                List<string> skipped = new List<string>();

                // ★ 获取当前实时正在运行的软件的物理路径集合 (转小写以忽略大小写差异)
                var runningPaths = new HashSet<string>(_activeAppSessions.Values.Select(v => v.ExePath.ToLower()));

                foreach (var app in listToLaunch)
                {
                    if (runningPaths.Contains(app.ExePath.ToLower()))
                    {
                        skipped.Add(app.ProcessName); // 已经运行的，加入跳过名单
                    }
                    else
                    {
                        try
                        {
                            Process.Start(new ProcessStartInfo(app.ExePath) { UseShellExecute = true });
                            launched.Add(app.ProcessName); // 成功启动的，加入成功名单
                        }
                        catch { }
                    }
                }

                // ★ 构建并显示批量执行报告
                StringBuilder msg = new StringBuilder();
                msg.AppendLine("⚡ 批量操作执行完毕！\n");

                msg.AppendLine($"🚀 成功新打开了 {launched.Count} 个软件:");
                msg.AppendLine(launched.Count > 0 ? string.Join(", ", launched) : "(无)");

                msg.AppendLine($"\n⏭️ 智能跳过了 {skipped.Count} 个已在运行的软件:");
                msg.AppendLine(skipped.Count > 0 ? string.Join(", ", skipped) : "(无)");

                MessageBox.Show(msg.ToString(), "一键唤醒报告", MessageBoxButton.OK, MessageBoxImage.Information);
            }
        }

        // 提取图标工具
        private ImageSource? GetIcon(string path)
        {
            try
            {
                if (!File.Exists(path)) return null;
                using var icon = System.Drawing.Icon.ExtractAssociatedIcon(path);

                var bmp = System.Windows.Interop.Imaging.CreateBitmapSourceFromHIcon(
                    icon.Handle, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());

                // ★ 核心修复：冻结图像资源！这样后台线程创建的图片才能被 UI 线程安全渲染，否则必崩。
                bmp.Freeze();
                return bmp;
            }
            catch { return null; }
        }
        private void BtnAdjustTime_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && int.TryParse(btn.Tag?.ToString(), out int minutes))
            {
                if (minutes == 0) // 点击“现在”，恢复显示全量流水账
                {
                    if (this.FindName("GridAppLogs") is DataGrid grid) grid.ItemsSource = _uiAppLogs;
                    if (this.FindName("TxtLauncherListTitle") is TextBlock title) title.Text = "🕒 软件运行流水 (实时全量记录)";
                    if (this.FindName("DatePickerSnapshot") is DatePicker dp) dp.SelectedDate = DateTime.Now.Date;
                    if (this.FindName("TxtSnapshotTime") is TextBox tbTime) tbTime.Text = DateTime.Now.ToString("HH:mm:ss");
                    return;
                }

                DateTime target = DateTime.Now.AddMinutes(minutes);
                if (this.FindName("DatePickerSnapshot") is DatePicker dp2) dp2.SelectedDate = target.Date;
                if (this.FindName("TxtSnapshotTime") is TextBox tbTime2) tbTime2.Text = target.ToString("HH:mm:ss");
                BtnQuerySnapshot_Click(null, null);
            }
        }
        public void AddFileLogEvent(string path, string processName, string action)
        {
            var log = new FileIoEvent { Timestamp = DateTime.Now, FilePath = path, ProcessName = processName, ActionType = action };

            Task.Run(() => {
                var icon = GetIcon(path);
                Dispatcher.InvokeAsync(() => log.Icon = icon);
            });

            lock (_saveDataLock)
            {
                _savedData.FileLogs ??= new List<FileIoEvent>();
                _savedData.FileLogs.Insert(0, log);
                if (_savedData.FileLogs.Count > 1000) _savedData.FileLogs.RemoveAt(_savedData.FileLogs.Count - 1);
            }
            _uiFileLogs.Insert(0, log);
            if (_uiFileLogs.Count > 1000) _uiFileLogs.RemoveAt(_uiFileLogs.Count - 1);
        }

        private void BtnLocateFile_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Tag is string path && !string.IsNullOrEmpty(path))
            {
                try
                {
                    if (File.Exists(path)) Process.Start("explorer.exe", $"/select,\"{path}\"");
                    else if (Directory.Exists(System.IO.Path.GetDirectoryName(path))) Process.Start("explorer.exe", $"\"{System.IO.Path.GetDirectoryName(path)}\"");
                    else MessageBox.Show("该文件或其所在目录已被删除或移动。", "无法定位");
                }
                catch { }
            }
        }
        // 1. 关闭/收起进程详情列表
        private void BtnCloseDetails_Click(object sender, RoutedEventArgs e)
        {
            if (ProcessGrid != null) ProcessGrid.SelectedIndex = -1;
        }

        // 2. 拦截内层 DataGrid 滚轮，防止外层页面跟着滚动
        private void InnerDataGrid_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
        {
            var innerScrollViewer = FindVisualChild<ScrollViewer>(sender as DependencyObject);
            if (innerScrollViewer != null)
            {
                double delta = e.Delta;
                // 增加容差，防止高分屏下小数点计算导致的边界判定失效
                bool isAtTop = innerScrollViewer.VerticalOffset <= 2.0;
                bool isAtBottom = innerScrollViewer.VerticalOffset >= (innerScrollViewer.ScrollableHeight - 2.0);

                // 如果已经到底部或顶部，手动将滚动事件“接力”路由给外层最近的 ScrollViewer
                if ((delta > 0 && isAtTop) || (delta < 0 && isAtBottom))
                {
                    e.Handled = true;
                    var parent = VisualTreeHelper.GetParent(sender as DependencyObject);
                    while (parent != null)
                    {
                        if (parent is ScrollViewer sv && sv != innerScrollViewer)
                        {
                            sv.ScrollToVerticalOffset(sv.VerticalOffset - delta);
                            return;
                        }
                        parent = VisualTreeHelper.GetParent(parent);
                    }
                    return;
                }

                // 正常内部滚动
                innerScrollViewer.ScrollToVerticalOffset(innerScrollViewer.VerticalOffset - delta);
                e.Handled = true;
            }
        }
        // 3. 辅助方法：寻找视觉树中的子控件
        private T? FindVisualChild<T>(DependencyObject? obj) where T : DependencyObject
        {
            if (obj == null) return null;
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(obj); i++)
            {
                DependencyObject child = VisualTreeHelper.GetChild(obj, i);
                if (child != null && child is T t) return t;
                T? childItem = FindVisualChild<T>(child);
                if (childItem != null) return childItem;
            }
            return null;
        }
        // ==========================================
        // 6. 全新日志过滤分类 & 局域网扫描引擎
        // ==========================================
        public class LogCategory : INotifyPropertyChanged
        {
            public string Name { get; set; } = "";
            public int Count { get; set; }
            public string FilterKey { get; set; } = "";
            public bool IsHeader { get; set; }
            public string ColorHex { get; set; } = "";
            public SolidColorBrush ColorBrush
            {
                get
                {
                    if (string.IsNullOrEmpty(ColorHex))
                        return (Application.Current.MainWindow as MainWindow)?.Resources["TextMainBrush"] as SolidColorBrush ?? Brushes.White;
                    return new SolidColorBrush((Color)ColorConverter.ConvertFromString(ColorHex));
                }
            }
            public string FontWeight { get; set; } = "Normal";

            public string CountDisplay => IsHeader || Count == 0 ? "" : Count.ToString();

            public event PropertyChangedEventHandler? PropertyChanged;
            public void NotifyUpdate() { OnPropertyChanged(nameof(Count)); OnPropertyChanged(nameof(CountDisplay)); }
            public void NotifyColorChange() { OnPropertyChanged(nameof(ColorBrush)); }

            protected void OnPropertyChanged(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }
        private ObservableCollection<LogCategory> _logCategories = new ObservableCollection<LogCategory>();

        private void InitializeLogCategories()
        {
            if (_logCategories.Count > 0) return;
            _logCategories.Add(new LogCategory { Name = "All Alerts", FilterKey = "All", FontWeight = "Bold" });

            _logCategories.Add(new LogCategory { Name = "Filter by severity", IsHeader = true, ColorHex = "#888888" });
            _logCategories.Add(new LogCategory { Name = "Security / Important", FilterKey = "Security", ColorHex = "#FF3D71" });

            _logCategories.Add(new LogCategory { Name = "Filter by alert type", IsHeader = true, ColorHex = "#888888" });
            _logCategories.Add(new LogCategory { Name = "System file monitor", FilterKey = "Security" });
            _logCategories.Add(new LogCategory { Name = "Application info monitor", FilterKey = "AppUpdate" }); // 对应版本更新
            _logCategories.Add(new LogCategory { Name = "Internet access monitor", FilterKey = "Network" });   // 对应网络断开/恢复
            _logCategories.Add(new LogCategory { Name = "First network activity", FilterKey = "App" });
            _logCategories.Add(new LogCategory { Name = "Network Scanner", FilterKey = "NetworkScanner" });
            _logCategories.Add(new LogCategory { Name = "File IO Logs", FilterKey = "FileIO", ColorHex = "#00E5FF" });

            if (this.FindName("LogCategoryList") is ListBox lb) lb.ItemsSource = _logCategories;
            UpdateLogCategoryCounts();
        }
        private void UpdateLogCategoryCounts()
        {
            foreach (var cat in _logCategories)
            {
                if (cat.IsHeader) continue;
                if (cat.FilterKey == "All") cat.Count = _savedData.AppLogs.Count + _savedData.FileLogs.Count;
                else if (cat.FilterKey == "FileIO") cat.Count = _savedData.FileLogs.Count;
                // ★ 核心：精准统计对应的 Type
                else cat.Count = _savedData.AppLogs.Count(l => l.Type == cat.FilterKey);
                cat.NotifyUpdate();
            }
        }

        private void LogCategoryList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (this.FindName("LogCategoryList") is ListBox lb && lb.SelectedItem is LogCategory cat)
            {
                if (cat.IsHeader) return;
                if (cat.FilterKey == "FileIO")
                {
                    if (this.FindName("LogListBox") is ListBox llb) llb.Visibility = Visibility.Collapsed;
                    if (this.FindName("GridFileLogs") is DataGrid gfl) gfl.Visibility = Visibility.Visible;
                }
                else
                {
                    if (this.FindName("LogListBox") is ListBox llb)
                    {
                        llb.Visibility = Visibility.Visible;
                        if (cat.FilterKey == "All") llb.ItemsSource = _uiLogs;
                        // ★ 核心：根据 FilterKey 匹配刚才触发的事件类型
                        else llb.ItemsSource = _uiLogs.Where(l => l.Type == cat.FilterKey).ToList();
                    }
                    if (this.FindName("GridFileLogs") is DataGrid gfl) gfl.Visibility = Visibility.Collapsed;
                }
            }
        }
        // 扫描器模型数据绑定

        private ObservableCollection<ScannerInterface> _scannerInterfaces = new ObservableCollection<ScannerInterface>();

        [DllImport("iphlpapi.dll", SetLastError = true)]
        static extern uint GetIpNetTable(IntPtr pIpNetTable, ref uint pdwSize, bool bOrder);

        [StructLayout(LayoutKind.Sequential)]
        public struct MIB_IPNETROW
        {
            public int dwIndex; public int dwPhysAddrLen;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 8)] public byte[] bPhysAddr;
            public uint dwAddr; public int dwType;
        }
        private void ScannerInterfaceHeader_Click(object sender, MouseButtonEventArgs e)
        {
            if (sender is FrameworkElement fe && fe.DataContext is ScannerInterface si)
            {
                si.IsExpanded = !si.IsExpanded; // 翻转状态
                e.Handled = true;
            }
        }

        private bool _isScanning = false;
        private string _currentScannerFilter = "All";

        private void ScannerFilter_Click(object sender, RoutedEventArgs e)
        {
            if (sender is RadioButton rb && rb.IsChecked == true)
            {
                // ★ 修复：依赖Name而非经常变动的Content(由于带有数量字符)
                _currentScannerFilter = rb.Name.Contains("All") ? "All" : "Active";
                RenderScannerUI();
            }
        }
        // ★ 新增：快速读取系统ARP缓存表 (不进行耗时的Ping探测)，呈现伪静态历史状态
        private void QuickLoadArpTable()
        {
            var interfaces = NetworkInterface.GetAllNetworkInterfaces()
                .Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType != NetworkInterfaceType.Loopback)
                .ToList();

            uint bytesNeeded = 0;
            if (GetIpNetTable(IntPtr.Zero, ref bytesNeeded, false) == 122 && bytesNeeded > 0)
            {
                IntPtr buffer = Marshal.AllocCoTaskMem((int)bytesNeeded);
                try
                {
                    if (GetIpNetTable(buffer, ref bytesNeeded, false) == 0)
                    {
                        int entries = Marshal.ReadInt32(buffer);
                        IntPtr currentBuffer = new IntPtr(buffer.ToInt64() + 4);
                        for (int i = 0; i < entries; i++)
                        {
                            var row = Marshal.PtrToStructure<MIB_IPNETROW>(currentBuffer);
                            if (row.dwType == 3 || row.dwType == 4)
                            {
                                string ip = new IPAddress((long)row.dwAddr).ToString();
                                string mac = string.Join(":", row.bPhysAddr.Take(row.dwPhysAddrLen).Select(b => b.ToString("X2")));

                                if (ip == "127.0.0.1" || ip == "0.0.0.0" || ip.StartsWith("224.") || ip.StartsWith("239.") || ip == "255.255.255.255")
                                {
                                    currentBuffer = new IntPtr(currentBuffer.ToInt64() + Marshal.SizeOf<MIB_IPNETROW>());
                                    continue;
                                }

                                if (!_savedData.ScannerHistory.ContainsKey(mac))
                                {
                                    // 重点伪造一个20分钟前的时间，使其天然呈现灰色的“不活动/历史”状态
                                    _savedData.ScannerHistory[mac] = new ScannerDevice { MAC = mac, IP = ip, LastSeenTime = DateTime.Now.AddMinutes(-20), Index = row.dwIndex };
                                    ResolveHostnameAsync(mac, ip);
                                }
                            }
                            currentBuffer = new IntPtr(currentBuffer.ToInt64() + Marshal.SizeOf<MIB_IPNETROW>());
                        }
                    }
                }
                finally { Marshal.FreeCoTaskMem(buffer); }
            }
            RenderScannerUI(interfaces);
        }
        private void BtnScannerSettings_Click(object sender, RoutedEventArgs e)
        {
            if (this.FindName("ScannerSettingsPanel") is UIElement panel)
            {
                panel.Visibility = panel.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible;
                if (panel.Visibility == Visibility.Visible)
                {
                    if (this.FindName("TxtAutoScanMins") is TextBox t) t.Text = _savedData.ScannerAutoScanMinutes.ToString();
                    if (this.FindName("SliderThreads") is Slider s) s.Value = _savedData.ScannerThreads;
                    if (this.FindName("ChkProtoDHCP") is CheckBox c1) c1.IsChecked = _savedData.ScannerUseDHCP;
                    if (this.FindName("ChkProtoSNMP") is CheckBox c2) c2.IsChecked = _savedData.ScannerUseSNMP;
                    if (this.FindName("ChkProtoMDNS") is CheckBox c3) c3.IsChecked = _savedData.ScannerUseMDNS;
                    if (this.FindName("ChkProtoSSDP") is CheckBox c4) c4.IsChecked = _savedData.ScannerUseSSDP;
                }
            }
        }

        private void ScannerSettings_Changed(object sender, RoutedEventArgs e)
        {
            if (!IsLoaded) return;
            if (this.FindName("TxtAutoScanMins") is TextBox t && int.TryParse(t.Text, out int m)) _savedData.ScannerAutoScanMinutes = m;
            if (this.FindName("SliderThreads") is Slider s) { _savedData.ScannerThreads = (int)s.Value; if (this.FindName("TxtThreadsValue") is TextBlock tv) tv.Text = _savedData.ScannerThreads.ToString(); }
            if (this.FindName("ChkProtoDHCP") is CheckBox c1) _savedData.ScannerUseDHCP = c1.IsChecked == true;
            if (this.FindName("ChkProtoSNMP") is CheckBox c2) _savedData.ScannerUseSNMP = c2.IsChecked == true;
            if (this.FindName("ChkProtoMDNS") is CheckBox c3) _savedData.ScannerUseMDNS = c3.IsChecked == true;
            if (this.FindName("ChkProtoSSDP") is CheckBox c4) _savedData.ScannerUseSSDP = c4.IsChecked == true;
            SaveData();

            if (this.Tag is DispatcherTimer timer)
            {
                timer.Interval = TimeSpan.FromMinutes(Math.Max(1, _savedData.ScannerAutoScanMinutes));
            }
        }

        private async void BtnScan_Click(object sender, RoutedEventArgs e)
        {
            if (_isScanning) return;
            _isScanning = true;
            if (this.FindName("BtnScan") is Button btnScan) btnScan.Content = "⏳ 正在深度扫描...";

            if (this.FindName("ScannerInterfacesList") is ItemsControl scList && scList.ItemsSource == null)
                scList.ItemsSource = _scannerInterfaces;

            int maxThreads = _savedData.ScannerThreads > 0 ? _savedData.ScannerThreads : 32;
            using var semaphore = new SemaphoreSlim(maxThreads);

            var interfaces = NetworkInterface.GetAllNetworkInterfaces()
                .Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType != NetworkInterfaceType.Loopback)
                .ToList();

            var pingTasks = new List<Task>();

            await Task.Run(() => {
                foreach (var ni in interfaces)
                {
                    var props = ni.GetIPProperties();
                    foreach (var ipInfo in props.UnicastAddresses)
                    {
                        if (ipInfo.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
                        {
                            byte[] ipBytes = ipInfo.Address.GetAddressBytes();
                            byte[] maskBytes = ipInfo.IPv4Mask.GetAddressBytes();

                            byte[] networkBytes = new byte[4];
                            for (int i = 0; i < 4; i++) networkBytes[i] = (byte)(ipBytes[i] & maskBytes[i]);

                            int hosts = ~BitConverter.ToInt32(maskBytes.Reverse().ToArray(), 0);
                            if (hosts > 2048) hosts = 2048; // Protect against huge subnets to prevent app hanging

                            // ★ 核心 1：多线程并发 Ping 探测整个子网，这是深度扫描 "需等待" 的本质
                            for (int i = 1; i < hosts; i++)
                            {
                                byte[] targetBytes = new byte[4];
                                Array.Copy(networkBytes, targetBytes, 4);
                                int current = BitConverter.ToInt32(targetBytes.Reverse().ToArray(), 0);
                                current += i;
                                targetBytes = BitConverter.GetBytes(current).Reverse().ToArray();

                                string targetIp = new IPAddress(targetBytes).ToString();
                                if (targetIp == ipInfo.Address.ToString()) continue;

                                pingTasks.Add(Task.Run(async () =>
                                {
                                    await semaphore.WaitAsync();
                                    try
                                    {
                                        using var ping = new Ping();
                                        await ping.SendPingAsync(targetIp, 800);
                                    }
                                    catch { }
                                    finally { semaphore.Release(); }
                                }));
                            }
                        }
                    }
                }
            });

            // ★ 核心 2：同时发射对应协议的探索包，强制唤醒哑设备并在系统 ARP 中注册存在感
            if (_savedData.ScannerUseMDNS) SendUdpMulticast("224.0.0.251", 5353, new byte[] { 0x00, 0x00, 0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x09, 0x5f, 0x73, 0x65, 0x72, 0x76, 0x69, 0x63, 0x65, 0x73, 0x07, 0x5f, 0x64, 0x6e, 0x73, 0x2d, 0x73, 0x64, 0x04, 0x5f, 0x75, 0x64, 0x70, 0x05, 0x6c, 0x6f, 0x63, 0x61, 0x6c, 0x00, 0x00, 0x0c, 0x00, 0x01 });
            if (_savedData.ScannerUseSSDP) SendUdpMulticast("239.255.255.250", 1900, Encoding.ASCII.GetBytes("M-SEARCH * HTTP/1.1\r\nHost: 239.255.255.250:1900\r\nMan: \"ssdp:discover\"\r\nST: ssdp:all\r\n\r\n"));
            if (_savedData.ScannerUseDHCP) SendUdpMulticast("255.255.255.255", 67, new byte[300]);
            if (_savedData.ScannerUseSNMP) SendUdpMulticast("255.255.255.255", 161, new byte[] { 0x30, 0x26, 0x02, 0x01, 0x01, 0x04, 0x06, 0x70, 0x75, 0x62, 0x6c, 0x69, 0x63, 0xa0, 0x19, 0x02, 0x04, 0x12, 0x34, 0x56, 0x78, 0x02, 0x01, 0x00, 0x02, 0x01, 0x00, 0x30, 0x0b, 0x30, 0x09, 0x06, 0x05, 0x2b, 0x06, 0x01, 0x02, 0x01, 0x05, 0x00 });

            // 等待所有探测线程落地发包
            await Task.WhenAll(pingTasks);

            // 解析已通过以上操作自动收割满战利品的 ARP 缓存表
            ProcessArpTable(interfaces);

            _isScanning = false;
            if (this.FindName("BtnScan") is Button b) b.Content = "🔄 一键深度扫描";
            AddLogEvent("NetworkScanner", "深度局域网扫描", $"完成了多线程网卡深度扫描，历史总设备 {_savedData.ScannerHistory.Count} 个。", "#8BC34A");
            UpdateLogCategoryCounts();
        }
        private void SendUdpMulticast(string ip, int port, byte[] data)
        {
            Task.Run(() => {
                try
                {
                    using var udp = new System.Net.Sockets.UdpClient();
                    udp.EnableBroadcast = true;
                    udp.Send(data, data.Length, ip, port);
                }
                catch { }
            });
        }

        private void ProcessArpTable(IEnumerable<NetworkInterface> interfaces)
        {
            uint bytesNeeded = 0;
            if (GetIpNetTable(IntPtr.Zero, ref bytesNeeded, false) == 122 && bytesNeeded > 0)
            {
                IntPtr buffer = Marshal.AllocCoTaskMem((int)bytesNeeded);
                try
                {
                    if (GetIpNetTable(buffer, ref bytesNeeded, false) == 0)
                    {
                        int entries = Marshal.ReadInt32(buffer);
                        IntPtr currentBuffer = new IntPtr(buffer.ToInt64() + 4);
                        for (int i = 0; i < entries; i++)
                        {
                            var row = Marshal.PtrToStructure<MIB_IPNETROW>(currentBuffer);
                            if (row.dwType == 3 || row.dwType == 4)
                            {
                                string ip = new IPAddress((long)row.dwAddr).ToString();
                                string mac = string.Join(":", row.bPhysAddr.Take(row.dwPhysAddrLen).Select(b => b.ToString("X2")));

                                if (ip == "127.0.0.1" || ip == "0.0.0.0" || ip.StartsWith("224.") || ip.StartsWith("239.") || ip == "255.255.255.255")
                                {
                                    currentBuffer = new IntPtr(currentBuffer.ToInt64() + Marshal.SizeOf<MIB_IPNETROW>());
                                    continue;
                                }

                                if (!_savedData.ScannerHistory.ContainsKey(mac))
                                {
                                    _savedData.ScannerHistory[mac] = new ScannerDevice { MAC = mac, IP = ip, LastSeenTime = DateTime.Now, Index = row.dwIndex };
                                    ResolveHostnameAsync(mac, ip);
                                }
                                else
                                {
                                    _savedData.ScannerHistory[mac].IP = ip;
                                    _savedData.ScannerHistory[mac].LastSeenTime = DateTime.Now;
                                    _savedData.ScannerHistory[mac].Index = row.dwIndex;
                                }
                            }
                            currentBuffer = new IntPtr(currentBuffer.ToInt64() + Marshal.SizeOf<MIB_IPNETROW>());
                        }
                    }
                }
                finally { Marshal.FreeCoTaskMem(buffer); }
            }

            RenderScannerUI(interfaces);
            SaveData();
        }

        private async void ResolveHostnameAsync(string mac, string ip)
        {
            try
            {
                var entry = await Dns.GetHostEntryAsync(ip);
                if (!string.IsNullOrEmpty(entry.HostName))
                {
                    if (_savedData.ScannerHistory.TryGetValue(mac, out var device))
                    {
                        device.Name = entry.HostName;
                        Dispatcher.Invoke(() => { RenderScannerUI(); });
                    }
                }
            }
            catch { }
        }

        private void RenderScannerUI(IEnumerable<NetworkInterface> interfaces = null)
        {
            if (interfaces == null)
            {
                interfaces = NetworkInterface.GetAllNetworkInterfaces()
                    .Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType != NetworkInterfaceType.Loopback);
            }

            // ★ 修复闪烁问题：不再全部 Clear(会引发整棵树摧毁与Resource加载报错)，而是局部修改增量状态
            var currentMacs = new HashSet<string>();
            int allCount = 0;
            int activeCount = 0;

            if (this.FindName("ScannerInterfacesList") is ItemsControl scList && scList.ItemsSource == null)
                scList.ItemsSource = _scannerInterfaces;

            foreach (var ni in interfaces)
            {
                var props = ni.GetIPProperties();
                int ipv4Index = -1;
                try { var ipv4Props = props.GetIPv4Properties(); if (ipv4Props != null) ipv4Index = ipv4Props.Index; } catch { continue; }
                if (ipv4Index == -1) continue;

                string mac = string.Join(":", ni.GetPhysicalAddress().GetAddressBytes().Select(b => b.ToString("X2")));
                currentMacs.Add(mac);

                var si = _scannerInterfaces.FirstOrDefault(s => s.MAC == mac);
                if (si == null)
                {
                    si = new ScannerInterface
                    {
                        Name = ni.Name,
                        Description = ni.Description,
                        MAC = mac,
                        DnsServers = string.Join("\n", props.DnsAddresses.Select(d => d.ToString())),
                        IPAddresses = string.Join("\n", props.UnicastAddresses.Select(u => u.Address.ToString())),
                        Gateway = string.Join("\n", props.GatewayAddresses.Select(g => g.Address.ToString())),
                        ScannedTime = $"Scanned just now",
                        IsExpanded = true
                    };
                    _scannerInterfaces.Add(si);
                }
                else
                {
                    si.ScannedTime = $"Scanned just now";
                }

                if (!_savedData.ScannerHistory.ContainsKey(mac))
                {
                    string localIp = props.UnicastAddresses.FirstOrDefault(u => u.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)?.Address.ToString() ?? "";
                    _savedData.ScannerHistory[mac] = new ScannerDevice { MAC = mac, IP = localIp, Name = Environment.MachineName, Description = "Local Device", LastSeenTime = DateTime.Now, Index = ipv4Index };
                }
                else if (ni.OperationalStatus == OperationalStatus.Up)
                {
                    // 本机始终保持活动状态
                    _savedData.ScannerHistory[mac].LastSeenTime = DateTime.Now;
                }

                var matched = _savedData.ScannerHistory.Values.Where(a => a.Index == ipv4Index).ToList();

                // ★ 关键：仅清空内部子列表以触发 Observable 的局部通知，保护根节点 DataGrid 不被销毁
                si.Devices.Clear();

                foreach (var d in matched)
                {
                    allCount++;
                    if (d.IsActive) activeCount++;

                    // 执行 All 或 Active 过滤逻辑
                    if (_currentScannerFilter == "All" || (_currentScannerFilter == "Active" && d.IsActive))
                    {
                        si.Devices.Add(d);
                    }
                }
            }

            // 清理已断开物理连接的旧网卡卡片
            var toRemove = _scannerInterfaces.Where(s => !currentMacs.Contains(s.MAC)).ToList();
            foreach (var r in toRemove) _scannerInterfaces.Remove(r);

            // 更新 UI 按钮显示的数量
            if (this.FindName("RadioFilterAll") is RadioButton rAll) rAll.Content = $"All {allCount}";
            if (this.FindName("RadioFilterActive") is RadioButton rAct) rAct.Content = $"Active {activeCount}";
        }
        private void BtnScanSingle_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Tag is string macAddress)
            {
                e.Handled = true;
                var targetInterface = _scannerInterfaces.FirstOrDefault(s => s.MAC == macAddress);
                if (targetInterface == null) return;

                // 对于单网卡扫描按钮，直接触发全局多线程扫描即可满足需求
                BtnScan_Click(null, null);
            }
        }


    }




public class TcpConnection { public IPAddress? RemoteAddress { get; set; } public ushort RemotePort { get; set; } public uint State { get; set; } public int ProcessId { get; set; } }



}
