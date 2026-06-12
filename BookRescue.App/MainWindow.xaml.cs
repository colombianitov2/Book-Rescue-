using System.Windows;
using BookRescue.App.ViewModels;

namespace BookRescue.App;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        DataContext = new MainWindowViewModel();
    }

    protected override void OnContentRendered(EventArgs e)
    {
        base.OnContentRendered(e);

        if (DataContext is MainWindowViewModel viewModel)
        {
            viewModel.ShowHardwareWarningIfNeeded();
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        if (DataContext is IDisposable disposable)
        {
            disposable.Dispose();
        }

        base.OnClosed(e);
    }
}
