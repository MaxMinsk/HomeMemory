using MemoryMcp.Core.Diagnostics;
using Xunit;

namespace MemoryMcp.Tests.Notes;

/// <summary>
/// MEMP-247: the server reports what it COSTS to run, so "is this too heavy for the N150" is a reading rather
/// than an argument — and so the decision to add an embedding layer can be judged against measurements instead
/// of guesses.
/// </summary>
public class OperationMetricsTests
{
    [Fact]
    public void Timings_report_count_and_percentiles_per_operation()
    {
        var metrics = new OperationMetrics();
        foreach (var ms in new double[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 100 })
        {
            metrics.Record("notes_search", ms);
        }

        metrics.Record("notes_upsert", 42);

        var report = metrics.Snapshot();

        var search = Assert.Single(report.Operations, op => op.Operation == "notes_search");
        Assert.Equal(10, search.Count);
        Assert.Equal(5, search.P50Ms);
        Assert.Equal(100, search.P95Ms); // the outlier is the point: p95 is where trouble shows first
        Assert.Equal(100, search.MaxMs);
        Assert.Equal(1, Assert.Single(report.Operations, op => op.Operation == "notes_upsert").Count);
    }

    /// <summary>The worst p95 first, so whatever is hurting is the first thing read — not buried alphabetically.</summary>
    [Fact]
    public void Operations_are_reported_worst_first()
    {
        var metrics = new OperationMetrics();
        metrics.Record("fast", 1);
        metrics.Record("slow", 900);
        metrics.Record("middling", 50);

        var operations = metrics.Snapshot().Operations.Select(op => op.Operation).ToList();

        Assert.Equal(["slow", "middling", "fast"], operations);
    }

    /// <summary>
    /// The whole point of a ring buffer: a server left running for months must not accumulate samples. The
    /// call COUNT still has to keep rising, or "how often" would silently reset every window.
    /// </summary>
    [Fact]
    public void Samples_are_bounded_but_the_call_count_is_not()
    {
        var metrics = new OperationMetrics();
        for (var i = 0; i < OperationMetrics.Window * 3; i++)
        {
            metrics.Record("notes_search", 5);
        }

        metrics.Record("notes_search", 999); // lands inside the retained window

        var search = Assert.Single(metrics.Snapshot().Operations);
        Assert.Equal((OperationMetrics.Window * 3) + 1, search.Count);
        Assert.Equal(999, search.MaxMs);
    }

    /// <summary>An operation that is slow BECAUSE it throws is exactly what this is meant to reveal.</summary>
    [Fact]
    public void A_failed_operation_is_still_timed()
    {
        var metrics = new OperationMetrics();

        Assert.Throws<InvalidOperationException>(() =>
            metrics.Measure<int>("notes_upsert", () => throw new InvalidOperationException("boom")));

        Assert.Equal(1, Assert.Single(metrics.Snapshot().Operations).Count);
    }

    [Fact]
    public void Process_figures_are_reported()
    {
        var report = new OperationMetrics().Snapshot();

        Assert.True(report.WorkingSetBytes > 0, "resident memory should be readable");
        Assert.True(report.ManagedHeapBytes > 0, "managed heap should be readable");
        Assert.True(report.CpuSeconds >= 0);
        Assert.True(report.UptimeSeconds >= 0);
    }

    [Fact]
    public void An_untouched_recorder_reports_no_operations()
    {
        Assert.Empty(new OperationMetrics().Snapshot().Operations);
    }
}
