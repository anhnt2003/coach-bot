using CoachBot.Core.Abstractions;
using CoachBot.Core.Domain;
using Microsoft.EntityFrameworkCore;

namespace CoachBot.Infrastructure.Persistence;

/// <summary>
/// Maps the domain onto the schema owned by supabase/migrations/*.sql. EF migrations are intentionally not used:
/// the SQL files are the single source of truth and the integration tests verify this mapping against them.
/// </summary>
public sealed class CoachDbContext(DbContextOptions<CoachDbContext> options) : DbContext(options), ICoachDb
{
    public const string Schema = "coach";

    public DbSet<Athlete> Athletes => Set<Athlete>();
    public DbSet<RaceGoal> RaceGoals => Set<RaceGoal>();
    public DbSet<CorosConnection> CorosConnections => Set<CorosConnection>();
    public DbSet<Activity> Activities => Set<Activity>();
    public DbSet<DailyMetric> DailyMetrics => Set<DailyMetric>();
    public DbSet<TrainingPlan> TrainingPlans => Set<TrainingPlan>();
    public DbSet<PlannedWorkout> PlannedWorkouts => Set<PlannedWorkout>();
    public DbSet<RunAnalysis> RunAnalyses => Set<RunAnalysis>();
    public DbSet<AthleteFeedback> Feedback => Set<AthleteFeedback>();
    public DbSet<Recommendation> Recommendations => Set<Recommendation>();
    public DbSet<PlanChange> PlanChanges => Set<PlanChange>();
    public DbSet<Alert> Alerts => Set<Alert>();
    public DbSet<JobRun> JobRuns => Set<JobRun>();

    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        // Enums are stored as readable text (easier to inspect in the Supabase table editor).
        configurationBuilder.Properties<WorkoutType>().HaveConversion<string>();
        configurationBuilder.Properties<WorkoutStatus>().HaveConversion<string>();
        configurationBuilder.Properties<PlanStatus>().HaveConversion<string>();
        configurationBuilder.Properties<PlanPhase>().HaveConversion<string>();
        configurationBuilder.Properties<ReadinessLevel>().HaveConversion<string>();
        configurationBuilder.Properties<Confidence>().HaveConversion<string>();
        configurationBuilder.Properties<ChangeMagnitude>().HaveConversion<string>();
        configurationBuilder.Properties<RecommendationStatus>().HaveConversion<string>();
        configurationBuilder.Properties<AlertType>().HaveConversion<string>();
        configurationBuilder.Properties<AlertSeverity>().HaveConversion<string>();
        configurationBuilder.Properties<SyncStatus>().HaveConversion<string>();
        configurationBuilder.Properties<SymptomCategory>().HaveConversion<string>();
        configurationBuilder.Properties<DayOfWeek>().HaveConversion<string>();
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        var b = modelBuilder;
        b.HasDefaultSchema(Schema);

        b.Entity<Athlete>(e => e.HasKey(x => x.Id));

        b.Entity<RaceGoal>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasOne<Athlete>().WithMany().HasForeignKey(x => x.AthleteId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<CorosConnection>(e =>
        {
            e.HasKey(x => x.AthleteId);
            e.Property(x => x.CapabilitiesJson).HasColumnType("jsonb");
            e.HasOne<Athlete>().WithOne().HasForeignKey<CorosConnection>(x => x.AthleteId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<Activity>(e =>
        {
            e.HasKey(x => x.Id);
            e.Ignore(x => x.DistanceKm);
            e.Ignore(x => x.IsRun);
            e.Property(x => x.RawJson).HasColumnType("jsonb");
            e.HasIndex(x => new { x.Source, x.ExternalId }).IsUnique();
            e.HasOne<Athlete>().WithMany().HasForeignKey(x => x.AthleteId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<DailyMetric>(e =>
        {
            e.HasKey(x => new { x.AthleteId, x.Date });
            e.Property(x => x.Vo2Max).HasColumnName("vo2_max");
            e.Property(x => x.RawJson).HasColumnType("jsonb");
            e.HasOne<Athlete>().WithMany().HasForeignKey(x => x.AthleteId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<TrainingPlan>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.WarningsJson).HasColumnType("jsonb");
            e.Property(x => x.InputsJson).HasColumnType("jsonb");
            e.HasMany(x => x.Workouts).WithOne().HasForeignKey(w => w.PlanId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne<Athlete>().WithMany().HasForeignKey(x => x.AthleteId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne<RaceGoal>().WithMany().HasForeignKey(x => x.RaceGoalId).OnDelete(DeleteBehavior.NoAction);
        });

        b.Entity<PlannedWorkout>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.StepsJson).HasColumnName("steps").HasColumnType("jsonb");
            e.HasOne<Activity>().WithMany().HasForeignKey(x => x.CompletedActivityId).OnDelete(DeleteBehavior.SetNull);
        });

        b.Entity<RunAnalysis>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.DetailsJson).HasColumnType("jsonb");
            e.HasIndex(x => x.ActivityId).IsUnique();
            e.HasOne<Athlete>().WithMany().HasForeignKey(x => x.AthleteId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne<Activity>().WithMany().HasForeignKey(x => x.ActivityId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne<PlannedWorkout>().WithMany().HasForeignKey(x => x.PlannedWorkoutId).OnDelete(DeleteBehavior.SetNull);
        });

        b.Entity<AthleteFeedback>(e =>
        {
            e.ToTable("athlete_feedback");
            e.HasKey(x => x.Id);
            e.HasOne<Athlete>().WithMany().HasForeignKey(x => x.AthleteId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne<Activity>().WithMany().HasForeignKey(x => x.ActivityId).OnDelete(DeleteBehavior.SetNull);
        });

        b.Entity<Recommendation>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.ChangesJson).HasColumnType("jsonb");
            e.HasOne<Athlete>().WithMany().HasForeignKey(x => x.AthleteId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne<TrainingPlan>().WithMany().HasForeignKey(x => x.PlanId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne<RunAnalysis>().WithMany().HasForeignKey(x => x.RunAnalysisId).OnDelete(DeleteBehavior.SetNull);
        });

        b.Entity<PlanChange>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.BeforeJson).HasColumnType("jsonb");
            e.Property(x => x.AfterJson).HasColumnType("jsonb");
            e.HasOne<TrainingPlan>().WithMany().HasForeignKey(x => x.PlanId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne<PlannedWorkout>().WithMany().HasForeignKey(x => x.PlannedWorkoutId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne<Recommendation>().WithMany().HasForeignKey(x => x.RecommendationId).OnDelete(DeleteBehavior.SetNull);
        });

        b.Entity<Alert>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasOne<Athlete>().WithMany().HasForeignKey(x => x.AthleteId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<JobRun>(e => e.HasKey(x => new { x.JobName, x.RunKey }));
    }
}
