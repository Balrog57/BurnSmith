namespace BurnSmith.Payload;

public static class Payload
{
    /// <summary>The zip of bundled tools, or null when this build carries none.</summary>
    public static Stream? Open() => typeof(Payload).Assembly.GetManifestResourceStream("tools.zip");
}
