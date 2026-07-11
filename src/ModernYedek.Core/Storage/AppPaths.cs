namespace ModernYedek.Core.Storage;

public sealed class AppPaths
{
    public required string RootDirectory { get; init; }
    public required string SettingsFile { get; init; }
    public required string SecretsFile { get; init; }
    public required string LogFile { get; init; }

    public static AppPaths ForCurrentUser()
    {
        var root = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "ModernYedek");

        return new AppPaths
        {
            RootDirectory = root,
            SettingsFile = Path.Combine(root, "settings.json"),
            SecretsFile = Path.Combine(root, "secrets.dat"),
            LogFile = Path.Combine(root, "logs.jsonl")
        };
    }

    public static AppPaths ForDirectory(string root)
    {
        return new AppPaths
        {
            RootDirectory = root,
            SettingsFile = Path.Combine(root, "settings.json"),
            SecretsFile = Path.Combine(root, "secrets.dat"),
            LogFile = Path.Combine(root, "logs.jsonl")
        };
    }
}
