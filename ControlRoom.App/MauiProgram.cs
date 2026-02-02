using Microsoft.Extensions.Logging;
using ControlRoom.Application.UseCases;
using ControlRoom.Application.Services;
using ControlRoom.Domain.Services;
using ControlRoom.Infrastructure.AI;
using ControlRoom.Infrastructure.Process;
using ControlRoom.Infrastructure.Storage;
using ControlRoom.Infrastructure.Storage.Queries;
using ControlRoom.App.ViewModels;
using ControlRoom.App.Views;

namespace ControlRoom.App;

public static class MauiProgram
{
    public static MauiApp CreateMauiApp()
    {
        var builder = MauiApp.CreateBuilder();
        builder
            .UseMauiApp<App>()
            .ConfigureFonts(fonts =>
            {
                fonts.AddFont("OpenSans-Regular.ttf", "OpenSansRegular");
                fonts.AddFont("OpenSans-Semibold.ttf", "OpenSansSemibold");
            });

        // Database
        var dbPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ControlRoom",
            "controlroom.db");

        builder.Services.AddSingleton(new Db(dbPath));
        builder.Services.AddSingleton<Migrator>(sp =>
        {
            var db = sp.GetRequiredService<Db>();
            return new Migrator(db);
        });

        // Infrastructure
        builder.Services.AddSingleton<IScriptRunner, ScriptRunner>();
        builder.Services.AddSingleton<IAIAssistant>(sp => new OllamaAIAssistant());

        // Queries
        builder.Services.AddSingleton<RunQueries>();
        builder.Services.AddSingleton<ThingQueries>();
        builder.Services.AddSingleton<ArtifactQueries>();
        builder.Services.AddSingleton<RunbookQueries>();
        builder.Services.AddSingleton<MetricsQueries>();

        // Settings
        builder.Services.AddSingleton<AppSettings>();

        // Use Cases
        builder.Services.AddSingleton<RunLocalScript>();
        builder.Services.AddSingleton<IRunbookExecutor, RunbookExecutor>();
        builder.Services.AddSingleton<ITriggerService, TriggerService>();
        builder.Services.AddSingleton<IRunbookTemplateService, RunbookTemplateService>();
        builder.Services.AddSingleton<IAlertEngine, AlertEngine>();

        // ViewModels
        builder.Services.AddTransient<TimelineViewModel>();
        builder.Services.AddTransient<ThingsViewModel>();
        builder.Services.AddTransient<RunDetailViewModel>();
        builder.Services.AddTransient<NewThingViewModel>();
        builder.Services.AddTransient<FailuresViewModel>();
        builder.Services.AddTransient<RunbooksViewModel>();
        builder.Services.AddTransient<RunbookDesignerViewModel>();
        builder.Services.AddTransient<DashboardViewModel>();

        // Command Palette (singleton so state persists across opens)
        builder.Services.AddSingleton<CommandPaletteViewModel>();

        // Pages
        builder.Services.AddTransient<TimelinePage>();
        builder.Services.AddTransient<ThingsPage>();
        builder.Services.AddTransient<RunDetailPage>();
        builder.Services.AddTransient<NewThingPage>();
        builder.Services.AddTransient<FailuresPage>();
        builder.Services.AddTransient<RunbooksPage>();
        builder.Services.AddTransient<RunbookDesignerPage>();
        builder.Services.AddTransient<DashboardPage>();
        builder.Services.AddSingleton<MainHostPage>();

#if DEBUG
        builder.Logging.AddDebug();
#endif

        return builder.Build();
    }
}
