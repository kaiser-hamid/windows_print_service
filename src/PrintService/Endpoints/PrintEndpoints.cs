using EpicErp.PrintService.Printing;
using Microsoft.Net.Http.Headers;

namespace EpicErp.PrintService.Endpoints;

public static class PrintEndpoints
{
    public const int MaxCopies = 99;
    public const int MaxJobNameLength = 128;
    public const string DefaultJobName = "ERP Document";
    public const string CompatibilityReportJobName = "Compatibility Test Report";
    public const string SampleLabelJobName = "Sample Labels";

    private static readonly string[] AcceptedContentTypes = ["application/pdf", "application/octet-stream"];
    private static readonly string[] AcceptedRawContentTypes = ["text/plain", "application/octet-stream"];
    private static readonly byte[] PdfSignature = "%PDF-"u8.ToArray();

    // The PDF spec allows the header anywhere in the first 1024 bytes.
    private const int SignatureSearchWindow = 1024;

    public static IEndpointRouteBuilder MapPrintEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/v1/print/pdf", PrintPdfAsync)
            .WithName("PrintPdf");

        app.MapPost("/api/v1/print-compatibility-test", PrintCompatibilityTestAsync)
            .WithName("PrintCompatibilityTest");

        app.MapPost("/api/v1/print-sample-label", PrintSampleLabelAsync)
            .WithName("PrintSampleLabel");

        return app;
    }

    /// <summary>Prints on the OS default printer.</summary>
    private static Task<IResult> PrintPdfAsync(
        HttpContext context,
        IPrinterCatalog catalog,
        IPdfPrinter printer,
        ILoggerFactory loggerFactory,
        int? copies,
        string? jobName,
        CancellationToken cancellationToken) =>
        HandlePrintAsync(context, printer, loggerFactory, copies, jobName, DefaultJobName,
            catalog.ResolveDefaultPrinter,
            noPrinterDetail: "No default printer is set for the account the print service runs as.",
            exactSize: false,
            cancellationToken);

    /// <summary>
    /// Prints the compatibility test report, always on the fixed "report_print" printer, at exact size
    /// (no scaling, aligned to the paper edge) because it's laid out for pre-printed letterhead.
    /// </summary>
    private static Task<IResult> PrintCompatibilityTestAsync(
        HttpContext context,
        IPdfPrinter printer,
        ILoggerFactory loggerFactory,
        int? copies,
        string? jobName,
        CancellationToken cancellationToken) =>
        HandlePrintAsync(context, printer, loggerFactory, copies, jobName, CompatibilityReportJobName,
            () => ServiceConstants.CompatibilityReportPrinter,
            noPrinterDetail: null,
            exactSize: true,
            cancellationToken);

    private static async Task<IResult> HandlePrintAsync(
        HttpContext context,
        IPdfPrinter printer,
        ILoggerFactory loggerFactory,
        int? copies,
        string? jobName,
        string defaultJobName,
        Func<string?> resolvePrinter,
        string? noPrinterDetail,
        bool exactSize,
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

        var name = NormalizeJobName(jobName, defaultJobName);

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

        var printerName = resolvePrinter();
        if (printerName is null)
            return Problem(StatusCodes.Status503ServiceUnavailable, "No default printer", noPrinterDetail ?? "No printer available.");

        try
        {
            var printRequest = new PdfPrintRequest(pdf, printerName, copyCount, name) { ExactSize = exactSize };
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
    /// Prints sample barcode labels on the fixed "label_print" GoDEX printer. The body is printer-ready EZPL
    /// (possibly several labels in one job) and is spooled as a RAW job exactly as received: it is never
    /// decoded, trimmed or re-encoded. There is deliberately no "copies" parameter: the ERP backend logs
    /// every label print, so reprints must go through it.
    /// </summary>
    private static async Task<IResult> PrintSampleLabelAsync(
        HttpContext context,
        IRawPrinter printer,
        ILoggerFactory loggerFactory,
        string? jobName,
        CancellationToken cancellationToken)
    {
        var logger = loggerFactory.CreateLogger(typeof(PrintEndpoints));
        var request = context.Request;
        const long maxBytes = ServiceConstants.MaxRawSizeBytes;
        const string printerName = ServiceConstants.SampleLabelPrinter;

        if (!IsAcceptedContentType(request.ContentType, AcceptedRawContentTypes))
            return Problem(StatusCodes.Status415UnsupportedMediaType, "Unsupported content type",
                "Send the raw label data with Content-Type: text/plain (or application/octet-stream).");

        var name = NormalizeJobName(jobName, SampleLabelJobName);

        if (request.ContentLength > maxBytes)
            return LabelTooLarge(maxBytes);

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
            return LabelTooLarge(maxBytes);

        if (IsBlank(data))
            return Problem(StatusCodes.Status400BadRequest, "Empty body",
                "The request body must contain the label printer data (EZPL).");

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

    private static IResult LabelTooLarge(long maxBytes) =>
        Problem(StatusCodes.Status413PayloadTooLarge, "Label data too large",
            $"The label data exceeds the maximum allowed size of {maxBytes / (1024 * 1024)} MB.");

    private static IResult Problem(int status, string title, string detail) =>
        TypedResults.Problem(statusCode: status, title: title, detail: detail);
}
