using ControlRoom.App.Views;

namespace ControlRoom.App;

public partial class AppShell : Shell
{
    public AppShell()
    {
        InitializeComponent();

        // Register detail page routes
        Routing.RegisterRoute("run", typeof(RunDetailPage));
        Routing.RegisterRoute("thing/new", typeof(NewThingPage));
    }
}
