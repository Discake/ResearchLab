using ResearchLab.Cli;

using var cancellation = new CancellationTokenSource();
ConsoleCancelEventHandler cancel = (_, eventArgs) =>
{
    eventArgs.Cancel = true;
    cancellation.Cancel();
};
Console.CancelKeyPress += cancel;
try { return await ConsoleHost.RunAsync(args, Console.Out, Console.Error, cancellation.Token); }
finally
{
    Console.CancelKeyPress -= cancel;
#if RESEARCHLAB_CRASH_TESTS
    ResearchLab.Application.CrashTestCheckpoint.RecordFinally();
#endif
}
