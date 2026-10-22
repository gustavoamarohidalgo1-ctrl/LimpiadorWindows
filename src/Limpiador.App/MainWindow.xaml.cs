using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Microsoft.Win32;

namespace Limpiador.App;

public partial class MainWindow : Window
{
    private readonly IAppController _controller;
    private readonly AppViewModel _model = new();
    private CancellationTokenSource? _cancellation;
    private Task? _activeOperation;
    private bool _closePending;
    private bool _allowClose;
    private bool? _compactLayout;
    private bool _preparingPlan;

    public MainWindow(IAppController controller)
    {
        _controller = controller;
        UiTheme.Initialize();
        InitializeComponent();
        WindowLayout.Attach(this, minimumContentHeight: 440);
        DataContext = _model;
    }
    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        UpdateLayoutMode();
        await RunAsync(async (token, _) =>
        {
            _model.Status = "Cargando las preferencias del análisis…";
            _model.ApplyPreferences(await _controller.GetPreferencesAsync(token));
            _model.SetOverview(await _controller.GetOverviewAsync(token));
            _model.Status = "Listo. Pulsa «Analizar equipo» para empezar.";
        });
    }
    private async void Scan_Click(object sender, RoutedEventArgs e)
    {
        if (!_model.CanScan) return;
        _model.ClearSelection();
        _model.AnalysisCurrent = false;
        _model.Scanning = true;
        _model.ScanSummary = "Análisis en curso. Las cifras anteriores se reemplazarán al terminar.";
        ShowResults();
        var preferences = _model.GetPreferences();
        var options = new UiScanOptions(preferences.MinimumAgeDays, preferences.AdditionalFolder, preferences.ExcludedFolders);
        await RunAsync(async (token, progress) =>
        {
            _model.Status = "Analizando tus archivos…";
            var result = await _controller.ScanAsync(options, progress, token);
            token.ThrowIfCancellationRequested();
            _model.SetResults(result, options);
            _model.Status = "Análisis terminado. Ningún elemento se ha marcado automáticamente.";
            await RefreshOverviewAfterOperationAsync();
        });
        _model.Scanning = false;
        if (!_model.AnalysisCurrent)
            _model.ScanSummary = "El análisis no terminó. Vuelve a analizar para obtener resultados actualizados.";
    }
    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        _cancellation?.Cancel();
        _model.Status = "Deteniendo… Esperando a que termine de forma segura la operación en curso.";
    }
    private async void Preferences_Click(object sender, RoutedEventArgs e)
    {
        if (!_model.Idle) return;
        var draft = _model.GetPreferences();
        while (!_closePending)
        {
            var dialog = new PreferencesWindow(draft, recoveryRequired: !_model.PreferencesReady) { Owner = this };
            if (dialog.ShowDialog() != true || dialog.Preferences is not { } edited) return;
            draft = edited;
            var saved = false;
            await RunAsync(async (token, _) =>
            {
                _model.Status = "Guardando las preferencias del análisis…";
                await _controller.SavePreferencesAsync(edited, token);
                _model.InvalidateAnalysis();
                _model.ApplyPreferences(edited);
                _model.ScanSummary = "Las opciones cambiaron. Vuelve a analizar para aplicar las carpetas excluidas y la antigüedad.";
                _model.Status = "Preferencias guardadas. Analiza el equipo para aplicar estas opciones.";
                saved = true;
            });
            if (saved) return;
            // A failed or cancelled save keeps the draft available for correction.
        }
    }
    private void SelectSafe_Click(object sender, RoutedEventArgs e)
    {
        if (!_model.CanSelectItems) return;
        SearchBox.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();
        _model.SelectRecommended();
    }
    private void ClearSelection_Click(object sender, RoutedEventArgs e) => _model.ClearSelection(userInitiated: true);
    private void Maximum_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new MaximumWindow(_model.Items.Where(x => !x.UserDeselected && x.Item.CanSelect && x.Item.Risk == UiRisk.Safe && (x.Item.Recommended || x.Item.MaximumGroup is not null)).ToArray()) { Owner = this };
        if (dialog.ShowDialog() == true)
            _model.AddSelection(dialog.ChosenItems);
    }
    private async void Clean_Click(object sender, RoutedEventArgs e)
    {
        if (!_model.CanClean) return;
        var requestedIds = _model.Items.Where(x => x.Selected && x.Item.CanSelect).Select(x => x.Item.Id).ToArray();
        UiCleanupPlan? plan = null;
        _preparingPlan = true;
        try
        {
            await RunAsync(async (token, _) =>
            {
                _model.Status = "Preparando el plan de limpieza y comprobando las omisiones…";
                var prepared = await _controller.PreparePlanAsync(requestedIds, token);
                token.ThrowIfCancellationRequested();
                CleanupPlanReview.Validate(prepared, requestedIds);
                plan = new(prepared.Items.ToArray(), prepared.Omissions.ToArray());
                _model.Status = $"Plan preparado: {plan.Items.Count:N0} acciones aceptadas y {plan.Omissions.Count:N0} omitidas. No se ha modificado ningún archivo.";
            });
        }
        finally { _preparingPlan = false; }
        if (plan is null || _closePending) return;
        var dialog = new ConfirmationWindow(plan) { Owner = this };
        if (dialog.ShowDialog() != true || plan.Items.Count == 0) return;
        var approvedIds = plan.Items.Select(x => x.Id).ToArray();
        await RunAsync(async (token, progress) =>
        {
            _model.Status = "Aplicando las acciones confirmadas…";
            try
            {
                var result = await _controller.ExecuteAsync(approvedIds, progress, token);
                _model.Status = result.Summary;
                if (!_closePending) new ResultWindow("Resultado de la limpieza", result) { Owner = this }.ShowDialog();
            }
            finally
            {
                _model.InvalidateAnalysis();
                await RefreshOverviewAfterOperationAsync();
            }
        });
    }
    private async void CloseApplications_Click(object sender, RoutedEventArgs e)
    {
        if (!_model.CanSelectItems || _model.FocusedItem?.Item is not { CanCloseApplications: true } item) return;
        var confirmation = new ApplicationCloseWindow(item.Name, item.ApplicationsToClose ?? []) { Owner = this };
        if (confirmation.ShowDialog() != true) return;
        await RunAsync(async (token, progress) =>
        {
            _model.Status = "Solicitando el cierre de las aplicaciones…";
            try
            {
                var result = await _controller.CloseApplicationsAsync([item.Id], progress, token);
                _model.Status = result.Summary;
                if (!_closePending) new ResultWindow("Cierre de aplicaciones", result) { Owner = this }.ShowDialog();
            }
            finally
            {
                _model.InvalidateAnalysis();
                _model.ScanSummary = "Vuelve a analizar para comprobar qué aplicaciones siguen en uso antes de limpiar.";
            }
        });
    }
    private async void History_Click(object sender, RoutedEventArgs e)
    {
        IReadOnlyList<UiHistoryEntry>? history = null;
        await RunAsync(async (token, _) => history = await _controller.GetHistoryAsync(token));
        if (history is null || _closePending) return;
        var dialog = new HistoryWindow(history) { Owner = this };
        if (dialog.ShowDialog() == true && dialog.RestoreEntry is { CanRestore: true } entry)
        {
            await RunAsync(async (token, progress) =>
            {
                _model.Status = "Restaurando archivos…";
                try
                {
                    var result = await _controller.RestoreAsync(entry.Id, progress, token);
                    _model.Status = result.Summary;
                    if (!_closePending) new ResultWindow("Resultado de la restauración", result) { Owner = this }.ShowDialog();
                }
                finally
                {
                    _model.InvalidateAnalysis();
                    await RefreshOverviewAfterOperationAsync();
                }
            });
        }
    }
    private async Task RefreshOverviewAfterOperationAsync()
    {
        try { _model.SetOverview(await _controller.GetOverviewAsync(CancellationToken.None)); }
        catch (Exception exception) { _model.Status += $" No se pudo actualizar el espacio libre: {exception.Message}"; }
    }
    private void ExploreSpace_Click(object sender, RoutedEventArgs e)
    {
        if (!_model.CanExplore) return;
        var explorer = new DirectoryExplorerWindow(_model.Directories, _model.Items.Select(x => x.Item).ToArray(),
            _model.Warnings.ToArray(), _model.AnalysisCurrent, _controller.OpenInExplorer) { Owner = this };
        if (explorer.ShowDialog() != true || explorer.CandidateToFocus is not { } candidateId) return;
        _model.Category = _model.Categories.FirstOrDefault(x => x.Id == "all");
        _model.Query = "";
        _model.RiskIndex = 0;
        _model.FocusedItem = _model.Items.FirstOrDefault(x => x.Item.Id == candidateId);
        ShowResults();
        if (_model.FocusedItem is not null) { ResultsGrid.ScrollIntoView(_model.FocusedItem); ResultsGrid.Focus(); }
    }
    private void Explorer_Click(object sender, RoutedEventArgs e)
    {
        var path = PathsList.SelectedItem as string ?? _model.FocusedItem?.Item.Paths.FirstOrDefault();
        if (path is null) return;
        try { _controller.OpenInExplorer(path); }
        catch (Exception exception) { ShowError("No se pudo abrir la ubicación", exception); }
    }
    private void CopyPaths_Click(object sender, RoutedEventArgs e)
    {
        if (_model.FocusedItem is not { } item) return;
        try { Clipboard.SetText(item.Item.PathsText); }
        catch (Exception exception) { ShowError("No se pudieron copiar las rutas", exception); }
    }
    private void StorageSettings_Click(object sender, RoutedEventArgs e)
    {
        try { _controller.OpenStorageSettings(); }
        catch (Exception exception) { ShowError("No se pudo abrir Almacenamiento", exception); }
    }
    private async Task RunAsync(Func<CancellationToken, IProgress<UiProgress>, Task> operation)
    {
        if (_model.Busy) return;
        _cancellation = new CancellationTokenSource();
        var operationCancellation = _cancellation;
        var token = operationCancellation.Token;
        _model.Busy = true;
        _model.Indeterminate = true;
        _model.Progress = 0;
        // Progress<T> posts to the UI context. Ignore notifications queued after completion.
        var progress = new Progress<UiProgress>(p =>
        {
            if (!_model.Busy || _cancellation != operationCancellation || token.IsCancellationRequested) return;
            _model.Status = p.Message;
            _model.Indeterminate = p.Total <= 0;
            _model.Progress = p.Percent;
        });
        _activeOperation = ExecuteAsync(operation, token, progress);
        await _activeOperation;
    }
    private async Task ExecuteAsync(Func<CancellationToken, IProgress<UiProgress>, Task> operation, CancellationToken token, IProgress<UiProgress> progress)
    {
        try { await operation(token, progress); }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            _model.Status = _model.Scanning ? "Análisis detenido. No se modificó ningún archivo."
                : _preparingPlan ? "Revisión detenida. No se modificó ningún archivo."
                : "Operación detenida. Consulta el historial si se alcanzaron a aplicar cambios.";
        }
        catch (Exception exception)
        {
            _model.Status = $"La operación no terminó: {exception.Message}";
            if (!_closePending) ShowError("No se pudo completar la operación", exception);
        }
        finally
        {
            _model.Busy = false;
            _model.Indeterminate = false;
            _cancellation?.Dispose();
            _cancellation = null;
        }
    }
    private async void Export_Click(object sender, RoutedEventArgs e)
    {
        if (!_model.CanExport || _model.LastResult is not { } result || _model.AnalyzedAt is not { } analyzedAt) return;
        var dialog = new SaveFileDialog
        {
            Title = "Exportar el último análisis", Filter = "Informe JSON (*.json)|*.json",
            DefaultExt = ".json", AddExtension = true, OverwritePrompt = true,
            FileName = $"analisis-limpiador-{analyzedAt.LocalDateTime:yyyyMMdd-HHmm}.json"
        };
        if (dialog.ShowDialog(this) != true) return;
        var current = _model.AnalysisCurrent;
        var options = _model.AnalyzedOptions;
        await RunAsync(async (token, _) =>
        {
            _model.Status = "Guardando el informe del análisis…";
            await Task.Run(() => AnalysisReportWriter.WriteAsync(dialog.FileName, result, analyzedAt, options, current, token), token);
            _model.Status = "Informe guardado: " + dialog.FileName;
        });
    }
    private void ClearSearch_Click(object sender, RoutedEventArgs e)
    {
        SearchBox.Clear();
        SearchBox.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();
        SearchBox.Focus();
    }
    private void ClearFilters_Click(object sender, RoutedEventArgs e)
    {
        ClearSearch_Click(sender, e);
        _model.RiskIndex = 0;
        _model.Category = _model.Categories.FirstOrDefault(x => x.Id == "all");
    }
    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.F && Keyboard.Modifiers == ModifierKeys.Control)
        { ShowResults(); SearchBox.Focus(); SearchBox.SelectAll(); e.Handled = true; }
        else if (Keyboard.Modifiers == ModifierKeys.Control && (e.Key == Key.D1 || e.Key == Key.D2 || e.Key == Key.D3))
        {
            if (_compactLayout == true) CompactTabs.SelectedIndex = e.Key == Key.D1 ? 0 : e.Key == Key.D2 ? 1 : 2;
            if (e.Key == Key.D1) StoragePane.MoveFocus(new(FocusNavigationDirection.First));
            else if (e.Key == Key.D2) SearchBox.Focus();
            else if (_model.FocusedItem is not null) ShowDetails();
            e.Handled = true;
        }
        else if (e.Key == Key.F5 && Keyboard.Modifiers == ModifierKeys.None)
        { if (_model.CanScan) Scan_Click(sender, e); e.Handled = true; }
        else if (e.Key == Key.Escape && SearchBox.IsKeyboardFocusWithin && SearchBox.Text.Length > 0)
        { ClearSearch_Click(sender, e); e.Handled = true; }
    }
    private void Results_KeyDown(object sender, KeyEventArgs e)
    {
        if (Keyboard.Modifiers != ModifierKeys.None || e.OriginalSource is not DependencyObject source ||
            ItemsControl.ContainerFromElement(ResultsGrid, source) is not DataGridRow) return;
        if (e.Key == Key.Enter) { ShowDetails(); e.Handled = true; }
        else if (e.Key == Key.Space && Keyboard.FocusedElement is not CheckBox &&
            _model.CanSelectItems && _model.FocusedItem is { Item.CanSelect: true } item)
        { item.Selected = !item.Selected; e.Handled = true; }
    }
    private void Results_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (Keyboard.FocusedElement is not CheckBox && e.OriginalSource is DependencyObject source && ItemsControl.ContainerFromElement(ResultsGrid, source) is DataGridRow)
            ShowDetails();
    }
    private void ShowDetails()
    {
        if (_model.FocusedItem is null) return;
        if (_compactLayout == true) CompactTabs.SelectedIndex = 2;
        PathsList.Focus();
        DetailsPane.BringIntoView();
    }
    private void ShowResults() { if (_compactLayout == true) CompactTabs.SelectedIndex = 1; }
    private void Window_SizeChanged(object sender, SizeChangedEventArgs e) => UpdateLayoutMode();
    private void CompactTabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_compactLayout == true && ReferenceEquals(e.OriginalSource, CompactTabs)) UpdatePaneVisibility();
    }
    private void UpdateLayoutMode()
    {
        if (!IsInitialized || WorkArea is null) return;
        var compact = ActualWidth < 1120;
        if (_compactLayout == compact) return;
        _compactLayout = compact;
        CompactTabs.Visibility = compact ? Visibility.Visible : Visibility.Collapsed;
        FirstSplitter.Visibility = SecondSplitter.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
        StorageColumn.MinWidth = compact ? 0 : 210;
        ResultsColumn.MinWidth = compact ? 0 : 440;
        DetailsColumn.MinWidth = compact ? 0 : 270;
        StorageColumn.Width = compact ? new(1, GridUnitType.Star) : new(244);
        FirstSplitterColumn.Width = SecondSplitterColumn.Width = new(compact ? 0 : 5);
        ResultsColumn.Width = compact ? new(0) : new(1, GridUnitType.Star);
        DetailsColumn.Width = new(compact ? 0 : 330);
        Grid.SetColumn(ResultsPane, compact ? 0 : 2);
        Grid.SetColumn(DetailsPane, compact ? 0 : 4);
        foreach (var actions in new FrameworkElement[] { HeaderActions, FooterActions })
        {
            Grid.SetColumn(actions, compact ? 0 : 1);
            Grid.SetRow(actions, compact ? 1 : 0);
            Grid.SetColumnSpan(actions, compact ? 2 : 1);
            actions.Margin = compact ? new(0, 6, 0, 0) : new(0);
        }
        HeaderBorder.Padding = compact ? new(12, 8, 12, 6) : new(24, 18, 24, 18);
        FooterBorder.Padding = compact ? new(12, 8, 12, 8) : new(24, 14, 24, 14);
        ApplicationTitle.FontSize = compact ? 20 : 25;
        ComputerDescriptionText.Visibility = ScanSummaryText.Visibility = SelectionTimingText.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
        OptionsExpander.IsExpanded = !compact;
        ResultsPane.Margin = compact ? new(12, 8, 12, 8) : new(18, 20, 18, 20);
        UpdateMinimumContentHeight();
        UpdatePaneVisibility();
    }
    private void OptionsExpansion_Changed(object sender, RoutedEventArgs e) => UpdateMinimumContentHeight();
    private void UpdateMinimumContentHeight()
    {
        if (MainLayout is null || OptionsExpander is null) return;
        MainLayout.MinHeight = _compactLayout == true
            ? OptionsExpander.IsExpanded ? 680 : 480
            : OptionsExpander.IsExpanded ? 740 : 540;
    }
    private void UpdatePaneVisibility()
    {
        if (StoragePane is null || ResultsPane is null || DetailsPane is null) return;
        StoragePane.Visibility = _compactLayout != true || CompactTabs.SelectedIndex == 0 ? Visibility.Visible : Visibility.Collapsed;
        ResultsPane.Visibility = _compactLayout != true || CompactTabs.SelectedIndex == 1 ? Visibility.Visible : Visibility.Collapsed;
        DetailsPane.Visibility = _compactLayout != true || CompactTabs.SelectedIndex == 2 ? Visibility.Visible : Visibility.Collapsed;
    }
    private void ShowError(string title, Exception exception) => MessageBox.Show(this, exception.Message, title, MessageBoxButton.OK, MessageBoxImage.Warning);
    private async void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (_allowClose || !_model.Busy) return;
        e.Cancel = true;
        if (_closePending) return;
        _closePending = true;
        _model.Status = "Cerrando… La operación se detendrá antes de salir.";
        _cancellation?.Cancel();
        if (_activeOperation is { } operation) await operation;
        _allowClose = true;
        Close();
    }
}
