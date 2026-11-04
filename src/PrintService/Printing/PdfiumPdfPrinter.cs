using System.ComponentModel;
using System.Drawing.Printing;
using System.Runtime.InteropServices;

namespace EpicErp.PrintService.Printing;

/// <summary>
/// Prints PDFs by having PDFium render each page directly onto the printer's GDI device context.
/// There is no rasterisation step on our side, so text and vectors stay sharp at the printer's DPI.
/// </summary>
public sealed class PdfiumPdfPrinter(ILogger<PdfiumPdfPrinter> logger) : IPdfPrinter
{
    private const int LOGPIXELSX = 88;
    private const int LOGPIXELSY = 90;

    private static readonly Lazy<bool> Initialized = new(() =>
    {
        PdfiumNative.FPDF_InitLibrary();
        return true;
    }, LazyThreadSafetyMode.ExecutionAndPublication);

    // PDFium is not thread-safe, so every job goes through the process-wide print queue.
    public Task<PdfPrintResult> PrintAsync(PdfPrintRequest request, CancellationToken cancellationToken) =>
        PrintQueue.RunAsync(() => PrintCore(request), cancellationToken);

    private PdfPrintResult PrintCore(PdfPrintRequest request)
    {
        _ = Initialized.Value;

        var buffer = Marshal.AllocHGlobal(request.Pdf.Length);
        var document = IntPtr.Zero;
        try
        {
            Marshal.Copy(request.Pdf, 0, buffer, request.Pdf.Length);

            document = PdfiumNative.FPDF_LoadMemDocument64(buffer, (nuint)request.Pdf.Length, null);
            if (document == IntPtr.Zero)
                throw new InvalidPdfException(DescribeLoadError(PdfiumNative.FPDF_GetLastError()));

            var pageCount = PdfiumNative.FPDF_GetPageCount(document);
            if (pageCount <= 0)
                throw new InvalidPdfException("The PDF contains no pages.");

            Spool(document, pageCount, request);

            logger.LogInformation("Spooled '{JobName}' ({Pages} page(s) x {Copies}) to printer '{Printer}'",
                request.JobName, pageCount, request.Copies, request.PrinterName);

            return new PdfPrintResult(request.PrinterName, request.JobName, pageCount, request.Copies);
        }
        finally
        {
            if (document != IntPtr.Zero)
                PdfiumNative.FPDF_CloseDocument(document);
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static void Spool(IntPtr document, int pageCount, PdfPrintRequest request)
    {
        using var printDocument = new PrintDocument();
        printDocument.PrintController = new StandardPrintController(); // no "Printing..." dialog
        printDocument.DocumentName = request.JobName;
        printDocument.OriginAtMargins = false;

        var settings = printDocument.PrinterSettings;
        settings.PrinterName = request.PrinterName;
        if (!settings.IsValid)
            throw new PrinterUnavailableException($"Printer '{request.PrinterName}' is not installed on this PC.");

        var paperSizes = GetPaperSizes(settings);

        // Copies are produced by repeating the pages ourselves. Many drivers ignore the Copies setting,
        // and doing it this way keeps the output collated on every printer.
        settings.Copies = 1;

        if (request.PrintToFilePath is { } file)
        {
            settings.PrintToFile = true;
            settings.PrintFileName = file;
        }

        var totalSheets = pageCount * request.Copies;
        var sheet = 0;

        printDocument.BeginPrint += (_, _) => sheet = 0;

        // Match each sheet to its PDF page: pick the printer's paper of the same size (A4 page -> A4 paper)
        // and turn landscape pages landscape. If no paper matches, the printer's default paper is used.
        printDocument.QueryPageSettings += (_, e) =>
        {
            var (width, height) = GetPageSize(document, sheet % pageCount);
            if (FindMatchingPaper(paperSizes, width, height) is { } paper)
                e.PageSettings.PaperSize = paper;
            e.PageSettings.Landscape = width > height;
        };

        printDocument.PrintPage += (_, e) =>
        {
            RenderPage(document, sheet % pageCount, request.ExactSize, e);
            sheet++;
            e.HasMorePages = sheet < totalSheets;
        };

        try
        {
            printDocument.Print();
        }
        catch (InvalidPrinterException ex)
        {
            throw new PrinterUnavailableException($"Printer '{request.PrinterName}' is not available.", ex);
        }
        catch (Win32Exception ex)
        {
            throw new PrinterUnavailableException($"Printer '{request.PrinterName}' rejected the job: {ex.Message}", ex);
        }
    }

    // A paper matches a PDF page when both sides are within ~1.3 mm (PaperSize uses 1/100 inch units).
    private const int PaperMatchTolerance = 5;

    private static List<PaperSize> GetPaperSizes(PrinterSettings settings)
    {
        try
        {
            return settings.PaperSizes.Cast<PaperSize>().Where(p => p.Width > 0 && p.Height > 0).ToList();
        }
        catch (Exception)
        {
            return []; // driver couldn't list its paper sizes; fall back to its default paper
        }
    }

    private static PaperSize? FindMatchingPaper(List<PaperSize> paperSizes, float widthPt, float heightPt)
    {
        if (paperSizes.Count == 0 || widthPt <= 0 || heightPt <= 0)
            return null;

        // PDF points (1/72 inch) -> 1/100 inch, compared orientation-independently.
        var shortSide = Math.Min(widthPt, heightPt) / 72.0 * 100;
        var longSide = Math.Max(widthPt, heightPt) / 72.0 * 100;

        PaperSize? best = null;
        var bestError = double.MaxValue;
        foreach (var paper in paperSizes)
        {
            var errorShort = Math.Abs(Math.Min(paper.Width, paper.Height) - shortSide);
            var errorLong = Math.Abs(Math.Max(paper.Width, paper.Height) - longSide);
            if (errorShort > PaperMatchTolerance || errorLong > PaperMatchTolerance)
                continue;
            if (errorShort + errorLong < bestError)
            {
                best = paper;
                bestError = errorShort + errorLong;
            }
        }
        return best;
    }

    private static (float Width, float Height) GetPageSize(IntPtr document, int index)
    {
        var page = PdfiumNative.FPDF_LoadPage(document, index);
        if (page == IntPtr.Zero)
            return (0, 0);
        try
        {
            return (PdfiumNative.FPDF_GetPageWidthF(page), PdfiumNative.FPDF_GetPageHeightF(page));
        }
        finally
        {
            PdfiumNative.FPDF_ClosePage(page);
        }
    }

    private static void RenderPage(IntPtr document, int index, bool exactSize, PrintPageEventArgs e)
    {
        var page = PdfiumNative.FPDF_LoadPage(document, index);
        if (page == IntPtr.Zero)
            throw new InvalidPdfException($"Page {index + 1} could not be loaded.");

        try
        {
            // PDF page size in points (1/72 inch).
            var pageWidthPt = PdfiumNative.FPDF_GetPageWidthF(page);
            var pageHeightPt = PdfiumNative.FPDF_GetPageHeightF(page);

            var hdc = e.Graphics!.GetHdc();
            try
            {
                // Printable area in device pixels. On a printer DC, (0,0) is the top-left of the printable area.
                var areaWidth = Gdi32.GetDeviceCaps(hdc, Gdi32.HORZRES);
                var areaHeight = Gdi32.GetDeviceCaps(hdc, Gdi32.VERTRES);
                var dpiX = Gdi32.GetDeviceCaps(hdc, LOGPIXELSX);
                var dpiY = Gdi32.GetDeviceCaps(hdc, LOGPIXELSY);

                var rotate = 0;
                double targetWidth = pageWidthPt / 72.0 * dpiX;
                double targetHeight = pageHeightPt / 72.0 * dpiY;

                // If the driver ignored the orientation request, rotate the content to make the best use of the sheet.
                var pageIsLandscape = pageWidthPt > pageHeightPt;
                var areaIsLandscape = areaWidth > areaHeight;
                if (pageWidthPt != pageHeightPt && pageIsLandscape != areaIsLandscape)
                {
                    rotate = 1;
                    targetWidth = pageHeightPt / 72.0 * dpiX;
                    targetHeight = pageWidthPt / 72.0 * dpiY;
                }

                int x, y, width, height;
                if (exactSize)
                {
                    // 1:1, with the page's top-left on the paper's physical top-left. The DC origin is the
                    // printable area, which starts PHYSICALOFFSET pixels in from the paper edge.
                    width = (int)Math.Round(targetWidth);
                    height = (int)Math.Round(targetHeight);
                    x = -Gdi32.GetDeviceCaps(hdc, Gdi32.PHYSICALOFFSETX);
                    y = -Gdi32.GetDeviceCaps(hdc, Gdi32.PHYSICALOFFSETY);
                }
                else
                {
                    // Print at actual size, shrinking only when the page doesn't fit inside the printable area.
                    var scale = Math.Min(1.0, Math.Min(areaWidth / targetWidth, areaHeight / targetHeight));
                    width = (int)Math.Round(targetWidth * scale);
                    height = (int)Math.Round(targetHeight * scale);
                    x = (areaWidth - width) / 2;
                    y = (areaHeight - height) / 2;
                }

                PdfiumNative.FPDF_RenderPage(hdc, page, x, y, width, height, rotate,
                    PdfiumNative.FPDF_ANNOT | PdfiumNative.FPDF_PRINTING);
            }
            finally
            {
                e.Graphics.ReleaseHdc(hdc);
            }
        }
        finally
        {
            PdfiumNative.FPDF_ClosePage(page);
        }
    }

    private static string DescribeLoadError(uint code) => code switch
    {
        PdfiumNative.FPDF_ERR_FORMAT => "The file is not a valid PDF or is corrupted.",
        PdfiumNative.FPDF_ERR_PASSWORD => "The PDF is password protected.",
        PdfiumNative.FPDF_ERR_SECURITY => "The PDF uses an unsupported security handler.",
        _ => $"The PDF could not be loaded (PDFium error {code}).",
    };
}
