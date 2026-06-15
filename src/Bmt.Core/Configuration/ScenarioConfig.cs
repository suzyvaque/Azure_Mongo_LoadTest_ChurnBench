namespace Bmt.Core.Configuration;

/// <summary>
/// Scenario + run settings for the <c>test</c> command (test_instruction.md §6.2). Phase-1 v1 drives
/// Scenario A (steady peak-hour) and Scenario B (Poisson burst) within a single run. All rates are
/// config-driven so a short smoke run can reuse the production shape at reduced duration.
/// </summary>
public sealed class ScenarioConfig
{
    public SteadyScenarioConfig Steady { get; set; } = new();

    public BurstScenarioConfig Burst { get; set; } = new();

    /// <summary>
    /// Cap on concurrently in-flight Tasks (each owns one fresh connection). Guards the client host
    /// from unbounded socket growth; set at/above the §6.2 concurrent-connection target (≥ 11,000).
    /// </summary>
    public int MaxConcurrentTasks { get; set; } = 15_000;

    /// <summary>Period (ms) for sampling client-host resources (ports/TIME_WAIT/handles/CPU/mem), §7.3.</summary>
    public int ResourceSampleIntervalMs { get; set; } = 1_000;

    public void Validate()
    {
        Steady.Validate();
        Burst.Validate();
        if (MaxConcurrentTasks <= 0)
        {
            throw new InvalidOperationException("Scenario.MaxConcurrentTasks must be > 0.");
        }

        if (ResourceSampleIntervalMs <= 0)
        {
            throw new InvalidOperationException("Scenario.ResourceSampleIntervalMs must be > 0.");
        }
    }
}

/// <summary>Scenario A — steady peak-hour sustained load (§6.2: 135 conn/sec, 540 ops/sec for 1 h).</summary>
public sealed class SteadyScenarioConfig
{
    public bool Enabled { get; set; } = true;

    /// <summary>New Tasks (= new connections) opened per second. §6.2 peak = 135.</summary>
    public double TasksPerSecond { get; set; } = 135;

    /// <summary>How long to hold the steady load, seconds. §6.4 Phase 1 = 3600 (1 hour).</summary>
    public int DurationSeconds { get; set; } = 3_600;

    public void Validate()
    {
        if (TasksPerSecond <= 0)
        {
            throw new InvalidOperationException("Steady.TasksPerSecond must be > 0.");
        }

        if (DurationSeconds <= 0)
        {
            throw new InvalidOperationException("Steady.DurationSeconds must be > 0.");
        }
    }
}

/// <summary>Scenario B — Poisson-arrival burst (§6.2: λ=0.57 Job/sec, ≤500 Tasks/Job injected at once).</summary>
public sealed class BurstScenarioConfig
{
    public bool Enabled { get; set; } = true;

    /// <summary>Poisson arrival rate of Jobs per second. §6.2 = 0.57.</summary>
    public double JobsPerSecondLambda { get; set; } = 0.57;

    /// <summary>Max Tasks injected simultaneously per Job (the 500 batch cap = worst-case burst).</summary>
    public int MaxTasksPerJob { get; set; } = 500;

    /// <summary>Min Tasks per Job (production observed ≈ 14..491; default keeps bursts meaningful).</summary>
    public int MinTasksPerJob { get; set; } = 14;

    /// <summary>How long to run the burst phase, seconds.</summary>
    public int DurationSeconds { get; set; } = 600;

    public void Validate()
    {
        if (JobsPerSecondLambda <= 0)
        {
            throw new InvalidOperationException("Burst.JobsPerSecondLambda must be > 0.");
        }

        if (MaxTasksPerJob <= 0 || MinTasksPerJob <= 0 || MinTasksPerJob > MaxTasksPerJob)
        {
            throw new InvalidOperationException("Burst.MinTasksPerJob/MaxTasksPerJob invalid (0 < min <= max).");
        }

        if (DurationSeconds <= 0)
        {
            throw new InvalidOperationException("Burst.DurationSeconds must be > 0.");
        }
    }
}
