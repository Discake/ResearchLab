using Xunit;

namespace ResearchLab.Domain.Tests;

public sealed class RunLifecycleTests
{
    public static TheoryData<RunStatus, RunStatus, bool> Transitions
    {
        get
        {
            var allowed = new HashSet<(RunStatus, RunStatus)>
            {
                (RunStatus.Created, RunStatus.Running),
                (RunStatus.Created, RunStatus.Failed),
                (RunStatus.Created, RunStatus.Canceled),
                (RunStatus.Created, RunStatus.Interrupted),
                (RunStatus.Running, RunStatus.Succeeded),
                (RunStatus.Running, RunStatus.Failed),
                (RunStatus.Running, RunStatus.Canceled),
                (RunStatus.Running, RunStatus.TimedOut),
                (RunStatus.Running, RunStatus.Interrupted)
            };
            var data = new TheoryData<RunStatus, RunStatus, bool>();
            foreach (var from in Enum.GetValues<RunStatus>())
                foreach (var to in Enum.GetValues<RunStatus>().Where(status => status != RunStatus.Created))
                    data.Add(from, to, allowed.Contains((from, to)));
            return data;
        }
    }

    [Theory]
    [MemberData(nameof(Transitions))]
    public void EnforcesEveryExposedTransitionIncludingRepeatedCompletion(RunStatus from, RunStatus to, bool allowed)
    {
        var run = AtStatus(from);
        var before = (run.Status, run.Version, run.StartedAtUtc, run.FinishedAtUtc, run.ProcessId, run.ExitCode, run.FailureReason);
        if (allowed)
        {
            Move(run, to, DomainFixtures.Epoch.AddSeconds(30));
            Assert.Equal(to, run.Status);
            Assert.Equal(before.Version + 1, run.Version);
        }
        else
        {
            Assert.Equal("InvalidRunTransition", Assert.Throws<DomainException>(() =>
                Move(run, to, DomainFixtures.Epoch.AddSeconds(30))).Code);
            Assert.Equal(before, (run.Status, run.Version, run.StartedAtUtc, run.FinishedAtUtc, run.ProcessId, run.ExitCode, run.FailureReason));
        }
    }

    [Fact]
    public void InterruptedCreatedPreservesUnknownStartupAndRequiresValidRecoveryMetadata()
    {
        var run = DomainFixtures.Run();
        Assert.Throws<DomainException>(() => run.Interrupt(DomainFixtures.Epoch, " ", run.Version));
        Assert.Throws<DomainException>(() => run.Interrupt(DomainFixtures.Epoch.AddSeconds(-1), "Owner lost", run.Version));
        Assert.Equal(0, run.Version);
        run.Interrupt(DomainFixtures.Epoch.AddSeconds(10), "Owner ended; startup outcome unknown", 0);
        Assert.Equal(RunStatus.Interrupted, run.Status);
        Assert.Equal(1, run.Version);
        Assert.Null(run.ProcessId);
        Assert.Null(run.StartedAtUtc);
        Assert.Null(run.ExitCode);
        Assert.Null(run.Duration);
        Assert.Equal("Owner ended; startup outcome unknown", run.FailureReason);
        Assert.Throws<DomainException>(() => run.Interrupt(DomainFixtures.Epoch.AddSeconds(11), "Repeat", 0));
    }

    [Fact]
    public void CreatedIsOnlyAvailableThroughFactoryAndStatusCannotBeSetPublicly()
    {
        Assert.Empty(typeof(Run).GetConstructors());
        Assert.False(typeof(Run).GetProperty(nameof(Run.Status))!.SetMethod!.IsPublic);
        Assert.Equal(RunStatus.Created, DomainFixtures.Run().Status);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CompletionBeforeStartNeverInventsProcessMetadata(bool fail)
    {
        var run = DomainFixtures.Run();
        if (fail)
            run.Fail(DomainFixtures.Epoch.AddSeconds(1), "Executable not found", run.Version);
        else
            run.Cancel(DomainFixtures.Epoch.AddSeconds(1), run.Version);
        Assert.Null(run.StartedAtUtc);
        Assert.Null(run.ProcessId);
        Assert.Null(run.ExitCode);
        Assert.Null(run.Duration);
        Assert.Equal(DomainFixtures.Epoch.AddSeconds(1), run.FinishedAtUtc);
    }

    [Fact]
    public void RecordsProcessResultAndNormalizesTimestampsToUtc()
    {
        var run = DomainFixtures.Run();
        run.Start(DomainFixtures.Epoch.AddSeconds(1).ToOffset(TimeSpan.FromHours(3)), 123, run.Version);
        run.Succeed(DomainFixtures.Epoch.AddSeconds(5), 0, true, run.Version);
        Assert.Equal(TimeSpan.Zero, run.StartedAtUtc!.Value.Offset);
        Assert.Equal(TimeSpan.FromSeconds(4), run.Duration);
        Assert.Equal(0, run.ExitCode);
        Assert.Null(run.FailureReason);
    }

    [Theory]
    [InlineData(1, true)]
    [InlineData(0, false)]
    public void CannotClaimSuccessWithoutExitZeroAndValidatedResults(int exitCode, bool resultsValidated)
    {
        var run = DomainFixtures.Running();
        Assert.Equal("InvalidSuccess", Assert.Throws<DomainException>(() =>
            run.Succeed(DomainFixtures.Epoch.AddSeconds(2), exitCode, resultsValidated, run.Version)).Code);
        Assert.Equal(RunStatus.Running, run.Status);
        Assert.Null(run.FinishedAtUtc);
        Assert.Equal(1, run.Version);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    public void FailedRunRetainsExitCodeAndReason(int exitCode)
    {
        var run = DomainFixtures.Running();
        run.Fail(DomainFixtures.Epoch.AddSeconds(2), "Result processing or process failure", run.Version, exitCode);
        Assert.Equal(exitCode, run.ExitCode);
        Assert.Equal("Result processing or process failure", run.FailureReason);
    }

    [Fact]
    public void InvalidMetadataDoesNotPartiallyChangeState()
    {
        var run = DomainFixtures.Run();
        Assert.Throws<DomainException>(() => run.Start(DomainFixtures.Epoch, 0, run.Version));
        Assert.Throws<DomainException>(() => run.Start(DomainFixtures.Epoch.AddSeconds(-1), 1, run.Version));
        Assert.Throws<DomainException>(() => run.Fail(DomainFixtures.Epoch, " ", run.Version));
        Assert.Throws<DomainException>(() => run.Fail(DomainFixtures.Epoch, "startup failure", run.Version, 1));
        Assert.Equal(RunStatus.Created, run.Status);
        Assert.Equal(0, run.Version);
        Assert.Null(run.StartedAtUtc);
        Assert.Null(run.FinishedAtUtc);

        run.Start(DomainFixtures.Epoch.AddSeconds(1), 123, run.Version);
        Assert.Throws<DomainException>(() => run.Succeed(DomainFixtures.Epoch, 0, true, run.Version));
        Assert.Throws<DomainException>(() => run.Interrupt(DomainFixtures.Epoch.AddSeconds(2), " ", run.Version));
        Assert.Equal("TimeoutNotReached", Assert.Throws<DomainException>(() =>
            run.TimeOut(DomainFixtures.Epoch.AddSeconds(10), run.Version)).Code);
        Assert.Equal(1, run.Version);
        run.TimeOut(DomainFixtures.Epoch.AddSeconds(11), run.Version);
        Assert.Equal(RunStatus.TimedOut, run.Status);
    }

    [Fact]
    public void StaleUpdatesCannotOverwriteTheWinner()
    {
        var run = DomainFixtures.Running();
        var expectedVersion = run.Version;
        run.Cancel(DomainFixtures.Epoch.AddSeconds(2), expectedVersion);
        Assert.Equal("VersionConflict", Assert.Throws<DomainException>(() =>
            run.Succeed(DomainFixtures.Epoch.AddSeconds(3), 0, true, expectedVersion)).Code);
        Assert.Equal(RunStatus.Canceled, run.Status);
        Assert.Equal(DomainFixtures.Epoch.AddSeconds(2), run.FinishedAtUtc);
        Assert.Null(run.ExitCode);
    }

    private static Run AtStatus(RunStatus status)
    {
        var run = DomainFixtures.Run();
        if (status == RunStatus.Created)
            return run;
        run.Start(DomainFixtures.Epoch.AddSeconds(1), 123, run.Version);
        if (status != RunStatus.Running)
            Move(run, status, DomainFixtures.Epoch.AddSeconds(20));
        return run;
    }

    private static void Move(Run run, RunStatus status, DateTimeOffset at)
    {
        switch (status)
        {
            case RunStatus.Running: run.Start(at, 123, run.Version); break;
            case RunStatus.Succeeded: run.Succeed(at, 0, true, run.Version); break;
            case RunStatus.Failed: run.Fail(at, "Test failure", run.Version); break;
            case RunStatus.Canceled: run.Cancel(at, run.Version); break;
            case RunStatus.TimedOut: run.TimeOut(at, run.Version); break;
            case RunStatus.Interrupted: run.Interrupt(at, "Test interruption", run.Version); break;
            default: throw new ArgumentOutOfRangeException(nameof(status));
        }
    }
}
