namespace RoundTableMeeting.AgentCollaboration;

internal static class NativeMeetingGuide
{
    internal const string Path = "/agent/0.1.0/native-guide.txt";
    internal static readonly string Text = Read();

    private static string Read()
    {
        using var stream = typeof(NativeMeetingGuide).Assembly.GetManifestResourceStream("RoundTableMeeting.NativeGuide.v0.1.0")
            ?? throw new InvalidOperationException("Native meeting guide is missing.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
