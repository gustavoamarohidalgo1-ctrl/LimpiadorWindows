using System.Windows;
using System.Windows.Input;

namespace Limpiador.App;
public partial class ConfirmationWindow : Window
{
    private readonly bool _hasIrreversible;
    private readonly bool _hasActions;
    public ConfirmationWindow(UiCleanupPlan plan)
    {
        InitializeComponent();
        WindowLayout.Attach(this, minimumContentHeight: 600, minimumContentWidth: 460);
        var items = plan.Items;
        _hasActions = items.Count > 0;
        _hasIrreversible = items.Any(x => x.IsIrreversible);
        ItemsList.ItemsSource = items;
        SummaryText.Text = !_hasActions ? "0 acciones aceptadas. No se realizará ningún cambio."
            : $"{items.Count:N0} acciones aceptadas · {DisplaySize.Bytes(plan.AcceptedBytes)}. " +
                (items.Any(x => x.UsesRecycleBin) ? "El espacio en la Papelera se libera cuando la vacías." : "El ahorro real puede variar.");
        AcceptedTab.Header = $"Acciones aceptadas ({items.Count:N0})";
        OmissionsTab.Header = $"Elementos omitidos ({plan.Omissions.Count:N0})";
        OmissionsTab.Visibility = plan.Omissions.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        OmissionsList.ItemsSource = plan.Omissions;
        OmissionsNotice.Text = !_hasActions ? "No hay acciones ejecutables en esta selección. Consulta los motivos de omisión y vuelve a revisar los resultados."
            : plan.Omissions.Count > 0 ? $"{plan.Omissions.Count:N0} elementos de tu selección no se ejecutarán. Consulta la pestaña «Elementos omitidos» antes de continuar."
            : "Todos los elementos solicitados están incluidos en este plan.";
        Acknowledgements.Visibility = Confirm.Visibility = _hasActions ? Visibility.Visible : Visibility.Collapsed;
        if (!_hasActions) PlanTabs.SelectedItem = OmissionsTab;
        Irreversible.Visibility = _hasIrreversible ? Visibility.Visible : Visibility.Collapsed;
        Loaded += (_, _) => { if (_hasActions) Reviewed.Focus(); else OmissionsList.Focus(); };
    }
    private void Acknowledgement_Changed(object sender, RoutedEventArgs e)
    {
        if (Confirm is not null && Reviewed is not null && Irreversible is not null)
            Confirm.IsEnabled = _hasActions && Reviewed.IsChecked == true && (!_hasIrreversible || Irreversible.IsChecked == true);
    }
    private void Confirm_Click(object sender, RoutedEventArgs e) { if (Confirm.IsEnabled) DialogResult = true; }
    private void Window_PreviewKeyDown(object sender, KeyEventArgs e) { if (e.Key == Key.Enter) e.Handled = true; }
}
