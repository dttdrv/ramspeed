namespace optiRAM.Services;

internal class StepEffectivenessTracker
{
    private readonly Dictionary<string, Queue<long>> _history = new();
    private readonly int _windowSize;
    private readonly long _minEffectiveBytes;
    private DateTime _lastResetUtc = DateTime.UtcNow;
    private static readonly TimeSpan ResetInterval = TimeSpan.FromMinutes(30);

    public StepEffectivenessTracker(int windowSize = 10, long minEffectiveBytes = 1_048_576)
    {
        _windowSize = windowSize;
        _minEffectiveBytes = minEffectiveBytes;
    }

    public void Record(string stepName, long freedBytes)
    {
        MaybeAutoReset();
        if (!_history.TryGetValue(stepName, out var queue))
        {
            queue = new Queue<long>();
            _history[stepName] = queue;
        }
        queue.Enqueue(Math.Max(0, freedBytes));
        while (queue.Count > _windowSize)
            queue.Dequeue();
    }

    public bool ShouldSkip(string stepName)
    {
        MaybeAutoReset();
        if (!_history.TryGetValue(stepName, out var queue) || queue.Count < _windowSize)
            return false;
        return queue.Average() < _minEffectiveBytes;
    }

    public void Reset()
    {
        _history.Clear();
        _lastResetUtc = DateTime.UtcNow;
    }

    private void MaybeAutoReset()
    {
        if (DateTime.UtcNow - _lastResetUtc >= ResetInterval)
            Reset();
    }
}
