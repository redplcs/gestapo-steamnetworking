using System.Collections.Concurrent;
using Serilog.Core;
using Serilog.Events;

namespace Gestapo.SteamNetworking.Game;

// Shows every error in the game's message box, one at a time and in order.
internal sealed class MessageBoxSink : ILogEventSink
{
    private readonly ConcurrentQueue<string> _pending = new();

    public void Emit(LogEvent logEvent) => _pending.Enqueue(Describe(logEvent).ReplaceLineEndings(" "));

    internal void ShowPending()
    {
        GameMessageBox.Update();

        if (GameMessageBox.CanShow && _pending.TryDequeue(out var text))
        {
            GameMessageBox.Show(text);
        }
    }

    private static string Describe(LogEvent logEvent) => logEvent.Exception is { } e
        ? $"{logEvent.RenderMessage()}: {e.Message}"
        : logEvent.RenderMessage();
}
