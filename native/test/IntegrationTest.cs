using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace OpenSeatNative {
    // 在真实 Windows 桌面上运行的集成测试：屏幕监控 + 虚拟机工位。
    // 用法：OpenSeat.Integration.exe <输出目录> <假 vmware.exe 路径>
    public static class IntegrationTest {
        [DllImport("user32.dll")] static extern bool GetClipCursor(out WindowTools.RECT rect);
        [DllImport("user32.dll")] static extern bool IsZoomed(IntPtr hwnd);
        [DllImport("user32.dll")] static extern IntPtr SendMessage(IntPtr hwnd, int msg, IntPtr w, IntPtr l);

        static readonly List<string> rows = new List<string>();
        static int passed, failed;
        static string outDir;

        static void Record(string area, string name, bool ok, string detail) {
            if (ok) passed++; else failed++;
            rows.Add("| " + area + " | " + name + " | " + (ok ? "✅ 通过" : "❌ 失败") + " | " + (detail ?? "").Replace("|", "/").Replace("\r", " ").Replace("\n", " ") + " |");
            Console.WriteLine((ok ? "PASS " : "FAIL ") + area + " / " + name + " :: " + detail);
        }
        static void Check(string area, string name, Func<string> body) {
            string detail; bool ok;
            try { detail = body(); ok = !detail.StartsWith("FAIL:"); if (!ok) detail = detail.Substring(5); }
            catch (Exception ex) { ok = false; detail = ex.GetType().Name + ": " + ex.Message; }
            Record(area, name, ok, detail);
        }
        static string Expect(bool condition, string pass, string fail) { return condition ? pass : "FAIL:" + fail; }
        static void Pump(int ms) { var sw = Stopwatch.StartNew(); while (sw.ElapsedMilliseconds < ms) { Application.DoEvents(); Thread.Sleep(15); } }
        static bool PumpUntil(Func<bool> done, int ms) { var sw = Stopwatch.StartNew(); while (sw.ElapsedMilliseconds < ms) { Application.DoEvents(); if (done()) return true; Thread.Sleep(20); } return done(); }
        static object Get(object o, string field) { return o.GetType().GetField(field, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public).GetValue(o); }
        static void Set(object o, string field, object v) { o.GetType().GetField(field, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public).SetValue(o, v); }
        static object Call(object o, string method, params object[] args) { return o.GetType().GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public).Invoke(o, args); }
        static void Shot(string name) {
            Rectangle b = SystemInformation.VirtualScreen;
            using (var bmp = new Bitmap(b.Width, b.Height)) {
                using (var g = Graphics.FromImage(bmp)) g.CopyFromScreen(b.Location, Point.Empty, b.Size);
                bmp.Save(Path.Combine(outDir, name + ".png"), ImageFormat.Png);
            }
        }
        static Color Center(Bitmap b) { return b.GetPixel(b.Width / 2, b.Height / 2); }

        [STAThread]
        public static int Main(string[] args) {
            outDir = args.Length > 0 ? Path.GetFullPath(args[0]) : Path.GetFullPath("integration-results");
            string fakeVmware = args.Length > 1 ? Path.GetFullPath(args[1]) : "";
            Directory.CreateDirectory(outDir);
            Native.SetProcessDPIAware(); Application.EnableVisualStyles();
            Screen primary = Screen.PrimaryScreen;
            string work = Path.Combine(Path.GetTempPath(), "OpenSeat 集成测试 " + Guid.NewGuid().ToString("N").Substring(0, 6));
            Directory.CreateDirectory(work);
            try {
                ScreenMonitoringTests(primary);
                VmSeatTests(primary, work, fakeVmware);
            } catch (Exception ex) { Record("运行", "测试程序异常", false, ex.ToString()); }
            finally { WindowTools.Unclip(); try { Directory.Delete(work, true); } catch { } }
            WriteReport(primary);
            Console.WriteLine(passed + " passed, " + failed + " failed");
            return failed == 0 ? 0 : 1;
        }

        static void ScreenMonitoringTests(Screen primary) {
            const string A = "屏幕监控";
            Check(A, "找到工位显示器", delegate {
                Screen s = ScreenCapture.Find(primary.DeviceName);
                return Expect(s != null && ScreenCapture.Find("\\\\.\\DISPLAY99") == null && ScreenCapture.Find("") == null,
                    "按设备名找到 " + primary.DeviceName + "，不存在/空名称返回 null", "查找结果不正确");
            });
            // 用全屏纯色窗口验证截取到的是“屏幕上的真实内容”，而不是空白图。
            var red = new Form { FormBorderStyle = FormBorderStyle.None, StartPosition = FormStartPosition.Manual, Bounds = primary.Bounds, BackColor = Color.FromArgb(220, 20, 30), TopMost = true, ShowInTaskbar = false };
            red.Show(); Pump(800);
            Check(A, "缩略图截取真实画面", delegate {
                using (Bitmap b = ScreenCapture.Grab(primary.Bounds, new Size(320, 180))) {
                    Color c = Center(b);
                    return Expect(b.Size == ScreenCapture.Fit(primary.Bounds.Size, new Size(320, 180)).Size && c.R > 180 && c.G < 70 && c.B < 80,
                        "缩略图 " + b.Width + "×" + b.Height + "，中心像素 RGB(" + c.R + "," + c.G + "," + c.B + ") 与屏幕上的红色窗口一致", "中心像素 RGB(" + c.R + "," + c.G + "," + c.B + ") 不是红色");
                }
            });
            Check(A, "截取性能", delegate {
                var sw = Stopwatch.StartNew();
                for (int i = 0; i < 10; i++) using (ScreenCapture.Grab(primary.Bounds, new Size(600, 170))) { }
                double ms = sw.ElapsedMilliseconds / 10.0;
                return Expect(ms < 150, "一块 " + primary.Bounds.Width + "×" + primary.Bounds.Height + " 屏幕平均 " + ms.ToString("0.0") + " ms/帧（主窗口三块屏每 500 ms 刷新一次）", "平均 " + ms.ToString("0.0") + " ms/帧，偏慢");
            });
            red.Close(); red.Dispose(); Pump(300);

            Check(A, "缩略图控件释放旧画面", delegate {
                var tile = new MonitorTile { Size = new Size(320, 180) };
                var first = new Bitmap(10, 10); tile.SetImage(first); tile.SetImage(new Bitmap(10, 10));
                bool disposed = false; try { int w = first.Width; } catch (ArgumentException) { disposed = true; }
                tile.ShowMessage("x"); bool cleared = Get(tile, "image") == null; tile.Dispose();
                return Expect(disposed && cleared, "换新画面时旧位图被释放；切换到提示文字时画面被清空（无内存泄漏）", "旧位图未释放=" + !disposed + "，提示时未清空=" + !cleared);
            });

            // 主窗口：给工位 1 分配显示器，检查缩略图、暂停开关、未分配工位的提示。
            var main = new MainWindow { StartPosition = FormStartPosition.Manual, Location = primary.WorkingArea.Location, WindowState = FormWindowState.Maximized };
            main.Show(); Pump(1500);
            var config = (Settings)Get(main, "config");
            config.Seats[0].Monitor = primary.DeviceName; config.Seats[0].Name = "前台";
            Call(main, "RefreshMonitors"); Pump(600);
            var tiles = (MonitorTile[])Get(main, "tiles");
            Check(A, "主窗口缩略图显示画面", delegate {
                var img = (Bitmap)Get(tiles[0], "image");
                return Expect(img != null && tiles[0].Caption.StartsWith("前台") && tiles[0].Caption.Contains("点击放大"),
                    "工位 1 缩略图 " + (img == null ? "无" : img.Width + "×" + img.Height) + "，标题“" + tiles[0].Caption + "”", "工位 1 没有画面或标题不对：“" + tiles[0].Caption + "”");
            });
            Check(A, "未分配工位的提示", delegate {
                string msg = (string)Get(tiles[1], "message");
                return Expect(Get(tiles[1], "image") == null && msg == "未分配在线显示器", "工位 2 显示“" + msg + "”", "工位 2 提示为“" + msg + "”");
            });
            Check(A, "缩略图自动刷新", delegate {
                object before = Get(tiles[0], "image");
                bool changed = PumpUntil(delegate { return !ReferenceEquals(before, Get(tiles[0], "image")); }, 2000);
                return Expect(changed, "主窗口定时器在 2 秒内换上了新一帧", "2 秒内画面没有更新");
            });
            Shot("1-主窗口-缩略图");
            Check(A, "暂停开关", delegate {
                var toggle = (CheckBox)Get(main, "monitorToggle");
                toggle.Checked = false; Pump(1200);
                bool paused = Get(tiles[0], "image") == null && (string)Get(tiles[0], "message") == "屏幕监控已暂停";
                toggle.Checked = true; Pump(800);
                bool resumed = Get(tiles[0], "image") != null;
                return Expect(paused && resumed, "取消勾选后三格显示“屏幕监控已暂停”且不再截图；重新勾选后恢复画面", "暂停=" + paused + "，恢复=" + resumed);
            });

            // 放大监控窗
            Check(A, "点击缩略图打开放大监控窗", delegate {
                Call(main, "OpenMonitor", 0); Pump(1500);
                var windows = (MonitorWindow[])Get(main, "monitorWindows");
                MonitorWindow w = windows[0];
                bool open = w != null && w.Visible;
                var view = (MonitorTile)Get(w, "view");
                object f1 = Get(view, "image");
                bool refreshed = PumpUntil(delegate { return Get(view, "image") != null && !ReferenceEquals(f1, Get(view, "image")); }, 1500);
                Call(main, "OpenMonitor", 0); Pump(300);
                bool single = ReferenceEquals(w, ((MonitorWindow[])Get(main, "monitorWindows"))[0]);
                Shot("2-放大监控窗");
                return Expect(open && refreshed && single && w.Text.Contains("只读"),
                    "窗口“" + w.Text + "”已打开并持续刷新；再次点击不会重复打开", "打开=" + open + "，刷新=" + refreshed + "，单实例=" + single);
            });
            Check(A, "Esc 关闭放大监控窗", delegate {
                MonitorWindow w = ((MonitorWindow[])Get(main, "monitorWindows"))[0];
                w.Activate(); Pump(300);
                SendKeys.SendWait("{ESC}"); bool closed = PumpUntil(delegate { return w.IsDisposed || !w.Visible; }, 2000);
                return Expect(closed, "按 Esc 后监控窗关闭", "按 Esc 后监控窗仍然打开");
            });
            Check(A, "监控窗：显示器被取消分配", delegate {
                string target = primary.DeviceName;
                var w = new MonitorWindow("测试", delegate { return target; }, primary.WorkingArea);
                w.Show(); Pump(700); bool hadImage = Get((MonitorTile)Get(w, "view"), "image") != null;
                target = ""; Pump(700);
                string msg = (string)Get((MonitorTile)Get(w, "view"), "message");
                w.Close(); w.Dispose();
                return Expect(hadImage && msg == "该工位未分配在线显示器", "有显示器时显示画面；取消分配后改为提示“" + msg + "”", "有画面=" + hadImage + "，提示=“" + msg + "”");
            });
            Check(A, "切换到双屏（2 个工位）", delegate {
                Call(main, "ChangeSeatCount", 2); Pump(800);
                var cardPanels = (FlowLayoutPanel[])Get(main, "cardPanels");
                string run = ((Button)Get(main, "runButton")).Text, status = ((Label)Get(main, "status")).Text;
                bool hidden = !tiles[2].Visible && !cardPanels[2].Visible && tiles[1].Visible && cardPanels[1].Visible;
                bool wide = tiles[0].Width > primary.WorkingArea.Width / 3;
                Shot("1b-主窗口-双屏");
                Call(main, "ChangeSeatCount", 3); Pump(600);
                bool back = tiles[2].Visible && cardPanels[2].Visible && ((Settings)Get(main, "config")).SeatCount == 3;
                return Expect(hidden && wide && run.Contains("双屏") && back,
                    "工位 3 的监控格和卡片隐藏，前两格加宽到半屏（" + tiles[0].Width + " px）；按钮“" + run + "”；切回三屏后恢复", "隐藏=" + hidden + "，加宽=" + wide + "，按钮=“" + run + "”，恢复=" + back);
            });
            Set(main, "dirty", false); main.Close(); main.Dispose(); Pump(300);
        }

        static string MakeVmx(string dir, string name) {
            Directory.CreateDirectory(dir);
            string path = Path.Combine(dir, name + ".vmx");
            File.WriteAllText(path, ".encoding = \"UTF-8\"\r\nconfig.version = \"8\"\r\ndisplayName = \"" + name + "\"\r\nmemsize = \"8192\"\r\nusb.present = \"FALSE\"\r\nusb.autoConnect.device0 = \"vid:0x9999 pid:0x9999\"\r\n", new UTF8Encoding(false));
            return path;
        }

        static void VmSeatTests(Screen primary, string work, string fakeVmware) {
            const string A = "虚拟机工位";
            string vm2 = MakeVmx(Path.Combine(work, "虚拟机 2"), "工位2");
            string vm3 = MakeVmx(Path.Combine(work, "虚拟机 3"), "工位3");
            string original2 = File.ReadAllText(vm2);
            var config = new Settings();
            config.Seats[0].Mouse = @"\\?\HID#VID_046D&PID_C077#7&1&0000#{378de44c-56ef-11d1-bc8c-00a0c91e6bf6}";
            config.Seats[1].Mouse = @"\\?\HID#VID_1532&PID_0084&MI_00#8&2&0000#{378de44c-56ef-11d1-bc8c-00a0c91e6bf6}";
            config.Seats[1].Keyboard = @"\\?\HID#VID_04F2&PID_0112#8&3&0000#{884b96c3-56ef-11d1-bc8c-00a0c91e6bf6}";
            config.Seats[2].Mouse = @"\\?\HID#VID_045E&PID_07B2&MI_01&Col01#9&4&0000#{378de44c-56ef-11d1-bc8c-00a0c91e6bf6}";
            config.Seats[2].Keyboard = @"\\?\HID#VID_045E&PID_07B2&MI_00#9&5&0000#{884b96c3-56ef-11d1-bc8c-00a0c91e6bf6}";
            config.Seats[1].VmxPath = vm2; config.Seats[2].VmxPath = vm3;
            // 测试机只有一块屏幕：三个工位都指向它，只用于验证启动与窗口定位流程。
            foreach (Seat s in config.Seats) s.Monitor = primary.DeviceName;

            Check(A, "配置保存/读取虚拟机字段", delegate {
                string file = Path.Combine(work, "config.xml");
                var copy = new Settings(); copy.Seats[1].VmxPath = vm2; copy.Seats[2].VmxPath = vm3; copy.VmwarePath = fakeVmware;
                copy.Save(file); Settings back = Settings.Load(file);
                bool dupRejected = false; copy.Seats[2].VmxPath = vm2.ToUpperInvariant(); try { copy.Validate(); } catch (InvalidDataException) { dupRejected = true; }
                return Expect(back.Seats[1].VmxPath == vm2 && back.Seats[2].VmxPath == vm3 && back.VmwarePath == fakeVmware && dupRejected,
                    "中文和空格路径往返一致；两个工位选同一台虚拟机被拒绝", "往返或重复检查失败");
            });

            VmSeatsWindow window = null;
            Check(A, "打开虚拟机工位窗口 + 方案检查", delegate {
                window = new VmSeatsWindow(config, delegate { });
                window.Show(); Pump(1000);
                string status = ((Label)Get(window, "status")).Text;
                return Expect(status.StartsWith("方案检查通过"), "状态栏：“" + status + "”", "状态栏：“" + status + "”");
            });
            Check(A, "① 写入 USB 直通配置（真实 .vmx 文件）", delegate {
                Call(window, "WriteAll"); Pump(300);
                string v2 = File.ReadAllText(vm2), v3 = File.ReadAllText(vm3);
                bool ok2 = v2.Contains("usb.autoConnect.device0 = \"vid:0x1532 pid:0x0084\"") && v2.Contains("usb.autoConnect.device1 = \"vid:0x04f2 pid:0x0112\"")
                    && v2.Contains("usb.present = \"TRUE\"") && v2.Contains("usb.generic.allowHID = \"TRUE\"") && !v2.Contains("0x9999") && !v2.Contains("allowLastHID") && v2.Contains("memsize = \"8192\"");
                bool ok3 = v3.Contains("usb.autoConnect.device0 = \"vid:0x045e pid:0x07b2\"") && !v3.Contains("usb.autoConnect.device1");
                bool hostUntouched = !v2.Contains("0x046d") && !v3.Contains("0x046d");
                bool backup = File.ReadAllText(vm2 + ".openseat-backup") == original2;
                Shot("3-虚拟机工位窗口-写入后");
                return Expect(ok2 && ok3 && hostUntouched && backup,
                    "工位2 写入鼠标+键盘两条；工位3 的一体接收器（同 VID/PID）合并为一条；主机鼠标未被写入；原文件已备份；旧的直通行已清除，其他设置保留",
                    "工位2=" + ok2 + "，工位3=" + ok3 + "，主机未写入=" + hostUntouched + "，备份=" + backup);
            });
            Check(A, "再次写入不覆盖原始备份", delegate {
                Call(window, "WriteAll");
                bool ok = File.ReadAllText(vm2 + ".openseat-backup") == original2 && File.ReadAllText(vm2).Split(new[] { "usb.autoConnect.device0" }, StringSplitOptions.None).Length == 2;
                return Expect(ok, "备份仍是最初的文件；直通行没有重复", "备份被覆盖或出现重复行");
            });
            Check(A, "虚拟机运行中拒绝写入", delegate {
                Directory.CreateDirectory(vm2 + ".lck");
                bool refused = false; string msg = "";
                try { VmPlan.WriteVmx(vm2, VmPlan.DevicesFor(config.Seats[1])); } catch (InvalidOperationException ex) { refused = true; msg = ex.Message; }
                Directory.Delete(vm2 + ".lck");
                return Expect(refused, "检测到 .lck 文件夹时拒绝修改：“" + msg + "”", "运行中仍然写入了 .vmx");
            });
            Check(A, "方案检查拦截危险配置", delegate {
                string keep = config.Seats[2].Mouse; var problems = new List<string>();
                config.Seats[2].Mouse = @"\\?\HID#VID_046D&PID_C077#9&9&0000#{378de44c-56ef-11d1-bc8c-00a0c91e6bf6}"; problems.Add(VmPlan.Check(config));
                config.Seats[2].Mouse = @"\\?\HID#{00001124-0000-1000-8000-00805f9b34fb}_VID&0002046d_PID&b023&Col01#8&1&0000#{378de44c-56ef-11d1-bc8c-00a0c91e6bf6}"; problems.Add(VmPlan.Check(config));
                config.Seats[2].Mouse = ""; problems.Add(VmPlan.Check(config));
                config.Seats[2].Mouse = keep;
                bool all = problems.TrueForAll(delegate(string p) { return p != ""; }) && VmPlan.Check(config) == "";
                return Expect(all, "与主机同型号、蓝牙鼠标、未绑鼠标都被拦截（例：" + problems[0] + "）", "有危险配置未被拦截：" + String.Join(" / ", problems.ToArray()));
            });

            string log = Path.Combine(work, "fake-vmware.log");
            Environment.SetEnvironmentVariable("OPENSEAT_FAKE_VMWARE_LOG", log);
            config.VmwarePath = fakeVmware;
            Check(A, "② 启动虚拟机工位：命令行参数", delegate {
                if (!File.Exists(fakeVmware)) return "FAIL:找不到假 vmware.exe：" + fakeVmware;
                Call(window, "LaunchAll");
                bool started = PumpUntil(delegate { return File.Exists(log) && File.ReadAllLines(log).Length >= 2; }, 15000);
                string[] lines = started ? File.ReadAllLines(log) : new string[0];
                bool ok = lines.Length == 2 && Array.Exists(lines, delegate(string l) { return l == "-n|-x|" + vm2; }) && Array.Exists(lines, delegate(string l) { return l == "-n|-x|" + vm3; });
                return Expect(ok, "两台虚拟机各启动一次，参数为 -n -x \"<路径>\"，含中文和空格的路径完整传入", "收到的参数：" + String.Join(" ; ", lines));
            });
            Check(A, "② 启动虚拟机工位：窗口移到目标屏幕并最大化", delegate {
                IntPtr h2 = IntPtr.Zero, h3 = IntPtr.Zero;
                bool placed = PumpUntil(delegate {
                    h2 = WindowTools.FindByTitle("工位2"); h3 = WindowTools.FindByTitle("工位3");
                    return h2 != IntPtr.Zero && h3 != IntPtr.Zero && IsZoomed(h2) && IsZoomed(h3);
                }, 20000);
                Pump(500); Shot("4-虚拟机窗口已最大化");
                string status = ((Label)Get(window, "status")).Text;
                bool onScreen = h2 != IntPtr.Zero && Screen.FromHandle(h2).DeviceName == primary.DeviceName;
                return Expect(placed && onScreen, "找到“工位2/工位3 - VMware Workstation”窗口，均已最大化在 " + primary.DeviceName + "；状态栏：“" + status + "”", "定位失败：工位2=" + h2 + "，工位3=" + h3 + "；状态栏：“" + status + "”");
            });
            foreach (Process p in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(fakeVmware))) { try { p.Kill(); } catch { } }
            Pump(500);

            Check(A, "主机鼠标锁定（勾选框）", delegate {
                var lockBox = (CheckBox)Get(window, "lockBox");
                lockBox.Checked = true; Pump(400);
                WindowTools.RECT r; GetClipCursor(out r);
                bool locked = r.Left == primary.Bounds.Left && r.Top == primary.Bounds.Top && r.Right == primary.Bounds.Right && r.Bottom == primary.Bounds.Bottom;
                WindowTools.Unclip(); Pump(600); GetClipCursor(out r);
                bool reapplied = r.Right == primary.Bounds.Right && r.Bottom == primary.Bounds.Bottom && r.Left == primary.Bounds.Left;
                lockBox.Checked = false; Pump(300); GetClipCursor(out r);
                Rectangle v = SystemInformation.VirtualScreen;
                bool released = r.Left == v.Left && r.Top == v.Top && r.Right == v.Right && r.Bottom == v.Bottom;
                return Expect(locked && reapplied && released, "勾选后光标被限制在主机工位屏幕；被其他程序解除后 250 ms 内自动恢复；取消勾选后完全释放", "锁定=" + locked + "，自动恢复=" + reapplied + "，释放=" + released);
            });
            Check(A, "主机鼠标锁定（Ctrl+Alt+F12 热键）", delegate {
                var lockBox = (CheckBox)Get(window, "lockBox");
                SendMessage(window.Handle, 0x0312, new IntPtr(0x4F53), IntPtr.Zero); Pump(300); bool on = lockBox.Checked;
                SendMessage(window.Handle, 0x0312, new IntPtr(0x4F53), IntPtr.Zero); Pump(300); bool off = !lockBox.Checked;
                bool registered = !WindowTools.RegisterHotKey(IntPtr.Zero, 0x5151, 0x0001 | 0x0002, (uint)Keys.F12);
                if (!registered) WindowTools.UnregisterHotKey(IntPtr.Zero, 0x5151);
                return Expect(on && off && registered, "热键已向系统注册（其他程序无法再占用 Ctrl+Alt+F12）；按一次锁定、再按一次解除", "开=" + on + "，关=" + off + "，已注册=" + registered);
            });
            Check(A, "关闭窗口时自动释放鼠标", delegate {
                ((CheckBox)Get(window, "lockBox")).Checked = true; Pump(300);
                window.Close(); Pump(400);
                WindowTools.RECT r; GetClipCursor(out r); Rectangle v = SystemInformation.VirtualScreen;
                bool released = r.Right == v.Right && r.Bottom == v.Bottom && r.Left == v.Left && r.Top == v.Top;
                bool freed = WindowTools.RegisterHotKey(IntPtr.Zero, 0x5152, 0x0001 | 0x0002, (uint)Keys.F12);
                if (freed) WindowTools.UnregisterHotKey(IntPtr.Zero, 0x5152);
                return Expect(released && freed, "关闭虚拟机工位窗口后光标恢复自由，热键也被释放", "光标释放=" + released + "，热键释放=" + freed);
            });
            Check(A, "还原 .vmx 备份", delegate {
                var w = new VmSeatsWindow(config, delegate { }); w.Show(); Pump(500);
                Call(w, "RestoreAll"); string status = ((Label)Get(w, "status")).Text; w.Close();
                return Expect(File.ReadAllText(vm2) == original2 && status.Contains("2"), "两台虚拟机的 .vmx 恢复为原始内容；状态栏：“" + status + "”", "还原后内容不一致；状态栏：“" + status + "”");
            });
            Check(A, "双屏：一个主机工位 + 一台虚拟机", delegate {
                config.SeatCount = 2; string ok = VmPlan.Check(config);
                var w = new VmSeatsWindow(config, delegate { }); w.Show(); Pump(600);
                string status = ((Label)Get(w, "status")).Text, title = w.Text;
                bool thirdRowHidden = Get(w, "paths") is TextBox[] && ((TextBox[])Get(w, "paths"))[2] == null;
                w.Close(); config.SeatCount = 3;
                return Expect(ok == "" && status.StartsWith("方案检查通过") && title.Contains("双屏") && thirdRowHidden,
                    "方案检查通过，窗口标题“" + title + "”，只显示两个工位", "检查=“" + ok + "”，状态=“" + status + "”，第三行隐藏=" + thirdRowHidden);
            });
            Check(A, "查找 vmware.exe", delegate {
                string configured = VmPlan.FindVmware(fakeVmware), auto = VmPlan.FindVmware("");
                return Expect(configured == fakeVmware, "手动指定的路径被采用；自动查找结果：" + (auto ?? "未安装 VMware（测试机正常情况）"), "手动指定的路径未被采用");
            });
        }

        static void WriteReport(Screen primary) {
            var b = new StringBuilder();
            b.AppendLine("# OpenSeat " + Program.AppVersion + " 集成测试报告");
            b.AppendLine();
            b.AppendLine("- 时间（UTC）：" + DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss"));
            b.AppendLine("- 系统：" + Environment.OSVersion + "，64 位进程：" + Environment.Is64BitProcess);
            b.AppendLine("- 显示器：" + Screen.AllScreens.Length + " 块，主屏 " + primary.Bounds.Width + "×" + primary.Bounds.Height);
            b.AppendLine("- 结果：**" + passed + " 项通过，" + failed + " 项失败**");
            b.AppendLine();
            b.AppendLine("| 功能 | 测试项 | 结果 | 说明 |");
            b.AppendLine("|---|---|---|---|");
            foreach (string r in rows) b.AppendLine(r);
            File.WriteAllText(Path.Combine(outDir, "report.md"), b.ToString(), new UTF8Encoding(false));
        }
    }
}
