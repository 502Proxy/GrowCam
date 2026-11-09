using Avalonia.Controls;
using GrowCam.App.ViewModels;

namespace GrowCam.App.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
    }

    /// <summary>Closing only hides the window: GrowCam keeps working in the tray.</summary>
    protected override void OnClosing(WindowClosingEventArgs e)
    {
        if (!App.IsQuitting)
        {
            e.Cancel = true;
            Hide();
            (DataContext as MainViewModel)?.OnWindowVisibilityChanged(false);
        }
        base.OnClosing(e);
    }

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        (DataContext as MainViewModel)?.OnWindowVisibilityChanged(true);
    }
}
