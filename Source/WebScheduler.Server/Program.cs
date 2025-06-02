namespace WebScheduler.Server;

using System.Runtime.InteropServices;
using Newtonsoft.Json;
using Newtonsoft.Json.Serialization;
using Orleans;
using Orleans.Configuration;
using Orleans.Hosting;
using Orleans.Runtime;
using Orleans;
using Orleans.Statistics;
using WebScheduler.Server.Options;
using Serilog;
using Serilog.Extensions.Hosting;
using Boxed.AspNetCore;
using Serilog.Formatting.Compact;
using Orleans.Versions.Compatibility;
using Orleans.Versions.Selector;
using WebScheduler.Server.HealthChecks;
using System.IO;
using WebScheduler.Grains.Scheduler;
using WebScheduler.Abstractions.Grains.Scheduler;
using WebScheduler.Grains.Constants;
using WebScheduler.Abstractions.Grains.History;
using Orleans.Serialization;


public class Program
{
    public static async Task<int> Main(string[] args)
    {
        Log.Logger = CreateBootstrapLogger();
        IHost? host = null;

        try
        {
            host = CreateHostBuilder(args).Build();

            host.LogApplicationStarted();
            await host.RunAsync();
            host!.LogApplicationStopped();

            return 0;
        }
        catch (OrleansLifecycleCanceledException)
        {
            return 0;
        }
        catch (Exception exception)
        {
            host!.LogApplicationTerminatedUnexpectedly(exception);

            return 1;
        }
        finally
        {
            Log.CloseAndFlush();
        }
    }

    private static IHostBuilder CreateHostBuilder(string[] args) =>
        new HostBuilder()
            .UseContentRoot(Directory.GetCurrentDirectory())
            .ConfigureHostConfiguration(
                configurationBuilder => configurationBuilder.AddCustomBootstrapConfiguration(args))
            .ConfigureAppConfiguration(
                (hostingContext, configurationBuilder) =>
                {
                    hostingContext.HostingEnvironment.ApplicationName = AssemblyInformation.Current.Product;
                    _ = configurationBuilder.AddCustomConfiguration(hostingContext.HostingEnvironment, args);
                })
            .UseSerilog(ConfigureReloadableLogger)
            .UseDefaultServiceProvider(
                (context, options) =>
                {
                    var isDevelopment = context.HostingEnvironment.IsDevelopment();
                    options.ValidateScopes = isDevelopment;
                    options.ValidateOnBuild = isDevelopment;
                })
            .UseOrleans(ConfigureSiloBuilder)
            .ConfigureWebHost(ConfigureWebHostBuilder)
            .UseConsoleLifetime();

    private static void ConfigureSiloBuilder(
        Microsoft.Extensions.Hosting.HostBuilderContext context,
        ISiloBuilder siloBuilder) =>
        siloBuilder
        //.AddActivityPropagation()
            .ConfigureServices(
                services =>
                {
                    _ = services.ConfigureAndValidateSingleton<ApplicationOptions>(context.Configuration);
                    _ = services.ConfigureAndValidateSingleton<ClusterMembershipOptions>(context.Configuration.GetSection(nameof(ApplicationOptions.ClusterMembership)));
                    _ = services.ConfigureAndValidateSingleton<ClusterOptions>(context.Configuration.GetSection(nameof(ApplicationOptions.Cluster)));
                    _ = services.ConfigureAndValidateSingleton<StorageOptions>(context.Configuration.GetSection(nameof(ApplicationOptions.Storage)));
                })
            .Configure<GrainVersioningOptions>(options =>
            {
                options.DefaultCompatibilityStrategy = nameof(BackwardCompatible);
                options.DefaultVersionSelectorStrategy = nameof(AllCompatibleVersions);
            })
            .UseDashboard(options => GetOrleansDashboardOptions(context.Configuration).Bind(options))
            
            //.AddActivityPropagation()
            //TODO: never find this method before, check how to deal with it
            //.UseSiloUnobservedExceptionsHandler()
            .UseAdoNetClustering(options =>options.Bind(GetStorageOptions(context.Configuration)))
            .ConfigureEndpoints(
                EndpointOptions.DEFAULT_SILO_PORT,
                EndpointOptions.DEFAULT_GATEWAY_PORT,
                listenOnAnyHostAddress: !context.HostingEnvironment.IsDevelopment())
                .Services.AddSerializer(builder => builder.AddJsonSerializer(type=> type.Namespace.Contains("WebScheduler.Abstractions") ))
            //.AddStorageInterceptors()
             .AddAdoNetGrainStorageAsDefault( options => options.Bind(GetStorageOptions(context.Configuration)))
            .AddAdoNetGrainStorage(GrainStorageProviderName.ScheduledTaskState, options =>  options.Bind(GetStorageOptions(context.Configuration)))
           //TODO: Check how to do it in orleans 9
            // .UseGenericStorageInterceptor<ScheduledTaskState>(GrainStorageProviderName.ScheduledTaskState, StateName.ScheduledTaskState, o =>
            // {
            //     o.OnBeforeWriteStateFunc = (grainActivationContext, currentState) => new((false, (object?)null));
            //     o.OnAfterWriteStateFunc = (grainActivationContext, currentState, sharedState) => ValueTask.CompletedTask;

            //     o.OnBeforeClearStateAsync = (grainActivationContext, currentState) => new((false, (object?)null));
            //     o.OnAfterClearStateAsync = (grainActivationContext, currentState, sharedState) => ValueTask.CompletedTask;

            //     o.OnBeforeReadStateAsync = (grainActivationContext, currentState) => new((false, (object?)null));
            //     o.OnAfterReadStateFunc = (grainActivationContext, currentState, sharedState) => ValueTask.CompletedTask;
            // }) // simulate non-op
            .AddAdoNetGrainStorage(GrainStorageProviderName.ScheduledTaskMetadataHistory, options =>  options.Bind(GetStorageOptions(context.Configuration)))
                //TODO: Check how to do it in orleans 9
            // .UseGenericStorageInterceptor<HistoryState<ScheduledTaskMetadata, ScheduledTaskOperationType>>(GrainStorageProviderName.ScheduledTaskMetadataHistory, StateName.ScheduledTaskMetadataHistory, o =>
            // {
            //     o.OnBeforeWriteStateFunc = (grainActivationContext, currentState) => new((false, (object?)null));
            //     o.OnAfterWriteStateFunc = (grainActivationContext, currentState, sharedState) => ValueTask.CompletedTask;

            //     o.OnBeforeClearStateAsync = (grainActivationContext, currentState) => new((false, (object?)null));
            //     o.OnAfterClearStateAsync = (grainActivationContext, currentState, sharedState) => ValueTask.CompletedTask;

            //     o.OnBeforeReadStateAsync = (grainActivationContext, currentState) => new((false, (object?)null));
            //     o.OnAfterReadStateFunc = (grainActivationContext, currentState, sharedState) => ValueTask.CompletedTask;
            // }) // simulate non-op

            .AddAdoNetGrainStorage(GrainStorageProviderName.ScheduledTaskTriggerHistory, options => options.Bind(GetStorageOptions(context.Configuration)))
            //TODO: Check how to do it in orleans 9
            // .UseGenericStorageInterceptor<HistoryState<ScheduledTaskTriggerHistory, TaskTriggerType>>(StateName.ScheduledTaskTriggerHistory, GrainStorageProviderName.ScheduledTaskTriggerHistory, o =>
            // {
            //     o.OnBeforeWriteStateFunc = (grainActivationContext, currentState) => new((false, (object?)null));
            //     o.OnAfterWriteStateFunc = (grainActivationContext, currentState, sharedState) => ValueTask.CompletedTask;

            //     o.OnBeforeClearStateAsync = (grainActivationContext, currentState) => new((false, (object?)null));
            //     o.OnAfterClearStateAsync = (grainActivationContext, currentState, sharedState) => ValueTask.CompletedTask;

            //     o.OnBeforeReadStateAsync = (grainActivationContext, currentState) => new((false, (object?)null));
            //     o.OnAfterReadStateFunc = (grainActivationContext, currentState, sharedState) => ValueTask.CompletedTask;
            // }) // simulate non-op
            .UseAdoNetReminderService(options => options.Bind(GetStorageOptions(context.Configuration)))
            //TODO: Is there need for this in orleans 9?
            // .UseIf(RuntimeInformation.IsOSPlatform(OSPlatform.Linux), x => x.UseLinuxEnvironmentStatistics())
            // .UseIf(RuntimeInformation.IsOSPlatform(OSPlatform.Windows), x => x.UsePerfCounterEnvironmentStatistics())
            //
            ;

    private static void ConfigureWebHostBuilder(IWebHostBuilder webHostBuilder) =>
        webHostBuilder
            .UseKestrel(
                (builderContext, options) =>
                {
                    options.AddServerHeader = false;
                    _ = options.Configure(
                        builderContext.Configuration.GetSection(nameof(ApplicationOptions.Kestrel)),
                        reloadOnChange: false);
                })
            .UseStartup<Startup>();

    /// <summary>
    /// Creates a logger used during application initialisation.
    /// <see href="https://nblumhardt.com/2020/10/bootstrap-logger/"/>.
    /// </summary>
    /// <returns>A logger that can load a new configuration.</returns>
    private static ReloadableLogger CreateBootstrapLogger() =>
        new LoggerConfiguration()
            .WriteTo.Console(new CompactJsonFormatter())
            .CreateBootstrapLogger();

    /// <summary>
    /// Configures a logger used during the applications lifetime.
    /// <see href="https://nblumhardt.com/2020/10/bootstrap-logger/"/>.
    /// </summary>
    /// <param name="context">The context.</param>
    /// <param name="services">The services.</param>
    /// <param name="configuration">The configuration.</param>
    private static void ConfigureReloadableLogger(
        Microsoft.Extensions.Hosting.HostBuilderContext context,
        IServiceProvider services,
        LoggerConfiguration configuration) =>
        configuration
            .ReadFrom.Configuration(context.Configuration)
            .ReadFrom.Services(services)
            .Enrich.WithProperty("Application", context.HostingEnvironment.ApplicationName)
            .Enrich.WithProperty("Environment", context.HostingEnvironment.EnvironmentName);

    private static void ConfigureJsonSerializerSettings(JsonSerializerSettings jsonSerializerSettings)
    {
        jsonSerializerSettings.ContractResolver = new CamelCasePropertyNamesContractResolver();
        jsonSerializerSettings.DateParseHandling = DateParseHandling.DateTimeOffset;
    }

    private static IConfigurationSection GetStorageOptions(IConfiguration configuration) =>
        configuration.GetSection(nameof(ApplicationOptions.Storage));

    private static IConfigurationSection GetOrleansDashboardOptions(IConfiguration configuration) =>
        configuration.GetSection(nameof(ApplicationOptions.OrleansDashboard));
}
