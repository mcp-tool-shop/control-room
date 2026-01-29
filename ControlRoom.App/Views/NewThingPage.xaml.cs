using ControlRoom.App.ViewModels;

namespace ControlRoom.App.Views;

public partial class NewThingPage : ContentPage
{
    public NewThingPage(NewThingViewModel vm)
    {
        InitializeComponent();
        BindingContext = vm;
    }
}
