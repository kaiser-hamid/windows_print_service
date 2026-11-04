using EpicErp.PrintService;
using EpicErp.PrintService.Printing;
using EpicErp.PrintService.Security;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace PrintService.Tests;

public sealed class PrintServiceFactory : WebApplicationFactory<Program>
{
    public FakePrinterCatalog Catalog { get; } = new();
    public FakePdfPrinter Printer { get; } = new();
    public FakeRawPrinter RawPrinter { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<IPrinterCatalog>();
            services.RemoveAll<IPdfPrinter>();
            services.RemoveAll<IRawPrinter>();
            services.AddSingleton<IPrinterCatalog>(Catalog);
            services.AddSingleton<IPdfPrinter>(Printer);
            services.AddSingleton<IRawPrinter>(RawPrinter);
        });
    }

    public HttpClient CreateAuthorizedClient()
    {
        var client = CreateClient();
        client.DefaultRequestHeaders.Add(ApiKeyMiddleware.HeaderName, ServiceConstants.ApiKey);
        return client;
    }
}

public sealed class FakePrinterCatalog : IPrinterCatalog
{
    public string? Default { get; set; } = "Office Laser";

    public PrinterListResponse GetPrinters() => new(Default,
    [
        new PrinterInfo("Office Laser", Default == "Office Laser", "Idle", false, false),
        new PrinterInfo("Label Printer", Default == "Label Printer", "Offline", true, true),
    ]);

    public string? ResolveDefaultPrinter() => Default;
}

public sealed class FakePdfPrinter : IPdfPrinter
{
    public List<PdfPrintRequest> Requests { get; } = [];
    public Exception? ThrowOnPrint { get; set; }

    public Task<PdfPrintResult> PrintAsync(PdfPrintRequest request, CancellationToken cancellationToken)
    {
        if (ThrowOnPrint is not null)
            throw ThrowOnPrint;
        Requests.Add(request);
        return Task.FromResult(new PdfPrintResult(request.PrinterName, request.JobName, 2, request.Copies));
    }
}

public sealed class FakeRawPrinter : IRawPrinter
{
    public List<RawPrintRequest> Requests { get; } = [];
    public Exception? ThrowOnPrint { get; set; }

    public Task<RawPrintResult> PrintAsync(RawPrintRequest request, CancellationToken cancellationToken)
    {
        if (ThrowOnPrint is not null)
            throw ThrowOnPrint;
        Requests.Add(request);
        return Task.FromResult(new RawPrintResult(request.PrinterName, request.JobName, request.Data.Length));
    }
}
