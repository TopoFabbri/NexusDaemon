using Microsoft.Extensions.Options;
using NexusDaemon.Configuration;

namespace NexusDaemon.Phase2_StateAndMemory;

public sealed record ChatMessageEntry(string Role, string Content, DateTimeOffset TimestampUtc);

/// <summary>
/// Phase 2 Singleton: A bounded queue holding the last 5-10 interactions.
/// When a new message arrives, it pushes to the queue; if the count exceeds the limit,
/// it dequeues the oldest while preserving Gemini's user-first role alternation invariant.
/// </summary>
public sealed class SlidingWindowQueue
{
    private readonly Queue<ChatMessageEntry> _queue = new();
    private readonly object _sync = new();
    private readonly int _maxMessages;

    public SlidingWindowQueue(IOptions<NexusOptions> options)
        : this(options.Value.Gemini.SlidingWindowSize)
    {
    }

    public SlidingWindowQueue(int maxMessages)
    {
        _maxMessages = Math.Clamp(maxMessages, 5, 20);
    }

    public int MaxMessages => _maxMessages;

    public int Count
    {
        get
        {
            lock (_sync)
            {
                return _queue.Count;
            }
        }
    }

    public void Enqueue(string role, string content)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(role);
        if (content is null)
        {
            throw new ArgumentNullException(nameof(content));
        }

        var normalizedRole = NormalizeRole(role);

        lock (_sync)
        {
            _queue.Enqueue(new ChatMessageEntry(normalizedRole, content, DateTimeOffset.UtcNow));
            TrimQueueUnsafe();
        }
    }

    /// <summary>
    /// Atomically enqueues a (user, model) interaction pair under a single lock so concurrent
    /// user turns and background system events can never interleave roles out of order.
    /// </summary>
    public void EnqueueInteraction(string userContent, string modelContent)
    {
        ArgumentNullException.ThrowIfNull(userContent);
        ArgumentNullException.ThrowIfNull(modelContent);

        var now = DateTimeOffset.UtcNow;
        lock (_sync)
        {
            _queue.Enqueue(new ChatMessageEntry("user", userContent, now));
            _queue.Enqueue(new ChatMessageEntry("model", modelContent, now));
            TrimQueueUnsafe();
        }
    }

    private void TrimQueueUnsafe()
    {
        while (_queue.Count > _maxMessages)
        {
            _queue.Dequeue();
        }

        // Ensure the window always starts with a "user" role for Gemini API validity
        while (_queue.Count > 0 && !string.Equals(_queue.Peek().Role, "user", StringComparison.OrdinalIgnoreCase))
        {
            _queue.Dequeue();
        }
    }

    public IReadOnlyList<ChatMessageEntry> GetSnapshot()
    {
        lock (_sync)
        {
            return _queue.ToList();
        }
    }

    public void Clear()
    {
        lock (_sync)
        {
            _queue.Clear();
        }
    }

    private static string NormalizeRole(string role)
    {
        var trimmed = role.Trim().ToLowerInvariant();
        return trimmed switch
        {
            "assistant" or "model" or "bot" => "model",
            _ => "user"
        };
    }
}
