namespace Flipper.Core.Update;

public readonly record struct InstallProgress(int Current, int Total, string Message);
