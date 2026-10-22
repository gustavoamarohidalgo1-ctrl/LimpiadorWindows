using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace Limpiador.App;

public partial class DirectoryExplorerWindow : Window
{
    private readonly DirectoryExplorerModel _model;
    private readonly Action<string> _openExplorer;
    public string? CandidateToFocus { get; private set; }
    public DirectoryExplorerWindow(IReadOnlyList<UiDirectory> directories, IReadOnlyList<UiItem> candidates,
        IReadOnlyList<string> warnings, bool analysisCurrent, Action<string> openExplorer)
    {
        InitializeComponent();
        WindowLayout.Attach(this, minimumContentHeight: 650, minimumContentWidth: 560);
        _openExplorer = openExplorer;
        _model = new(directories, candidates, warnings, analysisCurrent);
        DataContext = _model;
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.F && Keyboard.Modifiers == ModifierKeys.Control)
            { DirectorySearch.Focus(); DirectorySearch.SelectAll(); e.Handled = true; }
            else if (e.Key == Key.Escape && DirectorySearch.IsKeyboardFocusWithin && DirectorySearch.Text.Length > 0)
            { DirectorySearch.Clear(); DirectorySearch.GetBindingExpression(TextBox.TextProperty)?.UpdateSource(); e.Handled = true; }
            else if (e.Key == Key.Up && Keyboard.Modifiers == ModifierKeys.Alt)
            { _model.GoUp(); e.Handled = true; }
        };
    }
    private void Roots_Click(object sender, RoutedEventArgs e) => _model.GoTo(null);
    private void Up_Click(object sender, RoutedEventArgs e) => _model.GoUp();
    private void Descend_Click(object sender, RoutedEventArgs e) { if (_model.SelectedDirectory is { } directory) _model.GoTo(directory); }
    private void Directory_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is DependencyObject source && ItemsControl.ContainerFromElement(DirectoriesGrid, source) is DataGridRow)
            Descend_Click(sender, e);
    }
    private void Directory_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { Descend_Click(sender, e); e.Handled = true; }
        else if (e.Key == Key.Back) { _model.GoUp(); e.Handled = true; }
    }
    private void OpenExplorer_Click(object sender, RoutedEventArgs e)
    {
        if (_model.ExplorerPath is not { } path) return;
        try { _openExplorer(path); }
        catch (Exception exception) { MessageBox.Show(this, exception.Message, "No se pudo abrir la ubicación", MessageBoxButton.OK, MessageBoxImage.Warning); }
    }
    private void ShowCandidate_Click(object sender, RoutedEventArgs e)
    {
        if (!_model.CanShowCandidate || _model.SelectedCandidate is not { } candidate) return;
        CandidateToFocus = candidate.Id; DialogResult = true;
    }
}

public sealed class DirectoryExplorerModel : ObservableModel
{
    private readonly Dictionary<string, UiDirectory> _directories;
    private readonly Dictionary<string, UiDirectory[]> _children;
    private readonly UiDirectory[] _roots;
    private readonly IReadOnlyList<UiItem> _candidates;
    private readonly bool _analysisCurrent;
    private UiDirectory? _current;
    public IReadOnlyList<string> Warnings { get; }
    public bool HasWarnings => Warnings.Count > 0;
    public string WarningsTitle => $"Avisos del análisis ({Warnings.Count})";
    public string FreshnessText => _analysisCurrent ? "Vista de solo lectura. El contenido puede cambiar después del análisis."
        : "Estos tamaños pertenecen a un análisis anterior. Vuelve a analizar para actualizar las cifras y revisar candidatos.";
    public string[] SortOptions { get; } = ["Mayor tamaño", "Más archivos", "Nombre"];
    public string CurrentPath => _current?.Path ?? "Carpetas principales del análisis";
    public bool CanGoUp => _current is not null || Query.Length > 0;
    public IReadOnlyList<UiDirectory> VisibleDirectories { get; private set; } = [];
    public IReadOnlyList<UiItem> RelatedItems { get; private set; } = [];
    public string DirectorySummary { get; private set; } = "";
    public string? ExplorerPath => SelectedDirectory?.Path ?? _current?.Path;
    public bool CanOpenExplorer => ExplorerPath is not null;
    public bool HasSelectedDirectory => SelectedDirectory is not null;
    public bool CanShowCandidate => _analysisCurrent && SelectedCandidate is not null;
    public string RelatedTitle => ExplorerPath is null ? "Selecciona una carpeta para ver sus candidatos" : $"Candidatos relacionados ({RelatedItems.Count:N0})";
    public string SelectionNotice
    {
        get
        {
            var directory = SelectedDirectory ?? _current;
            if (directory is null) return "Navegar por las carpetas no marca elementos ni modifica archivos.";
            return directory.StatusText + ". " + (!directory.Complete ? "El tamaño es parcial y puede ser menor que el real. " : "") +
                (directory.Sensitive ? "Contiene rutas protegidas que no se pueden limpiar. " : "") +
                (RelatedItems.Count == 0 ? "No hay una acción de limpieza propuesta para esta carpeta." : "Revisa cada candidato en la ventana principal antes de marcarlo.");
        }
    }
    private string _query = "";
    public string Query { get => _query; set { if (Set(ref _query, value)) Refresh(); } }
    private int _sortIndex;
    public int SortIndex { get => _sortIndex; set { if (Set(ref _sortIndex, value)) Refresh(); } }
    private UiDirectory? _selectedDirectory;
    public UiDirectory? SelectedDirectory
    {
        get => _selectedDirectory;
        set { if (Set(ref _selectedDirectory, value)) RefreshRelated(); }
    }
    private UiItem? _selectedCandidate;
    public UiItem? SelectedCandidate { get => _selectedCandidate; set { if (Set(ref _selectedCandidate, value)) Changed(nameof(CanShowCandidate)); } }

    public DirectoryExplorerModel(IReadOnlyList<UiDirectory> directories, IReadOnlyList<UiItem> candidates,
        IReadOnlyList<string> warnings, bool analysisCurrent)
    {
        _directories = directories.GroupBy(x => Normalize(x.Path), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(x => x.Key, x => x.First(), StringComparer.OrdinalIgnoreCase);
        _children = _directories.Values.GroupBy(x => Parent(x.Path) ?? "", StringComparer.OrdinalIgnoreCase)
            .ToDictionary(x => x.Key, x => x.ToArray(), StringComparer.OrdinalIgnoreCase);
        _roots = _directories.Values.Where(x => Parent(x.Path) is not { } parent || !_directories.ContainsKey(parent)).ToArray();
        _candidates = candidates; Warnings = warnings; _analysisCurrent = analysisCurrent;
        Refresh();
    }
    public void GoTo(UiDirectory? directory)
    {
        _current = directory; _query = ""; _selectedDirectory = null;
        Changed(nameof(Query)); Changed(nameof(CurrentPath)); Changed(nameof(SelectedDirectory));
        Refresh();
    }
    public void GoUp()
    {
        if (Query.Length > 0) { Query = ""; return; }
        var parent = _current is null ? null : Parent(_current.Path);
        GoTo(parent is not null ? _directories.GetValueOrDefault(parent) : null);
    }
    private void Refresh()
    {
        var query = Query.Trim();
        IEnumerable<UiDirectory> scope = query.Length > 0
            ? _directories.Values.Where(x => x.Path.Contains(query, StringComparison.OrdinalIgnoreCase))
            : _current is null ? _roots : _children.GetValueOrDefault(Normalize(_current.Path)) ?? [];
        VisibleDirectories = SortIndex switch
        {
            1 => scope.OrderByDescending(x => x.Files).ThenByDescending(x => x.Bytes).ToArray(),
            2 => scope.OrderBy(x => x.Name, StringComparer.CurrentCultureIgnoreCase).ToArray(),
            _ => scope.OrderByDescending(x => x.Bytes).ThenBy(x => x.Name, StringComparer.CurrentCultureIgnoreCase).ToArray()
        };
        _selectedDirectory = null;
        if (query.Length > 0) DirectorySummary = $"{VisibleDirectories.Count:N0} carpetas coincidentes en todo el análisis. No se suman tamaños para evitar contar padres e hijos dos veces.";
        else if (_current is null) DirectorySummary = $"{_roots.Length:N0} carpetas principales · {DisplaySize.Bytes(_roots.Sum(x => x.Bytes))} medidos. Las carpetas con lectura parcial pueden contener más datos.";
        else
        {
            var directFiles = Math.Max(0, _current.Files - VisibleDirectories.Sum(x => x.Files));
            var directBytes = Math.Max(0, _current.Bytes - VisibleDirectories.Sum(x => x.Bytes));
            DirectorySummary = $"{VisibleDirectories.Count:N0} subcarpetas · {_current.Files:N0} archivos en total · {_current.SizeText}. Archivos directamente aquí: {directFiles:N0} · {DisplaySize.Bytes(directBytes)}. Los archivos individuales se revisan en los candidatos del análisis.";
        }
        Changed(nameof(VisibleDirectories)); Changed(nameof(SelectedDirectory)); Changed(nameof(DirectorySummary)); Changed(nameof(CanGoUp));
        RefreshRelated();
    }
    private void RefreshRelated()
    {
        var scope = ExplorerPath;
        RelatedItems = scope is null ? [] : _candidates.Where(item => item.Paths.Any(path => IsWithin(path, scope) || IsWithin(scope, path)))
            .OrderByDescending(x => x.SizeBytes).ToArray();
        SelectedCandidate = null;
        Changed(nameof(RelatedItems)); Changed(nameof(RelatedTitle)); Changed(nameof(SelectionNotice));
        Changed(nameof(CanOpenExplorer)); Changed(nameof(HasSelectedDirectory)); Changed(nameof(ExplorerPath));
    }
    private static string Normalize(string path) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
    private static string? Parent(string path) => Path.GetDirectoryName(Normalize(path)) is { } parent ? Normalize(parent) : null;
    private static bool IsWithin(string path, string root)
    {
        path = Normalize(path); root = Normalize(root);
        return path.Equals(root, StringComparison.OrdinalIgnoreCase) || path.StartsWith(root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }
}
