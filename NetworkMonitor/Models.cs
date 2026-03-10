using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Net;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using static NetworkMonitor.MainWindow;

namespace NetworkMonitor
{
    public class FileIoEvent : INotifyPropertyChanged
    {

        public DateTime Timestamp { get; set; }
        public string ProcessName { get; set; } = "";
        public string ActionType { get; set; } = "";
        public string FilePath { get; set; } = "";
        [System.Text.Json.Serialization.JsonIgnore]
        public string TimeDisplay => Timestamp.ToString("MM-dd HH:mm:ss");
        public string DisplayName => ProcessName;
        public string ToolTipText => ProcessDictionary.GetTooltip(ProcessName);

        [System.Text.Json.Serialization.JsonIgnore]
        public SolidColorBrush ActionColorBrush
        {
            get
            {
                if (ActionType.Contains("删除")) return new SolidColorBrush(Color.FromRgb(255, 61, 113)); // 红色
                if (ActionType.Contains("移动") || ActionType.Contains("重命名")) return new SolidColorBrush(Color.FromRgb(255, 152, 0)); // 橙色
                return new SolidColorBrush(Color.FromRgb(50, 205, 50)); // 绿色 (创建/修改)
            }
        }

        private ImageSource? _icon;
        [System.Text.Json.Serialization.JsonIgnore]
        public ImageSource? Icon
        {
            get => _icon;
            set { _icon = value; OnPropertyChanged(nameof(Icon)); }
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        protected void OnPropertyChanged(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
    public class AppLogEvent
    {
        public DateTime Timestamp { get; set; }
        public string Type { get; set; } = "";
        public string Title { get; set; } = "";
        public string Message { get; set; } = "";
        public string ColorHex { get; set; } = "#FF2277";

        [System.Text.Json.Serialization.JsonIgnore]
        public string TimeDisplay => Timestamp.ToString("MM-dd HH:mm:ss");
        [System.Text.Json.Serialization.JsonIgnore]
        public SolidColorBrush ColorBrush => new SolidColorBrush((Color)ColorConverter.ConvertFromString(ColorHex));
        [System.Text.Json.Serialization.JsonIgnore]
        public string IconText => Type == "App" ? "NEW" : "🌐";
    }


    // 文件操作流水事件类

    // ★ 移出：时光机会话类 (加入 Icon 的动态通知)
    public class AppSessionInfo : INotifyPropertyChanged
    {
        public string ProcessName { get; set; } = "";
        public string ExePath { get; set; } = "";
        public DateTime StartTime { get; set; }

        private ImageSource? _icon;
        [System.Text.Json.Serialization.JsonIgnore]
        public ImageSource? Icon
        {
            get => _icon;
            set { _icon = value; OnPropertyChanged(nameof(Icon)); }
        }
        public string DisplayName => ProcessName;
        public string ToolTipText => ProcessDictionary.GetTooltip(ProcessName);

        private DateTime? _endTime;
        public DateTime? EndTime
        {
            get => _endTime;
            set { _endTime = value; OnPropertyChanged(nameof(EndTime)); OnPropertyChanged(nameof(StatusText)); OnPropertyChanged(nameof(TimeRange)); OnPropertyChanged(nameof(StatusColor)); OnPropertyChanged(nameof(StatusColorBrush)); }
        }
        [System.Text.Json.Serialization.JsonIgnore]
        public string StatusText => EndTime.HasValue ? $"已关闭" : "运行中";
        [System.Text.Json.Serialization.JsonIgnore]
        public string TimeRange => $"{StartTime:MM-dd HH:mm:ss} ~ {(EndTime.HasValue ? EndTime.Value.ToString("HH:mm:ss") : "至今")}";
        [System.Text.Json.Serialization.JsonIgnore]
        public string StatusColor => EndTime.HasValue ? "#888888" : "#32CD32";
        [System.Text.Json.Serialization.JsonIgnore]
        public SolidColorBrush StatusColorBrush => new SolidColorBrush((Color)ColorConverter.ConvertFromString(StatusColor));

        public event PropertyChangedEventHandler? PropertyChanged;
        protected void OnPropertyChanged(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
    public static class ProcessDictionary
    {
        // ★ 开放默认字典，用于首次生成本地 JSON
        public static readonly Dictionary<string, string> DefaultDict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        {"chrome", "谷歌浏览器"}, {"msedge", "Edge浏览器"}, {"firefox", "火狐浏览器"},
        {"explorer", "资源管理器"}, {"svchost", "系统服务宿主"}, {"system", "系统内核"},
        {"code", "VS Code"}, {"devenv", "Visual Studio"}, {"idea64", "IntelliJ IDEA"},
        {"pycharm64", "PyCharm"}, {"wechat", "微信"}, {"qq", "QQ"},
        {"dingtalk", "钉钉"}, {"feishu", "飞书"}, {"cloudmusic", "网易云音乐"},
        {"qqmusic", "QQ音乐"}, {"obs64", "OBS Studio"}, {"discord", "Discord"},
        {"telegram", "Telegram"}, {"steam", "Steam客户端"}, {"epicgameslauncher", "Epic客户端"},
        {"qbittorrent", "qBittorrent"}, {"baidunetdisk", "百度网盘"}, {"xunlei", "迅雷"},
        {"taskmgr", "任务管理器"}, {"cmd", "命令提示符"}, {"powershell", "PowerShell"},
        {"pwsh", "PowerShell Core"}, {"windowsterminal", "Windows终端"}, {"v2rayn", "V2RayN"},
        {"clash-verge", "Clash Verge"}, {"mihomo", "Mihomo内核"}, {"sing-box", "Sing-Box内核"}
    };

        private static Dictionary<string, string> _customDict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        public static void LoadCustomDict(Dictionary<string, string> customDict)
        {
            _customDict = new Dictionary<string, string>(customDict ?? new Dictionary<string, string>(), StringComparer.OrdinalIgnoreCase);
        }

        public static string GetWithDesc(string processName)
        {
            if (string.IsNullOrEmpty(processName)) return "";
            string key = processName.ToLower().Replace(".exe", "");
            if (_customDict.TryGetValue(key, out string desc)) return $"{processName} ({desc})";
            return processName; // 找不到就返回原名
        }

        public static string GetDesc(string processName)
        {
            if (string.IsNullOrEmpty(processName)) return "";
            string key = processName.ToLower().Replace(".exe", "");
            if (_customDict.TryGetValue(key, out string desc)) return desc;
            return "";
        }

        public static string GetTooltip(string processName)
        {
            if (MainWindow.TooltipMode == 0) return null;
            if (MainWindow.TooltipMode == 1) return $"名称: {processName}";

            string desc = GetDesc(processName);
            if (string.IsNullOrEmpty(desc))
                return $"名称: {processName}";
            else
                return $"名称: {processName}\n具体描述: {desc}";
        }
    }
    public class DashboardSaveData

    {
        public int TooltipMode { get; set; } = 2;
        // ★ 新增：用于存储用户自定义字典

        // ★ 新增：扫描器历史记录与配置状态
        public Dictionary<string, ScannerDevice> ScannerHistory { get; set; } = new Dictionary<string, ScannerDevice>();
        public int ScannerAutoScanMinutes { get; set; } = 30;
        public int ScannerThreads { get; set; } = 32;
        public bool ScannerUseDHCP { get; set; } = true;
        public bool ScannerUseSNMP { get; set; } = true;
        public bool ScannerUseMDNS { get; set; } = true;
        public bool ScannerUseSSDP { get; set; } = true;
        // ★ 新增：记住桌面悬浮窗的显示状态
        public bool ShowMiniWindow { get; set; } = false;
        public Dictionary<string, string> AppVersions { get; set; } = new Dictionary<string, string>();
        public List<AppLogEvent> AppLogs { get; set; } = new List<AppLogEvent>();
        public List<AppSessionInfo> AppSessions { get; set; } = new List<AppSessionInfo>();
        //  持久化文件操作日志
        public List<FileIoEvent> FileLogs { get; set; } = new List<FileIoEvent>();
        public string CustomMapPath { get; set; } = ""; // 必须包含此行，否则补丁引擎会报错
        public bool ShowCpu { get; set; } = true;
        public bool ShowRam { get; set; } = true;
        public bool ShowBat { get; set; } = false;
        public bool ShowGpu { get; set; } = false;
        public bool ShowDisk { get; set; } = false;



        public DateTime LastSavedTime { get; set; } // ★ 记录关机时间
        public int TickSec { get; set; }
        public int TickMin { get; set; }
        public int TickHour { get; set; }
        public Dictionary<string, ulong> ProcessDownloadTraffic { get; set; } = new Dictionary<string, ulong>();
        public Dictionary<string, ulong> ProcessUploadTraffic { get; set; } = new Dictionary<string, ulong>();

        public Dictionary<string, Dictionary<int, ResourceSnap>> DailyResourceHistory { get; set; } = new Dictionary<string, Dictionary<int, ResourceSnap>>();

        public Dictionary<string, long> ProcessTotalTraffic { get; set; } = new Dictionary<string, long>();
        public Dictionary<string, long> ProcessActiveTime { get; set; } = new Dictionary<string, long>();
        public Dictionary<string, long> DailyTraffic { get; set; } = new Dictionary<string, long>();
        public Dictionary<int, long> ActiveSecondsPerHour { get; set; } = new Dictionary<int, long>();
        public bool HasAskedMinimize { get; set; } = false;
        public bool MinimizeToTray { get; set; } = true;
        public Dictionary<string, long> PrimaryWindowTimes { get; set; } = new Dictionary<string, long>();
        public Dictionary<string, long> SecondaryWindowTimes { get; set; } = new Dictionary<string, long>();
        public Dictionary<string, long> BackgroundProcessTimes { get; set; } = new Dictionary<string, long>();
        public Dictionary<string, Dictionary<string, long>> DailyAppActiveTime { get; set; } = new Dictionary<string, Dictionary<string, long>>();
        public string ColorDown { get; set; } = "#FF2277";
        public string ColorUp { get; set; } = "#00E5FF";
        public string ColorCpu { get; set; } = "#ff3d71";
        public string ColorRam { get; set; } = "#A142F4";
        // 全局背景颜色存档
        public string ColorBgMain { get; set; } = "#121216";
        public string ColorBgCard { get; set; } = "#18181e";
        public double GlobalFontSize { get; set; } = 14;
        public double ScrollSpeed { get; set; } = 1.0; // ★ 新增：滚动速度倍率
        public Dictionary<string, ResourceSnap> DailyResourceAverages { get; set; } = new Dictionary<string, ResourceSnap>();
        public Dictionary<string, Dictionary<int, long>> DailyHourlyActive { get; set; } = new Dictionary<string, Dictionary<int, long>>();

        public List<double> SavedMinDown { get; set; } = new List<double>();
        public List<double> SavedMinUp { get; set; } = new List<double>();
        public List<double> SavedSecDown { get; set; } = new List<double>();
        public List<double> SavedSecUp { get; set; } = new List<double>();
        public List<double> SavedCpuSec { get; set; } = new List<double>();
        public List<double> SavedRamSec { get; set; } = new List<double>();
        public List<double> SavedHourDown { get; set; } = new List<double>();
        public List<double> SavedHourUp { get; set; } = new List<double>();
        public List<double> SavedDayDown { get; set; } = new List<double>();
        public List<double> SavedDayUp { get; set; } = new List<double>();
        public List<double> SavedCpuMin { get; set; } = new List<double>();
        public List<double> SavedRamMin { get; set; } = new List<double>();
        public List<double> SavedCpuHour { get; set; } = new List<double>();
        public List<double> SavedRamHour { get; set; } = new List<double>();
        public List<double> SavedCpuDay { get; set; } = new List<double>();
        public List<double> SavedRamDay { get; set; } = new List<double>();
        public Dictionary<string, long> DailyVpnTraffic { get; set; } = new Dictionary<string, long>();
        public Dictionary<string, Dictionary<string, long>> DailyAppTraffic { get; set; } = new Dictionary<string, Dictionary<string, long>>();
        public Dictionary<string, Dictionary<string, long>> DailyPrimaryTime { get; set; } = new Dictionary<string, Dictionary<string, long>>();
        public Dictionary<string, Dictionary<string, long>> DailySecondaryTime { get; set; } = new Dictionary<string, Dictionary<string, long>>();
        public Dictionary<string, Dictionary<string, long>> DailyBackgroundTime { get; set; } = new Dictionary<string, Dictionary<string, long>>();
        // ★ 新增：按小时切片的应用追踪数据 (支持历史时间范围过滤)
        public Dictionary<string, Dictionary<string, long>> HourlyPrimaryTime { get; set; } = new Dictionary<string, Dictionary<string, long>>();
        public Dictionary<string, Dictionary<string, long>> HourlySecondaryTime { get; set; } = new Dictionary<string, Dictionary<string, long>>();
        public Dictionary<string, Dictionary<string, long>> HourlyBackgroundTime { get; set; } = new Dictionary<string, Dictionary<string, long>>();
        public Dictionary<string, Dictionary<string, long>> HourlyAppTraffic { get; set; } = new Dictionary<string, Dictionary<string, long>>();

        public HashSet<string> KnownNetworkApps { get; set; } = new HashSet<string>();

    }
    // IP 连接数据对象
    // IP 连接数据对象 (阶段 1 升级版)
    public class IPConnectionInfo : INotifyPropertyChanged
    {
        public string IP { get; set; } = "";

        private string _flagAndRegion = "🌍 未知";
        public string FlagAndRegion { get => _flagAndRegion; set { _flagAndRegion = value; OnPropertyChanged(nameof(FlagAndRegion)); } }

        private string _downloadDisplay = "0 B";
        public string DownloadDisplay { get => _downloadDisplay; set { _downloadDisplay = value; OnPropertyChanged(nameof(DownloadDisplay)); } }

        private string _uploadDisplay = "0 B";
        public string UploadDisplay { get => _uploadDisplay; set { _uploadDisplay = value; OnPropertyChanged(nameof(UploadDisplay)); } }

        // 存储共用该 IP 的其他进程名称
        public ObservableCollection<string> AssociatedProcesses { get; set; } = new ObservableCollection<string>();

        public event PropertyChangedEventHandler? PropertyChanged;
        protected void OnPropertyChanged(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    // 新增：高精度流量快照
    public class TrafficSnapshot
    {
        public DateTime Timestamp { get; set; }
        public Dictionary<int, ulong> PidDownload { get; set; } = new Dictionary<int, ulong>();
        public Dictionary<int, ulong> PidUpload { get; set; } = new Dictionary<int, ulong>();
        public Dictionary<int, HashSet<string>> PidIPs { get; set; } = new Dictionary<int, HashSet<string>>();
    }    // UI 绑定的进程类
    public class ProcessNetworkInfo : INotifyPropertyChanged
    {
        public string ProcessName { get; set; } = "";
        public string State { get; set; } = "";
        public long RawTotal { get; set; }
        // ★ 新增：用于让 DataGrid 进行正确的数值比大小
        public long RawDownload { get; set; }
        public long RawUpload { get; set; }

        private string _downloadDisplay = "0 B";
        public string DisplayName => ProcessName;
        public string ToolTipText => ProcessDictionary.GetTooltip(ProcessName);
        public string DownloadDisplay { get => _downloadDisplay; set { _downloadDisplay = value; OnPropertyChanged(nameof(DownloadDisplay)); } }

        private string _uploadDisplay = "0 B";
        public string UploadDisplay { get => _uploadDisplay; set { _uploadDisplay = value; OnPropertyChanged(nameof(UploadDisplay)); } }

        private int _connectionCount = 0;
        public int ConnectionCount { get => _connectionCount; set { _connectionCount = value; OnPropertyChanged(nameof(ConnectionCount)); } }

        // ★ 子表格数据源
        public ObservableCollection<IPConnectionInfo> Connections { get; set; } = new ObservableCollection<IPConnectionInfo>();

        public event PropertyChangedEventHandler? PropertyChanged;
        protected void OnPropertyChanged(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    // 定义一个简单的类来避免元组识别问题
    public class ResourceSnap
    {
        public double cpu { get; set; }
        public double ram { get; set; }
    }
    public class AppDistItem : INotifyPropertyChanged
    {
        public string Name { get; set; } = "";

        private string _valueDisplay = "";
        public string ValueDisplay { get => _valueDisplay; set { _valueDisplay = value; OnPropertyChanged(nameof(ValueDisplay)); } }

        public string ToolTipText => ProcessDictionary.GetTooltip(Name);
        public long RawValue { get; set; }

        private bool _isUpdating;
        public bool IsUpdating
        {
            get => _isUpdating;
            set { _isUpdating = value; OnPropertyChanged(nameof(IsUpdating)); OnPropertyChanged(nameof(StatusDisplay)); }
        }

        public string StatusDisplay => IsUpdating ? " 🔺" : "";

        public event PropertyChangedEventHandler? PropertyChanged;
        protected void OnPropertyChanged(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
    public class WindowActivityInfo : INotifyPropertyChanged
    {
        public string Name { get; set; } = "";
        private string _timeDisplay = "0 秒";
        public string TimeDisplay { get => _timeDisplay; set { _timeDisplay = value; OnPropertyChanged(nameof(TimeDisplay)); } }
        public long RawSeconds { get; set; }
        public string DisplayName => Name;
        public string ToolTipText => ProcessDictionary.GetTooltip(Name);
        // 用于 UI 绑定显示加号
        private bool _isUpdating;
        public bool IsUpdating
        {
            get => _isUpdating;
            set
            {
                _isUpdating = value;
                OnPropertyChanged(nameof(IsUpdating));
                OnPropertyChanged(nameof(StatusDisplay)); // ★ 核心修复：触发加号的刷新
            }
        }

        // ★ 核心修复：根据状态显示加号或上升图标
        public string StatusDisplay => IsUpdating ? " 🔺" : "";

        public event PropertyChangedEventHandler? PropertyChanged;
        protected void OnPropertyChanged(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
    public class ProcessAggregateInfo
    {
        public string ProcessName { get; set; } = "";
        public ulong SessionDownload { get; set; }
        public ulong SessionUpload { get; set; }
        public HashSet<string> ConnectedIPs { get; set; } = new HashSet<string>();

        public long SessionDelta { get; set; }
        public int ConnectionCount { get; set; }
        public string PrimaryIP { get; set; } = "";
    }
    public class ScannerDevice
    {
        public string Name { get; set; } = "Generic";
        public string Description { get; set; } = "Unknown Device";
        public string IP { get; set; } = "";
        public string MAC { get; set; } = "";
        [System.Text.Json.Serialization.JsonIgnore]
        public string IPMACDisplay => $"{IP}\n{MAC}";
        public int Index { get; set; }
        public DateTime LastSeenTime { get; set; } = DateTime.Now;
        [System.Text.Json.Serialization.JsonIgnore]
        public string LastSeen => LastSeenTime.ToString("d MMM, yyyy\nHH:mm");
        [System.Text.Json.Serialization.JsonIgnore]
        public bool IsActive => (DateTime.Now - LastSeenTime).TotalMinutes < 15;
        [System.Text.Json.Serialization.JsonIgnore]
        public SolidColorBrush StatusColor => IsActive ? new SolidColorBrush(Color.FromRgb(139, 195, 74)) : new SolidColorBrush(Colors.Gray);
    }
    public class ScannerInterface : INotifyPropertyChanged
    {
        public string Name { get; set; } = "";
        public string Description { get; set; } = "";
        public string MAC { get; set; } = "";
        public string DnsServers { get; set; } = "";
        public string IPAddresses { get; set; } = "";
        public string Gateway { get; set; } = "";
        private string _scannedTime = "";
        public string ScannedTime
        {
            get => _scannedTime;
            set { _scannedTime = value; OnPropertyChanged(nameof(ScannedTime)); }
        }
        public ObservableCollection<ScannerDevice> Devices { get; set; } = new ObservableCollection<ScannerDevice>();
        // 展开属性与通知机制
        private bool _isExpanded = false;
        public bool IsExpanded
        {
            get => _isExpanded;
            set { _isExpanded = value; OnPropertyChanged(nameof(IsExpanded)); OnPropertyChanged(nameof(ExpandedVisibility)); OnPropertyChanged(nameof(ExpandIcon)); }
        }
        public Visibility ExpandedVisibility => IsExpanded ? Visibility.Visible : Visibility.Collapsed;
        public string ExpandIcon => IsExpanded ? "▲ 收起" : "▼ 展开详情";

        public event PropertyChangedEventHandler? PropertyChanged;
        protected void OnPropertyChanged(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
    public class DictItem : INotifyPropertyChanged
    {
        private string _key = "";
        public string Key { get => _key; set { _key = value; OnPropertyChanged(nameof(Key)); } }

        private string _value = "";
        public string Value { get => _value; set { _value = value; OnPropertyChanged(nameof(Value)); } }

        public event PropertyChangedEventHandler? PropertyChanged;
        protected void OnPropertyChanged(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
    // 高精度流量历史切片 (用于图表框选回溯)






















    [StructLayout(LayoutKind.Sequential)] public struct FILETIME { public uint dwLowDateTime; public uint dwHighDateTime; }
    [StructLayout(LayoutKind.Sequential)] public struct MIB_TCPROW_OWNER_PID { public uint state; public uint localAddr; public uint localPort; public uint remoteAddr; public uint remotePort; public int owningPid; }
    [StructLayout(LayoutKind.Sequential)] public struct MIB_UDPROW_OWNER_PID { public uint localAddr; public uint localPort; public int owningPid; }
    [StructLayout(LayoutKind.Sequential)] public struct IO_COUNTERS { public ulong ReadOp; public ulong WriteOp; public ulong OtherOp; public ulong ReadTransferCount; public ulong WriteTransferCount; public ulong OtherTransfer; }
    [StructLayout(LayoutKind.Sequential)] public struct MEMORYSTATUSEX { public uint dwLength; public uint dwMemoryLoad; public ulong ullTotalPhys; public ulong ullAvailPhys; public ulong ullTotalPageFile; public ulong ullAvailPageFile; public ulong ullTotalVirtual; public ulong ullAvailVirtual; public ulong ullAvailExtendedVirtual; }


}