namespace EpicErp.PrintService.Printing;

public sealed record PrinterInfo(
    string Name,
    bool IsDefault,
    string? Status,
    bool? IsOffline,
    bool? IsNetwork);

public sealed record PrinterListResponse(string? DefaultPrinter, IReadOnlyList<PrinterInfo> Printers);

public interface IPrinterCatalog
{
    /// <summary>All printers installed for the account the service runs as.</summary>
    PrinterListResponse GetPrinters();

    /// <summary>The OS default printer of the account the service runs as, or null if there is none.</summary>
    string? ResolveDefaultPrinter();

    /// <summary>
    /// Finds an installed printer by name (case-insensitive) and returns its exact installed name,
    /// or null if no such printer is installed.
    /// </summary>
    string? FindInstalledPrinter(string name);
}
