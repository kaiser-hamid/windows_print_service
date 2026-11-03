using System.Runtime.InteropServices;

namespace EpicErp.PrintService.Printing;

/// <summary>
/// Minimal P/Invoke surface over the native pdfium.dll shipped by the bblanchon.PDFium.Win32 package.
/// FPDF_RenderPage (rendering straight to a GDI device context) only exists in Windows builds of PDFium.
/// </summary>
internal static class PdfiumNative
{
    private const string Lib = "pdfium";

    public const int FPDF_ANNOT = 0x01;
    public const int FPDF_PRINTING = 0x800;

    public const uint FPDF_ERR_SUCCESS = 0;
    public const uint FPDF_ERR_FORMAT = 3;
    public const uint FPDF_ERR_PASSWORD = 4;
    public const uint FPDF_ERR_SECURITY = 5;

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern void FPDF_InitLibrary();

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi, BestFitMapping = false)]
    public static extern IntPtr FPDF_LoadMemDocument64(IntPtr dataBuffer, nuint size, string? password);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern void FPDF_CloseDocument(IntPtr document);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern uint FPDF_GetLastError();

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern int FPDF_GetPageCount(IntPtr document);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr FPDF_LoadPage(IntPtr document, int pageIndex);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern void FPDF_ClosePage(IntPtr page);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern float FPDF_GetPageWidthF(IntPtr page);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern float FPDF_GetPageHeightF(IntPtr page);

    /// <param name="rotate">0 = normal, 1 = 90° clockwise, 2 = 180°, 3 = 90° counter-clockwise.</param>
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern void FPDF_RenderPage(IntPtr dc, IntPtr page, int startX, int startY, int sizeX, int sizeY, int rotate, int flags);
}

internal static class Gdi32
{
    public const int HORZRES = 8;
    public const int VERTRES = 10;

    [DllImport("gdi32.dll")]
    public static extern int GetDeviceCaps(IntPtr hdc, int index);
}
