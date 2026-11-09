using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Microsoft.Win32;

namespace MouseSwipe
{
    internal static class Program
    {
        [STAThread]
        private static void Main()
        {
            // Keep cursor/hook coordinates in physical pixels on scaled displays.
            // Without this, SetCursorPos would scale again on 125%/150%/200% DPI.
            SetProcessDPIAware();

            bool createdNew;
            using (System.Threading.Mutex mutex =
                new System.Threading.Mutex(true, "MouseSwipe.SingleInstance", out createdNew))
            {
                if (!createdNew)
                {
                    return;
                }

                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Application.Run(new TrayContext());
            }
        }

        [DllImport("user32.dll")]
        private static extern bool SetProcessDPIAware();
    }

    /// <summary>Per-user settings stored in HKCU.</summary>
    internal static class Settings
    {
        private const string AppKey = @"Software\MouseSwipe";
        private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string RunValue = "MouseSwipe";

        public static int GestureButton
        {
            get { return ReadInt("GestureButton", 1); }
            set { WriteInt("GestureButton", value); }
        }

        public static int Threshold
        {
            get { return ReadInt("Threshold", 80); }
            set { WriteInt("Threshold", value); }
        }

        public static bool Invert
        {
            get { return ReadInt("Invert", 0) != 0; }
            set { WriteInt("Invert", value ? 1 : 0); }
        }

        public static bool RunAtStartup
        {
            get
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(RunKey))
                {
                    if (key == null)
                    {
                        return false;
                    }

                    string value = key.GetValue(RunValue) as string;
                    return !string.IsNullOrEmpty(value) &&
                           value.IndexOf(Application.ExecutablePath, StringComparison.OrdinalIgnoreCase) >= 0;
                }
            }
            set
            {
                using (RegistryKey key = Registry.CurrentUser.CreateSubKey(RunKey))
                {
                    if (key == null)
                    {
                        return;
                    }

                    if (value)
                    {
                        key.SetValue(RunValue, "\"" + Application.ExecutablePath + "\"");
                    }
                    else
                    {
                        key.DeleteValue(RunValue, false);
                    }
                }
            }
        }

        private static int ReadInt(string name, int fallback)
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(AppKey))
                {
                    if (key == null)
                    {
                        return fallback;
                    }

                    object raw = key.GetValue(name);
                    if (raw == null)
                    {
                        return fallback;
                    }

                    return Convert.ToInt32(raw);
                }
            }
            catch
            {
                return fallback;
            }
        }

        private static void WriteInt(string name, int value)
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.CreateSubKey(AppKey))
                {
                    if (key != null)
                    {
                        key.SetValue(name, value, RegistryValueKind.DWord);
                    }
                }
            }
            catch
            {
                // Settings are a convenience; never crash the tray app because of them.
            }
        }
    }

    /// <summary>Tray application: owns the hook and the settings menu.</summary>
    internal sealed class TrayContext : ApplicationContext
    {
        private readonly MouseHook _hook;
        private readonly NotifyIcon _tray;
        private readonly Icon _icon;
        private readonly ContextMenuStrip _menu;

        private ToolStripMenuItem _button1Item;
        private ToolStripMenuItem _button2Item;
        private ToolStripMenuItem _invertItem;
        private ToolStripMenuItem _startupItem;
        private ToolStripMenuItem[] _thresholdItems;

        public TrayContext()
        {
            _icon = CreateTrayIcon();
            _hook = new MouseHook();
            _hook.Captured += OnButtonCaptured;

            _menu = new ContextMenuStrip();
            BuildMenu();

            _tray = new NotifyIcon();
            _tray.Icon = _icon;
            _tray.Text = "MouseSwipe 侧键手势";
            _tray.ContextMenuStrip = _menu;
            _tray.DoubleClick += delegate { ShowHelp(); };
            _tray.Visible = true;

            _hook.RefreshSettings();
            if (!_hook.Start())
            {
                MessageBox.Show(
                    "无法安装全局鼠标钩子（错误码 " + Marshal.GetLastWin32Error() + "）。\n" +
                    "请检查安全软件是否拦截，或换一个目录重试。",
                    "MouseSwipe",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error);
            }

            _tray.BalloonTipTitle = "MouseSwipe 已启动";
            _tray.BalloonTipText = "轻点侧键：任务视图\n按住侧键左右滑动：切换桌面\n右键托盘图标可设置";
            _tray.ShowBalloonTip(5000);
        }

        private void BuildMenu()
        {
            _menu.Font = new Font("Microsoft YaHei UI", 9F);

            ToolStripMenuItem header = new ToolStripMenuItem("轻点 = 任务视图　按住左右滑 = 切换桌面");
            header.Enabled = false;

            _button1Item = new ToolStripMenuItem("侧键 1（后退 / Back）");
            _button1Item.Click += delegate { SetGestureButton(1); };

            _button2Item = new ToolStripMenuItem("侧键 2（前进 / Forward）");
            _button2Item.Click += delegate { SetGestureButton(2); };

            ToolStripMenuItem captureItem = new ToolStripMenuItem("按下侧键来识别…");
            captureItem.Click += delegate
            {
                _hook.CaptureNextButton = true;
                _tray.ShowBalloonTip(2500, "识别侧键", "请按一下要使用的鼠标侧键", ToolTipIcon.Info);
            };

            ToolStripMenuItem buttonMenu = new ToolStripMenuItem("手势侧键");
            buttonMenu.DropDownItems.Add(_button1Item);
            buttonMenu.DropDownItems.Add(_button2Item);
            buttonMenu.DropDownItems.Add(new ToolStripSeparator());
            buttonMenu.DropDownItems.Add(captureItem);

            ToolStripMenuItem thresholdMenu = new ToolStripMenuItem("滑动识别距离");
            int[] thresholds = new int[] { 60, 80, 100, 120, 160, 220, 300 };
            _thresholdItems = new ToolStripMenuItem[thresholds.Length];
            for (int i = 0; i < thresholds.Length; i++)
            {
                int value = thresholds[i];
                ToolStripMenuItem item = new ToolStripMenuItem(value + " 像素");
                item.Click += delegate
                {
                    Settings.Threshold = value;
                    UpdateChecks();
                };
                _thresholdItems[i] = item;
                thresholdMenu.DropDownItems.Add(item);
            }

            _invertItem = new ToolStripMenuItem("反向：右滑切到左侧桌面");
            _invertItem.Click += delegate
            {
                Settings.Invert = !Settings.Invert;
                UpdateChecks();
            };

            _startupItem = new ToolStripMenuItem("开机自动启动");
            _startupItem.Click += delegate
            {
                Settings.RunAtStartup = !Settings.RunAtStartup;
                UpdateChecks();
            };

            ToolStripMenuItem helpItem = new ToolStripMenuItem("使用说明");
            helpItem.Click += delegate { ShowHelp(); };

            ToolStripMenuItem exitItem = new ToolStripMenuItem("退出");
            exitItem.Click += delegate { ExitThread(); };

            _menu.Items.Add(header);
            _menu.Items.Add(new ToolStripSeparator());
            _menu.Items.Add(buttonMenu);
            _menu.Items.Add(thresholdMenu);
            _menu.Items.Add(_invertItem);
            _menu.Items.Add(_startupItem);
            _menu.Items.Add(new ToolStripSeparator());
            _menu.Items.Add(helpItem);
            _menu.Items.Add(exitItem);
            _menu.Opening += delegate { UpdateChecks(); };

            UpdateChecks();
        }

        private void UpdateChecks()
        {
            int button = Settings.GestureButton;
            _button1Item.Checked = button == 1;
            _button2Item.Checked = button == 2;

            int threshold = Settings.Threshold;
            for (int i = 0; i < _thresholdItems.Length; i++)
            {
                string[] parts = _thresholdItems[i].Text.Split(' ');
                int value;
                if (parts.Length > 0 && int.TryParse(parts[0], out value))
                {
                    _thresholdItems[i].Checked = value == threshold;
                }
            }

            _invertItem.Checked = Settings.Invert;
            _startupItem.Checked = Settings.RunAtStartup;
        }

        private void SetGestureButton(int button)
        {
            Settings.GestureButton = button;
            _hook.RefreshSettings();
            UpdateChecks();
        }

        private void OnButtonCaptured(int button)
        {
            Settings.GestureButton = button;
            _hook.RefreshSettings();
            UpdateChecks();
            _tray.ShowBalloonTip(
                2500,
                "已识别侧键",
                "手势键已设为侧键 " + button + (button == 1 ? "（后退）" : "（前进）"),
                ToolTipIcon.Info);
        }

        private void ShowHelp()
        {
            MessageBox.Show(
                "鼠标操作\n" +
                "· 轻点侧键：打开 / 关闭任务视图（Win+Tab）\n" +
                "· 按住侧键左右滑动：直接切换到相邻虚拟桌面（不弹出任务视图）\n" +
                "· 按住期间指针会锁定在按下时的位置，滑动过程和切换后都不会移动\n" +
                "· 一次连续滑动只切换一个桌面，滑得再远也不会连续闪切\n" +
                "· 停下约 0.15 秒后再滑、或反向滑动，才会触发下一次\n" +
                "· 按住不松可以左右交替滑，连续切换多个桌面\n" +
                "· 上下移动或小幅抖动不会触发\n\n" +
                "托盘菜单\n" +
                "· 手势侧键：侧键 1（后退）或侧键 2（前进），也可“按下侧键来识别”\n" +
                "· 滑动识别距离：一次连续滑动至少移动多少像素才被识别，和滑动长短无关\n" +
                "· 反向：右滑改为切换到左侧桌面\n" +
                "· 开机自动启动：写入当前用户启动项\n\n" +
                "鼠标兼容性 / 为什么有时要改驱动里的按键\n" +
                "本工具识别的是 Windows 标准侧键事件（XBUTTON1/2）。绝大多数鼠标出厂即发送标准侧键，\n" +
                "插上就能用，不需要任何设置。只有当厂商驱动把侧键改成了宏、快捷键或私有手势时，\n" +
                "才需要在该驱动里恢复成“后退 (Back)”或“前进 (Forward)”。换普通鼠标通常什么都不用改。\n" +
                "罗技：请在 Logi Options+ 中把要用的侧键设为“后退”或“前进”，不要设为“无操作”或宏。\n" +
                "不需要卸载 Options+，但同一个侧键不要再绑定其它动作。\n\n" +
                "兼容性\n" +
                "适用于发送标准侧键事件的任意鼠标；Windows 10 / 11，需要 .NET Framework 4.8（系统自带）。\n" +
                "以管理员身份运行的程序获得焦点时，注入按键可能被系统拦截。",
                "MouseSwipe 使用说明",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
        }

        protected override void ExitThreadCore()
        {
            _hook.Dispose();
            _tray.Visible = false;
            _tray.Dispose();
            _icon.Dispose();
            base.ExitThreadCore();
        }

        private static Icon CreateTrayIcon()
        {
            using (Bitmap bitmap = new Bitmap(32, 32))
            {
                using (Graphics graphics = Graphics.FromImage(bitmap))
                {
                    graphics.SmoothingMode = SmoothingMode.AntiAlias;
                    graphics.Clear(Color.Transparent);

                    using (SolidBrush background = new SolidBrush(Color.FromArgb(37, 99, 235)))
                    {
                        graphics.FillEllipse(background, 0, 0, 31, 31);
                    }

                    using (Pen pen = new Pen(Color.White, 2.8f))
                    {
                        pen.StartCap = LineCap.Round;
                        pen.EndCap = LineCap.Round;
                        pen.LineJoin = LineJoin.Round;

                        graphics.DrawLine(pen, 21, 11, 9, 11);
                        graphics.DrawLine(pen, 9, 11, 13, 7);
                        graphics.DrawLine(pen, 9, 11, 13, 15);

                        graphics.DrawLine(pen, 11, 21, 23, 21);
                        graphics.DrawLine(pen, 23, 21, 19, 17);
                        graphics.DrawLine(pen, 23, 21, 19, 25);
                    }
                }

                IntPtr handle = bitmap.GetHicon();
                try
                {
                    using (Icon temporary = Icon.FromHandle(handle))
                    {
                        return (Icon)temporary.Clone();
                    }
                }
                finally
                {
                    DestroyIcon(handle);
                }
            }
        }

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool DestroyIcon(IntPtr handle);
    }

    /// <summary>
    /// Global low-level mouse hook. Tracks horizontal movement while the configured
    /// side button is held and swallows that button's events.
    /// </summary>
    internal sealed class MouseHook : IDisposable
    {
        private const int WH_MOUSE_LL = 14;
        private const int WM_MOUSEMOVE = 0x0200;
        private const int WM_XBUTTONDOWN = 0x020B;
        private const int WM_XBUTTONUP = 0x020C;

        private const int VK_TAB = 0x09;
        private const int VK_CONTROL = 0x11;
        private const int VK_LEFT = 0x25;
        private const int VK_RIGHT = 0x27;
        private const int VK_LWIN = 0x5B;

        private const uint INPUT_KEYBOARD = 1;
        private const uint KEYEVENTF_EXTENDEDKEY = 0x0001;
        private const uint KEYEVENTF_KEYUP = 0x0002;

        private readonly LowLevelMouseProc _procedure;

        private IntPtr _hookId = IntPtr.Zero;
        private int _gestureButton;
        private bool _invert;
        private readonly GestureTracker _tracker = new GestureTracker();

        private bool _held;
        private bool _swiped;
        private int _startX;
        private int _startY;

        public MouseHook()
        {
            _procedure = HookCallback;
            RefreshSettings();
        }

        public event Action<int> Captured;

        public bool CaptureNextButton { get; set; }

        public void RefreshSettings()
        {
            _gestureButton = Settings.GestureButton;
        }

        public bool Start()
        {
            if (_hookId != IntPtr.Zero)
            {
                return true;
            }

            _hookId = SetWindowsHookEx(WH_MOUSE_LL, _procedure, GetModuleHandle(null), 0);
            return _hookId != IntPtr.Zero;
        }

        public void Dispose()
        {
            if (_hookId != IntPtr.Zero)
            {
                UnhookWindowsHookEx(_hookId);
                _hookId = IntPtr.Zero;
            }
        }

        private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
        {
            if (nCode >= 0)
            {
                int message = wParam.ToInt32();

                if (message == WM_XBUTTONDOWN || message == WM_XBUTTONUP)
                {
                    MSLLHOOKSTRUCT info = (MSLLHOOKSTRUCT)Marshal.PtrToStructure(
                        lParam, typeof(MSLLHOOKSTRUCT));
                    int xButton = (int)(info.mouseData >> 16);

                    if (xButton == 1 || xButton == 2)
                    {
                        if (message == WM_XBUTTONDOWN)
                        {
                            if (CaptureNextButton)
                            {
                                CaptureNextButton = false;
                                Action<int> captured = Captured;
                                if (captured != null)
                                {
                                    captured(xButton);
                                }
                                return (IntPtr)1;
                            }

                            if (xButton == _gestureButton)
                            {
                                BeginGesture(info.pt);
                                return (IntPtr)1;
                            }
                        }
                        else if (xButton == _gestureButton && _held)
                        {
                            EndGesture();
                            return (IntPtr)1;
                        }

                        // Swallow a stray up event for our button too, so no app
                        // receives half of a back/forward click.
                        if (xButton == _gestureButton)
                        {
                            return (IntPtr)1;
                        }
                    }
                }
                else if (message == WM_MOUSEMOVE && _held)
                {
                    MSLLHOOKSTRUCT info = (MSLLHOOKSTRUCT)Marshal.PtrToStructure(
                        lParam, typeof(MSLLHOOKSTRUCT));
                    TrackMovement(info.pt);

                    // Swallow the move and keep the pointer at the gesture
                    // origin, so it stays visually still while the user swipes
                    // (like a driver-level gesture button).
                    return (IntPtr)1;
                }
            }

            return CallNextHookEx(_hookId, nCode, wParam, lParam);
        }

        private void BeginGesture(POINT point)
        {
            _held = true;
            _swiped = false;
            _startX = point.X;
            _startY = point.Y;
            _invert = Settings.Invert;
            _tracker.Begin(Math.Max(30, Settings.Threshold), Environment.TickCount);
        }

        private void TrackMovement(POINT point)
        {
            // The cursor is pinned at _start, so each event reports only the
            // movement since the previous event. Put the pointer back before
            // the system applies the move, then let the tracker turn the stream
            // into discrete continuous swipes.
            if (point.X != _startX || point.Y != _startY)
            {
                SetCursorPos(_startX, _startY);
            }

            int direction = _tracker.Move(
                point.X - _startX,
                point.Y - _startY,
                Environment.TickCount);

            if (direction == 0)
            {
                return;
            }

            bool toRight = direction > 0;
            if (_invert)
            {
                toRight = !toRight;
            }

            SendDesktopSwitch(toRight);
            _swiped = true;
        }

        private void EndGesture()
        {
            if (!_held)
            {
                return;
            }

            _held = false;
            bool swiped = _swiped;
            _swiped = false;

            if (swiped)
            {
                // Put the pointer back where the gesture started, so the new
                // desktop does not inherit the horizontal offset of the swipe.
                SetCursorPos(_startX, _startY);
            }
            else
            {
                // A swipe has already changed desktop; only a plain tap opens the
                // Task View. This also means a second tap closes it (Win+Tab toggles).
                SendWinTab();
            }
        }

        private static void SendWinTab()
        {
            INPUT[] sequence = new INPUT[]
            {
                Key(VK_LWIN, false, false),
                Key(VK_TAB, false, false),
                Key(VK_TAB, true, false),
                Key(VK_LWIN, true, false)
            };
            SendSequence(sequence);
        }

        private static void SendDesktopSwitch(bool toRight)
        {
            ushort arrow = (ushort)(toRight ? VK_RIGHT : VK_LEFT);
            INPUT[] sequence = new INPUT[]
            {
                Key(VK_CONTROL, false, false),
                Key(VK_LWIN, false, false),
                Key(arrow, false, true),
                Key(arrow, true, true),
                Key(VK_LWIN, true, false),
                Key(VK_CONTROL, true, false)
            };
            SendSequence(sequence);
        }

        private static void SendSequence(INPUT[] sequence)
        {
            SendInput((uint)sequence.Length, sequence, Marshal.SizeOf(typeof(INPUT)));
        }

        private static INPUT Key(ushort virtualKey, bool keyUp, bool extended)
        {
            INPUT input = new INPUT();
            input.type = INPUT_KEYBOARD;
            input.data.ki.wVk = virtualKey;
            input.data.ki.wScan = 0;
            input.data.ki.dwFlags =
                (keyUp ? KEYEVENTF_KEYUP : 0u) |
                (extended ? KEYEVENTF_EXTENDEDKEY : 0u);
            input.data.ki.time = 0;
            input.data.ki.dwExtraInfo = IntPtr.Zero;
            return input;
        }

        private delegate IntPtr LowLevelMouseProc(int nCode, IntPtr wParam, IntPtr lParam);

        [StructLayout(LayoutKind.Sequential)]
        private struct POINT
        {
            public int X;
            public int Y;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MSLLHOOKSTRUCT
        {
            public POINT pt;
            public uint mouseData;
            public uint flags;
            public uint time;
            public IntPtr dwExtraInfo;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MOUSEINPUT
        {
            public int dx;
            public int dy;
            public uint mouseData;
            public uint dwFlags;
            public uint time;
            public IntPtr dwExtraInfo;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct KEYBDINPUT
        {
            public ushort wVk;
            public ushort wScan;
            public uint dwFlags;
            public uint time;
            public IntPtr dwExtraInfo;
        }

        [StructLayout(LayoutKind.Explicit)]
        private struct INPUTUNION
        {
            [FieldOffset(0)]
            public MOUSEINPUT mi;

            [FieldOffset(0)]
            public KEYBDINPUT ki;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct INPUT
        {
            public uint type;
            public INPUTUNION data;
        }

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr SetWindowsHookEx(
            int idHook, LowLevelMouseProc callback, IntPtr moduleHandle, uint threadId);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool UnhookWindowsHookEx(IntPtr hookId);

        [DllImport("user32.dll")]
        private static extern IntPtr CallNextHookEx(
            IntPtr hookId, int nCode, IntPtr wParam, IntPtr lParam);

        [DllImport("kernel32.dll", CharSet = CharSet.Auto)]
        private static extern IntPtr GetModuleHandle(string moduleName);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern uint SendInput(
            uint inputCount, INPUT[] inputs, int inputSize);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool SetCursorPos(int x, int y);
    }
}
