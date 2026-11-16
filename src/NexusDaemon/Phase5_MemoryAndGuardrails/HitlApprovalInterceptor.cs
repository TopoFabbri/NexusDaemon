using NexusDaemon.Phase2_StateAndMemory;

namespace NexusDaemon.Phase5_MemoryAndGuardrails;

public enum HitlDecision
{
    NotApplicable,
    Approved,
    Rejected
}

/// <summary>
/// Phase 5: Human-in-the-Loop (HITL) guardrail.
/// If a stateless tool call requests a destructive action (isDestructive: true),
/// intercepts the call, stores it in StateManager.PendingApproval, and waits for a Y/N response.
/// </summary>
public sealed class HitlApprovalInterceptor
{
    private readonly StateManager _stateManager;

    public HitlApprovalInterceptor(StateManager stateManager)
    {
        _stateManager = stateManager;
    }

    public bool RequiresApproval(string toolName, IReadOnlyDictionary<string, object?> arguments)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(toolName);
        ArgumentNullException.ThrowIfNull(arguments);

        if (string.Equals(toolName, "ReadFile", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(toolName, "SwitchModel", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (arguments.TryGetValue("isDestructive", out var rawVal) && rawVal is not null)
        {
            return rawVal switch
            {
                bool b => b,
                string s when bool.TryParse(s, out var parsed) => parsed,
                _ => false
            };
        }

        return false;
    }

    public PendingApprovalRequest HoldForApproval(
        string toolName,
        string projectName,
        string summary,
        IReadOnlyDictionary<string, object?> arguments)
    {
        var request = new PendingApprovalRequest(
            Guid.NewGuid().ToString("N")[..8],
            toolName,
            projectName,
            summary,
            new Dictionary<string, object?>(arguments, StringComparer.OrdinalIgnoreCase),
            DateTimeOffset.UtcNow);

        _stateManager.SetPendingApproval(request);
        return request;
    }

    public string FormatConfirmationPrompt(PendingApprovalRequest request)
    {
        return $"⚠️ [HITL Confirmation Required — ID #{request.RequestId}]\n" +
               $"Tool: {request.ToolName}\n" +
               $"Project: {request.ProjectName}\n" +
               $"Action: {request.Summary}\n\n" +
               "Reply 'Y' (or 'Yes') to execute, or 'N' (or 'No') to cancel.";
    }

    public (HitlDecision Decision, PendingApprovalRequest? Request) TryEvaluateUserReply(string userMessage)
    {
        var pending = _stateManager.PendingApproval;
        if (pending is null || string.IsNullOrWhiteSpace(userMessage))
        {
            return (HitlDecision.NotApplicable, null);
        }

        var normalized = userMessage.Trim().TrimEnd('.', '!', '?').Trim().ToLowerInvariant();
        if (normalized is "y" or "yes" or "approve" or "confirm")
        {
            var consumed = _stateManager.ConsumePendingApproval();
            return (HitlDecision.Approved, consumed);
        }

        if (normalized is "n" or "no" or "cancel" or "deny" or "reject")
        {
            var consumed = _stateManager.ConsumePendingApproval();
            return (HitlDecision.Rejected, consumed);
        }

        return (HitlDecision.NotApplicable, null);
    }
}
