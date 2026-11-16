using System.Diagnostics;
using System.Net;
using System.Text;
using Microsoft.Extensions.Configuration;
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

public sealed class EdgeCaseTests
{
    [Fact]
    public void ServiceCollection_AddNexusDaemon_ResolvesAllSingletonsWithoutCircularDependency()
    {
        var config = new ConfigurationBuilder()
            .AddJsonFile(@"D:\Files\Repos\NexusDaemon\src\NexusDaemon\appsettings.json", optional: false)
            .Build();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddNexusDaemon(config);

        using var provider = services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateOnBuild = true,
            ValidateScopes = true
        });

        var router = provider.GetRequiredService<NexusRouterService>();
        var stateManager = provider.GetRequiredService<StateManager>();
        var slidingWindow = provider.GetRequiredService<SlidingWindowQueue>();
        var chatGateway = provider.GetRequiredService<IChatGateway>();
        var runner = provider.GetRequiredService<IAgentRunner>();

        Assert.NotNull(router);
        Assert.NotNull(stateManager);
        Assert.NotNull(slidingWindow);
        Assert.NotNull(chatGateway);
        Assert.NotNull(runner);
        Assert.Equal("hydrogen-4", stateManager.ActiveRouterModel);
        Assert.Equal("hydrogen-4", stateManager.ActiveWorkerModel);
    }

    [Fact]
    public async Task ProcessGitStatusProvider_QueriesRealHordePunkMaidensRepository()
    {
        var options = Options.Create(new NexusOptions());
        var provider = new ProcessGitStatusProvider(options, NullLogger<ProcessGitStatusProvider>.Instance);

        var status = await provider.GetRepoStatusAsync("HordePunkMaidens", @"D:\Files\Repos\HordePunkMaidens");
        Assert.True(status.IsAvailable, status.ErrorMessage);
        Assert.False(string.IsNullOrWhiteSpace(status.Branch));
    }

    [Fact]
    public async Task NexusEventBus_And_HandleSystemEventAsync_FormatsWorkerCompletionSummary()
    {
        var eventBus = new NexusEventBus();
        var completedEvt = new AgentTaskCompletedEvent(
            WorkerId: "agy-9911",
            ProcessId: 24680,
            CascadeId: "4ba92256-fa92-49cc-b107-068678281b61",
            ProjectName: "HordePunkMaidens",
            TaskDescription: "Implement armor upgrade curve",
            Model: "hydrogen-4",
            Success: true,
            JsonResult: """{"type":"result","status":"completed","filesModified":["ArmorUpgrade.cs"]}""",
            Duration: TimeSpan.FromSeconds(14.2),
            OccurredAtUtc: DateTimeOffset.UtcNow);

        await eventBus.PublishAsync(completedEvt);
        Assert.True(eventBus.Reader.TryRead(out var readEvt));
        Assert.Same(completedEvt, readEvt);

        var promptText = readEvt!.FormatForStatelessRouterPrompt();
        Assert.Contains("AgentTaskCompletedEvent", promptText);
        Assert.Contains("ArmorUpgrade.cs", promptText);
    }

    [Fact]
    public void SlidingWindowQueue_BoundaryValidationAndThreadSafety()
    {
        var minClamped = new SlidingWindowQueue(maxMessages: 1);
        Assert.Equal(5, minClamped.MaxMessages);

        var maxClamped = new SlidingWindowQueue(maxMessages: 999);
        Assert.Equal(20, maxClamped.MaxMessages);

        Assert.Throws<ArgumentException>(() => minClamped.Enqueue("   ", "hello"));
        Assert.Throws<ArgumentNullException>(() => minClamped.Enqueue("user", null!));

        Parallel.For(0, 100, i =>
        {
            minClamped.Enqueue(i % 2 == 0 ? "user" : "model", $"msg-{i}");
            _ = minClamped.GetSnapshot();
        });

        var finalSnap = minClamped.GetSnapshot();
        Assert.InRange(finalSnap.Count, 1, 5);
        Assert.Equal("user", finalSnap[0].Role);
    }

    [Fact]
    public async Task StreamJsonParser_HandlesEmptyAndErrorStreams()
    {
        using var emptyReader = new StringReader(string.Empty);
        var emptyOutcome = await StreamJsonParser.ParseStreamToCompletionAsync(emptyReader);
        Assert.False(emptyOutcome.IsTerminalFound);
        Assert.False(emptyOutcome.IsSuccess);
        Assert.Equal(0, emptyOutcome.LinesProcessed);

        using var errorReader = new StringReader("""{"type":"error","is_error":true,"error":"Compilation failed"}""");
        var errorOutcome = await StreamJsonParser.ParseStreamToCompletionAsync(errorReader);
        Assert.True(errorOutcome.IsTerminalFound);
        Assert.False(errorOutcome.IsSuccess);
        Assert.Contains("Compilation failed", errorOutcome.FinalResultJson);
    }

    [Fact]
    public void AntigravityDaemonRunner_ExtractsUuidFromCliOutput()
    {
        var output = "Started conversation 4ba92256-fa92-49cc-b107-068678281b61 on hydrogen-4";
        var id = AntigravityDaemonRunner.ExtractConversationId(output);
        Assert.Equal("4ba92256-fa92-49cc-b107-068678281b61", id);
        Assert.Null(AntigravityDaemonRunner.ExtractConversationId("no uuid here"));
    }

    [Fact]
    public void TelegramChatGateway_ChunksLongMessagesCleanly()
    {
        var longText = new string('A', 9500);
        var chunks = TelegramChatGateway.ChunkMessage(longText, 4000);
        Assert.Equal(3, chunks.Count);
        Assert.Equal(4000, chunks[0].Length);
        Assert.Equal(4000, chunks[1].Length);
        Assert.Equal(1500, chunks[2].Length);
    }

    [Fact]
    public void ModelCatalog_ResolvesAntigravityHydrogen4AndAllHubModelAliases()
    {
        var options = Options.Create(new NexusOptions());
        var catalog = new ModelCatalog(options);

        // Antigravity CLI requires "gemini-4-hydrogen-high" (not bare "hydrogen-4") so it doesn't downgrade to flash
        Assert.Equal("gemini-4-hydrogen-high", catalog.ResolveAntigravityCliModelArgument(null));
        Assert.Equal("gemini-4-hydrogen-high", catalog.ResolveAntigravityCliModelArgument("hydrogen 4"));
        Assert.Equal("gemini-4-hydrogen-high", catalog.ResolveAntigravityCliModelArgument("hydrogen-4"));
        Assert.Equal("gemini-4-hydrogen-xhigh", catalog.ResolveAntigravityCliModelArgument("hydrogen 4 xhigh"));
        Assert.Equal("gemini-4-hydrogen-medium", catalog.ResolveAntigravityCliModelArgument("hydrogen 4 medium"));
        Assert.Equal("gemini-4-hydrogen-low", catalog.ResolveAntigravityCliModelArgument("hydrogen 4 low"));
        Assert.Equal("gemini-4-argon-high", catalog.ResolveAntigravityCliModelArgument("argon 4"));
        Assert.Equal("claude-opus-5-5-high", catalog.ResolveAntigravityCliModelArgument("claude opus 5.5"));
        Assert.Equal("claude-sonnet-5-5-high", catalog.ResolveAntigravityCliModelArgument("claude sonnet 5.5"));
        Assert.Equal("gpt-oss-120b-medium", catalog.ResolveAntigravityCliModelArgument("gpt-oss"));
    }

    [Fact]
    public void AntigravityDaemonRunner_DiscoversLivePidHubAndSkipsDeadPids()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"nexus-hub-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            var currentPid = Environment.ProcessId;
            // Write an older file with a LIVE PID (current process)
            var liveFile = Path.Combine(tempDir, "hub_64811.json");
            File.WriteAllText(liveFile, $$"""{"pid": {{currentPid}}, "httpPort": 64811, "csrfToken": "live-csrf"}""");
            File.SetLastWriteTimeUtc(liveFile, DateTime.UtcNow.AddMinutes(-10));

            // Write a newer file with a DEAD PID (999999)
            var deadFile = Path.Combine(tempDir, "hub_56429.json");
            File.WriteAllText(deadFile, """{"pid": 999999, "httpPort": 56429, "csrfToken": "dead-csrf"}""");
            File.SetLastWriteTimeUtc(deadFile, DateTime.UtcNow);

            var options = Options.Create(new NexusOptions());
            var catalog = new ModelCatalog(options);
            var stateManager = new StateManager(
                options,
                catalog,
                new ProcessGitStatusProvider(options, NullLogger<ProcessGitStatusProvider>.Instance));
            var runner = new AntigravityDaemonRunner(
                options,
                stateManager,
                new NexusEventBus(),
                catalog,
                new StubHttpClientFactory(),
                NullLogger<AntigravityDaemonRunner>.Instance);

            var discovered = runner.TryDiscoverLatestHubEndpoint(tempDir);
            Assert.NotNull(discovered);
            Assert.Equal("http://127.0.0.1:64811", discovered!.BaseUri);
            Assert.Equal("live-csrf", discovered.CsrfToken);
            Assert.Equal(currentPid, discovered.ProcessId);
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public void AntigravityDaemonRunner_ExtractsCompactStructuredTrajectoryResult()
    {
        var rawTrajectory = """
        {
          "status": "CASCADE_RUN_STATUS_IDLE",
          "numTotalSteps": 4,
          "trajectory": {
            "steps": [
              {
                "type": "CORTEX_STEP_TYPE_USER_INPUT",
                "status": "CORTEX_STEP_STATUS_DONE"
              },
              {
                "type": "CORTEX_STEP_TYPE_PLANNER_RESPONSE",
                "status": "CORTEX_STEP_STATUS_DONE",
                "plannerResponse": {
                  "response": "Reading files...",
                  "thinking": "very long thinking text",
                  "thinkingSignature": "AAAA_HUGE_BASE64_SIGNATURE_BLOB",
                  "toolCalls": [{ "name": "read_file" }, { "name": "edit_file" }]
                }
              },
              {
                "type": "CORTEX_STEP_TYPE_PLANNER_RESPONSE",
                "status": "CORTEX_STEP_STATUS_DONE",
                "plannerResponse": {
                  "response": "Draft summary",
                  "modifiedResponse": "Completed refactoring NetworkedAttackExcecutor.cs and verified compilation."
                }
              }
            ]
          }
        }
        """;

        var (isSuccess, structuredJson) = AntigravityDaemonRunner.ExtractStructuredTrajectoryResult(
            "4ba92256-fa92-49cc-b107-068678281b61",
            rawTrajectory);

        Assert.True(isSuccess);
        Assert.DoesNotContain("AAAA_HUGE_BASE64_SIGNATURE_BLOB", structuredJson);
        Assert.Contains("Completed refactoring NetworkedAttackExcecutor.cs", structuredJson);
        Assert.Contains("read_file", structuredJson);
        Assert.Contains("edit_file", structuredJson);

        var (failedSuccess, failedJson) = AntigravityDaemonRunner.ExtractStructuredTrajectoryResult(
            "4ba92256-fa92-49cc-b107-068678281b61",
            """{"status":"CASCADE_RUN_STATUS_FAILED","numTotalSteps":1}""");
        Assert.False(failedSuccess);
        Assert.Contains("\"is_error\":true", failedJson);
    }

    [Fact]
    public void ProcessGitStatusProvider_ParsesUnbornAndDetachedBranchesCorrectly()
    {
        var unbornMain = ProcessGitStatusProvider.ParsePorcelainBranchOutput(
            "HordePunkMaidens",
            "## No commits yet on main\n?? Architecture.md\n");
        Assert.Equal("main", unbornMain.Branch);
        Assert.Equal(1, unbornMain.ModifiedOrUntrackedCount);

        var initialCommit = ProcessGitStatusProvider.ParsePorcelainBranchOutput(
            "HordePunkMaidens",
            "## Initial commit on develop\n");
        Assert.Equal("develop", initialCommit.Branch);

        var detached = ProcessGitStatusProvider.ParsePorcelainBranchOutput(
            "HordePunkMaidens",
            "## HEAD (no branch)\n M file.cs\n");
        Assert.Equal("detached", detached.Branch);
        Assert.Equal(1, detached.ModifiedOrUntrackedCount);
    }

    [Fact]
    public void BuildRunner_ResolvesUnityCliAndCustomBuildModes()
    {
        var options = Options.Create(new NexusOptions());
        var catalog = new ModelCatalog(options);
        var stateManager = new StateManager(
            options,
            catalog,
            new ProcessGitStatusProvider(options, NullLogger<ProcessGitStatusProvider>.Instance));
        var buildRunner = new BuildRunner(
            options,
            stateManager,
            new NexusEventBus(),
            NullLogger<BuildRunner>.Instance);

        var unityCliProject = new ProjectOptions
        {
            RootPath = @"D:\Files\Repos\HordePunkMaidens",
            UnityProjectPath = @"D:\Files\Repos\HordePunkMaidens\HordePunkMaidens",
            BuildExecutableOutput = @"D:\Files\Repos\HordePunkMaidens\HordePunkMaidens\Builds\Windows\HordePunkMaidens.exe",
            BuildMode = "UnityCli"
        };

        var resolvedCli = buildRunner.TryResolveBuildCommand("HordePunkMaidens", unityCliProject, "Release", out var diag);
        Assert.NotNull(resolvedCli);
        Assert.Empty(diag);
        Assert.Contains("StandaloneWindows64", resolvedCli!.Arguments);
        Assert.Contains("--no-tail", resolvedCli.Arguments);
    }

    [Fact]
    public void NexusRouterService_CoalescesAdjacentSameRoleMessagesForStrictAlternation()
    {
        var entries = new List<ChatMessageEntry>
        {
            new("user", "First user message", DateTimeOffset.UtcNow),
            new("user", "Second consecutive user message", DateTimeOffset.UtcNow),
            new("model", "Model reply", DateTimeOffset.UtcNow),
            new("model", "Second model message", DateTimeOffset.UtcNow)
        };

        var contents = NexusRouterService.BuildStatelessContentsArray(entries, "Current turn question");
        // Should be strictly: user (2 parts) -> model (2 parts) -> user (1 part)
        Assert.Equal(3, contents.Count);
        Assert.Equal("user", contents[0]!["role"]!.GetValue<string>());
        Assert.Equal("model", contents[1]!["role"]!.GetValue<string>());
        Assert.Equal("user", contents[2]!["role"]!.GetValue<string>());
    }

    [Fact]
    public async Task NexusRouterService_SupersedesStalePendingHitlWhenUserSendsNewNonYnCommand()
    {
        var callCount = 0;
        var handler = new StubHttpMessageHandler((_, _) =>
        {
            callCount++;
            if (callCount == 1)
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("""
                    {
                      "candidates": [{
                        "content": {
                          "role": "model",
                          "parts": [{
                            "functionCall": {
                              "name": "DelegateTask",
                              "args": {
                                "projectName": "HordePunkMaidens",
                                "prompt": "Wipe all scenes",
                              "isDestructive": true
                              }
                            }
                          }]
                        }
                      }]
                    }
                    """, Encoding.UTF8, "application/json")
                };
            }

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""
                {
                  "candidates": [{
                    "content": {
                      "role": "model",
                      "parts": [{ "text": "Normal non-destructive reply." }]
                    }
                  }]
                }
                """, Encoding.UTF8, "application/json")
            };
        });

        var options = Options.Create(new NexusOptions
        {
            Gemini = new GeminiOptions { ApiKey = "test-key" }
        });
        var catalog = new ModelCatalog(options);
        var queue = new SlidingWindowQueue(options);
        var state = new StateManager(options, catalog, new ProcessGitStatusProvider(options, NullLogger<ProcessGitStatusProvider>.Instance));
        var runner = new CountingAgentRunner(state);
        var buildRunner = new BuildRunner(options, state, new NexusEventBus(), NullLogger<BuildRunner>.Instance);
        var dispatcher = new NexusToolDispatcher(runner, buildRunner, new ProjectFileReader(options), state);
        var hitl = new HitlApprovalInterceptor(state);
        var gateway = new NullChatGateway();
        var router = new NexusRouterService(
            options,
            queue,
            state,
            catalog,
            dispatcher,
            hitl,
            gateway,
            new StubHttpClientFactory(handler),
            NullLogger<NexusRouterService>.Instance);

        // Turn 1: Destructive tool call is held in PendingApproval
        var t1 = await router.HandleUserMessageAsync("Wipe all scenes");
        Assert.True(t1.AwaitingHitlApproval);
        Assert.NotNull(state.PendingApproval);

        // Turn 2: User ignores Y/N and asks a different question -> PendingApproval is superseded & cleared!
        var t2 = await router.HandleUserMessageAsync("Actually nevermind, what is the status?");
        Assert.False(t2.AwaitingHitlApproval);
        Assert.Null(state.PendingApproval);
        Assert.Equal(0, runner.StartCount);

        // Turn 3: User says "yes" to a normal question -> must NOT execute Turn 1's destructive action!
        var t3 = await router.HandleUserMessageAsync("yes");
        Assert.Equal(0, runner.StartCount);
    }

    [Fact]
    public async Task CliStreamJsonRunner_TerminatesProcessThatKeepsStderrOpenAfterTerminalEvent()
    {
        var eventBus = new NexusEventBus();
        var options = Options.Create(new NexusOptions
        {
            Runner = new RunnerOptions
            {
                Mode = "CliStreamJson",
                AgyExecutablePath = "powershell.exe",
                // Emit a terminal result JSON line and then sleep 30s without closing stderr
                AgyArgumentsTemplate = "-NoProfile -Command [Console]::Out.WriteLine('{\"type\":\"result\",\"status\":\"completed\",\"result\":\"fast_done\"}');[Console]::Out.Flush();Start-Sleep -Seconds 30",
                WorkerTimeoutSeconds = 30
            }
        });
        var catalog = new ModelCatalog(options);
        var state = new StateManager(options, catalog, new ProcessGitStatusProvider(options, NullLogger<ProcessGitStatusProvider>.Instance));
        var cliRunner = new CliStreamJsonRunner(options, state, eventBus, catalog, NullLogger<CliStreamJsonRunner>.Instance);

        _ = cliRunner.StartWorkerInBackground("HordePunkMaidens", "Test prompt");

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var completedEvt = await eventBus.Reader.ReadAsync(cts.Token);
        Assert.NotNull(completedEvt);
        var agentEvt = Assert.IsType<AgentTaskCompletedEvent>(completedEvt);
        Assert.True(agentEvt.Success, agentEvt.JsonResult);
        Assert.Contains("fast_done", agentEvt.JsonResult);
        Assert.Empty(state.ActiveWorkers);
    }

    [Fact]
    public void RunnerOptions_NormalizeAddress_StripsPrefixesAndTrailingSlashes()
    {
        Assert.Equal("127.0.0.1:64811", RunnerOptions.NormalizeAddress("http://127.0.0.1:64811/"));
        Assert.Equal("localhost:5387", RunnerOptions.NormalizeAddress("https://localhost:5387"));
        Assert.Equal("127.0.0.1:9999", RunnerOptions.NormalizeAddress("  127.0.0.1:9999/  "));
        Assert.Equal("", RunnerOptions.NormalizeAddress(""));
        Assert.Equal("", RunnerOptions.NormalizeAddress("   "));
    }

    [Fact]
    public void RunnerOptions_ResolveLsAddress_FollowsPriorityHierarchy()
    {
        var options = new RunnerOptions();
        var originalEnv = Environment.GetEnvironmentVariable("ANTIGRAVITY_LS_ADDRESS");

        try
        {
            Environment.SetEnvironmentVariable("ANTIGRAVITY_LS_ADDRESS", null);

            // 1. Fallback when nothing set
            Assert.Equal("127.0.0.1:5387", options.ResolveLsAddress(null));

            // 2. Discovered address
            Assert.Equal("127.0.0.1:64811", options.ResolveLsAddress("http://127.0.0.1:64811/"));

            // 3. Environment variable takes priority over discovered
            Environment.SetEnvironmentVariable("ANTIGRAVITY_LS_ADDRESS", "127.0.0.1:7777");
            Assert.Equal("127.0.0.1:7777", options.ResolveLsAddress("http://127.0.0.1:64811/"));

            // 4. Explicit option takes priority over environment variable
            options.LsAddress = "http://127.0.0.1:8888";
            Assert.Equal("127.0.0.1:8888", options.ResolveLsAddress("http://127.0.0.1:64811/"));
        }
        finally
        {
            Environment.SetEnvironmentVariable("ANTIGRAVITY_LS_ADDRESS", originalEnv);
        }
    }

    [Fact]
    public void RunnerOptions_ResolveCsrfToken_FollowsPriorityHierarchy()
    {
        var options = new RunnerOptions();
        var origCsrf = Environment.GetEnvironmentVariable("ANTIGRAVITY_CSRF_TOKEN");
        var origServerCsrf = Environment.GetEnvironmentVariable("ANTIGRAVITY_SERVER_CSRF_TOKEN");

        try
        {
            Environment.SetEnvironmentVariable("ANTIGRAVITY_CSRF_TOKEN", null);
            Environment.SetEnvironmentVariable("ANTIGRAVITY_SERVER_CSRF_TOKEN", null);

            // 1. Fallback when nothing set
            Assert.Equal(string.Empty, options.ResolveCsrfToken(null));

            // 2. Discovered token
            Assert.Equal("discovered-token", options.ResolveCsrfToken("discovered-token"));

            // 3. Environment variable takes priority over discovered
            Environment.SetEnvironmentVariable("ANTIGRAVITY_CSRF_TOKEN", "env-csrf-token");
            Assert.Equal("env-csrf-token", options.ResolveCsrfToken("discovered-token"));

            // 4. Explicit option takes priority over environment variable
            options.CsrfToken = "config-csrf-token";
            Assert.Equal("config-csrf-token", options.ResolveCsrfToken("discovered-token"));
        }
        finally
        {
            Environment.SetEnvironmentVariable("ANTIGRAVITY_CSRF_TOKEN", origCsrf);
            Environment.SetEnvironmentVariable("ANTIGRAVITY_SERVER_CSRF_TOKEN", origServerCsrf);
        }
    }

    [Fact]
    public void RunnerOptions_DaemonDirectory_FallsBackToUserProfileWhenUnconfigured()
    {
        var options = new RunnerOptions();
        Assert.False(string.IsNullOrWhiteSpace(options.DaemonDirectory));
        Assert.Contains(".gemini", options.DaemonDirectory);

        options.DaemonDirectory = "";
        Assert.False(string.IsNullOrWhiteSpace(options.DaemonDirectory));
        Assert.Contains(".gemini", options.DaemonDirectory);
    }

    [Fact]
    public void DotEnv_ParsesAndLoadsKeyValuesWithoutOverwritingExisting()
    {
        var tempFile = Path.Combine(Path.GetTempPath(), $"dotenv-test-{Guid.NewGuid():N}.env");
        var preExistingKey = "TEST_DOTENV_PRE_EXISTING";
        var newKey1 = "TEST_DOTENV_KEY_1";
        var newKey2 = "TEST_DOTENV_KEY_2";
        var newKey3 = "TEST_DOTENV_KEY_3";

        try
        {
            Environment.SetEnvironmentVariable(preExistingKey, "original-val");
            Environment.SetEnvironmentVariable(newKey1, null);
            Environment.SetEnvironmentVariable(newKey2, null);
            Environment.SetEnvironmentVariable(newKey3, null);

            File.WriteAllText(tempFile, $"""
            # This is a comment
               # Another comment
            {preExistingKey}=new-overwritten-val
            {newKey1}=hello_world
            {newKey2}="quoted value with spaces"
            {newKey3}='single quoted value'
            INVALID_LINE_WITHOUT_EQUALS
            """);

            DotEnv.Load(tempFile);

            Assert.Equal("original-val", Environment.GetEnvironmentVariable(preExistingKey));
            Assert.Equal("hello_world", Environment.GetEnvironmentVariable(newKey1));
            Assert.Equal("quoted value with spaces", Environment.GetEnvironmentVariable(newKey2));
            Assert.Equal("single quoted value", Environment.GetEnvironmentVariable(newKey3));
        }
        finally
        {
            Environment.SetEnvironmentVariable(preExistingKey, null);
            Environment.SetEnvironmentVariable(newKey1, null);
            Environment.SetEnvironmentVariable(newKey2, null);
            Environment.SetEnvironmentVariable(newKey3, null);
            if (File.Exists(tempFile))
            {
                File.Delete(tempFile);
            }
        }
    }

    [Fact]
    public void AntigravityDaemonRunner_InjectsAntigravityEnvironmentVariablesIntoProcessStartInfo()
    {
        var psi = new ProcessStartInfo();
        AntigravityDaemonRunner.InjectAntigravityEnvironment(psi, "http://127.0.0.1:64811/", "test-csrf-token-123");

        Assert.Equal("127.0.0.1:64811", psi.Environment["ANTIGRAVITY_LS_ADDRESS"]);
        Assert.Equal("test-csrf-token-123", psi.Environment["ANTIGRAVITY_CSRF_TOKEN"]);
        Assert.Equal("test-csrf-token-123", psi.Environment["ANTIGRAVITY_SERVER_CSRF_TOKEN"]);
    }

    [Fact]
    public async Task AntigravityDaemonRunner_AwaitConversationIdle_UsesConversationIdKey()
    {
        var recordedBodies = new List<string>();
        var handler = new StubHttpMessageHandler((req, body) =>
        {
            recordedBodies.Add(body);
            if (req.RequestUri!.AbsolutePath.Contains("WaitForConversationFullyIdle"))
            {
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}") };
            }

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"status":"CASCADE_RUN_STATUS_IDLE","trajectory":{"steps":[]}}""")
            };
        });

        var options = Options.Create(new NexusOptions());
        var catalog = new ModelCatalog(options);
        var stateManager = new StateManager(options, catalog, new ProcessGitStatusProvider(options, NullLogger<ProcessGitStatusProvider>.Instance));
        var runner = new AntigravityDaemonRunner(
            options,
            stateManager,
            new NexusEventBus(),
            catalog,
            new StubHttpClientFactory(handler),
            NullLogger<AntigravityDaemonRunner>.Instance);

        var hubEndpoint = new AntigravityDaemonRunner.HubEndpointInfo("http://127.0.0.1:64811", "test-csrf", 1234);
        var (isSuccess, json) = await runner.AwaitConversationIdleAndFetchTrajectoryAsync(
            "test-cascade-uuid",
            "init",
            hubEndpoint,
            CancellationToken.None);

        Assert.True(isSuccess, json);
        Assert.NotEmpty(recordedBodies);
        Assert.Contains("\"conversationId\":\"test-cascade-uuid\"", recordedBodies[0]);
    }

    [Fact]
    public async Task AntigravityDaemonRunner_AwaitConversationIdle_SurfacesErrorOnNonSuccess()
    {
        var handler = new StubHttpMessageHandler((req, _) =>
        {
            if (req.RequestUri!.AbsolutePath.Contains("WaitForConversationFullyIdle"))
            {
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}") };
            }

            return new HttpResponseMessage(HttpStatusCode.Unauthorized)
            {
                Content = new StringContent("""{"code":"unauthenticated","message":"unauthenticated: invalid CSRF token"}""")
            };
        });

        var options = Options.Create(new NexusOptions());
        var catalog = new ModelCatalog(options);
        var stateManager = new StateManager(options, catalog, new ProcessGitStatusProvider(options, NullLogger<ProcessGitStatusProvider>.Instance));
        var runner = new AntigravityDaemonRunner(
            options,
            stateManager,
            new NexusEventBus(),
            catalog,
            new StubHttpClientFactory(handler),
            NullLogger<AntigravityDaemonRunner>.Instance);

        var hubEndpoint = new AntigravityDaemonRunner.HubEndpointInfo("http://127.0.0.1:64811", "stale-csrf", 1234);
        var (isSuccess, json) = await runner.AwaitConversationIdleAndFetchTrajectoryAsync(
            "test-cascade-uuid",
            "init",
            hubEndpoint,
            CancellationToken.None);

        Assert.False(isSuccess);
        Assert.Contains("FAILED", json);
        Assert.Contains("401", json);
    }

    [Fact]
    public async Task BuildAgentApiStartInfo_MultilinePrompt_IsNotTruncatedByCmdBatchWrapper()
    {
        var fullPrompt = AntigravityDaemonRunner.BuildWorkerPrompt(
            "NexusDaemon",
            @"D:\Files\Repos\NexusDaemon",
            "Find all bugs\nFix \"DelegateTask\" & verify tests");

        Assert.DoesNotContain('\n', fullPrompt);
        Assert.DoesNotContain('\r', fullPrompt);
        Assert.DoesNotContain('"', fullPrompt);
        Assert.Contains("Find all bugs", fullPrompt);
        Assert.Contains("Fix 'DelegateTask' & verify tests", fullPrompt);

        var tempBat = Path.Combine(Path.GetTempPath(), $"nexus_arg_test_{Guid.NewGuid():N}.bat");
        await File.WriteAllTextAsync(tempBat, "@echo off\r\necho %*\r\n");
        try
        {
            var psi = AntigravityDaemonRunner.BuildAgentApiStartInfo(
                tempBat,
                Path.GetTempPath(),
                "NexusDaemon",
                fullPrompt,
                "hydrogen-4",
                lsAddress: null,
                csrfToken: null);

            using var process = Process.Start(psi)!;
            var stdout = await process.StandardOutput.ReadToEndAsync();
            await process.WaitForExitAsync();

            Assert.Equal(0, process.ExitCode);
            Assert.Contains("Find all bugs", stdout);
            Assert.Contains("Fix 'DelegateTask' & verify tests", stdout);
        }
        finally
        {
            File.Delete(tempBat);
        }
    }

    private sealed class StubHttpClientFactory(HttpMessageHandler? handler = null) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) =>
            handler is not null ? new HttpClient(handler, disposeHandler: false) : new HttpClient();
    }

    private sealed class StubHttpMessageHandler(Func<HttpRequestMessage, string, HttpResponseMessage> responder) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is not null ? await request.Content.ReadAsStringAsync(cancellationToken) : string.Empty;
            return responder(request, body);
        }
    }

    private sealed class CountingAgentRunner(StateManager state) : IAgentRunner
    {
        public int StartCount { get; private set; }
        public WorkerStatus StartWorkerInBackground(string projectName, string prompt, string? model = null, CancellationToken stoppingToken = default)
        {
            StartCount++;
            return state.RegisterWorker($"w-{StartCount}", projectName, prompt, model);
        }
    }

    private sealed class NullChatGateway : IChatGateway
    {
        public Task SendTextAsync(string message, CancellationToken ct = default) => Task.CompletedTask;
        public Task SendFileAsync(string filePath, string? caption = null, CancellationToken ct = default) => Task.CompletedTask;
    }
}
