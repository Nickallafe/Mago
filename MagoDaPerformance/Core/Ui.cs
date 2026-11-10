using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using System.Windows.Media;

namespace MagoDaPerformance;

/// <summary>Paleta e fabricas de controles (UI montada em codigo, sem XAML).</summary>
public static class Ui
{
    public static readonly Color Bg = Color.FromRgb(0x07, 0x09, 0x0E);
    public static readonly Color Card = Color.FromRgb(0x0E, 0x13, 0x1C);
    public static readonly Color Side = Color.FromRgb(0x0B, 0x0F, 0x16);
    public static readonly Color Line = Color.FromRgb(0x1B, 0x24, 0x30);
    public static readonly Color Txt = Color.FromRgb(0xE6, 0xED, 0xF3);
    public static readonly Color Dim = Color.FromRgb(0x8A, 0x97, 0xA8);
    public static readonly Color Green = Color.FromRgb(0x3D, 0xF5, 0x9A);
    public static readonly Color Cyan = Color.FromRgb(0x5C, 0xD6, 0xFF);
    public static readonly Color Gold = Color.FromRgb(0xFF, 0xC8, 0x5C);
    public static readonly Color Purple = Color.FromRgb(0xA9, 0x7B, 0xFF);
    public static readonly Color Red = Color.FromRgb(0xFF, 0x6B, 0x6B);

    public static readonly FontFamily UiFont = new("Segoe UI");
    public static readonly FontFamily MonoFont = new("Consolas");

    public static SolidColorBrush Br(Color c, double opacity = 1)
        => new(Color.FromArgb((byte)(opacity * 255), c.R, c.G, c.B));

    public static TextBlock Lbl(string text, double size = 12, Color? fg = null, bool bold = false, bool wrap = true)
        => new()
        {
            Text = text,
            FontSize = size,
            FontFamily = UiFont,
            Foreground = Br(fg ?? Txt),
            FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal,
            TextWrapping = wrap ? TextWrapping.Wrap : TextWrapping.NoWrap,
            Margin = new Thickness(0, 2, 0, 2),
        };

    public static TextBlock Mono(string text, double size = 11, Color? fg = null)
        => new()
        {
            Text = text,
            FontSize = size,
            FontFamily = MonoFont,
            Foreground = Br(fg ?? Green),
            TextWrapping = TextWrapping.Wrap,
        };

    public static Button Btn(string text, Action onClick, Color? fg = null, bool primary = false)
    {
        var b = new Button
        {
            Content = text,
            Foreground = Br(fg ?? (primary ? Color.FromRgb(0x04, 0x0A, 0x07) : Txt)),
            Background = Br(primary ? Green : Color.FromRgb(0x1B, 0x24, 0x30)),
            BorderBrush = Br(primary ? Green : Line),
            Padding = new Thickness(14, 8, 14, 8),
            MinHeight = 34,
            Margin = new Thickness(0, 0, 8, 8),
            FontFamily = UiFont,
            FontSize = 12,
            FontWeight = FontWeights.SemiBold,
            Cursor = System.Windows.Input.Cursors.Hand,
            Template = ButtonTemplate(),
        };
        b.Click += (_, _) => { try { onClick(); } catch (Exception ex) { Log.Write(ex.Message, "ERRO"); } };
        b.MouseEnter += (_, _) => b.Opacity = 0.88;
        b.MouseLeave += (_, _) => b.Opacity = 1.0;
        return b;
    }

    private static ControlTemplate ButtonTemplate()
    {
        var border = new FrameworkElementFactory(typeof(Border));
        border.SetValue(Border.CornerRadiusProperty, new CornerRadius(6));
        border.SetBinding(Border.BackgroundProperty, new System.Windows.Data.Binding("Background")
        { RelativeSource = System.Windows.Data.RelativeSource.TemplatedParent });
        border.SetBinding(Border.BorderBrushProperty, new System.Windows.Data.Binding("BorderBrush")
        { RelativeSource = System.Windows.Data.RelativeSource.TemplatedParent });
        border.SetValue(Border.BorderThicknessProperty, new Thickness(1));
        // O Padding vai para a Border: no ContentPresenter ele nao deslocava o texto.
        border.SetBinding(Border.PaddingProperty, new System.Windows.Data.Binding("Padding")
        { RelativeSource = System.Windows.Data.RelativeSource.TemplatedParent });
        var presenter = new FrameworkElementFactory(typeof(ContentPresenter));
        presenter.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Center);
        presenter.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        border.AppendChild(presenter);

        return new ControlTemplate(typeof(Button)) { VisualTree = border };
    }

    /// <summary>Cartao com titulo; o corpo recebe os controles.</summary>
    /// <summary>
    /// Cartao com largura preferida mas que encolhe quando a janela e' estreita. Antes
    /// usava Width fixo: a 900 DIPs (1366x768 a 150%) os cartoes de 970 estouravam a pagina.
    /// </summary>
    public sealed class ResponsiveCard : Border
    {
        public double PreferredWidth { get; set; } = 380;

        protected override Size MeasureOverride(Size constraint)
        {
            var chromeWidth = Padding.Left + Padding.Right + BorderThickness.Left + BorderThickness.Right;
            var chromeHeight = Padding.Top + Padding.Bottom + BorderThickness.Top + BorderThickness.Bottom;

            var maxWidth = double.IsInfinity(constraint.Width)
                ? PreferredWidth
                : Math.Min(PreferredWidth, constraint.Width);
            var innerWidth = Math.Max(0, maxWidth - chromeWidth);
            var innerHeight = double.IsInfinity(constraint.Height)
                ? double.PositiveInfinity
                : Math.Max(0, constraint.Height - chromeHeight);

            Child?.Measure(new Size(innerWidth, innerHeight));

            var contentWidth = (Child?.DesiredSize.Width ?? 0) + chromeWidth;
            // Nunca mais estreito do que o conteudo nem mais largo do que o espaco disponivel.
            var width = Math.Min(maxWidth, Math.Max(contentWidth, maxWidth));
            var height = (Child?.DesiredSize.Height ?? 0) + chromeHeight;
            return new Size(width, height);
        }
    }

    public static ResponsiveCard ResponsiveCardBox(string title, double preferredWidth, out StackPanel body)
    {
        var border = CardBox(title, out body);
        // O filho tem de ser desligado do Border temporario antes de mudar de pai.
        var content = border.Child;
        border.Child = null;

        return new ResponsiveCard
        {
            PreferredWidth = preferredWidth,
            Background = border.Background,
            BorderBrush = border.BorderBrush,
            BorderThickness = border.BorderThickness,
            CornerRadius = border.CornerRadius,
            Padding = border.Padding,
            Margin = border.Margin,
            Child = content,
        };
    }

    public static Border CardBox(string title, out StackPanel body)
    {
        body = new StackPanel { Margin = new Thickness(0) };
        var stack = new StackPanel();
        stack.Children.Add(new TextBlock
        {
            Text = title,
            FontSize = 11,
            FontWeight = FontWeights.Bold,
            FontFamily = UiFont,
            Foreground = Br(Cyan),
            Margin = new Thickness(0, 0, 0, 8),
        });
        stack.Children.Add(body);
        return new Border
        {
            Background = Br(Card),
            BorderBrush = Br(Line),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(14),
            Margin = new Thickness(0, 0, 12, 12),
            Child = stack,
        };
    }

    public static StackPanel Row(params UIElement[] items)
    {
        var p = new StackPanel { Orientation = Orientation.Horizontal };
        foreach (var i in items) p.Children.Add(i);
        return p;
    }

    public static WrapPanel Wrap(params UIElement[] items)
    {
        var p = new WrapPanel { Orientation = Orientation.Horizontal };
        foreach (var i in items) p.Children.Add(i);
        return p;
    }

    public static TextBox Input(string placeholder = "", double width = 320)
    {
        var tb = new TextBox
        {
            Width = width,
            Padding = new Thickness(8, 6, 8, 6),
            Background = Br(Color.FromRgb(0x05, 0x07, 0x0B)),
            Foreground = Br(Txt),
            BorderBrush = Br(Line),
            CaretBrush = Br(Green),
            FontFamily = UiFont,
            Margin = new Thickness(0, 0, 8, 8),
            ToolTip = placeholder,
        };
        return tb;
    }

    public static ListBox List(double height = 220)
    {
        var list = new ListBox
        {
            Height = height,
            Background = Br(Color.FromRgb(0x05, 0x07, 0x0B)),
            Foreground = Br(Txt),
            BorderBrush = Br(Line),
            FontFamily = UiFont,
            FontSize = 12,
            Margin = new Thickness(0, 4, 0, 8),
        };
        // Sem barra horizontal: ela aparece branca no tema escuro e corta o texto.
        ScrollViewer.SetHorizontalScrollBarVisibility(list, ScrollBarVisibility.Disabled);
        return list;
    }

    /// <summary>Lista de texto com quebra de linha e tooltip (caminhos longos ficam legiveis).</summary>
    public static ListBox TextList(double height = 220)
    {
        var list = List(height);
        var text = new FrameworkElementFactory(typeof(TextBlock));
        text.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding());
        text.SetBinding(FrameworkElement.ToolTipProperty, new System.Windows.Data.Binding());
        text.SetValue(TextBlock.TextWrappingProperty, TextWrapping.Wrap);
        list.ItemTemplate = new DataTemplate { VisualTree = text };
        return list;
    }

    /// <summary>
    /// Slider escuro do tema (trilho Line, preenchimento e polegar Green), pronto para
    /// claro/escuro do sistema sem depender do visual padrao do Windows.
    /// </summary>
    public static Style DarkSlider { get; } = BuildDarkSlider();

    private static Style BuildDarkSlider()
    {
        var line = Line.ToString();
        var green = Green.ToString();
        var bg = Bg.ToString();

        var xaml = $$"""
<ControlTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                 xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
                 TargetType="{x:Type Slider}">
  <Grid Height="22" Background="Transparent">
    <Border Height="4" CornerRadius="2" Background="{{line}}" VerticalAlignment="Center" />
    <Track x:Name="PART_Track">
      <Track.DecreaseRepeatButton>
        <RepeatButton Command="{x:Static Slider.DecreaseLarge}" Focusable="False">
          <RepeatButton.Template>
            <ControlTemplate TargetType="{x:Type RepeatButton}">
              <Border Height="4" CornerRadius="2" Background="{{green}}" VerticalAlignment="Center" />
            </ControlTemplate>
          </RepeatButton.Template>
        </RepeatButton>
      </Track.DecreaseRepeatButton>
      <Track.Thumb>
        <Thumb Width="14" Height="14" Cursor="Hand">
          <Thumb.Template>
            <ControlTemplate TargetType="{x:Type Thumb}">
              <Ellipse Fill="{{green}}" Stroke="{{bg}}" StrokeThickness="2" />
            </ControlTemplate>
          </Thumb.Template>
        </Thumb>
      </Track.Thumb>
      <Track.IncreaseRepeatButton>
        <RepeatButton Command="{x:Static Slider.IncreaseLarge}" Focusable="False">
          <RepeatButton.Template>
            <ControlTemplate TargetType="{x:Type RepeatButton}">
              <Border Height="22" Background="Transparent" />
            </ControlTemplate>
          </RepeatButton.Template>
        </RepeatButton>
      </Track.IncreaseRepeatButton>
    </Track>
  </Grid>
</ControlTemplate>
""";

        return new Style(typeof(Slider))
        {
            Setters =
            {
                new Setter(Control.TemplateProperty, (ControlTemplate)XamlReader.Parse(xaml)),
            },
        };
    }

    public static ScrollViewer Scroll(UIElement content) => new()
    {
        Content = content,
        // Fundo explicito: sem isto a pagina fica transparente (a janela pinta por tras).
        Background = Br(Bg),
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        // Disabled: a pagina fica limitada a largura visivel. Os cartoes encolhem (ResponsiveCard)
        // e o WrapPanel passa a linha para baixo, em vez de aparecer barra horizontal.
        HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
        Padding = new Thickness(2, 2, 10, 2),
    };
}
