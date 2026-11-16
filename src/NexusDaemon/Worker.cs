using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using NexusDaemon.Configuration;
using NexusDaemon.Phase1_Gateways;
using NexusDaemon.Phase2_StateAndMemory;
using NexusDaemon.Phase3_Router;
using NexusDaemon.Phase4_ToolsAndEvents;
using NexusDaemon.Phase4_ToolsAndEvents.AgentRunners;
using NexusDaemon.Phase5_MemoryAndGuardrails;

namespace NexusDaemon;

public static class NexusServiceCollectionExtensions
{
    public static IServiceCollection AddNexusDaemon(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.Configure<NexusOptions>(configuration.GetSection(NexusOptions.SectionName));

        services.AddHttpClient("GeminiApi");
        services.AddHttpClient("TelegramApi");
        services.AddHttpClient("AntigravityHub");

        // Phase 2: State & Memory Engine Singletons
        services.AddSingleton<ModelCatalog>();
        services.AddSingleton<SlidingWindowQueue>();
        services.AddSingleton<IGitStatusProvider, ProcessGitStatusProvider>();
        services.AddSingleton<StateManager>();

        // Phase 5: Long-Term Memory & Guardrails
        services.AddSingleton<ProjectFileReader>();
        services.AddSingleton<HitlApprovalInterceptor>();

        // Phase 4: Toolset, Agent Runners, Build Runner & Event Bus
        services.AddSingleton<NexusEventBus>();
        services.AddSingleton<CliStreamJsonRunner>();
        services.AddSingleton<AntigravityDaemonRunner>();
        services.AddSingleton<IAgentRunner, ConfigurableAgentRunner>();
        services.AddSingleton<IBuildRunner, BuildRunner>();
        services.AddSingleton<NexusToolDispatcher>();
        services.AddHostedService<NexusEventConsumerService>();

        // Phase 1: The Director (Telegram Bot Gateway)
        services.AddSingleton<TelegramChatGateway>();
        services.AddSingleton<IChatGateway>(sp => sp.GetRequiredService<TelegramChatGateway>());
        services.AddHostedService(sp => sp.GetRequiredService<TelegramChatGateway>());

        // Phase 3: Stateless Nexus Router
        services.AddSingleton<NexusRouterService>();

        return services;
    }
}
