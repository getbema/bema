using System.Collections.Concurrent;

namespace Bema.App.Platform;

/// <summary>
/// One thread, in a single-threaded COM apartment, that runs every call into PowerPoint in order (the
/// Windows counterpart of the Mac app's serial AppleScript queue). PowerPoint's COM objects belong to
/// the thread that made them, so they never leave this thread.
/// </summary>
public sealed class StaWorker : IDisposable
{
    readonly BlockingCollection<Action> queue = [];
    readonly Thread thread;

    public StaWorker(string name)
    {
        thread = new Thread(() =>
        {
            foreach (var work in queue.GetConsumingEnumerable()) work();
        }) { IsBackground = true, Name = name };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
    }

    public Task<T> Run<T>(Func<T> work)
    {
        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        queue.Add(() =>
        {
            try { tcs.SetResult(Busy.Retry(work)); }
            catch (Exception e) { tcs.SetException(e); }
        });
        return tcs.Task;
    }

    public Task Run(Action work) => Run(() => { work(); return true; });

    public void Dispose() => queue.CompleteAdding();
}

/// <summary>PowerPoint answers "busy" while a dialog is open or it's opening a deck: try again briefly.</summary>
static class Busy
{
    const int RpcCallRejected = unchecked((int)0x80010001);
    const int RpcServerCallRetryLater = unchecked((int)0x8001010A);

    public static T Retry<T>(Func<T> work)
    {
        for (var attempt = 0; ; attempt++)
        {
            try { return work(); }
            catch (System.Runtime.InteropServices.COMException e) when ((e.HResult == RpcCallRejected || e.HResult == RpcServerCallRetryLater) && attempt < 20)
            {
                Thread.Sleep(150);
            }
        }
    }
}
