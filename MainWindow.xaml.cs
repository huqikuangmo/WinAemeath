using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Threading;

namespace WinAemeath
{
    public partial class MainWindow : Window
    {
        #region Win32 API

        [DllImport("user32.dll")] private static extern IntPtr SetWinEventHook(uint eventMin, uint eventMax, IntPtr hmodWinEventProc, WinEventDelegate lpfnWinEventProc, uint idProcess, uint idThread, uint dwFlags);
        [DllImport("user32.dll")] private static extern bool UnhookWinEvent(IntPtr hWinEventHook);
        [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);
        [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] private static extern int GetWindowLong(IntPtr hWnd, int nIndex);
        [DllImport("user32.dll")] private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);
        [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Auto)] private static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);
        [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hWnd);
        [DllImport("user32.dll")] private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);
        [DllImport("user32.dll")] private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint dwFlags);
        [DllImport("shcore.dll")] private static extern int GetDpiForMonitor(IntPtr hmonitor, int dpiType, out uint dpiX, out uint dpiY);
        [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);
        [DllImport("user32.dll")] private static extern bool IsWindow(IntPtr hWnd);
        [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(IntPtr hwnd, int dwAttribute, out RECT pvAttribute, int cbAttribute);

        private delegate void WinEventDelegate(IntPtr hWinEventHook, uint eventType, IntPtr hwnd, int idObject, int idChild, uint dwEventThread, uint dwmsEventTime);

        [StructLayout(LayoutKind.Sequential)]
        public struct RECT { public int Left, Top, Right, Bottom; }

        [StructLayout(LayoutKind.Sequential)]
        public struct MONITORINFO
        {
            public int cbSize;
            public RECT rcMonitor;
            public RECT rcWork;
            public uint dwFlags;
        }

        private const uint EVENT_SYSTEM_FOREGROUND = 0x0003;
        private const uint EVENT_OBJECT_LOCATIONCHANGE = 0x800B;
        private const uint EVENT_OBJECT_DESTROY = 0x8001;
        private const uint WINEVENT_OUTOFCONTEXT = 0;
        private const uint MONITOR_DEFAULTTONEAREST = 2;
        private const int GWL_EXSTYLE = -20;
        private const int WS_EX_TRANSPARENT = 0x00000020;
        private const int WS_EX_LAYERED = 0x00080000;
        private const int WS_EX_TOOLWINDOW = 0x00000080;
        private const int WS_EX_APPWINDOW = 0x00040000;
        private const int DWMWA_EXTENDED_FRAME_BOUNDS = 9;

        #endregion

        #region Layout Constants

        private const double PAD_LEFT = 175.0;
        private const double PAD_TOP = 75.0;
        private const double PAD_RIGHT = 200.0;
        private const double PAD_BOTTOM = 75.0;
        private const double TOP_IMAGE_SIZE = 150.0;
        private const double BOTTOM_IMAGE_SIZE = 200.0;

        #endregion

        // 精确匹配的系统窗口类名黑名单
        private static readonly HashSet<string> _blockedClassNames = new(StringComparer.OrdinalIgnoreCase)
        {
            "Shell_TrayWnd", "Shell_SecondaryTrayWnd", "NotifyIconOverflowWindow",
            "TrayNotifyWnd", "SysPager", "ToolbarWindow32", "Progman", "WorkerW",
            "MultitaskingViewFrame", "TaskSwitcherWnd", "TaskSwitcherOverlayWnd",
            "#32768",
        };

        // 前缀/包含匹配的系统窗口类名黑名单
        private static readonly string[] _blockedClassPrefixes =
        {
            "Windows.UI", "Xaml", "DesktopChildSiteBridge", "Popup",
            "DesktopWindowContentBridge", "XamlExplorerHostIslandWindow",
        };

        // ── 字段 ────────────────────────────────────────────────────

        private IntPtr _hook;
        private IntPtr _hookDestroy;
        private WinEventDelegate _delegate;   // 防止 GC 回收委托
        private IntPtr _myHwnd;
        private IntPtr _targetHwnd;
        private IntPtr _lastForeground;

        // 缓存自身进程 ID，避免在 WinEvent 热路径中重复调用
        private readonly uint _myPid = (uint)System.Diagnostics.Process.GetCurrentProcess().Id;

        // ── 构造 ────────────────────────────────────────────────────

        public MainWindow()
        {
            InitializeComponent();
            Loaded += OnLoaded;
            Closed += OnClosed;
        }

        // ── 初始化 ──────────────────────────────────────────────────

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            _myHwnd = new WindowInteropHelper(this).Handle;
            MakeClickThrough();
            RegisterEventHooks();
            InitTrackingWithDelay();
            StartPositionPolling();
        }

        private void MakeClickThrough()
        {
            int style = GetWindowLong(_myHwnd, GWL_EXSTYLE);
            SetWindowLong(_myHwnd, GWL_EXSTYLE,
                (style | WS_EX_TRANSPARENT | WS_EX_LAYERED | WS_EX_TOOLWINDOW) & ~WS_EX_APPWINDOW);
        }

        private void RegisterEventHooks()
        {
            _delegate = WinEventCallback;
            _hook = SetWinEventHook(
                EVENT_SYSTEM_FOREGROUND, EVENT_OBJECT_LOCATIONCHANGE,
                IntPtr.Zero, _delegate, 0, 0, WINEVENT_OUTOFCONTEXT);

            _hookDestroy = SetWinEventHook(
                EVENT_OBJECT_DESTROY, EVENT_OBJECT_DESTROY,
                IntPtr.Zero, _delegate, 0, 0, WINEVENT_OUTOFCONTEXT);
        }

        /// <summary>
        /// 延迟到下一帧再抓前台窗口，避免启动时自身被误识别为目标。
        /// </summary>
        private void InitTrackingWithDelay()
        {
            Hide();

            // 用 BeginInvoke(Background) 替代 0ms Timer，语义更清晰
            Dispatcher.BeginInvoke(() =>
            {
                IntPtr hwnd = GetForegroundWindow();
                if (hwnd != IntPtr.Zero && hwnd != _myHwnd && !IsSystemWindow(hwnd))
                {
                    _targetHwnd = hwnd;
                    UpdatePosition(_targetHwnd);
                }
            }, DispatcherPriority.Background);
        }

        /// <summary>
        /// 高频轮询（~60fps）：同步目标窗口位置，并检测前台变化。
        /// 使用轮询兜底 WinEvent 可能丢失的拖动帧。
        /// </summary>
        private void StartPositionPolling()
        {
            var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };

            timer.Tick += (_, _) =>
            {
                // 1. 目标窗口有效性检查 + 位置同步
                if (_targetHwnd != IntPtr.Zero)
                {
                    if (!IsWindow(_targetHwnd) || !IsWindowVisible(_targetHwnd))
                    {
                        _targetHwnd = IntPtr.Zero;
                        Hide();
                        return;
                    }

                    if (IsVisible)
                        UpdatePosition(_targetHwnd);
                }

                // 2. 前台窗口变化检测
                IntPtr fg = GetForegroundWindow();
                if (fg != IntPtr.Zero && fg != _lastForeground && fg != _myHwnd)
                {
                    _lastForeground = fg;
                    if (!IsSystemWindow(fg))
                        OnForegroundChanged(fg);
                }
            };

            timer.Start();
        }

        // ── WinEvent 回调（系统线程，不可操作 UI）─────────────────

        private void WinEventCallback(IntPtr hWinEventHook, uint eventType, IntPtr hwnd,
                                      int idObject, int idChild, uint dwEventThread, uint dwmsEventTime)
        {
            if (hwnd == IntPtr.Zero || hwnd == _myHwnd) return;

            // 过滤自身进程产生的事件
            GetWindowThreadProcessId(hwnd, out uint pid);
            if (pid == _myPid) return;

            switch (eventType)
            {
                case EVENT_SYSTEM_FOREGROUND:
                    if (!IsSystemWindow(hwnd))
                        Dispatcher.BeginInvoke(() => OnForegroundChanged(hwnd));
                    break;

                case EVENT_OBJECT_DESTROY:
                    if (hwnd == _targetHwnd)
                        Dispatcher.BeginInvoke(() => { _targetHwnd = IntPtr.Zero; Hide(); });
                    break;

                case EVENT_OBJECT_LOCATIONCHANGE:
                    if (hwnd == _targetHwnd)
                        Dispatcher.BeginInvoke(OnTargetMoved);
                    break;
            }
        }

        // ── UI 线程处理 ──────────────────────────────────────────────

        private void OnForegroundChanged(IntPtr hwnd)
        {
            if (!IsWindow(hwnd)) { Hide(); return; }

            // 延迟 100ms 等待新窗口完成激活，再确认其有效性
            var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
            timer.Tick += (_, _) =>
            {
                timer.Stop();
                if (!IsWindow(hwnd) || !IsWindowVisible(hwnd)) return;
                if (ShouldHide(hwnd)) { Hide(); return; }

                _targetHwnd = hwnd;
                UpdatePosition(_targetHwnd);
                Show();
            };
            timer.Start();
        }

        private void OnTargetMoved()
        {
            if (ShouldHide(_targetHwnd)) { Hide(); return; }
            UpdatePosition(_targetHwnd);
        }

        // ── 判断逻辑 ─────────────────────────────────────────────────

        /// <summary>
        /// 覆盖层是否应该隐藏：窗口无效、太小、工具窗口、全屏无边框。
        /// </summary>
        private bool ShouldHide(IntPtr hwnd)
        {
            // 句柄基础检查（顺序很重要：先判空再使用）
            if (hwnd == IntPtr.Zero) return true;
            if (!IsWindow(hwnd)) return true;
            if (!IsWindowVisible(hwnd)) return true;

            // 工具窗口（托盘菜单、弹窗等）
            if ((GetWindowLong(hwnd, GWL_EXSTYLE) & WS_EX_TOOLWINDOW) != 0)
                return true;

            // 窗口尺寸过滤
            if (GetWindowRect(hwnd, out RECT r))
            {
                int w = r.Right - r.Left;
                int h = r.Bottom - r.Top;

                if (w < 100 || h < 100) return true;   // 过小
                if (w < 400 && h < 400) return true;   // 弹出菜单/托盘菜单
                if (h / (double)w > 1.5 && w < 400) return true; // 高瘦型弹窗
            }

            return IsFullscreenBorderless(hwnd);
        }

        /// <summary>
        /// 是否为需要过滤的系统 UI 窗口。
        /// </summary>
        private static bool IsSystemWindow(IntPtr hwnd)
        {
            if (hwnd == IntPtr.Zero) return true;

            var sb = new StringBuilder(256);
            GetClassName(hwnd, sb, 256);
            string cn = sb.ToString();

            if (_blockedClassNames.Contains(cn)) return true;
            foreach (var prefix in _blockedClassPrefixes)
                if (cn.Contains(prefix, StringComparison.OrdinalIgnoreCase)) return true;

            return false;
        }

        /// <summary>
        /// 是否处于窗口化全屏状态（覆盖整个显示器但无边框）。
        /// </summary>
        private static bool IsFullscreenBorderless(IntPtr hwnd)
        {
            if (!GetWindowRect(hwnd, out RECT win)) return false;

            IntPtr monitor = MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST);
            var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
            if (!GetMonitorInfo(monitor, ref info)) return false;

            const int tolerance = 8; // 兼容 DPI 缩放和窗口阴影偏移

            static bool Covers(RECT win, RECT target, int tol) =>
                win.Left <= target.Left + tol &&
                win.Top <= target.Top + tol &&
                win.Right >= target.Right - tol &&
                win.Bottom >= target.Bottom - tol;

            return Covers(win, info.rcMonitor, tolerance) || Covers(win, info.rcWork, tolerance);
        }

        // ── 位置更新 ─────────────────────────────────────────────────

        /// <summary>
        /// 覆盖层跟随目标窗口，Canvas 内分别定位三个装饰图片。
        /// </summary>
        private void UpdatePosition(IntPtr targetHwnd)
        {
            if (targetHwnd == IntPtr.Zero) return;

            // 优先用 DWM 视觉边框，失败则回退到 GetWindowRect
            RECT rect;
            if (DwmGetWindowAttribute(targetHwnd, DWMWA_EXTENDED_FRAME_BOUNDS,
                                      out rect, Marshal.SizeOf<RECT>()) != 0)
            {
                if (!GetWindowRect(targetHwnd, out rect)) return;
            }

            double scale = GetDpiScale(targetHwnd);
            double winLeft = rect.Left / scale;
            double winTop = rect.Top / scale;
            double winWidth = (rect.Right - rect.Left) / scale;
            double winHeight = (rect.Bottom - rect.Top) / scale;

            double totalWidth = winWidth + PAD_LEFT + PAD_RIGHT;
            double totalHeight = winHeight + PAD_TOP + PAD_BOTTOM;

            Left = winLeft - PAD_LEFT;
            Top = winTop - PAD_TOP;
            Width = totalWidth;
            Height = totalHeight;

            RootCanvas.Width = totalWidth;
            RootCanvas.Height = totalHeight;

            Canvas.SetLeft(TopLeftImage, PAD_LEFT - 70);
            Canvas.SetTop(TopLeftImage, PAD_TOP - 75);

            Canvas.SetLeft(BottomLeftImage, PAD_LEFT - 120);
            Canvas.SetTop(BottomLeftImage, PAD_TOP + winHeight - BOTTOM_IMAGE_SIZE + 70);

            Canvas.SetLeft(BottomRightImage, PAD_LEFT + winWidth - 65);
            Canvas.SetTop(BottomRightImage, PAD_TOP + winHeight - BOTTOM_IMAGE_SIZE + 70);
        }

        private static double GetDpiScale(IntPtr hwnd)
        {
            IntPtr monitor = MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST);
            return GetDpiForMonitor(monitor, 0, out uint dpiX, out _) == 0
                ? dpiX / 96.0
                : 1.0;
        }

        // ── 清理 ─────────────────────────────────────────────────────

        private void OnClosed(object sender, EventArgs e)
        {
            if (_hook != IntPtr.Zero) UnhookWinEvent(_hook);
            if (_hookDestroy != IntPtr.Zero) UnhookWinEvent(_hookDestroy);
        }
    }
}