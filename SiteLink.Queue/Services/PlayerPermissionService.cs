using Grpc.Core;
using KingsPlayground.Shared.Protobuf;
using Microsoft.Extensions.Hosting;
using SiteLink.API.Misc;

namespace SiteLink.Queue.Services;

internal sealed class PlayerPermissionService : IHostedService
{
    private static readonly TimeSpan DefaultLookupTimeout = TimeSpan.FromSeconds(5);

    private readonly Func<Config> _getConfig;
    private readonly TimeSpan _lookupTimeout;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly object _streamStateLock = new();
    private readonly CorrelatedResponseRegistry<ProxyServerResponse> _responses = new();

    private TaskCompletionSource<IClientStreamWriter<ProxyServerRequest>> _requestStreamSource = CreateStreamSource();
    private Task _connectionTask;
    private int _started;

    internal int PendingRequestCount => _responses.Count;

    public PlayerPermissionService(Func<Config> getConfig, TimeSpan? lookupTimeout = null)
    {
        _getConfig = getConfig;
        _lookupTimeout = lookupTimeout ?? DefaultLookupTimeout;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (Interlocked.Exchange(ref _started, 1) == 0)
            _connectionTask = Task.Run(() => RunConnectionLoopAsync(_shutdown.Token), CancellationToken.None);

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => ShutdownAsync();

    public async Task<IReadOnlySet<int>> LookupAsync(string userId, CancellationToken cancellationToken)
    {
        string correlationId = Guid.NewGuid().ToString();
        TaskCompletionSource<ProxyServerResponse> responseSource = _responses.Register(correlationId);

        using CancellationTokenSource lookupCancellation =
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _shutdown.Token);
        lookupCancellation.CancelAfter(_lookupTimeout);

        using CancellationTokenRegistration registration = lookupCancellation.Token.Register(
            () => responseSource.TrySetCanceled(lookupCancellation.Token));

        try
        {
            await WriteAsync(
                new ProxyServerRequest
                {
                    CorrelationId = correlationId,
                    PlayerLookup = new ProxyServerPlayerLookup { PlatformId = userId }
                },
                lookupCancellation.Token);

            ProxyServerResponse response = await responseSource.Task;
            if (response.PayloadCase != ProxyServerResponse.PayloadOneofCase.PlayerLookupResponse)
            {
                throw new InvalidOperationException(
                    $"Queue permission lookup received unexpected payload {response.PayloadCase}.");
            }

            return response.PlayerLookupResponse.Permissions
                .Select(permission => (int)permission)
                .ToHashSet();
        }
        finally
        {
            _responses.Remove(correlationId);
        }
    }

    public async Task ShutdownAsync()
    {
        if (!_shutdown.IsCancellationRequested)
            _shutdown.Cancel();

        FailStreamAndPending(new OperationCanceledException("The proxy-server gRPC client is shutting down."));

        if (_connectionTask == null)
            return;

        try
        {
            await _connectionTask;
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested)
        {
            // Expected during shutdown.
        }
    }

    private async Task RunConnectionLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            Channel channel = null;
            AsyncDuplexStreamingCall<ProxyServerRequest, ProxyServerResponse> streams = null;

            try
            {
                Config config = _getConfig();
                string target = NormalizeTarget(config.GrpcUrl);
                channel = new Channel(target, ChannelCredentials.Insecure);

                var client = new ProxyServerService.ProxyServerServiceClient(channel);
                Metadata metadata = CreateMetadata(config);
                ProxyServerConnection connection = await client.TryConnectionAsync(
                    new EmptyMessage(),
                    metadata,
                    deadline: DateTime.UtcNow.Add(_lookupTimeout),
                    cancellationToken: cancellationToken);

                if (!connection.Success)
                    throw new InvalidOperationException("The proxy-server backend rejected the gRPC connection.");

                streams = client.Connect(metadata, cancellationToken: cancellationToken);
                PublishRequestStream(streams.RequestStream);
                SiteLinkLogger.Info($"Connected to proxy-server gRPC service at (f=cyan){target}(f=white).", "Queue");

                while (await streams.ResponseStream.MoveNext(cancellationToken))
                    HandleResponse(streams.ResponseStream.Current);

                throw new RpcException(new Status(StatusCode.Unavailable, "The proxy-server gRPC response stream closed."));
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                SiteLinkLogger.Error($"Proxy-server gRPC connection failed: {ex}", "Queue");
            }
            finally
            {
                FailStreamAndPending(new RpcException(
                    new Status(StatusCode.Unavailable, "The proxy-server gRPC connection closed.")));

                try
                {
                    streams?.Dispose();
                }
                catch (Exception)
                {
                    // Stream disposal is best effort during reconnect.
                }

                if (channel != null)
                {
                    try
                    {
                        await channel.ShutdownAsync();
                    }
                    catch (Exception)
                    {
                        // Channel shutdown is best effort during reconnect.
                    }
                }
            }

            if (cancellationToken.IsCancellationRequested)
                break;

            double retrySeconds = Math.Max(0, _getConfig().ConnectionRetryCooldown);
            SiteLinkLogger.Info(
                $"Proxy-server gRPC connection closed; retrying in (f=yellow){retrySeconds}(f=white) seconds.",
                "Queue");

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(retrySeconds), cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
        }

        FailStreamAndPending(new OperationCanceledException("The proxy-server gRPC client stopped."));
    }

    private async Task WriteAsync(ProxyServerRequest request, CancellationToken cancellationToken)
    {
        Task<IClientStreamWriter<ProxyServerRequest>> streamTask;
        lock (_streamStateLock)
            streamTask = _requestStreamSource.Task;

        IClientStreamWriter<ProxyServerRequest> stream = await streamTask.WaitAsync(cancellationToken);
        await _writeLock.WaitAsync(cancellationToken);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            await stream.WriteAsync(request);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private void HandleResponse(ProxyServerResponse response)
    {
        if (string.IsNullOrWhiteSpace(response.CorrelationId))
        {
            SiteLinkLogger.Warn("Received a proxy-server gRPC response without a correlation ID.", "Queue");
            return;
        }

        if (!_responses.TryComplete(response.CorrelationId, response))
        {
            SiteLinkLogger.Warn(
                $"Received an unbound proxy-server gRPC correlation ID: {response.CorrelationId}",
                "Queue");
        }
    }

    private void PublishRequestStream(IClientStreamWriter<ProxyServerRequest> stream)
    {
        TaskCompletionSource<IClientStreamWriter<ProxyServerRequest>> streamSource;
        lock (_streamStateLock)
            streamSource = _requestStreamSource;

        streamSource.TrySetResult(stream);
    }

    private void FailStreamAndPending(Exception exception)
    {
        TaskCompletionSource<IClientStreamWriter<ProxyServerRequest>> oldStreamSource;
        lock (_streamStateLock)
        {
            oldStreamSource = _requestStreamSource;
            _requestStreamSource = CreateStreamSource();
        }

        if (oldStreamSource.TrySetException(exception))
        {
            _ = oldStreamSource.Task.ContinueWith(
                task => _ = task.Exception,
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously | TaskContinuationOptions.OnlyOnFaulted,
                TaskScheduler.Default);
        }

        _responses.FailAll(exception);
    }

    private static Metadata CreateMetadata(Config config) =>
    [
        new Metadata.Entry("Authorization", $"Bearer {config.GrpcKey}"),
        new Metadata.Entry("x-server-id", config.ServerId.ToString())
    ];

    private static TaskCompletionSource<IClientStreamWriter<ProxyServerRequest>> CreateStreamSource() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static string NormalizeTarget(string url)
    {
        string target = (url ?? string.Empty).Trim().TrimEnd('/');
        if (target.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
            target = target[7..];
        else if (target.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            target = target[8..];

        if (string.IsNullOrWhiteSpace(target))
            throw new InvalidOperationException("The gRPC URL is not configured.");

        return target;
    }
}
