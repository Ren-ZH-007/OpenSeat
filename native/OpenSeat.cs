using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows.Forms;
using System.Xml.Serialization;

namespace OpenSeatNative {
    public class Seat {
        public string Name = "";
        public string Monitor = "";
        public string Mouse = "";
        public string Keyboard = "";
        public string VmxPath = "";
    }
    public class Settings {
        public const int MaxNameLength = 40;
        public int Version = 1;
        public string VmwarePath = "";
        public Seat[] Seats = new Seat[] {
            new Seat { Name = "工位 1" }, new Seat { Name = "工位 2" }, new Seat { Name = "工位 3" }
        };
        public void Validate() {
            if (Version != 1 || Seats == null || Seats.Length != 3) throw new InvalidDataException("必须是三工位版本 1 配置。");
            var devices = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var monitors = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var machines = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (VmwarePath == null) throw new InvalidDataException("VMware 路径字段无效。");
            foreach (Seat s in Seats) {
                if (s == null || s.Mouse == null || s.Keyboard == null || s.Monitor == null || s.VmxPath == null)
                    throw new InvalidDataException("工位字段无效。");
                if (String.IsNullOrWhiteSpace(s.Name) || s.Name.Length > MaxNameLength)
                    throw new InvalidDataException("工位名称不能为空，且不能超过 " + MaxNameLength + " 个字符。");
                foreach (string id in new string[] { s.Mouse, s.Keyboard })
                    if (id != "" && !devices.Add(id)) throw new InvalidDataException("同一个输入设备不能同时分配给两个工位。");
                if (s.Monitor != "" && !monitors.Add(s.Monitor)) throw new InvalidDataException("同一显示器不能分配给两个工位。");
                if (s.VmxPath != "" && !machines.Add(s.VmxPath)) throw new InvalidDataException("同一台虚拟机不能分配给两个工位。");
            }
        }
        public static Settings Load(string path) {
            var serializer = new XmlSerializer(typeof(Settings));
            using (var stream = File.OpenRead(path)) {
                Settings value;
                try { value = (Settings)serializer.Deserialize(stream); }
                catch (InvalidOperationException ex) { throw new InvalidDataException("配置文件不是有效的 OpenSeat XML：" + (ex.InnerException != null ? ex.InnerException.Message : ex.Message)); }
                if (value == null) throw new InvalidDataException("配置文件为空。");
                value.Validate(); return value;
            }
        }
        public void Save(string path) {
            Validate();
            path = Path.GetFullPath(path);
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            string temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try {
                using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None)) {
                    new XmlSerializer(typeof(Settings)).Serialize(stream, this); stream.Flush(true);
                }
                if (File.Exists(path)) File.Replace(temp, path, null); else File.Move(temp, path);
            } finally { if (File.Exists(temp)) File.Delete(temp); }
        }
    }
    public struct InputEvent {
        public uint Kind;
        public IntPtr Device;
        public bool KeyDown, KeyUp, Absolute;
        public int X, Y;
        public ushort Buttons, MakeCode, KeyFlags;
    }
    // 按物理按键计数：同一个键在松开之前重复发来的“按下”（键盘自动重复）只算一次。
    public class KeyTracker {
        readonly HashSet<int> down = new HashSet<int>();
        public long Presses, RawEvents;
        public bool Feed(InputEvent e) {
            if (e.Kind != 1) return false;
            int id = Native.KeyId(e);
            if (e.KeyDown) { RawEvents++; if (down.Add(id)) { Presses++; return true; } return false; }
            if (e.KeyUp) down.Remove(id);
            return false;
        }
        public void Reset() { down.Clear(); }
    }
    public static class Native {
        public const int WM_DISPLAYCHANGE = 0x007E, WM_INPUT_DEVICE_CHANGE = 0x00FE, WM_INPUT = 0x00FF;
        public const int GIDC_ARRIVAL = 1, GIDC_REMOVAL = 2;
        [StructLayout(LayoutKind.Sequential)] public struct Header {
            public uint Type, Size; public IntPtr Device; public UIntPtr WParam;
        }
        [StructLayout(LayoutKind.Sequential)] public struct RawDevice {
            public ushort Page, Usage; public uint Flags; public IntPtr Target;
        }
        [StructLayout(LayoutKind.Sequential)] public struct DeviceList {
            public IntPtr Handle; public uint Type;
        }
        [DllImport("user32.dll", SetLastError=true)] public static extern bool RegisterRawInputDevices(RawDevice[] devices, uint count, uint size);
        [DllImport("user32.dll", SetLastError=true)] static extern uint GetRawInputData(IntPtr raw, uint command, IntPtr data, ref uint size, uint headerSize);
        [DllImport("user32.dll", CharSet=CharSet.Unicode, SetLastError=true)] static extern uint GetRawInputDeviceInfo(IntPtr device, uint command, StringBuilder data, ref uint size);
        [DllImport("user32.dll", SetLastError=true)] static extern uint GetRawInputDeviceList(IntPtr list, ref uint count, uint size);
        [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
        public static void Register(IntPtr window, bool enabled) {
            RawDevice[] data = new RawDevice[] {
                new RawDevice { Page=1, Usage=2, Flags=enabled ? 0x2100u : 1u, Target=enabled ? window : IntPtr.Zero },
                new RawDevice { Page=1, Usage=6, Flags=enabled ? 0x2100u : 1u, Target=enabled ? window : IntPtr.Zero }
            };
            if (!RegisterRawInputDevices(data, 2, (uint)Marshal.SizeOf(typeof(RawDevice)))) throw new Win32Exception();
        }
        public static string DeviceName(IntPtr device) {
            uint size = 0;
            if (device == IntPtr.Zero) return "";
            if (GetRawInputDeviceInfo(device, 0x20000007, null, ref size) == UInt32.MaxValue || size == 0) return "";
            var name = new StringBuilder((int)size + 1);
            if (GetRawInputDeviceInfo(device, 0x20000007, name, ref size) == UInt32.MaxValue) return "";
            return name.ToString();
        }
        public static Dictionary<string, uint> Inventory() {
            int stride = Marshal.SizeOf(typeof(DeviceList));
            for (int attempt=0; attempt<3; attempt++) {
                uint count=0;
                if (GetRawInputDeviceList(IntPtr.Zero, ref count, (uint)stride) == UInt32.MaxValue) throw new Win32Exception();
                if (count == 0) return new Dictionary<string, uint>(StringComparer.OrdinalIgnoreCase);
                IntPtr buffer=Marshal.AllocHGlobal(checked((int)count*stride));
                try {
                    uint actual=GetRawInputDeviceList(buffer, ref count, (uint)stride);
                    if (actual == UInt32.MaxValue) continue;
                    var result=new Dictionary<string, uint>(StringComparer.OrdinalIgnoreCase);
                    for (int i=0; i<actual; i++) {
                        var item=(DeviceList)Marshal.PtrToStructure(IntPtr.Add(buffer, i*stride), typeof(DeviceList));
                        string id=DeviceName(item.Handle);
                        if (item.Type <= 1 && id != "") result[id]=item.Type;
                    }
                    return result;
                } finally { Marshal.FreeHGlobal(buffer); }
            }
            throw new IOException("设备清单正在变化，请重新扫描。");
        }
        public static InputEvent Decode(IntPtr buffer, uint bytes) {
            int headerSize=Marshal.SizeOf(typeof(Header));
            if (bytes < headerSize) throw new InvalidDataException("输入头被截断。");
            var header=(Header)Marshal.PtrToStructure(buffer, typeof(Header));
            if (header.Size > bytes || header.Size < headerSize) throw new InvalidDataException("输入包长度无效。");
            var value=new InputEvent { Kind=header.Type, Device=header.Device };
            IntPtr body=IntPtr.Add(buffer, headerSize);
            if (header.Type == 0) {
                if (header.Size < headerSize+24) throw new InvalidDataException("鼠标包被截断。");
                value.Absolute=(Marshal.ReadInt16(body, 0)&1) != 0;
                value.Buttons=(ushort)Marshal.ReadInt16(body, 4);
                value.X=Marshal.ReadInt32(body, 12); value.Y=Marshal.ReadInt32(body, 16);
            } else if (header.Type == 1) {
                if (header.Size < headerSize+16) throw new InvalidDataException("键盘包被截断。");
                ushort make=(ushort)Marshal.ReadInt16(body, 0), flags=(ushort)Marshal.ReadInt16(body, 2), vkey=(ushort)Marshal.ReadInt16(body, 6);
                bool valid = make != 255 && vkey < 255;
                value.MakeCode=make; value.KeyFlags=flags;
                value.KeyDown = valid && (flags&1) == 0;
                value.KeyUp = valid && (flags&1) != 0;
            }
            return value;
        }
        public static InputEvent Read(IntPtr raw) {
            uint size=0, headerSize=(uint)Marshal.SizeOf(typeof(Header));
            if (GetRawInputData(raw, 0x10000003, IntPtr.Zero, ref size, headerSize)==UInt32.MaxValue) throw new Win32Exception();
            if (size==0 || size>1024*1024) throw new InvalidDataException("输入包长度超限。");
            IntPtr buffer=Marshal.AllocHGlobal((int)size);
            try {
                uint actual=GetRawInputData(raw, 0x10000003, buffer, ref size, headerSize);
                if (actual==UInt32.MaxValue) throw new Win32Exception();
                return Decode(buffer, actual);
            } finally { Marshal.FreeHGlobal(buffer); }
        }
        // 扫描码 + E0/E1 前缀，区分左右 Ctrl/Alt 等共用扫描码的按键。
        public static int KeyId(InputEvent e) {
            return e.MakeCode | ((e.KeyFlags & 2) != 0 ? 0x100 : 0) | ((e.KeyFlags & 4) != 0 ? 0x200 : 0);
        }
        // 左、右、中、侧键 4、侧键 5 的“按下”标志位：0x1、0x4、0x10、0x40、0x100。
        public static int ButtonPresses(ushort flags) {
            int n=0; for (int b=0; b<5; b++) if ((flags & (1 << (2*b))) != 0) n++; return n;
        }
        // 从 Raw Input 设备路径中提取便于人工核对的 VID/PID/接口号；USB 与蓝牙两种写法都支持。
        public static string DeviceLabel(string id) {
            if (String.IsNullOrEmpty(id)) return "";
            Match m=Regex.Match(id, @"VID[_&]([0-9A-Fa-f]{4,8}).*?PID[_&]([0-9A-Fa-f]{4})", RegexOptions.IgnoreCase);
            if (!m.Success) return id.Length > 36 ? "…" + id.Substring(id.Length-36) : id;
            string vid=m.Groups[1].Value; vid=vid.Substring(vid.Length-4);
            string label="VID " + vid.ToUpperInvariant() + " · PID " + m.Groups[2].Value.ToUpperInvariant();
            Match mi=Regex.Match(id, @"MI_([0-9A-Fa-f]{2})", RegexOptions.IgnoreCase);
            if (mi.Success) label += " · 接口 " + mi.Groups[1].Value.ToUpperInvariant();
            return label;
        }
        public static string MonitorLabel(string deviceName) {
            if (String.IsNullOrEmpty(deviceName)) return "（未选择）";
            string shortName=deviceName.StartsWith(@"\\.\") ? deviceName.Substring(4) : deviceName;
            foreach (Screen s in Screen.AllScreens)
                if (String.Equals(s.DeviceName, deviceName, StringComparison.OrdinalIgnoreCase))
                    return shortName + " · " + s.Bounds.Width + "×" + s.Bounds.Height + " · 位置 (" + s.Bounds.X + "," + s.Bounds.Y + ")" + (s.Primary ? " · 主屏" : "");
            return shortName + " · 未连接";
        }
    }
    public class SeatCanvas : Control {
        public int PointerX=100, PointerY=150;
        public long MouseEvents, Clicks;
        public KeyTracker KeyCounter=new KeyTracker();
        public string SeatName="";
        public SeatCanvas() { DoubleBuffered=true; BackColor=Color.FromArgb(17,27,45); ForeColor=Color.White; }
        public void Receive(InputEvent e) {
            if (e.Kind==0) {
                MouseEvents++;
                if (e.Absolute) { PointerX=(int)(Math.Max(0, Math.Min(65535,e.X))/65535.0*Math.Max(1,Width-16)); PointerY=(int)(Math.Max(0, Math.Min(65535,e.Y))/65535.0*Math.Max(1,Height-16)); }
                else { PointerX=(int)Math.Max(0,Math.Min((long)Math.Max(0,Width-16),(long)PointerX+e.X)); PointerY=(int)Math.Max(0,Math.Min((long)Math.Max(0,Height-16),(long)PointerY+e.Y)); }
                Clicks+=Native.ButtonPresses(e.Buttons);
            } else if (e.Kind==1) KeyCounter.Feed(e);
        }
        protected override void OnPaint(PaintEventArgs e) {
            base.OnPaint(e);
            using (var heading=new Font("Microsoft YaHei UI",24,FontStyle.Bold)) e.Graphics.DrawString(SeatName,heading,Brushes.White,20,20);
            e.Graphics.DrawString("应用内输入测试 · 不是独立 Windows 桌面 · Esc 结束全部测试",Font,Brushes.LightSteelBlue,20,75);
            e.Graphics.DrawString("鼠标事件 "+MouseEvents+"    点击 "+Clicks+"    按键 "+KeyCounter.Presses+" 次（原始按键事件 "+KeyCounter.RawEvents+"，长按重复已过滤）",Font,Brushes.White,20,105);
            e.Graphics.FillEllipse(Brushes.DeepSkyBlue,PointerX,PointerY,16,16);
        }
    }
    public class TestWindow : Form {
        public SeatCanvas Canvas=new SeatCanvas();
        public TestWindow(Seat seat, Rectangle bounds) {
            Text=seat.Name+" · OpenSeat 输入测试"; StartPosition=FormStartPosition.Manual;
            Bounds=bounds; Canvas.Dock=DockStyle.Fill; Canvas.SeatName=seat.Name; Controls.Add(Canvas);
            KeyPreview=true; KeyDown+=delegate(object sender,KeyEventArgs e) { if(e.KeyCode==Keys.Escape) Close(); };
        }
    }
    // 屏幕监控：只读截取工位显示器画面并缩放显示，不向工位发送任何输入。
    public static class ScreenCapture {
        public static Rectangle Fit(Size source, Size target) {
            if (source.Width<=0 || source.Height<=0 || target.Width<=0 || target.Height<=0) return Rectangle.Empty;
            double scale=Math.Min((double)target.Width/source.Width,(double)target.Height/source.Height);
            int w=Math.Max(1,(int)(source.Width*scale)), h=Math.Max(1,(int)(source.Height*scale));
            return new Rectangle((target.Width-w)/2,(target.Height-h)/2,w,h);
        }
        public static Bitmap Grab(Rectangle bounds, Size target) {
            Rectangle fit=Fit(bounds.Size,target);
            if (fit.IsEmpty) return null;
            using (var full=new Bitmap(bounds.Width,bounds.Height,PixelFormat.Format32bppRgb)) {
                using (var g=Graphics.FromImage(full)) g.CopyFromScreen(bounds.Location,Point.Empty,bounds.Size,CopyPixelOperation.SourceCopy);
                var small=new Bitmap(fit.Width,fit.Height,PixelFormat.Format32bppRgb);
                using (var g=Graphics.FromImage(small)) { g.InterpolationMode=InterpolationMode.Bilinear; g.DrawImage(full,0,0,fit.Width,fit.Height); }
                return small;
            }
        }
        public static Screen Find(string deviceName) {
            if (String.IsNullOrEmpty(deviceName)) return null;
            foreach (Screen s in Screen.AllScreens) if (String.Equals(s.DeviceName,deviceName,StringComparison.OrdinalIgnoreCase)) return s;
            return null;
        }
    }
    public class MonitorTile : Control {
        Bitmap image; string message="";
        public string Caption="";
        public MonitorTile() { DoubleBuffered=true; BackColor=Color.FromArgb(17,27,45); ForeColor=Color.White; Cursor=Cursors.Hand; }
        public void SetImage(Bitmap next) { Bitmap old=image; image=next; message=""; if(old!=null) old.Dispose(); Invalidate(); }
        public void ShowMessage(string text) { if(image!=null) { image.Dispose(); image=null; } message=text; Invalidate(); }
        protected override void OnPaint(PaintEventArgs e) {
            base.OnPaint(e);
            if (image!=null) e.Graphics.DrawImage(image,ScreenCapture.Fit(image.Size,ClientSize));
            else using (var f=new StringFormat { Alignment=StringAlignment.Center,LineAlignment=StringAlignment.Center }) e.Graphics.DrawString(message,Font,Brushes.LightSteelBlue,ClientRectangle,f);
            if (Caption!="") {
                var size=e.Graphics.MeasureString(Caption,Font);
                using (var shade=new SolidBrush(Color.FromArgb(170,0,0,0))) e.Graphics.FillRectangle(shade,0,0,size.Width+12,size.Height+6);
                e.Graphics.DrawString(Caption,Font,Brushes.White,6,3);
            }
        }
        protected override void Dispose(bool disposing) { if(disposing && image!=null) { image.Dispose(); image=null; } base.Dispose(disposing); }
    }
    public class MonitorWindow : Form {
        readonly MonitorTile view=new MonitorTile();
        readonly Timer timer=new Timer();
        readonly Func<string> monitor;
        public MonitorWindow(string seatName, Func<string> monitorName, Rectangle near) {
            monitor=monitorName;
            Text=seatName+" · 屏幕监控（只读）"; StartPosition=FormStartPosition.Manual; MinimumSize=new Size(320,220);
            Bounds=new Rectangle(near.X+(near.Width-960)/2,near.Y+(near.Height-600)/2,960,600);
            view.Dock=DockStyle.Fill; view.Cursor=Cursors.Default; view.Caption=seatName; Controls.Add(view);
            KeyPreview=true; KeyDown+=delegate(object sender,KeyEventArgs e) { if(e.KeyCode==Keys.Escape) Close(); };
            timer.Interval=200; timer.Tick+=delegate { GrabFrame(); }; timer.Start();
            Shown+=delegate { GrabFrame(); };
        }
        void GrabFrame() {
            if (WindowState==FormWindowState.Minimized) return;
            Screen s=ScreenCapture.Find(monitor());
            if (s==null) { view.ShowMessage("该工位未分配在线显示器"); return; }
            view.Caption=Text.Replace(" · 屏幕监控（只读）","")+" · "+Native.MonitorLabel(s.DeviceName);
            try { view.SetImage(ScreenCapture.Grab(s.Bounds,view.ClientSize)); }
            catch (Exception) { view.ShowMessage("暂时无法捕获画面（可能处于锁屏或 UAC 安全桌面）"); }
        }
        protected override void Dispose(bool disposing) { if(disposing) timer.Dispose(); base.Dispose(disposing); }
    }
    public class MainWindow : Form {
        Settings config=new Settings();
        string configPath=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"OpenSeat","three-seat.xml");
        ComboBox[] screens=new ComboBox[3]; Label[] bindings=new Label[3]; TextBox[] nameBoxes=new TextBox[3];
        MonitorTile[] tiles=new MonitorTile[3]; MonitorWindow[] monitorWindows=new MonitorWindow[3];
        CheckBox monitorToggle; Timer monitorClock=new Timer(); VmSeatsWindow vmWindow;
        List<TestWindow> windows=new List<TestWindow>();
        Dictionary<IntPtr,string> names=new Dictionary<IntPtr,string>();
        HashSet<string> known=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        Label status=new Label(); TextBox inventory=new TextBox();
        Timer clock=new Timer(); Button runButton;
        bool running=false, dirty=false, refreshing=false;
        int bindSeat=-1; uint bindKind; DateTime armAt, expires;
        public MainWindow() {
            Text="OpenSeat "+Program.AppVersion+" · 三工位技术验证版"; Size=new Size(1080,1030); MinimumSize=new Size(940,960);
            Font=new Font("Microsoft YaHei UI",10); BackColor=Color.FromArgb(242,246,251);
            var layout=new TableLayoutPanel { Dock=DockStyle.Fill,Padding=new Padding(20),ColumnCount=1,RowCount=7 };
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute,78)); layout.RowStyles.Add(new RowStyle(SizeType.Absolute,94)); layout.RowStyles.Add(new RowStyle(SizeType.Absolute,180));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute,320)); layout.RowStyles.Add(new RowStyle(SizeType.Absolute,54));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent,100)); layout.RowStyles.Add(new RowStyle(SizeType.Absolute,60));
            Controls.Add(layout);
            var title=new Label { Text="OpenSeat / 三工位\n输入测试 · 屏幕监控 · VMware 虚拟机工位（三人各用独立 Windows）",Dock=DockStyle.Fill,Font=new Font(Font.FontFamily,15,FontStyle.Bold) };
            layout.Controls.Add(title,0,0);
            var toolbar=new FlowLayoutPanel { Dock=DockStyle.Fill };
            toolbar.Controls.Add(Button("刷新设备 / 屏幕", RefreshInventory)); toolbar.Controls.Add(Button("显示屏编号", Identify));
            toolbar.Controls.Add(Button("保存配置", Save)); toolbar.Controls.Add(Button("导入配置", Import)); toolbar.Controls.Add(Button("导出配置", Export));
            toolbar.Controls.Add(Button("虚拟机工位…", OpenVmSeats));
            toolbar.Controls.Add(Button("原生工位游戏…", OpenGaming));
            monitorToggle=new CheckBox { Text="屏幕监控",Checked=true,AutoSize=true,Margin=new Padding(12,9,3,3) };
            monitorToggle.CheckedChanged+=delegate { if(monitorToggle.Checked) RefreshMonitors(); else foreach(MonitorTile tile in tiles) tile.ShowMessage("屏幕监控已暂停"); };
            toolbar.Controls.Add(monitorToggle);
            layout.Controls.Add(toolbar,0,1);
            var monitorRow=new TableLayoutPanel { Dock=DockStyle.Fill,ColumnCount=3 };
            for(int i=0;i<3;i++) {
                monitorRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,33.333f)); int seat=i;
                tiles[i]=new MonitorTile { Dock=DockStyle.Fill,Margin=new Padding(4) };
                tiles[i].Click+=delegate { OpenMonitor(seat); };
                monitorRow.Controls.Add(tiles[i],i,0);
            }
            layout.Controls.Add(monitorRow,0,2);
            var cards=new TableLayoutPanel { Dock=DockStyle.Fill,ColumnCount=3 };
            for(int i=0;i<3;i++) {
                cards.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,33.333f)); int seat=i;
                var card=new FlowLayoutPanel { Dock=DockStyle.Fill,FlowDirection=FlowDirection.TopDown,WrapContents=false,Padding=new Padding(10),BackColor=Color.White,Margin=new Padding(4) };
                card.Controls.Add(new Label { Text="工位 "+(i+1),Font=new Font(Font.FontFamily,16,FontStyle.Bold),AutoSize=true });
                nameBoxes[i]=new TextBox { Width=275,MaxLength=Settings.MaxNameLength };
                nameBoxes[i].TextChanged+=delegate { if(!refreshing) { config.Seats[seat].Name=nameBoxes[seat].Text; dirty=true; } };
                card.Controls.Add(nameBoxes[i]);
                screens[i]=new ComboBox { DropDownStyle=ComboBoxStyle.DropDownList,Width=275,FormattingEnabled=true };
                screens[i].Format+=delegate(object sender,ListControlConvertEventArgs e) { e.Value=Native.MonitorLabel(e.ListItem as string); };
                screens[i].SelectedIndexChanged+=delegate { if(!refreshing) { if(running) Stop(); config.Seats[seat].Monitor=(string)screens[seat].SelectedItem ?? ""; dirty=true; } };
                card.Controls.Add(screens[i]);
                card.Controls.Add(Button("绑定鼠标：稍后移动它",delegate { Arm(seat,0); }));
                card.Controls.Add(Button("绑定键盘：稍后按一下",delegate { Arm(seat,1); }));
                card.Controls.Add(Button("清除此工位绑定",delegate { Stop(); config.Seats[seat].Mouse=""; config.Seats[seat].Keyboard=""; dirty=true; UpdateLabels(); status.Text="已清除"+config.Seats[seat].Name+"的键鼠绑定。"; }));
                bindings[i]=new Label { Width=285,Height=75 }; card.Controls.Add(bindings[i]); cards.Controls.Add(card,i,0);
            }
            layout.Controls.Add(cards,0,3);
            var controls=new FlowLayoutPanel { Dock=DockStyle.Fill };
            runButton=Button("启动三屏输入测试", Toggle); controls.Controls.Add(runButton);
            controls.Controls.Add(Button("停止 / 取消绑定",delegate { Stop(); status.Text="已停止。"; })); controls.Controls.Add(Button("导出诊断报告",Report));
            layout.Controls.Add(controls,0,4);
            inventory.Multiline=true; inventory.ReadOnly=true; inventory.ScrollBars=ScrollBars.Both; inventory.WordWrap=false; inventory.Dock=DockStyle.Fill;
            layout.Controls.Add(inventory,0,5); status.Dock=DockStyle.Fill; status.Text="就绪。需要三台扩展模式显示器、三个可独立识别的鼠标；键盘可选。"; layout.Controls.Add(status,0,6);
            Shown+=delegate { try { if(File.Exists(configPath)) config=Settings.Load(configPath); } catch(Exception ex) { Error(ex); } RefreshInventory(); };
            FormClosing+=delegate(object sender,FormClosingEventArgs e) { if(!ConfirmDiscard()) { e.Cancel=true; return; } Stop(); };
            monitorClock.Interval=500; monitorClock.Tick+=delegate { RefreshMonitors(); }; monitorClock.Start();
            clock.Interval=33; clock.Tick+=delegate {
                if(bindSeat>=0 && DateTime.UtcNow>expires) { Stop(); status.Text="绑定超时，请重试。"; }
                foreach(TestWindow w in windows) if(!w.IsDisposed) w.Canvas.Invalidate();
            }; clock.Start();
        }
        void RefreshMonitors() {
            if(monitorToggle==null || !monitorToggle.Checked || WindowState==FormWindowState.Minimized) return;
            for(int i=0;i<3;i++) {
                MonitorTile tile=tiles[i]; Seat seat=config.Seats[i];
                Screen s=ScreenCapture.Find(seat.Monitor);
                tile.Caption=seat.Name+(s==null ? "" : " · "+s.DeviceName.Replace(@"\\.\","")+" · 点击放大");
                if(s==null) { tile.ShowMessage("未分配在线显示器"); continue; }
                try { tile.SetImage(ScreenCapture.Grab(s.Bounds,tile.ClientSize)); }
                catch(Exception) { tile.ShowMessage("暂时无法捕获画面"); }
            }
        }
        void OpenMonitor(int seat) {
            MonitorWindow existing=monitorWindows[seat];
            if(existing!=null && !existing.IsDisposed) { if(existing.WindowState==FormWindowState.Minimized) existing.WindowState=FormWindowState.Normal; existing.Activate(); return; }
            var window=new MonitorWindow(config.Seats[seat].Name,delegate { return config.Seats[seat].Monitor; },Screen.FromControl(this).WorkingArea);
            monitorWindows[seat]=window; window.Show(this);
        }
        void OpenGaming() {
            string path=Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"OpenSeat.Gaming.exe");
            if(!File.Exists(path)) throw new FileNotFoundException("缺少 OpenSeat.Gaming.exe，请使用包含两个 EXE 的完整开发包。");
            Stop(); monitorToggle.Checked=false;
            foreach(MonitorWindow window in monitorWindows) if(window!=null && !window.IsDisposed) window.Close();
            using(var process=System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path) { UseShellExecute=false,WorkingDirectory=AppDomain.CurrentDomain.BaseDirectory })) { }
        }
        void OpenVmSeats() {
            if(vmWindow!=null && !vmWindow.IsDisposed) { if(vmWindow.WindowState==FormWindowState.Minimized) vmWindow.WindowState=FormWindowState.Normal; vmWindow.Activate(); return; }
            Stop();
            vmWindow=new VmSeatsWindow(config,delegate { dirty=true; });
            vmWindow.FormClosed+=delegate { UpdateLabels(); };
            vmWindow.Show(this);
        }
        Button Button(string text, Action action) { var b=new Button { Text=text,AutoSize=true,Height=34,Margin=new Padding(3) }; b.Click+=delegate { try { action(); } catch(Exception ex) { Error(ex); } }; return b; }
        void Error(Exception ex) { MessageBox.Show(this,ex.Message,"OpenSeat",MessageBoxButtons.OK,MessageBoxIcon.Error); }
        static string Online(string id, Dictionary<string,uint> online) { return online==null ? "" : (online.ContainsKey(id) ? " [在线]" : " [离线]"); }
        void UpdateLabels() {
            Dictionary<string,uint> online=null; try { online=Native.Inventory(); } catch { }
            for(int i=0;i<3;i++) {
                Seat s=config.Seats[i];
                bindings[i].Text="鼠标："+(s.Mouse=="" ? "未绑定" : Native.DeviceLabel(s.Mouse)+Online(s.Mouse,online))
                    +"\n键盘："+(s.Keyboard=="" ? "未绑定（可选）" : Native.DeviceLabel(s.Keyboard)+Online(s.Keyboard,online));
            }
        }
        string Describe() {
            var b=new StringBuilder(); b.AppendLine("OpenSeat "+Program.AppVersion+" | OS: "+Environment.OSVersion+" | 64-bit process: "+Environment.Is64BitProcess);
            b.AppendLine("屏幕拓扑（Windows 必须设置为扩展模式）：");
            foreach(Screen s in Screen.AllScreens) b.AppendLine("  "+Native.MonitorLabel(s.DeviceName));
            b.AppendLine("Raw Input 设备（不是 PnP 清单的 ID；复合接收器可能合并设备）：");
            var online=Native.Inventory(); foreach(var item in online) b.AppendLine("  "+(item.Value==0 ? "鼠标 " : "键盘 ")+Native.DeviceLabel(item.Key)+"  "+item.Key);
            b.AppendLine("工位绑定：");
            foreach(Seat s in config.Seats) b.AppendLine("  "+s.Name+" | 屏幕："+Native.MonitorLabel(s.Monitor)+" | "+(s.VmxPath==""?"主机工位":"虚拟机："+s.VmxPath)+"\r\n    鼠标："+(s.Mouse==""?"未绑定":s.Mouse+Online(s.Mouse,online))+"\r\n    键盘："+(s.Keyboard==""?"未绑定":s.Keyboard+Online(s.Keyboard,online)));
            b.AppendLine("desktopIsolation=false; sessionEngine=false; audioRouting=false; inputTestOnly=true"); return b.ToString();
        }
        void RefreshInventory() {
            Stop(); names.Clear(); refreshing=true;
            try {
                for(int i=0;i<3;i++) {
                    nameBoxes[i].Text=config.Seats[i].Name;
                    screens[i].Items.Clear(); screens[i].Items.Add(""); foreach(Screen s in Screen.AllScreens) screens[i].Items.Add(s.DeviceName);
                    if(config.Seats[i].Monitor!=""&&!screens[i].Items.Contains(config.Seats[i].Monitor)) screens[i].Items.Add(config.Seats[i].Monitor);
                    screens[i].SelectedItem=config.Seats[i].Monitor;
                }
            }
            finally { refreshing=false; }
            UpdateLabels(); inventory.Text=Describe();
        }
        // 记录当前在线设备后再注册监听。注册 RIDEV_DEVNOTIFY 时 Windows 会对已连接设备逐一发送“到达”通知，
        // 这些已知设备不应被当成连接变化。
        void Listen() {
            known=new HashSet<string>(Native.Inventory().Keys,StringComparer.OrdinalIgnoreCase);
            Native.Register(Handle,true);
        }
        void Arm(int seat,uint kind) {
            Stop(); Listen(); bindSeat=seat; bindKind=kind; armAt=DateTime.UtcNow.AddSeconds(1); expires=DateTime.UtcNow.AddSeconds(20);
            status.Text="等待绑定"+config.Seats[seat].Name+"：1 秒后只操作目标"+(kind==0?"鼠标（移动）":"键盘（按键）")+"。20 秒超时；请勿操作其他同类设备。";
        }
        bool OurForeground() { IntPtr foreground=Native.GetForegroundWindow(); if(foreground==Handle) return true; foreach(TestWindow w in windows) if(!w.IsDisposed && w.Handle==foreground) return true; return false; }
        bool IsBound(string id) {
            foreach(Seat s in config.Seats) if(String.Equals(s.Mouse,id,StringComparison.OrdinalIgnoreCase)||String.Equals(s.Keyboard,id,StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }
        void DeviceChanged(long kind,IntPtr handle) {
            if(kind==Native.GIDC_ARRIVAL) {
                string id=Native.DeviceName(handle); if(id=="") return;
                names[handle]=id;
                if(known.Add(id)) status.Text="检测到新接入的输入设备（"+Native.DeviceLabel(id)+"）。当前操作继续；如需绑定它，请结束后刷新。";
                return;
            }
            if(kind!=Native.GIDC_REMOVAL) return;
            string removed; bool affected=false;
            if(names.TryGetValue(handle,out removed)) { names.Remove(handle); known.Remove(removed); affected=IsBound(removed); }
            else {
                try {
                    var online=Native.Inventory(); known=new HashSet<string>(online.Keys,StringComparer.OrdinalIgnoreCase);
                    foreach(Seat s in config.Seats) if((s.Mouse!=""&&!online.ContainsKey(s.Mouse))||(s.Keyboard!=""&&!online.ContainsKey(s.Keyboard))) affected=true;
                } catch { affected=true; }
            }
            if(running && affected) { Stop(); status.Text="已绑定的输入设备被拔出，测试已停止。请重新连接后刷新并核对绑定。"; }
            else if(affected) UpdateLabels();
        }
        protected override void WndProc(ref Message m) {
            if(m.Msg==Native.WM_INPUT_DEVICE_CHANGE && (running || bindSeat>=0)) {
                try { DeviceChanged(m.WParam.ToInt64(),m.LParam); } catch(Exception ex) { Stop(); status.Text="设备变化处理失败，测试已停止："+ex.Message; }
            }
            if(m.Msg==Native.WM_DISPLAYCHANGE && (running || bindSeat>=0)) { Stop(); status.Text="显示拓扑发生变化，测试已停止，请刷新。"; }
            if(m.Msg==Native.WM_INPUT && (running || bindSeat>=0) && OurForeground()) {
                try {
                    InputEvent input=Native.Read(m.LParam);
                    string id;
                    if(!names.TryGetValue(input.Device,out id)) { id=Native.DeviceName(input.Device); names[input.Device]=id; }
                    if(id!="") Route(id,input);
                } catch(Exception ex) { Stop(); status.Text="输入处理停止："+ex.Message; }
            }
            base.WndProc(ref m);
        }
        void Route(string id,InputEvent input) {
            if(bindSeat>=0) {
                if(DateTime.UtcNow<armAt || input.Kind!=bindKind || (bindKind==0 && input.X==0 && input.Y==0) || (bindKind==1 && !input.KeyDown)) return;
                for(int i=0;i<3;i++) if(i!=bindSeat && (String.Equals(config.Seats[i].Mouse,id,StringComparison.OrdinalIgnoreCase)||String.Equals(config.Seats[i].Keyboard,id,StringComparison.OrdinalIgnoreCase))) { Stop(); status.Text="这个设备已绑定到"+config.Seats[i].Name+"；请先清除旧绑定。"; return; }
                int target=bindSeat; if(bindKind==0) config.Seats[target].Mouse=id; else config.Seats[target].Keyboard=id;
                dirty=true; Stop(); UpdateLabels(); inventory.Text=Describe(); status.Text=config.Seats[target].Name+" 绑定完成（"+Native.DeviceLabel(id)+"），请保存配置。"; return;
            }
            for(int i=0;i<3 && i<windows.Count;i++) if(!windows[i].IsDisposed && ((input.Kind==0 && String.Equals(config.Seats[i].Mouse,id,StringComparison.OrdinalIgnoreCase))||(input.Kind==1 && String.Equals(config.Seats[i].Keyboard,id,StringComparison.OrdinalIgnoreCase)))) windows[i].Canvas.Receive(input);
        }
        void Toggle() {
            if(running) { Stop(); status.Text="测试已停止。"; return; } Stop(); config.Validate();
            var online=Native.Inventory(); var chosen=new List<Screen>();
            foreach(Seat s in config.Seats) {
                Screen found=null; foreach(Screen screen in Screen.AllScreens) if(screen.DeviceName==s.Monitor) found=screen;
                if(found==null) throw new InvalidOperationException(s.Name+" 未分配在线显示器。"); chosen.Add(found);
                if(s.Mouse=="" || !online.ContainsKey(s.Mouse) || online[s.Mouse]!=0) throw new InvalidOperationException(s.Name+" 未绑定在线鼠标。");
                if(s.Keyboard!="" && (!online.ContainsKey(s.Keyboard)||online[s.Keyboard]!=1)) throw new InvalidOperationException(s.Name+" 绑定的键盘离线或类型不匹配。");
            }
            try {
                Listen(); running=true;
                for(int i=0;i<3;i++) { var window=new TestWindow(config.Seats[i],chosen[i].WorkingArea); windows.Add(window); window.FormClosed+=delegate { if(running) BeginInvoke((Action)delegate { Stop(); status.Text="测试窗口已关闭，全部测试停止。"; }); }; window.Show(this); }
                runButton.Text="停止三屏输入测试"; status.Text="测试进行中。蓝点是应用内指针；系统鼠标仍共享。只统计本程序前台时的输入，不记录按键内容。Esc 可关闭测试窗口。";
            } catch { Stop(); throw; }
        }
        void Stop() {
            bool registered=running || bindSeat>=0; running=false; bindSeat=-1;
            foreach(TestWindow w in windows) if(!w.IsDisposed) w.Close(); windows.Clear();
            if(registered && IsHandleCreated) { try { Native.Register(Handle,false); } catch(Exception ex) { status.Text="注销输入监听失败："+ex.Message; } }
            if(runButton!=null) runButton.Text="启动三屏输入测试";
        }
        void Identify() {
            foreach(Screen s in Screen.AllScreens) {
                string owner="未分配给任何工位";
                foreach(Seat seat in config.Seats) if(String.Equals(seat.Monitor,s.DeviceName,StringComparison.OrdinalIgnoreCase)) owner="已分配给："+seat.Name;
                var popup=new Form { StartPosition=FormStartPosition.Manual,Bounds=new Rectangle(s.Bounds.X+50,s.Bounds.Y+50,520,190),TopMost=true,FormBorderStyle=FormBorderStyle.FixedToolWindow,Text="OpenSeat 显示屏编号" };
                popup.Controls.Add(new Label { Dock=DockStyle.Fill,Text=s.DeviceName.Replace(@"\\.\","")+"\n"+owner,TextAlign=ContentAlignment.MiddleCenter,Font=new Font(Font.FontFamily,24,FontStyle.Bold) });
                var timer=new Timer { Interval=2500 }; timer.Tick+=delegate { popup.Close(); }; popup.FormClosed+=delegate { timer.Stop(); timer.Dispose(); }; popup.Show(this); timer.Start();
            }
        }
        void Save() { config.Save(configPath); dirty=false; status.Text="已保存："+configPath; }
        bool ConfirmDiscard() { if(!dirty) return true; var answer=MessageBox.Show(this,"保存当前绑定配置？","未保存修改",MessageBoxButtons.YesNoCancel); if(answer==DialogResult.Cancel) return false; if(answer==DialogResult.Yes) { try { Save(); } catch(Exception ex) { Error(ex); return false; } } return true; }
        void Import() { if(!ConfirmDiscard()) return; using(var dialog=new OpenFileDialog { Filter="OpenSeat XML|*.xml" }) if(dialog.ShowDialog(this)==DialogResult.OK) { Settings candidate=Settings.Load(dialog.FileName); Stop(); if(vmWindow!=null && !vmWindow.IsDisposed) vmWindow.Close(); config=candidate; dirty=true; RefreshInventory(); status.Text="已导入，尚未保存；确认无误后请点“保存配置”。"; } }
        void Export() { using(var dialog=new SaveFileDialog { Filter="OpenSeat XML|*.xml",FileName="three-seat.xml" }) if(dialog.ShowDialog(this)==DialogResult.OK) { config.Save(dialog.FileName); status.Text="已导出："+dialog.FileName; } }
        void Report() { using(var dialog=new SaveFileDialog { Filter="文本报告|*.txt",FileName="OpenSeat-diagnostics.txt" }) if(dialog.ShowDialog(this)==DialogResult.OK) { File.WriteAllText(dialog.FileName,Describe(),Encoding.UTF8); status.Text="诊断报告已导出（含设备实例 ID，不含输入内容）。"; } }
        protected override void Dispose(bool disposing) { if(disposing) { clock.Dispose(); monitorClock.Dispose(); } base.Dispose(disposing); }
    }
    public static class Program {
        public const string AppVersion="0.4.0-preview.1";
        [STAThread] public static int Main(string[] args) {
            if(args.Length>0 && args[0]=="--self-test") return SelfTest.Run();
            try { Native.SetProcessDPIAware(); Application.EnableVisualStyles(); Application.SetCompatibleTextRenderingDefault(false); Application.Run(new MainWindow()); return 0; }
            catch(Exception ex) { MessageBox.Show(ex.ToString(),"OpenSeat 启动失败"); return 1; }
        }
    }
}
