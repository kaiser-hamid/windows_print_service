using System.ComponentModel;
using System.Runtime.InteropServices;

namespace EpicErp.PrintService.Printing;

/// <summary>
/// Sends printer-ready data (e.g. GoDEX EZPL) to a printer as a RAW spooler job, byte for byte.
/// Nothing is rendered: the driver passes the bytes straight to the device.
/// </summary>
public sealed class WinSpoolRawPrinter(ILogger<WinSpoolRawPrinter> logger) : IRawPrinter
{
    public Task<RawPrintResult> PrintAsync(RawPrintRequest request, CancellationToken cancellationToken) =>
        PrintQueue.RunAsync(() => PrintCore(request), cancellationToken);

    private RawPrintResult PrintCore(RawPrintRequest request)
    {
        var printerName = request.PrinterName;
        var data = request.Data;

        if (!WinSpool.OpenPrinterW(printerName, out var printer, IntPtr.Zero))
        {
            var error = Marshal.GetLastPInvokeError();
            throw error == WinSpool.ERROR_INVALID_PRINTER_NAME
                ? new PrinterUnavailableException($"Printer '{printerName}' is not installed on this PC.")
                : new PrinterUnavailableException($"Printer '{printerName}' could not be opened: {Describe(error)}");
        }

        try
        {
            ThrowIfOffline(printer, printerName);

            var docInfo = new WinSpool.DOC_INFO_1 { pDocName = request.JobName, pOutputFile = request.PrintToFilePath, pDatatype = "RAW" };
            if (WinSpool.StartDocPrinterW(printer, 1, ref docInfo) == 0)
                throw Rejected(printerName, "StartDocPrinter");

            var completed = false;
            try
            {
                if (!WinSpool.StartPagePrinter(printer))
                    throw Rejected(printerName, "StartPagePrinter");

                if (!WinSpool.WritePrinter(printer, data, data.Length, out var written))
                    throw Rejected(printerName, "WritePrinter");
                if (written != data.Length)
                    throw new PrinterUnavailableException(
                        $"Printer '{printerName}' accepted only {written} of {data.Length} bytes; the job was cancelled.");

                if (!WinSpool.EndPagePrinter(printer))
                    throw Rejected(printerName, "EndPagePrinter");
                if (!WinSpool.EndDocPrinter(printer))
                    throw Rejected(printerName, "EndDocPrinter");

                completed = true;
            }
            finally
            {
                // Never let a partial label job reach the printer.
                if (!completed)
                    WinSpool.AbortPrinter(printer);
            }
        }
        finally
        {
            WinSpool.ClosePrinter(printer);
        }

        logger.LogInformation("Spooled raw job '{JobName}' ({Bytes} bytes) to printer '{Printer}'",
            request.JobName, data.Length, printerName);

        return new RawPrintResult(printerName, request.JobName, data.Length);
    }

    /// <summary>
    /// A RAW job sent to an offline printer would just sit in the queue, so refuse it up front.
    /// If the status can't be read, the job is sent anyway.
    /// </summary>
    private void ThrowIfOffline(IntPtr printer, string printerName)
    {
        WinSpool.GetPrinterW(printer, 2, IntPtr.Zero, 0, out var needed);
        if (needed <= 0)
        {
            logger.LogWarning("Could not read the status of printer '{Printer}': {Error}",
                printerName, Describe(Marshal.GetLastPInvokeError()));
            return;
        }

        var buffer = Marshal.AllocHGlobal(needed);
        try
        {
            if (!WinSpool.GetPrinterW(printer, 2, buffer, needed, out _))
            {
                logger.LogWarning("Could not read the status of printer '{Printer}': {Error}",
                    printerName, Describe(Marshal.GetLastPInvokeError()));
                return;
            }

            var info = Marshal.PtrToStructure<WinSpool.PRINTER_INFO_2>(buffer);
            if ((info.Attributes & WinSpool.PRINTER_ATTRIBUTE_WORK_OFFLINE) != 0
                || (info.Status & WinSpool.PRINTER_STATUS_OFFLINE) != 0)
                throw new PrinterUnavailableException($"Printer '{printerName}' is offline.");
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static PrinterUnavailableException Rejected(string printerName, string call)
    {
        var error = Marshal.GetLastPInvokeError();
        return new PrinterUnavailableException(
            $"Printer '{printerName}' rejected the job ({call} failed: {Describe(error)}).",
            new Win32Exception(error));
    }

    private static string Describe(int error) => $"{new Win32Exception(error).Message} (error {error})";

    private static class WinSpool
    {
        private const string Lib = "winspool.drv";

        public const int ERROR_INVALID_PRINTER_NAME = 1801;
        public const uint PRINTER_ATTRIBUTE_WORK_OFFLINE = 0x400;
        public const uint PRINTER_STATUS_OFFLINE = 0x80;

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        public struct DOC_INFO_1
        {
            public string pDocName;
            public string? pOutputFile;
            public string pDatatype;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct PRINTER_INFO_2
        {
            public IntPtr pServerName;
            public IntPtr pPrinterName;
            public IntPtr pShareName;
            public IntPtr pPortName;
            public IntPtr pDriverName;
            public IntPtr pComment;
            public IntPtr pLocation;
            public IntPtr pDevMode;
            public IntPtr pSepFile;
            public IntPtr pPrintProcessor;
            public IntPtr pDatatype;
            public IntPtr pParameters;
            public IntPtr pSecurityDescriptor;
            public uint Attributes;
            public uint Priority;
            public uint DefaultPriority;
            public uint StartTime;
            public uint UntilTime;
            public uint Status;
            public uint cJobs;
            public uint AveragePPM;
        }

        [DllImport(Lib, SetLastError = true, CharSet = CharSet.Unicode, ExactSpelling = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool OpenPrinterW(string pPrinterName, out IntPtr phPrinter, IntPtr pDefault);

        [DllImport(Lib, SetLastError = true, ExactSpelling = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool ClosePrinter(IntPtr hPrinter);

        [DllImport(Lib, SetLastError = true, CharSet = CharSet.Unicode, ExactSpelling = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool GetPrinterW(IntPtr hPrinter, int level, IntPtr pPrinter, int cbBuf, out int pcbNeeded);

        /// <returns>The print job id, or 0 on failure.</returns>
        [DllImport(Lib, SetLastError = true, CharSet = CharSet.Unicode, ExactSpelling = true)]
        public static extern int StartDocPrinterW(IntPtr hPrinter, int level, [In] ref DOC_INFO_1 pDocInfo);

        [DllImport(Lib, SetLastError = true, ExactSpelling = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool StartPagePrinter(IntPtr hPrinter);

        [DllImport(Lib, SetLastError = true, ExactSpelling = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool WritePrinter(IntPtr hPrinter, byte[] pBuf, int cbBuf, out int pcWritten);

        [DllImport(Lib, SetLastError = true, ExactSpelling = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool EndPagePrinter(IntPtr hPrinter);

        [DllImport(Lib, SetLastError = true, ExactSpelling = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool EndDocPrinter(IntPtr hPrinter);

        [DllImport(Lib, SetLastError = true, ExactSpelling = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool AbortPrinter(IntPtr hPrinter);
    }
}
