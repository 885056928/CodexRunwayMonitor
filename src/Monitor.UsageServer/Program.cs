namespace Monitor.UsageServer;

internal static class Program
{
    public static async Task Main(string[] args)
    {
        await using var app = UsageServer.Create(args);
        await app.RunAsync();
    }
}
