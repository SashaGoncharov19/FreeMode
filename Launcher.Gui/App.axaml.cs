using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using GTANetwork.Launcher.Gui.ViewModels;
using GTANetwork.Launcher.Gui.Views;

namespace GTANetwork.Launcher.Gui;

public partial class App : Application
{
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var vm = new MainViewModel(Program.InstallDir);
            desktop.MainWindow = new MainWindow { DataContext = vm };
            vm.RefreshStatus();
            vm.RefreshLogs();
        }
        base.OnFrameworkInitializationCompleted();
    }
}
