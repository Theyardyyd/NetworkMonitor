using Microsoft.Diagnostics.Tracing.Parsers;
using Microsoft.Diagnostics.Tracing.Session;
using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;

namespace NetworkMonitor
{
    public static class EtwNetworkTracker
    {
        // 用于防抖和记录当前前台 PID
        public static int CurrentForegroundPid = 0;
        public static ConcurrentDictionary<string, DateTime> _lastFileIo = new ConcurrentDictionary<string, DateTime>();
        // 使用线程安全的字典来记录每个 PID 的纯网络流量 (Bytes)
        public static ConcurrentDictionary<int, ulong> ProcessDownloadBytes = new ConcurrentDictionary<int, ulong>();
        public static ConcurrentDictionary<int, ulong> ProcessUploadBytes = new ConcurrentDictionary<int, ulong>();
        public static ulong GlobalLanDownloadBytes = 0;
        public static ulong GlobalLanUploadBytes = 0;
        private static TraceEventSession _session;
        public static ConcurrentDictionary<string, int> IpToPidMap = new ConcurrentDictionary<string, int>();
        // 更严谨的 LAN 识别，完美支持 IPv4 映射 IPv6 和全部私有网段
        public static bool IsLanIP(string ip)
        {
            if (string.IsNullOrEmpty(ip)) return false;
            // 清理 ::ffff: 前缀 (底层套接字常将 IPv4 映射为此格式)
            if (ip.StartsWith("::ffff:", StringComparison.OrdinalIgnoreCase))
                ip = ip.Substring(7);

            if (ip == "127.0.0.1" || ip == "::1") return false; // 排除本机纯回环

            // 识别 10.x, 192.168.x 以及 169.254.x(APIPA)
            if (ip.StartsWith("10.") || ip.StartsWith("192.168.") || ip.StartsWith("169.254.")) return true;

            // 识别 172.16.x 到 172.31.x
            if (ip.StartsWith("172."))
            {
                var parts = ip.Split('.');
                if (parts.Length >= 2 && int.TryParse(parts[1], out int second))
                    if (second >= 16 && second <= 31) return true;
            }

            // IPv6 局域网地址 (Link-local 和 ULA)
            if (ip.StartsWith("fe80", StringComparison.OrdinalIgnoreCase)) return true;
            if (ip.StartsWith("fc", StringComparison.OrdinalIgnoreCase) ||
                ip.StartsWith("fd", StringComparison.OrdinalIgnoreCase)) return true;

            return false;
        }

        public static void StartTracking()
        {
            Task.Run(() =>
            {
                try
                {
                    // 强杀以往所有非正常退出遗留的僵尸会话，防止 ETW 引擎假死引发流量全 0！
                    foreach (var name in TraceEventSession.GetActiveSessionNames())
                    {
                        if (name.StartsWith("DashboardNetworkSession"))
                        {
                            try { using (var old = new TraceEventSession(name)) { old.Stop(); } } catch { }
                        }
                    }

                    // 生成带进程ID的唯一会话名称
                    string sessionName = $"DashboardNetworkSession_{Process.GetCurrentProcess().Id}";

                    using (_session = new TraceEventSession(sessionName))
                    {
                        _session.EnableKernelProvider(
                            KernelTraceEventParser.Keywords.NetworkTCPIP |
                            KernelTraceEventParser.Keywords.FileIOInit);
                        Action<string, string, ulong, int, bool> ProcessPacket = (src, dst, size, pid, isUpload) =>
                        {
                            if (string.IsNullOrEmpty(src) || string.IsNullOrEmpty(dst)) return;

                            // 屏蔽本机纯回环流量
                            if (src == "127.0.0.1" || dst == "127.0.0.1" || src == "::1" || dst == "::1" ||
                                src.StartsWith("::ffff:127.0.0.1") || dst.StartsWith("::ffff:127.0.0.1")) return;

                            // 提取远端 IP
                            string remoteIp = isUpload ? dst : src;
                            if (remoteIp.StartsWith("::ffff:", StringComparison.OrdinalIgnoreCase))
                                remoteIp = remoteIp.Substring(7);
                            int targetPid = pid;
                            if ((pid == 0 || pid == 4) && IpToPidMap.TryGetValue(remoteIp, out int realPid))
                            {
                                targetPid = realPid;
                            }

                            if (isUpload) ProcessUploadBytes.AddOrUpdate(targetPid, size, (id, old) => old + size);
                            else ProcessDownloadBytes.AddOrUpdate(targetPid, size, (id, old) => old + size);
                            if (IsLanIP(remoteIp))
                            {
                                if (isUpload) Interlocked.Add(ref GlobalLanUploadBytes, size);
                                else Interlocked.Add(ref GlobalLanDownloadBytes, size);
                            }
                        };

                        _session.Source.Kernel.TcpIpRecv += (d) => ProcessPacket(d.saddr?.ToString(), d.daddr?.ToString(), (ulong)d.size, d.ProcessID, false);
                        _session.Source.Kernel.TcpIpSend += (d) => ProcessPacket(d.saddr?.ToString(), d.daddr?.ToString(), (ulong)d.size, d.ProcessID, true);
                        _session.Source.Kernel.UdpIpRecv += (d) => ProcessPacket(d.saddr?.ToString(), d.daddr?.ToString(), (ulong)d.size, d.ProcessID, false);
                        _session.Source.Kernel.UdpIpSend += (d) => ProcessPacket(d.saddr?.ToString(), d.daddr?.ToString(), (ulong)d.size, d.ProcessID, true);

                        // 文件操作高精度追踪与智能过滤 ---
                        // 文件操作高精度追踪与智能过滤 ---
                        Action<string, string, int, string> ProcessFile = (path, pName, pid, action) =>
                        {
                            if (string.IsNullOrEmpty(path) || string.IsNullOrEmpty(pName)) return;
                            // 拦截自身进程，防止程序保存 dashboard_data.json 时被无限套娃记录！
                            if (pid == Process.GetCurrentProcess().Id) return;
                            // 过滤掉没有扩展名的路径 (99%是系统底层调用或目录本身)
                            int lastDot = path.LastIndexOf('.');
                            int lastSlash = path.LastIndexOf('\\');
                            if (lastDot < lastSlash || lastDot == -1) return;

                            string pLow = pName.ToLower();

                            bool isForeground = (pid == CurrentForegroundPid);
                            bool isExplorer = (pLow == "explorer");
                            if (!isExplorer && !isForeground) return;

                            if ((pLow == "chrome" || pLow == "msedge" || pLow == "firefox") && action == "访问/修改") return;
                            if (pLow == "system" || pLow == "svchost" || pLow == "searchindexer" || pLow == "devenv" || pLow == "conhost") return;

                            string pathLow = path.ToLower();
                            // 系统级 Hosts 文件防篡改监控
                            if (pathLow.EndsWith(@"system32\drivers\etc\hosts") && action != "访问")
                            {
                                Application.Current.Dispatcher.InvokeAsync(() => {
                                    (Application.Current.MainWindow as MainWindow)?.AddLogEvent("Security", "系统核心文件被修改", $"System file \"hosts\" changed in {path}\n修改来源进程: {pName}", "#FF3D71");
                                });
                            }
                            // 极度严格的白名单机制，只记录用户可能手动修改的常规文件格式，杜绝海量无用日志
                            string[] allowedExts = { ".txt", ".doc", ".docx", ".xls", ".xlsx", ".ppt", ".pptx", ".pdf", ".jpg", ".jpeg", ".png", ".gif", ".mp4", ".mp3", ".zip", ".rar", ".7z", ".md", ".csv", ".json", ".cs", ".cpp", ".py", ".html", ".css", ".js" };
                            bool isAllowed = false;
                            foreach (var ext in allowedExts)
                            {
                                if (pathLow.EndsWith(ext)) { isAllowed = true; break; }
                            }
                            if (!isAllowed) return;

                            // 进一步过滤系统盘敏感目录，避免后台缓存或系统自动备份绕过白名单
                            if (pathLow.Contains(@"\appdata\") || pathLow.Contains(@"\windows\") ||
                                pathLow.Contains(@"\programdata\") || pathLow.Contains(@"\temp\") ||
                                pathLow.Contains(@"$recycle.bin") || pathLow.Contains(@"\.git\") ||
                                pathLow.Contains(@"\program files")) return;

                            // 防止瞬间大量写入刷屏 (防抖)
                            string cacheKey = $"{pathLow}_{action}";
                            if (_lastFileIo.TryGetValue(cacheKey, out DateTime lastTime))
                                if ((DateTime.Now - lastTime).TotalSeconds < 2) return;

                            if (_lastFileIo.Count > 5000) _lastFileIo.Clear();
                            _lastFileIo[cacheKey] = DateTime.Now;

                            // 抛给 UI 层渲染
                            Application.Current.Dispatcher.InvokeAsync(() => {
                                (Application.Current.MainWindow as MainWindow)?.AddFileLogEvent(path, pName, action);
                            });
                        }; _session.Source.Kernel.FileIOCreate += (d) => ProcessFile(d.FileName, d.ProcessName, d.ProcessID, "访问/修改");
                        _session.Source.Kernel.FileIODelete += (d) => ProcessFile(d.FileName, d.ProcessName, d.ProcessID, "删除");
                        _session.Source.Kernel.FileIORename += (d) => ProcessFile(d.FileName, d.ProcessName, d.ProcessID, "移动/重命名");

                        _session.Source.Kernel.TcpIpRecvIPV6 += (d) => ProcessPacket(d.saddr?.ToString(), d.daddr?.ToString(), (ulong)d.size, d.ProcessID, false);
                        _session.Source.Kernel.TcpIpSendIPV6 += (d) => ProcessPacket(d.saddr?.ToString(), d.daddr?.ToString(), (ulong)d.size, d.ProcessID, true);
                        _session.Source.Kernel.UdpIpRecvIPV6 += (d) => ProcessPacket(d.saddr?.ToString(), d.daddr?.ToString(), (ulong)d.size, d.ProcessID, false);
                        _session.Source.Kernel.UdpIpSendIPV6 += (d) => ProcessPacket(d.saddr?.ToString(), d.daddr?.ToString(), (ulong)d.size, d.ProcessID, true);

                        _session.Source.Process();
                    }
                }
                catch { /* 忽略错误以防崩溃 */ }
            });
        }

        public static void StopTracking() { _session?.Dispose(); }
    }








}