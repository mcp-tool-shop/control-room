using ControlRoom.App.ViewModels;

namespace ControlRoom.App.Views;

public partial class CommandPaletteView : ContentView
{
    public CommandPaletteView()
    {
        InitializeComponent();
    }

    protected override void OnBindingContextChanged()
    {
        base.OnBindingContextChanged();

        if (BindingContext is CommandPaletteViewModel vm)
        {
            vm.Opened += OnPaletteOpened;
        }
    }

    private void OnPaletteOpened()
    {
        // Focus the search entry when palette opens
        Dispatcher.Dispatch(async () =>
        {
            await Task.Delay(50); // Small delay to ensure UI is ready
            SearchEntry.Focus();
        });
    }
}
