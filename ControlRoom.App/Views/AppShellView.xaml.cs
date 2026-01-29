using ControlRoom.Infrastructure.Storage;

namespace ControlRoom.App.Views;

public partial class AppShellView : Shell
{
    private readonly AppSettings? _settings;

    public AppShellView()
    {
        InitializeComponent();

        // Register detail page routes
        Routing.RegisterRoute("run", typeof(RunDetailPage));
        Routing.RegisterRoute("thing/new", typeof(NewThingPage));

        // Try to get settings from DI (may be null during design time)
        _settings = IPlatformApplication.Current?.Services.GetService<AppSettings>();

        // Track navigation to save last route
        Navigated += OnNavigated;
    }

    protected override async void OnParentSet()
    {
        base.OnParentSet();

        // Restore last route on startup (after shell is fully loaded)
        if (_settings is not null)
        {
            var stableId = _settings.Get<string>(AppSettings.Keys.LastRoute);
            var routeString = AppSettings.Routes.ToRouteString(stableId);

            if (!string.IsNullOrEmpty(routeString))
            {
                try
                {
                    // Small delay to ensure shell is ready
                    await Task.Delay(100);
                    await GoToAsync(routeString);
                }
                catch
                {
                    // Ignore navigation failures (route may no longer be valid)
                }
            }
        }
    }

    private void OnNavigated(object? sender, ShellNavigatedEventArgs e)
    {
        if (_settings is null) return;

        // Convert route string to stable ID (only for top-level routes)
        var location = e.Current?.Location?.ToString();
        var stableId = AppSettings.Routes.ToStableId(location);

        if (stableId is not null)
        {
            _settings.Set(AppSettings.Keys.LastRoute, stableId);
        }
    }
}
