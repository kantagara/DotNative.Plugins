using DotNative.Plugins;
using DotNative.Permissions;
using DotNative.FilePicker;

var first = PluginImplementations.Register<IPermissions>(() => new AppPermissions());
try
{
    PluginImplementations.Register<IPermissions>(() => new AppPermissions());
    throw new Exception("Duplicate registration accepted");
}
catch (InvalidOperationException) { }
if (await Permissions.CheckAsync(PermissionKind.Camera) != PermissionStatus.Restricted)
    throw new Exception("Static call did not use the application's partial class");
first.Dispose();
using var replacement = PluginImplementations.Register<IPermissions>(() => new AppPermissions());
first.Dispose();
if (await Permissions.RequestAsync(PermissionKind.Camera) != PermissionStatus.Restricted)
    throw new Exception("Old registration removed its replacement");

var releases = 0;
var stream = new MemoryStream("Application backend"u8.ToArray());
using var picker = PluginImplementations.Register<IFilePicker>(() => new AppPicker(() =>
    PickedFile.FromStream("fixture.txt", stream.Length, _ => Task.FromResult<Stream>(stream),
        () => { releases++; return ValueTask.CompletedTask; })));
var file = await FilePicker.PickAsync() ?? throw new Exception("No application file");
await using (var selected = await file.OpenReadAsync())
    if (await new StreamReader(selected).ReadToEndAsync() != "Application backend")
        throw new Exception("Application file content mismatch");
await file.DisposeAsync();
await file.DisposeAsync();
if (releases != 1 || stream.CanRead) throw new Exception("Application resources were not released once");
try { await file.OpenReadAsync(); throw new Exception("Disposed file reopened"); }
catch (ObjectDisposedException) { }

Console.WriteLine("PASS: packaged static APIs use application partial backends without DI or a native host; duplicate and stale registrations, file ownership");

sealed class AppPicker(Func<PickedFile> select) : IFilePicker
{
    public Task<PickedFile?> PickAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<PickedFile?>(select());
}

sealed partial class AppPermissions : IPermissions
{
    public partial Task<PermissionStatus> CheckAsync(PermissionKind permission, CancellationToken cancellationToken = default);
    public Task<PermissionStatus> RequestAsync(PermissionKind permission, CancellationToken cancellationToken = default) =>
        CheckAsync(permission, cancellationToken);
}
