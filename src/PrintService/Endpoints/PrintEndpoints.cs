using EpicErp.PrintService.Printing;
using Microsoft.Net.Http.Headers;

namespace EpicErp.PrintService.Endpoints;

public static class PrintEndpoints
{
    public const int MaxCopies = 99;
    public const int MaxJobNameLength = 128;
    public const string DefaultJobName = "ERP Document";

    private static readonly string[] AcceptedContentTypes = ["application/pdf", "application/octet-stream"];
    private static readonly byte[] PdfSignature = "%PDF-"u8.ToArray();

    // The PDF spec allows the header anywhere in the first 1024 bytes.
    private const int SignatureSearchWindow = 1024;

    public static IEndpointRouteBuilder MapPrintEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/v1/print/pdf", PrintPdfAsync)
            .WithName("PrintPdf");

        return app;
    }

    private static async Task<IResult> PrintPdfAsync(
        HttpContext context,
        IPrinterCatalog catalog,
        IPdfPrinter printer,
        ILoggerFactory loggerFactory,
        int? copies,
        string? jobName,
        CancellationToken cancellationToken)
    {
        var logger = loggerFactory.CreateLogger(typeof(PrintEndpoints));
        var request = context.Request;
        const long maxBytes = ServiceConstants.MaxPdfSizeBytes;

        if (!IsAcceptedContentType(request.ContentType))
            return Problem(StatusCodes.Status415UnsupportedMediaType, "Unsupported content type",
                "Send the raw PDF bytes with Content-Type: application/pdf (or application/octet-stream).");

        var copyCount = copies ?? 1;
        if (copyCount is < 1 or > MaxCopies)
            return Problem(StatusCodes.Status400BadRequest, "Invalid copies", $"'copies' must be between 1 and {MaxCopies}.");

        var name = string.IsNullOrWhiteSpace(jobName) ? DefaultJobName : jobName.Trim();
        if (name.Length > MaxJobNameLength)
            name = name[..MaxJobNameLength];

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

        var printerName = catalog.ResolveDefaultPrinter();
        if (printerName is null)
            return Problem(StatusCodes.Status503ServiceUnavailable, "No default printer",
                "No default printer is set for the account the print service runs as.");

        try
        {
            var result = await printer.PrintAsync(new PdfPrintRequest(pdf, printerName, copyCount, name), cancellationToken);
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

    private static bool IsAcceptedContentType(string? contentType)
    {
        if (!MediaTypeHeaderValue.TryParse(contentType, out var parsed) || parsed.MediaType.Value is not { } mediaType)
            return false;
        return AcceptedContentTypes.Contains(mediaType, StringComparer.OrdinalIgnoreCase);
    }

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

    private static IResult Problem(int status, string title, string detail) =>
        TypedResults.Problem(statusCode: status, title: title, detail: detail);
}
