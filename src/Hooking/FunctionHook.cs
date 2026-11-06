namespace Gestapo.SteamNetworking.Hooking;

// An installed hook. It stays until Dispose, so keeping a reference is optional.
internal sealed class FunctionHook : IDisposable
{
    private readonly HookTarget _target;
    private Action? _restore;

    internal FunctionHook(HookTarget target, Action restore)
    {
        _target = target;
        _restore = restore;
    }

    public void Dispose()
    {
        if (_restore is null)
        {
            return;
        }

        _restore();
        _restore = null;
        Log.Information("Unhooked {Hook}", this);
    }

    public override string ToString() => _target.ToString();
}
