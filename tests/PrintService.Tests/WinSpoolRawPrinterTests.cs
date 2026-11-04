using System.Text;
using EpicErp.PrintService.Printing;
using Microsoft.Extensions.Logging.Abstractions;

namespace PrintService.Tests;

/// <summary>Uses the real Windows spooler. Jobs are redirected to a file, so nothing is physically printed.</summary>
public class WinSpoolRawPrinterTests
{
    private static readonly WinSpoolRawPrinter Printer = new(NullLogger<WinSpoolRawPrinter>.Instance);

    [Fact]
    public async Task Unknown_printer_is_reported_as_not_installed()
    {
        var ex = await Assert.ThrowsAsync<PrinterUnavailableException>(() =>
            Printer.PrintAsync(new RawPrintRequest("^L\r\nE\r\n"u8.ToArray(), "No Such Printer 9f3a", "x"), CancellationToken.None));
        Assert.Contains("not installed", ex.Message);
    }

    [Fact]
    public async Task Raw_job_reaches_the_spooler_byte_for_byte()
    {
        // Needs a v3 (GDI) driver queue: v4 drivers run an XPS filter pipeline that drops RAW data.
        // Only queues on a virtual port are used, so a physical printer is never involved, e.g.:
        //   Add-Printer -Name 'epic_raw_test' -DriverName 'Generic / Text Only' -PortName 'NUL:'
        var printerName = FindVirtualV3Printer();
        if (printerName is null)
            return; // no suitable printer on this machine; nothing to check

        var data = Encoding.ASCII.GetBytes("^Q25,3\r\n^W38\r\n^L\r\nAB,20,100,1,1,0,0E,S2610310001\r\nE\r\n^L\r\nE\r\n  ")
            .Concat(new byte[] { 0xEF, 0xBB, 0xBF, 0x00, 0xFF, (byte)'\n' }).ToArray();
        var output = Path.Combine(Path.GetTempPath(), $"epic-raw-test-{Guid.NewGuid():N}.prn");
        try
        {
            var result = await Printer.PrintAsync(
                new RawPrintRequest(data, printerName, "Epic raw test") { PrintToFilePath = output },
                CancellationToken.None);

            Assert.Equal(data.Length, result.Bytes);

            // The spooler writes the file asynchronously after the job is handed over.
            var deadline = DateTime.UtcNow.AddSeconds(30);
            while (DateTime.UtcNow < deadline && (!File.Exists(output) || new FileInfo(output).Length < data.Length || IsLocked(output)))
                await Task.Delay(250);

            Assert.True(File.Exists(output), "Spooled file was not produced");
            Assert.Equal(data, await File.ReadAllBytesAsync(output));
        }
        finally
        {
            if (File.Exists(output))
                File.Delete(output);
        }
    }

    private static readonly string[] VirtualPorts = ["NUL:", "FILE:", "PORTPROMPT:"];

    private static string? FindVirtualV3Printer()
    {
        using var searcher = new System.Management.ManagementObjectSearcher("SELECT Name, PortName, DriverName FROM Win32_Printer");
        using var driverSearcher = new System.Management.ManagementObjectSearcher("SELECT Name FROM Win32_PrinterDriver");
        // Win32_PrinterDriver.Name is "<driver>,<version>,<environment>"; version 3 = GDI driver.
        var v3Drivers = driverSearcher.Get().Cast<System.Management.ManagementBaseObject>()
            .Select(d => ((string)d["Name"]).Split(','))
            .Where(parts => parts.Length >= 2 && parts[1] == "3")
            .Select(parts => parts[0])
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        return searcher.Get().Cast<System.Management.ManagementBaseObject>()
            .Where(p => VirtualPorts.Contains(p["PortName"] as string, StringComparer.OrdinalIgnoreCase)
                        && v3Drivers.Contains(p["DriverName"] as string ?? ""))
            .Select(p => (string)p["Name"])
            .FirstOrDefault();
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
