using System.Diagnostics;
using System.Runtime.InteropServices;
using ResearchLab.Application;
using static ResearchLab.Execution.WindowsNativeMethods;

namespace ResearchLab.Execution;

// Small resource adapter: lifecycle, arbitration, log bounds and persistence remain outside it.
internal interface IRunProcess : IDisposable
{
    int Id { get; }
    Stream Stdout { get; }
    Stream Stderr { get; }
    bool IsComplete { get; }
    int ExitCode { get; }
    void CloseInput();
    void Stop();
}

internal interface IWindowsJobControl
{
    uint ActiveProcesses(KernelHandle job);
    void Terminate(KernelHandle job);
}

internal sealed class WindowsJobControl : IWindowsJobControl
{
    public uint ActiveProcesses(KernelHandle job)
    {
        WindowsNativeProcessLauncher.Check(QueryInformationJobObject(job, 1, out var accounting,
            (uint)Marshal.SizeOf<BasicAccounting>(), 0));
        return accounting.ActiveProcesses;
    }

    public void Terminate(KernelHandle job) => WindowsNativeProcessLauncher.Check(TerminateJobObject(job, 1));
}

internal sealed class WindowsRunProcess(IWindowsJobControl jobs) : IRunProcess
{
    private WindowsNativeProcess native = null!;
    private int? rootExitCode;
    private bool complete;
    internal void Start(PreparedRun launch, string parametersJson, string outputDirectory) =>
        native = new WindowsNativeProcessLauncher().Launch(launch.ExecutablePath,
            launch.ExperimentSnapshot.Task.Arguments, launch.WorkingDirectory, parametersJson, outputDirectory);
    public int Id => native.ProcessId;
    public Stream Stdout => native.StandardOutput;
    public Stream Stderr => native.StandardError;
    public int ExitCode => rootExitCode ?? throw new InvalidOperationException("Root exit has not been observed.");
    public void CloseInput() { } // Native launch returns with its stdin writer closed.
    public void Stop() => jobs.Terminate(native.Job);

    // Called only under the runner gate. Never retain a root reference while awaiting Job drain.
    public bool IsComplete
    {
        get
        {
            if (complete) return true;
            if (rootExitCode is null)
            {
                var status = WaitForSingleObject(native.Process, 0);
                if (status == 258) return false;
                WindowsNativeProcessLauncher.Check(status == 0);
                WindowsNativeProcessLauncher.Check(GetExitCodeProcess(native.Process, out var code));
                rootExitCode = unchecked((int)code);
                native.Process.Dispose(); // The thread handle was closed immediately after creation.
            }
            // The retained Job, pipe and attribute resources are not process/thread references.
            // A closed/invalid Job is an error, never evidence that the group has drained.
            complete = jobs.ActiveProcesses(native.Job) == 0;
            return complete;
        }
    }

    public void Dispose() => native.Dispose();
}

// Existing non-Windows backend: root completion and best-effort tree kill, without Job guarantees.
internal sealed class LocalRunProcess : IRunProcess
{
    private readonly Process process = new();
    public int Id => process.Id;
    public Stream Stdout => process.StandardOutput.BaseStream;
    public Stream Stderr => process.StandardError.BaseStream;
    public bool IsComplete => process.HasExited;
    public int ExitCode => process.ExitCode;
    public void CloseInput() => process.StandardInput.Close();
    public void Stop() => process.Kill(entireProcessTree: true);
    public void Dispose() => process.Dispose();

    internal LocalRunProcess(PreparedRun launch, string parametersJson, string outputDirectory)
    {
        try
        {
            process.StartInfo = new ProcessStartInfo(launch.ExecutablePath)
            {
                UseShellExecute = false,
                WorkingDirectory = launch.WorkingDirectory,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            foreach (var argument in launch.ExperimentSnapshot.Task.Arguments)
                process.StartInfo.ArgumentList.Add(argument);
            process.StartInfo.Environment["RESEARCHLAB_PARAMETERS_JSON"] = parametersJson;
            process.StartInfo.Environment["RESEARCHLAB_OUTPUT_DIR"] = outputDirectory;
            if (!process.Start()) throw new InvalidOperationException("Process did not start.");
        }
        catch { process.Dispose(); throw; }
    }
}
