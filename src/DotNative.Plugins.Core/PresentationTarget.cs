namespace DotNative;

public enum NativePlatform
{
    Unknown,
    IOS,
    Android,
    MacOS,
    Windows,
    Linux,
}

/// <summary>The native renderer's platform, which can differ from the development host.</summary>
public sealed record PresentationTarget(NativePlatform Platform)
{
    public static PresentationTarget Local { get; } =
        new(
            (OperatingSystem.IsIOS() || OperatingSystem.IsMacCatalyst()) ? NativePlatform.IOS
            : OperatingSystem.IsAndroid() ? NativePlatform.Android
            : OperatingSystem.IsMacOS() ? NativePlatform.MacOS
            : OperatingSystem.IsWindows() ? NativePlatform.Windows
            : OperatingSystem.IsLinux() ? NativePlatform.Linux
            : NativePlatform.Unknown
        );
}
