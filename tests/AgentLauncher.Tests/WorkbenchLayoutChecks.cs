using AgentLauncher.Core;

namespace AgentLauncher.Tests;

internal static class WorkbenchLayoutChecks
{
    public static void Run(Action<string, Action> Test)
    {
        void Assert(bool value, string message) { if (!value) throw new Exception(message); }

        Test("workbench grid maps 1-6 panes and rejects 7", () =>
        {
            Assert(WorkbenchLayout.GridFor(0) == (0, 0), "empty");
            Assert(WorkbenchLayout.GridFor(1) == (1, 1), "1");
            Assert(WorkbenchLayout.GridFor(2) == (2, 1), "2 wide");
            Assert(WorkbenchLayout.GridFor(3) == (2, 2), "3");
            Assert(WorkbenchLayout.GridFor(4) == (2, 2), "4");
            Assert(WorkbenchLayout.GridFor(5) == (2, 3), "5");
            Assert(WorkbenchLayout.GridFor(6) == (2, 3), "6");
            try { WorkbenchLayout.GridFor(7); throw new Exception("7 should throw"); }
            catch (ArgumentOutOfRangeException) { }
            try { WorkbenchLayout.GridFor(-1); throw new Exception("-1 should throw"); }
            catch (ArgumentOutOfRangeException) { }
        });

        Test("narrow width falls back to a scrolling single column", () =>
        {
            Assert(!WorkbenchLayout.UseSingleColumn(420 * 2 + 8, 4), "wide 4");
            Assert(WorkbenchLayout.UseSingleColumn(420 * 2 + 8 - 1, 4), "just too narrow");
            Assert(WorkbenchLayout.UseSingleColumn(2000, 1), "one pane is a single cell");
            Assert(!WorkbenchLayout.UseSingleColumn(2000, 0), "empty is not a column");
            var narrow = WorkbenchLayout.Layout(500, 400, 3);
            Assert(narrow.SingleColumn && narrow.Columns == 1 && narrow.Rows == 3 && narrow.Tiles.Count == 3, "3 stacked");
            Assert(narrow.Tiles.All(t => t.Width == 500 && t.Height >= WorkbenchLayout.MinPaneHeight), "min height and full width");
            Assert(narrow.Tiles[0].Y == 0 && narrow.Tiles[1].Y == narrow.Tiles[0].Height + 8, "stacked with gap");
            Assert(narrow.Tiles[0].Height + 8 + narrow.Tiles[1].Height + 8 + narrow.Tiles[2].Height > 400, "overflows so the host can scroll");
        });

        Test("wide layout packs 5 panes as 2x3 without stretching the last cell", () =>
        {
            var wide = WorkbenchLayout.Layout(1000, 800, 5);
            Assert(!wide.SingleColumn && wide.Columns == 2 && wide.Rows == 3 && wide.Tiles.Count == 5, "2x3");
            Assert(wide.Tiles[0].X == 0 && wide.Tiles[1].X > wide.Tiles[0].X, "row-major");
            Assert(wide.Tiles[4].X == 0 && wide.Tiles[4].Width == wide.Tiles[0].Width, "last cell stays one column wide");
            Assert(wide.Tiles.All(t => t.Width > 0 && t.Height > 0), "positive sizes");
        });

        Test("two panes are side by side when wide and stacked when narrow", () =>
        {
            var side = WorkbenchLayout.Layout(1200, 600, 2);
            Assert(!side.SingleColumn && side.Tiles.Count == 2 && side.Tiles[0].Y == side.Tiles[1].Y, "side by side");
            var stacked = WorkbenchLayout.Layout(500, 800, 2);
            Assert(stacked.SingleColumn && stacked.Tiles[0].X == stacked.Tiles[1].X && stacked.Tiles[1].Y > stacked.Tiles[0].Y, "stacked");
        });

        Test("zero panes layout is empty and window mode stays on the workbench after exits", () =>
        {
            var empty = WorkbenchLayout.Layout(800, 600, 0);
            Assert(empty.Tiles.Count == 0 && empty.Columns == 0 && empty.Rows == 0 && !empty.SingleColumn, "empty result");
            Assert(WorkbenchLayout.AfterLaunchAttempt(0) == MainWindowMode.StartPage, "launch none");
            Assert(WorkbenchLayout.AfterLaunchAttempt(1) == MainWindowMode.Workbench, "launch one");
            Assert(WorkbenchLayout.AfterAllPanesExited() == MainWindowMode.Workbench, "stay");
            Assert(WorkbenchLayout.ShowBackToStart(MainWindowMode.Workbench, 0), "offer return");
            Assert(!WorkbenchLayout.ShowBackToStart(MainWindowMode.Workbench, 2), "still running");
            Assert(!WorkbenchLayout.ShowBackToStart(MainWindowMode.StartPage, 0), "already there");
        });
    }
}