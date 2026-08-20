namespace Flipper.Core.Update;

public sealed class UninstallInfo
{
    public required string DisplayName { get; init; }
    public required string Publisher { get; init; }
    public required string DisplayVersion { get; init; }
    public required string InstallLocation { get; init; }
    public required string UninstallString { get; init; }
    public required string DisplayIcon { get; init; }
    public int EstimatedSizeKb { get; init; }
}
