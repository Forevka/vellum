using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;

namespace Vellum.Interop;

/// <summary>
/// Requests Intel AMX (Advanced Matrix Extensions) tile-data permission for the
/// process before <c>libchromescreenai.so</c> executes any AMX instruction.
///
/// ScreenAI dispatches its OCR matrix kernels to AMX tile instructions (e.g.
/// <c>tilezero %tmm0</c>) on Sapphire-Rapids-class CPUs. Since Linux 5.16 the
/// kernel gates AMX behind a per-process opt-in (the XFD mechanism) because the
/// tile-register XSAVE area is large: a process must call
/// <c>arch_prctl(ARCH_REQ_XCOMP_PERM, XFEATURE_XTILEDATA)</c> BEFORE the first
/// AMX instruction, otherwise it faults with SIGILL (#UD). Chrome's library never
/// makes that request, so on AMX-capable hosts the first OCR crashes the whole
/// process. We request it here, once, when the engine is constructed — the grant
/// is process-wide and inherited by the worker threads ScreenAI spawns during
/// OCR. No-op on non-Linux, non-x64, or hosts/kernels without AMX (the request
/// just fails harmlessly there, and a non-AMX kernel is used).
/// </summary>
internal static class AmxPermission
{
    private const long SYS_arch_prctl = 158;          // x86_64 syscall number
    private const ulong ARCH_REQ_XCOMP_PERM = 0x1023; // arch/x86/include/uapi/asm/prctl.h
    private const ulong XFEATURE_XTILEDATA = 18;       // XSTATE bit index for AMX tile data

    private static bool _requested;
    private static readonly object Sync = new();

    [DllImport("libc", SetLastError = true)]
    private static extern long syscall(long number, ulong arg1, ulong arg2);

    public static void Ensure(ILogger? log)
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux)) return;
        if (RuntimeInformation.ProcessArchitecture != Architecture.X64) return;

        lock (Sync)
        {
            if (_requested) return;
            _requested = true;

            try
            {
                var rc = syscall(SYS_arch_prctl, ARCH_REQ_XCOMP_PERM, XFEATURE_XTILEDATA);
                if (rc == 0)
                {
                    log?.LogDebug("AMX XTILEDATA permission granted for process.");
                }
                else
                {
                    log?.LogDebug(
                        "arch_prctl(ARCH_REQ_XCOMP_PERM, XTILEDATA) returned {Rc} (errno {Errno}); " +
                        "host likely has no AMX — continuing with a non-AMX kernel.",
                        rc, Marshal.GetLastPInvokeError());
                }
            }
            catch (Exception ex)
            {
                log?.LogDebug(ex, "Failed to request AMX permission; continuing without it.");
            }
        }
    }
}
