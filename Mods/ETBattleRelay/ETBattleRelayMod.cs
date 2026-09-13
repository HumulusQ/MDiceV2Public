using System.Diagnostics;
using MDiceV2.Interfaces.Mod;

namespace ETBattleRelay;

/// <summary>An isolated ET Battle room/signalling relay that never processes MDice messages.</summary>
public sealed class ETBattleRelayMod : IModPlugin
{
    private readonly object _gate = new();
    private RelayServer? _server;

    public string ModId => "com.etbattle.relay";
    public string ModName => "ET Battle Relay";
    public string Version => "1.0.0";
    public string Author => "ET Battle Engine";
    public string Description => "Public room, WebRTC signalling, and opaque WSS forwarding for ET Battle Engine.";

    public void OnLoad() { }

    public void OnEnable()
    {
        lock (_gate)
        {
            if (_server is { IsRunning: true })
                return;
            var options = RelayOptions.Load();
            if (!options.Enabled)
            {
                Trace.WriteLine("[ETBattleRelay] Listener remains disabled by configuration.");
                return;
            }
            var server = new RelayServer(options);
            try
            {
                server.Start();
                _server = server;
                Trace.WriteLine($"[ETBattleRelay] Listening on {options.HttpPrefix}");
            }
            catch
            {
                server.Dispose();
                throw;
            }
        }
    }

    public void OnDisable() => StopServer();
    public void OnUnload() => StopServer();

    public ModMessageResult? OnGroupMessage(long groupId, long userId, string content, bool isAted) => null;
    public ModMessageResult? OnPrivateMessage(long userId, string content) => null;

    private void StopServer()
    {
        RelayServer? server;
        lock (_gate)
        {
            server = _server;
            _server = null;
        }
        if (server is null)
            return;
        try
        {
            server.StopAsync().Wait(TimeSpan.FromSeconds(5));
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[ETBattleRelay] Stop warning: {ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            server.Dispose();
        }
    }
}
