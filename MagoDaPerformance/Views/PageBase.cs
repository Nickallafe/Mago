using System.Windows;
using System.Windows.Controls;

namespace MagoDaPerformance;

public abstract class PageBase
{
    protected readonly MainWindow Host;
    protected readonly StackPanel Body = new();
    public FrameworkElement Root { get; }

    protected PageBase(MainWindow host, string title, string subtitle)
    {
        Host = host;
        var stack = new StackPanel();
        stack.Children.Add(Ui.Lbl(title, 20, Ui.Txt, true));
        if (!string.IsNullOrEmpty(subtitle)) stack.Children.Add(Ui.Lbl(subtitle, 11, Ui.Dim));
        Body.Margin = new Thickness(0, 12, 0, 8);
        stack.Children.Add(Body);
        Root = Ui.Scroll(stack);
    }

    /// <summary>Chamado sempre que a pagina passa a estar visivel.</summary>
    public virtual void OnShown() { }

    protected void Add(UIElement element) => Body.Children.Add(element);

    /// <summary>Cartao de largura fixa (WrapPanel) com o corpo para preencher.</summary>
    protected Border Card(string title, double width = 380)
    {
        var card = Ui.CardBox(title, out var body);
        card.Width = width;
        // Sem isto o WrapPanel estica todos os cartoes da linha a altura do mais alto.
        card.VerticalAlignment = VerticalAlignment.Top;
        body.Tag = card;
        return card;
    }

    protected static WrapPanel Row(params UIElement[] items) => Ui.Wrap(items);
}
