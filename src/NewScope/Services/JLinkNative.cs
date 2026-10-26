using System.Runtime.InteropServices;

namespace NewScope.Services;

internal static class JLinkNative
{
    private const string DllName = "JLink_x64.dll";
    private static readonly string[] DllCandidates =
    [
        @"D:\Program Files (x86)\SEGGER\JLink\JLink_x64.dll",
        @"C:\Program Files\SEGGER\JLink\JLink_x64.dll",
        @"C:\Program Files (x86)\SEGGER\JLink\JLink_x64.dll"
    ];

    private static readonly LogCallback LogHandler = OnLog;
    private static readonly ErrorCallback ErrorHandler = OnError;

    static JLinkNative()
    {
        try
        {
            NativeLibrary.SetDllImportResolver(typeof(JLinkNative).Assembly, Resolve);
        }
        catch (InvalidOperationException)
        {
            // A resolver was already registered for this assembly.
        }
    }

    public static string DllPath =>
        DllCandidates.FirstOrDefault(File.Exists) ?? DllName;

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void LogCallback(IntPtr message);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void ErrorCallback(IntPtr message);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    private static extern IntPtr JLINKARM_OpenEx(IntPtr logHandler, IntPtr errorHandler);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    private static extern void JLINKARM_Close();

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    private static extern int JLINKARM_TIF_Select(int interfaceType);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    private static extern void JLINKARM_SetSpeed(int speedKHz);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    private static extern ushort JLINKARM_GetSpeed();

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    private static extern int JLINKARM_Connect();

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    private static extern void JLINKARM_Go();

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    private static extern int JLINKARM_IsConnected();

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    private static extern int JLINKARM_GetDLLVersion();

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    private static extern int JLINKARM_GetSN();

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    private static extern int JLINKARM_HasError();

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    private static extern void JLINKARM_ClrError();

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    private static extern int JLINKARM_ReadMemEx(uint address, uint numBytes, [In, Out] byte[] buffer, uint access);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true, CharSet = CharSet.Ansi)]
    private static extern int JLINKARM_ExecCommand(string command, byte[] errorBuffer, int errorBufferSize);

    public static bool TryOpen(out string? error)
    {
        var pointer = JLINKARM_OpenEx(
            Marshal.GetFunctionPointerForDelegate(LogHandler),
            Marshal.GetFunctionPointerForDelegate(ErrorHandler));
        if (pointer != IntPtr.Zero)
        {
            error = Marshal.PtrToStringAnsi(pointer) ?? "J-Link open failed.";
            return false;
        }

        error = null;
        return true;
    }

    public static void Close()
    {
        try
        {
            JLINKARM_Close();
        }
        catch
        {
            // The DLL can throw through the native boundary while a probe is
            // disappearing. The managed session still marks itself closed.
        }
    }

    public static int SelectSwd() => JLINKARM_TIF_Select(1);

    public static void SetSpeed(int speedKHz) => JLINKARM_SetSpeed(speedKHz);

    public static int GetSpeed() => JLINKARM_GetSpeed();

    public static int Connect() => JLINKARM_Connect();

    public static void Go() => JLINKARM_Go();

    public static bool IsConnected() => JLINKARM_IsConnected() != 0;

    public static int GetDllVersion() => JLINKARM_GetDLLVersion();

    public static int GetSerialNumber() => JLINKARM_GetSN();

    public static int GetError() => JLINKARM_HasError();

    public static void ClearError() => JLINKARM_ClrError();

    public static int ReadMemory(uint address, byte[] buffer, int length)
    {
        if (length <= 0 || length > buffer.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(length));
        }

        return JLINKARM_ReadMemEx(address, (uint)length, buffer, 1);
    }

    public static int ExecCommand(string command, out string error)
    {
        var buffer = new byte[512];
        var result = JLINKARM_ExecCommand(command, buffer, buffer.Length);
        var terminator = Array.IndexOf(buffer, (byte)0);
        error = System.Text.Encoding.ASCII.GetString(buffer, 0, terminator < 0 ? buffer.Length : terminator).Trim();
        return result;
    }

    public static string FormatVersion(int version)
    {
        if (version <= 0)
        {
            return "unknown";
        }

        var major = version / 10000;
        var minor = version / 100 % 100;
        var patch = version % 100;
        return $"{major}.{minor:00}.{patch}";
    }

    private static IntPtr Resolve(string libraryName, System.Reflection.Assembly assembly, DllImportSearchPath? searchPath)
    {
        if (!string.Equals(libraryName, DllName, StringComparison.OrdinalIgnoreCase))
        {
            return IntPtr.Zero;
        }

        foreach (var candidate in DllCandidates)
        {
            if (!File.Exists(candidate))
            {
                continue;
            }

            try
            {
                return NativeLibrary.Load(candidate);
            }
            catch
            {
                // Try the next location, then fall back to the default loader.
            }
        }

        return IntPtr.Zero;
    }

    private static void OnLog(IntPtr message)
    {
        // Keep native logging silent. The UI surfaces actionable errors only.
    }

    private static void OnError(IntPtr message)
    {
    }
}
