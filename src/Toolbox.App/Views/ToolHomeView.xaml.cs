using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Toolbox.App.Tools;

namespace Toolbox.App.Views;

public sealed partial class ToolHomeView : UserControl
{
    internal event Action<ToolId>? ToolSelected;
    public ToolHomeView()
    {
        InitializeComponent();
        foreach (var category in ToolCatalog.All.GroupBy(tool => tool.Category))
        {
            var section = new StackPanel { Spacing = 12 };
            section.Children.Add(new TextBlock { Text = category.Key, FontSize = 15, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
            var grid = new Grid { ColumnSpacing = 16, RowSpacing = 16 };
            grid.ColumnDefinitions.Add(new()); grid.ColumnDefinitions.Add(new());
            var index = 0;
            foreach (var tool in category)
            {
                if (index % 2 == 0) grid.RowDefinitions.Add(new() { Height = GridLength.Auto });
                var icon = new FontIcon { Glyph = tool.Glyph, FontSize = 20,
                    Foreground = new SolidColorBrush(tool.Id == ToolId.FileManager ? ColorHelper.FromArgb(255, 71, 104, 165) : ColorHelper.FromArgb(255, 19, 127, 112)) };
                var content = new StackPanel { Spacing = 12 };
                var heading = new Grid { ColumnSpacing = 12 };
                heading.ColumnDefinitions.Add(new() { Width = GridLength.Auto }); heading.ColumnDefinitions.Add(new()); heading.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
                heading.Children.Add(new Border { Child = icon, Width = 38, Height = 38, CornerRadius = new(10),
                    Background = new SolidColorBrush(tool.Id == ToolId.FileManager ? ColorHelper.FromArgb(255, 236, 241, 251) : ColorHelper.FromArgb(255, 235, 246, 240)) });
                var title = new TextBlock { Text = tool.Title, FontSize = 18, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center };
                Grid.SetColumn(title, 1); heading.Children.Add(title);
                var arrow = new FontIcon { Glyph = "\uE76C", FontSize = 11, Foreground = new SolidColorBrush(ColorHelper.FromArgb(255, 151, 168, 157)) };
                Grid.SetColumn(arrow, 2); heading.Children.Add(arrow);
                content.Children.Add(heading);
                content.Children.Add(new TextBlock { Text = tool.Description, FontSize = 12, TextWrapping = TextWrapping.Wrap,
                    Foreground = new SolidColorBrush(ColorHelper.FromArgb(255, 99, 121, 107)) });
                var button = new Button { Content = content, Padding = new(20), CornerRadius = new(14), MinHeight = 128,
                    HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch,
                    Background = new SolidColorBrush(Colors.White), BorderBrush = new SolidColorBrush(ColorHelper.FromArgb(255, 226, 232, 227)) };
                button.Click += (_, _) => ToolSelected?.Invoke(tool.Id);
                Grid.SetColumn(button, index % 2); Grid.SetRow(button, index / 2);
                grid.Children.Add(button); index++;
            }
            var currentColumns = 2;
            grid.SizeChanged += (_, e) =>
            {
                var columns = e.NewSize.Width < 560 ? 1 : 2;
                if (columns == currentColumns) return;
                currentColumns = columns;
                var rows = (grid.Children.Count + columns - 1) / columns;
                while (grid.RowDefinitions.Count < rows) grid.RowDefinitions.Add(new() { Height = GridLength.Auto });
                while (grid.RowDefinitions.Count > rows) grid.RowDefinitions.RemoveAt(grid.RowDefinitions.Count - 1);
                grid.ColumnDefinitions[1].Width = columns == 1 ? new(0) : new(1, GridUnitType.Star);
                for (var i = 0; i < grid.Children.Count; i++)
                {
                    var card = (FrameworkElement)grid.Children[i];
                    Grid.SetColumn(card, i % columns); Grid.SetRow(card, i / columns);
                }
            };
            section.Children.Add(grid); Sections.Children.Add(section);
        }
    }
}
