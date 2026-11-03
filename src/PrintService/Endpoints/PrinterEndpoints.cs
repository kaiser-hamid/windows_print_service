using EpicErp.PrintService.Printing;

namespace EpicErp.PrintService.Endpoints;

public static class PrinterEndpoints
{
    public static IEndpointRouteBuilder MapPrinterEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/v1/printers", (IPrinterCatalog catalog) => TypedResults.Ok(catalog.GetPrinters()))
            .WithName("ListPrinters");

        return app;
    }
}
