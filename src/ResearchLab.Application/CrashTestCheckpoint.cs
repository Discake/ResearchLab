#if RESEARCHLAB_CRASH_TESTS
using System.IO.Pipes;
using System.Text.Json;

namespace ResearchLab.Application;

// Compiled ONLY into the dedicated CrashTests CLI/dependencies, absent from shipping builds.
// The supervisor receives stage/PID, never a Job handle. No callback in normal execution.
public static class CrashTestCheckpoint
{
    public static void Reach(string stage, int? childPid = null)
    {
        if (Environment.GetEnvironmentVariable("RESEARCHLAB_CRASH_CHECKPOINT") != stage) return;
        using var pipe = new NamedPipeClientStream(".", Environment.GetEnvironmentVariable("RESEARCHLAB_CRASH_PIPE")!, PipeDirection.InOut);
        pipe.Connect(10000);
        using var writer = new StreamWriter(pipe, leaveOpen: true) { AutoFlush = true };
        using var reader = new StreamReader(pipe, leaveOpen: true);
        writer.WriteLine(JsonSerializer.Serialize(new { Stage = stage, OwnerPid = Environment.ProcessId, ChildPid = childPid }));
        if (reader.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(45)).GetAwaiter().GetResult() != "continue")
            throw new IOException("Synthetic checkpoint was not released.");
    }

    public static void RecordFinally()
    {
        var path = Environment.GetEnvironmentVariable("RESEARCHLAB_CRASH_FINALLY_FILE");
        if (path is not null) File.WriteAllText(path, "managed finally executed");
    }
}
#endif
