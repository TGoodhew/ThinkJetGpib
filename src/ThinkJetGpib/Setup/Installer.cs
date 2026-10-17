using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.ServiceProcess;
using Microsoft.Win32;
using ThinkJetGpib.Bridge;

namespace ThinkJetGpib.Setup;

/// <summary>
/// Install and uninstall. The exe carries the driver files and the two PowerShell scripts as
/// embedded resources; it copies itself to Program Files as ThinkJetGpib.exe (which is also the
/// service binary), extracts the payload next to it, and runs the scripts in 64-bit PowerShell.
/// </summary>
public sealed class Installer(SetupOptions options)
{
    public const string ProductName = "ThinkJet GPIB Printer (HP 2225)";
    private const string ArpKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\ThinkJetGpib";
    private const string InstalledExeName = "ThinkJetGpib.exe";

    private static string InstallDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "ThinkJetGpib");
    private static string InstalledExe => Path.Combine(InstallDir, InstalledExeName);
    private static string StateFile => Path.Combine(BridgeConfig.DataDir, "install-state.json");
    private static string Version =>
        typeof(Installer).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion?.Split('+')[0] ?? "1.0.0";

    public int Install()
    {
        Banner($"{ProductName} setup {Version}");

        if (RuntimeInformation.OSArchitecture != Architecture.X64)
            return Fail($"This driver is for 64-bit (x64) Windows; this PC is {RuntimeInformation.OSArchitecture}.");

        if (!Visa.IsAvailable())
            return Fail("NI-VISA was not found (visa32.dll). Install NI-VISA and NI-488.2, then run setup again.");
        Say("NI-VISA found.");

        var existing = BridgeConfig.Load(BridgeConfig.DefaultPath);
        var config = existing;
        config.Resource = ChooseResource(existing.Resource);
        if (options.Port is int port) config.Port = port;

        CheckResource(config.Resource);

        bool testPage = options.TestPage ?? (!options.Quiet && AskYesNo("Print a Windows test page when setup finishes?", true));

        try
        {
            if (File.Exists(StateFile) || ServiceExists())
            {
                Banner("Removing the previous installation");
                RunUninstallScript();
            }

            Banner("Copying files");
            Directory.CreateDirectory(InstallDir);
            string self = Environment.ProcessPath ?? throw new InvalidOperationException("Cannot locate setup's own exe.");
            if (!string.Equals(Path.GetFullPath(self), Path.GetFullPath(InstalledExe), StringComparison.OrdinalIgnoreCase))
                CopyOverInstalledExe(self);
            ExtractPayload(InstallDir);
            config.Save(BridgeConfig.DefaultPath);
            Say($"Installed to {InstallDir}");
            Say($"Settings: {BridgeConfig.DefaultPath} ({config.Resource}, port {config.Port})");

            Banner("Installing driver, printer and service");
            RunScript(Path.Combine(InstallDir, "scripts", "install.ps1"),
                $"-InstallDir {Quote(InstallDir)} -ExePath {Quote(InstalledExe)} -StateFile {Quote(StateFile)} " +
                $"-Port {config.Port}" + (testPage ? " -TestPage" : ""));

            RegisterUninstall();
        }
        catch (Exception ex)
        {
            return Fail(ex.Message + Environment.NewLine +
                        "Fix the cause and run setup again (it cleans up the partial install first), " +
                        $"or remove it with \"{InstalledExe}\" --uninstall.");
        }

        Banner("Setup complete");
        Say("Printer:  HP 2225 ThinkJet");
        Say("Service:  ThinkJet GPIB Print Bridge");
        Say($"Log:      {Log.FilePath}");
        Say("Uninstall from Settings > Apps > Installed apps.");
        Pause();
        return 0;
    }

    public int Uninstall()
    {
        Banner($"Uninstalling {ProductName}");
        try
        {
            RunUninstallScript();
        }
        catch (Exception ex)
        {
            return Fail(ex.Message);
        }

        try { Registry.LocalMachine.DeleteSubKeyTree(ArpKey, throwOnMissingSubKey: false); } catch (Exception) { }
        foreach (string file in new[] { BridgeConfig.DefaultPath, Log.FilePath, Path.Combine(BridgeConfig.DataDir, "bridge.1.log") })
            try { File.Delete(file); } catch (Exception) { }
        try { Directory.Delete(BridgeConfig.DataDir); } catch (Exception) { }

        Banner("Uninstall complete");
        Pause();

        // This exe may be the one in the install folder: remove the folder once this process exits.
        if (Directory.Exists(InstallDir))
        {
            string command = $"Wait-Process -Id {Environment.ProcessId} -ErrorAction SilentlyContinue; " +
                             $"Remove-Item -LiteralPath '{InstallDir.Replace("'", "''")}' -Recurse -Force";
            var cleanup = new ProcessStartInfo
            {
                FileName = PowerShellPath(),
                Arguments = $"-NoProfile -NonInteractive -WindowStyle Hidden -Command \"{command}\"",
                WorkingDirectory = Path.GetTempPath(),
                CreateNoWindow = true,
                UseShellExecute = false,
            };
            UseDefaultModulePath(cleanup);
            Process.Start(cleanup);
        }
        return 0;
    }

    /// <summary>Runs uninstall.ps1 from a fresh copy of the payload in %TEMP%, so it neither
    /// depends on nor locks the install folder.</summary>
    private static void RunUninstallScript()
    {
        string temp = Path.Combine(Path.GetTempPath(), "ThinkJetGpib-setup-" + Environment.ProcessId);
        try
        {
            ExtractPayload(temp);
            RunScript(Path.Combine(temp, "scripts", "uninstall.ps1"), $"-StateFile {Quote(StateFile)}");
        }
        finally
        {
            try { Directory.Delete(temp, recursive: true); } catch (Exception) { }
        }
    }

    /// <summary>The service process can outlive "Stopped" by a moment, so wait for anything running
    /// from the installed exe to exit, then retry the copy briefly.</summary>
    private static void CopyOverInstalledExe(string source)
    {
        foreach (Process p in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(InstalledExeName)))
        {
            try
            {
                if (string.Equals(p.MainModule?.FileName, InstalledExe, StringComparison.OrdinalIgnoreCase))
                    p.WaitForExit(15000);
            }
            catch (Exception) { /* access denied or already gone */ }
            finally { p.Dispose(); }
        }

        for (int attempt = 1; ; attempt++)
        {
            try
            {
                File.Copy(source, InstalledExe, overwrite: true);
                return;
            }
            catch (IOException) when (attempt < 20)
            {
                Thread.Sleep(500);
            }
        }
    }

    // ---- Address ---------------------------------------------------------------------------------

    private string ChooseResource(string current)
    {
        if (options.Resource is not null) return options.Resource;
        int fallback = BridgeConfig.AddressOf(current) ?? 5;
        if (options.Address is int a) return $"GPIB0::{a}::INSTR";
        if (options.Quiet) return $"GPIB0::{fallback}::INSTR";

        while (true)
        {
            Console.Write($"GPIB address of the HP 2225 [{fallback}]: ");
            string? text = Console.ReadLine()?.Trim();
            if (string.IsNullOrEmpty(text)) return $"GPIB0::{fallback}::INSTR";
            if (int.TryParse(text, out int n) && n is >= 0 and <= 30) return $"GPIB0::{n}::INSTR";
            Console.WriteLine("  Enter a primary address from 0 to 30.");
        }
    }

    private static void CheckResource(string resource)
    {
        try
        {
            using var sink = new VisaPrinterSink(resource, 3000);
            Say($"Opened {resource}.");
        }
        catch (Exception ex)
        {
            Warn($"Could not open {resource}: {ex.Message}");
            Warn("Setup will continue; jobs will fail until the GPIB-ENET/1000 and printer are reachable.");
        }
    }

    // ---- Payload and scripts -------------------------------------------------------------------

    /// <summary>Writes the embedded driver/ and scripts/ files under <paramref name="root"/>.</summary>
    private static string ExtractPayload(string root)
    {
        Assembly asm = typeof(Installer).Assembly;
        foreach (string name in asm.GetManifestResourceNames())
        {
            if (!name.StartsWith("payload/", StringComparison.Ordinal)) continue;
            string relative = name["payload/".Length..].Replace('/', Path.DirectorySeparatorChar);
            string target = Path.Combine(root, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            using Stream source = asm.GetManifestResourceStream(name)!;
            using FileStream output = File.Create(target);
            source.CopyTo(output);
        }
        return root;
    }

    /// <summary>Runs a script in 64-bit Windows PowerShell (this process is 32-bit, so go through
    /// Sysnative) and shows its output. Throws if the script fails.</summary>
    private static string PowerShellPath()
    {
        string windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        string sysnative = Path.Combine(windows, "Sysnative", @"WindowsPowerShell\v1.0\powershell.exe");
        return File.Exists(sysnative) ? sysnative : Path.Combine(windows, "System32", @"WindowsPowerShell\v1.0\powershell.exe");
    }

    /// <summary>
    /// Started from PowerShell 7, setup inherits a PSModulePath that puts PowerShell 7's modules
    /// first, and Windows PowerShell 5.1 then fails to load those (e.g. Microsoft.PowerShell.Security
    /// for New-FileCatalog). With the variable removed, 5.1 builds its own default module path.
    /// </summary>
    private static void UseDefaultModulePath(ProcessStartInfo psi) => psi.Environment.Remove("PSModulePath");

    private static void RunScript(string script, string arguments)
    {
        var psi = new ProcessStartInfo
        {
            FileName = PowerShellPath(),
            Arguments = $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -File {Quote(script)} {arguments}",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        UseDefaultModulePath(psi);
        using Process process = Process.Start(psi)!;
        process.OutputDataReceived += (_, e) => { if (e.Data is not null) Console.WriteLine(e.Data); };
        process.ErrorDataReceived += (_, e) => { if (e.Data is not null) WriteColor(e.Data, ConsoleColor.Red); };
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        process.WaitForExit();
        if (process.ExitCode != 0)
            throw new InvalidOperationException($"{Path.GetFileName(script)} failed (exit code {process.ExitCode}); see the messages above.");
    }

    private void RegisterUninstall()
    {
        using RegistryKey key = Registry.LocalMachine.CreateSubKey(ArpKey);
        key.SetValue("DisplayName", ProductName);
        key.SetValue("DisplayVersion", Version);
        key.SetValue("Publisher", "ThinkJetGpib");
        key.SetValue("InstallLocation", InstallDir);
        key.SetValue("DisplayIcon", InstalledExe);
        key.SetValue("UninstallString", $"\"{InstalledExe}\" --uninstall");
        key.SetValue("QuietUninstallString", $"\"{InstalledExe}\" --uninstall --quiet");
        key.SetValue("NoModify", 1, RegistryValueKind.DWord);
        key.SetValue("NoRepair", 1, RegistryValueKind.DWord);
        long kb = new DirectoryInfo(InstallDir).EnumerateFiles("*", SearchOption.AllDirectories).Sum(f => f.Length) / 1024;
        key.SetValue("EstimatedSize", (int)kb, RegistryValueKind.DWord);
    }

    private static bool ServiceExists() =>
        ServiceController.GetServices().Any(s => s.ServiceName.Equals(BridgeService.Name, StringComparison.OrdinalIgnoreCase));

    // ---- Console helpers -----------------------------------------------------------------------

    private static string Quote(string s) => "\"" + s.TrimEnd('\\') + "\"";

    private bool AskYesNo(string question, bool defaultYes)
    {
        Console.Write($"{question} [{(defaultYes ? "Y/n" : "y/N")}]: ");
        string? answer = Console.ReadLine()?.Trim();
        return string.IsNullOrEmpty(answer) ? defaultYes : answer.StartsWith('y') || answer.StartsWith('Y');
    }

    private void Pause()
    {
        if (options.Quiet) return;
        Console.WriteLine();
        Console.Write("Press Enter to close.");
        Console.ReadLine();
    }

    private int Fail(string message)
    {
        Console.WriteLine();
        WriteColor("Setup failed: " + message, ConsoleColor.Red);
        Pause();
        return 1;
    }

    private static void Banner(string text)
    {
        Console.WriteLine();
        WriteColor(text, ConsoleColor.Cyan);
    }

    private static void Say(string text) => Console.WriteLine("  " + text);
    private static void Warn(string text) => WriteColor("  " + text, ConsoleColor.Yellow);

    private static void WriteColor(string text, ConsoleColor color)
    {
        ConsoleColor before = Console.ForegroundColor;
        Console.ForegroundColor = color;
        Console.WriteLine(text);
        Console.ForegroundColor = before;
    }
}

public sealed record SetupOptions
{
    public int? Address { get; init; }
    public string? Resource { get; init; }
    public int? Port { get; init; }
    public bool? TestPage { get; init; }
    public bool Quiet { get; init; }
}
