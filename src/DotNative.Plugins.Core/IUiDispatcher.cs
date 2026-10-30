namespace DotNative;

public interface IUiDispatcher
{
    bool CheckAccess();

    // Must enqueue (never invoke inline), execute on the OS main thread, and
    // surface callback exceptions to the host's error boundary.
    void Post(Action action);
}
