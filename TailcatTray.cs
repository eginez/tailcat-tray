using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading;
using System.Web.Script.Serialization;
using System.Windows.Forms;

namespace TailcatTray {
    public sealed class Settings {
        public static string UserPath { get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config", "tailcat-tray", "settings.json"); } }
        public string TailcatPath { get; set; }
        public string MacKeyPath { get; set; }
        public string SshIdentity { get; set; }
        public string SshAuthorizedKeysPath { get; set; }
        public string ServerKey { get; set; }
        public string ServerKeyPath { get; set; }
        public Settings() {
            string user = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            TailcatPath = Path.Combine(user, "scoop", "apps", "tailcat", "current", "tailcat.exe");
            MacKeyPath = Path.Combine(user, ".config", "tailcat", "nodekeys");
            ServerKey = "new";
            SshIdentity = "windows";
            SshAuthorizedKeysPath = @"C:\ProgramData\ssh\administrators_authorized_keys";
        }
        public static Settings Load(string path) {
            if (!File.Exists(path)) {
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                new Settings().Save(path);
            }
            var value = new JavaScriptSerializer().Deserialize<Settings>(File.ReadAllText(path));
            if (value == null || String.IsNullOrWhiteSpace(value.TailcatPath) || String.IsNullOrWhiteSpace(value.MacKeyPath))
                throw new InvalidDataException("Set TailcatPath and MacKeyPath in settings.json.");
            if (value.SshIdentity != "windows" && value.SshIdentity != "current-user")
                throw new InvalidDataException("SshIdentity must be windows or current-user.");
            return value;
        }
        public void Save(string path) {
            string temporary = path + ".tmp";
            File.WriteAllText(temporary, new JavaScriptSerializer().Serialize(this));
            if (File.Exists(path)) File.Replace(temporary, path, null);
            else File.Move(temporary, path);
        }
    }

    public sealed class Tunnel : IDisposable {
        Process child;
        Job job;
        ConcurrentQueue<string> output = new ConcurrentQueue<string>();
        DateTime started;
        public string Address { get; private set; }
        public string Mode { get; private set; }
        public bool Running { get { return child != null && !child.HasExited; } }
        public Action<string> Log = delegate { };
        public static string Arguments(string key, bool rdp) {
            return Arguments(key, rdp, new Settings());
        }
        public static string Arguments(string key, bool rdp, Settings settings) {
            if (!Regex.IsMatch(key, @"\Anodekey:[a-fA-F0-9]{64}\z"))
                throw new InvalidDataException("The Mac key file must contain one nodekey: followed by 64 hexadecimal characters.");
            if (String.IsNullOrWhiteSpace(settings.ServerKey) || !Regex.IsMatch(settings.ServerKey, @"\A[a-zA-Z0-9_-]+\z"))
                throw new InvalidDataException("ServerKey must be a saved key name or new.");
            string args = "--json serve --key=" + settings.ServerKey + " --allow=" + key;
            if (!String.IsNullOrEmpty(settings.ServerKeyPath)) {
                if (!Path.IsPathRooted(settings.ServerKeyPath) || settings.ServerKeyPath.IndexOfAny(new char[] { '"', '\r', '\n' }) >= 0 || !settings.ServerKeyPath.EndsWith(".private.json"))
                    throw new InvalidDataException("Invalid server key path.");
                if (!File.Exists(settings.ServerKeyPath)) throw new FileNotFoundException("Saved server key not found.");
                args = "--json serve --key=\"" + settings.ServerKeyPath + "\" --allow=" + key;
            }
            if (settings.SshIdentity == "windows") return args + (rdp ? " 22,3389" : " 22");
            if (settings.SshIdentity != "current-user") throw new InvalidDataException("Unknown SSH identity.");
            string path = settings.SshAuthorizedKeysPath;
            if (String.IsNullOrWhiteSpace(path) || path.IndexOfAny(new char[] { '"', '\r', '\n', ',' }) >= 0 || path.EndsWith("\\"))
                throw new InvalidDataException("Set SshAuthorizedKeysPath to a single authorized keys file.");
            if (!File.Exists(path)) throw new FileNotFoundException("SSH authorized keys file not found.", path);
            return args + " --ssh-authorized-keys=\"" + path + "\"" + (rdp ? " ssh,3389" : " ssh");
        }
        public static string ReadAddress(string line) {
            try {
                var obj = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(line);
                object value;
                if (obj != null && obj.TryGetValue("listenAddr", out value)) {
                    var address = value as string;
                    if (address != null && Regex.IsMatch(address, @"\Atc[A-Za-z0-9_-]+\z")) return address;
                }
            } catch (ArgumentException) { } catch (InvalidOperationException) { }
            return null;
        }
        public void Start(Settings settings, bool rdp) {
            if (!File.Exists(settings.TailcatPath)) throw new FileNotFoundException("Tailcat executable not found.", settings.TailcatPath);
            string args = Arguments(File.ReadAllText(settings.MacKeyPath).Trim(), rdp, settings);
            Stop();
            output = new ConcurrentQueue<string>();
            var queue = output;
            var info = new ProcessStartInfo(settings.TailcatPath, args) {
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true
            };
            info.EnvironmentVariables.Remove("TAILCAT_ADDR_FILE");
            child = new Process { StartInfo = info };
            child.OutputDataReceived += delegate(object sender, DataReceivedEventArgs e) { if (e.Data != null) queue.Enqueue(e.Data); };
            child.ErrorDataReceived += delegate(object sender, DataReceivedEventArgs e) { if (e.Data != null) queue.Enqueue(e.Data); };
            try {
                job = new Job();
                child.Start();
                job.Add(child);
                child.BeginOutputReadLine();
                child.BeginErrorReadLine();
                started = DateTime.UtcNow;
                Mode = rdp ? "SSH + RDP" : "SSH only";
                Log("Starting " + Mode + " (SSH identity: " + settings.SshIdentity + ").");
            } catch { Stop(); throw; }
        }
        public void Poll() {
            string line;
            while (output.TryDequeue(out line)) {
                string address = ReadAddress(line);
                if (address != null) { Address = address; Log("Tailcat address: " + address); }
                else Log(line);
            }
            if (child != null && child.HasExited) {
                int code = child.ExitCode;
                Stop();
                throw new InvalidOperationException("Tailcat stopped (exit code " + code + "). See View log.");
            }
            if (child != null && Address == null && DateTime.UtcNow - started > TimeSpan.FromSeconds(45)) {
                Stop();
                throw new TimeoutException("Tailcat did not become ready within 45 seconds. See View log.");
            }
        }
        public void Stop() {
            Address = null;
            Mode = null;
            if (job != null) { job.Dispose(); job = null; }
            if (child != null) {
                try { if (!child.HasExited) { child.Kill(); child.WaitForExit(3000); } }
                catch (InvalidOperationException) { }
                finally { child.Dispose(); child = null; }
                Log("Tunnel stopped.");
            }
        }
        public void Dispose() { Stop(); }
    }

    // Closing this job also stops Tailcat if the tray process is terminated.
    sealed class Job : IDisposable {
        IntPtr handle;
        [StructLayout(LayoutKind.Sequential)] struct Basic {
            public long ProcessTime, JobTime;
            public uint Flags;
            public UIntPtr MinWorkingSet, MaxWorkingSet;
            public uint ActiveProcesses;
            public UIntPtr Affinity;
            public uint Priority, Scheduling;
        }
        [StructLayout(LayoutKind.Sequential)] struct Limits {
            public Basic Basic;
            public ulong ReadOps, WriteOps, OtherOps, ReadBytes, WriteBytes, OtherBytes;
            public UIntPtr ProcessMemory, JobMemory, PeakProcessMemory, PeakJobMemory;
        }
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern IntPtr CreateJobObject(IntPtr attributes, string name);
        [DllImport("kernel32.dll", SetLastError = true)] static extern bool SetInformationJobObject(IntPtr job, int type, ref Limits limits, uint size);
        [DllImport("kernel32.dll", SetLastError = true)] static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);
        [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr handle);
        public Job() {
            handle = CreateJobObject(IntPtr.Zero, null);
            var limits = new Limits();
            limits.Basic.Flags = 0x2000;
            if (handle == IntPtr.Zero || !SetInformationJobObject(handle, 9, ref limits, (uint)Marshal.SizeOf(typeof(Limits)))) {
                int error = Marshal.GetLastWin32Error(); Dispose(); throw new System.ComponentModel.Win32Exception(error);
            }
        }
        public void Add(Process process) {
            if (!AssignProcessToJobObject(handle, process.Handle)) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        }
        public void Dispose() { if (handle != IntPtr.Zero) { CloseHandle(handle); handle = IntPtr.Zero; } }
    }

    public sealed class LogWindow : Form {
        readonly string path;
        readonly TextBox text = new TextBox { Multiline = true, ReadOnly = true, Dock = DockStyle.Fill, ScrollBars = ScrollBars.Both, WordWrap = false };
        readonly System.Windows.Forms.Timer refresh = new System.Windows.Forms.Timer { Interval = 1000 };
        public string LogText { get { return text.Text; } }
        public LogWindow(string logPath) {
            path = logPath;
            Text = "Tailcat Tray - Log";
            Size = new Size(850, 450);
            StartPosition = FormStartPosition.CenterScreen;
            text.Font = new Font(FontFamily.GenericMonospace, 10);
            Controls.Add(text);
            refresh.Tick += delegate { Reload(); };
            Shown += delegate { Reload(); refresh.Start(); };
        }
        public void Reload() {
            string contents;
            try {
                using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                using (var reader = new StreamReader(stream)) contents = reader.ReadToEnd();
            } catch (IOException e) { contents = "Unable to read log: " + e.Message; }
              catch (UnauthorizedAccessException e) { contents = "Unable to read log: " + e.Message; }
            if (text.Text == contents) return;
            text.Text = contents;
            text.SelectionStart = text.TextLength;
            text.ScrollToCaret();
        }
        protected override void Dispose(bool disposing) {
            if (disposing) refresh.Dispose();
            base.Dispose(disposing);
        }
    }

    sealed class Tray : ApplicationContext {
        readonly bool serviceMode = ServiceLink.Installed;
        TunnelStatus serviceStatus = new TunnelStatus();
        ControlServer control;
        DateTime? quitAt;
        readonly ToolStripMenuItem windowsIdentity = new ToolStripMenuItem("Windows accounts");
        readonly ToolStripMenuItem currentIdentity = new ToolStripMenuItem("Current tray user (" + System.Security.Principal.WindowsIdentity.GetCurrent().Name + ")");
        Settings settings;
        LogWindow logWindow;
        readonly Tunnel tunnel = new Tunnel();
        readonly NotifyIcon icon = new NotifyIcon();
        readonly ContextMenuStrip menu = new ContextMenuStrip();
        readonly ToolStripMenuItem status = new ToolStripMenuItem("Disabled") { Enabled = false };
        readonly ToolStripMenuItem disabled = new ToolStripMenuItem("Disabled");
        readonly ToolStripMenuItem ssh = new ToolStripMenuItem("SSH only");
        readonly ToolStripMenuItem rdp = new ToolStripMenuItem("SSH + RDP");
        readonly ToolStripMenuItem copy = new ToolStripMenuItem("Copy address");
        readonly System.Windows.Forms.Timer timer = new System.Windows.Forms.Timer { Interval = 250 };
        readonly string settingsPath = Settings.UserPath;
        readonly string logPath;
        readonly Icon off = MakeIcon(Color.Gray), pending = MakeIcon(Color.Goldenrod), on = MakeIcon(Color.SeaGreen);
        string error;
        [DllImport("user32.dll")] static extern bool DestroyIcon(IntPtr handle);
        static Icon MakeIcon(Color color) {
            using (var bitmap = new Bitmap(32, 32)) {
                using (var g = Graphics.FromImage(bitmap)) {
                    g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                    using (var brush = new SolidBrush(color)) g.FillEllipse(brush, 2, 2, 28, 28);
                    using (var pen = new Pen(Color.White, 3)) {
                        g.DrawLine(pen, 9, 11, 14, 16); g.DrawLine(pen, 14, 16, 9, 21); g.DrawLine(pen, 18, 21, 24, 21);
                    }
                }
                IntPtr native = bitmap.GetHicon();
                try { using (var temporary = Icon.FromHandle(native)) return (Icon)temporary.Clone(); }
                finally { DestroyIcon(native); }
            }
        }
        public Tray() {
            settings = Settings.Load(settingsPath);
            string folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TailcatTray");
            Directory.CreateDirectory(folder);
            logPath = serviceMode ? ServiceLink.LogPath : Path.Combine(folder, "tailcat.log");
            if (!serviceMode && File.Exists(logPath) && new FileInfo(logPath).Length > 1024 * 1024) File.WriteAllText(logPath, "");
            tunnel.Log = delegate(string text) { try { File.AppendAllText(logPath, DateTime.Now.ToString("s") + " " + text + Environment.NewLine); } catch (IOException) { } };
            menu.Items.AddRange(new ToolStripItem[] { status, new ToolStripSeparator(), disabled, ssh, rdp, new ToolStripSeparator(), copy });
            var identityMenu = new ToolStripMenuItem("SSH identity");
            identityMenu.DropDownItems.AddRange(new ToolStripItem[] { windowsIdentity, currentIdentity });
            menu.Items.Insert(5, identityMenu);
            windowsIdentity.Click += delegate { SelectIdentity("windows"); };
            currentIdentity.Click += delegate { SelectIdentity("current-user"); };
            if (serviceMode) {
                currentIdentity.Enabled = false;
                currentIdentity.ToolTipText = "Boot service supports Windows accounts only.";
                settings.SshIdentity = "windows";
                timer.Interval = 1000;
            }
            menu.Items.Add("View log", null, delegate {
                try {
                    if (logWindow == null || logWindow.IsDisposed) logWindow = new LogWindow(logPath);
                    logWindow.Show();
                    logWindow.WindowState = FormWindowState.Normal;
                    logWindow.Activate();
                    logWindow.Reload();
                } catch (Exception e) { Fail(e); }
            });
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(serviceMode ? "Quit tray (keep service running)" : "Quit", null, delegate { ExitThread(); });
            disabled.Click += delegate { try { Execute("disable"); } catch (Exception e) { Fail(e); } };
            ssh.Click += delegate { Enable(false); };
            rdp.Click += delegate { Enable(true); };
            copy.Click += delegate {
                try { Execute("copy-address"); }
                catch (Exception e) { Fail(e); }
            };
            icon.ContextMenuStrip = menu;
            icon.MouseUp += delegate(object sender, MouseEventArgs e) {
                if (e.Button == MouseButtons.Left) {
                    typeof(NotifyIcon).GetMethod("ShowContextMenu", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic).Invoke(icon, null);
                }
            };
            control = new ControlServer();
            timer.Tick += delegate {
                control.Poll(Execute);
                if (quitAt.HasValue && DateTime.UtcNow >= quitAt.Value) { ExitThread(); return; }
                if (serviceMode) {
                    try { UpdateService(); } catch (Exception e) { serviceStatus = new TunnelStatus { Error = e.Message }; Refresh(); }
                } else try { tunnel.Poll(); Refresh(); } catch (Exception e) { Fail(e); }
            };
            if (!serviceMode) tunnel.Log("Tray started disabled.");
            Refresh(); icon.Visible = true; timer.Start();
        }
        void Enable(bool withRdp) {
            try { Execute(withRdp ? "enable ssh-rdp" : "enable ssh"); }
            catch (Exception e) { Fail(e); }
        }
        string Execute(string command) {
            if (quitAt.HasValue) throw new InvalidOperationException("Tray is exiting.");
            if (serviceMode && command != "quit" && command != "log") {
                string result = ServiceLink.Execute(command == "copy-address" ? "address" : command);
                if (command == "copy-address") { Clipboard.SetText(result); result = "Address copied."; }
                UpdateService();
                return result;
            }
            switch (command) {
                case "status": return status.Text + "; SSH identity: " + settings.SshIdentity;
                case "enable ssh":
                case "enable ssh-rdp":
                    tunnel.Start(settings, command == "enable ssh-rdp"); error = null; Refresh();
                    return "Starting " + tunnel.Mode + ". Use 'address' once ready.";
                case "disable": tunnel.Stop(); error = null; Refresh(); return "Disabled.";
                case "identity windows":
                case "identity current-user":
                    ChangeIdentity(command.Substring(9));
                    return "SSH identity: " + settings.SshIdentity;
                case "address":
                case "copy-address":
                    if (!tunnel.Running || tunnel.Address == null) throw new InvalidOperationException("No address available; enable the tunnel and wait until ready.");
                    if (command == "address") return tunnel.Address;
                    Clipboard.SetText(tunnel.Address); return "Address copied.";
                case "log":
                    if (logWindow == null || logWindow.IsDisposed) logWindow = new LogWindow(logPath);
                    logWindow.Show(); logWindow.WindowState = FormWindowState.Normal; logWindow.Activate(); logWindow.Reload();
                    return "Log window opened.";
                case "quit": tunnel.Stop(); quitAt = DateTime.UtcNow.AddMilliseconds(500); return "Tray stopped.";
                default: throw new InvalidOperationException("Unknown command.");
            }
        }
        void SelectIdentity(string identity) {
            try { ChangeIdentity(identity); } catch (Exception e) { Fail(e); }
        }
        void ChangeIdentity(string identity) {
            if (serviceMode) { ServiceLink.Execute("identity " + identity); return; }
            if (settings.SshIdentity == identity) return;
                var updated = Settings.Load(settingsPath);
                updated.SshIdentity = identity;
                Tunnel.Arguments(File.ReadAllText(updated.MacKeyPath).Trim(), false, updated);
                updated.Save(settingsPath);
                bool running = tunnel.Running;
                bool withRdp = tunnel.Mode == "SSH + RDP";
                settings = updated;
                if (running) {
                    tunnel.Stop();
                    tunnel.Start(settings, withRdp);
                }
                error = null;
                Refresh();
        }
        void Fail(Exception e) {
            error = e.Message;
            if (!serviceMode) tunnel.Log(error);
            Refresh();
            icon.ShowBalloonTip(6000, "Tailcat Tray", error, ToolTipIcon.Error);
        }
        void Refresh() {
            if (serviceMode) {
                status.Text = serviceStatus.Ready ? "Connected: " + serviceStatus.Mode : serviceStatus.Running ? "Starting: " + serviceStatus.Mode : serviceStatus.Error != null ? "Service unavailable / retrying" : "Disabled (service)";
                icon.Text = "Tailcat service: " + (serviceStatus.Ready ? "connected" : serviceStatus.Running ? "starting" : "disabled / unavailable");
                icon.Icon = serviceStatus.Ready ? on : serviceStatus.Running ? pending : off;
                disabled.Checked = !serviceStatus.Running;
                ssh.Checked = serviceStatus.Running && serviceStatus.Mode == "SSH only";
                rdp.Checked = serviceStatus.Running && serviceStatus.Mode == "SSH + RDP";
                copy.Enabled = serviceStatus.Ready;
                windowsIdentity.Checked = true; currentIdentity.Checked = false;
                return;
            }
            bool running = tunnel.Running;
            bool ready = running && tunnel.Address != null;
            status.Text = running ? (ready ? "Connected: " : "Starting: ") + tunnel.Mode : (error == null ? "Disabled" : "Disabled - see log");
            icon.Text = running ? "Tailcat: " + (ready ? tunnel.Mode : "starting") : "Tailcat: disabled";
            icon.Icon = ready ? on : (running ? pending : off);
            disabled.Checked = !running;
            ssh.Checked = running && tunnel.Mode == "SSH only";
            rdp.Checked = running && tunnel.Mode == "SSH + RDP";
            copy.Enabled = ready;
            windowsIdentity.Checked = settings.SshIdentity == "windows";
            currentIdentity.Checked = settings.SshIdentity == "current-user";
        }
        void UpdateService() {
            serviceStatus = new JavaScriptSerializer().Deserialize<TunnelStatus>(ServiceLink.Execute("snapshot"));
            Refresh();
        }
        protected override void ExitThreadCore() {
            timer.Stop();
            tunnel.Dispose();
            if (logWindow != null) logWindow.Dispose();
            icon.Visible = false;
            icon.Dispose(); menu.Dispose(); timer.Dispose();
            off.Dispose(); pending.Dispose(); on.Dispose();
            base.ExitThreadCore();
        }
    }
    static class Program {
        [STAThread] static void Main() {
            bool created;
            using (var mutex = new Mutex(true, "Local\\TailcatTray-" + System.Security.Principal.WindowsIdentity.GetCurrent().User.Value, out created)) {
                if (!created) { MessageBox.Show("Tailcat Tray is already running.", "Tailcat Tray"); return; }
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                try { Application.Run(new Tray()); }
                catch (Exception e) { MessageBox.Show(e.Message, "Tailcat Tray", MessageBoxButtons.OK, MessageBoxIcon.Error); }
            }
        }
    }
}
