using DotNative.Plugins;
using DotNative.Permissions;
using DotNative.Sensors;
using Storage = DotNative.SecureStorage.SecureStorage;
using Picker = DotNative.FilePicker.FilePicker;
using PermissionApi = DotNative.Permissions.Permissions;
using SensorApi = DotNative.Sensors.Sensors;

internal static class MigratedPluginChecks
{
    private static string? previousValue;
#if MIGRATED_FILE_SELECTION
    private static bool selectedFixture;
#endif

    public static async Task RunAsync()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        var token = deadline.Token;
        const string persistentKey = "dotnative-verification-lifecycle";
        if (previousValue is not null && await Storage.ReadAsync(persistentKey, token) != previousValue)
            throw new Exception("SecureStorage lost data when the host was recreated");
        var key = "probe-" + Guid.NewGuid().ToString("N");
        var value = "Native storage: ćirilica Ћирилица " + Guid.NewGuid();
        Exception? storageFailure = null;
        try
        {
            await Task.Run(() => Storage.WriteAsync(key, value, token));
            if (await Storage.ReadAsync(key, token) != value || !await Storage.ContainsKeyAsync(key, token))
                throw new Exception("SecureStorage roundtrip mismatch");
            if (!(await Storage.ReadAllAsync(token)).TryGetValue(key, out var all) || all != value)
                throw new Exception("SecureStorage enumeration mismatch");
            await Storage.WriteAsync(key, value + " updated", token);
            if (await Storage.ReadAsync(key, token) != value + " updated")
                throw new Exception("SecureStorage update mismatch");
            await Storage.WriteAsync(persistentKey, value, token);
            previousValue = value;
        }
        catch (Exception error) { storageFailure = error; throw; }
        finally
        {
            using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            try { await Storage.DeleteAsync(key, cleanup.Token); }
            catch (Exception error) when (storageFailure is not null)
            { Console.WriteLine("CLEANUP: test key removal failed: " + error.Message); }
        }
        if (await Storage.ContainsKeyAsync(key, token)) throw new Exception("SecureStorage delete failed");
        Console.WriteLine("PASS: SecureStorage native worker CRUD, Unicode, enumeration, host persistence");

        if (OperatingSystem.IsAndroid() || OperatingSystem.IsIOS())
        {
            var before = await PermissionApi.CheckAsync(PermissionKind.Camera, token);
            Console.WriteLine("WAIT: requesting camera permission; before=" + before);
            var result = await PermissionApi.RequestAsync(PermissionKind.Camera, token);
            if (await PermissionApi.CheckAsync(PermissionKind.Camera, token) != result)
                throw new Exception("Permission request/check mismatch");
            Console.WriteLine("PASS: Permissions native request callback and status=" + result);

            using var sensorTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(6));
            try
            {
                var count = 0;
                await foreach (var sample in SensorApi.WatchAsync(SensorKind.Accelerometer, sensorTimeout.Token))
                {
                    if (!double.IsFinite(sample.X) || !double.IsFinite(sample.Y) || !double.IsFinite(sample.Z))
                        throw new Exception("Invalid native sensor sample");
                    if (++count == 2) break;
                }
                if (count != 2) throw new Exception("Sensor stream ended before two samples");
                Console.WriteLine("PASS: Sensors two native samples and subscription disposal");
            }
            catch (PluginException error) when (error.Code == "sensor_unavailable")
            {
                Console.WriteLine("LIMITATION: native sensor_unavailable; physical sensor delivery not verified here");
            }
        }

        Console.WriteLine("WAIT: FilePicker cancellation check");
        using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(700));
        try
        {
            await Picker.PickAsync(cancel.Token).WaitAsync(TimeSpan.FromSeconds(10));
            throw new Exception("FilePicker did not report cancellation");
        }
        catch (OperationCanceledException) when (cancel.IsCancellationRequested) { }
        Console.WriteLine("PASS: FilePicker native presentation and cancellation completed");
#if MIGRATED_FILE_SELECTION
        if (!selectedFixture)
        {
            const string name = "dotnative-plugin-fixture.txt";
            var expected = System.Text.Encoding.UTF8.GetBytes(string.Concat(
                Enumerable.Range(0, 4096).Select(i => $"DotNative native picker — row {i:D4}: čćžšđ\n")));
            if (OperatingSystem.IsIOS())
                await File.WriteAllBytesAsync(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), name), expected);
            else if (!OperatingSystem.IsAndroid())
            {
                var directory = Path.Combine(Path.GetTempPath(), "dotnative-plugin-verification");
                Directory.CreateDirectory(directory);
                var fixture = Path.Combine(directory, name);
                await File.WriteAllBytesAsync(fixture, expected);
                Console.WriteLine("FIXTURE: " + fixture);
            }
            Console.WriteLine("WAIT: select owned fixture " + name);
            using var selectionDeadline = new CancellationTokenSource(TimeSpan.FromMinutes(2));
            await using var file = await Picker.PickAsync(selectionDeadline.Token)
                ?? throw new Exception("Fixture selection was cancelled");
            if (file.Name != name) throw new Exception("Select only the generated test fixture; no other file will be read");
            if (file.Length is { } length && length != expected.Length) throw new Exception("Picked file length mismatch");
            await using (var stream = await file.OpenReadAsync(selectionDeadline.Token))
            {
                using var actual = new MemoryStream();
                await stream.CopyToAsync(actual, selectionDeadline.Token);
                if (!actual.ToArray().SequenceEqual(expected)) throw new Exception("Native file stream contents mismatch");
            }
            await file.DisposeAsync();
            try
            {
                await file.OpenReadAsync();
                throw new Exception("Disposed file reopened");
            }
            catch (ObjectDisposedException) { }
            selectedFixture = true;
            Console.WriteLine("PASS: FilePicker selected owned UTF-8 fixture, read all native stream chunks, disposed stream/file, rejected reopen");
        }
#endif
    }
}
