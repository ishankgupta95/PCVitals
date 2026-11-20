using Xunit;

namespace PCVitals.Tests;

/// <summary>Searching the settings window finds settings by their words, synonyms and choices.</summary>
public sealed class SettingsSearchTests
{
    sealed class Host : ISettingsHost
    {
        public AppSettings Settings { get; } = AppSettings.CreateDefault();
        public bool IsAdmin => false;
        public Snapshot? Latest => null;
        public string? WidgetFitNote => null;
        public bool GpuPowerEstimated => true;
        public IReadOnlyDictionary<string, float> PowerRatios => new Dictionary<string, float>();
        public void SettingsChanged() { }
        public void RecalibrateGpuPower() { }
        public int ExportHistory(string path) => 0;
        public HistoryArchive? Archive => null;
        public long HistorySizeBytes => 0;
        public Task DeleteHistoryAsync() => Task.CompletedTask;
        public AlertLog AlertLog { get; } = new(Path.Combine(Path.GetTempPath(), "pcvitals-search-test-alerts.json"));
        public DateTime? LastFired(string ruleId) => null;
        public void TestAlert(AlertRule rule) { }
        public void ReopenSettings(string page) { }
        public void EditPanel(string id) { }
    }

    static List<SettingsForm.SearchHit> Find(string query)
    {
        using var form = new SettingsForm(new Host());
        return form.Search(query).ToList();
    }

    [Fact]
    public void Finds_settings_by_title_synonym_and_choice()
    {
        // A synonym ("animation" → motion) ranks the setting named for it first.
        var animation = Find("animation");
        Assert.Equal("Motion", animation[0].Card.Title);
        Assert.All(animation, h => Assert.Equal("panel", h.Page));

        Assert.Contains(Find("start with windows"), h => h.Card.Title == "Start with Windows");
        // Dropdown choices are searchable too.
        Assert.Contains(Find("radial"), h => h.Card.Title == "Background gradient");
        // Plurals and American spelling.
        Assert.Contains(Find("gradients"), h => h.Card.Title == "Background gradient");
        Assert.Contains(Find("color"), h => h.Card.Title == "Labels");
        // Every word has to match.
        Assert.Empty(Find("glow xyzzy"));
    }
}
