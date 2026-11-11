using System.Globalization;

namespace AgentLauncher.Core;

public sealed record PaneCell(int EnabledIndex,int CreationOrder,int Column,int Row,int ColumnsInRow,int RowsInColumn);

public static class PaneLayout
{
    public static int Columns(int n,string twoPaneLayout)
    {
        if(n is <1 or >ResourcePolicy.MaxSlots)throw new ArgumentOutOfRangeException(nameof(n),"窗格数必须为 1～16。");
        return n switch{1=>1,2 when twoPaneLayout=="horizontal"=>1,<=4=>2,_=>4};
    }

    public static int[] RowsPerColumn(int n,string twoPaneLayout)
    {
        var columns=Columns(n,twoPaneLayout);if(n==3)return [1,2];
        var rows=new int[columns];for(var column=0;column<columns;column++)rows[column]=n/columns+(column<n%columns?1:0);return rows;
    }

    public static IReadOnlyList<PaneCell> Map(int n,string twoPaneLayout)
    {
        var rows=RowsPerColumn(n,twoPaneLayout);var cells=new PaneCell[n];var enabled=0;var nextCreation=rows.Length;
        for(var column=0;column<rows.Length;column++)for(var row=0;row<rows[column];row++)
        {
            cells[enabled]=new(enabled,row==0?column:nextCreation++,column,row,rows.Length,rows[column]);enabled++;
        }
        return cells;
    }

    public static IReadOnlyList<string> SplitSteps(int n,string twoPaneLayout)
    {
        var rows=RowsPerColumn(n,twoPaneLayout);var steps=new List<string>(9*n-4){"new-tab"};
        // WT creates the new pane on the right (-V) or below (-H); --size is its share of the parent.
        for(var column=1;column<rows.Length;column++)Split(column-1,"-V",rows.Length-column+1);
        var nextCreation=rows.Length;
        for(var column=0;column<rows.Length;column++)
        {
            var parent=column;
            for(var remaining=rows[column];remaining>1;remaining--){Split(parent,"-H",remaining);parent=nextCreation++;}
        }
        Focus(0);return steps;

        void Focus(int target)
        {
            steps.Add(";");steps.Add("focus-pane");steps.Add("--target");steps.Add(target.ToString(CultureInfo.InvariantCulture));
        }
        void Split(int parent,string direction,int remaining)
        {
            Focus(parent);steps.Add(";");steps.Add("split-pane");steps.Add(direction);steps.Add("--size");steps.Add(((remaining-1d)/remaining).ToString("R",CultureInfo.InvariantCulture));
        }
    }
}
