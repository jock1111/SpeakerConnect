using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;

namespace SpeakerConnect.Views;

public partial class DashboardView : UserControl
{
    /// <summary>
    /// Width applied to each speaker card. Computed from the available viewport so
    /// cards fill the row (1–4 columns) instead of leaving dead space.
    /// </summary>
    public static readonly DependencyProperty CardWidthProperty =
        DependencyProperty.Register(
            nameof(CardWidth),
            typeof(double),
            typeof(DashboardView),
            new PropertyMetadata(520.0));

    public double CardWidth
    {
        get => (double)GetValue(CardWidthProperty);
        private set => SetValue(CardWidthProperty, value);
    }

    public DashboardView()
    {
        InitializeComponent();
        CardsScroll.SizeChanged += (_, _) => UpdateCardWidth();
        CardsScroll.ScrollChanged += OnScrollChanged;
        SizeChanged += (_, _) => UpdateCardWidth();
    }

    private void OnScrollChanged(object sender, ScrollChangedEventArgs e) =>
        UpdateCardWidth();

    private void UpdateCardWidth()
    {
        var available = CardsScroll.ViewportWidth;
        if (double.IsNaN(available) || available < 200)
        {
            return;
        }

        // Each card keeps a 16px right margin; aim for ~516px cards so the column
        // count adapts: 1 col under ~700px, 2 under ~1230px, 3 under ~1750px, else 4.
        var columns = Math.Clamp((int)Math.Floor((available + 16) / 516.0), 1, 4);
        var width = available / columns - 16.0;
        CardWidth = Math.Clamp(width, 430.0, 680.0);
    }
}
