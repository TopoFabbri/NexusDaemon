using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NexusDaemon.Configuration;
using NexusDaemon.Phase1_Gateways;
using NexusDaemon.Phase2_StateAndMemory;
using NexusDaemon.Phase3_Router;
using NexusDaemon.Phase4_ToolsAndEvents;
using NexusDaemon.Phase4_ToolsAndEvents.AgentRunners;
using NexusDaemon.Phase5_MemoryAndGuardrails;

namespace NexusDaemon.Tests;

public sealed class NexusDaemonTests
{
    [Fact]
    public void SlidingWindowQueue_EnforcesMaxCapacityAndStartsWithUserRole()
    {
        var queue = new SlidingWindowQueue(maxMessages: 6);

        for (var i = 1; i <= 5; i++)
        {
            queue.Enqueue("user", $"User turn {i}");
            queue.Enqueue("model", $"Model turn {i}");
        }

        var snapshot = queue.GetSnapshot();
        Assert.Equal(6, snapshot.Count);
        Assert.Equal("user", snapshot[0].Role);
        Assert.Equal("User turn 3", snapshot[0].Content);
        Assert.Equal("model", snapshot[^1].Role);
        Assert.Equal("Model turn 5", snapshot[^1].Content);
    }

    [Fact]
    public void SlidingWindowQueue_DropsLeadingModelMessageToMaintainAlternationValidity()
    {
        var queue = new SlidingWindowQueue(maxMessages: 5);

        queue.Enqueue("user", "u1");
        queue.Enqueue("model", "m1");
        queue.Enqueue("user", "u2");
        queue.Enqueue("model", "m2");
        queue.Enqueue("user", "u3");
        // Adding m3 pushes count to 6 -> dequeues u1 -> top is m1 -> also dequeued so window starts with u2!
        queue.Enqueue("model", "m3");

        var snapshot = queue.GetSnapshot();
        Assert.Equal(4, snapshot.Count);
        Assert.Equal("user", snapshot[0].Role);
        Assert.Equal("u2", snapshot[0].Content);
        Assert.Equal("m3", snapshot[^1].Content);
    }

    [Fact]
    public void ModelCatalog_DefaultsToHydrogen4AndSupportsAllModels()
    {
        var options = Options.Create(new NexusOptions());
        var catalog = new ModelCatalog(options);

        Assert.Equal("hydrogen-4", catalog.NormalizeModelId(null));
        Assert.Equal("hydrogen-4", catalog.NormalizeModelId("hydrogen 4"));
        Assert.Equal("hydrogen-4", catalog.NormalizeModelId("hydrogen-4"));
        Assert.Equal("gemini-3.1-pro-preview", catalog.NormalizeModelId("pro"));
        Assert.Equal("gemini-3.1-pro-preview", catalog.NormalizeModelId("gemini-2.5-pro"));
        Assert.Equal("gemini-3.1-pro-preview", catalog.NormalizeModelId("gemini 2.5 pro"));
        Assert.Equal("gemini-3.1-pro-preview", catalog.NormalizeModelId("2.5-pro"));
        Assert.Equal("gemini-3.1-pro-preview", catalog.NormalizeModelId("gemini-pro"));
        Assert.Equal("gemini-3.1-pro-preview", catalog.NormalizeModelId("models/gemini-2.5-pro"));
        Assert.Equal("gemini-3.1-pro-preview", catalog.NormalizeModelId(" models/gemini-2.5-pro "));
        Assert.Equal("gemini-3.1-pro-preview", catalog.NormalizeModelId("models/2.5-pro"));
        Assert.Equal("hydrogen-4", catalog.NormalizeModelId("models/hydrogen-4"));
        Assert.Equal("gemini-2.5-flash", catalog.NormalizeModelId("flash"));
        Assert.Equal("gemini-3.8-flash-high", catalog.NormalizeModelId("gemini-3.8-flash-high"));
        Assert.Equal("any-custom-model-v42", catalog.NormalizeModelId("any-custom-model-v42"));
    }

    [Fact]
    public async Task StateManager_TracksWorkersBuildsModelsAndGitStatus()
    {
        var options = Options.Create(CreateTestOptions());
        var catalog = new ModelCatalog(options);
        var gitProvider = new FakeGitStatusProvider(new GitRepoStatus("HordePunkMaidens", "main", 2, true));
        var stateManager = new StateManager(options, catalog, gitProvider);

        Assert.Equal("hydrogen-4", stateManager.ActiveRouterModel);
        Assert.Equal("hydrogen-4", stateManager.ActiveWorkerModel);

        stateManager.RegisterWorker(
            "agy-01",
            "HordePunkMaidens",
            "modifying FishNet network behaviors",
            model: "hydrogen 4",
            processId: 18420);

        var summary = await stateManager.BuildSystemStateSummaryAsync();
        Assert.Contains("RouterModel=hydrogen-4", summary);
        Assert.Contains("HordePunkMaidens (branch: main, 2 modified/untracked files)", summary);
        Assert.Contains("1 active agy worker(s) [PID 18420 (hydrogen-4): modifying FishNet network behaviors]", summary);
        Assert.Contains("0 active builds", summary);

        stateManager.CompleteWorker("agy-01", out var removed);
        Assert.NotNull(removed);
        Assert.Empty(stateManager.ActiveWorkers);
    }

    [Fact]
    public void ProcessGitStatusProvider_ParsesPorcelainBranchOutputCorrectly()
    {
        var porcelain = "## feature/combat...origin/feature/combat [ahead 1]\n M Assets/Scripts/Attack.cs\n?? Architecture.md\n";
        var parsed = ProcessGitStatusProvider.ParsePorcelainBranchOutput("HordePunkMaidens", porcelain);

        Assert.True(parsed.IsAvailable);
        Assert.Equal("feature/combat", parsed.Branch);
        Assert.Equal(2, parsed.ModifiedOrUntrackedCount);
    }

    [Fact]
    public async Task ProjectFileReader_ReadsScaffoldedArchitectureAndLoreAndRejectsPathTraversal()
    {
        var options = Options.Create(CreateTestOptions());
        var reader = new ProjectFileReader(options);

        var archResult = await reader.ReadFileAsync("HordePunkMaidens", "Architecture.md");
        Assert.True(archResult.Success, archResult.Content);
        Assert.Contains("Metronome Attack & Hitbox System", archResult.Content);
        Assert.Contains("FishNet", archResult.Content);

        var loreResult = await reader.ReadFileAsync("HordePunkMaidens", "Lore.md");
        Assert.True(loreResult.Success, loreResult.Content);
        Assert.Contains("HordePunkMaidens", loreResult.Content);

        var nestedScriptResult = await reader.ReadFileAsync("HordePunkMaidens", "Assets/Scripts/Controller/ServiceManager.cs");
        Assert.True(nestedScriptResult.Success, nestedScriptResult.Content);
        Assert.Contains("class ServiceManager", nestedScriptResult.Content);

        var traversalResult = await reader.ReadFileAsync("HordePunkMaidens", "../../Windows/win.ini");
        Assert.False(traversalResult.Success);
        Assert.Contains("escapes the sandboxed project root", traversalResult.Content);
    }

    [Fact]
    public void BuildRunner_ReadsUnityVersionAndResolvesDiagnosticOrCommand()
    {
        var unityVersion = BuildRunner.TryReadUnityEditorVersion(@"D:\Files\Repos\HordePunkMaidens\HordePunkMaidens");
        Assert.Equal("6000.3.8f1", unityVersion);
    }

    [Fact]
    public async Task StreamJsonParser_DetectsTerminalResultAndHandlesNoiseLines()
    {
        var ndjson = string.Join("\n",
        [
            "Starting headless worker...",
            """{"type":"system","subtype":"init","model":"hydrogen-4"}""",
            """{"type":"assistant","content":"Inspecting NetworkedAttackExecutor.cs..."}""",
            """{"type":"result","status":"completed","is_error":false,"result":"Refactored FishNet hitbox pooling."}""",
            """{"type":"ignored_after_terminal"}"""
        ]);

        using var reader = new StringReader(ndjson);
        var outcome = await StreamJsonParser.ParseStreamToCompletionAsync(reader);

        Assert.True(outcome.IsTerminalFound);
        Assert.True(outcome.IsSuccess);
        Assert.Contains("Refactored FishNet hitbox pooling.", outcome.FinalResultJson);
        Assert.Equal(4, outcome.LinesProcessed);
    }

    [Fact]
    public async Task NexusRouterService_StatelessReadFileHop_InjectsArchitectureMdAndReplies()
    {
        var callCount = 0;
        var capturedBodies = new List<string>();

        var handler = new StubHttpMessageHandler((req, body) =>
        {
            callCount++;
            capturedBodies.Add(body);

            if (callCount == 1)
            {
                // First stateless request -> Model asks to ReadFile("HordePunkMaidens", "Architecture.md")
                return CreateJsonResponse(HttpStatusCode.OK, """
                {
                  "candidates": [{
                    "content": {
                      "role": "model",
                      "parts": [{
                        "functionCall": {
                          "name": "ReadFile",
                          "args": {
                            "projectName": "HordePunkMaidens",
                            "relativePath": "Architecture.md"
                          }
                        }
                      }]
                    }
                  }]
                }
                """);
            }

            // Second stateless request -> Model receives Architecture.md content and answers the user
            return CreateJsonResponse(HttpStatusCode.OK, """
            {
              "candidates": [{
                "content": {
                  "role": "model",
                  "parts": [{
                    "text": "According to Architecture.md, UI components must subscribe to EventBus or query UIService/HealthService with texel-perfect readability."
                  }]
                }
              }]
            }
            """);
        });

        var harness = CreateRouterHarness(handler);
        var result = await harness.Router.HandleUserMessageAsync("How should the new UI look and be decoupled?");

        Assert.Equal(2, callCount);
        Assert.Contains("ReadFile", result.InvokedTools);
        Assert.Contains("According to Architecture.md", result.ReplyText);
        Assert.Contains("Strict Assembly Decoupling", capturedBodies[1]);
        Assert.Contains("FishNet", capturedBodies[1]);
        Assert.Single(harness.ChatGateway.SentMessages);
    }

    [Fact]
    public async Task NexusRouterService_HitlDestructiveGate_HoldsUntilUserRepliesYOrN()
    {
        var handler = new StubHttpMessageHandler((_, _) =>
            CreateJsonResponse(HttpStatusCode.OK, """
            {
              "candidates": [{
                "content": {
                  "role": "model",
                  "parts": [{
                    "functionCall": {
                      "name": "DelegateTask",
                      "args": {
                        "projectName": "HordePunkMaidens",
                        "prompt": "Delete legacy prefabs and reset scene hierarchy",
                        "isDestructive": true
                      }
                    }
                  }]
                }
              }]
            }
            """));

        var harness = CreateRouterHarness(handler);

        // 1. User requests destructive operation -> Router holds in StateManager.PendingApproval
        var turn1 = await harness.Router.HandleUserMessageAsync("Delete the legacy prefabs in HordePunkMaidens");
        Assert.True(turn1.AwaitingHitlApproval);
        Assert.NotNull(harness.StateManager.PendingApproval);
        Assert.Equal(0, harness.AgentRunner.StartCount);
        Assert.Contains("HITL Confirmation Required", turn1.ReplyText);

        // 2. User replies "Y" -> Pending approval is consumed and worker starts!
        var turn2 = await harness.Router.HandleUserMessageAsync("Y");
        Assert.False(turn2.AwaitingHitlApproval);
        Assert.Null(harness.StateManager.PendingApproval);
        Assert.Equal(1, harness.AgentRunner.StartCount);
        Assert.Contains("Confirmed Request", turn2.ReplyText);
    }

    [Fact]
    public async Task NexusRouterService_HitlDestructiveGate_CancelsWhenUserRepliesN()
    {
        var handler = new StubHttpMessageHandler((_, _) =>
            CreateJsonResponse(HttpStatusCode.OK, """
            {
              "candidates": [{
                "content": {
                  "role": "model",
                  "parts": [{
                    "functionCall": {
                      "name": "DelegateTask",
                      "args": {
                        "projectName": "HordePunkMaidens",
                        "prompt": "Wipe SaveService storage",
                        "isDestructive": true
                      }
                    }
                  }]
                }
              }]
            }
            """));

        var harness = CreateRouterHarness(handler);

        await harness.Router.HandleUserMessageAsync("Wipe SaveService storage");
        Assert.NotNull(harness.StateManager.PendingApproval);

        var cancelTurn = await harness.Router.HandleUserMessageAsync("N");
        Assert.Null(harness.StateManager.PendingApproval);
        Assert.Equal(0, harness.AgentRunner.StartCount);
        Assert.Contains("Cancelled destructive action", cancelTurn.ReplyText);
    }

    [Fact]
    public async Task NexusRouterService_FallsBackFromCodenameModelWhenEndpointReturns404()
    {
        var requestedUrls = new List<string>();
        var handler = new StubHttpMessageHandler((req, _) =>
        {
            var url = req.RequestUri!.ToString();
            requestedUrls.Add(url);

            if (url.Contains("hydrogen-4", StringComparison.OrdinalIgnoreCase))
            {
                return CreateJsonResponse(HttpStatusCode.NotFound, """{"error":{"message":"model hydrogen-4 not found"}}""");
            }

            return CreateJsonResponse(HttpStatusCode.OK, """
            {
              "candidates": [{
                "content": {
                  "role": "model",
                  "parts": [{ "text": "Fallback model succeeded seamlessly." }]
                }
              }]
            }
            """);
        });

        var harness = CreateRouterHarness(handler);
        var result = await harness.Router.HandleUserMessageAsync("Status report");

        Assert.Equal(2, requestedUrls.Count);
        Assert.Contains("hydrogen-4:generateContent", requestedUrls[0]);
        Assert.Contains("gemini-3.1-pro-preview:generateContent", requestedUrls[1]);
        Assert.Equal("gemini-3.1-pro-preview", result.ModelUsed);
        Assert.Equal("Fallback model succeeded seamlessly.", result.ReplyText);
    }

    [Fact]
    public async Task TelegramChatGateway_AllowsSwitchingToAllModelsViaSlashCommand()
    {
        var handler = new StubHttpMessageHandler((_, _) => CreateJsonResponse(HttpStatusCode.OK, """{"ok":true}"""));
        var harness = CreateRouterHarness(handler);

        var services = new ServiceCollection();
        services.AddSingleton(harness.Router);
        using var sp = services.BuildServiceProvider();

        var gateway = new TelegramChatGateway(
            Options.Create(CreateTestOptions()),
            harness.StateManager,
            harness.SlidingWindow,
            harness.ModelCatalog,
            new StubHttpClientFactory(handler),
            sp,
            NullLogger<TelegramChatGateway>.Instance);

        await gateway.ProcessIncomingMessageAsync(6622366604, "/model gemini-2.5-flash", harness.Router);
        Assert.Equal("gemini-2.5-flash", harness.StateManager.ActiveRouterModel);
        Assert.Equal("gemini-2.5-flash", harness.StateManager.ActiveWorkerModel);

        await gateway.ProcessIncomingMessageAsync(6622366604, "/model gemini-2.5-pro", harness.Router);
        Assert.Equal("gemini-3.1-pro-preview", harness.StateManager.ActiveRouterModel);
        Assert.Equal("gemini-3.1-pro-preview", harness.StateManager.ActiveWorkerModel);

        await gateway.ProcessIncomingMessageAsync(6622366604, "/model hydrogen 4", harness.Router);
        Assert.Equal("hydrogen-4", harness.StateManager.ActiveRouterModel);
        Assert.Equal("hydrogen-4", harness.StateManager.ActiveWorkerModel);

        await gateway.ProcessIncomingMessageAsync(6622366604, "/model custom-omni-model-2027", harness.Router);
        Assert.Equal("custom-omni-model-2027", harness.StateManager.ActiveRouterModel);
        Assert.Equal("custom-omni-model-2027", harness.StateManager.ActiveWorkerModel);

        await gateway.ProcessIncomingMessageAsync(6622366604, "/model models/gemini-2.5-pro", harness.Router);
        Assert.Equal("gemini-3.1-pro-preview", harness.StateManager.ActiveRouterModel);
        Assert.Equal("gemini-3.1-pro-preview", harness.StateManager.ActiveWorkerModel);

        await gateway.ProcessIncomingMessageAsync(6622366604, "/model  models/gemini-2.5-pro ", harness.Router);
        Assert.Equal("gemini-3.1-pro-preview", harness.StateManager.ActiveRouterModel);
        Assert.Equal("gemini-3.1-pro-preview", harness.StateManager.ActiveWorkerModel);

        await gateway.ProcessIncomingMessageAsync(6622366604, "/model models/2.5-pro", harness.Router);
        Assert.Equal("gemini-3.1-pro-preview", harness.StateManager.ActiveRouterModel);
        Assert.Equal("gemini-3.1-pro-preview", harness.StateManager.ActiveWorkerModel);

        await gateway.ProcessIncomingMessageAsync(6622366604, "/model models/hydrogen-4", harness.Router);
        Assert.Equal("hydrogen-4", harness.StateManager.ActiveRouterModel);
        Assert.Equal("hydrogen-4", harness.StateManager.ActiveWorkerModel);
    }

    private static NexusOptions CreateTestOptions()
    {
        return new NexusOptions
        {
            Gemini = new GeminiOptions
            {
                ApiKey = "test-api-key",
                DefaultModel = "hydrogen-4",
                FallbackModel = "gemini-3.1-pro-preview",
                SlidingWindowSize = 10
            },
            Telegram = new TelegramOptions
            {
                Enabled = true,
                BotToken = "test-bot-token",
                AllowedChatId = 6622366604
            },
            Runner = new RunnerOptions
            {
                Mode = "AntigravityDaemon",
                DefaultWorkerModel = "hydrogen-4"
            },
            Projects = new Dictionary<string, ProjectOptions>(StringComparer.OrdinalIgnoreCase)
            {
                ["HordePunkMaidens"] = new ProjectOptions
                {
                    RootPath = @"D:\Files\Repos\HordePunkMaidens",
                    UnityProjectPath = @"D:\Files\Repos\HordePunkMaidens\HordePunkMaidens",
                    BuildExecutableOutput = @"D:\Files\Repos\HordePunkMaidens\HordePunkMaidens\Builds\Windows\HordePunkMaidens.exe"
                }
            }
        };
    }

    private static RouterTestHarness CreateRouterHarness(StubHttpMessageHandler httpHandler)
    {
        var options = Options.Create(CreateTestOptions());
        var modelCatalog = new ModelCatalog(options);
        var slidingWindow = new SlidingWindowQueue(options);
        var gitProvider = new FakeGitStatusProvider(new GitRepoStatus("HordePunkMaidens", "main", 2, true));
        var stateManager = new StateManager(options, modelCatalog, gitProvider);
        var agentRunner = new FakeAgentRunner(stateManager);
        var buildRunner = new FakeBuildRunner(stateManager);
        var fileReader = new ProjectFileReader(options);
        var toolDispatcher = new NexusToolDispatcher(agentRunner, buildRunner, fileReader, stateManager);
        var hitlInterceptor = new HitlApprovalInterceptor(stateManager);
        var chatGateway = new RecordingChatGateway();
        var httpClientFactory = new StubHttpClientFactory(httpHandler);

        var router = new NexusRouterService(
            options,
            slidingWindow,
            stateManager,
            modelCatalog,
            toolDispatcher,
            hitlInterceptor,
            chatGateway,
            httpClientFactory,
            NullLogger<NexusRouterService>.Instance);

        return new RouterTestHarness(
            router,
            stateManager,
            slidingWindow,
            modelCatalog,
            agentRunner,
            buildRunner,
            chatGateway);
    }

    private static HttpResponseMessage CreateJsonResponse(HttpStatusCode statusCode, string json)
    {
        return new HttpResponseMessage(statusCode)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };
    }

    private sealed record RouterTestHarness(
        NexusRouterService Router,
        StateManager StateManager,
        SlidingWindowQueue SlidingWindow,
        ModelCatalog ModelCatalog,
        FakeAgentRunner AgentRunner,
        FakeBuildRunner BuildRunner,
        RecordingChatGateway ChatGateway);

    private sealed class FakeGitStatusProvider(GitRepoStatus status) : IGitStatusProvider
    {
        public Task<GitRepoStatus> GetRepoStatusAsync(string projectName, string repoRootPath, CancellationToken ct = default)
            => Task.FromResult(status);
    }

    private sealed class FakeAgentRunner(StateManager stateManager) : IAgentRunner
    {
        public int StartCount { get; private set; }

        public WorkerStatus StartWorkerInBackground(
            string projectName,
            string prompt,
            string? model = null,
            CancellationToken stoppingToken = default)
        {
            StartCount++;
            return stateManager.RegisterWorker($"agy-test-{StartCount}", projectName, prompt, model, processId: 1000 + StartCount);
        }
    }

    private sealed class FakeBuildRunner(StateManager stateManager) : IBuildRunner
    {
        public int StartCount { get; private set; }

        public BuildStatus StartBuildInBackground(
            string projectName,
            string configuration = "Release",
            CancellationToken stoppingToken = default)
        {
            StartCount++;
            return stateManager.RegisterBuild($"build-test-{StartCount}", projectName, configuration, "dotnet build");
        }
    }

    private sealed class RecordingChatGateway : IChatGateway
    {
        public List<string> SentMessages { get; } = [];
        public List<string> SentFiles { get; } = [];

        public Task SendTextAsync(string message, CancellationToken ct = default)
        {
            SentMessages.Add(message);
            return Task.CompletedTask;
        }

        public Task SendFileAsync(string filePath, string? caption = null, CancellationToken ct = default)
        {
            SentFiles.Add(filePath);
            return Task.CompletedTask;
        }
    }

    private sealed class StubHttpClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private sealed class StubHttpMessageHandler(
        Func<HttpRequestMessage, string, HttpResponseMessage> responder) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var body = request.Content is not null
                ? await request.Content.ReadAsStringAsync(cancellationToken)
                : string.Empty;
            return responder(request, body);
        }
    }
}
