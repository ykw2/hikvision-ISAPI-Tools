namespace HikIsapi;

internal static class AppIcon
{
    private static readonly Icon Icon = Load();

    public static Icon Current => Icon;

    private static Icon Load()
    {
        using var source = typeof(AppIcon).Assembly.GetManifestResourceStream("HikIsapi.app.ico");
        if (source == null)
            return SystemIcons.Application;
        var buffer = new MemoryStream();
        source.CopyTo(buffer);
        buffer.Position = 0;
        return new Icon(buffer);
    }
}
