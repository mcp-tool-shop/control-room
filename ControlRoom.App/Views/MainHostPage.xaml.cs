using ControlRoom.App.ViewModels;

namespace ControlRoom.App.Views;

public partial class MainHostPage : ContentPage
{
    private readonly CommandPaletteViewModel _paletteVm;

    public MainHostPage(CommandPaletteViewModel paletteVm)
    {
        InitializeComponent();
        _paletteVm = paletteVm;
        BindingContext = paletteVm;

        // Set up keyboard handling for Windows
        SetupKeyboardHandling();
    }

    private void SetupKeyboardHandling()
    {
#if WINDOWS
        // For MAUI on Windows, we use the Window's keyboard events
        Loaded += (_, _) =>
        {
            if (Window?.Handler?.PlatformView is Microsoft.UI.Xaml.Window winWindow)
            {
                winWindow.Content.KeyDown += OnKeyDown;
            }
        };
#endif
    }

#if WINDOWS
    private void OnKeyDown(object sender, Microsoft.UI.Xaml.Input.KeyRoutedEventArgs e)
    {
        // Ctrl+K or Ctrl+P to toggle palette
        var ctrl = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(Windows.System.VirtualKey.Control)
            .HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);

        if (ctrl && (e.Key == Windows.System.VirtualKey.K || e.Key == Windows.System.VirtualKey.P))
        {
            _paletteVm.Toggle();
            e.Handled = true;
            return;
        }

        // When palette is open, handle navigation keys
        if (_paletteVm.IsOpen)
        {
            switch (e.Key)
            {
                case Windows.System.VirtualKey.Escape:
                    _paletteVm.Close();
                    e.Handled = true;
                    break;

                case Windows.System.VirtualKey.Up:
                    _paletteVm.SelectPreviousCommand.Execute(null);
                    e.Handled = true;
                    break;

                case Windows.System.VirtualKey.Down:
                    _paletteVm.SelectNextCommand.Execute(null);
                    e.Handled = true;
                    break;

                case Windows.System.VirtualKey.Enter:
                    _paletteVm.ExecuteSelectedCommand.Execute(null);
                    e.Handled = true;
                    break;
            }
        }
    }
#endif
}
