using MegaCrit.Sts2.Core.Entities.Multiplayer;
using MegaCrit.Sts2.Core.Multiplayer;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Multiplayer.Quality;
using MegaCrit.Sts2.Core.Multiplayer.Serialization;
using MegaCrit.Sts2.Core.Platform;

namespace Ai4Sts2.Workbench;

public sealed class HeadlessHostGameService : INetGameService
{
    public ulong NetId => 1;

    public bool IsConnected => true;

    public bool IsGameLoading { get; private set; }

    public NetGameType Type => NetGameType.Host;

    public PlatformType Platform => PlatformType.None;

    public PeerVersionInfo LocalVersion { get; } = PeerVersionInfo.LocalDefault();

    public event Action<NetErrorInfo>? Disconnected
    {
        add { }
        remove { }
    }

    public void SendMessage<T>(T message, ulong playerId)
        where T : INetMessage => Ignore(message);

    public void SendMessage<T>(T message)
        where T : INetMessage => Ignore(message);

    public void RegisterMessageHandler<T>(MessageHandlerDelegate<T> messageHandlerDelegate)
        where T : INetMessage => Ignore(messageHandlerDelegate);

    public void UnregisterMessageHandler<T>(MessageHandlerDelegate<T> messageHandlerDelegate)
        where T : INetMessage => Ignore(messageHandlerDelegate);

    private static void Ignore(object? value) => _ = value;

    public void Update() { }

    public void Disconnect(NetError reason, bool now = false) { }

    public ConnectionStats? GetStatsForPeer(ulong peerId) => new(peerId);

    public void SetGameLoading(bool isLoading) => IsGameLoading = isLoading;

    public void SetBufferMessages(bool bufferMessages) { }

    public string? GetRawLobbyIdentifier() => null;
}
