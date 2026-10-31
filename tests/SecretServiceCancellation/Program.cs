using System.Diagnostics;
using DotNative.SecureStorage;

// This test deliberately locks the login collection. Never run against a user's keyring.
if (!OperatingSystem.IsLinux()
    || (!File.Exists("/.dockerenv") && !File.Exists("/run/.containerenv"))
    || Environment.GetEnvironmentVariable("DOTNATIVE_ISOLATED_KEYRING_TEST") != "1")
    throw new InvalidOperationException("Requires an isolated Linux container/keyring and DOTNATIVE_ISOLATED_KEYRING_TEST=1.");

var storage = new SystemSecureStorage("dotnative-cancellation-verification");
if (args.Contains("cleanup"))
{
    using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(5));
    await storage.DeleteAllAsync(cleanup.Token);
    return;
}

using (var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10)))
{
    const string fixture = "Native Keyring: Ћирилица čćžšđ";
    await storage.WriteAsync("owned-fixture", fixture, deadline.Token);
    if (await storage.ReadAsync("owned-fixture", deadline.Token) != fixture
        || !await storage.ContainsKeyAsync("owned-fixture", deadline.Token))
        throw new Exception("Native Secret Service Unicode roundtrip failed");
    await storage.WriteAsync("owned-fixture", fixture + " updated", deadline.Token);
    var values = await storage.ReadAllAsync(deadline.Token);
    if (!values.TryGetValue("owned-fixture", out var value) || value != fixture + " updated")
        throw new Exception("Native Secret Service update/enumeration failed");
    await storage.DeleteAsync("owned-fixture", deadline.Token);
    if (await storage.ContainsKeyAsync("owned-fixture", deadline.Token))
        throw new Exception("Native Secret Service deletion failed");
    await storage.WriteAsync("owned-fixture", fixture, deadline.Token);
    Console.WriteLine("PASS: Linux Secret Service native CRUD, Unicode and enumeration with native-owned schema");
}
var lockInfo = new ProcessStartInfo("gdbus");
foreach (var arg in new[] { "call", "--session", "--dest", "org.freedesktop.secrets",
    "--object-path", "/org/freedesktop/secrets", "--method", "org.freedesktop.Secret.Service.Lock",
    "['/org/freedesktop/secrets/collection/login']" })
    lockInfo.ArgumentList.Add(arg);
using var locking = Process.Start(lockInfo) ?? throw new Exception("Could not start gdbus");
await locking.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
if (locking.ExitCode != 0) throw new Exception("Could not lock test keyring");

using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(2));
var timer = Stopwatch.StartNew();
try
{
    await storage.ReadAsync("owned-fixture", cancel.Token).WaitAsync(TimeSpan.FromSeconds(8));
    throw new Exception("Locked keyring read did not cancel");
}
catch (OperationCanceledException) when (cancel.IsCancellationRequested)
{
    Console.WriteLine($"PASS: Linux Secret Service cancelled a blocked native read after {timer.ElapsedMilliseconds} ms");
}
