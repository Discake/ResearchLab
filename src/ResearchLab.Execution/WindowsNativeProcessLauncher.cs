using System.Collections;
using System.ComponentModel;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using Microsoft.Win32.SafeHandles;
using static ResearchLab.Execution.WindowsNativeMethods;

namespace ResearchLab.Execution;

// Deliberately separate from ProcessRunner until group completion is implemented (M3.7c).
internal sealed class WindowsNativeProcessLauncher(Action<KernelHandle>? inspectPreparedJob = null)
{
    // Inputs come from trusted run preparation. This layer never searches PATH or invokes a shell.
    internal WindowsNativeProcess Launch(string executable, IReadOnlyList<string> arguments,
        string workingDirectory, string parametersJson, string outputDirectory)
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10))
            throw new PlatformNotSupportedException("Native job-list launch requires Windows 10 or later.");
        RequireAbsolute(executable);
        RequireAbsolute(workingDirectory);
        RequireAbsolute(outputDirectory);
        if (!string.Equals(Path.GetExtension(executable), ".exe", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("A native .exe is required.", nameof(executable));
        using var environment = new NativeText(BuildEnvironment(parametersJson, outputDirectory));
        var job = CreateJobObjectW(0, null); // NULL security attributes: never inheritable.
        WindowsNativeProcess? owner = null;
        OutputPipe? stdout = null;
        OutputPipe? stderr = null;
        try
        {
            Check(!job.IsInvalid);
            ConfigureJob(job);
            inspectPreparedJob?.Invoke(job); // Narrow pre-start fault/inspection seam, never a containment fallback.
            var command = BuildCommandLine(executable, arguments);
            stdout = new OutputPipe();
            stderr = new OutputPipe();
            using var stdin = new AnonymousPipeServerStream(PipeDirection.Out, HandleInheritability.Inheritable);
            using var attributes = new NativeAttributes(job.DangerousGetHandle(), stdin.ClientSafePipeHandle.DangerousGetHandle(),
                stdout.Child.SafePipeHandle.DangerousGetHandle(), stderr.Child.SafePipeHandle.DangerousGetHandle());
            var startup = new StartupInfoEx
            {
                Startup = new StartupInfo
                {
                    Size = (uint)Marshal.SizeOf<StartupInfoEx>(),
                    Flags = 0x100, // STARTF_USESTDHANDLES
                    Stdin = stdin.ClientSafePipeHandle.DangerousGetHandle(),
                    Stdout = stdout.Child.SafePipeHandle.DangerousGetHandle(),
                    Stderr = stderr.Child.SafePipeHandle.DangerousGetHandle()
                },
                Attributes = attributes.DangerousGetHandle()
            };
            // Allocate the owner and empty process/thread SafeHandles before the process exists.
            owner = new WindowsNativeProcess(job, stdout.Parent, stderr.Parent);
            Check(CreateProcessW(executable, command, 0, 0, true,
                0x00080000 | 0x00000400 | 0x08000000, // EXTENDED_STARTUPINFO_PRESENT | UNICODE_ENVIRONMENT | NO_WINDOW
                environment.DangerousGetHandle(), workingDirectory, ref startup, out var info));
            // No fallible setup, membership query, callback, or managed allocation after success.
            owner.Adopt(info);
            return owner;
        }
        catch
        {
            owner?.Dispose();
            stdout?.Parent.Dispose();
            stderr?.Parent.Dispose();
            job.Dispose();
            throw;
        }
        finally
        {
            stdout?.Child.Dispose();
            stderr?.Child.Dispose();
            // Raw attribute pointers remain valid through CreateProcess and list destruction.
            GC.KeepAlive(job);
            GC.KeepAlive(stdout);
            GC.KeepAlive(stderr);
        }
    }

    internal static void ConfigureJob(KernelHandle job)
    {
        var limits = new ExtendedLimits { Basic = new BasicLimits { Flags = 0x2000 } }; // KILL_ON_JOB_CLOSE only; no breakaway.
        Check(SetInformationJobObject(job, 9, ref limits, (uint)Marshal.SizeOf<ExtendedLimits>()));
    }

    // CRT/.NET argument grammar; arbitrary executables can use a different parser.
    internal static StringBuilder BuildCommandLine(string executable, IReadOnlyList<string> arguments)
    {
        var result = new StringBuilder();
        AppendQuoted(result, executable);
        foreach (var argument in arguments)
        {
            result.Append(' ');
            AppendQuoted(result, argument);
        }
        if (result.Length >= 32767)
            throw new ArgumentException("Windows command line exceeds 32767 characters including NUL.");
        return result;
    }

    private static void AppendQuoted(StringBuilder target, string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (value.Contains('\0'))
            throw new ArgumentException("Arguments cannot contain NUL.");
        target.Append('"');
        var slashes = 0;
        foreach (var character in value)
        {
            if (character == '\\') { slashes++; continue; }
            target.Append('\\', character == '"' ? slashes * 2 + 1 : slashes);
            target.Append(character);
            slashes = 0;
        }
        target.Append('\\', slashes * 2);
        target.Append('"');
    }

    private static string BuildEnvironment(string parameters, string output)
    {
        if (parameters.Contains('\0') || output.Contains('\0'))
            throw new ArgumentException("Environment values cannot contain NUL.");
        var values = new SortedDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (DictionaryEntry entry in Environment.GetEnvironmentVariables())
            values[(string)entry.Key] = (string)entry.Value!;
        values["RESEARCHLAB_PARAMETERS_JSON"] = parameters;
        values["RESEARCHLAB_OUTPUT_DIR"] = output;
        return string.Join('\0', values.Select(pair => $"{pair.Key}={pair.Value}")) + "\0\0";
    }

    private static void RequireAbsolute(string path)
    {
        if (!Path.IsPathFullyQualified(path) || path.Contains('\0'))
            throw new ArgumentException("A validated absolute path is required.");
    }

    internal static void Check(bool success)
    {
        if (!success) throw new Win32Exception(Marshal.GetLastPInvokeError());
    }

    // Parent read handles are noninheritable and overlapped, permitting cancellable async I/O.
    private sealed class OutputPipe
    {
        internal NamedPipeServerStream Parent { get; }
        internal NamedPipeClientStream Child { get; }

        internal OutputPipe()
        {
            var name = "ResearchLab-" + Guid.NewGuid().ToString("N");
            Parent = new NamedPipeServerStream(name, PipeDirection.In, 1, PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            try
            {
                Child = new NamedPipeClientStream(".", name, PipeDirection.Out, PipeOptions.None,
                    TokenImpersonationLevel.Anonymous, HandleInheritability.Inheritable);
                try
                {
                    Child.Connect(5000);
                    Parent.WaitForConnection();
                }
                catch { Child.Dispose(); throw; }
            }
            catch { Parent.Dispose(); throw; }
        }
    }

    private sealed class NativeText : SafeHandleZeroOrMinusOneIsInvalid
    {
        internal NativeText(string value) : base(true) => SetHandle(Marshal.StringToHGlobalUni(value));
        protected override bool ReleaseHandle() { Marshal.FreeHGlobal(handle); return true; }
    }

    private sealed class NativeAttributes : SafeHandleZeroOrMinusOneIsInvalid
    {
        private nint values;
        private bool initialized;

        internal NativeAttributes(nint job, nint stdin, nint stdout, nint stderr) : base(true)
        {
            nuint size = 0;
            InitializeProcThreadAttributeList(0, 2, 0, ref size); // Expected ERROR_INSUFFICIENT_BUFFER.
            if (size == 0) throw new Win32Exception(Marshal.GetLastPInvokeError());
            SetHandle(Marshal.AllocHGlobal(checked((nint)size)));
            try
            {
                Check(InitializeProcThreadAttributeList(handle, 2, 0, ref size));
                initialized = true;
                values = Marshal.AllocHGlobal(4 * nint.Size);
                Marshal.WriteIntPtr(values, job);
                Marshal.WriteIntPtr(values, nint.Size, stdin);
                Marshal.WriteIntPtr(values, 2 * nint.Size, stdout);
                Marshal.WriteIntPtr(values, 3 * nint.Size, stderr);
                Check(UpdateProcThreadAttribute(handle, 0, 0x0002000D, values, (nuint)nint.Size, 0, 0)); // JOB_LIST
                Check(UpdateProcThreadAttribute(handle, 0, 0x00020002, values + nint.Size, (nuint)(3 * nint.Size), 0, 0)); // HANDLE_LIST
            }
            catch { Dispose(); throw; }
        }

        protected override bool ReleaseHandle()
        {
            if (initialized) DeleteProcThreadAttributeList(handle);
            Marshal.FreeHGlobal(values);
            Marshal.FreeHGlobal(handle);
            return true;
        }
    }
}

// One caller owns lifetime. Dispose must not race wait/read operations; settle canceled reads first.
// Closing the Job requests group termination, but does NOT confirm group completion.
internal sealed class WindowsNativeProcess(KernelHandle job, Stream stdout, Stream stderr) : IDisposable
{
    internal KernelHandle Job { get; } = job;
    internal KernelHandle Process { get; } = new();
    private readonly KernelHandle thread = new();
    internal int ProcessId { get; private set; }
    internal Stream StandardOutput { get; } = stdout;
    internal Stream StandardError { get; } = stderr;

    internal void Adopt(ProcessInformation information)
    {
        Process.Adopt(information.Process);
        thread.Adopt(information.Thread);
        ProcessId = unchecked((int)information.ProcessId);
        thread.Dispose();
    }

    internal async Task<int> WaitForRootExitAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            var status = WaitForSingleObject(Process, 0);
            if (status == 0) break;
            WindowsNativeProcessLauncher.Check(status == 258); // WAIT_TIMEOUT; every other value is an error.
            await Task.Delay(10, cancellationToken);
        }
        WindowsNativeProcessLauncher.Check(GetExitCodeProcess(Process, out var code));
        return unchecked((int)code); // A signaled handle disambiguates exit code 259 from STILL_ACTIVE.
    }

    public void Dispose()
    {
        Job.Dispose();
        thread.Dispose();
        Process.Dispose();
        StandardOutput.Dispose();
        StandardError.Dispose();
    }
}
