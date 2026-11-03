namespace Gestapo.SteamNetworking;

public static class Logger
{
    private static readonly TextWriter s_out = TextWriter.Synchronized(Open());

    public static void WriteLine(string message)
    {
        s_out.WriteLine($"{DateTime.Now:HH:mm:ss.fff} {message}");
    }

    // BaseDirectory is the game's folder, so a second game instance finds the file taken.
    private static StreamWriter Open()
    {
        for (var n = 1; ; n++)
        {
            try
            {
                return new StreamWriter(
                    path: Path.Combine(AppContext.BaseDirectory, n == 1 ? "steamnet.log" : $"steamnet.{n}.log"),
                    append: true)
                {
                    AutoFlush = true
                };
            }
            catch (IOException) when (n < 8)
            {
            }
        }
    }
}