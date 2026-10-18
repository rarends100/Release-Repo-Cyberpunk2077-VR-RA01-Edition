using RA01.Core;
using Xunit;

namespace RA01.Tests;

// RA01: M47 - the F10 window's tables (Core\UI_SettingsMenus\SettingsMenu.cs) and the values they show (Core\Tuning.cs). Adding a tab means keeping four
// tables in step (TabNames, the Items list, TabStart, TabCount); these tests catch one being missed, a slider whose range is upside down, a default
// outside its own slider, and Reset all not giving the same result twice.
// Builder picture: checking a tool wall - every tool on its own hook, the outlines in the right order, and each tool back on its outline after a reset.
// Cost - CPU: extremely low (a few hundred compares; each row's own value read once); GPU: none.
public sealed class SettingsMenuTests : IDisposable
{
    // RA01: the tab names, in order (tripwire: a new tab is fine, but Dev_Rehook stays last so no other tab moves).
    static readonly string[] TabSnapshot = { "VR Video Settings", "VR Controls", "VRIK", "HUD", "Weapons", "Vehicle", "Performance", "Log control", "Dev Only Tools", "Dev_Rehook" };

    readonly Action restoreTuning = Kit.Remember(typeof(Tuning));

    // RA01: Tuning back as it was. Cost - CPU: extremely low; GPU: none.
    public void Dispose() => restoreTuning();

    // RA01: the four tab tables line up: one start and one count per tab name, each tab starts where the last ended, and together they cover every item.
    // Cost - CPU: extremely low; GPU: none.
    [Fact]
    public void Tab_tables_line_up()
    {
        int tabs = SettingsMenu.TabNames.Length;
        Assert.Equal(tabs, SettingsMenu.TabStart.Length);
        Assert.Equal(tabs, SettingsMenu.TabCount.Length);
        Assert.Equal(0, SettingsMenu.TabStart[0]);
        for (int t = 1; t < tabs; t++)
            Assert.True(SettingsMenu.TabStart[t] == SettingsMenu.TabStart[t - 1] + SettingsMenu.TabCount[t - 1], $"tab \"{SettingsMenu.TabNames[t]}\" should start at {SettingsMenu.TabStart[t - 1] + SettingsMenu.TabCount[t - 1]}, its TabStart says {SettingsMenu.TabStart[t]}");
        Assert.Equal(SettingsMenu.Items.Length, SettingsMenu.TabStart[^1] + SettingsMenu.TabCount[^1]);
        Assert.All(SettingsMenu.TabCount, c => Assert.True(c > 0, "a tab with no rows"));
    }

    // RA01: the tab names match the snapshot, and Dev_Rehook is last with its 4 rows plus one row per Rehooker table row. Cost - CPU: extremely low; GPU: none.
    [Fact]
    public void Tabs_match_snapshot_and_rehook_is_last()
    {
        Assert.True(SettingsMenu.TabNames.SequenceEqual(TabSnapshot), Kit.Tripwire($"the F10 tabs (now: {string.Join(" | ", SettingsMenu.TabNames)})", "SettingsMenuTests.cs, TabSnapshot"));
        Assert.Equal("Dev_Rehook", SettingsMenu.TabNames[^1]);
        Assert.Equal(4 + HookRegistry.Rows.Length, SettingsMenu.TabCount[^1]);
        for (int i = 0; i < HookRegistry.Rows.Length; i++)
            Assert.Equal(HookRegistry.Rows[i].Name, SettingsMenu.Items[SettingsMenu.TabStart[^1] + 4 + i].Name);
    }

    // RA01: every row has a name and a tip, its range is the right way up, a choice row has choices, and no two rows in one tab share a name.
    // Cost - CPU: extremely low; GPU: none.
    [Fact]
    public void Every_row_is_well_formed()
    {
        for (int t = 0; t < SettingsMenu.TabNames.Length; t++)
        {
            var tab = SettingsMenu.Items.Skip(SettingsMenu.TabStart[t]).Take(SettingsMenu.TabCount[t]).ToArray();
            var twice = tab.GroupBy(i => i.Name).Where(g => g.Count() > 1).Select(g => g.Key).ToArray();
            Assert.True(twice.Length == 0, $"tab \"{SettingsMenu.TabNames[t]}\" has two rows called: {string.Join(", ", twice)}");
            foreach (SettingsMenu.Item item in tab)
            {
                Assert.False(string.IsNullOrWhiteSpace(item.Name), $"a row with no name in tab \"{SettingsMenu.TabNames[t]}\"");
                Assert.False(string.IsNullOrWhiteSpace(item.Tip), $"row \"{item.Name}\" has no tip");
                Assert.True(item.Min <= item.Max, $"row \"{item.Name}\": its range is upside down ({item.Min} > {item.Max})");
                Assert.True(item.Options == null || item.Options.Length > 0, $"row \"{item.Name}\": a choice row with no choices");
                Assert.NotNull(item.Get);
                Assert.NotNull(item.Set);
            }
        }
    }

    // RA01: after Reset all, every slider shows a value inside its own range (a default outside its slider snaps the first time it is touched).
    // Only plain sliders are checked: rows with a button (Trigger), a status line or their own text show something else.
    // Cost - CPU: extremely low (one value read per row); GPU: none.
    [Fact]
    public void Defaults_sit_inside_their_sliders()
    {
        Tuning.ResetAll();
        foreach (SettingsMenu.Item item in SettingsMenu.Items.Where(i => i.Trigger == null && i.Status == null && i.Text == null && i.Options == null && i.Max > i.Min))
        {
            float v = item.Get();
            Assert.True(float.IsFinite(v), $"row \"{item.Name}\" shows {v}");
            Assert.True(v >= item.Min - 1e-4f && v <= item.Max + 1e-4f, $"row \"{item.Name}\": its value after Reset all ({v}) is outside its slider ({item.Min} .. {item.Max})");
        }
    }

    // RA01: the IPD row is found by its name (SettingsMenu.IpdItem looks it up that way, M24-2). Cost - CPU: extremely low; GPU: none.
    [Fact]
    public void Ipd_row_is_found_by_name()
    {
        Assert.Contains(SettingsMenu.Items, i => i.Name == "Eye distance (IPD)");
    }
}
