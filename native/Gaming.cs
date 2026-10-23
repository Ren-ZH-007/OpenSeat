using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using System.Xml;
using System.Xml.Serialization;

namespace OpenSeatGaming {
    public class GameProfile {
        public string Name = "";
        public string Executable = "";
        public string Arguments = "";
    }
    public class GamingConfig {
        public int Version = 1;
        public int Seat = 1;
        public string Monitor = "";
        public string EngineConsole = "";
        public GameProfile[] Games = new GameProfile[] {
            new GameProfile { Name = "英雄联盟（国服）" },
            new GameProfile { Name = "三角洲行动（国服）" },
            new GameProfile { Name = "穿越火线（国服）" }
        };
        public void Validate() {
            if (Version != 1 || Seat < 1 || Seat > 3 || Games == null || Games.Length != 3)
                throw new InvalidDataException("必须使用版本 1 的三工位游戏配置。");
            if (Monitor == null || EngineConsole == null) throw new InvalidDataException("配置字段为空。");
            foreach (GameProfile g in Games) {
                if (g == null || String.IsNullOrWhiteSpace(g.Name) || g.Name.Length > 80 || g.Executable == null || g.Arguments == null)
                    throw new InvalidDataException("游戏配置字段无效。");
                if (g.Arguments.Length > 4096 || g.Arguments.IndexOfAny(new char[] {'\0', '\r', '\n'}) >= 0)
                    throw new InvalidDataException("启动参数过长或包含换行。");
                ValidatePath(g.Executable);
            }
            ValidatePath(EngineConsole);
        }
        public static void ValidatePath(string path) {
            if (path == "") return;
            if (path.Length > 240 || path.IndexOfAny(new char[] {'\0', '\r', '\n', '"'}) >= 0 ||
                !Path.IsPathRooted(path) || !String.Equals(Path.GetExtension(path), ".exe", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("请选择完整的 EXE 文件路径，不支持命令、脚本或网址。");
        }
        public static GamingConfig Load(string path) {
            // DTD and external entities are never resolved from user-editable XML.
            using (XmlReader reader = XmlReader.Create(path, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 65536 })) {
                var value = (GamingConfig)new XmlSerializer(typeof(GamingConfig)).Deserialize(reader);
                if (value == null) throw new InvalidDataException("配置为空。");
                value.Validate(); return value;
            }
        }
        public void Save(string path) {
            Validate(); path = Path.GetFullPath(path);
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            string temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try {
                using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None)) {
                    new XmlSerializer(typeof(GamingConfig)).Serialize(stream, this); stream.Flush(true);
                }
                if (File.Exists(path)) File.Replace(temp, path, null); else File.Move(temp, path);
            } finally { if (File.Exists(temp)) File.Delete(temp); }
        }
    }
    public class SessionInfo {
        public int Id;
        public string Station, State;
        public bool Active;
    }
    public static class Sessions {
        [StructLayout(LayoutKind.Sequential)] struct Entry { public int Id; public IntPtr Station; public int State; }
        [DllImport("wtsapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern bool WTSEnumerateSessions(IntPtr server, int reserved, int version, out IntPtr data, out int count);
        [DllImport("wtsapi32.dll")] static extern void WTSFreeMemory(IntPtr data);
        public static List<SessionInfo> Read() {
            IntPtr data; int count;
            if (!WTSEnumerateSessions(IntPtr.Zero, 0, 1, out data, out count)) throw new Win32Exception();
            try {
                var result = new List<SessionInfo>(); int stride = Marshal.SizeOf(typeof(Entry));
                string[] states = { "活动", "已连接", "连接查询", "影子", "已断开", "空闲", "监听", "重置", "关闭", "初始化" };
                for (int i = 0; i < count; i++) {
                    var e = (Entry)Marshal.PtrToStructure(IntPtr.Add(data, i * stride), typeof(Entry));
                    if (e.Id == 0 || e.State == 6) continue;
                    result.Add(new SessionInfo { Id = e.Id, Station = Marshal.PtrToStringUni(e.Station) ?? "", Active = e.State == 0,
                        State = e.State >= 0 && e.State < states.Length ? states[e.State] : "未知" });
                }
                return result;
            } finally { WTSFreeMemory(data); }
        }
    }
    public static class Launcher {
        public static ProcessStartInfo Prepare(GameProfile game) {
            if (game == null) throw new InvalidDataException("未选择游戏。");
            var config = new GamingConfig(); config.Games[0] = game; config.Validate();
            if (game.Executable == "" || !File.Exists(game.Executable)) throw new FileNotFoundException("请先选择已安装的游戏或官方启动器 EXE。");
            return new ProcessStartInfo(game.Executable, game.Arguments) {
                WorkingDirectory = Path.GetDirectoryName(game.Executable), UseShellExecute = false
            };
        }
        // Match only the process started here. Do not guess by game title or inject into games.
        [DllImport("user32.dll", SetLastError = true)] static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int w, int height, uint flags);
        [DllImport("user32.dll")] static extern bool ShowWindow(IntPtr h, int command);
        public static void Move(Process process, Screen screen) {
            if (screen == null) throw new InvalidOperationException("工位显示器已离线，请重新选择。");
            if (process == null || process.HasExited) throw new InvalidOperationException("所启动的进程已退出；请在游戏设置中选择显示器。");
            process.Refresh(); IntPtr h = process.MainWindowHandle;
            if (h == IntPtr.Zero) throw new InvalidOperationException("该进程还没有主窗口；若启动器已转交游戏，请在游戏中选择显示器。");
            ShowWindow(h, 9);
            Rectangle r = screen.WorkingArea;
            if (!SetWindowPos(h, IntPtr.Zero, r.X, r.Y, r.Width, r.Height, 0x0014)) throw new Win32Exception();
        }
    }
    public class GamingWindow : Form {
        readonly string configPath;
        GamingConfig config;
        readonly NumericUpDown seat = new NumericUpDown { Minimum = 1, Maximum = 3, Width = 60 };
        readonly ComboBox monitors = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 350 };
        readonly TextBox engine = new TextBox { ReadOnly = true, Width = 420 };
        readonly TextBox[] executables = new TextBox[3], arguments = new TextBox[3];
        readonly Label[] states = new Label[3];
        readonly Process[] processes = new Process[3];
        readonly ListView sessionList = new ListView { View = View.Details, FullRowSelect = true, Height = 110, Dock = DockStyle.Fill };
        readonly Label status = new Label { AutoSize = true, MaximumSize = new Size(850, 0) };
        readonly System.Windows.Forms.Timer timer = new System.Windows.Forms.Timer { Interval = 2000 };
        readonly List<string> monitorIds = new List<string>();
        bool dirty, loading = true, corrupt;
        public GamingWindow() {
            configPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OpenSeat", "native-gaming.xml");
            config = new GamingConfig(); string loadError = "";
            if (File.Exists(configPath)) {
                try { config = GamingConfig.Load(configPath); }
                catch (Exception ex) { corrupt = true; loadError = "配置读取失败，原文件已保留。请先修复或备份移走文件，再重新打开：" + ex.Message; }
            }
            Text = "OpenSeat · 原生工位游戏启动器（外部引擎模式）";
            Font = new Font("Microsoft YaHei UI", 10); Size = new Size(940, 820); MinimumSize = new Size(800, 650);
            AutoScaleMode = AutoScaleMode.Dpi; StartPosition = FormStartPosition.CenterScreen;
            var root = new TableLayoutPanel { Dock = DockStyle.Fill, AutoScroll = true, ColumnCount = 1, Padding = new Padding(16) };
            Controls.Add(root);
            Add(root, new Label { AutoSize = true, MaximumSize = new Size(850, 0), Text = "此程序在当前 Windows 会话内启动游戏。独立桌面、显卡输出和键鼠独占须先由外部多工位引擎配置。\n外部引擎状态：未验证；检测到多个会话也不代表设备已隔离。三款游戏兼容性均未验证。" });
            var identity = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill };
            identity.Controls.Add(new Label { AutoSize = true, Text = "工位编号（标签）" }); identity.Controls.Add(seat);
            identity.Controls.Add(new Label { AutoSize = true, Text = "本会话窗口目标屏幕" }); identity.Controls.Add(monitors);
            Add(root, identity);
            var engineRow = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill };
            engineRow.Controls.Add(engine); engineRow.Controls.Add(Button("选择引擎控制台…", ChooseEngine));
            engineRow.Controls.Add(Button("打开控制台", OpenEngine)); Add(root, engineRow);
            Add(root, new Label { AutoSize = true, Text = "仅打开你指定的外部控制台，不自动配置 ASTER，不包含其许可证或驱动。" });
            for (int i = 0; i < 3; i++) {
                int index = i;
                var box = new GroupBox { Text = config.Games[i].Name, Dock = DockStyle.Top, Height = 125 };
                var grid = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, RowCount = 3, Padding = new Padding(8) };
                grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 80)); grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 120));
                executables[i] = new TextBox { Dock = DockStyle.Fill, ReadOnly = true, Text = config.Games[i].Executable };
                arguments[i] = new TextBox { Dock = DockStyle.Fill, Text = config.Games[i].Arguments };
                states[i] = new Label { AutoSize = true, Text = "未由本程序启动" };
                grid.Controls.Add(new Label { Text = "启动 EXE", AutoSize = true }, 0, 0); grid.Controls.Add(executables[i], 1, 0); grid.Controls.Add(Button("选择…", delegate { ChooseGame(index); }), 2, 0);
                grid.Controls.Add(new Label { Text = "参数", AutoSize = true }, 0, 1); grid.Controls.Add(arguments[i], 1, 1); grid.Controls.Add(Button("启动", delegate { StartGame(index); }), 2, 1);
                grid.Controls.Add(states[i], 1, 2); grid.Controls.Add(Button("定位启动窗口", delegate { MoveGame(index); }), 2, 2);
                arguments[i].TextChanged += delegate { MarkDirty(); }; box.Controls.Add(grid); Add(root, box);
            }
            sessionList.Columns.Add("会话 ID", 90); sessionList.Columns.Add("连接名称", 190); sessionList.Columns.Add("状态", 100); sessionList.Columns.Add("当前会话", 100);
            Add(root, sessionList);
            var actions = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill };
            actions.Controls.Add(Button("保存配置", Save)); actions.Controls.Add(Button("刷新会话和屏幕", RefreshInventory));
            actions.Controls.Add(Button("打开配置和日志目录", delegate { Directory.CreateDirectory(Path.GetDirectoryName(configPath)); Process.Start(Path.GetDirectoryName(configPath)); }));
            Add(root, actions); Add(root, status);
            seat.Value = config.Seat; engine.Text = config.EngineConsole; PopulateMonitors(config.Monitor);
            seat.ValueChanged += delegate { MarkDirty(); }; monitors.SelectedIndexChanged += delegate { MarkDirty(); };
            loading = false;
            RefreshSessions(); status.Text = loadError != "" ? loadError : "当前用户：" + Environment.UserName + "；会话 " + CurrentSession + "。每个独立用户会话中分别启动本程序。";
            timer.Tick += delegate { RefreshProcesses(); }; timer.Start();
            FormClosing += ClosingWindow;
            FormClosed += delegate { timer.Dispose(); foreach (Process p in processes) if (p != null) p.Dispose(); };
        }
        static int CurrentSession { get { using (Process p = Process.GetCurrentProcess()) return p.SessionId; } }
        void Add(TableLayoutPanel root, Control control) { root.RowCount++; root.RowStyles.Add(new RowStyle(SizeType.AutoSize)); control.Margin = new Padding(0, 0, 0, 10); root.Controls.Add(control, 0, root.RowCount - 1); }
        Button Button(string text, Action action) {
            var button = new Button { Text = text, AutoSize = true };
            button.Click += delegate { try { action(); } catch (Exception ex) { status.Text = ex.Message; Log("操作失败：" + ex.GetType().Name + " " + ex.Message); MessageBox.Show(this, ex.Message, "OpenSeat", MessageBoxButtons.OK, MessageBoxIcon.Warning); } };
            return button;
        }
        void MarkDirty() { if (!loading) dirty = true; }
        string ChooseExe() { using (var d = new OpenFileDialog { Filter = "应用程序|*.exe", CheckFileExists = true }) return d.ShowDialog(this) == DialogResult.OK ? d.FileName : null; }
        void ChooseGame(int i) { string path = ChooseExe(); if (path != null) { executables[i].Text = path; MarkDirty(); } }
        void ChooseEngine() { string path = ChooseExe(); if (path != null) { engine.Text = path; MarkDirty(); } }
        void OpenEngine() {
            var p = new GameProfile { Name = "外部引擎控制台", Executable = engine.Text };
            // Shell allows a vendor's own manifest/UAC to request elevation. No elevation for games.
            ProcessStartInfo start = Launcher.Prepare(p); start.UseShellExecute = true;
            using (Process process = Process.Start(start)) { }
            status.Text = "已请求打开外部控制台；工位隔离状态仍未验证。";
        }
        GamingConfig ReadConfig() {
            var value = new GamingConfig { Seat = (int)seat.Value, Monitor = monitors.SelectedIndex < 0 ? "" : monitorIds[monitors.SelectedIndex], EngineConsole = engine.Text };
            for (int i = 0; i < 3; i++) value.Games[i] = new GameProfile { Name = config.Games[i].Name, Executable = executables[i].Text, Arguments = arguments[i].Text };
            value.Validate(); return value;
        }
        void Save() {
            if (corrupt) throw new InvalidOperationException("原配置损坏，禁止覆盖。请先备份并修复或移走 native-gaming.xml，再重新打开程序。");
            GamingConfig next = ReadConfig(); next.Save(configPath); config = next; dirty = false; status.Text = "配置已保存到当前用户。工位编号只作标签，不会创建独立会话。";
        }
        void StartGame(int i) {
            if (!Environment.UserInteractive || CurrentSession == 0) throw new InvalidOperationException("游戏必须在已登录的交互会话内启动。");
            RefreshProcesses();
            if (processes[i] != null) throw new InvalidOperationException("本次启动的进程仍在运行。请在已有启动器中操作。");
            GamingConfig next = ReadConfig(); ProcessStartInfo start = Launcher.Prepare(next.Games[i]);
            Process p = Process.Start(start);
            if (p == null) throw new InvalidOperationException("未取得启动进程。");
            processes[i] = p; states[i].Text = "启动进程 PID " + p.Id + "（未确认进入游戏）";
            status.Text = "已在当前会话启动。若出现官方登录器，请手动登录。不会跨会话启动或注入游戏。";
            Log("工位 " + next.Seat + " 会话 " + CurrentSession + " 启动 " + next.Games[i].Name + " PID " + p.Id);
        }
        void MoveGame(int i) {
            string id = ReadConfig().Monitor; Screen target = null;
            foreach (Screen screen in Screen.AllScreens) if (screen.DeviceName == id) target = screen;
            Launcher.Move(processes[i], target); status.Text = "已移动所启动进程的窗口。游戏另开进程或独占全屏时，请使用游戏自身的显示设置。";
        }
        void RefreshProcesses() {
            for (int i = 0; i < 3; i++) if (processes[i] != null) {
                try {
                    if (!processes[i].HasExited) continue;
                    states[i].Text = "启动进程已退出（退出码 " + processes[i].ExitCode + "），游戏状态未知";
                    processes[i].Dispose(); processes[i] = null;
                } catch (InvalidOperationException) { processes[i].Dispose(); processes[i] = null; states[i].Text = "进程状态不可用"; }
                catch (Win32Exception) { states[i].Text = "无法读取进程状态"; }
            }
        }
        void PopulateMonitors(string selected) {
            bool wasLoading = loading; loading = true;
            try {
                monitors.Items.Clear(); monitorIds.Clear();
                foreach (Screen screen in Screen.AllScreens) {
                    monitorIds.Add(screen.DeviceName); monitors.Items.Add(screen.DeviceName + " · " + screen.Bounds.Width + "×" + screen.Bounds.Height);
                }
                if (selected != "" && !monitorIds.Contains(selected)) { monitorIds.Add(selected); monitors.Items.Add(selected + " · 离线"); }
                monitors.SelectedIndex = monitorIds.IndexOf(selected);
            } finally { loading = wasLoading; }
        }
        void RefreshInventory() { string selected = monitors.SelectedIndex < 0 ? "" : monitorIds[monitors.SelectedIndex]; PopulateMonitors(selected); RefreshSessions(); status.Text = "已刷新。会话列表仅供诊断，不证明三套键鼠已独立。"; }
        void RefreshSessions() {
            sessionList.Items.Clear();
            try { foreach (SessionInfo s in Sessions.Read()) sessionList.Items.Add(new ListViewItem(new string[] { s.Id.ToString(), s.Station, s.State, s.Id == CurrentSession ? "当前" : "" })); }
            catch (Exception ex) { sessionList.Items.Add("无法枚举会话：" + ex.Message); }
        }
        void Log(string message) {
            try {
                string path = Path.Combine(Path.GetDirectoryName(configPath), "gaming.log"); Directory.CreateDirectory(Path.GetDirectoryName(path));
                if (File.Exists(path) && new FileInfo(path).Length > 1024 * 1024) { string old = path + ".1"; if (File.Exists(old)) File.Delete(old); File.Move(path, old); }
                File.AppendAllText(path, DateTime.UtcNow.ToString("o") + " " + message + Environment.NewLine, Encoding.UTF8);
            } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
        void ClosingWindow(object sender, FormClosingEventArgs e) {
            if (!dirty) return;
            var answer = MessageBox.Show(this, "保存修改后的游戏配置？关闭启动器不会结束游戏。", "OpenSeat", MessageBoxButtons.YesNoCancel);
            if (answer == DialogResult.Cancel) { e.Cancel = true; return; }
            if (answer == DialogResult.Yes) try { Save(); } catch (Exception ex) { e.Cancel = true; MessageBox.Show(this, ex.Message); }
        }
    }
    public static class GamingProgram {
        [STAThread] public static int Main(string[] args) {
            if (args.Length == 1 && args[0] == "--self-test") return GamingTests.Run();
            if (Environment.OSVersion.Platform != PlatformID.Win32NT) { Console.Error.WriteLine("Windows is required."); return 1; }
            bool created;
            // Local namespace scopes the guard to this Windows session. Other seats can run concurrently.
            using (var mutex = new Mutex(true, @"Local\OpenSeat.NativeGaming.v1", out created)) {
                if (!created) { MessageBox.Show("当前 Windows 会话已经运行一个工位游戏启动器。"); return 0; }
                try { Application.EnableVisualStyles(); Application.SetCompatibleTextRenderingDefault(false); Application.Run(new GamingWindow()); return 0; }
                catch (Exception ex) { MessageBox.Show(ex.Message, "OpenSeat 启动失败"); return 1; }
                finally { mutex.ReleaseMutex(); }
            }
        }
    }
}
