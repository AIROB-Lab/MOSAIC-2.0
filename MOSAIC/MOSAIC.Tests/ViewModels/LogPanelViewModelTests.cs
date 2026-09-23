using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using MOSAIC.Diagnostics;
using MOSAIC.ViewModels;

namespace MOSAIC.Tests.ViewModels;

/// <summary>
/// Guards the split between what the logger CAPTURES and what the panel DISPLAYS.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="Log.MinimumLevel"/> is startup state — the build default plus <c>MOSAIC_LOG_LEVEL</c> —
/// and decides what is ever formatted, queued and kept at all.
/// <see cref="LogPanelViewModel.ViewLevel"/> is the toolbar dropdown and decides only which of the
/// already-captured entries are on screen. Wiring the dropdown back to the capture level would look
/// entirely right in the panel while silently stripping Info and Warn out of the file the student
/// attaches to a bug report — which is the failure the whole subsystem exists to prevent, and which
/// nothing else in the suite would notice.
/// </para>
/// <para>
/// <see cref="DoNotParallelizeAttribute"/> for the same reason as <c>LogTests</c>: the ring, the
/// level and the sink are process-wide, so a neighbouring test logging mid-assert would make every
/// assertion here a coin flip. Determinism comes from leaving the sink closed — <c>Log.Write</c>
/// then reaches the ring on the calling thread — rather than from waiting on the writer task.
/// </para>
/// <para>
/// The panel is driven through <see cref="LogPanelViewModel.ViewLevel"/>, which is exactly what the
/// dropdown binds to, and through its private tick. <see cref="LogPanelViewModel.Start"/> is
/// deliberately not called: it subscribes to <c>VisualizationTimer</c>, whose <c>DispatcherTimer</c>
/// wants an Avalonia dispatcher this headless process does not have, and no tick would ever fire
/// here in any case.
/// </para>
/// <para>
/// Every test here that logs narrows the view level BEFORE it does so, and that ordering is the whole
/// point rather than a style choice. Entries logged first are in the ring whatever the dropdown does
/// afterwards, so a test that logs first cannot tell the two designs apart; entries logged while the
/// panel is narrowed past them exist only if the dropdown left the gate alone, so widening afterwards
/// and finding them is proof no rewiring is in place.
/// </para>
/// </remarks>
[TestClass]
[DoNotParallelize]
public class LogPanelViewModelTests
{
    /// <summary>Category every entry these tests produce carries, so foreign entries can be filtered out.</summary>
    private const string Category = "LogPanelTests";

    private const string InfoMarker = "info-that-must-survive";
    private const string ErrorMarker = "error-the-panel-shows";

    /// <summary>
    /// The panel's pull from the ring. Private, so reached by reflection rather than by starting the
    /// shared timer; <see cref="Tick"/> fails loudly if it is ever renamed away.
    /// </summary>
    private static readonly MethodInfo? TickMethod =
        typeof(LogPanelViewModel).GetMethod("OnTick", BindingFlags.Instance | BindingFlags.NonPublic);

    private LogLevel _savedLevel;

    [TestInitialize]
    public void Setup()
    {
        _savedLevel = Log.MinimumLevel;

        // No sink: entries then travel to the ring on this thread, which is what makes every
        // assertion below exact rather than a race with the writer task.
        Log.Shutdown();
        Log.MinimumLevel = LogLevel.Debug;
        Log.Ring.Clear();
    }

    [TestCleanup]
    public void Cleanup()
    {
        Log.MinimumLevel = _savedLevel;
        Log.Ring.Clear();
    }

    /// <summary>
    /// The regression this file exists for: the dropdown must be a view filter and nothing else.
    /// </summary>
    [TestMethod]
    public void SettingTheViewLevel_NeverMovesTheCaptureLevel()
    {
        Log.MinimumLevel = LogLevel.Debug;
        using var panel = new LogPanelViewModel();

        foreach (var level in panel.Levels)
        {
            panel.ViewLevel = level;

            Assert.AreEqual(LogLevel.Debug, Log.MinimumLevel,
                $"choosing {level} in the panel rewrote the capture level — the entries below it would " +
                "then be missing from the file a student sends, and no later choice could bring them back");
            Assert.IsTrue(Log.IsEnabled(LogLevel.Debug),
                "the gate must stay open at the level the build captures at, whatever the panel is showing");
        }
    }

    /// <summary>
    /// An entry the panel is not showing must still have been recorded, because the file is the
    /// artefact a bug report is read from.
    /// </summary>
    [TestMethod]
    public void AnEntryBelowTheViewLevel_IsStillCaptured_MerelyNotDisplayed()
    {
        using var panel = new LogPanelViewModel();

        // Narrowed before either entry exists, so the Info entry is written against whatever gate the
        // dropdown left behind rather than against the open one it would have found had it gone first.
        panel.ViewLevel = LogLevel.Error;

        Log.Info(Category, InfoMarker);
        Log.Error(Category, ErrorMarker);

        Tick(panel);

        var shown = Displayed(panel);
        Assert.HasCount(1, shown, "an Error view must show the error and nothing quieter");
        Assert.AreEqual(ErrorMarker, shown[0].Message);

        Assert.IsTrue(Log.IsEnabled(LogLevel.Info),
            "narrowing the panel must not close the gate the Info entry has to pass");

        var captured = Captured().Select(entry => entry.Message).ToList();
        Assert.HasCount(2, captured, "both entries must still be retained, whatever the panel displays");
        Assert.Contains(InfoMarker, captured,
            "an entry logged while the panel was narrowed past it must still have reached the ring — " +
            "and so the file with it; wired to the capture level, the dropdown would have discarded it " +
            "at the gate");
    }

    /// <summary>
    /// Proves the hidden entries were retained rather than discarded: only a view filter can hand
    /// them back.
    /// </summary>
    [TestMethod]
    public void LoweringTheViewLevelAgain_BringsTheHiddenEntriesBack()
    {
        using var panel = new LogPanelViewModel();

        // Narrowed first, so the three quieter entries below are logged against the narrowed dropdown.
        // Logging them first would put them in the ring under either design and prove nothing.
        panel.ViewLevel = LogLevel.Error;

        Log.Debug(Category, "d");
        Log.Info(Category, "i");
        Log.Warn(Category, "w");
        Log.Error(Category, "e");

        Tick(panel);
        Assert.AreEqual("e", string.Join(",", Displayed(panel).Select(entry => entry.Message)),
            "an Error view must narrow to the one error");

        panel.ViewLevel = LogLevel.Debug;
        Assert.AreEqual("d,i,w,e", string.Join(",", Displayed(panel).Select(entry => entry.Message)),
            "widening the filter must reveal, in order, the three entries logged while the panel was " +
            "narrowed past them — had the dropdown been wired to the capture level the gate would have " +
            "stood at Error when they were written, so they would never have been captured and no " +
            "later choice could bring them back");
    }

    /// <summary>
    /// The cap is what stops a long session growing the UI without limit. It must drop the OLDEST
    /// rows: the newest are the ones describing whatever just went wrong.
    /// </summary>
    /// <remarks>
    /// Measured on the rebuild path, and reached by widening the dropdown over a batch logged while it
    /// was narrower — so the batch exists to be capped at all only if the dropdown left the gate alone.
    /// </remarks>
    [TestMethod]
    public void Entries_KeepAtMostTheVisibleCap_AndDropTheOldestFirst()
    {
        const int extra = 200;
        var total = LogPanelViewModel.MaxVisibleEntries + extra;

        Assert.IsGreaterThan(total, Log.Ring.Capacity,
            "the ring must hold the whole batch, or this would be measuring the ring's cap and not the panel's");

        using var panel = new LogPanelViewModel();
        panel.ViewLevel = LogLevel.Error;

        for (var i = 0; i < total; i++) Log.Info(Category, "row-" + i);

        // Widening rebuilds from the ring, which is where the whole batch has to have been all along.
        panel.ViewLevel = LogLevel.Info;

        Assert.HasCount(LogPanelViewModel.MaxVisibleEntries, panel.Entries,
            "the bound collection must not grow past its cap however long the session runs — an empty " +
            "collection here means the batch was never captured, so the dropdown moved the gate");
        Assert.AreEqual("row-" + extra, panel.Entries[0].Message,
            "the oldest rows are the ones that must go");
        Assert.AreEqual("row-" + (total - 1), panel.Entries[^1].Message,
            "the newest row must always survive");
    }

    /// <summary>
    /// The same cap on the live path, which appends batch by batch instead of rebuilding.
    /// </summary>
    [TestMethod]
    public void TickedEntries_AlsoKeepTheCap_AndDropTheOldestFirst()
    {
        const int extra = 200;
        var total = LogPanelViewModel.MaxVisibleEntries + extra;

        Assert.IsGreaterThan(total + 1, Log.Ring.Capacity,
            "the ring must hold the whole batch, or this would be measuring the ring's cap and not the panel's");

        using var panel = new LogPanelViewModel();
        panel.ViewLevel = LogLevel.Warn;

        // One entry below the view level, ahead of the burst: it must never take a row, and it must
        // still be in the ring afterwards, which it can only be if the dropdown is a view filter.
        Log.Info(Category, InfoMarker);

        for (var i = 0; i < total; i++) Log.Warn(Category, "tick-" + i);

        Tick(panel);

        Assert.HasCount(LogPanelViewModel.MaxVisibleEntries, panel.Entries,
            "a burst appended over several ticks must still be trimmed to the cap");
        Assert.AreEqual("tick-" + extra, panel.Entries[0].Message,
            "the oldest rows are the ones that must go");
        Assert.AreEqual("tick-" + (total - 1), panel.Entries[^1].Message,
            "the newest row must always survive");

        Assert.Contains(InfoMarker, Captured().Select(entry => entry.Message).ToList(),
            "the hidden entry must have been captured, not gated away, however many rows the panel trims");
    }

    /// <summary>
    /// A narrowed panel must not fall behind the ring: the read cursor advances over every captured
    /// entry, not only over the ones the filter admits.
    /// </summary>
    [TestMethod]
    public void ATick_AdvancesPastTheEntriesTheViewFilterHides()
    {
        const int noise = 400;

        using var panel = new LogPanelViewModel();
        panel.ViewLevel = LogLevel.Error;

        // A burst the filter rejects, and then the one entry that matters. A cursor that only moved
        // over matching entries would still be stuck inside the noise when the error arrived.
        for (var i = 0; i < noise; i++) Log.Info(Category, "noise-" + i);
        Log.Error(Category, ErrorMarker);

        Tick(panel);

        var shown = Displayed(panel);
        Assert.HasCount(1, shown, "the noise must stay hidden");
        Assert.AreEqual(ErrorMarker, shown[0].Message,
            "an error logged behind a burst the panel is filtering out must still appear");

        // The cursor can only have to skip the noise if the noise was captured; gated away at the
        // dropdown it would be the ring, not the read cursor, that this test was exercising.
        Assert.HasCount(noise + 1, Captured(),
            "the hidden burst must have reached the ring, which is what the cursor has to advance over");
    }

    // ---------------------------------------------------------------- helpers

    /// <summary>
    /// Runs the panel's pull enough times to drain any batch these tests produce. Deliberately a
    /// fixed count rather than a wait: the tick is synchronous, so there is nothing to race with.
    /// </summary>
    private static void Tick(LogPanelViewModel panel)
    {
        var tick = TickMethod;
        if (tick is null)
        {
            Assert.Fail("LogPanelViewModel.OnTick is gone — these tests drive the panel's pull through it");
        }
        else
        {
            // One tick copies at most a couple of hundred entries, so the bursts above need several
            // passes; the surplus passes find nothing and cost a counter read each.
            for (var i = 0; i < 32; i++) tick.Invoke(panel, null);
        }
    }

    /// <summary>Only the displayed rows these tests produced, so a foreign entry cannot skew a count.</summary>
    private static List<LogEntry> Displayed(LogPanelViewModel panel) =>
        panel.Entries.Where(entry => entry.Category == Category).ToList();

    /// <summary>Only the retained entries these tests produced, read straight from the ring.</summary>
    private static List<LogEntry> Captured()
    {
        var into = new List<LogEntry>();
        Log.Ring.CopyFrom(0, into, int.MaxValue);
        return into.Where(entry => entry.Category == Category).ToList();
    }
}
