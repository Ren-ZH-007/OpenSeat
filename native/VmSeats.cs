using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows.Forms;
using Microsoft.Win32;

namespace OpenSeatNative {
    // 虚拟机工位：一个工位使用主机 Windows，另外两个工位各运行一台 VMware 虚拟机，
    // 通过 USB 直通让每台虚拟机独占自己的键盘和鼠标，从而可以运行任意软件、互不干扰。
    public static class VmPlan {
        public struct UsbId { public ushort Vid, Pid; }

        public static bool TryParseUsb(string deviceId, out UsbId id) {
            id = new UsbId();
            if (String.IsNullOrEmpty(deviceId) || IsBluetooth(deviceId)) return false;
            Match m = Regex.Match(deviceId, @"VID_([0-9A-Fa-f]{4}).*?PID_([0-9A-Fa-f]{4})", RegexOptions.IgnoreCase);
            if (!m.Success) return false;
            id.Vid = Convert.ToUInt16(m.Groups[1].Value, 16);
            id.Pid = Convert.ToUInt16(m.Groups[2].Value, 16);
            return true;
        }
        public static bool IsBluetooth(string deviceId) {
            return deviceId != null && (deviceId.IndexOf("00001124-0000-1000-8000-00805f9b34fb", StringComparison.OrdinalIgnoreCase) >= 0
                || deviceId.IndexOf("{00001812-", StringComparison.OrdinalIgnoreCase) >= 0
                || deviceId.IndexOf("BTHLE", StringComparison.OrdinalIgnoreCase) >= 0
                || deviceId.IndexOf("BTHENUM", StringComparison.OrdinalIgnoreCase) >= 0);
        }
        public static string Format(UsbId id) { return "vid:0x" + id.Vid.ToString("x4") + " pid:0x" + id.Pid.ToString("x4"); }

        // 检查方案；返回空字符串表示可以执行，否则返回第一条中文错误说明。
        public static string Check(Settings config) {
            int hosts = 0;
            var owner = new Dictionary<string, string>();
            foreach (Seat s in config.Active()) {
                if (s.VmxPath == "") { hosts++; }
                else {
                    if (s.Mouse == "") return s.Name + "：虚拟机工位必须先绑定鼠标。";
                }
                foreach (string device in new string[] { s.Mouse, s.Keyboard }) {
                    if (device == "") continue;
                    if (IsBluetooth(device) && s.VmxPath != "") return s.Name + "：蓝牙设备无法 USB 直通给虚拟机，请改用 USB 接收器或有线设备。";
                    UsbId id;
                    if (!TryParseUsb(device, out id)) {
                        if (s.VmxPath != "") return s.Name + "：无法从设备路径读出 VID/PID，不能配置直通。";
                        continue;
                    }
                    string key = Format(id), other;
                    if (owner.TryGetValue(key, out other) && other != s.Name)
                        return s.Name + " 和 " + other + " 的设备型号相同（" + Native.DeviceLabel(device) + "）。VMware 按型号直通，无法区分同型号设备，请换用不同型号。";
                    owner[key] = s.Name;
                }
            }
            if (hosts != 1) return "必须正好有一个工位留空虚拟机文件（作为主机工位），其余 " + (config.Active().Length - 1) + " 个工位各选一台虚拟机。";
            var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (Seat s in config.Active()) if (s.VmxPath != "" && !paths.Add(s.VmxPath)) return "两个工位不能使用同一台虚拟机。";
            return "";
        }

        public static List<UsbId> DevicesFor(Seat seat) {
            var list = new List<UsbId>();
            foreach (string device in new string[] { seat.Mouse, seat.Keyboard }) {
                UsbId id;
                if (device != "" && TryParseUsb(device, out id) && !list.Exists(delegate(UsbId x) { return x.Vid == id.Vid && x.Pid == id.Pid; })) list.Add(id);
            }
            return list;
        }

        static readonly Regex ManagedKey = new Regex(@"^\s*(usb\.present|usb\.generic\.allowHID|usb\.generic\.allowLastHID|usb\.autoConnect\.device\d+)\s*=", RegexOptions.IgnoreCase);

        // 纯文本变换：移除旧的 USB 直通相关行，写入本工位设备。不启用 allowLastHID，避免抢走主机最后一套键鼠。
        public static string ApplyUsb(string vmx, List<UsbId> devices) {
            string newline = vmx.Contains("\r\n") ? "\r\n" : "\n";
            var lines = new List<string>();
            foreach (string line in vmx.Replace("\r\n", "\n").Split('\n')) if (!ManagedKey.IsMatch(line)) lines.Add(line);
            while (lines.Count > 0 && lines[lines.Count - 1].Trim() == "") lines.RemoveAt(lines.Count - 1);
            lines.Add("usb.present = \"TRUE\"");
            lines.Add("usb.generic.allowHID = \"TRUE\"");
            for (int i = 0; i < devices.Count; i++) lines.Add("usb.autoConnect.device" + i + " = \"" + Format(devices[i]) + "\"");
            return String.Join(newline, lines.ToArray()) + newline;
        }

        public static string DisplayName(string vmx, string fallback) {
            Match m = Regex.Match(vmx, "^\\s*displayName\\s*=\\s*\"([^\"]*)\"", RegexOptions.IgnoreCase | RegexOptions.Multiline);
            return m.Success && m.Groups[1].Value.Trim() != "" ? m.Groups[1].Value : fallback;
        }

        // vmware.exe：-n 新窗口，-x 开机（不直接全屏，便于先把窗口移到目标屏幕）。
        public static string LaunchArguments(string vmxPath) { return "-n -x \"" + vmxPath + "\""; }
        public static bool IsRunning(string vmxPath) { return Directory.Exists(vmxPath + ".lck"); }

        public static void WriteVmx(string vmxPath, List<UsbId> devices) {
            if (!File.Exists(vmxPath)) throw new FileNotFoundException("找不到虚拟机文件：" + vmxPath);
            if (IsRunning(vmxPath)) throw new InvalidOperationException("虚拟机正在运行或未正常关闭（存在 .lck 文件夹）。请先在 VMware 中关机后再写入。");
            string text = File.ReadAllText(vmxPath, Encoding.UTF8);
            string backup = vmxPath + ".openseat-backup";
            if (!File.Exists(backup)) File.Copy(vmxPath, backup);
            string temp = vmxPath + ".openseat.tmp";
            File.WriteAllText(temp, ApplyUsb(text, devices), new UTF8Encoding(false));
            File.Replace(temp, vmxPath, null);
        }

        public static string FindVmware(string configured) {
            if (!String.IsNullOrEmpty(configured) && File.Exists(configured)) return configured;
            foreach (string key in new string[] { @"SOFTWARE\WOW6432Node\VMware, Inc.\VMware Workstation", @"SOFTWARE\VMware, Inc.\VMware Workstation" }) {
                try {
                    using (RegistryKey k = Registry.LocalMachine.OpenSubKey(key)) {
                        string dir = k == null ? null : k.GetValue("InstallPath") as string;
                        if (!String.IsNullOrEmpty(dir) && File.Exists(Path.Combine(dir, "vmware.exe"))) return Path.Combine(dir, "vmware.exe");
                    }
                } catch { }
            }
            foreach (Environment.SpecialFolder f in new Environment.SpecialFolder[] { Environment.SpecialFolder.ProgramFilesX86, Environment.SpecialFolder.ProgramFiles }) {
                string p = Path.Combine(Environment.GetFolderPath(f), @"VMware\VMware Workstation\vmware.exe");
                if (File.Exists(p)) return p;
            }
            return null;
        }
    }

    public static class WindowTools {
        public delegate bool EnumProc(IntPtr hwnd, IntPtr lParam);
        [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
        [DllImport("user32.dll")] static extern bool EnumWindows(EnumProc proc, IntPtr lParam);
        [DllImport("user32.dll", CharSet=CharSet.Unicode)] static extern int GetWindowText(IntPtr hwnd, StringBuilder text, int max);
        [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr hwnd);
        [DllImport("user32.dll")] static extern bool ShowWindow(IntPtr hwnd, int cmd);
        [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int w, int h, uint flags);
        [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hwnd);
        [DllImport("user32.dll")] static extern bool ClipCursor(ref RECT rect);
        [DllImport("user32.dll", EntryPoint="ClipCursor")] static extern bool ClipCursorNull(IntPtr rect);
        [DllImport("user32.dll", SetLastError=true)] public static extern bool RegisterHotKey(IntPtr hwnd, int id, uint modifiers, uint key);
        [DllImport("user32.dll")] public static extern bool UnregisterHotKey(IntPtr hwnd, int id);

        public static IntPtr FindByTitle(string part) {
            IntPtr found = IntPtr.Zero;
            EnumWindows(delegate(IntPtr h, IntPtr l) {
                if (!IsWindowVisible(h)) return true;
                var b = new StringBuilder(512); GetWindowText(h, b, b.Capacity);
                string title = b.ToString();
                if (title.IndexOf(part, StringComparison.OrdinalIgnoreCase) >= 0 && title.IndexOf("VMware", StringComparison.OrdinalIgnoreCase) >= 0) { found = h; return false; }
                return true;
            }, IntPtr.Zero);
            return found;
        }
        public static void MoveAndMaximize(IntPtr hwnd, Rectangle area) {
            ShowWindow(hwnd, 9); // SW_RESTORE，先还原才能移到其他显示器
            SetWindowPos(hwnd, IntPtr.Zero, area.X, area.Y, area.Width, area.Height, 0x0004 | 0x0040); // NOZORDER | SHOWWINDOW
            ShowWindow(hwnd, 3); // SW_MAXIMIZE
        }
        public static void Clip(Rectangle r) { var rect = new RECT { Left=r.Left, Top=r.Top, Right=r.Right, Bottom=r.Bottom }; ClipCursor(ref rect); }
        public static void Unclip() { ClipCursorNull(IntPtr.Zero); }
    }

    public class VmSeatsWindow : Form {
        readonly Settings config; readonly Action markDirty;
        readonly TextBox[] paths = new TextBox[3]; readonly Label[] notes = new Label[3];
        readonly TextBox vmwarePath = new TextBox();
        readonly Label status = new Label();
        readonly CheckBox lockBox = new CheckBox();
        readonly Timer lockTimer = new Timer();
        const int HotKeyId = 0x4F53;
        public VmSeatsWindow(Settings settings, Action onChanged) {
            config = settings; markDirty = onChanged;
            Text = "虚拟机工位（VMware Workstation · " + settings.Layout() + "）"; Size = new Size(900, 560); MinimumSize = new Size(760, 520);
            Font = new Font("Microsoft YaHei UI", 10); StartPosition = FormStartPosition.CenterParent;
            var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(16), ColumnCount = 1, RowCount = 6 };
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 66)); layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 230));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 44)); layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 48));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 40)); layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            Controls.Add(layout);
            layout.Controls.Add(new Label { Dock = DockStyle.Fill, Text = "一个工位留空作为主机工位（使用本机 Windows），其余工位各选一台已关机的 VMware 虚拟机（.vmx）。双屏时只需一台虚拟机。\nOpenSeat 会把该工位绑定的键鼠写成 USB 直通，虚拟机开机后独占这套键鼠。" }, 0, 0);
            var grid = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 4, RowCount = 3 };
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 120)); grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 90)); grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 90));
            int seats = config.Active().Length; // 窗口打开期间工位数量不变；主窗口切换数量时会先关闭本窗口
            for (int i = 0; i < seats; i++) {
                int seat = i;
                grid.RowStyles.Add(new RowStyle(SizeType.Percent, 100f / seats));
                grid.Controls.Add(new Label { Text = config.Seats[i].Name, AutoSize = true, Font = new Font(Font, FontStyle.Bold), Margin = new Padding(3, 8, 3, 3) }, 0, i);
                var cell = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false };
                paths[i] = new TextBox { Width = 470, ReadOnly = true, Text = config.Seats[i].VmxPath };
                notes[i] = new Label { AutoSize = true, ForeColor = Color.DimGray };
                cell.Controls.Add(paths[i]); cell.Controls.Add(notes[i]); grid.Controls.Add(cell, 1, i);
                grid.Controls.Add(Button("选择…", delegate { Browse(seat); }), 2, i);
                grid.Controls.Add(Button("设为主机", delegate { config.Seats[seat].VmxPath = ""; markDirty(); UpdateNotes(); }), 3, i);
            }
            layout.Controls.Add(grid, 0, 1);
            var vm = new FlowLayoutPanel { Dock = DockStyle.Fill };
            vm.Controls.Add(new Label { Text = "vmware.exe：", AutoSize = true, Margin = new Padding(3, 8, 3, 3) });
            vmwarePath.Width = 520; vmwarePath.ReadOnly = true; vm.Controls.Add(vmwarePath);
            vm.Controls.Add(Button("手动指定…", BrowseVmware));
            layout.Controls.Add(vm, 0, 2);
            var actions = new FlowLayoutPanel { Dock = DockStyle.Fill };
            actions.Controls.Add(Button("① 写入 USB 直通配置", WriteAll));
            actions.Controls.Add(Button("② 启动虚拟机工位", LaunchAll));
            actions.Controls.Add(Button("还原 .vmx 备份", RestoreAll));
            layout.Controls.Add(actions, 0, 3);
            lockBox.Text = "把主机鼠标锁定在主机工位屏幕（Ctrl+Alt+F12 随时切换）"; lockBox.AutoSize = true;
            lockBox.CheckedChanged += delegate { ApplyLock(); };
            layout.Controls.Add(lockBox, 0, 4);
            status.Dock = DockStyle.Fill; layout.Controls.Add(status, 0, 5);
            lockTimer.Interval = 250; lockTimer.Tick += delegate { if (lockBox.Checked) ClipToHost(); };
            Shown += delegate {
                WindowTools.RegisterHotKey(Handle, HotKeyId, 0x0001 | 0x0002 | 0x4000, (uint)Keys.F12); // Alt|Ctrl|NOREPEAT
                UpdateNotes();
            };
            FormClosed += delegate { lockBox.Checked = false; WindowTools.UnregisterHotKey(Handle, HotKeyId); lockTimer.Dispose(); };
        }
        Button Button(string text, Action action) { var b = new Button { Text = text, AutoSize = true, Height = 34 }; b.Click += delegate { try { action(); } catch (Exception ex) { status.Text = "失败：" + ex.Message; MessageBox.Show(this, ex.Message, "OpenSeat", MessageBoxButtons.OK, MessageBoxIcon.Error); } }; return b; }
        protected override void WndProc(ref Message m) {
            if (m.Msg == 0x0312 && m.WParam.ToInt32() == HotKeyId) lockBox.Checked = !lockBox.Checked;
            base.WndProc(ref m);
        }
        void UpdateNotes() {
            string exe = VmPlan.FindVmware(config.VmwarePath);
            vmwarePath.Text = exe ?? "未找到。请安装 VMware Workstation Pro，或点“手动指定…”";
            for (int i = 0; i < config.Active().Length; i++) {
                Seat s = config.Seats[i]; paths[i].Text = s.VmxPath == "" ? "（主机工位：使用本机 Windows）" : s.VmxPath;
                if (s.VmxPath == "") { notes[i].Text = "屏幕：" + Native.MonitorLabel(s.Monitor); continue; }
                var devices = VmPlan.DevicesFor(s); var parts = new List<string>();
                foreach (var d in devices) parts.Add(VmPlan.Format(d));
                notes[i].Text = "屏幕：" + Native.MonitorLabel(s.Monitor) + "　直通：" + (parts.Count == 0 ? "（未绑定键鼠）" : String.Join("，", parts.ToArray()))
                    + (File.Exists(s.VmxPath) ? (VmPlan.IsRunning(s.VmxPath) ? "　[运行中]" : "") : "　[文件不存在]");
            }
            string problem = VmPlan.Check(config);
            status.Text = problem == "" ? "方案检查通过。先点 ①（虚拟机须关机），再点 ②。" : "方案检查：" + problem;
        }
        void Browse(int seat) {
            using (var d = new OpenFileDialog { Filter = "VMware 虚拟机|*.vmx", Title = "选择 " + config.Seats[seat].Name + " 的虚拟机" })
                if (d.ShowDialog(this) == DialogResult.OK) { config.Seats[seat].VmxPath = d.FileName; markDirty(); UpdateNotes(); }
        }
        void BrowseVmware() {
            using (var d = new OpenFileDialog { Filter = "vmware.exe|vmware.exe" })
                if (d.ShowDialog(this) == DialogResult.OK) { config.VmwarePath = d.FileName; markDirty(); UpdateNotes(); }
        }
        void Require() { string problem = VmPlan.Check(config); if (problem != "") throw new InvalidOperationException(problem); }
        void WriteAll() {
            Require();
            foreach (Seat s in config.Active()) if (s.VmxPath != "") VmPlan.WriteVmx(s.VmxPath, VmPlan.DevicesFor(s));
            UpdateNotes(); status.Text = "已写入 USB 直通配置（原文件备份为 .vmx.openseat-backup）。下一步点 ②。";
        }
        void RestoreAll() {
            int restored = 0;
            foreach (Seat s in config.Active()) {
                if (s.VmxPath == "" || !File.Exists(s.VmxPath + ".openseat-backup")) continue;
                if (VmPlan.IsRunning(s.VmxPath)) throw new InvalidOperationException(s.Name + " 的虚拟机正在运行，请先关机。");
                File.Copy(s.VmxPath + ".openseat-backup", s.VmxPath, true); restored++;
            }
            status.Text = restored == 0 ? "没有可还原的备份。" : "已还原 " + restored + " 个 .vmx 文件。";
        }
        void LaunchAll() {
            Require();
            string exe = VmPlan.FindVmware(config.VmwarePath);
            if (exe == null) throw new InvalidOperationException("未找到 vmware.exe。请安装 VMware Workstation Pro，或手动指定。");
            foreach (Seat s in config.Active()) {
                if (s.VmxPath == "") continue;
                if (ScreenCapture.Find(s.Monitor) == null) throw new InvalidOperationException(s.Name + " 未分配在线显示器。");
                if (!File.Exists(s.VmxPath)) throw new FileNotFoundException(s.Name + " 的虚拟机文件不存在。");
            }
            foreach (Seat s in config.Active()) {
                if (s.VmxPath == "") continue;
                string title = VmPlan.DisplayName(File.ReadAllText(s.VmxPath, Encoding.UTF8), Path.GetFileNameWithoutExtension(s.VmxPath));
                if (!VmPlan.IsRunning(s.VmxPath)) Process.Start(new ProcessStartInfo(exe, VmPlan.LaunchArguments(s.VmxPath)) { UseShellExecute = false });
                Place(title, ScreenCapture.Find(s.Monitor).Bounds, s.Name);
            }
            status.Text = "正在启动虚拟机并移动到各自屏幕…";
        }
        void Place(string title, Rectangle bounds, string seatName) {
            var timer = new Timer { Interval = 500 }; DateTime until = DateTime.UtcNow.AddSeconds(60);
            timer.Tick += delegate {
                IntPtr h = WindowTools.FindByTitle(title);
                if (h != IntPtr.Zero) {
                    timer.Stop(); timer.Dispose();
                    WindowTools.MoveAndMaximize(h, bounds);
                    WindowTools.SetForegroundWindow(Handle); // 把主机键盘焦点拿回主机工位
                    status.Text = seatName + " 的虚拟机窗口已放到目标屏幕并最大化。如需全屏：用主机工位的鼠标点该窗口菜单“查看 → 全屏”，完成后再锁定主机鼠标。";
                } else if (DateTime.UtcNow > until) { timer.Stop(); timer.Dispose(); status.Text = "60 秒内没找到“" + title + "”的 VMware 窗口，请手动把它拖到对应屏幕。"; }
            };
            timer.Start();
        }
        void ClipToHost() {
            foreach (Seat s in config.Active()) if (s.VmxPath == "") { Screen screen = ScreenCapture.Find(s.Monitor); if (screen != null) WindowTools.Clip(screen.Bounds); return; }
        }
        void ApplyLock() {
            if (lockBox.Checked) {
                Seat host = Array.Find(config.Active(), delegate(Seat s) { return s.VmxPath == ""; });
                if (host == null || ScreenCapture.Find(host.Monitor) == null) { lockBox.Checked = false; status.Text = "主机工位未分配在线显示器，无法锁定鼠标。"; return; }
                ClipToHost(); lockTimer.Start(); status.Text = "主机鼠标已锁定在 " + host.Name + " 屏幕。按 Ctrl+Alt+F12 解除。";
            } else { lockTimer.Stop(); WindowTools.Unclip(); status.Text = "主机鼠标已解除锁定。"; }
        }
    }
}
