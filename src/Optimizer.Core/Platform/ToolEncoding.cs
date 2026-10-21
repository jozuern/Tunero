using System.Runtime.InteropServices;
using System.Text;

namespace Optimizer.Core.Platform;

/// <summary>The encodings Windows' console tools write to a redirected pipe.</summary>
public static class ToolEncoding
{
    /// <summary>sfc.exe writes UTF-16 to a redirected pipe.</summary>
    public static readonly Encoding Utf16 = Encoding.Unicode;

    /// <summary>
    /// The OEM code page of the system (850 on German Windows), which console tools such as dism, ipconfig, chkdsk,
    /// netsh, net, reagentc and Windows PowerShell write to a redirected pipe. Read as UTF-8, umlauts come out as
    /// replacement characters. UTF-8 when the code page is not available.
    /// </summary>
    public static readonly Encoding Oem = OemEncoding();

    private static Encoding OemEncoding()
    {
        try
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            return Encoding.GetEncoding((int)GetOEMCP());
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException)
        {
            return Encoding.UTF8;
        }
    }

    [DllImport("kernel32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern uint GetOEMCP();
}
