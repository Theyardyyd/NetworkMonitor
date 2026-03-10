using System;
using System.Collections.Generic;
using System.Net;
using System.Runtime.InteropServices;
using System.Text;
using static NetworkMonitor.MainWindow;

namespace NetworkMonitor
{
    // ==========================================
    // 独立系统 API 与硬件监控引擎
    // ==========================================
    public static class SystemMonitor
    {



        public static List<TcpConnection> GetAllTcpConnections()
        {
            var res = new List<TcpConnection>(); int size = 0; GetExtendedTcpTable(IntPtr.Zero, ref size, true, 2, 5, 0); IntPtr ptr = Marshal.AllocHGlobal(size);
            try { if (GetExtendedTcpTable(ptr, ref size, true, 2, 5, 0) == 0) { int cnt = Marshal.ReadInt32(ptr); IntPtr rPtr = (IntPtr)((long)ptr + 4); for (int i = 0; i < cnt; i++) { var r = Marshal.PtrToStructure<MIB_TCPROW_OWNER_PID>(rPtr); res.Add(new TcpConnection { State = r.state, RemoteAddress = new IPAddress(r.remoteAddr), RemotePort = (ushort)((r.remotePort & 0xff) << 8 | (r.remotePort >> 8) & 0xff), ProcessId = r.owningPid }); rPtr = (IntPtr)((long)rPtr + Marshal.SizeOf<MIB_TCPROW_OWNER_PID>()); } } } finally { Marshal.FreeHGlobal(ptr); }
            return res;
        }




        [DllImport("iphlpapi.dll")] public static extern uint GetExtendedTcpTable(IntPtr p, ref int s, bool b, int v, int c, uint r);
        [DllImport("iphlpapi.dll")] public static extern uint GetExtendedUdpTable(IntPtr p, ref int s, bool b, int v, int c, uint r);
        [DllImport("kernel32.dll")] public static extern bool GetProcessIoCounters(IntPtr h, out IO_COUNTERS c);
        [DllImport("kernel32.dll")] public static extern bool GetSystemTimes(out FILETIME i, out FILETIME k, out FILETIME u);
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
        public static extern bool GetSystemPowerStatus(out SYSTEM_POWER_STATUS sps);
        [DllImport("kernel32.dll")] public static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX m);

        [DllImport("user32.dll")]
        public static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        public static extern bool IsWindowVisible(IntPtr hWnd);

        [DllImport("user32.dll")]
        public static extern bool IsIconic(IntPtr hWnd);

        [DllImport("user32.dll")]
        public static extern int GetWindowThreadProcessId(IntPtr hWnd, out int lpdwProcessId);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        public static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

        [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        public static extern int GetWindowTextLength(IntPtr hWnd);

        public delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

        [DllImport("user32.dll")]
        public static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

        [DllImport("dwmapi.dll")]
        public static extern int DwmGetWindowAttribute(IntPtr hwnd, int dwAttribute, out int pvAttribute, int cbAttribute);

        [DllImport("user32.dll")]
        public static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

        [DllImport("user32.dll")]
        public static extern IntPtr GetShellWindow();
        [StructLayout(LayoutKind.Sequential)]
        public struct LASTINPUTINFO
        {
            public uint cbSize;
            public uint dwTime;
        }

        [DllImport("user32.dll")]
        public static extern bool GetLastInputInfo(ref LASTINPUTINFO plii);

        // 用于检测全屏看视频或全屏游戏状态 (QUNS_RUNNING_D3D_FULL_SCREEN = 3, QUNS_PRESENTATION_MODE = 4)
        [DllImport("shell32.dll")]
        public static extern int SHQueryUserNotificationState(out int pquns);



    }
}