using System.Globalization;
using System.Security.Principal;
using System.Windows;
using System.Windows.Markup;

namespace Limpiador.App;

public partial class App : Application
{
    private Mutex? _instanceMutex;
    private bool _ownsInstanceMutex;
    public static Func<IAppController>? ControllerFactory { get; set; }
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        var culture = CultureInfo.GetCultureInfo("es-ES");
        CultureInfo.DefaultThreadCurrentCulture = culture;
        CultureInfo.DefaultThreadCurrentUICulture = culture;
        FrameworkElement.LanguageProperty.OverrideMetadata(typeof(FrameworkElement), new FrameworkPropertyMetadata(XmlLanguage.GetLanguage(culture.IetfLanguageTag)));
        try
        {
            if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 17763))
                throw new PlatformNotSupportedException("Esta aplicación requiere Windows 10 1809 o posterior, o Windows 11.");
            using var identity = WindowsIdentity.GetCurrent();
            var user = identity.User?.Value ?? throw new InvalidOperationException("No se pudo identificar la sesión de Windows.");
            _instanceMutex = new Mutex(false, @"Local\LimpiadorUltraWindows-" + user);
            try { _ownsInstanceMutex = _instanceMutex.WaitOne(0); }
            catch (AbandonedMutexException) { _ownsInstanceMutex = true; }
            if (!_ownsInstanceMutex)
            {
                MessageBox.Show("Limpiador Ultra Windows ya está abierto en esta sesión. Usa la ventana existente para revisar el análisis o el historial.",
                    "La aplicación ya está abierta", MessageBoxButton.OK, MessageBoxImage.Information);
                Shutdown();
                return;
            }
            var controller = CreateController();
            MainWindow = new MainWindow(controller);
            MainWindow.Show();
        }
        catch (Exception exception)
        {
            MessageBox.Show($"No se pudo iniciar Limpiador Ultra Windows.\n\n{exception.Message}", "Error al iniciar", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
        }
    }
    protected override void OnExit(ExitEventArgs e)
    {
        if (_ownsInstanceMutex) _instanceMutex?.ReleaseMutex();
        _instanceMutex?.Dispose();
        base.OnExit(e);
    }
    private static IAppController CreateController()
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 17763))
            throw new PlatformNotSupportedException("Esta aplicación requiere Windows 10 1809 o posterior, o Windows 11.");
        return ControllerFactory?.Invoke() ?? new WindowsAppController();
    }
}
