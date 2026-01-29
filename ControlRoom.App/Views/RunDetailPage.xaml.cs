using ControlRoom.App.ViewModels;

namespace ControlRoom.App.Views;

public partial class RunDetailPage : ContentPage
{
    public RunDetailPage(RunDetailViewModel vm)
    {
        InitializeComponent();
        BindingContext = vm;
    }
}
