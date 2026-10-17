namespace ThinkJetGpib.Bridge;

/// <summary>
/// Settings in %ProgramData%\ThinkJetGpib\bridge.ini (plain Key=Value lines, '#' comments).
/// The installer writes it; edit it and restart the service to change the address.
/// </summary>
public sealed class BridgeConfig
{
    public string Resource { get; set; } = "GPIB0::5::INSTR";
    public int Port { get; set; } = 9100;
    public int TimeoutMs { get; set; } = 60000;
    public int ChunkBytes { get; set; } = 1024;

    public static string DataDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "ThinkJetGpib");

    public static string DefaultPath => Path.Combine(DataDir, "bridge.ini");

    public static BridgeConfig Parse(IEnumerable<string> lines)
    {
        var config = new BridgeConfig();
        foreach (string raw in lines)
        {
            string line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#') || line.StartsWith(';')) continue;
            int eq = line.IndexOf('=');
            if (eq <= 0) continue;
            string key = line[..eq].Trim();
            string value = line[(eq + 1)..].Trim();
            switch (key.ToLowerInvariant())
            {
                case "resource": if (value.Length > 0) config.Resource = value; break;
                case "port": config.Port = ParseInt(value, 1, 65535, key); break;
                case "timeoutms": config.TimeoutMs = ParseInt(value, 1000, 3_600_000, key); break;
                case "chunkbytes": config.ChunkBytes = ParseInt(value, 16, 1 << 20, key); break;
            }
        }
        return config;
    }

    public static BridgeConfig Load(string path) =>
        File.Exists(path) ? Parse(File.ReadAllLines(path)) : new BridgeConfig();

    public void Save(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllLines(path, new[]
        {
            "# ThinkJetGpib bridge settings. Restart the 'ThinkJet GPIB Print Bridge' service after editing.",
            "# VISA resource of the HP 2225 ThinkJet.",
            $"Resource={Resource}",
            "# Loopback TCP port the Windows printer port sends jobs to (must match the printer port).",
            $"Port={Port}",
            "# VISA I/O timeout per chunk. The printer holds off the handshake while it prints, so keep this generous.",
            $"TimeoutMs={TimeoutMs}",
            "# Bytes per GPIB write.",
            $"ChunkBytes={ChunkBytes}",
        });
    }

    /// <summary>The primary address in a GPIBn::addr::INSTR resource, or null for any other form.</summary>
    public static int? AddressOf(string resource)
    {
        string[] parts = resource.Split("::");
        return parts.Length >= 2 && parts[0].StartsWith("GPIB", StringComparison.OrdinalIgnoreCase)
               && int.TryParse(parts[1], out int a) ? a : null;
    }

    private static int ParseInt(string value, int min, int max, string key)
    {
        if (!int.TryParse(value, out int n) || n < min || n > max)
            throw new FormatException($"bridge.ini: {key}={value} is not a number from {min} to {max}.");
        return n;
    }
}
