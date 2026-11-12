using System;
using System.IO;
using System.ServiceProcess;
using System.Threading;
using System.Web.Script.Serialization;

namespace TailcatTray {
    public sealed class ServiceConfig {
        public Settings Tunnel { get; set; }
        public string ControlSid { get; set; }
    }
    sealed class TunnelService : ServiceBase {
        readonly ManualResetEvent stop = new ManualResetEvent(false);
        readonly Tunnel tunnel = new Tunnel();
        Thread worker;
        Settings settings;
        ControlServer control;
        string desired;
        string error;
        DateTime retryAt;
        static string StatePath { get { return Path.Combine(ServiceLink.Root, "state", "mode.txt"); } }
        public TunnelService() { ServiceName = ServiceLink.Name; CanShutdown = true; }
        protected override void OnStart(string[] args) {
            var config = new JavaScriptSerializer().Deserialize<ServiceConfig>(File.ReadAllText(Path.Combine(ServiceLink.Root, "service.json")));
            if (config == null || config.Tunnel == null || config.Tunnel.SshIdentity != "windows" || String.IsNullOrEmpty(config.Tunnel.ServerKeyPath))
                throw new InvalidDataException("Service requires Windows SSH and an explicit saved server key.");
            settings = config.Tunnel;
            Tunnel.Arguments(File.ReadAllText(settings.MacKeyPath).Trim(), false, settings);
            desired = File.ReadAllText(StatePath).Trim();
            ValidateMode(desired);
            tunnel.Log = Log;
            string serviceSid = ((System.Security.Principal.SecurityIdentifier)new System.Security.Principal.NTAccount(@"NT SERVICE\" + ServiceLink.Name).Translate(typeof(System.Security.Principal.SecurityIdentifier))).Value;
            control = new ControlServer(ServiceLink.Pipe, config.ControlSid, serviceSid);
            stop.Reset();
            worker = new Thread(Run) { IsBackground = true };
            worker.Start();
        }
        static void ValidateMode(string value) {
            if (value != "disabled" && value != "ssh" && value != "ssh-rdp") throw new InvalidDataException("Unknown service mode.");
        }
        void Log(string text) {
            try {
                if (File.Exists(ServiceLink.LogPath) && new FileInfo(ServiceLink.LogPath).Length > 2 * 1024 * 1024) {
                    string old = ServiceLink.LogPath + ".previous";
                    if (File.Exists(old)) File.Delete(old);
                    File.Move(ServiceLink.LogPath, old);
                }
                File.AppendAllText(ServiceLink.LogPath, DateTime.Now.ToString("s") + " " + text + Environment.NewLine);
            } catch (IOException) { }
        }
        void Run() {
            Log("Service started; desired mode: " + desired);
            try {
                while (!stop.WaitOne(250)) {
                    control.Poll(Execute);
                    try {
                        tunnel.Poll();
                        if (desired != "disabled" && !tunnel.Running && DateTime.UtcNow >= retryAt) {
                            tunnel.Start(settings, desired == "ssh-rdp");
                            error = null;
                        }
                    } catch (Exception e) {
                        tunnel.Stop(); error = e.Message;
                        retryAt = DateTime.UtcNow.AddSeconds(10);
                        Log("Tunnel failed; retrying in 10 seconds: " + error);
                    }
                }
            } finally { tunnel.Dispose(); Log("Service stopped."); }
        }
        string Execute(string command) {
            switch (command) {
                case "enable ssh": SetMode("ssh"); return "SSH enabled; will resume after restart.";
                case "enable ssh-rdp": SetMode("ssh-rdp"); return "SSH + RDP enabled; will resume after restart.";
                case "disable": SetMode("disabled"); return "Disabled, including after restart.";
                case "identity windows": return "SSH identity: windows (service).";
                case "identity current-user": throw new InvalidOperationException("The boot service forwards Windows SSH only. It cannot provide a shell as the desktop user.");
                case "address":
                    if (!tunnel.Running || tunnel.Address == null) throw new InvalidOperationException("Tunnel is not ready. Check status or the service log.");
                    return tunnel.Address;
                case "snapshot":
                    return new JavaScriptSerializer().Serialize(new TunnelStatus { Running = tunnel.Running, Ready = tunnel.Running && tunnel.Address != null, Mode = tunnel.Mode, Address = tunnel.Address, Error = error });
                case "status":
                    return (tunnel.Running ? (tunnel.Address == null ? "Starting: " : "Connected: ") + tunnel.Mode : desired == "disabled" ? "Disabled" : "Retrying") + "; service; boot mode: " + desired + (error == null ? "" : "; " + error);
                default: throw new InvalidOperationException("Unsupported service command.");
            }
        }
        void SetMode(string mode) {
            ValidateMode(mode);
            string temporary = StatePath + ".tmp";
            File.WriteAllText(temporary, mode);
            File.Replace(temporary, StatePath, null);
            desired = mode;
            tunnel.Stop(); error = null; retryAt = DateTime.MinValue;
        }
        protected override void OnStop() {
            stop.Set();
            if (worker != null && !worker.Join(10000)) throw new System.TimeoutException("Tunnel worker did not stop.");
        }
        protected override void OnShutdown() { OnStop(); }
        public static void Main() { ServiceBase.Run(new TunnelService()); }
    }
}
