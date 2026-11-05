using System.Drawing;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using MouseWithoutBorders.Core;

namespace MouseWithoutBorders.UnitTests.Core;

[TestClass]
public sealed class MonitorLayoutTests
{
    private static readonly MonitorPlacement Main1 = new(new(1, "Main1"), new(0, 0, 2560, 1440), new(0, 0));
    private static readonly MonitorPlacement Game = new(new(2, "Game1"), new(0, 0, 2560, 1440), new(2560, 0));
    private static readonly MonitorPlacement Main2 = new(new(1, "Main2"), new(-8, -1440, 2560, 1440), new(5120, 0));

    [TestMethod]
    public void MainGameMainUsesWorldNeighboursAndNegativeNativeArrivalBothWays()
    {
        var layout = new MonitorLayout([Main1, Game, Main2]);
        AssertPosition(layout.Route(Main1.Key, new(2558, 720), new(2559, 720)), Game, new(0, 720));
        AssertPosition(layout.Route(Game.Key, new(2558, 720), new(2559, 720)), Main2, new(-8, -720));
        AssertPosition(layout.Route(Main2.Key, new(-7, -720), new(-8, -720)), Game, new(2559, 720));
        AssertPosition(layout.Route(Game.Key, new(1, 720), new(0, 720)), Main1, new(2559, 720));
        AssertPosition(layout.Route(Main2.Key, new(90, -750), new(100, -740)), Main2, new(100, -740));
    }

    [TestMethod]
    public void WindowsNeighbourAboveMain1DoesNotBecomeItsWorldNeighbour()
    {
        var layout = new MonitorLayout([Main1, Game, Main2]);
        AssertPosition(layout.Route(Main1.Key, new(100, 1), new(100, -1)), Main1, new(100, 0));
    }

    [TestMethod]
    public void SamePeerWorldNeighbourWarpsInsteadOfFollowingWindowsLayout()
    {
        var next = Main2 with { WorldOrigin = new(2560, 0) };
        var layout = new MonitorLayout([Main1, next]);
        AssertPosition(layout.Route(Main1.Key, new(2558, 720), new(2559, 720)), next, new(-8, -720));
    }

    [TestMethod]
    public void BottomLastPixelAndTopEdgeReachTheirSharedInterval()
    {
        var below = new MonitorPlacement(new(3, "Truman1"), new(-1920, 200, 1920, 1080), new(320, 1440));
        var layout = new MonitorLayout([Main1, below]);
        AssertPosition(layout.Route(Main1.Key, new(500, 1438), new(500, 1439)), below, new(-1740, 200));
        AssertPosition(layout.Route(below.Key, new(-1740, 201), new(-1740, 200)), Main1, new(500, 1439));
        AssertPosition(layout.Route(Main1.Key, new(100, 1438), new(100, 1445)), Main1, new(100, 1439));
    }

    [TestMethod]
    [DataRow(0, 100, 98, 98, 99, 99, 99, 0)]
    [DataRow(100, 0, 98, 98, 99, 99, 0, 99)]
    [DataRow(0, -100, 1, 1, 0, 0, 0, 99)]
    [DataRow(-100, 0, 1, 1, 0, 0, 99, 0)]
    public void SimultaneousEdgesUseTheAvailableSharedEdge(int worldX, int worldY,
        int previousX, int previousY, int nextX, int nextY, int expectedX, int expectedY)
    {
        var first = new MonitorPlacement(new(1, "A"), new(0, 0, 100, 100), new(0, 0));
        var next = new MonitorPlacement(new(2, "B"), new(0, 0, 100, 100), new(worldX, worldY));
        AssertPosition(new MonitorLayout([first, next]).Route(first.Key, new(previousX, previousY), new(nextX, nextY)),
            next, new(expectedX, expectedY));
    }

    [TestMethod]
    public void AnEarlierBlockedEdgeCannotFallThroughToALaterEdge()
    {
        var first = new MonitorPlacement(new(1, "A"), new(0, 0, 100, 100), new(0, 0));
        var below = new MonitorPlacement(new(2, "B"), new(0, 0, 100, 100), new(0, 100));
        AssertPosition(new MonitorLayout([first, below]).Route(first.Key, new(90, 90), new(120, 110)), first, new(99, 99));
    }

    [TestMethod]
    public void GapsAndCornerContactCannotAuthorizeADiagonalJump()
    {
        var gap = Game with { WorldOrigin = new(2561, 0) };
        AssertPosition(new MonitorLayout([Main1, gap]).Route(Main1.Key, new(2558, 720), new(2570, 720)), Main1, new(2559, 720));
        var corner = Game with { WorldOrigin = new(2560, 1440) };
        AssertPosition(new MonitorLayout([Main1, corner]).Route(Main1.Key, new(2558, 1438), new(2565, 1445)), Main1, new(2559, 1439));
    }

    [TestMethod]
    public void LargeMoveCrossesEverySharedEdgeInsteadOfSkippingAMonitor()
    {
        var layout = new MonitorLayout([Main1, Game, Main2]);
        AssertPosition(layout.Route(Main1.Key, new(2500, 720), new(5200, 720)), Main2, new(72, -720));
        AssertPosition(layout.Route(Main2.Key, new(72, -720), new(-2700, -720)), Main1, new(2428, 720));
        var gap = Main2 with { WorldOrigin = new(5121, 0) };
        AssertPosition(new MonitorLayout([Main1, Game, gap]).Route(Main1.Key, new(2500, 720), new(5300, 720)), Game, new(2559, 720));
    }

    [TestMethod]
    [DataRow(false, false)]
    [DataRow(false, true)]
    [DataRow(true, false)]
    [DataRow(true, true)]
    public void ALargeMoveEndingOnTheNextEdgeMatchesSplitMoves(bool vertical, bool reverse)
    {
        Point At(int value) => vertical ? new(50, value) : new(value, 50);
        var monitors = Enumerable.Range(0, 3).Select(i => new MonitorPlacement(new((uint)i + 1, $"Screen{i}"),
            new(0, 0, 100, 100), vertical ? new(0, i * 100) : new(i * 100, 0))).ToArray();
        var layout = new MonitorLayout(monitors);
        var source = monitors[reverse ? 2 : 0];
        var expected = monitors[reverse ? 0 : 2];
        var direct = layout.Route(source.Key, At(reverse ? 10 : 90), At(reverse ? -100 : 199));
        var middle = layout.Route(source.Key, At(reverse ? 10 : 90), At(reverse ? -90 : 110));
        var split = layout.Route(middle.Monitor.Key, middle.NativePoint, At(reverse ? 0 : 99));
        AssertPosition(direct, expected, At(reverse ? 99 : 0));
        Assert.AreEqual(direct, split);
    }

    [DataTestMethod]
    [DataRow(false, false)]
    [DataRow(false, true)]
    [DataRow(true, false)]
    [DataRow(true, true)]
    public void AForbiddenIntermediatePeerStopsLargeAndSplitMoves(bool samePeerAtBothEnds, bool reverse)
    {
        var first = new MonitorPlacement(new(1, "A"), new(0, 0, 100, 100), new(0, 0));
        var middle = new MonitorPlacement(new(2, "B"), new(0, 0, 100, 100), new(100, 0));
        var last = new MonitorPlacement(new(samePeerAtBothEnds ? 1u : 3u, "C"), new(200, 0, 100, 100), new(200, 0));
        var layout = new MonitorLayout([first, middle, last]);
        var source = reverse ? last : first;
        var visited = new List<uint>();
        bool Allow(uint peer) { visited.Add(peer); return peer != 2; }
        Point At(int x) => new(source.NativeBounds.Left + x, 50);
        var direct = layout.Route(source.Key, At(reverse ? 10 : 90), At(reverse ? -149 : 249), Allow);
        AssertPosition(direct, source, At(reverse ? 0 : 99));
        CollectionAssert.AreEqual(new uint[] { 2 }, visited, "The denied first boundary must stop traversal before another peer is queried.");
        var split = layout.Route(source.Key, At(reverse ? 10 : 90), At(reverse ? -10 : 110), Allow);
        split = layout.Route(split.Monitor.Key, split.NativePoint, new(split.NativePoint.X + (reverse ? -139 : 139), 50), Allow);
        Assert.AreEqual(direct, split);
    }

    [TestMethod]
    public void SamePeerMonitorBoundaryDoesNotRequirePeerEntryPermission()
    {
        var next = Main2 with { WorldOrigin = new(2560, 0) };
        int queries = 0;
        bool Deny(uint peer) { queries++; return false; }
        AssertPosition(new MonitorLayout([Main1, next]).Route(Main1.Key, new(2558, 720), new(2559, 720), Deny), next, new(-8, -720));
        Assert.AreEqual(0, queries);
    }

    [TestMethod]
    public void AnEndpointInsideANeighbourCannotSkipTheUnsharedCrossing()
    {
        var first = new MonitorPlacement(new(1, "A"), new(0, 0, 100, 100), new(0, 0));
        var next = new MonitorPlacement(new(2, "B"), new(0, 0, 100, 100), new(100, 50));
        AssertPosition(new MonitorLayout([first, next]).Route(first.Key, new(90, 0), new(120, 100)), first, new(99, 99));
    }

    [TestMethod]
    public void AnArrivalEchoOrParallelMoveDoesNotBounceToThePreviousMonitor()
    {
        var layout = new MonitorLayout([Main1, Game]);
        AssertPosition(layout.Route(Game.Key, new(0, 720), new(0, 720)), Game, new(0, 720));
        AssertPosition(layout.Route(Game.Key, new(0, 720), new(0, 725)), Game, new(0, 725));
    }

    [TestMethod]
    public void AmbiguousAppliedLayoutsAreRejectedOnceAtConstruction()
    {
        Assert.ThrowsException<ArgumentException>(() => new MonitorLayout([Main1, Main1]));
        Assert.ThrowsException<ArgumentException>(() => new MonitorLayout([Main1, Game with { WorldOrigin = new(2559, 0) }]));
        Assert.ThrowsException<ArgumentException>(() => new MonitorLayout([Main1 with { NativeBounds = new(0, 0, 0, 1440) }]));
        Assert.ThrowsException<OverflowException>(() => new MonitorLayout([Main1 with { WorldOrigin = new(int.MaxValue, 0) }]));
    }

    private static void AssertPosition(MonitorPosition actual, MonitorPlacement expected, Point point)
    {
        Assert.AreEqual(expected.Key, actual.Monitor.Key);
        Assert.AreEqual(point, actual.NativePoint);
    }
}
