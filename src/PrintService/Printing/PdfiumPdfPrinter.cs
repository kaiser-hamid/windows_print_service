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

    // PDFium is not thread-safe, so the whole process shares one gate.
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static readonly Lazy<bool> Initialized = new(() =>
    {
        PdfiumNative.FPDF_InitLibrary();
        return true;
    }, LazyThreadSafetyMode.ExecutionAndPublication);

    public async Task<PdfPrintResult> PrintAsync(PdfPrintRequest request, CancellationToken cancellationToken)
    {
        await Gate.WaitAsync(cancellationToken);
        try
        {
            // Once spooling has started we let it finish, so a dropped HTTP connection can't leave half a job.
            return await Task.Run(() => PrintCore(request), CancellationToken.None);
        }
        finally
        {
            Gate.Release();
        }
    }

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
            throw new PrinterUnavailableException($"Printer '{request.PrinterName}' is not available.");

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

        // Turn each sheet to match its page, so landscape pages print landscape on drivers that support it.
        printDocument.QueryPageSettings += (_, e) =>
        {
            var (width, height) = GetPageSize(document, sheet % pageCount);
            e.PageSettings.Landscape = width > height;
        };

        printDocument.PrintPage += (_, e) =>
        {
            RenderPage(document, sheet % pageCount, e);
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

    private static void RenderPage(IntPtr document, int index, PrintPageEventArgs e)
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

                // Print at actual size, shrinking only when the page doesn't fit inside the printable area.
                var scale = Math.Min(1.0, Math.Min(areaWidth / targetWidth, areaHeight / targetHeight));
                var width = (int)Math.Round(targetWidth * scale);
                var height = (int)Math.Round(targetHeight * scale);
                var x = (areaWidth - width) / 2;
                var y = (areaHeight - height) / 2;

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
