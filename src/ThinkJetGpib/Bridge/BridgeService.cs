using System.ServiceProcess;

namespace ThinkJetGpib.Bridge;

/// <summary>The Windows service wrapper ("ThinkJet GPIB Print Bridge").</summary>
public sealed class BridgeService : ServiceBase
{
    public const string Name = "ThinkJetGpib";
    private BridgeServer? _server;

    public BridgeService() => ServiceName = Name;

    protected override void OnStart(string[] args)
    {
        try
        {
            var config = BridgeConfig.Load(BridgeConfig.DefaultPath);
            _server = new BridgeServer(config);
            _server.Start();
        }
        catch (Exception ex)
        {
            Log.Error($"Service failed to start: {ex.Message}");
            throw;
        }
    }

    protected override void OnStop() => _server?.Stop();
}
