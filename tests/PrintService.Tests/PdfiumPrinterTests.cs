using System.Drawing.Printing;
using EpicErp.PrintService.Printing;
using Microsoft.Extensions.Logging.Abstractions;

namespace PrintService.Tests;

/// <summary>Uses the real native PDFium library and the Windows spooler.</summary>
public class PdfiumPrinterTests
{
    private const string PdfPrinter = "Microsoft Print to PDF";

    private static readonly PdfiumPdfPrinter Printer = new(NullLogger<PdfiumPdfPrinter>.Instance);

    [Fact]
    public async Task Corrupt_pdf_is_reported_as_invalid()
    {
        var bytes = "%PDF-1.4\nthis is not really a pdf"u8.ToArray();
        await Assert.ThrowsAsync<InvalidPdfException>(() =>
            Printer.PrintAsync(new PdfPrintRequest(bytes, PdfPrinter, 1, "corrupt"), CancellationToken.None));
    }

    [Fact]
    public async Task Unknown_printer_is_reported_as_unavailable()
    {
        await Assert.ThrowsAsync<PrinterUnavailableException>(() =>
            Printer.PrintAsync(new PdfPrintRequest(TestPdf.TwoPages(), "No Such Printer 9f3a", 1, "x"), CancellationToken.None));
    }

    [Fact]
    public async Task Renders_and_spools_to_microsoft_print_to_pdf()
    {
        if (!PrinterSettings.InstalledPrinters.Cast<string>().Contains(PdfPrinter))
            return; // Printer not installed on this machine; nothing to check.

        var output = Path.Combine(Path.GetTempPath(), $"epic-print-test-{Guid.NewGuid():N}.pdf");
        try
        {
            var result = await Printer.PrintAsync(
                new PdfPrintRequest(TestPdf.TwoPages(), PdfPrinter, 2, "Epic print test") { PrintToFilePath = output },
                CancellationToken.None);

            Assert.Equal(2, result.Pages);
            Assert.Equal(2, result.Copies);

            // The spooler writes the file asynchronously after the job is handed over.
            var deadline = DateTime.UtcNow.AddSeconds(30);
            while (DateTime.UtcNow < deadline && (!File.Exists(output) || new FileInfo(output).Length == 0 || IsLocked(output)))
                await Task.Delay(250);

            Assert.True(File.Exists(output), "Spooled PDF was not produced");
            var produced = await File.ReadAllBytesAsync(output);
            Assert.StartsWith("%PDF-", System.Text.Encoding.ASCII.GetString(produced, 0, 5));

            // 2 pages x 2 copies, collated; the second page must have come out landscape.
            var (pageCount, secondIsLandscape) = InspectPdf(produced);
            Assert.Equal(4, pageCount);
            Assert.True(secondIsLandscape, "Landscape page was not printed landscape");
        }
        finally
        {
            if (File.Exists(output))
                File.Delete(output);
        }
    }

    private static (int PageCount, bool SecondIsLandscape) InspectPdf(byte[] pdf)
    {
        var buffer = System.Runtime.InteropServices.Marshal.AllocHGlobal(pdf.Length);
        try
        {
            System.Runtime.InteropServices.Marshal.Copy(pdf, 0, buffer, pdf.Length);
            var doc = PdfiumNative.FPDF_LoadMemDocument64(buffer, (nuint)pdf.Length, null);
            Assert.NotEqual(IntPtr.Zero, doc);
            try
            {
                var count = PdfiumNative.FPDF_GetPageCount(doc);
                var page = PdfiumNative.FPDF_LoadPage(doc, 1);
                var landscape = PdfiumNative.FPDF_GetPageWidthF(page) > PdfiumNative.FPDF_GetPageHeightF(page);
                PdfiumNative.FPDF_ClosePage(page);
                return (count, landscape);
            }
            finally
            {
                PdfiumNative.FPDF_CloseDocument(doc);
            }
        }
        finally
        {
            System.Runtime.InteropServices.Marshal.FreeHGlobal(buffer);
        }
    }

    private static bool IsLocked(string path)
    {
        try
        {
            using var _ = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.None);
            return false;
        }
        catch (IOException)
        {
            return true;
        }
    }
}
