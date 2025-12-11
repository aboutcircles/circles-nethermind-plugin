using Circles.Cache.Service;
using Circles.Cache.Service.Caches;
using Circles.Cache.Service.Services;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Xunit;

namespace Circles.Cache.Service.Tests;

public class CacheWarmupServiceTests
{
    [Fact]
    public async Task RetriesUntilSuccessAndMarksWarmupComplete()
    {
        var settings = new CacheServiceSettings { PostgresConnectionString = "Host=localhost" };
        var state = new CacheServiceState(rollbackCapacity: 4);
        var caches = new CacheContainer(rollbackCapacity: 4);
        var service = new TestWarmupService(settings, state, caches, failuresBeforeSuccess: 2);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await service.ExecuteOnceAsync(cts.Token);

        service.AttemptCount.Should().Be(3);
        service.DelayCount.Should().Be(2);
        state.WarmupComplete.Should().BeTrue();
    }

    [Fact]
    public async Task StopsRetryingWhenCancellationRequested()
    {
        var settings = new CacheServiceSettings { PostgresConnectionString = "Host=localhost" };
        var state = new CacheServiceState(rollbackCapacity: 4);
        var caches = new CacheContainer(rollbackCapacity: 4);
        var service = new TestWarmupService(settings, state, caches, failuresBeforeSuccess: null);

        using var cts = new CancellationTokenSource();
        var executeTask = service.ExecuteOnceAsync(cts.Token);

        await WaitForAttemptsAsync(service, minimumAttempts: 2);
        cts.Cancel();

        await executeTask;

        service.AttemptCount.Should().BeGreaterOrEqualTo(2);
        state.WarmupComplete.Should().BeFalse();
    }

    private static async Task WaitForAttemptsAsync(TestWarmupService service, int minimumAttempts)
    {
        var timeout = DateTime.UtcNow + TimeSpan.FromSeconds(2);
        while (service.AttemptCount < minimumAttempts && DateTime.UtcNow < timeout)
        {
            await Task.Delay(10);
        }
    }

    private sealed class TestWarmupService : CacheWarmupService
    {
        private readonly int? _failuresBeforeSuccess;

        public TestWarmupService(
            CacheServiceSettings settings,
            CacheServiceState state,
            CacheContainer caches,
            int? failuresBeforeSuccess)
            : base(NullLogger<CacheWarmupService>.Instance, settings, state, caches)
        {
            _failuresBeforeSuccess = failuresBeforeSuccess;
        }

        public int AttemptCount { get; private set; }
        public int DelayCount { get; private set; }

        public Task ExecuteOnceAsync(CancellationToken token) => ExecuteAsync(token);

        protected override Task RunWarmupIterationAsync(CancellationToken stoppingToken)
        {
            AttemptCount++;

            if (!_failuresBeforeSuccess.HasValue || AttemptCount <= _failuresBeforeSuccess.Value)
            {
                if (!_failuresBeforeSuccess.HasValue)
                {
                    throw new InvalidOperationException("still failing");
                }

                if (AttemptCount <= _failuresBeforeSuccess.Value)
                {
                    throw new InvalidOperationException("transient failure");
                }
            }

            State.WarmupTargetBlock = 42;
            State.LastProcessedBlock = 42;
            State.WarmupComplete = true;
            return Task.CompletedTask;
        }

        protected override Task DelayAfterFailureAsync(CancellationToken ct)
        {
            DelayCount++;
            return Task.CompletedTask;
        }
    }
}

public class NotificationListenerServiceTests
{
    [Fact]
    public async Task ProcessesNewBlocksWhenHeadAdvances()
    {
        var settings = new CacheServiceSettings
        {
            RollbackCapacity = 5,
            PostgresConnectionString = "Host=localhost"
        };
        var state = new CacheServiceState(settings.RollbackCapacity)
        {
            LastProcessedBlock = 5
        };
        var caches = new CacheContainer(settings.RollbackCapacity);

        var blocks = new List<(long BlockNumber, string BlockHash)>
        {
            (4, "0x04"),
            (5, "0x05"),
            (6, "0x06"),
            (7, "0x07")
        };

        var service = new TestNotificationListenerService(settings, state, caches, blocks);

        await service.InvokeHandleAsync("{}", CancellationToken.None);

        service.ProcessedRanges.Should().Equal(new[] { (6L, 7L) });
        state.LastProcessedBlock.Should().Be(7);
        state.BlockRingBuffer.LatestBlockNumber.Should().Be(7);
    }

    [Fact]
    public async Task HandlesReorgAndRollsBackCaches()
    {
        var settings = new CacheServiceSettings
        {
            RollbackCapacity = 5,
            PostgresConnectionString = "Host=localhost"
        };
        var state = new CacheServiceState(settings.RollbackCapacity)
        {
            LastProcessedBlock = 10
        };
        state.BlockRingBuffer.UpdateFromBlocks(new[]
        {
            (8L, "0x08"),
            (9L, "0x09-old"),
            (10L, "0x10-old")
        });

        var caches = new CacheContainer(settings.RollbackCapacity);
        const string avatarKey = "0xabc";
        caches.V1Avatars.Add(10, avatarKey, ("Human", "0xtoken"));

        var blocks = new List<(long BlockNumber, string BlockHash)>
        {
            (8L, "0x08"),
            (9L, "0x09-new"),
            (10L, "0x10-new"),
            (11L, "0x11")
        };

        var service = new TestNotificationListenerService(settings, state, caches, blocks);

        await service.InvokeHandleAsync("{}", CancellationToken.None);

        service.ProcessedRanges.Should().Equal(new[] { (9L, 11L) });
        state.LastProcessedBlock.Should().Be(11);
        caches.V1Avatars.ContainsKey(avatarKey).Should().BeFalse();
    }

    private sealed class TestNotificationListenerService : NotificationListenerService
    {
        private readonly List<(long BlockNumber, string BlockHash)> _blocks;
        private readonly List<(long From, long To)> _processedRanges = new();

        public TestNotificationListenerService(
            CacheServiceSettings settings,
            CacheServiceState state,
            CacheContainer caches,
            List<(long BlockNumber, string BlockHash)> blocks)
            : base(NullLogger<NotificationListenerService>.Instance, settings, state, caches)
        {
            _blocks = blocks;
        }

        public IReadOnlyList<(long From, long To)> ProcessedRanges => _processedRanges;

        public Task InvokeHandleAsync(string payload, CancellationToken token) => HandleNotificationAsync(payload, token);

        protected override Task WithReadonlyConnectionAsync(Func<NpgsqlConnection, CancellationToken, Task> action, CancellationToken ct)
            => action(null!, ct);

        protected override Task<List<(long BlockNumber, string BlockHash)>> GetRecentBlocksAsync(
            NpgsqlConnection conn,
            int count,
            CancellationToken ct)
            => Task.FromResult(_blocks);

        protected override Task ProcessBlockRangeAsync(long fromBlock, long toBlock, CancellationToken ct)
        {
            _processedRanges.Add((fromBlock, toBlock));
            return Task.CompletedTask;
        }
    }
}
