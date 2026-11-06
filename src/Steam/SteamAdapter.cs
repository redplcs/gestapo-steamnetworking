using System.Collections.Concurrent;
using System.Net;
using System.Threading.Channels;
using Gestapo.SteamNetworking.Sockets;
using Steamworks;

namespace Gestapo.SteamNetworking.Steam;

// Carries packets to friends who run the mod, over SteamNetworkingMessages. Each instance marks
// itself with a rich presence key and owns 10.x.y.z, the low 24 bits of its account ID.
internal sealed unsafe class SteamAdapter : INetworkAdapter
{
    private const string PresenceKey = "gestapo";

    // Keeps a session open with every peer: Steam needs over a second to open one, longer than the
    // game's LAN search waits for replies. Sessions that stay idle for a few minutes are closed.
    private const int KeepaliveInterval = 10_000;

    // Shorter than any packet header, so the receiving VirtualWinsock drops it.
    private static readonly byte[] s_keepalive = [0];

    private static readonly AppId_t s_appId = new(480);

    private readonly ConcurrentDictionary<IPAddress, CSteamID> _peers = new();
    private readonly Channel<Packet> _received = Channel.CreateUnbounded<Packet>(new() { SingleReader = true });
    private readonly Callback<SteamNetworkingMessagesSessionRequest_t> _sessionRequest;
    private readonly Callback<SteamNetworkingMessagesSessionFailed_t> _sessionFailed;
    private readonly Thread _thread;
    private volatile bool _closed;

    internal SteamAdapter()
    {
        // Replaces steam_appid.txt, which would land in VirtualStore when the game is in Program Files.
        Environment.SetEnvironmentVariable("SteamAppId", s_appId.ToString());

        if (SteamAPI.InitEx(out var error) != ESteamAPIInitResult.k_ESteamAPIInitResult_OK)
        {
            throw new InvalidOperationException(error);
        }

        Address = AddressOf(SteamUser.GetSteamID());
        SteamFriends.SetRichPresence(PresenceKey, "1");
        SteamNetworkingUtils.InitRelayNetworkAccess();

        _sessionRequest = Callback<SteamNetworkingMessagesSessionRequest_t>.Create(OnSessionRequest);
        _sessionFailed = Callback<SteamNetworkingMessagesSessionFailed_t>.Create(OnSessionFailed);
        _thread = new Thread(Pump) { IsBackground = true, Name = nameof(SteamAdapter) };
        _thread.Start();

        Log.Information("Steam adapter started for {Name} at {Address}", SteamFriends.GetPersonaName(), Address);
    }

    public IPAddress Address { get; }

    public void Dispose()
    {
        // The pump calls into Steam, so it has to stop before the API shuts down below.
        _closed = true;
        _thread.Join();
        _received.Writer.TryComplete();
        _sessionRequest.Dispose();
        _sessionFailed.Dispose();
        SteamFriends.ClearRichPresence();
        SteamAPI.Shutdown();
    }

    public IAsyncEnumerable<Packet> ReceiveAllAsync(CancellationToken cancellationToken = default)
    {
        return _received.Reader.ReadAllAsync(cancellationToken);
    }

    public void Send(IPAddress to, ReadOnlySpan<byte> data)
    {
        // The game keeps sending after the mod stops, when Steam is already shut down.
        if (_closed)
        {
            return;
        }

        if (to.Equals(Address))
        {
            _received.Writer.TryWrite(new Packet(Address, data.ToArray()));
            return;
        }

        if (to.Equals(IPAddress.Broadcast))
        {
            _received.Writer.TryWrite(new Packet(Address, data.ToArray()));

            foreach (var peer in FindPeers())
            {
                SendTo(peer, data);
            }

            return;
        }

        if (!_peers.TryGetValue(to, out var target))
        {
            target = FindPeers().FirstOrDefault(peer => AddressOf(peer).Equals(to));

            if (target == CSteamID.Nil)
            {
                Log.Warning("No Steam peer at {Address}", to);
                return;
            }

            _peers[to] = target;
        }

        SendTo(target, data);
    }

    private static void SendTo(CSteamID peer, ReadOnlySpan<byte> data)
    {
        const int flags = Constants.k_nSteamNetworkingSend_UnreliableNoNagle |
                          Constants.k_nSteamNetworkingSend_AutoRestartBrokenSession;
        
        var result = SteamNetworkingMessages.SendMessageToUser(peer, data, flags, 0);

        if (result != EResult.k_EResultOK)
        {
            Log.Warning("Sending to {Peer} failed with {Result}", peer, result);
        }
    }

    private void Pump()
    {
        var messages = new nint[64];
        var keepaliveDue = 0L;

        while (!_closed)
        {
            try
            {
                SteamAPI.RunCallbacks();

                if (Environment.TickCount64 >= keepaliveDue)
                {
                    keepaliveDue = Environment.TickCount64 + KeepaliveInterval;

                    foreach (var peer in FindPeers())
                    {
                        SendTo(peer, s_keepalive);
                    }
                }

                int count;
                while ((count = SteamNetworkingMessages.ReceiveMessagesOnChannel(0, messages, messages.Length)) > 0)
                {
                    for (var i = 0; i < count; i++)
                    {
                        try
                        {
                            var message = SteamNetworkingMessage_t.FromIntPtr(messages[i]);
                            var peer = message.m_identityPeer.GetSteamID();
                            var from = AddressOf(peer);

                            _peers[from] = peer;
                            _received.Writer.TryWrite(new Packet(from, new ReadOnlySpan<byte>((void*)message.m_pData, message.m_cbSize).ToArray()));
                        }
                        finally
                        {
                            SteamNetworkingMessage_t.Release(messages[i]);
                        }
                    }
                }
            }
            catch (Exception e)
            {
                Log.Error(e, "Steam pump iteration failed");
            }

            Thread.Sleep(1);
        }
    }

    // Any friend may open a session, not only peers: Steam hides the rich presence of an Invisible
    // friend, who can still find a peer's game and join it.
    private void OnSessionRequest(SteamNetworkingMessagesSessionRequest_t request)
    {
        var identity = request.m_identityRemote;
        var peer = identity.GetSteamID();

        if (!SteamFriends.HasFriend(peer, EFriendFlags.k_EFriendFlagImmediate))
        {
            Log.Warning("Rejected a session request from {Peer}: not a friend", peer);
            return;
        }

        SteamNetworkingMessages.AcceptSessionWithUser(ref identity);
        Log.Information("Accepted a session with {Name} at {Address}", SteamFriends.GetFriendPersonaName(peer), AddressOf(peer));
    }

    private static void OnSessionFailed(SteamNetworkingMessagesSessionFailed_t failed)
    {
        Log.Warning("Session with {Peer} failed with {Reason}", failed.m_info.m_identityRemote.GetSteamID(), failed.m_info.m_eEndReason);
    }

    private static IEnumerable<CSteamID> FindPeers() => SteamFriends
        .GetFriends(EFriendFlags.k_EFriendFlagImmediate)
        .Where(friend => SteamFriends.GetFriendRichPresence(friend, PresenceKey) == "1");

    private static IPAddress AddressOf(CSteamID id)
    {
        var account = id.GetAccountID().m_AccountID;
        return new IPAddress([10, (byte)(account >> 16), (byte)(account >> 8), (byte)account]);
    }
}
