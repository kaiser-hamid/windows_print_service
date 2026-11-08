using System.Net;
using System.Net.Sockets;
using EpicErp.PrintService;
using EpicErp.PrintService.Endpoints;
using EpicErp.PrintService.Printing;
using EpicErp.PrintService.Security;
using Microsoft.Extensions.Hosting.WindowsServices;
using Microsoft.Extensions.Logging.EventLog;

const string CorsPolicyName = "LocalhostOnly";

var builder = WebApplication.CreateBuilder(new WebApplicationOptions
{
    Args = args,
    // A service starts in C:\Windows\System32, so point the content root at the install folder.
    ContentRootPath = WindowsServiceHelpers.IsWindowsService() ? AppContext.BaseDirectory : default,
});

builder.Services.AddWindowsService(o => o.ServiceName = ServiceConstants.ServiceName);
builder.Services.Configure<EventLogSettings>(s =>
{
    s.SourceName = ServiceConstants.EventLogSource;
    s.LogName = "Application";
});

// Loopback only: the API can never be reached from another machine.
builder.WebHost.ConfigureKestrel(kestrel =>
{
    kestrel.AddServerHeader = false;
    kestrel.Limits.MaxRequestBodySize = ServiceConstants.MaxPdfSizeBytes;
    kestrel.Listen(IPAddress.Loopback, ServiceConstants.Port);
    if (Socket.OSSupportsIPv6)
        kestrel.Listen(IPAddress.IPv6Loopback, ServiceConstants.Port);
});

builder.Services.AddCors(cors => cors.AddPolicy(CorsPolicyName, policy => policy
    .SetIsOriginAllowed(LocalhostOriginPolicy.IsAllowed)
    .WithMethods(HttpMethods.Get, HttpMethods.Post)
    .WithHeaders(ApiKeyMiddleware.HeaderName, EpicErp.PrintService.Endpoints.PrintEndpoints.PrinterNameHeader, "Content-Type")
    .SetPreflightMaxAge(TimeSpan.FromMinutes(10))));

builder.Services.AddProblemDetails();
builder.Services.AddSingleton<IPrinterCatalog, WindowsPrinterCatalog>();
builder.Services.AddSingleton<IPdfPrinter, PdfiumPdfPrinter>();
builder.Services.AddSingleton<IRawPrinter, WinSpoolRawPrinter>();

var app = builder.Build();

app.UseExceptionHandler();

// Order matters: drop non-localhost origins → answer CORS preflight → require the API key.
app.UseMiddleware<OriginGuardMiddleware>();
app.UseCors(CorsPolicyName);
app.UseMiddleware<ApiKeyMiddleware>();

app.MapPrinterEndpoints();
app.MapPrintEndpoints();

app.Lifetime.ApplicationStarted.Register(() =>
    app.Logger.LogInformation("Epic Print Service started on http://127.0.0.1:{Port}", ServiceConstants.Port));

app.Run();

public partial class Program;
