using EpicErp.PrintService.Printing;
using Microsoft.Net.Http.Headers;

namespace EpicErp.PrintService.Endpoints;

public static class PrintEndpoints
{
    public const int MaxCopies = 99;
    public const int MaxJobNameLength = 128;
    public const string DefaultJobName = "ERP Document";
    public const string RawJobName = "Raw Print";

    /// <summary>Request header carrying the name of the installed printer to print on (required).</summary>
    public const string PrinterNameHeader = "X-Printer-Name";

    private static readonly string[] AcceptedContentTypes = ["application/pdf", "application/octet-stream"];
    private static readonly string[] AcceptedRawContentTypes = ["text/plain", "application/octet-stream"];
    private static readonly byte[] PdfSignature = "%PDF-"u8.ToArray();

    // The PDF spec allows the header anywhere in the first 1024 bytes.
    private const int SignatureSearchWindow = 1024;

    public static IEndpointRouteBuilder MapPrintEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/v1/print/pdf", PrintPdfAsync)
            .WithName("PrintPdf");

        app.MapPost("/api/v1/print/raw", PrintRawAsync)
            .WithName("PrintRaw");

        return app;
    }

    /// <summary>
    /// Prints a PDF on the installed printer named in the X-Printer-Name header. Each page picks the printer
    /// paper that matches its own size. With exactSize=true pages print 1:1 aligned to the paper edge
    /// (for pre-printed letterhead) instead of being fitted to the printable area.
    /// </summary>
    private static async Task<IResult> PrintPdfAsync(
        HttpContext context,
        IPrinterCatalog catalog,
        IPdfPrinter printer,
        ILoggerFactory loggerFactory,
        int? copies,
        string? jobName,
        bool? exactSize,
        CancellationToken cancellationToken)
    {
        var logger = loggerFactory.CreateLogger(typeof(PrintEndpoints));
        var request = context.Request;
        const long maxBytes = ServiceConstants.MaxPdfSizeBytes;

        if (!IsAcceptedContentType(request.ContentType, AcceptedContentTypes))
            return Problem(StatusCodes.Status415UnsupportedMediaType, "Unsupported content type",
                "Send the raw PDF bytes with Content-Type: application/pdf (or application/octet-stream).");

        var copyCount = copies ?? 1;
        if (copyCount is < 1 or > MaxCopies)
            return Problem(StatusCodes.Status400BadRequest, "Invalid copies", $"'copies' must be between 1 and {MaxCopies}.");

        var (printerName, printerError) = ResolvePrinter(request, catalog);
        if (printerName is null)
            return printerError!;

        var name = NormalizeJobName(jobName, DefaultJobName);

        if (request.ContentLength > maxBytes)
            return TooLarge(maxBytes);

        byte[]? pdf;
        try
        {
            pdf = await ReadBodyAsync(request.Body, maxBytes, cancellationToken);
        }
        catch (BadHttpRequestException ex) when (ex.StatusCode == StatusCodes.Status413PayloadTooLarge)
        {
            pdf = null;
        }

        if (pdf is null)
            return TooLarge(maxBytes);

        if (pdf.Length == 0)
            return Problem(StatusCodes.Status400BadRequest, "Empty body", "The request body must contain the PDF file.");

        if (!HasPdfSignature(pdf))
            return Problem(StatusCodes.Status400BadRequest, "Invalid PDF", "The request body is not a PDF file (missing %PDF- header).");

        try
        {
            var printRequest = new PdfPrintRequest(pdf, printerName, copyCount, name) { ExactSize = exactSize ?? false };
            var result = await printer.PrintAsync(printRequest, cancellationToken);
            return TypedResults.Ok(result);
        }
        catch (InvalidPdfException ex)
        {
            return Problem(StatusCodes.Status400BadRequest, "Invalid PDF", ex.Message);
        }
        catch (PrinterUnavailableException ex)
        {
            logger.LogError(ex, "Printer '{Printer}' unavailable", printerName);
            return Problem(StatusCodes.Status503ServiceUnavailable, "Printer unavailable", ex.Message);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Printing to '{Printer}' failed", printerName);
            return Problem(StatusCodes.Status500InternalServerError, "Print failed", ex.Message);
        }
    }

    /// <summary>
    /// Prints printer-ready data (e.g. GoDEX EZPL labels, possibly several in one job) on the installed printer
    /// named in the X-Printer-Name header. The body is spooled as a RAW job exactly as received: it is never
    /// decoded, trimmed or re-encoded. There is deliberately no "copies" parameter: the ERP backend logs
    /// every label print, so reprints must go through it.
    /// </summary>
    private static async Task<IResult> PrintRawAsync(
        HttpContext context,
        IPrinterCatalog catalog,
        IRawPrinter printer,
        ILoggerFactory loggerFactory,
        string? jobName,
        CancellationToken cancellationToken)
    {
        var logger = loggerFactory.CreateLogger(typeof(PrintEndpoints));
        var request = context.Request;
        const long maxBytes = ServiceConstants.MaxRawSizeBytes;

        if (!IsAcceptedContentType(request.ContentType, AcceptedRawContentTypes))
            return Problem(StatusCodes.Status415UnsupportedMediaType, "Unsupported content type",
                "Send the raw print data with Content-Type: text/plain (or application/octet-stream).");

        var (printerName, printerError) = ResolvePrinter(request, catalog);
        if (printerName is null)
            return printerError!;

        var name = NormalizeJobName(jobName, RawJobName);

        if (request.ContentLength > maxBytes)
            return RawTooLarge(maxBytes);

        byte[]? data;
        try
        {
            data = await ReadBodyAsync(request.Body, maxBytes, cancellationToken);
        }
        catch (BadHttpRequestException ex) when (ex.StatusCode == StatusCodes.Status413PayloadTooLarge)
        {
            data = null;
        }

        if (data is null)
            return RawTooLarge(maxBytes);

        if (IsBlank(data))
            return Problem(StatusCodes.Status400BadRequest, "Empty body",
                "The request body must contain the printer data (e.g. EZPL).");

        try
        {
            var result = await printer.PrintAsync(new RawPrintRequest(data, printerName, name), cancellationToken);
            return TypedResults.Ok(result);
        }
        catch (PrinterUnavailableException ex)
        {
            logger.LogError(ex, "Printer '{Printer}' unavailable", printerName);
            return Problem(StatusCodes.Status503ServiceUnavailable, "Printer unavailable", ex.Message);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Raw printing to '{Printer}' failed", printerName);
            return Problem(StatusCodes.Status500InternalServerError, "Print failed", ex.Message);
        }
    }

    /// <summary>
    /// Reads the X-Printer-Name header and resolves it to an installed printer (case-insensitive).
    /// Returns the exact installed name, or a 400 problem when the header is missing or names no installed printer.
    /// </summary>
    private static (string? PrinterName, IResult? Error) ResolvePrinter(HttpRequest request, IPrinterCatalog catalog)
    {
        var requested = request.Headers[PrinterNameHeader].ToString().Trim();
        if (requested.Length == 0)
            return (null, Problem(StatusCodes.Status400BadRequest, "Printer name required",
                $"Send the printer name in the {PrinterNameHeader} header. List the installed printers with GET /api/v1/printers."));

        var installed = catalog.FindInstalledPrinter(requested);
        if (installed is null)
            return (null, Problem(StatusCodes.Status400BadRequest, "Unknown printer",
                $"No printer named '{requested}' is installed on this PC. List the installed printers with GET /api/v1/printers."));

        return (installed, null);
    }

    private static string NormalizeJobName(string? jobName, string defaultJobName)
    {
        var name = string.IsNullOrWhiteSpace(jobName) ? defaultJobName : jobName.Trim();
        return name.Length > MaxJobNameLength ? name[..MaxJobNameLength] : name;
    }

    private static bool IsAcceptedContentType(string? contentType, string[] accepted)
    {
        if (!MediaTypeHeaderValue.TryParse(contentType, out var parsed) || parsed.MediaType.Value is not { } mediaType)
            return false;
        return accepted.Contains(mediaType, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>True when the body is empty or only ASCII whitespace (space, tab, CR, LF, VT, FF).</summary>
    private static bool IsBlank(byte[] data) =>
        data.AsSpan().IndexOfAnyExcept(" \t\r\n\v\f"u8) < 0;

    /// <summary>Reads the body into memory. Returns null when it exceeds <paramref name="maxBytes"/>.</summary>
    private static async Task<byte[]?> ReadBodyAsync(Stream body, long maxBytes, CancellationToken cancellationToken)
    {
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int read;
        while ((read = await body.ReadAsync(chunk, cancellationToken)) > 0)
        {
            if (buffer.Length + read > maxBytes)
                return null;
            buffer.Write(chunk, 0, read);
        }
        return buffer.ToArray();
    }

    private static bool HasPdfSignature(byte[] data)
    {
        var window = data.AsSpan(0, Math.Min(data.Length, SignatureSearchWindow));
        return window.IndexOf(PdfSignature) >= 0;
    }

    private static IResult TooLarge(long maxBytes) =>
        Problem(StatusCodes.Status413PayloadTooLarge, "PDF too large",
            $"The PDF exceeds the maximum allowed size of {maxBytes / (1024 * 1024)} MB.");

    private static IResult RawTooLarge(long maxBytes) =>
        Problem(StatusCodes.Status413PayloadTooLarge, "Print data too large",
            $"The print data exceeds the maximum allowed size of {maxBytes / (1024 * 1024)} MB.");

    private static IResult Problem(int status, string title, string detail) =>
        TypedResults.Problem(statusCode: status, title: title, detail: detail);
}
