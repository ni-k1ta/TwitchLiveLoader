internal sealed class EmptyDirectoryCleaner
{
    private readonly string _root;
    private readonly CancellationToken _ct;

    public EmptyDirectoryCleaner(string root, CancellationToken ct = default)
        => (_root, _ct) = (root, ct);

    public Task RunAsync() => Task.Run(LoopAsync);  


    private async Task LoopAsync()
    {
        var timer = new PeriodicTimer(TimeSpan.FromDays(30));

        while (await timer.WaitForNextTickAsync(_ct))
        {
            CleanDir(_root);
        }
    }
    private void CleanDir(string dir)
    {
        foreach (var sub in Directory.EnumerateDirectories(dir))
            CleanDir(sub);

        if (!Directory.EnumerateFileSystemEntries(dir).Any())
        {
            Directory.Delete(dir, recursive: false);
        }
    }
}
