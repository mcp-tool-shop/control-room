using ControlRoom.App.ViewModels;

namespace ControlRoom.App.Views;

public partial class FailuresPage : ContentPage
{
    private readonly FailuresViewModel _vm;

    public FailuresPage(FailuresViewModel vm)
    {
        InitializeComponent();
        _vm = vm;
        BindingContext = vm;
    }

    protected override void OnAppearing()
    {
        base.OnAppearing();
        _vm.RefreshCommand.ExecuteAsync(null);
    }
}
