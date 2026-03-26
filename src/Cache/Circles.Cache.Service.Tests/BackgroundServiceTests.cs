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
    private static readonly NpgsqlDataSource DummyDataSource =
        NpgsqlDataSource.Create("Host=localhost;Database=dummy");
    [Fact]
    public async Task RetriesUntilSuccessAndMarksWarmupComplete()
    {
        var settings = new CacheServiceSettings { PostgresConnectionString = "Host=localhost" };
        var state = new CacheServiceState(rollbackCapacity: 4);
        var caches = new CacheContainer(rollbackCapacity: 4);
        var service = new TestWarmupService(settings, state, caches, failuresBeforeSuccess: 2);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var executeTask = service.ExecuteOnceAsync(cts.Token);

        await WaitForWarmupCompleteAsync(state);
        cts.Cancel();
        await executeTask;

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

    private static async Task WaitForWarmupCompleteAsync(CacheServiceState state)
    {
        var timeout = DateTime.UtcNow + TimeSpan.FromSeconds(2);
        while (!state.WarmupComplete && DateTime.UtcNow < timeout)
        {
            await Task.Delay(10);
        }

        state.WarmupComplete.Should().BeTrue();
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
            : base(NullLogger<CacheWarmupService>.Instance, settings, state, caches, DummyDataSource)
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
            return Task.Delay(1, ct);
        }
    }
}

public class NotificationListenerServiceTests
{
    private static readonly NpgsqlDataSource DummyDataSource =
        NpgsqlDataSource.Create("Host=localhost;Database=dummy");
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
            LastProcessedBlock = 5,
            WarmupComplete = true
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
        state.CurrentBlockTimestamp.Should().Be(7000);
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
            LastProcessedBlock = 10,
            WarmupComplete = true
        };
        state.WarmupTargetBlock = 8;
        state.BlockRingBuffer.UpdateFromBlocks(new[]
        {
            (8L, "0x08"),
            (9L, "0x09-old"),
            (10L, "0x10-old")
        });

        var caches = new CacheContainer(settings.RollbackCapacity);
        const string avatarKey = "0xabc";
        const string dummyAvatarKey = "0xdummy";

        // Seed ALL caches with initial state at block 8 (the common ancestor before the reorg)
        // This establishes the rollback baseline so RollbackAll can roll back to block 9
        SeedAllCachesAtBlock(caches, 8);

        // Add data at block 9 so it exists in the rollback history
        // (rollback to block 9 requires block 9 to be in _blockOrder)
        caches.V1Avatars.Add(9, dummyAvatarKey, ("CrcV1_Signup", "0xdummy"));
        caches.V1Avatars.Add(10, avatarKey, ("CrcV1_Signup", "0xtoken"));

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
        // Both avatars added at blocks 9 and 10 should be rolled back (removed)
        caches.V1Avatars.ContainsKey(avatarKey).Should().BeFalse();
        caches.V1Avatars.ContainsKey(dummyAvatarKey).Should().BeFalse();
    }

    [Fact]
    public async Task ReorgCrossingWarmupSeedBoundary_TriggersFullRewarmup()
    {
        var settings = new CacheServiceSettings
        {
            RollbackCapacity = 5,
            PostgresConnectionString = "Host=localhost"
        };
        var state = new CacheServiceState(settings.RollbackCapacity)
        {
            LastProcessedBlock = 102,
            WarmupComplete = true,
            WarmupTargetBlock = 100,
            CurrentBlockTimestamp = 12345
        };

        state.BlockRingBuffer.UpdateFromBlocks(new[]
        {
            (98L, "0x98"),
            (99L, "0x99"),
            (100L, "0x100-old"),
            (101L, "0x101-old"),
            (102L, "0x102-old")
        });

        var caches = new CacheContainer(settings.RollbackCapacity);
        SeedAllCachesAtBlock(caches, 100);
        caches.V1Avatars.Add(101, "0xuser", ("CrcV1_Signup", "0xtoken"));

        var blocks = new List<(long BlockNumber, string BlockHash)>
        {
            (98L, "0x98"),
            (99L, "0x99"),
            (100L, "0x100-new"),
            (101L, "0x101-new"),
            (102L, "0x102-new")
        };

        var service = new TestNotificationListenerService(settings, state, caches, blocks);

        await service.InvokeHandleAsync("{}", CancellationToken.None);

        service.ProcessedRanges.Should().BeEmpty();
        state.WarmupComplete.Should().BeFalse();
        state.LastProcessedBlock.Should().Be(-1);
        state.CurrentBlockTimestamp.Should().Be(0);
        caches.V1Avatars.Count.Should().Be(0);
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
            : base(NullLogger<NotificationListenerService>.Instance, settings, state, caches, DummyDataSource)
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

        protected override Task<long> GetBlockTimestampAsync(long blockNumber, CancellationToken ct)
            => Task.FromResult(blockNumber * 1000);
    }

    [Fact]
    public async Task RecoverFromProcessingFailure_WithinRollbackCapacity_RollsBackAndRetries()
    {
        var settings = new CacheServiceSettings
        {
            RollbackCapacity = 12,
            PostgresConnectionString = "Host=localhost"
        };
        var state = new CacheServiceState(settings.RollbackCapacity)
        {
            LastProcessedBlock = 100,
            WarmupComplete = true
        };
        state.BlockRingBuffer.UpdateFromBlocks(new[]
        {
            (99L, "0x99"),
            (100L, "0x100")
        });

        var caches = new CacheContainer(settings.RollbackCapacity);

        // Seed caches at block 100 to establish rollback history
        SeedAllCachesAtBlock(caches, 100);

        var blocks = new List<(long BlockNumber, string BlockHash)>
        {
            (99L, "0x99"),
            (100L, "0x100"),
            (101L, "0x101"),
            (102L, "0x102"),
            (103L, "0x103")
        };

        // First call fails, second succeeds
        var failOnFirstCall = true;
        var service = new FailingNotificationListenerService(
            settings, state, caches, blocks,
            shouldFail: () =>
            {
                if (failOnFirstCall)
                {
                    failOnFirstCall = false;
                    return true;
                }
                return false;
            });

        // First call - should fail but recover via rollback
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.InvokeHandleAsync("{}", CancellationToken.None));

        // State should still be at 100 (not advanced due to failure)
        state.LastProcessedBlock.Should().Be(100);

        // Warmup should still be complete (rollback succeeded, we're within capacity)
        state.WarmupComplete.Should().BeTrue();

        // Second call - should succeed now
        await service.InvokeHandleAsync("{}", CancellationToken.None);

        // Now state should be updated
        state.LastProcessedBlock.Should().Be(103);
    }

    [Fact]
    public async Task RecoverFromProcessingFailure_BeyondRollbackCapacity_TriggersReWarmup()
    {
        var settings = new CacheServiceSettings
        {
            RollbackCapacity = 5, // Small capacity for testing
            PostgresConnectionString = "Host=localhost"
        };
        var state = new CacheServiceState(settings.RollbackCapacity)
        {
            LastProcessedBlock = 100,
            WarmupComplete = true
        };
        var caches = new CacheContainer(settings.RollbackCapacity);

        // Seed caches at block 100
        SeedAllCachesAtBlock(caches, 100);

        // Simulate that caches have advanced far beyond the rollback window
        // by adding data at blocks that push the oldest block out of the window
        for (int i = 101; i <= 120; i++)
        {
            caches.V1Avatars.Add(i, $"0xuser{i}", ("CrcV1_Signup", $"0xtoken{i}"));
        }

        // Now the cache's oldest block is around 116 (120 - 5 + 1)
        // The cache's LastBlockNo is 120
        // If state.LastProcessedBlock is 100, fromBlock will be 101
        // Rollback to 101 will fail because it's beyond the rollback window

        var blocks = new List<(long BlockNumber, string BlockHash)>
        {
            (118L, "0x118"),
            (119L, "0x119"),
            (120L, "0x120"),
            (121L, "0x121")
        };

        // Create a service that will fail during processing
        // fromBlock will be 101 (LastProcessedBlock + 1)
        // Rollback to 101 will fail -> triggers re-warmup
        var service = new FailingNotificationListenerService(
            settings, state, caches, blocks,
            shouldFail: () => true); // Always fail

        // This should fail and trigger re-warmup because rollback to block 101 is impossible
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.InvokeHandleAsync("{}", CancellationToken.None));

        // WarmupComplete should be false (triggering re-warmup)
        state.WarmupComplete.Should().BeFalse();

        // LastProcessedBlock should be reset
        state.LastProcessedBlock.Should().Be(-1);
    }

    private sealed class FailingNotificationListenerService : NotificationListenerService
    {
        private readonly List<(long BlockNumber, string BlockHash)> _blocks;
        private readonly Func<bool> _shouldFail;

        public FailingNotificationListenerService(
            CacheServiceSettings settings,
            CacheServiceState state,
            CacheContainer caches,
            List<(long BlockNumber, string BlockHash)> blocks,
            Func<bool> shouldFail)
            : base(NullLogger<NotificationListenerService>.Instance, settings, state, caches, DummyDataSource)
        {
            _blocks = blocks;
            _shouldFail = shouldFail;
        }

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
            if (_shouldFail())
            {
                throw new InvalidOperationException("Simulated processing failure");
            }
            return Task.CompletedTask;
        }

        protected override Task<long> GetBlockTimestampAsync(long blockNumber, CancellationToken ct)
            => Task.FromResult(blockNumber * 1000);
    }

    [Fact]
    public async Task HandleNotification_SkipsProcessing_WhenWarmupNotComplete()
    {
        var settings = new CacheServiceSettings
        {
            RollbackCapacity = 5,
            PostgresConnectionString = "Host=localhost"
        };
        var state = new CacheServiceState(settings.RollbackCapacity)
        {
            LastProcessedBlock = 100,
            WarmupComplete = false // Warmup in progress
        };
        var caches = new CacheContainer(settings.RollbackCapacity);

        var blocks = new List<(long BlockNumber, string BlockHash)>
        {
            (100L, "0x100"),
            (101L, "0x101"),
            (102L, "0x102")
        };

        var service = new TestNotificationListenerService(settings, state, caches, blocks);

        await service.InvokeHandleAsync("{}", CancellationToken.None);

        // Should not have processed any blocks
        service.ProcessedRanges.Should().BeEmpty();
        // State should be unchanged
        state.LastProcessedBlock.Should().Be(100);
        // Buffer should be empty (not populated by skipped notification)
        state.BlockRingBuffer.Count.Should().Be(0);
    }

    [Fact]
    public void InitializeBlockRingBuffer_ClearsBufferBeforeAdding()
    {
        // Simulates the race: listener added newer blocks to the buffer
        // before warmup's InitializeBlockRingBufferAsync runs
        var buffer = new BlockRingBuffer(capacity: 10);

        // Listener added newer blocks after TriggerFullRewarmup cleared the buffer
        buffer.Add(200, "0xC8");
        buffer.Add(201, "0xC9");

        // Warmup clears before initializing (our fix)
        buffer.Clear();

        // Now warmup can add older blocks without exception
        buffer.Add(190, "0xBE");
        buffer.Add(191, "0xBF");

        buffer.Count.Should().Be(2);
        buffer.LatestBlockNumber.Should().Be(191);
    }

    // ──────────────────────────────────────────────────────────────────────
    // Critical regression tests for the race-condition fix (PR)
    // ──────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ConcurrentNotifications_AreSerialized_NoDuplicateBlockProcessing()
    {
        var settings = new CacheServiceSettings
        {
            RollbackCapacity = 5,
            PostgresConnectionString = "Host=localhost"
        };
        var state = new CacheServiceState(settings.RollbackCapacity)
        {
            LastProcessedBlock = 5,
            WarmupComplete = true
        };
        var caches = new CacheContainer(settings.RollbackCapacity);

        var blocks = new List<(long BlockNumber, string BlockHash)>
        {
            (4, "0x04"), (5, "0x05"), (6, "0x06"), (7, "0x07")
        };

        // SlowNotificationListenerService introduces a delay in GetRecentBlocksAsync
        // so that two concurrent handlers overlap in time.
        var service = new SlowNotificationListenerService(settings, state, caches, blocks, delayMs: 50);

        // Fire two concurrent notifications
        var t1 = service.InvokeHandleAsync("{}", CancellationToken.None);
        var t2 = service.InvokeHandleAsync("{}", CancellationToken.None);
        await Task.WhenAll(t1, t2);

        // The semaphore serializes: first handler processes 6→7, second sees
        // LastProcessedBlock=7 and has nothing to do. Range must appear exactly once.
        service.ProcessedRanges.Should().HaveCount(1);
        service.ProcessedRanges[0].Should().Be((6L, 7L));
        state.LastProcessedBlock.Should().Be(7);
    }

    [Fact]
    public async Task AfterRewarmup_FirstNotification_ProcessesFromBlock0()
    {
        var settings = new CacheServiceSettings
        {
            RollbackCapacity = 5,
            PostgresConnectionString = "Host=localhost"
        };
        // After TriggerFullRewarmup, LastProcessedBlock is -1.
        // Warmup sets WarmupComplete=true once done. First notification
        // should compute fromBlock = -1 + 1 = 0 and process from block 0.
        var state = new CacheServiceState(settings.RollbackCapacity)
        {
            LastProcessedBlock = -1,
            WarmupComplete = true,
            WarmupTargetBlock = 0
        };
        var caches = new CacheContainer(settings.RollbackCapacity);

        var blocks = new List<(long BlockNumber, string BlockHash)>
        {
            (0L, "0x00"), (1L, "0x01"), (2L, "0x02")
        };

        var service = new TestNotificationListenerService(settings, state, caches, blocks);

        await service.InvokeHandleAsync("{}", CancellationToken.None);

        // fromBlock = -1 + 1 = 0 — must not skip block 0
        service.ProcessedRanges.Should().Equal(new[] { (0L, 2L) });
        state.LastProcessedBlock.Should().Be(2);
    }

    [Fact]
    public async Task DeepReorg_ExceedsRollbackCapacity_TriggersFullRewarmup()
    {
        var settings = new CacheServiceSettings
        {
            RollbackCapacity = 10, // Used for ring buffer
            PostgresConnectionString = "Host=localhost"
        };
        // Ring buffer capacity = 10 (detects reorgs within 10 blocks)
        var state = new CacheServiceState(rollbackCapacity: 10)
        {
            LastProcessedBlock = 110,
            WarmupComplete = true,
            WarmupTargetBlock = 100
        };

        // Ring buffer has blocks 101-110 (capacity 10)
        state.BlockRingBuffer.UpdateFromBlocks(
            Enumerable.Range(101, 10).Select(i => ((long)i, $"0x{i:X}")).ToList());

        // Cache capacity = 3 → only retains diffs for blocks 108-110
        var caches = new CacheContainer(rollbackCapacity: 3);
        SeedAllCachesAtBlock(caches, 100);
        for (int i = 101; i <= 110; i++)
            caches.V1Avatars.Add(i, $"0xuser{i}", ("CrcV1_Signup", $"0xtoken{i}"));

        // Incoming blocks: hash mismatch at block 105 (detected by ring buffer),
        // but rollback to 105 fails because cache only retains diffs for 108-110.
        var blocks = new List<(long BlockNumber, string BlockHash)>
        {
            (105L, "0x69-new"), // reorg here — hash differs from "0x69"
            (106L, "0x6A-new"),
            (107L, "0x6B-new"),
            (108L, "0x6C-new"),
            (109L, "0x6D-new"),
            (110L, "0x6E-new"),
            (111L, "0x6F")
        };

        var service = new TestNotificationListenerService(settings, state, caches, blocks);

        await service.InvokeHandleAsync("{}", CancellationToken.None);

        // Deep reorg (105) exceeds cache rollback capacity (3, oldest=108)
        // → InvalidOperationException caught → TriggerFullRewarmup
        state.WarmupComplete.Should().BeFalse();
        state.LastProcessedBlock.Should().Be(-1);
        service.ProcessedRanges.Should().BeEmpty();
    }

    private sealed class SlowNotificationListenerService : NotificationListenerService
    {
        private readonly List<(long BlockNumber, string BlockHash)> _blocks;
        private readonly List<(long From, long To)> _processedRanges = new();
        private readonly int _delayMs;

        public SlowNotificationListenerService(
            CacheServiceSettings settings,
            CacheServiceState state,
            CacheContainer caches,
            List<(long BlockNumber, string BlockHash)> blocks,
            int delayMs)
            : base(NullLogger<NotificationListenerService>.Instance, settings, state, caches, DummyDataSource)
        {
            _blocks = blocks;
            _delayMs = delayMs;
        }

        public IReadOnlyList<(long From, long To)> ProcessedRanges => _processedRanges;

        public Task InvokeHandleAsync(string payload, CancellationToken token) => HandleNotificationAsync(payload, token);

        protected override Task WithReadonlyConnectionAsync(Func<NpgsqlConnection, CancellationToken, Task> action, CancellationToken ct)
            => action(null!, ct);

        protected override async Task<List<(long BlockNumber, string BlockHash)>> GetRecentBlocksAsync(
            NpgsqlConnection conn,
            int count,
            CancellationToken ct)
        {
            // Delay to simulate DB query time, creating an overlap window
            await Task.Delay(_delayMs, ct);
            return _blocks;
        }

        protected override Task ProcessBlockRangeAsync(long fromBlock, long toBlock, CancellationToken ct)
        {
            _processedRanges.Add((fromBlock, toBlock));
            return Task.CompletedTask;
        }

        protected override Task<long> GetBlockTimestampAsync(long blockNumber, CancellationToken ct)
            => Task.FromResult(blockNumber * 1000);
    }

    /// <summary>
    /// Seeds all caches in the container with empty data at the specified block.
    /// This establishes a rollback baseline for the RollbackAll operation.
    /// </summary>
    private static void SeedAllCachesAtBlock(CacheContainer caches, long blockNo)
    {
        caches.V1Avatars.Seed(new Dictionary<string, (string, string?)>(), blockNo);
        caches.V1TokenOwnerByToken.Seed(new Dictionary<string, string>(), blockNo);
        caches.V1AvatarToCidMap.Seed(new Dictionary<string, string>(), blockNo);
        caches.V2Avatars.Seed(new Dictionary<string, (string, long)>(), blockNo);
        caches.Erc20WrapperAddresses.Seed(new Dictionary<string, (string, int)>(), blockNo);
        caches.Groups.Seed(new Dictionary<string, (string, string, string)>(), blockNo);
        caches.GroupMemberships.Seed(new Dictionary<string, (string Member, long ExpiryTime)>(), blockNo);
        caches.V2AvatarToCidMap.Seed(new Dictionary<string, string>(), blockNo);
        caches.V2AvatarToShortNameMap.Seed(new Dictionary<string, string>(), blockNo);
        caches.V1BalancesByAccountAndToken.Seed(new Dictionary<string, decimal>(), blockNo);
        caches.V2BalancesByAccountAndToken.Seed(new Dictionary<string, decimal>(), blockNo);
        caches.V2LastActivity.Seed(new Dictionary<string, long>(), blockNo);
        caches.V1TrustRelations.Seed(new Dictionary<string, long>(), blockNo);
        caches.V2TrustRelations.Seed(new Dictionary<string, long>(), blockNo);
        caches.ConsentedFlowFlags.Seed(new Dictionary<string, byte[]>(), blockNo);
    }
}
