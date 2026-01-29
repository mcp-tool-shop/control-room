using ControlRoom.App.Views;
using ControlRoom.Infrastructure.Storage;

namespace ControlRoom.App;

public partial class App : Microsoft.Maui.Controls.Application
{
    private readonly IServiceProvider _services;
    private readonly AppSettings _settings;

    public App(Migrator migrator, AppSettings settings, IServiceProvider services)
    {
        InitializeComponent();

        _services = services;
        _settings = settings;

        var schemaSql = LoadSchemaSql();
        migrator.EnsureCreated(schemaSql);
    }

    protected override Window CreateWindow(IActivationState? activationState)
    {
        // Use MainHostPage which contains the Shell + Command Palette overlay
        var mainPage = _services.GetRequiredService<MainHostPage>();
        var window = new Window(mainPage);

        // Set up window state persistence (Windows-specific)
#if WINDOWS
        SetupWindowStatePersistence(window);
#endif

        return window;
    }

#if WINDOWS
    // Track the "restored" (non-maximized) bounds separately
    private double _restoredX = 100;
    private double _restoredY = 100;
    private double _restoredWidth = 1200;
    private double _restoredHeight = 800;

    // Debounce timer and pending state for shutdown safety
    private System.Timers.Timer? _saveDebounceTimer;
    private bool _hasPendingSave;
    private Window? _currentWindow;

    private void SetupWindowStatePersistence(Window window)
    {
        _currentWindow = window;

        // Restore saved state when window is created
        window.Created += (_, _) =>
        {
            var savedState = _settings.Get<WindowState>(AppSettings.Keys.WindowState);
            if (savedState is not null)
            {
                // Clamp bounds to valid work area
                var (x, y, w, h) = ClampToWorkArea(
                    savedState.X, savedState.Y,
                    savedState.Width, savedState.Height);

                _restoredX = x;
                _restoredY = y;
                _restoredWidth = w;
                _restoredHeight = h;

                // Apply bounds first
                window.X = x;
                window.Y = y;
                window.Width = w;
                window.Height = h;

                // Then apply maximized state
                if (savedState.IsMaximized)
                {
                    MainThread.BeginInvokeOnMainThread(async () =>
                    {
                        await Task.Delay(100);
                        MaximizeWindow(window);
                    });
                }
            }
            else
            {
                // Default window size for first run, centered
                var (x, y, w, h) = GetDefaultBounds();
                _restoredX = x;
                _restoredY = y;
                _restoredWidth = w;
                _restoredHeight = h;

                window.X = x;
                window.Y = y;
                window.Width = w;
                window.Height = h;
            }
        };

        // Save state when window is being destroyed - flush synchronously
        window.Destroying += (_, _) =>
        {
            FlushPendingSave();
        };

        // Debounced save on size/position changes
        _saveDebounceTimer = new System.Timers.Timer(500) { AutoReset = false };
        _saveDebounceTimer.Elapsed += (_, _) =>
        {
            MainThread.BeginInvokeOnMainThread(() =>
            {
                _hasPendingSave = false;
                SaveWindowState(window);
            });
        };

        window.SizeChanged += (_, _) =>
        {
            TrackRestoredBounds(window);
            ScheduleSave();
        };
    }

    private void TrackRestoredBounds(Window window)
    {
        // Only track bounds when not maximized/minimized
        var state = GetWindowState(window);
        if (state == WindowPresenterState.Normal)
        {
            _restoredX = window.X;
            _restoredY = window.Y;
            _restoredWidth = window.Width;
            _restoredHeight = window.Height;
        }
    }

    private void ScheduleSave()
    {
        _hasPendingSave = true;
        _saveDebounceTimer?.Stop();
        _saveDebounceTimer?.Start();
    }

    private void FlushPendingSave()
    {
        try
        {
            _saveDebounceTimer?.Stop();
            if (_hasPendingSave && _currentWindow is not null)
            {
                _hasPendingSave = false;
                SaveWindowState(_currentWindow);
            }
        }
        catch
        {
            // Don't block close on save failure
        }
    }

    private void SaveWindowState(Window window)
    {
        try
        {
            var presenterState = GetWindowState(window);

            // Don't persist minimized state - restore to previous state
            if (presenterState == WindowPresenterState.Minimized)
                return;

            var isMaximized = presenterState == WindowPresenterState.Maximized;

            // Always save the "restored" bounds, not the maximized bounds
            var state = new WindowState(
                X: _restoredX,
                Y: _restoredY,
                Width: _restoredWidth,
                Height: _restoredHeight,
                IsMaximized: isMaximized
            );

            _settings.Set(AppSettings.Keys.WindowState, state);
        }
        catch
        {
            // Silently ignore save failures
        }
    }

    private enum WindowPresenterState { Normal, Maximized, Minimized, Other }

    private static WindowPresenterState GetWindowState(Window window)
    {
        try
        {
            if (window.Handler?.PlatformView is Microsoft.UI.Xaml.Window winWindow)
            {
                var presenter = winWindow.AppWindow.Presenter as Microsoft.UI.Windowing.OverlappedPresenter;
                return presenter?.State switch
                {
                    Microsoft.UI.Windowing.OverlappedPresenterState.Maximized => WindowPresenterState.Maximized,
                    Microsoft.UI.Windowing.OverlappedPresenterState.Minimized => WindowPresenterState.Minimized,
                    Microsoft.UI.Windowing.OverlappedPresenterState.Restored => WindowPresenterState.Normal,
                    _ => WindowPresenterState.Other
                };
            }
        }
        catch { }
        return WindowPresenterState.Normal;
    }

    private static void MaximizeWindow(Window window)
    {
        try
        {
            if (window.Handler?.PlatformView is Microsoft.UI.Xaml.Window winWindow)
            {
                var presenter = winWindow.AppWindow.Presenter as Microsoft.UI.Windowing.OverlappedPresenter;
                presenter?.Maximize();
            }
        }
        catch { }
    }

    /// <summary>
    /// Clamp saved bounds to valid work area across all monitors.
    /// Falls back to defaults if bounds are invalid or off-screen.
    /// </summary>
    private static (double X, double Y, double Width, double Height) ClampToWorkArea(
        double x, double y, double width, double height)
    {
        const double MinSize = 400;
        const double MaxSize = 8000;
        const double DefaultWidth = 1200;
        const double DefaultHeight = 800;

        // Sanity check dimensions
        if (width < MinSize || width > MaxSize || height < MinSize || height > MaxSize)
        {
            return GetDefaultBounds();
        }

        try
        {
            // Get all display regions
            var displayInfo = Microsoft.Maui.Devices.DeviceDisplay.Current.MainDisplayInfo;
            var screenWidth = displayInfo.Width / displayInfo.Density;
            var screenHeight = displayInfo.Height / displayInfo.Density;

            // Check if window would be mostly visible
            // At least 100px of the window should be on-screen
            const double MinVisible = 100;

            bool isVisible =
                x + width > MinVisible &&  // Not too far left
                y + height > MinVisible && // Not too far up
                x < screenWidth - MinVisible && // Not too far right
                y < screenHeight - MinVisible;  // Not too far down

            if (!isVisible)
            {
                // Center on primary display
                return (
                    Math.Max(0, (screenWidth - DefaultWidth) / 2),
                    Math.Max(0, (screenHeight - DefaultHeight) / 2),
                    DefaultWidth,
                    DefaultHeight
                );
            }

            // Clamp to screen bounds if partially off
            var clampedX = Math.Max(0, Math.Min(x, screenWidth - width));
            var clampedY = Math.Max(0, Math.Min(y, screenHeight - height));

            return (clampedX, clampedY, width, height);
        }
        catch
        {
            return GetDefaultBounds();
        }
    }

    private static (double X, double Y, double Width, double Height) GetDefaultBounds()
    {
        const double DefaultWidth = 1200;
        const double DefaultHeight = 800;

        try
        {
            var displayInfo = Microsoft.Maui.Devices.DeviceDisplay.Current.MainDisplayInfo;
            var screenWidth = displayInfo.Width / displayInfo.Density;
            var screenHeight = displayInfo.Height / displayInfo.Density;

            return (
                Math.Max(0, (screenWidth - DefaultWidth) / 2),
                Math.Max(0, (screenHeight - DefaultHeight) / 2),
                DefaultWidth,
                DefaultHeight
            );
        }
        catch
        {
            return (100, 100, DefaultWidth, DefaultHeight);
        }
    }
#endif

    private static string LoadSchemaSql()
    {
        var asm = typeof(App).Assembly;
        using var s = asm.GetManifestResourceStream("ControlRoom.App.Schema.sql")!;
        using var r = new StreamReader(s);
        return r.ReadToEnd();
    }
}
