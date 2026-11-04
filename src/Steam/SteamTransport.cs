using System.Collections.Concurrent;
using System.Net;
using Gestapo.SteamNetworking.Sockets;
using Steamworks;

namespace Gestapo.SteamNetworking.Steam;

// Carries packets to friends who run the mod, over SteamNetworkingMessages. Each instance marks
// itself with a rich presence key and owns 10.x.y.z, the low 24 bits of its account ID.
internal sealed unsafe class SteamTransport : ITransport
{
    private const string PresenceKey = "gestapo";
    private const uint Broadcast = 0xFFFFFFFF;

    private const int SendFlags =
        Constants.k_nSteamNetworkingSend_UnreliableNoNagle | Constants.k_nSteamNetworkingSend_AutoRestartBrokenSession;

    // Keeps a session open with every peer: Steam needs over a second to open one, longer than the
    // game's LAN search waits for replies. Sessions that stay idle for a few minutes are closed.
    private const int KeepaliveInterval = 10_000;

    // Shorter than any packet header, so the receiving VirtualSockets drops it.
    private static readonly byte[] s_keepalive = [0];

    private readonly ConcurrentDictionary<uint, CSteamID> _peers = new();
    private readonly Callback<SteamNetworkingMessagesSessionRequest_t> _sessionRequest;
    private readonly Callback<SteamNetworkingMessagesSessionFailed_t> _sessionFailed;
    private readonly Thread _thread;
    private volatile bool _closed;

    internal SteamTransport()
    {
        Address = AddressOf(SteamUser.GetSteamID());
        SteamFriends.SetRichPresence(PresenceKey, "1");
        SteamNetworkingUtils.InitRelayNetworkAccess();

        _sessionRequest = Callback<SteamNetworkingMessagesSessionRequest_t>.Create(OnSessionRequest);
        _sessionFailed = Callback<SteamNetworkingMessagesSessionFailed_t>.Create(OnSessionFailed);
        _thread = new Thread(Pump) { IsBackground = true, Name = nameof(SteamTransport) };
        _thread.Start();

        Logger.WriteLine($"steam transport: {SteamFriends.GetPersonaName()}, address {new IPAddress(Address)}");
    }

    public event Action<uint, ReadOnlySpan<byte>>? Received;

    public uint Address { get; }

    public void Dispose()
    {
        // The pump calls into Steam, so it has to stop before SteamManager shuts the API down.
        _closed = true;
        _thread.Join();
        _sessionRequest.Dispose();
        _sessionFailed.Dispose();
        SteamFriends.ClearRichPresence();
    }

    public void Send(uint to, ReadOnlySpan<byte> packet)
    {
        if (to == Address)
        {
            Received?.Invoke(Address, packet);
            return;
        }

        if (to == Broadcast)
        {
            Received?.Invoke(Address, packet);

            foreach (var peer in FindPeers())
            {
                SendTo(peer, packet);
            }

            return;
        }

        if (!_peers.TryGetValue(to, out var target))
        {
            FindPeers();

            if (!_peers.TryGetValue(to, out target))
            {
                Logger.WriteLine($"steam transport: no peer at {new IPAddress(to)}");
                return;
            }
        }

        SendTo(target, packet);
    }

    private static void SendTo(CSteamID peer, ReadOnlySpan<byte> packet)
    {
        var identity = new SteamNetworkingIdentity();
        identity.SetSteamID(peer);

        fixed (byte* data = packet)
        {
            var result = SteamNetworkingMessages.SendMessageToUser(ref identity, (nint)data, (uint)packet.Length, SendFlags, 0);

            if (result != EResult.k_EResultOK)
            {
                Logger.WriteLine($"steam transport: send to {peer} failed: {result}");
            }
        }
    }

    // ponytail: polls every 1 ms, which Sleep rounds up to the system timer resolution
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
                            Received?.Invoke(from, new ReadOnlySpan<byte>((void*)message.m_pData, message.m_cbSize));
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
                Logger.WriteLine($"steam transport: pump failed: {e}");
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

        if (!IsFriend(peer))
        {
            Logger.WriteLine($"steam transport: rejected a session from {peer}");
            return;
        }

        SteamNetworkingMessages.AcceptSessionWithUser(ref identity);
        Logger.WriteLine($"steam transport: session with {SteamFriends.GetFriendPersonaName(peer)} at {new IPAddress(AddressOf(peer))}");
    }

    private static void OnSessionFailed(SteamNetworkingMessagesSessionFailed_t failed)
    {
        Logger.WriteLine($"steam transport: session with {failed.m_info.m_identityRemote.GetSteamID()} failed: {failed.m_info.m_eEndReason}");
    }

    private List<CSteamID> FindPeers()
    {
        var peers = new List<CSteamID>();
        var count = SteamFriends.GetFriendCount(EFriendFlags.k_EFriendFlagImmediate);

        for (var i = 0; i < count; i++)
        {
            var friend = SteamFriends.GetFriendByIndex(i, EFriendFlags.k_EFriendFlagImmediate);

            if (IsPeer(friend))
            {
                peers.Add(friend);
                _peers[AddressOf(friend)] = friend;
            }
        }

        return peers;
    }

    private static bool IsFriend(CSteamID id) =>
        SteamFriends.GetFriendRelationship(id) == EFriendRelationship.k_EFriendRelationshipFriend;

    private static bool IsPeer(CSteamID id) =>
        IsFriend(id) && SteamFriends.GetFriendRichPresence(id, PresenceKey) == "1";

    // 10.x.y.z in network byte order.
    private static uint AddressOf(CSteamID id)
    {
        var account = id.GetAccountID().m_AccountID;
        return 0x0A | (account >> 16 & 0xFF) << 8 | (account >> 8 & 0xFF) << 16 | (account & 0xFF) << 24;
    }
}
