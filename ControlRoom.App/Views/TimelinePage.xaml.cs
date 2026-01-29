using ControlRoom.App.ViewModels;

namespace ControlRoom.App.Views;

public partial class TimelinePage : ContentPage
{
    private readonly TimelineViewModel _vm;

    public TimelinePage(TimelineViewModel vm)
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
