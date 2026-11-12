using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using TailcatTray;
class Tests {
    static void Check(bool value, string message) { if (!value) throw new Exception(message); Console.WriteLine("PASS " + message); }
    static void Ready(Tunnel tunnel) {
        for (int i = 0; i < 50 && tunnel.Address == null; i++) { Thread.Sleep(50); tunnel.Poll(); }
        Check(tunnel.Address != null, "startup reads JSON address");
    }
    [STAThread] static void Main() {
        string root = AppDomain.CurrentDomain.BaseDirectory;
        string keyFile = Path.Combine(root, "test-key.txt");
        try {
            File.WriteAllText(keyFile, "first log entry");
            using (var viewer = new LogWindow(keyFile)) {
                viewer.Reload();
                Check(viewer.LogText == "first log entry", "built-in log viewer reads existing log");
                File.AppendAllText(keyFile, "\r\nsecond log entry");
                viewer.Reload();
                Check(viewer.LogText.Contains("second log entry"), "built-in log viewer refreshes new entries");
                File.Delete(keyFile);
                viewer.Reload();
                Check(viewer.LogText.StartsWith("Unable to read log:"), "log viewer displays file errors");
            }
            string key = "nodekey:" + new string('a', 64);
            Check(Tunnel.Arguments(key, false).EndsWith(" 22"), "SSH serves existing port 22");
            Check(Tunnel.Arguments(key, true).EndsWith(" 22,3389"), "SSH + RDP serves both ports");
            var persistent = new Settings { ServerKey = "home-pc" };
            Check(Tunnel.Arguments(key, false, persistent).Contains("--key=home-pc --allow=" + key), "persistent key retains client allowlist");
            persistent.ServerKey = "home-pc --allow=none";
            bool badServerKey = false;
            try { Tunnel.Arguments(key, false, persistent); } catch (InvalidDataException) { badServerKey = true; }
            Check(badServerKey, "server key cannot inject arguments");
            bool rejected = false;
            try { Tunnel.Arguments(key + " --allow=", false); } catch (InvalidDataException) { rejected = true; }
            Check(rejected, "invalid allowlist fails closed");
            Check(Tunnel.ReadAddress("diagnostic text") == null, "diagnostic text is not an address");
            Check(Tunnel.ReadAddress("{\"listenAddr\":\"invalid\"}") == null, "invalid address rejected");
            File.WriteAllText(keyFile, key + "\r\n");
            var settings = new Settings { TailcatPath = Path.Combine(root, "FakeTailcat.exe"), MacKeyPath = keyFile };
            var current = new Settings { TailcatPath = settings.TailcatPath, MacKeyPath = keyFile, SshIdentity = "current-user", SshAuthorizedKeysPath = keyFile };
            Check(Tunnel.Arguments(key, false, current).EndsWith(" ssh"), "current user uses built-in SSH");
            Check(Tunnel.Arguments(key, true, current).Contains("--ssh-authorized-keys=\"" + keyFile + "\" ssh,3389"), "current user requires authorized keys with optional RDP");
            string config = Path.Combine(root, "test-settings.json");
            try {
                File.WriteAllText(config, "{}"); current.Save(config);
                Check(Settings.Load(config).SshIdentity == "current-user", "identity persists in settings");
            } finally { if (File.Exists(config)) File.Delete(config); }
            Check(Cli.Valid("identity current-user") && Cli.Valid("enable ssh-rdp") && !Cli.Valid("enable all"), "CLI limits commands to supported operations");
            using (var tunnel = new Tunnel()) {
                string log = "";
                tunnel.Log = delegate(string text) { log += text; };
                tunnel.Start(settings, false);
                Ready(tunnel);
                Check(tunnel.Mode == "SSH only", "SSH mode selected");
                Check(log.Contains("Tailcat address: tcTEST_ADDRESS_12345678901234567890"), "full address included in log");
                tunnel.Start(settings, true);
                Ready(tunnel);
                Check(tunnel.Mode == "SSH + RDP", "mode switch replaces tunnel");
                Check(Process.GetProcessesByName("FakeTailcat").Length == 1, "only one owned process after switch");
                tunnel.Start(current, true);
                Ready(tunnel);
                Check(Process.GetProcessesByName("FakeTailcat").Length == 1, "current-user identity replaces Windows SSH tunnel");
                tunnel.Stop();
                Check(!tunnel.Running && tunnel.Address == null, "disable clears state and address");
                Thread.Sleep(100);
                Check(Process.GetProcessesByName("FakeTailcat").Length == 0, "disable stops child process");
                File.WriteAllText(keyFile, "nodekey:" + new string('b', 64));
                tunnel.Start(settings, false);
                bool exitDetected = false;
                for (int i = 0; i < 50 && !exitDetected; i++) {
                    Thread.Sleep(50);
                    try { tunnel.Poll(); } catch (InvalidOperationException) { exitDetected = true; }
                }
                Check(exitDetected && tunnel.Address == null, "unexpected exit clears stale address");
            }
            File.WriteAllText(keyFile, key);
            using (var tunnel = new Tunnel()) { tunnel.Start(settings, false); Ready(tunnel); }
            Thread.Sleep(100);
            Check(Process.GetProcessesByName("FakeTailcat").Length == 0, "dispose stops owned process");
        } finally { if (File.Exists(keyFile)) File.Delete(keyFile); }
    }
}
