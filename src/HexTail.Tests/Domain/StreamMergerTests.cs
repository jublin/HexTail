using System.Diagnostics;
using HexTail.Domain;

namespace HexTail.Tests.Domain;

public class StreamMergerTests(ITestOutputHelper output)
{
    private static readonly DateTimeOffset Epoch = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private sealed class Clock
    {
        public DateTimeOffset Now { get; set; } = Epoch.AddHours(1);

        public void Advance(TimeSpan by) => Now += by;
    }

    private static readonly TimeSpan Hold = TimeSpan.FromSeconds(5);

    private static (StreamMerger Merger, Clock Clock) Make(params string[] ids)
    {
        var clock = new Clock();
        return (new StreamMerger(ids, Hold, () => clock.Now), clock);
    }

    private static Line L(string raw, double? seconds) =>
        new(raw) { Timestamp = seconds is null ? null : Epoch.AddSeconds(seconds.Value) };

    private static Line[] Ls(string prefix, params double[] seconds) =>
        [.. seconds.Select(s => L($"{prefix}{s}", s))];

    private static string[] Raw(IReadOnlyList<Line> lines) => [.. lines.Select(l => l.Raw)];

    [Fact]
    public void TwoMembers_InterleaveByTimestamp_HoldingTheLeadingLine()
    {
        var (m, _) = Make("A", "B");

        m.Add("A", Ls("a", 1, 3, 5));
        m.Add("B", Ls("b", 2, 4));

        Assert.Equal(["a1", "b2", "a3", "b4"], Raw(m.Release()));
        Assert.Empty(m.Release());

        m.Add("B", Ls("b", 6));
        Assert.Equal(["a5"], Raw(m.Release()));
    }

    [Fact]
    public void Release_AfterOnlyOneMemberHasLines_HoldsUntilTheOtherCatchesUp()
    {
        var (m, _) = Make("A", "B");

        m.Add("A", Ls("a", 1, 2));
        m.Add("B", Ls("b", 1));

        Assert.Equal(["a1", "b1"], Raw(m.Release()));
    }

    [Fact]
    public void IdleMember_DoesNotBlock_AndRejoinsTheWatermarkWhenActiveAgain()
    {
        var (m, clock) = Make("A", "B");
        m.Add("A", Ls("a", 1));
        m.Add("B", Ls("b", 1));
        m.Release();

        clock.Advance(TimeSpan.FromSeconds(6)); // B idle for more than hold
        m.Add("A", Ls("a", 100));
        Assert.Equal(["a100"], Raw(m.Release()));

        var rejoin = L("b50", 50);
        m.Add("B", [rejoin]); // B active again at 50
        m.Add("A", Ls("a", 101));
        Assert.Equal(["b50"], Raw(m.Release())); // a101 now waits behind B's watermark
        Assert.True(m.IsLate(rejoin));
    }

    [Fact]
    public void Hold_ReleasesALaggingLineOnlyAfterTheHoldHasStrictlyPassed()
    {
        var (m, clock) = Make("A", "B");
        m.Add("A", Ls("a", 10));
        m.Add("B", Ls("b", 1));
        Assert.Equal(["b1"], Raw(m.Release()));

        clock.Advance(TimeSpan.FromSeconds(4));
        m.Add("B", Ls("b", 2)); // keeps B active and lagging
        Assert.Equal(["b2"], Raw(m.Release())); // a10 pending 4 s

        clock.Advance(TimeSpan.FromSeconds(1)); // exactly hold: not yet
        Assert.Empty(m.Release());

        clock.Advance(TimeSpan.FromMilliseconds(1)); // strictly over hold, B still active
        Assert.Equal(["a10"], Raw(m.Release()));
    }

    [Fact]
    public void HoldBoundary_ExactlyEqual_StaysActiveAndPending()
    {
        var (m, clock) = Make("A", "B");
        m.Add("A", Ls("a", 10));
        m.Add("B", Ls("b", 1));
        m.Release();

        clock.Advance(Hold);
        // Both members were last seen exactly `hold` ago: still active, a10 not older than hold.
        Assert.Empty(m.Release());

        clock.Advance(TimeSpan.FromTicks(1));
        Assert.Equal(["a10"], Raw(m.Release()));
    }

    [Fact]
    public void LateLine_IsReleasedNextAndFlagged_ButLaterLinesAreNot()
    {
        var (m, clock) = Make("A", "B");
        m.Add("A", Ls("a", 8, 10));
        m.Add("B", Ls("b", 9, 10));
        var first = m.Release();
        Assert.Equal(["a8", "b9", "a10", "b10"], Raw(first));
        Assert.Equal(Epoch.AddSeconds(10), m.LastReleased);
        Assert.All(first, l => Assert.False(m.IsLate(l)));

        clock.Advance(TimeSpan.FromSeconds(1));
        var lateLine = L("b7", 7);
        m.Add("B", [lateLine]);
        m.Add("A", Ls("a", 11));
        var released = m.Release();
        Assert.Equal(["b7"], Raw(released));
        Assert.True(m.IsLate(lateLine));

        m.Add("B", Ls("b", 11));
        var next = m.Release();
        Assert.Equal(["a11", "b11"], Raw(next));
        Assert.All(next, l => Assert.False(m.IsLate(l)));
    }

    [Fact]
    public void NullTimestamps_TakeTheMembersPreviousKey_AndKeepArrivalOrder()
    {
        var (m, _) = Make("A", "B");
        m.Add("A", [L("a3", 3), L("a-null1", null), L("a-null2", null), L("a5", 5)]);
        m.Add("B", Ls("b", 4, 6));

        Assert.Equal(["a3", "a-null1", "a-null2", "b4", "a5"], Raw(m.Release()));
    }

    [Fact]
    public void NullTimestamp_WithNoPreviousKey_UsesTheClock()
    {
        var (m, clock) = Make("A");
        m.Add("A", [L("x", null), L("y", null)]);

        Assert.Equal(["x", "y"], Raw(m.Release()));
        Assert.Equal(clock.Now, m.LastReleased);
    }

    [Fact]
    public void Ties_OrderByMemberIndexThenArrival()
    {
        var (m, _) = Make("A", "B");
        m.Add("B", [L("b1", 1), L("b1'", 1)]);
        m.Add("A", [L("a1", 1), L("a1'", 1)]);

        Assert.Equal(["a1", "a1'", "b1", "b1'"], Raw(m.Release()));
    }

    [Fact]
    public void NothingIsReleasedTwice_AndUnknownMembersAreIgnored()
    {
        var (m, clock) = Make("A");
        m.Add("A", Ls("a", 1, 2));
        m.Add("ghost", Ls("g", 1));

        Assert.Equal(["a1", "a2"], Raw(m.Release()));
        Assert.Empty(m.Release());
        clock.Advance(TimeSpan.FromMinutes(1));
        Assert.Empty(m.Release());
        Assert.Empty(m.FlushAll());
    }

    [Fact]
    public void MemberWithNoLinesEver_NeverBlocks()
    {
        var (m, _) = Make("A", "B", "C");
        m.Add("A", Ls("a", 1, 2));
        m.Add("B", Ls("b", 3));

        Assert.Equal(["a1", "a2"], Raw(m.Release())); // b3 waits for A, never for C

        m.Add("A", Ls("a", 4));
        Assert.Equal(["b3"], Raw(m.Release()));
    }

    [Fact]
    public void FastClockMember_IsHeldBySlowestActiveMember()
    {
        var (m, clock) = Make("A", "B");
        m.Add("A", Ls("a", 1000));
        m.Add("B", Ls("b", 1, 2));

        Assert.Equal(["b1", "b2"], Raw(m.Release()));

        clock.Advance(TimeSpan.FromSeconds(3));
        m.Add("B", Ls("b", 3));
        Assert.Equal(["b3"], Raw(m.Release()));

        clock.Advance(TimeSpan.FromSeconds(3)); // a1000 pending 6 s > hold
        Assert.Equal(["a1000"], Raw(m.Release()));
    }

    [Fact]
    public void ClockGoingBackwardsWithinAMember_ReleasesTheEarlierLineAndEventuallyTheRest()
    {
        var (m, clock) = Make("A", "B");
        var back = L("a5", 5);
        m.Add("A", [L("a10", 10), back, L("a12", 12)]);
        m.Add("B", Ls("b", 11));

        Assert.Equal(["a10", "a5", "b11"], Raw(m.Release()));
        Assert.True(m.IsLate(back));

        clock.Advance(TimeSpan.FromSeconds(6));
        Assert.Equal(["a12"], Raw(m.Release()));
    }

    [Fact]
    public void ClockGoingBackwards_FlaggedLate()
    {
        var (m, _) = Make("A");
        var back = L("a5", 5);
        m.Add("A", [L("a10", 10), back]);

        Assert.Equal(["a10", "a5"], Raw(m.Release()));
        Assert.True(m.IsLate(back));
    }

    [Fact]
    public void FlushAll_SortsOutOfOrderMembersStably_AndDoesNotMarkLate()
    {
        var (m, _) = Make("A", "B");
        var lines = new[] { L("a3", 3), L("a1", 1), L("a1b", 1), L("a2", 2) };
        m.Add("A", lines);
        m.Add("B", Ls("b", 1.5, 0.5));

        var flushed = m.FlushAll();

        Assert.Equal(["b0.5", "a1", "a1b", "b1.5", "a2", "a3"], Raw(flushed));
        Assert.All(flushed, l => Assert.False(m.IsLate(l)));
        Assert.Equal(Epoch.AddSeconds(3), m.LastReleased);
        Assert.Empty(m.FlushAll());
        Assert.Empty(m.Release());
    }

    [Fact]
    public void FlushAll_OnSortedInput_PreservesOrder()
    {
        var (m, _) = Make("A", "B");
        m.Add("A", Ls("a", 1, 3));
        m.Add("B", Ls("b", 2));

        Assert.Equal(["a1", "b2", "a3"], Raw(m.FlushAll()));
    }

    [Fact]
    public void Reset_ForgetsPendingLinesAndState()
    {
        var (m, _) = Make("A", "B");
        var released = L("a9", 9);
        m.Add("A", [released]);
        m.Add("B", Ls("b", 9, 20));
        m.Release();
        Assert.NotNull(m.LastReleased);

        m.Reset();

        Assert.Null(m.LastReleased);
        Assert.Empty(m.Release());
        Assert.Empty(m.FlushAll());
        Assert.False(m.IsLate(released));

        m.Add("A", Ls("a", 1));
        m.Add("B", Ls("b", 1));
        Assert.Equal(["a1", "b1"], Raw(m.Release()));
    }

    [Fact]
    public void EmptyMemberList_IsHarmless()
    {
        var m = new StreamMerger([], StreamMerger.DefaultHold, () => Epoch);
        m.Add("A", Ls("a", 1));
        Assert.Empty(m.Release());
        Assert.Empty(m.FlushAll());
        Assert.Equal(TimeSpan.FromSeconds(5), StreamMerger.DefaultHold);
    }

    [Fact]
    public void LateSet_IsBounded_OldestFlagsAreForgotten()
    {
        var (m, _) = Make("A");
        m.Add("A", [L("top", 1_000_000)]);
        m.Release();

        var lates = new Line[10_500];
        for (var i = 0; i < lates.Length; i++)
        {
            lates[i] = L("late", i);
        }
        m.Add("A", lates);
        Assert.Equal(lates.Length, m.Release().Count);

        Assert.True(m.IsLate(lates[^1]));
        Assert.False(m.IsLate(lates[0]));
    }

    [Fact]
    public void Performance_FourMembersFiftyThousandLinesEach_ReleasesQuicklyWithBoundedAllocations()
    {
        const int members = 4;
        const int batches = 100;
        const int batchSize = 500;
        var clock = new Clock();
        var ids = Enumerable.Range(0, members).Select(i => $"m{i}").ToArray();
        var merger = new StreamMerger(ids, Hold, () => clock.Now);

        var data = new Line[members][][];
        for (var mi = 0; mi < members; mi++)
        {
            data[mi] = new Line[batches][];
            for (var b = 0; b < batches; b++)
            {
                var batch = new Line[batchSize];
                for (var i = 0; i < batchSize; i++)
                {
                    var tick = (b * batchSize) + i;
                    batch[i] = new Line("x") { Timestamp = Epoch.AddMilliseconds((tick * 4) + mi) };
                }
                data[mi][b] = batch;
            }
        }

        var total = 0;
        var elapsed = TimeSpan.Zero;
        var allocBefore = GC.GetAllocatedBytesForCurrentThread();
        for (var b = 0; b < batches; b++)
        {
            for (var mi = 0; mi < members; mi++)
            {
                merger.Add(ids[mi], data[mi][b]);
            }
            var sw = Stopwatch.StartNew();
            var released = merger.Release();
            sw.Stop();
            elapsed += sw.Elapsed;
            total += released.Count;
            clock.Advance(TimeSpan.FromMilliseconds(100));
        }
        var sw2 = Stopwatch.StartNew();
        total += merger.FlushAll().Count;
        elapsed += sw2.Elapsed;
        var allocated = GC.GetAllocatedBytesForCurrentThread() - allocBefore;

        output.WriteLine(
            $"perf: {total} lines, Release total {elapsed.TotalMilliseconds:F1} ms, allocated {allocated} bytes"
        );
        Assert.Equal(members * batches * batchSize, total);
        Assert.True(
            elapsed < TimeSpan.FromSeconds(1),
            $"Release took {elapsed.TotalMilliseconds} ms"
        );
        // Output lists plus queue growth only: well under 100 bytes per line.
        Assert.True(allocated < total * 100L, $"allocated {allocated} bytes for {total} lines");
    }
}
