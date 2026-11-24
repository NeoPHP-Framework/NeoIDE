using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace NeoIDE.Controls.Terminal;

/// <summary>
/// A process attached to a Windows pseudo console (ConPTY, Windows 10 1809+): the shell believes it runs in a real
/// console (colours, cursor moves, prompts, line editing, full-screen programs) and we read / write the VT stream.
/// </summary>
public sealed class PseudoConsole : IDisposable
{
    private IntPtr _hpc;
    private SafeFileHandle? _inputWrite, _outputRead;
    private FileStream? _input;
    private IntPtr _process, _thread;
    private readonly Thread _reader;
    private bool _disposed;

    public int ProcessId { get; }
    public event Action<string>? Output;
    public event Action<int>? Exited;

    public static bool IsSupported => Environment.OSVersion.Version.Build >= 17763;

    public PseudoConsole(string commandLine, string workingDirectory, short cols, short rows, IDictionary<string, string>? environment = null)
    {
        if (!CreatePipe(out var inputRead, out _inputWrite, IntPtr.Zero, 0)) throw new Win32Exception();
        if (!CreatePipe(out _outputRead, out var outputWrite, IntPtr.Zero, 0)) throw new Win32Exception();
        var hr = CreatePseudoConsole(new COORD { X = cols, Y = rows }, inputRead, outputWrite, 0, out _hpc);
        if (hr != 0) throw new Win32Exception(hr);
        // the pseudo console owns copies of these ends now
        inputRead.Dispose();
        outputWrite.Dispose();

        var si = new STARTUPINFOEX();
        si.StartupInfo.cb = Marshal.SizeOf<STARTUPINFOEX>();
        // no standard handles: a console parent (or redirected handles) must not be inherited instead of the pseudo console
        si.StartupInfo.dwFlags = STARTF_USESTDHANDLES;
        var size = IntPtr.Zero;
        InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref size);
        si.lpAttributeList = Marshal.AllocHGlobal(size);
        if (!InitializeProcThreadAttributeList(si.lpAttributeList, 1, 0, ref size)) throw new Win32Exception();
        if (!UpdateProcThreadAttribute(si.lpAttributeList, 0, (IntPtr)PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE, _hpc, (IntPtr)IntPtr.Size, IntPtr.Zero, IntPtr.Zero))
            throw new Win32Exception();

        // environment block: the IDE's environment + overrides (UTF-16, "k=v\0...\0\0")
        var env = Environment.GetEnvironmentVariables().Cast<System.Collections.DictionaryEntry>()
            .ToDictionary(e => (string)e.Key, e => (string?)e.Value ?? "", StringComparer.OrdinalIgnoreCase);
        if (environment != null) foreach (var (k, v) in environment) env[k] = v;
        var block = new StringBuilder();
        foreach (var (k, v) in env.OrderBy(e => e.Key, StringComparer.OrdinalIgnoreCase)) block.Append(k).Append('=').Append(v).Append('\0');
        block.Append('\0');
        var envPtr = Marshal.StringToHGlobalUni(block.ToString());

        try
        {
            var pi = new PROCESS_INFORMATION();
            if (!CreateProcess(null, new StringBuilder(commandLine), IntPtr.Zero, IntPtr.Zero, false,
                    EXTENDED_STARTUPINFO_PRESENT | CREATE_UNICODE_ENVIRONMENT, envPtr, workingDirectory, ref si, out pi))
                throw new Win32Exception(Marshal.GetLastWin32Error(), $"Cannot start {commandLine}");
            _process = pi.hProcess;
            _thread = pi.hThread;
            ProcessId = pi.dwProcessId;
        }
        finally
        {
            Marshal.FreeHGlobal(envPtr);
            DeleteProcThreadAttributeList(si.lpAttributeList);
            Marshal.FreeHGlobal(si.lpAttributeList);
        }

        _input = new FileStream(_inputWrite, FileAccess.Write, 1, false);
        _reader = new Thread(ReadLoop) { IsBackground = true, Name = "ConPTY reader" };
        _reader.Start();
        var waiter = new Thread(() =>
        {
            WaitForSingleObject(_process, INFINITE);
            GetExitCodeProcess(_process, out var code);
            // give the reader the last bytes of the output, then close the console so ReadFile returns
            Thread.Sleep(150);
            Exited?.Invoke((int)code);
            ClosePseudo();
        }) { IsBackground = true, Name = "ConPTY waiter" };
        waiter.Start();
    }

    private void ReadLoop()
    {
        var decoder = Encoding.UTF8.GetDecoder();
        var buffer = new byte[16384];
        var chars = new char[16384 + 4];
        try
        {
            using var stream = new FileStream(_outputRead!, FileAccess.Read, 1, false);
            while (true)
            {
                var n = stream.Read(buffer, 0, buffer.Length);
                if (n <= 0) break;
                var c = decoder.GetChars(buffer, 0, n, chars, 0);
                if (c > 0) Output?.Invoke(new string(chars, 0, c));
            }
        }
        catch { }
    }

    public void Write(string text)
    {
        if (_disposed || _input == null) return;
        try
        {
            var bytes = Encoding.UTF8.GetBytes(text);
            _input.Write(bytes, 0, bytes.Length);
            _input.Flush();
        }
        catch { }
    }

    public void Resize(short cols, short rows)
    {
        if (_hpc != IntPtr.Zero && cols > 0 && rows > 0) ResizePseudoConsole(_hpc, new COORD { X = cols, Y = rows });
    }

    private readonly object _closeGate = new();
    private void ClosePseudo()
    {
        lock (_closeGate)
        {
            if (_hpc != IntPtr.Zero) { ClosePseudoConsole(_hpc); _hpc = IntPtr.Zero; }
        }
    }

    public void Kill()
    {
        if (_process == IntPtr.Zero) return;
        // the whole tree: a shell may have started php, composer, a server...
        try { System.Diagnostics.Process.GetProcessById(ProcessId).Kill(true); } catch { }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Kill();
        ClosePseudo();
        try { _input?.Dispose(); } catch { }
        if (_process != IntPtr.Zero) { CloseHandle(_process); _process = IntPtr.Zero; }
        if (_thread != IntPtr.Zero) { CloseHandle(_thread); _thread = IntPtr.Zero; }
    }

    // ------------------------------------------------------------------ native

    private const int PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE = 0x00020016;
    private const uint EXTENDED_STARTUPINFO_PRESENT = 0x00080000;
    private const uint CREATE_UNICODE_ENVIRONMENT = 0x00000400;
    private const uint INFINITE = 0xFFFFFFFF;
    private const int STARTF_USESTDHANDLES = 0x00000100;

    [StructLayout(LayoutKind.Sequential)]
    private struct COORD { public short X; public short Y; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct STARTUPINFO
    {
        public int cb;
        public string? lpReserved, lpDesktop, lpTitle;
        public int dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags;
        public short wShowWindow, cbReserved2;
        public IntPtr lpReserved2, hStdInput, hStdOutput, hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct STARTUPINFOEX { public STARTUPINFO StartupInfo; public IntPtr lpAttributeList; }

    [StructLayout(LayoutKind.Sequential)]
    private struct PROCESS_INFORMATION { public IntPtr hProcess, hThread; public int dwProcessId, dwThreadId; }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern int CreatePseudoConsole(COORD size, SafeFileHandle hInput, SafeFileHandle hOutput, uint dwFlags, out IntPtr phPC);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern int ResizePseudoConsole(IntPtr hPC, COORD size);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern void ClosePseudoConsole(IntPtr hPC);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CreatePipe(out SafeFileHandle hReadPipe, out SafeFileHandle hWritePipe, IntPtr lpPipeAttributes, int nSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool InitializeProcThreadAttributeList(IntPtr lpAttributeList, int dwAttributeCount, int dwFlags, ref IntPtr lpSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool UpdateProcThreadAttribute(IntPtr lpAttributeList, uint dwFlags, IntPtr attribute, IntPtr lpValue, IntPtr cbSize, IntPtr lpPreviousValue, IntPtr lpReturnSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern void DeleteProcThreadAttributeList(IntPtr lpAttributeList);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CreateProcess(string? lpApplicationName, StringBuilder lpCommandLine, IntPtr lpProcessAttributes, IntPtr lpThreadAttributes,
        bool bInheritHandles, uint dwCreationFlags, IntPtr lpEnvironment, string? lpCurrentDirectory, ref STARTUPINFOEX lpStartupInfo, out PROCESS_INFORMATION lpProcessInformation);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint WaitForSingleObject(IntPtr hHandle, uint dwMilliseconds);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetExitCodeProcess(IntPtr hProcess, out uint lpExitCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr hObject);
}
