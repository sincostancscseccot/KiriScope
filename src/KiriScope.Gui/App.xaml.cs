using System.Configuration;
using System.Data;
using System.Windows;

namespace KiriScope.Gui;

/// <summary>
/// Interaction logic for App.xaml
/// </summary>
public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        // WPF's font-cache URI setup relies on WINDIR.  Standard Explorer sessions inherit it,
        // but a few launchers provide only SystemRoot; normalize the process environment before
        // StartupUri loads any WPF controls.
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("WINDIR")))
        {
            var windowsDirectory = Environment.GetEnvironmentVariable("WINDIR", EnvironmentVariableTarget.Machine) ??
                Environment.GetEnvironmentVariable("SystemRoot") ??
                Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            if (!string.IsNullOrWhiteSpace(windowsDirectory))
            {
                Environment.SetEnvironmentVariable("WINDIR", windowsDirectory, EnvironmentVariableTarget.Process);
            }
        }

        base.OnStartup(e);
    }
}
