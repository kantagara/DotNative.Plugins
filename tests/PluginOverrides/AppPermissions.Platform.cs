using DotNative.Permissions;

// Test-only application backend. This verifies extensibility, not OS permission behavior.
sealed partial class AppPermissions
{
    public partial Task<PermissionStatus> CheckAsync(PermissionKind permission, CancellationToken cancellationToken) =>
        Task.FromResult(PermissionStatus.Restricted);
}
