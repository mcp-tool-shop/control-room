using ControlRoom.App.ViewModels;

namespace ControlRoom.App.Views;

public partial class ThingsPage : ContentPage
{
    private readonly ThingsViewModel _vm;

    public ThingsPage(ThingsViewModel vm)
    {
        InitializeComponent();
        _vm = vm;
        BindingContext = vm;
    }

    protected override void OnAppearing()
    {
        base.OnAppearing();
        // RelayCommand strips the "Async" suffix, so RefreshAsync becomes RefreshCommand
        _vm.RefreshCommand.ExecuteAsync(null);
    }
}
