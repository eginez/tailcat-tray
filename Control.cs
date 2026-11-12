using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Threading;
using System.Threading.Tasks;
using System.Text;
using System.Web.Script.Serialization;

namespace TailcatTray {
    public static class ServiceLink {
        public const string Name = "TailcatTunnel";
        public const string Pipe = "TailcatTunnel-control-v1";
        public static string Root { get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "TailcatTray"); } }
        public static string LogPath { get { return Path.Combine(Root, "state", "tailcat.log"); } }
        public static bool Installed { get { using (var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\" + Name)) return key != null; } }
        public static Reply Send(string command) { return Cli.SendTo(Pipe, command, 1000); }
        public static string Execute(string command) {
            var reply = Send(command);
            if (!reply.Ok) throw new InvalidOperationException(reply.Message);
            return reply.Message;
        }
    }
    public sealed class TunnelStatus {
        public bool Running { get; set; }
        public bool Ready { get; set; }
        public string Mode { get; set; }
        public string Address { get; set; }
        public string Error { get; set; }
    }
    public sealed class Reply {
        public bool Ok { get; set; }
        public string Message { get; set; }
    }
    sealed class ControlServer {
        sealed class Request {
            public string Command;
            public Reply Reply;
            public readonly ManualResetEvent Done = new ManualResetEvent(false);
        }
        readonly ConcurrentQueue<Request> requests = new ConcurrentQueue<Request>();
        readonly string pipeName;
        readonly SecurityIdentifier allowedUser;
        readonly SecurityIdentifier serverIdentity;
        readonly bool isolateOwner;
        public static string Name { get { return "TailcatTray-" + WindowsIdentity.GetCurrent().User.Value; } }
        public ControlServer() : this(Name, WindowsIdentity.GetCurrent().User.Value, null) { }
        public ControlServer(string name, string userSid, string serverSid) {
            pipeName = name;
            allowedUser = new SecurityIdentifier(userSid);
            serverIdentity = serverSid == null ? WindowsIdentity.GetCurrent().User : new SecurityIdentifier(serverSid);
            isolateOwner = serverSid != null;
            var thread = new Thread(Serve) { IsBackground = true };
            thread.Start();
        }
        static async Task<string> ReadCommand(StreamReader reader) {
            var text = new StringBuilder();
            var character = new char[1];
            while (text.Length <= 200) {
                if (await reader.ReadAsync(character, 0, 1) == 0) return null;
                if (character[0] == '\n') return text.ToString().TrimEnd('\r');
                text.Append(character[0]);
            }
            return null;
        }
        void Serve() {
            var security = new PipeSecurity();
            security.SetAccessRuleProtection(true, false);
            security.AddAccessRule(new PipeAccessRule(serverIdentity, PipeAccessRights.FullControl, AccessControlType.Allow));
            security.AddAccessRule(new PipeAccessRule(allowedUser, PipeAccessRights.ReadWrite, AccessControlType.Allow));
            if (isolateOwner) security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier("S-1-3-4"), PipeAccessRights.ReadPermissions, AccessControlType.Allow));
            while (true) {
                try {
                    using (var pipe = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 4096, 4096, security)) {
                        pipe.WaitForConnection();
                        using (var reader = new StreamReader(pipe))
                        using (var writer = new StreamWriter(pipe) { AutoFlush = true }) {
                            var read = ReadCommand(reader);
                            if (!read.Wait(5000)) continue;
                            string command = read.Result;
                            if (command == null || command.Length > 200) continue;
                            var request = new Request { Command = command };
                            requests.Enqueue(request);
                            request.Done.WaitOne();
                            request.Done.Dispose();
                            writer.WriteLine(new JavaScriptSerializer().Serialize(request.Reply));
                        }
                    }
                } catch (UnauthorizedAccessException) { Thread.Sleep(1000); }
                  catch (IOException) { Thread.Sleep(100); }
                  catch (ObjectDisposedException) { }
                  catch (AggregateException) { }
            }
        }
        public void Poll(Func<string, string> execute) {
            Request request;
            while (requests.TryDequeue(out request)) {
                try { request.Reply = new Reply { Ok = true, Message = execute(request.Command) }; }
                catch (Exception e) { request.Reply = new Reply { Ok = false, Message = e.Message }; }
                request.Done.Set();
            }
        }
    }
    public static class Cli {
        public static Reply Send(string command, int timeout) {
            return SendTo(ControlServer.Name, command, timeout);
        }
        public static Reply SendTo(string pipeName, string command, int timeout) {
            using (var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut)) {
                pipe.Connect(timeout);
                using (var reader = new StreamReader(pipe))
                using (var writer = new StreamWriter(pipe) { AutoFlush = true }) {
                    writer.WriteLine(command);
                    var read = reader.ReadLineAsync();
                    if (!read.Wait(10000)) throw new TimeoutException("Tray did not respond in time.");
                    if (read.Result == null) throw new IOException("Tray disconnected.");
                    return new JavaScriptSerializer().Deserialize<Reply>(read.Result);
                }
            }
        }
        public static bool Valid(string command) {
            return command == "enable ssh" || command == "enable ssh-rdp" || command == "disable" ||
                command == "identity windows" || command == "identity current-user" || command == "status" ||
                command == "address" || command == "copy-address" || command == "log" || command == "quit";
        }
        [STAThread] public static int Main(string[] args) {
            string command = String.Join(" ", args).ToLowerInvariant();
            if (command == "" || command == "help" || command == "--help") {
                Console.WriteLine("tailcat-tray enable ssh|ssh-rdp\ntailcat-tray disable\ntailcat-tray identity windows|current-user\ntailcat-tray status\ntailcat-tray address\ntailcat-tray copy-address\ntailcat-tray log\ntailcat-tray quit");
                return 0;
            }
            if (!Valid(command)) { Console.Error.WriteLine("Unknown command. Run tailcat-tray --help."); return 2; }
            try {
                Reply reply;
                if (ServiceLink.Installed && command != "quit" && command != "log") {
                    reply = ServiceLink.Send(command == "copy-address" ? "address" : command);
                    if (reply.Ok && command == "copy-address") {
                        System.Windows.Forms.Clipboard.SetText(reply.Message);
                        reply.Message = "Address copied.";
                    }
                    if (reply.Ok) Console.WriteLine(reply.Message); else Console.Error.WriteLine(reply.Message);
                    return reply.Ok ? 0 : 1;
                }
                try { reply = Send(command, 300); }
                catch (TimeoutException) {
                    if (command == "quit" || command == "disable" || command == "status") { Console.WriteLine("Disabled (tray is not running)."); return 0; }
                    var info = new ProcessStartInfo(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "TailcatTray.exe")) { UseShellExecute = false, CreateNoWindow = true };
                    Process.Start(info);
                    reply = Send(command, 5000);
                }
                if (reply.Ok) Console.WriteLine(reply.Message);
                else Console.Error.WriteLine(reply.Message);
                return reply.Ok ? 0 : 1;
            } catch (Exception e) { Console.Error.WriteLine(e.Message); return 1; }
        }
    }
}
