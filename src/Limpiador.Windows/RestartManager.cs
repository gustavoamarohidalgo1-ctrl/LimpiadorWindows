using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;

namespace Limpiador.Windows;

public sealed record LockingProcess(int ProcessId, string ApplicationName, string ServiceName, bool Restartable);

[SupportedOSPlatform("windows10.0.17763")]
public static class RestartManager
{
    public static IReadOnlyList<LockingProcess> GetLockingProcesses(IEnumerable<string> paths, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        WindowsFileSystem.EnsureWindows();
        var files = paths.Select(path =>
        { cancellationToken.ThrowIfCancellationRequested(); return WindowsFileSystem.NormalizeLocalPath(path); }).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        cancellationToken.ThrowIfCancellationRequested();
        if (files.Length == 0) return [];
        Check(RmStartSession(out var session, 0, new StringBuilder(33)), "iniciar Restart Manager");
        try
        {
            foreach (var chunk in files.Chunk(512))
            {
                cancellationToken.ThrowIfCancellationRequested();
                Check(RmRegisterResources(session, (uint)chunk.Length, chunk, 0, 0, 0, null), "registrar los archivos abiertos");
            }
            for (var attempt = 0; attempt < 5; attempt++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                uint needed = 0, count = 0, reasons = 0;
                var status = RmGetList(session, out needed, ref count, null, ref reasons);
                cancellationToken.ThrowIfCancellationRequested();
                if (status == 0 && needed == 0) { RequireNoRebootReason(reasons); return []; }
                if (status != 234) Check(status, "consultar los procesos");
                if (needed > 65536) throw new IOException("La lista de procesos es excesiva; no se modificará ningún archivo.");
                var processes = new RmProcessInfo[needed]; count = needed;
                status = RmGetList(session, out needed, ref count, processes, ref reasons);
                cancellationToken.ThrowIfCancellationRequested();
                if (status == 234) continue;
                Check(status, "comprobar los procesos");
                RequireNoRebootReason(reasons);
                if (count > processes.Length) throw new IOException("Restart Manager devolvió una lista incompleta; se cancela por seguridad.");
                return processes.Take((int)count).Select(p => new LockingProcess(p.Process.ProcessId, p.AppName, p.ServiceShortName, p.Restartable)).ToArray();
            }
            throw new IOException("La lista de procesos cambia continuamente; se cancela por seguridad.");
        }
        finally { RmEndSession(session); }
    }
    public static void RequireUnlocked(IEnumerable<string> paths, CancellationToken cancellationToken = default)
    {
        var locking = GetLockingProcesses(paths, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        if (locking.Count != 0) throw new IOException("Archivos en uso por: " + string.Join(", ", locking.Select(p => $"{p.ApplicationName} ({p.ProcessId})")));
    }
    private static void Check(int result, string operation)
    {
        if (result != 0) throw new IOException($"No se pudo {operation}; la operación se cancela por seguridad.", new Win32Exception(result));
    }
    private static void RequireNoRebootReason(uint reasons)
    {
        if (reasons != 0) throw new IOException($"Restart Manager detectó recursos que requieren reiniciar o no puede liberar (0x{reasons:X}); no se modificará ningún archivo.");
    }
    [StructLayout(LayoutKind.Sequential)] private struct RmUniqueProcess { internal int ProcessId; internal System.Runtime.InteropServices.ComTypes.FILETIME StartTime; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] private struct RmProcessInfo
    {
        internal RmUniqueProcess Process;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] internal string AppName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] internal string ServiceShortName;
        internal uint ApplicationType, AppStatus, TerminalSessionId;
        [MarshalAs(UnmanagedType.Bool)] internal bool Restartable;
    }
    [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode)] private static extern int RmStartSession(out uint handle, uint flags, StringBuilder key);
    [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode)] private static extern int RmRegisterResources(uint handle, uint count, [In, MarshalAs(UnmanagedType.LPArray, ArraySubType = UnmanagedType.LPWStr)] string[] files, uint applications, nint processes, uint services, string[]? serviceNames);
    [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode)] private static extern int RmGetList(uint handle, out uint needed, ref uint count, [In, Out] RmProcessInfo[]? processes, ref uint reasons);
    [DllImport("rstrtmgr.dll")] private static extern int RmEndSession(uint handle);
}
