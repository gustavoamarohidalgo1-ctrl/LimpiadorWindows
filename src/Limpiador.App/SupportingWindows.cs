using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;

namespace Limpiador.App;

internal static class DialogLayout
{
    public static void Configure(Window window, string title, double width = 760, double height = 620)
    {
        window.Title = title;
        window.Width = width; window.Height = height;
        WindowLayout.Attach(window, minimumContentHeight: 400, minimumContentWidth: 460);
        window.WindowStartupLocation = WindowStartupLocation.CenterOwner;
    }
    public static TextBlock Text(string text, double fontSize = 14, bool bold = false) => new()
    {
        Text = text, TextWrapping = TextWrapping.Wrap, FontSize = fontSize,
        FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal,
        Margin = new Thickness(0, 0, 0, 10)
    };
    public static Grid Grid(params GridLength[] heights)
    {
        var grid = new Grid { Margin = new Thickness(26) };
        foreach (var height in heights) grid.RowDefinitions.Add(new RowDefinition { Height = height });
        return grid;
    }
    public static void Add(Grid grid, UIElement element, int row) { System.Windows.Controls.Grid.SetRow(element, row); grid.Children.Add(element); }
    public static Button Button(string text, RoutedEventHandler onClick)
    {
        var button = new Button { Content = text }; button.Click += onClick; return button;
    }
}

public sealed class MaximumWindow : Window
{
    private readonly List<(CheckBox Choice, ItemViewModel[] Items)> _groups = [];
    public IReadOnlyList<ItemViewModel> ChosenItems => _groups.Where(x => x.Choice.IsChecked == true).SelectMany(x => x.Items).Distinct().ToArray();
    public MaximumWindow(IReadOnlyList<ItemViewModel> items)
    {
        DialogLayout.Configure(this, "Liberar al máximo: elegir los costes");
        var grid = DialogLayout.Grid(GridLength.Auto, GridLength.Auto, new(1, GridUnitType.Star), GridLength.Auto);
        Content = grid;
        DialogLayout.Add(grid, DialogLayout.Text("Elige hasta dónde quieres llegar", 25, true), 0);
        DialogLayout.Add(grid, DialogLayout.Text("Cada grupo muestra qué tendrás que volver a descargar, reconstruir o revisar. Las casillas solo añaden elementos a tu selección. Se respetan los elementos que desmarcaste manualmente; puedes volver a marcarlos en la lista o con «Marcar seguros»."), 1);
        var groupsPanel = new StackPanel();
        var scroll = new ScrollViewer { Content = groupsPanel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Margin = new(0, 8, 0, 18) };
        DialogLayout.Add(grid, scroll, 2);
        foreach (var group in items.GroupBy(x => x.Item.Recommended ? "Selección recomendada" : x.Item.MaximumGroup ?? "Otros elementos con costes").OrderBy(x => x.Key))
        {
            var elements = group.ToArray();
            var content = new StackPanel();
            content.Children.Add(DialogLayout.Text($"{group.Key} · {DisplaySize.Bytes(elements.Sum(x => x.Item.SizeBytes))}", 16, true));
            content.Children.Add(DialogLayout.Text($"{elements.Length:N0} elementos. " + string.Join("; ", elements.SelectMany(x => x.Item.Costs).Distinct().DefaultIfEmpty("Sin costes adicionales indicados.")), 13));
            var names = new Expander { Header = "Ver elementos", Margin = new(0, 0, 0, 8) };
            names.Content = new TextBox { Text = string.Join(Environment.NewLine, elements.Select(x => $"{x.Item.Name} — {x.Item.SizeText} — {x.Item.ActionLabel}")), IsReadOnly = true, TextWrapping = TextWrapping.Wrap, MaxHeight = 180, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
            content.Children.Add(names);
            var choice = new CheckBox { Content = "Añadir este grupo a mi selección", IsChecked = false };
            System.Windows.Automation.AutomationProperties.SetName(choice, "Seleccionar grupo: " + group.Key);
            content.Children.Add(choice);
            var card = new Border { Child = content, BorderThickness = new(1), CornerRadius = new(6), Padding = new(14), Margin = new(0, 0, 0, 12) };
            card.SetResourceReference(Border.BorderBrushProperty, "Line");
            card.SetResourceReference(Border.BackgroundProperty, "Surface");
            groupsPanel.Children.Add(card);
            _groups.Add((choice, elements));
        }
        if (_groups.Count == 0) groupsPanel.Children.Add(DialogLayout.Text("No hay grupos seguros disponibles. Los elementos que requieren revisión se marcan individualmente en la lista."));
        var footer = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Right };
        footer.Children.Add(new Button { Content = "Cancelar", IsCancel = true });
        var apply = DialogLayout.Button("Añadir selección", (_, _) => DialogResult = true);
        apply.IsEnabled = _groups.Count > 0;
        footer.Children.Add(apply); DialogLayout.Add(grid, footer, 3);
    }
}

public sealed class HistoryWindow : Window
{
    public UiHistoryEntry? RestoreEntry { get; private set; }
    public HistoryWindow(IReadOnlyList<UiHistoryEntry> entries)
    {
        DialogLayout.Configure(this, "Historial de operaciones", 860, 720);
        var grid = DialogLayout.Grid(GridLength.Auto, GridLength.Auto, new(1, GridUnitType.Star), new(0.8, GridUnitType.Star), GridLength.Auto);
        Content = grid;
        DialogLayout.Add(grid, DialogLayout.Text("Historial y recuperación", 25, true), 0);
        DialogLayout.Add(grid, DialogLayout.Text(entries.Count == 0 ? "Aún no hay operaciones guardadas." : "Selecciona una operación para ver su resultado. Solo se pueden restaurar los elementos que el historial señale como recuperables."), 1);
        var list = new DataGrid { ItemsSource = entries.OrderByDescending(x => x.Date).ToArray(), AutoGenerateColumns = false, IsReadOnly = true, CanUserAddRows = false, CanUserDeleteRows = false, SelectionMode = DataGridSelectionMode.Single, SelectionUnit = DataGridSelectionUnit.FullRow, HeadersVisibility = DataGridHeadersVisibility.Column, Margin = new(0, 8, 0, 12) };
        list.Columns.Add(new DataGridTextColumn { Header = "Fecha", Binding = new Binding(nameof(UiHistoryEntry.DateText)), Width = new DataGridLength(140) });
        list.Columns.Add(new DataGridTextColumn { Header = "Operación", Binding = new Binding(nameof(UiHistoryEntry.Title)), Width = new DataGridLength(1, DataGridLengthUnitType.Star) });
        list.Columns.Add(new DataGridTextColumn { Header = "Resultado", Binding = new Binding(nameof(UiHistoryEntry.Summary)), Width = new DataGridLength(2, DataGridLengthUnitType.Star) });
        System.Windows.Automation.AutomationProperties.SetName(list, "Operaciones guardadas");
        DialogLayout.Add(grid, list, 2);
        var details = new TextBox { IsReadOnly = true, TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Margin = new(0, 0, 0, 18) };
        System.Windows.Automation.AutomationProperties.SetName(details, "Detalles de la operación seleccionada");
        DialogLayout.Add(grid, details, 3);
        var footer = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Right };
        footer.Children.Add(new Button { Content = "Cerrar", IsCancel = true });
        var restore = DialogLayout.Button("Restaurar esta operación", (_, _) =>
        {
            if (list.SelectedItem is UiHistoryEntry { CanRestore: true } entry) { RestoreEntry = entry; DialogResult = true; }
        });
        restore.IsEnabled = false; footer.Children.Add(restore); DialogLayout.Add(grid, footer, 4);
        list.SelectionChanged += (_, _) =>
        {
            if (list.SelectedItem is not UiHistoryEntry entry) return;
            details.Text = entry.Summary + Environment.NewLine + Environment.NewLine + entry.DetailsText;
            restore.IsEnabled = entry.CanRestore;
        };
    }
}

public sealed class ResultWindow : Window
{
    public ResultWindow(string title, UiOperationResult result)
    {
        DialogLayout.Configure(this, title, 760, 540);
        var grid = DialogLayout.Grid(GridLength.Auto, GridLength.Auto, new(1, GridUnitType.Star), GridLength.Auto);
        Content = grid;
        DialogLayout.Add(grid, DialogLayout.Text(title, 25, true), 0);
        DialogLayout.Add(grid, DialogLayout.Text(result.Summary), 1);
        var details = new TextBox { Text = string.Join(Environment.NewLine, result.Details), IsReadOnly = true, TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Margin = new(0, 8, 0, 18) };
        System.Windows.Automation.AutomationProperties.SetName(details, "Resultado y detalles de la operación");
        DialogLayout.Add(grid, details, 2);
        var close = new Button { Content = "Cerrar", IsCancel = true, IsDefault = true, HorizontalAlignment = HorizontalAlignment.Right };
        DialogLayout.Add(grid, close, 3);
    }
}

public sealed class ApplicationCloseWindow : Window
{
    public ApplicationCloseWindow(string itemName, IReadOnlyList<string> applicationNames)
    {
        DialogLayout.Configure(this, "Solicitar cierre de aplicaciones", 660, 460);
        var grid = DialogLayout.Grid(GridLength.Auto, new(1, GridUnitType.Star), GridLength.Auto, GridLength.Auto);
        Content = grid;
        DialogLayout.Add(grid, DialogLayout.Text("Cerrar las aplicaciones asociadas", 24, true), 0);
        var names = applicationNames.Count == 0 ? itemName : string.Join(", ", applicationNames.Distinct(StringComparer.OrdinalIgnoreCase));
        var explanation = DialogLayout.Text($"Aplicaciones: {names}.\n\nSe solicitará el cierre de las aplicaciones asociadas a «{itemName}». Pueden mostrar una ventana para guardar tu trabajo.\n\nSe esperará hasta 20 segundos. Si siguen abiertas, se conservarán sus archivos y tendrás que cerrarlas tú. No se finalizará ningún proceso a la fuerza.\n\nEsta acción no limpia archivos. Después deberás volver a analizar.");
        DialogLayout.Add(grid, new ScrollViewer { Content = explanation, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Margin = new(0, 0, 0, 12) }, 1);
        var acknowledgement = new CheckBox { Content = DialogLayout.Text("He revisado qué aplicaciones se van a cerrar."), Margin = new(0, 0, 0, 18) };
        DialogLayout.Add(grid, acknowledgement, 2);
        var footer = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Right };
        footer.Children.Add(new Button { Content = "Cancelar", IsCancel = true });
        var confirm = DialogLayout.Button("Solicitar cierre", (_, _) => DialogResult = true);
        confirm.IsEnabled = false; confirm.IsDefault = false;
        acknowledgement.Checked += (_, _) => confirm.IsEnabled = true;
        acknowledgement.Unchecked += (_, _) => confirm.IsEnabled = false;
        System.Windows.Automation.AutomationProperties.SetHelpText(confirm, "Usa el ratón o enfoca este botón y pulsa Espacio. Enter no solicita el cierre.");
        footer.Children.Add(confirm); DialogLayout.Add(grid, footer, 3);
        PreviewKeyDown += (_, e) => { if (e.Key == Key.Enter) e.Handled = true; };
        Loaded += (_, _) => acknowledgement.Focus();
    }
}
