using System.IO;
using System.Windows;
using OneDriveLivePhotoViewer.Services;

namespace OneDriveLivePhotoViewer;

public partial class App : System.Windows.Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            AppLogger.Log("Unhandled AppDomain exception", args.ExceptionObject as Exception);
        };

        DispatcherUnhandledException += (_, args) =>
        {
            AppLogger.Log("Unhandled UI exception", args.Exception);
            System.Windows.MessageBox.Show(
                "The app hit an unexpected error. A crash log was written to:\n\n" + AppLogger.LogFilePath,
                "OneDrive Live Photo Viewer",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            args.Handled = true;
        };
    }
}
