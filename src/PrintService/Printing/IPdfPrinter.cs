namespace EpicErp.PrintService.Printing;

public sealed record PdfPrintRequest(byte[] Pdf, string PrinterName, int Copies, string JobName)
{
    /// <summary>
    /// Diagnostics/testing only, never set from the API: spool to this file instead of the device
    /// (e.g. with "Microsoft Print to PDF") so no save dialog appears.
    /// </summary>
    internal string? PrintToFilePath { get; init; }
}

public sealed record PdfPrintResult(string Printer, string JobName, int Pages, int Copies);

public interface IPdfPrinter
{
    /// <summary>Renders the PDF and spools it to the printer. Returns once the job has been handed to the spooler.</summary>
    /// <exception cref="InvalidPdfException">The bytes are not a PDF that can be loaded.</exception>
    /// <exception cref="PrinterUnavailableException">The printer does not exist or cannot be used.</exception>
    Task<PdfPrintResult> PrintAsync(PdfPrintRequest request, CancellationToken cancellationToken);
}

public sealed class InvalidPdfException(string message) : Exception(message);

public sealed class PrinterUnavailableException(string message, Exception? inner = null) : Exception(message, inner);
