namespace EpicErp.PrintService.Printing;

/// <param name="Data">Printer-ready bytes (e.g. GoDEX EZPL), sent to the printer exactly as given.</param>
public sealed record RawPrintRequest(byte[] Data, string PrinterName, string JobName)
{
    /// <summary>
    /// Diagnostics/testing only, never set from the API: the spooler writes the job to this file
    /// (DOC_INFO_1.pOutputFile) instead of sending it to the device.
    /// </summary>
    internal string? PrintToFilePath { get; init; }
}

public sealed record RawPrintResult(string Printer, string JobName, int Bytes);

public interface IRawPrinter
{
    /// <summary>
    /// Spools <see cref="RawPrintRequest.Data"/> byte for byte as a RAW job, bypassing the driver's rendering.
    /// Returns once the job has been handed to the spooler.
    /// </summary>
    /// <exception cref="PrinterUnavailableException">
    /// The printer does not exist, is offline, or the spooler rejected (part of) the job.
    /// </exception>
    Task<RawPrintResult> PrintAsync(RawPrintRequest request, CancellationToken cancellationToken);
}
