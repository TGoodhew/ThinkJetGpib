namespace ThinkJetGpib.Bridge;

/// <summary>Plain-text log at %ProgramData%\ThinkJetGpib\bridge.log, rolled to bridge.1.log at 1 MB.
/// Also echoed to the console when running with --console.</summary>
public static class Log
{
    private const long MaxBytes = 1024 * 1024;
    private static readonly object Gate = new();
    public static bool EchoToConsole { get; set; }

    public static string FilePath => Path.Combine(BridgeConfig.DataDir, "bridge.log");

    public static void Info(string message) => Write("INFO ", message);
    public static void Error(string message) => Write("ERROR", message);

    private static void Write(string level, string message)
    {
        string line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {level} {message}";
        lock (Gate)
        {
            if (EchoToConsole) Console.WriteLine(line);
            try
            {
                Directory.CreateDirectory(BridgeConfig.DataDir);
                var file = new FileInfo(FilePath);
                if (file.Exists && file.Length > MaxBytes)
                    File.Move(FilePath, Path.Combine(BridgeConfig.DataDir, "bridge.1.log"), overwrite: true);
                File.AppendAllText(FilePath, line + Environment.NewLine);
            }
            catch (Exception) { /* logging must never take the bridge down */ }
        }
    }
}
