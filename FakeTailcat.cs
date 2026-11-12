using System;
using System.Threading;
class FakeTailcat {
    static void Main(string[] args) {
        bool windows = args.Length == 5 && (args[4] == "22" || args[4] == "22,3389");
        bool current = args.Length == 6 && args[4].StartsWith("--ssh-authorized-keys=") && (args[5] == "ssh" || args[5] == "ssh,3389");
        if ((!windows && !current) || args[0] != "--json" || args[1] != "serve" || !args[2].StartsWith("--key=") || !args[3].StartsWith("--allow=nodekey:")) Environment.Exit(7);
        Console.WriteLine("{\"listenAddr\":\"tcTEST_ADDRESS_12345678901234567890\"}");
        Console.Error.WriteLine("Server address: tcTEST_ADDRESS_12345678901234567890");
        if (args[3].EndsWith(new string('b', 64))) { Thread.Sleep(500); Environment.Exit(9); }
        Thread.Sleep(60000);
    }
}
