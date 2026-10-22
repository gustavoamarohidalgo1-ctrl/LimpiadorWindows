using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Microsoft.Win32;

namespace Limpiador.App;

public partial class PreferencesWindow : Window
{
    private readonly ObservableCollection<string> _excludedFolders;
    public UiPreferences? Preferences { get; private set; }

    public PreferencesWindow(UiPreferences initial, bool recoveryRequired)
    {
        InitializeComponent();
        WindowLayout.Attach(this, minimumContentHeight: recoveryRequired ? 720 : 650, minimumContentWidth: 460);
        AgeBox.ItemsSource = new[] { 7, 30, 90, 180, 365 };
        AgeBox.SelectedItem = initial.MinimumAgeDays;
        AdditionalFolderBox.Text = initial.AdditionalFolder ?? "";
        _excludedFolders = new(initial.ExcludedFolders ?? []);
        ExcludedFoldersList.ItemsSource = _excludedFolders;
        RecoveryNotice.Visibility = recoveryRequired ? Visibility.Visible : Visibility.Collapsed;
        UpdateExclusionCount();
        Loaded += (_, _) => AgeBox.Focus();
    }
    private void ChooseAdditionalFolder_Click(object sender, RoutedEventArgs e)
    {
        var picker = new OpenFolderDialog { Title = "Elegir una carpeta adicional para analizar", Multiselect = false };
        if (picker.ShowDialog(this) == true) AdditionalFolderBox.Text = picker.FolderName;
    }
    private void RemoveAdditionalFolder_Click(object sender, RoutedEventArgs e) => AdditionalFolderBox.Clear();
    private void AddExcludedFolder_Click(object sender, RoutedEventArgs e)
    {
        var picker = new OpenFolderDialog { Title = "Excluir carpetas del análisis", Multiselect = true };
        if (picker.ShowDialog(this) != true) return;
        foreach (var folder in picker.FolderNames)
        {
            var path = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder));
            if (_excludedFolders.Any(existing => string.Equals(Path.TrimEndingDirectorySeparator(existing), path, StringComparison.OrdinalIgnoreCase))) continue;
            _excludedFolders.Add(path);
        }
        UpdateExclusionCount();
        if (_excludedFolders.Count > 0) ExcludedFoldersList.SelectedIndex = _excludedFolders.Count - 1;
        ExcludedFoldersList.Focus();
    }
    private void RemoveExcludedFolder_Click(object sender, RoutedEventArgs e)
    {
        if (ExcludedFoldersList.SelectedItem is not string folder) return;
        var previousIndex = ExcludedFoldersList.SelectedIndex;
        _excludedFolders.Remove(folder);
        if (_excludedFolders.Count > 0) ExcludedFoldersList.SelectedIndex = Math.Min(previousIndex, _excludedFolders.Count - 1);
        UpdateExclusionCount();
        ExcludedFoldersList.Focus();
    }
    private void ExcludedFolders_SelectionChanged(object sender, SelectionChangedEventArgs e) => RemoveExcludedFolder.IsEnabled = ExcludedFoldersList.SelectedItem is not null;
    private void ExcludedFolders_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Delete && Keyboard.Modifiers == ModifierKeys.None)
        { RemoveExcludedFolder_Click(sender, e); e.Handled = true; }
    }
    private void UpdateExclusionCount() => ExclusionCountText.Text = _excludedFolders.Count == 0
        ? "No has añadido exclusiones personales. Las protecciones del sistema siguen activas."
        : $"{_excludedFolders.Count:N0} carpetas excluidas, incluido todo su contenido.";
    private void Save_Click(object sender, RoutedEventArgs e)
    {
        if (AgeBox.SelectedItem is not int age) return;
        Preferences = new(age, string.IsNullOrWhiteSpace(AdditionalFolderBox.Text) ? null : AdditionalFolderBox.Text, _excludedFolders.ToArray());
        DialogResult = true;
    }
}
