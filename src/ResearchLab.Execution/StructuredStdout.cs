using ResearchLab.Application;

namespace ResearchLab.Execution;

// Raw logs are written independently. Oversized lines retain only a prefix long enough
// for M4.1 to report EventTooLarge; the remainder is discarded until the next LF.
internal sealed class StructuredStdout(IProcessOutputSink sink, Task<bool> runningAcknowledged,
    Func<IReadOnlyList<ProtocolDiagnostic>, Task> report)
{
    private readonly byte[] line = new byte[MetricProtocol.MaxEventBytes + 3];
    private int retained;
    private bool hasBytes;
    private long number = 1;

    public async Task FeedAsync(ReadOnlyMemory<byte> bytes)
    {
        for (var index = 0; index < bytes.Length; index++)
        {
            var value = bytes.Span[index];
            hasBytes = true;
            if (retained < line.Length)
                line[retained++] = value;
            if (value == (byte)'\n')
            {
                await EmitAsync();
                number++;
            }
        }
    }

    public async Task CompleteAsync(bool includePartialLine)
    {
        // An output-limit cutoff or read failure is not EOF: never parse a truncated event.
        if (includePartialLine && hasBytes)
            await EmitAsync();
        await report(await sink.CompleteStdoutAsync());
    }

    private async Task EmitAsync()
    {
        await report(await sink.OnStdoutLineAsync(line.AsMemory(0, retained), number, runningAcknowledged));
        retained = 0;
        hasBytes = false;
    }
}
