namespace DotNative;

/// <summary>
/// A service initializer scheduled by its application host. Implementations must observe cancellation.
/// DotNative starts these after the first frame; other hosts choose their startup integration.
/// </summary>
public interface IApplicationInitializer
{
    Task InitializeAsync(CancellationToken stopping);
}
