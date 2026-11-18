// SPDX-License-Identifier: AGPL-3.0-only

using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using GameTranslator.App.Controls;
using GameTranslator.Translation;

namespace GameTranslator.App.Views;

/// <summary>
/// Oynanan oyunu elle seçme: Otomatik ya da hazır çeviri paketi olan oyunlardan biri. Seçilen oyunun paketi ilk
/// cümleden kullanılır (<see cref="CommunityPatchManager.SelectedGameId"/>). Satıra basınca seçilir ve pencere kapanır.
/// </summary>
public partial class GamePickerWindow : Window
{
    private static readonly CultureInfo Turkish = CultureInfo.GetCultureInfo("tr-TR");
    private readonly IReadOnlyList<(string Id, string Title, int Lines)> games;

    internal GamePickerWindow()
    {
        InitializeComponent();
        ArabicLayout.Apply(this);
        games = CommunityPatchManager.Shared.Games;
        Fill();
        Loaded += (_, _) => Search.Focus();
    }

    /// <summary>Pencereyi açar; seçim değiştiyse true.</summary>
    public static bool Open(Window? owner)
    {
        var before = CommunityPatchManager.Shared.SelectedGameId;
        new GamePickerWindow { Owner = owner }.ShowDialog();
        return CommunityPatchManager.Shared.SelectedGameId != before;
    }

    private void Fill()
    {
        var query = Search.Text.Trim();
        SearchHint.Visibility = query.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        var selected = CommunityPatchManager.Shared.SelectedGameId;
        List.Children.Clear();
        if (query.Length == 0)
            Add(null, "Otomatik", "تُعرف اللعبة من أول بضع جمل (مستحسن)", selected is null);
        foreach (var (id, title, lines) in games)
        {
            if (query.Length > 0 && !Turkish.CompareInfo.IsPrefix(title, query, CompareOptions.IgnoreCase) &&
                Turkish.CompareInfo.IndexOf(title, query, CompareOptions.IgnoreCase) < 0) continue;
            Add(id, title, $"{lines.ToString("N0", Turkish)} جملة جاهزة", id == selected);
        }
        if (List.Children.Count == 0)
            List.Children.Add(new TextBlock
            {
                Text = "لا توجد حزمة لعبة بهذا الاسم. في الألعاب التي ليس لها حزمة ابقَ على «تلقائي»؛ خدمة الترجمة تترجم.",
                Style = (Style)FindResource("HintText"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(12),
            });
    }

    private void Add(string? id, string title, string detail, bool isSelected)
    {
        if (List.Children.Count > 0)
            List.Children.Add(new Rectangle { Height = 1, Fill = (Brush)FindResource("hairlineBrush"), Margin = new Thickness(10, 0, 10, 0) });
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var text = new StackPanel();
        text.Children.Add(new TextBlock { Text = title, FontSize = 15, FontWeight = isSelected ? FontWeights.SemiBold : FontWeights.Normal });
        text.Children.Add(new TextBlock { Text = detail, FontSize = 12, Foreground = (Brush)FindResource("textSecondaryBrush"), Margin = new Thickness(0, 2, 0, 0) });
        grid.Children.Add(text);
        if (isSelected)
        {
            var check = new TextBlock
            {
                Text = Glyphs.Check, Style = (Style)FindResource("GlyphText"), FontSize = 16,
                Foreground = (Brush)FindResource("accentBrush"), VerticalAlignment = VerticalAlignment.Center,
            };
            Grid.SetColumn(check, 1);
            grid.Children.Add(check);
        }
        var row = new Button { Style = (Style)FindResource("RowButton"), Content = grid, HorizontalContentAlignment = HorizontalAlignment.Stretch };
        row.Click += (_, _) =>
        {
            CommunityPatchManager.Shared.SelectedGameId = id;
            Close();
        };
        List.Children.Add(row);
    }

    private void OnSearch(object sender, TextChangedEventArgs e) => Fill();

    private void OnClose(object sender, RoutedEventArgs e) => Close();
}
