using Cleaner.App.Pages;
using Wpf.Ui.Controls;

namespace Cleaner.App;

public partial class MainWindow : FluentWindow
{
    public MainWindow()
    {
        InitializeComponent();
        if (App.SystemInfo.IsElevated) AppTitleBar.Title = Title = "Cleaner — администратор";
        Loaded += (_, _) => RootNavigation.Navigate(typeof(CleanPage));
    }
}
