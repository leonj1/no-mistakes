namespace NoMistakes.Core;

public sealed record BuildInfoOptions(string Version, string Commit, string Date)
{
    public static BuildInfoOptions Defaults { get; } = new("dev", "unknown", "unknown");
}

public static class BuildInfo
{
    public static string Format(BuildInfoOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        return $"{CurrentVersion(options)} ({options.Commit}) {options.Date}";
    }

    public static string CurrentVersion(BuildInfoOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        return string.IsNullOrWhiteSpace(options.Version) ? "dev" : options.Version;
    }
}
