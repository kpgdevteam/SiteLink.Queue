using System.Collections.Concurrent;

namespace SiteLink.Queue.Services;

internal sealed class CorrelatedResponseRegistry<TResponse>
{
    private readonly ConcurrentDictionary<string, TaskCompletionSource<TResponse>> _pending = new();

    public int Count => _pending.Count;

    public TaskCompletionSource<TResponse> Register(string correlationId)
    {
        TaskCompletionSource<TResponse> responseSource =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        if (!_pending.TryAdd(correlationId, responseSource))
            throw new InvalidOperationException($"Duplicate gRPC correlation ID: {correlationId}");

        return responseSource;
    }

    public bool TryComplete(string correlationId, TResponse response)
    {
        if (!_pending.TryGetValue(correlationId, out TaskCompletionSource<TResponse> responseSource))
            return false;

        responseSource.TrySetResult(response);
        return true;
    }

    public void Remove(string correlationId) => _pending.TryRemove(correlationId, out _);

    public void FailAll(Exception exception)
    {
        foreach ((string _, TaskCompletionSource<TResponse> responseSource) in _pending)
            responseSource.TrySetException(exception);
    }
}
