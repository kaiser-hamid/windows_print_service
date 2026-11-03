using System.Drawing.Printing;
using System.Management;

namespace EpicErp.PrintService.Printing;

public sealed class WindowsPrinterCatalog(ILogger<WindowsPrinterCatalog> logger) : IPrinterCatalog
{
    public PrinterListResponse GetPrinters()
    {
        var names = GetInstalledPrinterNames();
        var defaultPrinter = ResolveDefaultPrinter();
        var details = QueryWmiDetails();

        var printers = names
            .Select(name =>
            {
                details.TryGetValue(name, out var d);
                return new PrinterInfo(
                    Name: name,
                    IsDefault: string.Equals(name, defaultPrinter, StringComparison.OrdinalIgnoreCase),
                    Status: d?.Status,
                    IsOffline: d?.IsOffline,
                    IsNetwork: d?.IsNetwork);
            })
            .ToList();

        return new PrinterListResponse(defaultPrinter, printers);
    }

    public string? ResolveDefaultPrinter()
    {
        try
        {
            // A new PrinterSettings instance points at the OS default printer of the current account.
            var settings = new PrinterSettings();
            if (settings.IsValid && !string.IsNullOrWhiteSpace(settings.PrinterName))
                return settings.PrinterName;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not read the OS default printer");
        }

        return null;
    }

    private static List<string> GetInstalledPrinterNames()
    {
        var list = new List<string>();
        foreach (string name in PrinterSettings.InstalledPrinters)
            list.Add(name);
        return list;
    }

    private sealed record WmiPrinter(string? Status, bool? IsOffline, bool? IsNetwork);

    private Dictionary<string, WmiPrinter> QueryWmiDetails()
    {
        var result = new Dictionary<string, WmiPrinter>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var searcher = new ManagementObjectSearcher(
                "SELECT Name, PrinterStatus, WorkOffline, Network, ExtendedPrinterStatus FROM Win32_Printer");
            using var collection = searcher.Get();
            foreach (var obj in collection)
            {
                using (obj)
                {
                    if (obj["Name"] is not string name)
                        continue;

                    var status = MapStatus(obj["PrinterStatus"] as ushort?);
                    var workOffline = obj["WorkOffline"] as bool?;
                    var extended = obj["ExtendedPrinterStatus"] as ushort?;
                    bool? offline = workOffline == true || status == "Offline" || extended == 7
                        ? true
                        : workOffline;
                    if (offline == true)
                        status = "Offline";

                    result[name] = new WmiPrinter(status, offline, obj["Network"] as bool?);
                }
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "WMI printer query failed; printer status fields will be null");
        }

        return result;
    }

    // Win32_Printer.PrinterStatus values.
    private static string? MapStatus(ushort? code) => code switch
    {
        1 => "Other",
        2 => "Unknown",
        3 => "Idle",
        4 => "Printing",
        5 => "Warmup",
        6 => "Stopped",
        7 => "Offline",
        null => null,
        _ => "Unknown",
    };
}
