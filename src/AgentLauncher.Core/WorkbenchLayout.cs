namespace AgentLauncher.Core;

public enum MainWindowMode
{
    StartPage,
    Workbench
}

public enum WorkbenchPaneStatus
{
    Starting,
    Running,
    Waiting,
    Pressure,
    Exited
}

public readonly record struct WorkbenchTile(int Index, double X, double Y, double Width, double Height);

public sealed record WorkbenchLayoutResult(int Columns, int Rows, bool SingleColumn, IReadOnlyList<WorkbenchTile> Tiles);

public static class WorkbenchLayout
{
    public const int MaxPanes = 6;
    public const int MinPaneWidth = 420;
    public const int MinPaneHeight = 240;
    public const double DefaultGap = 8;

    public static (int Columns, int Rows) GridFor(int paneCount)
    {
        if (paneCount is < 0 or > MaxPanes) throw new ArgumentOutOfRangeException(nameof(paneCount));
        return paneCount switch
        {
            0 => (0, 0),
            1 => (1, 1),
            2 => (2, 1),
            <= 4 => (2, 2),
            _ => (2, 3)
        };
    }

    public static bool UseSingleColumn(double width, int paneCount, double gap = DefaultGap)
    {
        if (paneCount <= 0) return false;
        if (paneCount == 1) return true;
        return width < MinPaneWidth * 2 + gap;
    }

    public static WorkbenchLayoutResult Layout(double width, double height, int paneCount, double gap = DefaultGap)
    {
        if (paneCount is < 0 or > MaxPanes) throw new ArgumentOutOfRangeException(nameof(paneCount));
        if (paneCount == 0) return new WorkbenchLayoutResult(0, 0, false, Array.Empty<WorkbenchTile>());
        var single = UseSingleColumn(width, paneCount, gap);
        if (single)
        {
            var available = height - gap * (paneCount - 1);
            var even = available / paneCount;
            var tileHeight = even < MinPaneHeight ? MinPaneHeight : even;
            var tiles = new WorkbenchTile[paneCount];
            for (var i = 0; i < paneCount; i++)
                tiles[i] = new WorkbenchTile(i, 0, i * (tileHeight + gap), width, tileHeight);
            return new WorkbenchLayoutResult(1, paneCount, true, tiles);
        }

        var (columns, rows) = GridFor(paneCount);
        var tileWidth = (width - gap * (columns - 1)) / columns;
        var tileHeightWide = (height - gap * (rows - 1)) / rows;
        var tilesWide = new List<WorkbenchTile>(paneCount);
        for (var i = 0; i < paneCount; i++)
        {
            var row = i / columns;
            var column = i % columns;
            tilesWide.Add(new WorkbenchTile(i, column * (tileWidth + gap), row * (tileHeightWide + gap), tileWidth, tileHeightWide));
        }
        return new WorkbenchLayoutResult(columns, rows, false, tilesWide);
    }

    public static MainWindowMode AfterLaunchAttempt(int startedCount) =>
        startedCount >= 1 ? MainWindowMode.Workbench : MainWindowMode.StartPage;

    public static MainWindowMode AfterAllPanesExited() => MainWindowMode.Workbench;

    public static bool ShowBackToStart(MainWindowMode mode, int runningOrStartingCount) =>
        mode == MainWindowMode.Workbench && runningOrStartingCount == 0;
}