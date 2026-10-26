using System.Runtime.InteropServices;

namespace NewScope.Services;

internal static class Win32Timer
{
    [DllImport("winmm.dll", EntryPoint = "timeBeginPeriod")]
    private static extern uint TimeBeginPeriod(uint period);

    [DllImport("winmm.dll", EntryPoint = "timeEndPeriod")]
    private static extern uint TimeEndPeriod(uint period);

    private static int _refCount;

    public static void Begin()
    {
        if (Interlocked.Increment(ref _refCount) == 1)
        {
            TimeBeginPeriod(1);
        }
    }

    public static void End()
    {
        if (Interlocked.Decrement(ref _refCount) == 0)
        {
            TimeEndPeriod(1);
        }
    }
}
