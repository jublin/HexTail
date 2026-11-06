using System.Runtime.CompilerServices;
using Avalonia.Controls;
using Avalonia.VisualTree;

namespace HexTail.Views;

public partial class SettingsPanel : UserControl
{
    private readonly ConditionalWeakTable<Grid, GridLayout> _layouts = new();

    public SettingsPanel()
    {
        InitializeComponent();
        LayoutUpdated += ReflowColumns;
    }

    private void ReflowColumns(object? sender, EventArgs e)
    {
        if (Bounds.Width <= 0)
            return;

        var stack = Bounds.Width < 900;
        // ponytail: scan marked settings grids per layout; revisit if hundreds of cards are added.
        foreach (
            var grid in this.GetVisualDescendants()
                .OfType<Grid>()
                .Where(grid => grid.Classes.Contains("responsive-columns"))
        )
        {
            var original = _layouts.GetValue(
                grid,
                static grid => new GridLayout(
                    grid.RowDefinitions,
                    grid.ColumnDefinitions,
                    grid.RowSpacing,
                    grid.Children.Select(child => new Placement(
                            child,
                            Grid.GetRow(child),
                            Grid.GetColumn(child),
                            Grid.GetRowSpan(child),
                            Grid.GetColumnSpan(child)
                        ))
                        .OrderBy(placement => placement.Row)
                        .ThenBy(placement => placement.Column)
                        .ToArray()
                )
            );
            if (stack == (grid.ColumnDefinitions.Count == 1))
                continue;

            grid.ColumnDefinitions = stack ? new ColumnDefinitions("*") : original.Columns;
            grid.RowDefinitions = stack
                ? new RowDefinitions(
                    string.Join(",", Enumerable.Repeat("Auto", original.Children.Length))
                )
                : original.Rows;
            grid.RowSpacing = stack ? Math.Max(original.RowSpacing, 12) : original.RowSpacing;
            for (var index = 0; index < original.Children.Length; index++)
            {
                var placement = original.Children[index];
                Grid.SetRow(placement.Child, stack ? index : placement.Row);
                Grid.SetColumn(placement.Child, stack ? 0 : placement.Column);
                Grid.SetRowSpan(placement.Child, stack ? 1 : placement.RowSpan);
                Grid.SetColumnSpan(placement.Child, stack ? 1 : placement.ColumnSpan);
            }
        }
    }

    private sealed record GridLayout(
        RowDefinitions Rows,
        ColumnDefinitions Columns,
        double RowSpacing,
        Placement[] Children
    );

    private sealed record Placement(
        Control Child,
        int Row,
        int Column,
        int RowSpan,
        int ColumnSpan
    );
}
