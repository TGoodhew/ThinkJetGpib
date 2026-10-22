using System.ServiceProcess;
using ThinkJetGpib.Bridge;
using ThinkJetGpib.Setup;

namespace ThinkJetGpib;

/// <summary>
/// One exe, five jobs:
///   ThinkJetGpibSetup.exe [--address N] [--resource R] [--port P] [--test-page|--no-test-page] [--quiet]
///                                   install (the default)
///   ThinkJetGpib.exe --uninstall [--quiet]    remove everything
///   ThinkJetGpib.exe --service                run as the bridge service (the SCM starts it this way)
///   ThinkJetGpib.exe --console                run the bridge in this window, for debugging
///   ThinkJetGpib.exe --print-file FILE        send a file straight to the printer, bypassing Windows
///   ThinkJetGpib.exe --density-test           print the 96 vs 192 dpi raster comparison page
/// </summary>
internal static class Program
{
    private static int Main(string[] args)
    {
        try
        {
            if (Has(args, "--service"))
            {
                ServiceBase.Run(new BridgeService());
                return 0;
            }
            if (Has(args, "--console")) return RunConsole();
            if (Value(args, "--print-file") is string file) return PrintFile(file);
            if (Has(args, "--density-test")) return Print(new MemoryStream(DensityTest.Build()));
            if (Has(args, "--help") || Has(args, "-h") || Has(args, "/?")) return Usage();

            var options = new SetupOptions
            {
                Address = IntValue(args, "--address"),
                Resource = Value(args, "--resource"),
                Port = IntValue(args, "--port"),
                TestPage = Has(args, "--test-page") ? true : Has(args, "--no-test-page") ? false : null,
                Quiet = Has(args, "--quiet"),
            };
            var installer = new Installer(options);
            return Has(args, "--uninstall") ? installer.Uninstall() : installer.Install();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex.Message);
            return 1;
        }
    }

    private static int RunConsole()
    {
        Log.EchoToConsole = true;
        var server = new BridgeServer(BridgeConfig.Load(BridgeConfig.DefaultPath));
        server.Start();
        Console.WriteLine("Running. Press Ctrl+C to stop. (Stop the service first, or the port will be in use.)");
        using var done = new ManualResetEventSlim();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; done.Set(); };
        done.Wait();
        server.Stop();
        return 0;
    }

    private static int PrintFile(string path)
    {
        using FileStream input = File.OpenRead(path);
        return Print(input);
    }

    private static int Print(Stream input)
    {
        Log.EchoToConsole = true;
        var config = BridgeConfig.Load(BridgeConfig.DefaultPath);
        long bytes = JobPump.Pump(input, () => new VisaPrinterSink(config.Resource, config.TimeoutMs), config.ChunkBytes);
        Console.WriteLine($"Sent {bytes:N0} bytes to {config.Resource}.");
        return 0;
    }

    private static int Usage()
    {
        Console.WriteLine("""
            ThinkJetGpibSetup.exe                    install (asks for the GPIB address)
              --address N | --resource GPIB0::N::INSTR | --port 9100
              --test-page | --no-test-page | --quiet
            ThinkJetGpib.exe --uninstall [--quiet]   remove the printer, driver, service and certificate
            ThinkJetGpib.exe --console               run the bridge in this window (stop the service first)
            ThinkJetGpib.exe --print-file FILE       send FILE's bytes straight to the printer
            ThinkJetGpib.exe --density-test          print the 96 vs 192 dpi raster comparison page
            """);
        return 0;
    }

    private static bool Has(string[] args, string flag) =>
        args.Any(a => a.Equals(flag, StringComparison.OrdinalIgnoreCase) ||
                      a.Equals("/" + flag.TrimStart('-'), StringComparison.OrdinalIgnoreCase));

    private static string? Value(string[] args, string flag)
    {
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i].StartsWith(flag + "=", StringComparison.OrdinalIgnoreCase)) return args[i][(flag.Length + 1)..];
            if (args[i].Equals(flag, StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length) return args[i + 1];
        }
        return null;
    }

    private static int? IntValue(string[] args, string flag) =>
        Value(args, flag) is string s ? int.TryParse(s, out int n) ? n : throw new ArgumentException($"{flag} needs a number, not '{s}'.") : null;
}
