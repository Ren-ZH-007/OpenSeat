using System;
using System.IO;
using System.Diagnostics;
using System.Reflection;
using System.Windows.Forms;
namespace OpenSeatGaming {
    public static class GamingTests {
        static int count;
        static void Assert(bool value, string name) { if (!value) throw new Exception(name); count++; }
        static void Reject(Action action, string name) {
            bool failed = false; try { action(); } catch (Exception) { failed = true; }
            Assert(failed, name);
        }
        public static int Run() {
            string dir = Path.Combine(Path.GetTempPath(), "OpenSeat-gaming-test-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            try {
                var config = new GamingConfig(); config.Validate();
                Assert(config.Games.Length == 3 && config.Seat == 1, "three default profiles");
                config.Seat = 0; Reject(config.Validate, "invalid seat rejected"); config.Seat = 1;
                config.Version = 2; Reject(config.Validate, "unknown schema rejected"); config.Version = 1;
                config.Games[0].Arguments = "hello\nworld"; Reject(config.Validate, "newline rejected"); config.Games[0].Arguments = "";
                config.Games[0].Executable = "relative.exe"; Reject(config.Validate, "relative path rejected");
                config.Games[0].Executable = Path.Combine(dir, "launch.cmd"); Reject(config.Validate, "shell script rejected");
                config.Games[0].Executable = "";
                config.Monitor = null; Reject(config.Validate, "null monitor rejected"); config.Monitor = "";
                string path = Path.Combine(dir, "config.xml"); config.Save(path);
                Assert(GamingConfig.Load(path).Games[1].Name == "三角洲行动（国服）", "unicode config round trip");
                config.Seat = 3; config.Save(path); Assert(GamingConfig.Load(path).Seat == 3, "atomic replacement");
                config.Seat = 4; Reject(delegate { config.Save(path); }, "invalid save rejected");
                Assert(GamingConfig.Load(path).Seat == 3, "invalid save preserves previous config"); config.Seat = 3;
                string xml = File.ReadAllText(path).Replace("<Seat>3</Seat>", "<Seat>9</Seat>"); File.WriteAllText(path, xml);
                Reject(delegate { GamingConfig.Load(path); }, "invalid disk values rejected");
                File.WriteAllText(path, "<!DOCTYPE x [<!ENTITY xxe SYSTEM 'file:///etc/passwd'>]><GamingConfig>&xxe;</GamingConfig>");
                Reject(delegate { GamingConfig.Load(path); }, "DTD prohibited");
                Reject(delegate { Launcher.Prepare(config.Games[0]); }, "unconfigured game rejected");
                config.Games[0].Executable = Path.Combine(dir, "not-installed.exe");
                Reject(delegate { Launcher.Prepare(config.Games[0]); }, "missing exe rejected");
                string fake = Path.Combine(dir, "游戏 launcher.exe"); File.WriteAllText(fake, "not executed");
                config.Games[0].Executable = fake; config.Games[0].Arguments = "--label \"a b\" & unchanged";
                var start = Launcher.Prepare(config.Games[0]);
                Assert(!start.UseShellExecute && start.FileName == fake, "direct launch without command shell");
                Assert(start.Arguments == config.Games[0].Arguments && start.WorkingDirectory == dir, "arguments preserved and cwd selected");
                Reject(delegate { Launcher.Move(null, null); }, "offline screen rejected before native call");

                // Real Windows process creation: test the same launch path with a harmless child.
                if (Environment.OSVersion.Platform == PlatformID.Win32NT) {
                    var probe = new GameProfile { Name = "test child", Executable = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe"), Arguments = "/d /c exit 23" };
                    using (Process child = Process.Start(Launcher.Prepare(probe))) {
                        Assert(child != null && child.WaitForExit(10000), "actual child exits within timeout");
                        Assert(child.ExitCode == 23, "actual process exit code preserved");
                    }
                    var sessions = Sessions.Read();
                    using (Process current = Process.GetCurrentProcess())
                        Assert(sessions.Exists(delegate(SessionInfo item) { return item.Id == current.SessionId; }) || current.SessionId == 0, "WTS inventory contains current interactive session");

                    // Window construction and event-loop smoke test, not a visual/hardware acceptance test.
                    Application.EnableVisualStyles();
                    Application.SetCompatibleTextRenderingDefault(false);
                    using (var window = new GamingWindow()) {
                        window.Show(); Application.DoEvents();
                        Assert(window.IsHandleCreated && window.Visible, "gaming window opens");
                        var flags = BindingFlags.NonPublic | BindingFlags.Instance;
                        var executables = (TextBox[])typeof(GamingWindow).GetField("executables", flags).GetValue(window);
                        var argsBoxes = (TextBox[])typeof(GamingWindow).GetField("arguments", flags).GetValue(window);
                        var handles = (Process[])typeof(GamingWindow).GetField("processes", flags).GetValue(window);
                        var labels = (Label[])typeof(GamingWindow).GetField("states", flags).GetValue(window);
                        Assert(executables.Length == 3 && argsBoxes.Length == 3, "three editable game profiles created");
                        // Use the actual StartGame path, not just ProcessStartInfo construction.
                        executables[0].Text = probe.Executable; argsBoxes[0].Text = probe.Arguments;
                        typeof(GamingWindow).GetMethod("StartGame", flags).Invoke(window, new object[] { 0 });
                        Assert(handles[0] != null && handles[0].WaitForExit(10000), "UI start path launches child");
                        typeof(GamingWindow).GetMethod("RefreshProcesses", flags).Invoke(window, null);
                        Assert(handles[0] == null && labels[0].Text.Contains("游戏状态未知"), "exited launcher is not reported as stopped game");
                        typeof(GamingWindow).GetMethod("RefreshInventory", flags).Invoke(window, null);
                        Assert(window.IsHandleCreated, "inventory refresh keeps window usable");
                        typeof(GamingWindow).GetField("dirty", flags).SetValue(window, false); // discard test-only edits; never write user config
                        window.Close(); Application.DoEvents();
                        Assert(window.IsDisposed, "window closes cleanly");
                    }
                }

                Console.WriteLine(count + " gaming checks passed. Hardware isolation and game compatibility NOT validated."); return 0;
            } catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
            finally { Directory.Delete(dir, true); }
        }
    }
}
