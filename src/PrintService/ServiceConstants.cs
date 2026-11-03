namespace EpicErp.PrintService;

/// <summary>Fixed service settings. Nothing here is read from configuration.</summary>
public static class ServiceConstants
{
    public const string ServiceName = "EpicPrintService";
    public const string EventLogSource = "Epic Print Service";

    /// <summary>Loopback port the HTTP API listens on.</summary>
    public const int Port = 9555;

    /// <summary>Shared key the React app sends in the X-Api-Key header.</summary>
    public const string ApiKey = "epichealthcare@2026printserver";

    /// <summary>Printer that POST /api/v1/print-compatibility-test always prints to.</summary>
    public const string CompatibilityReportPrinter = "report_print";

    /// <summary>Largest PDF accepted (50 MB).</summary>
    public const long MaxPdfSizeBytes = 50L * 1024 * 1024;
}
