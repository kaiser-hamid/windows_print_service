namespace EpicErp.PrintService.Printing;

/// <summary>
/// The single queue every print job goes through, so jobs are processed one at a time across all endpoints.
/// PDFium is not thread-safe, and serialising raw jobs as well keeps them from interleaving in the spooler.
/// </summary>
internal static class PrintQueue
{
    private static readonly SemaphoreSlim Gate = new(1, 1);

    /// <summary>
    /// Waits for the queue (cancellable), then runs <paramref name="job"/> on the thread pool. Once the job has
    /// started it always runs to completion, so a dropped HTTP connection can't leave half a job in the spooler.
    /// </summary>
    public static async Task<T> RunAsync<T>(Func<T> job, CancellationToken cancellationToken)
    {
        await Gate.WaitAsync(cancellationToken);
        try
        {
            return await Task.Run(job, CancellationToken.None);
        }
        finally
        {
            Gate.Release();
        }
    }
}
