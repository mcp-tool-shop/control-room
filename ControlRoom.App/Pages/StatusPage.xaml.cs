using ControlRoom.App.ViewModels;

namespace ControlRoom.App.Pages;

public partial class StatusPage : ContentPage
{
    private readonly StatusPageViewModel _viewModel;

    public StatusPage(StatusPageViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        BindingContext = viewModel;
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        await _viewModel.LoadAsync();
    }

    protected override void OnDisappearing()
    {
        base.OnDisappearing();
        _viewModel.Cleanup();
    }
}
