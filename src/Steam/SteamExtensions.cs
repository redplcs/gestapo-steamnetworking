using Steamworks;

namespace Gestapo.SteamNetworking.Steam;

internal static class SteamExtensions
{
    extension(SteamFriends)
    {
        public static IEnumerable<CSteamID> GetFriends(EFriendFlags flags)
        {
            var count = SteamFriends.GetFriendCount(flags);

            for (var i = 0; i < count; i++)
            {
                yield return SteamFriends.GetFriendByIndex(i, flags);
            }
        }
    }

    extension(SteamNetworkingMessages)
    {
        public static unsafe EResult SendMessageToUser(CSteamID user, ReadOnlySpan<byte> data, int sendFlags, int remoteChannel)
        {
            var identity = new SteamNetworkingIdentity();
            identity.SetSteamID(user);

            fixed (byte* pointer = data)
            {
                return SteamNetworkingMessages.SendMessageToUser(ref identity, (nint)pointer, (uint)data.Length, sendFlags, remoteChannel);
            }
        }
    }
}
