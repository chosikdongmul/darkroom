using System.Text.Json;
using Darkroom.Core;

namespace Darkroom.Core.Tests;

public class DataBusTests
{
    [Fact]
    public void Publish_delivers_to_subscribers_and_keeps_last_value()
    {
        var bus = new DataBus();
        var got = new List<long>();
        using var sub = bus.Subscribe("time.now", (_, v) => got.Add(v.GetProperty("epochMs").GetInt64()));
        bus.Publish("time.now", new { epochMs = 5L });
        bus.Publish("time.now", new { epochMs = 6L });
        Assert.Equal([5L, 6L], got);
        Assert.True(bus.TryGet("time.now", out var last));
        Assert.Equal(6, last.GetProperty("epochMs").GetInt64());
    }

    [Fact]
    public void Namespace_events_fire_on_first_subscriber_and_last_unsubscribe()
    {
        var bus = new DataBus();
        var events = new List<(string, int)>();
        bus.NamespaceSubscribersChanged += (ns, n) => events.Add((ns, n));
        var a = bus.Subscribe("system.cpu", (_, _) => { });
        var b = bus.Subscribe("system.ram", (_, _) => { });
        a.Dispose();
        a.Dispose(); // 두 번 해제해도 안전
        b.Dispose();
        Assert.Equal([("system", 1), ("system", 0)], events);
    }

    [Theory]
    [InlineData("time.now", true)]
    [InlineData("sun.goldenHourStart", true)]
    [InlineData("time", false)]
    [InlineData("Time.now", false)]
    [InlineData("time.now; drop", false)]
    public void Key_validation(string key, bool valid) => Assert.Equal(valid, DataBus.IsValidKey(key));

    [Theory]
    [InlineData("system.*", true)]
    [InlineData("time.now", true)]
    [InlineData("*", false)]
    [InlineData("system.*.x", false)]
    [InlineData("system*", false)]
    public void Subscription_validation(string s, bool valid) => Assert.Equal(valid, DataBus.IsValidSubscription(s));
}

public class ProviderHostTests
{
    sealed class CountingProvider : IDataProvider
    {
        public int Calls;
        public string Namespace => "test";
        public TimeSpan Interval => TimeSpan.FromMilliseconds(50);
        public Task UpdateAsync(IDataBus bus, CancellationToken ct) { Interlocked.Increment(ref Calls); bus.Publish("test.n", Calls); return Task.CompletedTask; }
    }

    [Fact]
    public async Task Provider_runs_only_while_subscribed()
    {
        var bus = new DataBus();
        var p = new CountingProvider();
        using var host = new ProviderHost(bus, [p]);
        await Task.Delay(150);
        Assert.Equal(0, p.Calls);

        var sub = bus.Subscribe("test.n", (_, _) => { });
        await Task.Delay(300);
        Assert.True(p.Calls >= 2, $"calls={p.Calls}");
        Assert.Contains("test", host.Running);

        sub.Dispose();
        await Task.Delay(120);
        int after = p.Calls;
        await Task.Delay(250);
        Assert.Equal(after, p.Calls);
        Assert.Empty(host.Running);
    }
}

public class ManifestTests : IDisposable
{
    readonly string _dir = Path.Combine(Path.GetTempPath(), "darkroom-test-" + Guid.NewGuid().ToString("N"));

    public ManifestTests() => Directory.CreateDirectory(_dir);
    public void Dispose() => Directory.Delete(_dir, recursive: true);

    WidgetManifest Write(string json, bool withEntry = true)
    {
        File.WriteAllText(Path.Combine(_dir, "widget.json"), json);
        if (withEntry) File.WriteAllText(Path.Combine(_dir, "index.html"), "<html></html>");
        return WidgetManifest.Load(_dir);
    }

    [Fact]
    public void Loads_valid_manifest_and_merges_settings()
    {
        var m = Write("""
        { "id": "darkroom.clock", "name": "Clock", "subscribe": ["time.now"],
          "settings": { "format24h": { "type": "bool", "default": true },
                        "style": { "type": "choice", "options": ["a", "b"] },
                        "size": { "type": "number", "default": 2, "min": 1, "max": 5 } } }
        """);
        Assert.Equal("darkroom.clock.w.darkroom", m.HostName);
        var saved = new Dictionary<string, JsonElement>
        {
            ["format24h"] = DrJson.ToElement(false),
            ["size"] = DrJson.ToElement(99),      // 범위 밖 → 기본값
            ["unknown"] = DrJson.ToElement("x"),  // 스키마에 없음 → 버림
        };
        var s = m.EffectiveSettings(saved);
        Assert.False(s["format24h"].GetBoolean());
        Assert.Equal(2, s["size"].GetDouble());
        Assert.Equal("a", s["style"].GetString());
        Assert.False(s.ContainsKey("unknown"));
    }

    [Theory]
    [InlineData("""{ "id": "Bad Id" }""", "id")]
    [InlineData("""{ "id": "assets" }""", "호스트")]
    [InlineData("""{ "id": "a.b", "entry": "../x.html" }""", "entry")]
    [InlineData("""{ "id": "a.b", "windowMode": "floating" }""", "windowMode")]
    [InlineData("""{ "id": "a.b", "permissions": ["shell"] }""", "permission")]
    [InlineData("""{ "id": "a.b", "settings": { "x": { "type": "bool", "default": 3 } } }""", "default")]
    public void Rejects_invalid_manifest(string json, string expected)
    {
        var ex = Assert.Throws<InvalidDataException>(() => Write(json));
        Assert.Contains(expected, ex.Message);
    }

    static string RepoWidgets()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !Directory.Exists(Path.Combine(dir.FullName, "widgets"))) dir = dir.Parent;
        return Path.Combine(dir!.FullName, "widgets");
    }

    [Fact]
    public void Bundled_widgets_load_and_keep_setting_order()
    {
        foreach (var folder in Directory.GetDirectories(RepoWidgets()))
            WidgetManifest.Load(folder); // 하나라도 잘못되면 예외
        var dday = WidgetManifest.Load(Path.Combine(RepoWidgets(), "countdown"));
        Assert.Equal(["events", "countToday", "calendar"], dday.Settings.Keys.ToArray());
    }
}

public class FontPackTests
{
    static string RepoFonts()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !Directory.Exists(Path.Combine(dir.FullName, "sdk", "fonts"))) dir = dir.Parent;
        return Path.Combine(dir!.FullName, "sdk", "fonts");
    }

    [Fact]
    public void Bundled_packs_load_without_errors_and_have_css()
    {
        var errors = new List<string>();
        var packs = FontPack.LoadAll(RepoFonts(), errors);
        Assert.Empty(errors);
        Assert.Equal(["pretendard", "gallery", "lab", "magazine", "letterpress", "engraved", "credits"], packs.Keys);
        foreach (var p in packs.Values)
        {
            Assert.Contains("@font-face", p.Css);
            Assert.Contains("https://assets.darkroom/fonts/packs/" + p.Key + "/", p.Css);
            Assert.Equal(p.Num, p.CssVariables()["--dr-font-num"]);
        }
    }

    [Fact]
    public void Pack_values_that_could_break_css_are_rejected()
    {
        Assert.NotNull(new FontPack { Key = "x", Ui = "a; } body { color: red" }.Validate());
        Assert.NotNull(new FontPack { Key = "Bad Key" }.Validate());
        Assert.NotNull(new FontPack { Key = "x", NumStyle = "oblique" }.Validate());
        Assert.Null(new FontPack { Key = "x", Ui = "'Gowun Batang', serif", NumVariation = "\"SOFT\" 100" }.Validate());
    }
}

public class ThemeTests
{
    [Fact]
    public void Tokens_become_css_variables_and_unsafe_values_are_dropped()
    {
        var t = new Theme { Tokens = new() { ["bg"] = "#000", ["fontUi"] = "Pretendard", ["evil"] = "red; } body { x", ["bad-name"] = "1" } };
        var v = t.CssVariables();
        Assert.Equal("#000", v["--dr-bg"]);
        Assert.Equal("Pretendard", v["--dr-font-ui"]);
        Assert.Equal(2, v.Count);
    }
}

public class ActionPolicyTests
{
    static WidgetManifest Widget(params string[] perms) => new() { Id = "t", Permissions = perms.ToList() };

    [Fact]
    public void Allows_web_urls_and_existing_folders()
    {
        Assert.Null(ActionPolicy.CheckOpen("https://example.com", Widget()));
        Assert.Null(ActionPolicy.CheckOpen(Path.GetTempPath(), Widget()));
    }

    [Fact]
    public void Blocks_executables_and_odd_schemes_without_launch()
    {
        var exe = Path.Combine(Environment.SystemDirectory, "notepad.exe");
        Assert.NotNull(ActionPolicy.CheckOpen(exe, Widget()));
        Assert.Null(ActionPolicy.CheckOpen(exe, Widget("launch")));
        Assert.NotNull(ActionPolicy.CheckOpen("ms-settings:display", Widget()));
        Assert.NotNull(ActionPolicy.CheckOpen(@"C:\does\not\exist.txt", Widget()));
    }

    [Theory]
    [InlineData("내 레이아웃", "내 레이아웃")]
    [InlineData(@"..\..\evil", "evil")]
    [InlineData("a/b:c*?", "abc")]
    [InlineData("   ", "layout")]
    public void Layout_names_are_sanitized(string name, string key) => Assert.Equal(key, Layout.ToKey(name));
}
