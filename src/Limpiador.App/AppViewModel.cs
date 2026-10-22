using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Data;

namespace Limpiador.App;

public abstract class ObservableModel : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    protected void Changed([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value; Changed(name); return true;
    }
}
public sealed class ItemViewModel(UiItem item) : ObservableModel
{
    public UiItem Item { get; } = item;
    private bool _selected;
    private bool _userDeselected;
    public bool UserDeselected => _userDeselected;
    public bool Selected { get => _selected; set => SetSelected(value, userInitiated: true); }
    internal void SetSelected(bool value, bool userInitiated = false, bool resetUserDeselection = false)
    {
        if (value && !Item.CanSelect) { Changed(nameof(Selected)); return; }
        var deselected = _userDeselected;
        if (resetUserDeselection || (userInitiated && value)) deselected = false;
        else if (userInitiated && _selected && !value) deselected = true;
        if (_userDeselected != deselected) { _userDeselected = deselected; Changed(nameof(UserDeselected)); }
        Set(ref _selected, value, nameof(Selected));
    }
}
public sealed record CategoryViewModel(string Id, string Title, int Count, long Bytes)
{
    public string Summary => $"{Count:N0} elementos · {DisplaySize.Bytes(Bytes)}";
}
public sealed class ResultCollection<T> : ObservableCollection<T>
{
    public void ReplaceAll(IEnumerable<T> values)
    {
        CheckReentrancy();
        Items.Clear();
        foreach (var value in values) Items.Add(value);
        OnPropertyChanged(new(nameof(Count)));
        OnPropertyChanged(new("Item[]"));
        OnCollectionChanged(new(NotifyCollectionChangedAction.Reset));
    }
}
public sealed class AppViewModel : ObservableModel
{
    public ResultCollection<ItemViewModel> Items { get; } = [];
    public UiScanResult? LastResult { get; private set; }
    public DateTimeOffset? AnalyzedAt { get; private set; }
    public UiScanOptions? AnalyzedOptions { get; private set; }
    public bool CanExport => Idle && LastResult is not null;
    private bool _scanning;
    public bool Scanning { get => _scanning; set { if (Set(ref _scanning, value)) NotifyEmptyState(); } }
    public ObservableCollection<CategoryViewModel> Categories { get; } = [];
    public ObservableCollection<UiDisk> Disks { get; } = [];
    public ObservableCollection<string> Warnings { get; } = [];
    public IReadOnlyList<UiDirectory> Directories { get; private set; } = [];
    public bool CanExplore => Idle && Directories.Count > 0;
    public ICollectionView FilteredItems { get; }
    public int[] AgeOptions { get; } = [7, 30, 90, 180, 365];
    public string[] RiskOptions { get; } = ["Todos los riesgos", "Seguro", "Revisar", "Cuidado"];
    public string[] SortOptions { get; } = ["Mayor tamaño", "Menor riesgo", "Nombre"];
    private bool _preferencesReady;
    public bool PreferencesReady { get => _preferencesReady; set { if (Set(ref _preferencesReady, value)) { Changed(nameof(CanScan)); Changed(nameof(PreferencesSummary)); NotifyEmptyState(); } } }
    public bool CanScan => Idle && PreferencesReady;
    public IReadOnlyList<string> ExcludedFolders { get; private set; } = [];
    public string PreferencesSummary => !PreferencesReady ? "Configura las opciones para poder analizar."
        : $"Antigüedad mínima: {MinimumAgeDays} días · {ExcludedFolders.Count:N0} carpetas excluidas" +
            (AdditionalFolder.Length == 0 ? " · Sin carpeta adicional" : " · Carpeta adicional: " + AdditionalFolder);
    public UiPreferences GetPreferences() => new(MinimumAgeDays, string.IsNullOrWhiteSpace(AdditionalFolder) ? null : AdditionalFolder, ExcludedFolders.ToArray());
    public void ApplyPreferences(UiPreferences preferences)
    {
        MinimumAgeDays = preferences.MinimumAgeDays;
        AdditionalFolder = preferences.AdditionalFolder ?? "";
        ExcludedFolders = preferences.ExcludedFolders?.ToArray() ?? [];
        PreferencesReady = true;
        Changed(nameof(ExcludedFolders)); Changed(nameof(PreferencesSummary));
    }
    private int _minimumAgeDays = 90;
    public int MinimumAgeDays { get => _minimumAgeDays; set => Set(ref _minimumAgeDays, value); }
    private string _additionalFolder = "";
    public string AdditionalFolder { get => _additionalFolder; set => Set(ref _additionalFolder, value); }
    private string _computerDescription = "Windows · Archivos de tu usuario";
    public string ComputerDescription { get => _computerDescription; set => Set(ref _computerDescription, value); }
    private string _status = "Listo. Analiza tu equipo para encontrar oportunidades de limpieza.";
    public string Status { get => _status; set => Set(ref _status, value); }
    private string _scanSummary = "El análisis no modifica tus archivos. Tú eliges qué limpiar.";
    public string ScanSummary { get => _scanSummary; set => Set(ref _scanSummary, value); }
    private bool _busy;
    public bool Busy { get => _busy; set { if (Set(ref _busy, value)) { Changed(nameof(Idle)); Changed(nameof(CanClean)); Changed(nameof(CanSelectItems)); Changed(nameof(CanExplore)); Changed(nameof(CanExport)); Changed(nameof(CanScan)); } } }
    public bool Idle => !Busy;
    private bool _analysisCurrent;
    public bool AnalysisCurrent { get => _analysisCurrent; set { if (Set(ref _analysisCurrent, value)) { Changed(nameof(CanClean)); Changed(nameof(CanSelectItems)); NotifyEmptyState(); } } }
    public bool CanSelectItems => Idle && AnalysisCurrent;
    public bool CanClean => CanSelectItems && SelectedCount > 0;
    private bool _indeterminate;
    public bool Indeterminate { get => _indeterminate; set => Set(ref _indeterminate, value); }
    private double _progress;
    public double Progress { get => _progress; set => Set(ref _progress, value); }
    private string _query = "";
    private string _normalizedQuery = "";
    public string Query { get => _query; set { if (Set(ref _query, value)) { _normalizedQuery = value.Trim(); Refresh(); } } }
    private int _riskIndex;
    public int RiskIndex { get => _riskIndex; set { if (Set(ref _riskIndex, value)) { Refresh(); Changed(nameof(FilterCaption)); } } }
    public string FilterCaption => RiskIndex == 0 ? "Opciones, filtros y selección" : $"Opciones y selección · filtro: {RiskOptions[RiskIndex]}";
    private int _sortIndex;
    public int SortIndex { get => _sortIndex; set { if (Set(ref _sortIndex, value)) ApplySort(); } }
    private CategoryViewModel? _category;
    public CategoryViewModel? Category { get => _category; set { if (Set(ref _category, value)) { Refresh(); Changed(nameof(CategoryTitle)); } } }
    public string CategoryTitle => Category?.Title ?? "Todos los elementos";
    private ItemViewModel? _focusedItem;
    public ItemViewModel? FocusedItem { get => _focusedItem; set => Set(ref _focusedItem, value); }
    private readonly HashSet<ItemViewModel> _selectedItems = [];
    private long _selectedBytes;
    private int _selectedRecycleItems;
    private bool _batchSelection;
    public int SelectedCount => _selectedItems.Count;
    public long SelectedBytes => _selectedBytes;
    public string SelectionSummary => $"{SelectedCount:N0} seleccionados · {DisplaySize.Bytes(SelectedBytes)}";
    public string SelectionTiming => _selectedRecycleItems > 0
        ? "El espacio enviado a la Papelera se libera al vaciarla."
        : "Las estimaciones se comprueban de nuevo al ejecutar.";
    private int _visibleCount;
    public string ResultCount => $"{_visibleCount:N0} de {Items.Count:N0} elementos";
    public bool HasNoResults => _visibleCount == 0;
    public string EmptyTitle => Scanning ? "Analizando tus archivos…" : !PreferencesReady ? "Configura el análisis" : LastResult is null ? "Analiza tu equipo"
        : !AnalysisCurrent ? "Vuelve a analizar" : Items.Count == 0 ? "No se encontraron candidatos" : "No hay coincidencias";
    public string EmptyDescription => Scanning ? "El análisis es de solo lectura. Puedes detenerlo cuando lo necesites."
        : !PreferencesReady ? "Las preferencias deben estar disponibles antes de analizar. Abre Configurar para revisar las carpetas excluidas y guardar las opciones."
        : LastResult is null ? "Pulsa Analizar equipo o F5 para empezar. Tú decidirás qué elementos marcar."
        : !AnalysisCurrent ? "El análisis anterior ya no permite seleccionar elementos. Inicia otro para actualizar los resultados."
        : Items.Count == 0 ? "Revisa los avisos para conocer las ubicaciones que pudieron analizarse."
        : "Cambia la búsqueda, el riesgo o la categoría para ver otros elementos.";
    public bool HasWarnings => Warnings.Count > 0;
    public string WarningTitle => $"Avisos del análisis ({Warnings.Count})";
    public bool HasNoFocusedItem => FocusedItem is null;

    public AppViewModel()
    {
        FilteredItems = CollectionViewSource.GetDefaultView(Items);
        FilteredItems.Filter = Filter;
        ApplySort();
        PropertyChanged += (_, args) => { if (args.PropertyName == nameof(FocusedItem)) Changed(nameof(HasNoFocusedItem)); };
    }
    private bool Filter(object value)
    {
        if (value is not ItemViewModel item) return false;
        if (Category is { Id: not "all" } && item.Item.CategoryId != Category.Id) return false;
        if (RiskIndex > 0 && (int)item.Item.Risk != RiskIndex - 1) return false;
        var query = _normalizedQuery;
        return query.Length == 0 || item.Item.Name.Contains(query, StringComparison.OrdinalIgnoreCase)
            || item.Item.Description.Contains(query, StringComparison.OrdinalIgnoreCase)
            || item.Item.Paths.Any(p => p.Contains(query, StringComparison.OrdinalIgnoreCase));
    }
    public void SetOverview(UiOverview overview)
    {
        ComputerDescription = overview.ComputerDescription;
        Disks.Clear(); foreach (var disk in overview.Disks) Disks.Add(disk);
    }
    public void SetResults(UiScanResult result, UiScanOptions? options = null)
    {
        foreach (var item in Items) item.PropertyChanged -= ItemChanged;
        _selectedItems.Clear(); _selectedBytes = 0; _selectedRecycleItems = 0;
        Items.ReplaceAll(result.Items.Select(item =>
        {
            var model = new ItemViewModel(item); model.PropertyChanged += ItemChanged; return model;
        }));
        LastResult = result; AnalyzedAt = DateTimeOffset.UtcNow; AnalyzedOptions = options;
        Changed(nameof(CanExport));
        Categories.Clear();
        Categories.Add(new("all", "Todos los elementos", Items.Count, Items.Sum(x => x.Item.SizeBytes)));
        foreach (var group in Items.GroupBy(x => x.Item.CategoryId).OrderBy(x => x.First().Item.CategoryName))
            Categories.Add(new(group.Key, group.First().Item.CategoryName, group.Count(), group.Sum(x => x.Item.SizeBytes)));
        Category = Categories.First();
        Warnings.Clear(); foreach (var warning in result.Warnings) Warnings.Add(warning);
        Directories = result.Directories ?? [];
        Changed(nameof(Directories)); Changed(nameof(CanExplore));
        Changed(nameof(HasWarnings)); Changed(nameof(WarningTitle));
        ScanSummary = result.Summary;
        FocusedItem = null;
        AnalysisCurrent = true;
        Refresh(); SelectionChanged(); NotifyEmptyState();
    }
    private void ItemChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName != nameof(ItemViewModel.Selected) || sender is not ItemViewModel item) return;
        var changed = item.Selected ? _selectedItems.Add(item) : _selectedItems.Remove(item);
        if (!changed) return;
        var direction = item.Selected ? 1 : -1;
        _selectedBytes += direction * item.Item.SizeBytes;
        if (item.Item.UsesRecycleBin) _selectedRecycleItems += direction;
        if (!_batchSelection) SelectionChanged();
    }
    public void SelectionChanged() { Changed(nameof(SelectedCount)); Changed(nameof(SelectedBytes)); Changed(nameof(SelectionSummary)); Changed(nameof(SelectionTiming)); Changed(nameof(CanClean)); }
    public void ClearSelection(bool userInitiated = false)
    {
        _batchSelection = true;
        try { foreach (var item in _selectedItems.ToArray()) item.SetSelected(false, userInitiated); }
        finally { _batchSelection = false; SelectionChanged(); }
    }
    public void AddSelection(IEnumerable<ItemViewModel> items, bool resetUserDeselection = false)
    {
        _batchSelection = true;
        try
        {
            foreach (var item in items)
                if (!item.UserDeselected || resetUserDeselection) item.SetSelected(true, resetUserDeselection: resetUserDeselection);
        }
        finally { _batchSelection = false; SelectionChanged(); }
    }
    public void SelectRecommended()
    {
        AddSelection(FilteredItems.Cast<ItemViewModel>().Where(item =>
            item.Item.Recommended && item.Item.Risk == UiRisk.Safe && item.Item.CanSelect && !item.Item.IsIrreversible), resetUserDeselection: true);
    }
    public void InvalidateAnalysis()
    {
        ClearSelection(); AnalysisCurrent = false;
        ScanSummary = "La operación cambió el estado del disco. Vuelve a analizar antes de otra limpieza.";
    }
    private void Refresh()
    {
        FilteredItems.Refresh();
        UpdateVisibleCount();
    }
    private void UpdateVisibleCount()
    {
        _visibleCount = FilteredItems.Cast<ItemViewModel>().Count();
        Changed(nameof(ResultCount)); Changed(nameof(HasNoResults));
        NotifyEmptyState();
    }
    private void NotifyEmptyState() { Changed(nameof(EmptyTitle)); Changed(nameof(EmptyDescription)); }
    private void ApplySort()
    {
        using (FilteredItems.DeferRefresh())
        {
            FilteredItems.SortDescriptions.Clear();
            if (SortIndex == 1) FilteredItems.SortDescriptions.Add(new("Item.Risk", ListSortDirection.Ascending));
            if (SortIndex == 2) FilteredItems.SortDescriptions.Add(new("Item.Name", ListSortDirection.Ascending));
            else FilteredItems.SortDescriptions.Add(new("Item.SizeBytes", ListSortDirection.Descending));
        }
        UpdateVisibleCount();
    }
}
