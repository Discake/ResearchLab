using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using ResearchLab.Domain;

namespace ResearchLab.Infrastructure.Persistence;

public sealed class ResearchDbContext(DbContextOptions<ResearchDbContext> options) : DbContext(options)
{
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        var projects = modelBuilder.Entity<Project>();
        projects.ToTable("Projects");
        projects.HasKey(value => value.Id);
        projects.Property(value => value.Id).HasConversion(value => value.Value, value => new ProjectId(value)).ValueGeneratedNever();
        projects.Property(value => value.Name).IsRequired();
        projects.Property(value => value.Description);
        projects.Property(value => value.WorkspacePath).IsRequired();
        projects.Property<string>("WorkspaceKey").IsRequired();
        projects.HasIndex("WorkspaceKey").IsUnique();
        projects.Property(value => value.CreatedAtUtc);
        projects.Property(value => value.GitRemoteUrl);

        var experiments = modelBuilder.Entity<Experiment>();
        experiments.ToTable("Experiments");
        experiments.HasKey(value => value.Id);
        experiments.Property(value => value.Id).HasConversion(value => value.Value, value => new ExperimentId(value)).ValueGeneratedNever();
        experiments.Property(value => value.ProjectId).HasConversion(value => value.Value, value => new ProjectId(value));
        experiments.HasOne<Project>().WithMany().HasForeignKey(value => value.ProjectId).OnDelete(DeleteBehavior.Restrict);
        experiments.Property(value => value.Definition).HasConversion(value => SnapshotJson.WriteDefinition(value), value => SnapshotJson.ReadDefinition(value)).IsRequired();
        experiments.Property(value => value.CreatedAtUtc);
        experiments.Property(value => value.UpdatedAtUtc);

        var runs = modelBuilder.Entity<Run>();
        runs.ToTable("Runs");
        runs.HasKey(value => value.Id);
        runs.Property(value => value.Id).HasConversion(value => value.Value, value => new RunId(value)).ValueGeneratedNever();
        runs.Ignore(value => value.ExperimentId);
        runs.Property<ExperimentId>("ExperimentKey").HasColumnName("ExperimentId")
            .HasConversion(value => value.Value, value => new ExperimentId(value));
        runs.HasOne<Experiment>().WithMany().HasForeignKey("ExperimentKey").OnDelete(DeleteBehavior.Restrict);
        runs.Property(value => value.ExperimentSnapshot).HasConversion(value => SnapshotJson.WriteExperiment(value), value => SnapshotJson.ReadExperiment(value)).IsRequired();
        runs.Property(value => value.ParametersSnapshot).HasConversion(value => SnapshotJson.WriteParameters(value), value => SnapshotJson.ReadParameters(value)).IsRequired();
        runs.Property(value => value.SourceSnapshot).HasConversion(value => SnapshotJson.WriteSource(value), value => SnapshotJson.ReadSource(value)).IsRequired();
        runs.Property(value => value.EnvironmentSnapshot).HasConversion(value => SnapshotJson.WriteEnvironment(value), value => SnapshotJson.ReadEnvironment(value)).IsRequired();
        runs.Property(value => value.Status);
        runs.Property(value => value.Version).IsConcurrencyToken();
        runs.Property(value => value.CreatedAtUtc);
        runs.Property(value => value.StartedAtUtc);
        runs.Property(value => value.FinishedAtUtc);
        runs.Property(value => value.ProcessId);
        runs.Property(value => value.ExitCode);
        runs.Property(value => value.FailureReason);
        runs.Property<DateTimeOffset>("lastTransitionAt");
        runs.Ignore(value => value.Duration);
        runs.Ignore(value => value.Timeout);
        runs.HasMany(value => value.Metrics).WithOne().HasForeignKey(value => value.RunId).OnDelete(DeleteBehavior.Restrict);
        runs.Navigation(value => value.Metrics).HasField("metrics").UsePropertyAccessMode(PropertyAccessMode.Field);

        var metrics = modelBuilder.Entity<MetricPoint>();
        metrics.ToTable("MetricPoints");
        metrics.HasKey(value => value.Id);
        metrics.Property(value => value.Id).ValueGeneratedNever();
        metrics.Property(value => value.RunId).HasConversion(value => value.Value, value => new RunId(value));
        metrics.Property(value => value.Name).IsRequired();
        metrics.Property(value => value.Value);
        metrics.Property(value => value.Step);
        metrics.Property(value => value.RecordedAtUtc);
        metrics.Property(value => value.Unit);
        metrics.Property(value => value.Sequence);
        metrics.HasIndex(value => new { value.RunId, value.Sequence }).IsUnique();
    }
}

public sealed class ResearchDbContextFactory : IDesignTimeDbContextFactory<ResearchDbContext>
{
    public ResearchDbContext CreateDbContext(string[] args) => new(new DbContextOptionsBuilder<ResearchDbContext>()
        .UseSqlite("Data Source=researchlab-design.db").Options);
}
