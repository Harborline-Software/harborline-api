using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Harborline.Api.LocalNodeHost.Tests;

internal static partial class WindowsErrorModeModuleInitializer
{
    [ModuleInitializer]
    internal static void Initialize()
    {
        if (OperatingSystem.IsWindows())
            _ = SetErrorMode(0x0001 | 0x0002 | 0x8000);
    }

    [LibraryImport("kernel32.dll")]
    private static partial uint SetErrorMode(uint errorMode);
}
