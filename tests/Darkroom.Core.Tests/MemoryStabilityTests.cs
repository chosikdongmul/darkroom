using Darkroom.Core;
using Darkroom.Providers;
using Xunit.Abstractions;

namespace Darkroom.Core.Tests;

/// <summary>오래 켜 두는 앱이라 반복해도 메모리가 늘지 않는지 본다. 다른 테스트와 겹치면 측정이 흔들려 따로 돌린다.</summary>
[CollectionDefinition("memory", DisableParallelization = true)]
public class MemoryCollection { }

[Collection("memory")]
public class MemoryStabilityTests(ITestOutputHelper output)
{
    static long Heap() { GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect(); return GC.GetTotalMemory(true); }

    [Fact]
    public async Task Calendar_sync_repeated_does_not_grow()
    {
        var ics = "BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:t\r\n" + string.Concat(Enumerable.Range(0, 60).Select(i =>
            $"BEGIN:VEVENT\r\nUID:{i}\r\nSUMMARY:일정 {i}\r\nDTSTART;VALUE=DATE:{DateTime.Today.AddDays(i * 3):yyyyMMdd}\r\nRRULE:FREQ=YEARLY\r\nEND:VEVENT\r\n")) + "END:VCALENDAR\r\n";
        var cal = new CalendarInfo("https://x/cal/", "c", null);
        for (int i = 0; i < 30; i++) _ = CalendarIcs.Expand(ics, cal, DateTime.Today, DateTime.Today.AddDays(60), DateTime.Today.AddDays(400)).ToList();
        var before = Heap();
        for (int i = 0; i < 300; i++) _ = CalendarIcs.Expand(ics, cal, DateTime.Today, DateTime.Today.AddDays(60), DateTime.Today.AddDays(400)).ToList();
        var after = Heap();
        output.WriteLine($"expand x300: {before >> 10}KB -> {after >> 10}KB");
        Assert.True(after - before < 2_000_000, $"{before} -> {after}");
    }

    [Fact]
    public void Bus_subscribe_churn_does_not_grow()
    {
        var bus = new DataBus();
        for (int i = 0; i < 1000; i++) bus.Subscribe("time.now", (_, _) => { }).Dispose();
        var before = Heap();
        for (int i = 0; i < 100_000; i++)
        {
            using var s = bus.Subscribe("time.now", (_, _) => { });
            bus.Publish("time.now", new { epochMs = i });
        }
        var after = Heap();
        output.WriteLine($"bus x100k: {before >> 10}KB -> {after >> 10}KB");
        Assert.True(after - before < 1_000_000, $"{before} -> {after}");
    }
}
