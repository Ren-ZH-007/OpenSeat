using System;
using System.Drawing;
using System.IO;
using System.Text.RegularExpressions;
using System.Windows.Forms;

// 测试用的假 vmware.exe：记录收到的命令行，按 .vmx 的 displayName 打开一个
// 标题为 “<名称> - VMware Workstation” 的窗口，位置故意放在左上角小窗口。
public static class FakeVmware {
    [STAThread]
    public static int Main(string[] args) {
        string log = Environment.GetEnvironmentVariable("OPENSEAT_FAKE_VMWARE_LOG");
        string vmx = args.Length > 0 ? args[args.Length - 1] : "";
        // 两台“虚拟机”几乎同时启动，会同时写同一个日志：文件被占用时重试，避免测试工具自身崩溃。
        if (!String.IsNullOrEmpty(log))
            for (int attempt = 0; ; attempt++) {
                try { File.AppendAllText(log, String.Join("|", args) + Environment.NewLine); break; }
                catch (IOException) { if (attempt >= 100) throw; System.Threading.Thread.Sleep(20); }
            }
        string name = Path.GetFileNameWithoutExtension(vmx);
        if (File.Exists(vmx)) {
            Match m = Regex.Match(File.ReadAllText(vmx), "^\\s*displayName\\s*=\\s*\"([^\"]*)\"", RegexOptions.Multiline);
            if (m.Success) name = m.Groups[1].Value;
        }
        var form = new Form { Text = name + " - VMware Workstation", StartPosition = FormStartPosition.Manual, Bounds = new Rectangle(40, 40, 480, 320), BackColor = Color.FromArgb(40, 90, 160) };
        form.Controls.Add(new Label { Dock = DockStyle.Fill, Text = "FAKE VM: " + name, ForeColor = Color.White, Font = new Font("Microsoft YaHei UI", 28), TextAlign = ContentAlignment.MiddleCenter });
        var close = new Timer { Interval = 60000 }; close.Tick += delegate { form.Close(); }; close.Start();
        Application.Run(form);
        return 0;
    }
}
