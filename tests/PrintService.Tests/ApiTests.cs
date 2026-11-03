using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using EpicErp.PrintService;
using EpicErp.PrintService.Printing;

namespace PrintService.Tests;

public class ApiTests
{
    private static ByteArrayContent PdfContent(byte[] bytes, string contentType = "application/pdf")
    {
        var content = new ByteArrayContent(bytes);
        content.Headers.ContentType = new MediaTypeHeaderValue(contentType);
        return content;
    }

    [Fact]
    public async Task Printers_endpoint_returns_printers_and_default()
    {
        using var factory = new PrintServiceFactory();
        var response = await factory.CreateAuthorizedClient().GetAsync("/api/v1/printers");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("Office Laser", json.RootElement.GetProperty("defaultPrinter").GetString());
        var printers = json.RootElement.GetProperty("printers");
        Assert.Equal(2, printers.GetArrayLength());
        Assert.True(printers[0].GetProperty("isDefault").GetBoolean());
        Assert.Equal("Offline", printers[1].GetProperty("status").GetString());
    }

    [Fact]
    public async Task Print_valid_pdf_goes_to_default_printer()
    {
        using var factory = new PrintServiceFactory();
        var response = await factory.CreateAuthorizedClient()
            .PostAsync("/api/v1/print/pdf?copies=2&jobName=Invoice%20123", PdfContent(TestPdf.TwoPages()));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var result = await response.Content.ReadFromJsonAsync<PdfPrintResult>();
        Assert.Equal(new PdfPrintResult("Office Laser", "Invoice 123", 2, 2), result);

        var sent = Assert.Single(factory.Printer.Requests);
        Assert.Equal("Office Laser", sent.PrinterName);
        Assert.Equal(2, sent.Copies);
        Assert.Equal(TestPdf.TwoPages(), sent.Pdf);
    }

    [Fact]
    public async Task Print_accepts_octet_stream_and_defaults_job_name()
    {
        using var factory = new PrintServiceFactory();
        var response = await factory.CreateAuthorizedClient()
            .PostAsync("/api/v1/print/pdf", PdfContent(TestPdf.TwoPages(), "application/octet-stream"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var sent = Assert.Single(factory.Printer.Requests);
        Assert.Equal("ERP Document", sent.JobName);
        Assert.Equal(1, sent.Copies);
    }

    [Fact]
    public async Task Print_with_wrong_content_type_returns_415()
    {
        using var factory = new PrintServiceFactory();
        var response = await factory.CreateAuthorizedClient()
            .PostAsync("/api/v1/print/pdf", PdfContent(TestPdf.TwoPages(), "application/json"));
        Assert.Equal(HttpStatusCode.UnsupportedMediaType, response.StatusCode);
    }

    [Fact]
    public async Task Print_non_pdf_returns_400()
    {
        using var factory = new PrintServiceFactory();
        var response = await factory.CreateAuthorizedClient()
            .PostAsync("/api/v1/print/pdf", PdfContent("hello world"u8.ToArray()));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(factory.Printer.Requests);
    }

    [Fact]
    public async Task Print_empty_body_returns_400()
    {
        using var factory = new PrintServiceFactory();
        var response = await factory.CreateAuthorizedClient()
            .PostAsync("/api/v1/print/pdf", PdfContent([]));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(100)]
    public async Task Print_with_invalid_copies_returns_400(int copies)
    {
        using var factory = new PrintServiceFactory();
        var response = await factory.CreateAuthorizedClient()
            .PostAsync($"/api/v1/print/pdf?copies={copies}", PdfContent(TestPdf.TwoPages()));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Print_too_large_returns_413()
    {
        using var factory = new PrintServiceFactory();
        var big = new byte[ServiceConstants.MaxPdfSizeBytes + 1];
        TestPdf.TwoPages().CopyTo(big, 0);

        var response = await factory.CreateAuthorizedClient().PostAsync("/api/v1/print/pdf", PdfContent(big));

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
    }

    [Fact]
    public async Task Print_without_default_printer_returns_503()
    {
        using var factory = new PrintServiceFactory();
        factory.Catalog.Default = null;
        var response = await factory.CreateAuthorizedClient()
            .PostAsync("/api/v1/print/pdf", PdfContent(TestPdf.TwoPages()));
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
    }

    [Fact]
    public async Task Pdf_that_pdfium_rejects_returns_400()
    {
        using var factory = new PrintServiceFactory();
        factory.Printer.ThrowOnPrint = new InvalidPdfException("The PDF is password protected.");
        var response = await factory.CreateAuthorizedClient()
            .PostAsync("/api/v1/print/pdf", PdfContent(TestPdf.TwoPages()));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("password", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Compatibility_test_report_goes_to_report_print_printer()
    {
        using var factory = new PrintServiceFactory();
        var response = await factory.CreateAuthorizedClient()
            .PostAsync("/api/v1/print-compatibility-test", PdfContent(TestPdf.TwoPages()));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var sent = Assert.Single(factory.Printer.Requests);
        Assert.Equal("report_print", sent.PrinterName);
        Assert.Equal("Compatibility Test Report", sent.JobName);
        Assert.Equal(1, sent.Copies);
        Assert.True(sent.ExactSize, "Letterhead report must print at exact size");
    }

    [Fact]
    public async Task Print_pdf_uses_fit_to_page_not_exact_size()
    {
        using var factory = new PrintServiceFactory();
        await factory.CreateAuthorizedClient().PostAsync("/api/v1/print/pdf", PdfContent(TestPdf.TwoPages()));
        Assert.False(Assert.Single(factory.Printer.Requests).ExactSize);
    }

    [Fact]
    public async Task Compatibility_test_report_ignores_os_default_printer()
    {
        using var factory = new PrintServiceFactory();
        factory.Catalog.Default = null; // no OS default printer at all
        var response = await factory.CreateAuthorizedClient()
            .PostAsync("/api/v1/print-compatibility-test?copies=3&jobName=CT-42", PdfContent(TestPdf.TwoPages()));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var sent = Assert.Single(factory.Printer.Requests);
        Assert.Equal("report_print", sent.PrinterName);
        Assert.Equal("CT-42", sent.JobName);
        Assert.Equal(3, sent.Copies);
    }

    [Fact]
    public async Task Compatibility_test_report_validates_like_print_pdf()
    {
        using var factory = new PrintServiceFactory();
        var client = factory.CreateAuthorizedClient();

        var notPdf = await client.PostAsync("/api/v1/print-compatibility-test", PdfContent("hello"u8.ToArray()));
        var wrongType = await client.PostAsync("/api/v1/print-compatibility-test", PdfContent(TestPdf.TwoPages(), "text/plain"));

        Assert.Equal(HttpStatusCode.BadRequest, notPdf.StatusCode);
        Assert.Equal(HttpStatusCode.UnsupportedMediaType, wrongType.StatusCode);
        Assert.Empty(factory.Printer.Requests);
    }

    [Fact]
    public async Task Compatibility_test_report_requires_api_key()
    {
        using var factory = new PrintServiceFactory();
        var response = await factory.CreateClient()
            .PostAsync("/api/v1/print-compatibility-test", PdfContent(TestPdf.TwoPages()));
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Missing_report_printer_returns_503()
    {
        using var factory = new PrintServiceFactory();
        factory.Printer.ThrowOnPrint = new PrinterUnavailableException("Printer 'report_print' is not installed on this PC.");
        var response = await factory.CreateAuthorizedClient()
            .PostAsync("/api/v1/print-compatibility-test", PdfContent(TestPdf.TwoPages()));

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Contains("report_print", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Printer_failure_returns_503()
    {
        using var factory = new PrintServiceFactory();
        factory.Printer.ThrowOnPrint = new PrinterUnavailableException("Printer 'Office Laser' is not available.");
        var response = await factory.CreateAuthorizedClient()
            .PostAsync("/api/v1/print/pdf", PdfContent(TestPdf.TwoPages()));
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
    }
}
