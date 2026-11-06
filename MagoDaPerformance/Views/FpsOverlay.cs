using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace MagoDaPerformance;

public sealed class FpsOverlay : Window
{
    private readonly TextBlock _text;

    public FpsOverlay()
    {
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        Topmost = true;
        ShowInTaskbar = false;
        ResizeMode = ResizeMode.NoResize;
        SizeToContent = SizeToContent.WidthAndHeight;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Left = SystemParameters.PrimaryScreenWidth - 220;
        Top = 40;

        _text = new TextBlock
        {
            Text = "FPS --",
            FontFamily = new FontFamily("Consolas"),
            FontSize = 22,
            FontWeight = FontWeights.Bold,
            Foreground = Ui.Br(Ui.Green),
            Margin = new Thickness(14, 8, 14, 8),
        };

        var border = new Border
        {
            Background = Ui.Br(Color.FromRgb(0x05, 0x07, 0x0B), 0.82),
            BorderBrush = Ui.Br(Ui.Green),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Child = _text,
            Cursor = Cursors.SizeAll,
        };
        border.MouseLeftButtonDown += (_, e) => { if (e.ButtonState == MouseButtonState.Pressed) DragMove(); };
        Content = border;
    }

    public void UpdateFps(double fps)
    {
        _text.Text = fps > 0 ? $"FPS {fps:0}" : "FPS --";
        _text.Foreground = Ui.Br(fps >= 60 ? Ui.Green : fps >= 30 ? Ui.Gold : Ui.Red);
    }
}
