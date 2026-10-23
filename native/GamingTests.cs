using System;
using System.IO;
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
                Console.WriteLine(count + " gaming checks passed. Hardware isolation and game compatibility NOT validated."); return 0;
            } catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
            finally { Directory.Delete(dir, true); }
        }
    }
}
