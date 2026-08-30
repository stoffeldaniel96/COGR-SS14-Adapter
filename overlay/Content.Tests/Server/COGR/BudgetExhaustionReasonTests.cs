using System;
using System.Linq;
using Content.Server.COGR.Systems;
using NUnit.Framework;

namespace Content.Tests.Server.COGR;

[TestFixture]
[TestOf(typeof(BudgetExhaustionReason))]
public sealed class BudgetExhaustionReasonTests
{
    [Test]
    public void CategoryCounts_Total_SumsAllCategories()
    {
        var counts = CreateCounts(
            doors: 1,
            items: 2,
            tools: 3,
            actors: 4,
            controls: 5,
            containers: 6,
            machines: 7,
            barriers: 8,
            structures: 9,
            genericObjects: 10);

        Assert.That(counts.Total, Is.EqualTo(55));
    }

    [TestCase("door", nameof(CategoryCounts.Doors))]
    [TestCase("handheld_item", nameof(CategoryCounts.Items))]
    [TestCase("handheld_tool", nameof(CategoryCounts.Tools))]
    [TestCase("actor", nameof(CategoryCounts.Actors))]
    [TestCase("control", nameof(CategoryCounts.Controls))]
    [TestCase("container", nameof(CategoryCounts.Containers))]
    [TestCase("machine", nameof(CategoryCounts.Machines))]
    [TestCase("barrier", nameof(CategoryCounts.Barriers))]
    [TestCase("structure", nameof(CategoryCounts.Structures))]
    [TestCase("unclassified", nameof(CategoryCounts.GenericObjects))]
    public void CategoryCounts_Increment_UpdatesOnlySelectedCategory(
        string category,
        string expectedProperty)
    {
        var result = CreateCounts().Increment(category);

        Assert.That(result.Total, Is.EqualTo(1));
        var property = typeof(CategoryCounts).GetProperty(expectedProperty);
        Assert.That(property, Is.Not.Null);
        Assert.That(property!.GetValue(result), Is.EqualTo(1));
    }

    [Test]
    public void CategoryCounts_ToString_IncludesAllCategories()
    {
        var counts = CreateCounts(
            doors: 1,
            items: 2,
            tools: 3,
            actors: 4,
            controls: 5,
            containers: 6,
            machines: 7,
            barriers: 8,
            structures: 9,
            genericObjects: 10);

        var result = counts.ToString();

        Assert.That(result, Does.Contain("doors=1"));
        Assert.That(result, Does.Contain("items=2"));
        Assert.That(result, Does.Contain("tools=3"));
        Assert.That(result, Does.Contain("actors=4"));
        Assert.That(result, Does.Contain("controls=5"));
        Assert.That(result, Does.Contain("containers=6"));
        Assert.That(result, Does.Contain("machines=7"));
        Assert.That(result, Does.Contain("barriers=8"));
        Assert.That(result, Does.Contain("structures=9"));
        Assert.That(result, Does.Contain("generic=10"));
    }

    [Test]
    public void PerceptionDiagnostics_ToSummary_IncludesAllMetrics()
    {
        var diagnostics = new PerceptionDiagnostics
        {
            CandidatesDiscovered = 50,
            CandidatesEvaluated = 30,
            ObservationsEmitted = 10,
            ElapsedProjectionMs = 15.5,
            ExhaustionReason = BudgetExhaustionReason.CandidateLimitReached,
            DiscoveredByCategory = CreateCounts(doors: 5, items: 40, tools: 5),
            EvaluatedByCategory = CreateCounts(doors: 5, items: 20, tools: 5),
            EmittedByCategory = CreateCounts(doors: 5, items: 3, tools: 2),
        };

        var summary = diagnostics.ToSummary();

        Assert.That(summary, Does.Contain("discovered=50"));
        Assert.That(summary, Does.Contain("evaluated=30"));
        Assert.That(summary, Does.Contain("emitted=10"));
        Assert.That(summary, Does.Contain("elapsed=15.50ms"));
        Assert.That(summary, Does.Contain("exhaustion=CandidateLimitReached"));
    }

    [Test]
    [TestCase(BudgetExhaustionReason.None)]
    [TestCase(BudgetExhaustionReason.ServerLimitClamped)]
    [TestCase(BudgetExhaustionReason.CandidateLimitReached)]
    [TestCase(BudgetExhaustionReason.ObservationLimitReached)]
    [TestCase(BudgetExhaustionReason.TimeBudgetExhausted)]
    public void BudgetExhaustionReason_AllValuesAreDistinct(BudgetExhaustionReason reason)
    {
        _ = reason;
        var values = Enum.GetValues<BudgetExhaustionReason>();
        var distinctCount = values.Distinct().Count();

        Assert.That(distinctCount, Is.EqualTo(values.Length),
            "All BudgetExhaustionReason values should be distinct");
    }

    private static CategoryCounts CreateCounts(
        int doors = 0,
        int items = 0,
        int tools = 0,
        int actors = 0,
        int controls = 0,
        int containers = 0,
        int machines = 0,
        int barriers = 0,
        int structures = 0,
        int genericObjects = 0)
    {
        return new CategoryCounts(
            Doors: doors,
            Items: items,
            Tools: tools,
            Actors: actors,
            Controls: controls,
            Containers: containers,
            Machines: machines,
            Barriers: barriers,
            Structures: structures,
            GenericObjects: genericObjects);
    }
}
