using System.Text.Json;

namespace Circles.Metrics.Exporter.Services;

/// <summary>
/// Probes Circles RPC endpoints to determine deployment status.
/// Calls circles_tables to discover what the code supports,
/// then circles_query (limit=1) per table to verify data exists.
/// No direct database access required — works via public RPC URLs.
/// </summary>
public class DeploymentProber
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<DeploymentProber> _logger;

    /// <summary>
    /// Expected tables grouped by namespace.
    /// System tables excluded — not exposed via RPC.
    /// </summary>
    public static readonly Dictionary<string, string[]> ExpectedTables = new()
    {
        ["CrcV1"] = new[]
        {
            "CrcV1_Signup",
            "CrcV1_OrganizationSignup",
            "CrcV1_Trust",
            "CrcV1_HubTransfer",
            "CrcV1_Transfer",
            "CrcV1_TransferSummary",
            "CrcV1_UpdateMetadataDigest"
        },
        ["CrcV2"] = new[]
        {
            "CrcV2_PersonalMint",
            "CrcV2_RegisterGroup",
            "CrcV2_RegisterHuman",
            "CrcV2_RegisterOrganization",
            "CrcV2_Stopped",
            "CrcV2_Trust",
            "CrcV2_DiscountCost",
            "CrcV2_TransferSingle",
            "CrcV2_ApprovalForAll",
            "CrcV2_TransferBatch",
            "CrcV2_Erc20WrapperTransfer",
            "CrcV2_ERC20WrapperDeployed",
            "CrcV2_DepositInflationary",
            "CrcV2_WithdrawInflationary",
            "CrcV2_DepositDemurraged",
            "CrcV2_WithdrawDemurraged",
            "CrcV2_StreamCompleted",
            "CrcV2_GroupMint",
            "CrcV2_FlowEdgesScopeSingleStarted",
            "CrcV2_FlowEdgesScopeLastEnded",
            "CrcV2_TransferSummary",
            "CrcV2_SetAdvancedUsageFlag",
            "CrcV2_TransferData",
            "CrcV2_RegisterShortName",
            "CrcV2_UpdateMetadataDigest",
            "CrcV2_CidV0"
        },
        ["CrcV2_AffiliateGroupRegistry"] = new[]
        {
            "CrcV2_AffiliateGroupChanged",
            "CrcV2_NotificationFailed",
            "CrcV2_NotificationSuccessful"
        },
        ["CrcV2_BaseGroupDeployer"] = new[]
        {
            "CrcV2_BaseGroupCreated",
            "CrcV2_BaseGroupOwnerUpdated",
            "CrcV2_BaseGroupServiceUpdated",
            "CrcV2_BaseGroupFeeCollectionUpdated"
        },
        ["CrcV2_CMGroupDeployer"] = new[]
        {
            "CrcV2_CMGroupCreated"
        },
        ["CrcV2_InvitationEscrow"] = new[]
        {
            "CrcV2_InvitationEscrow_InvitationEscrowed",
            "CrcV2_InvitationEscrow_InvitationRedeemed",
            "CrcV2_InvitationEscrow_InvitationRefunded",
            "CrcV2_InvitationEscrow_InvitationRevoked"
        },
        ["CrcV2_InvitationsAtScale"] = new[]
        {
            "CrcV2_InvitationsAtScale_RegisterHuman",
            "CrcV2_InvitationsAtScale_AccountCreated",
            "CrcV2_InvitationsAtScale_AccountClaimed",
            "CrcV2_InvitationsAtScale_AdminSet",
            "CrcV2_InvitationsAtScale_MaintainerSet",
            "CrcV2_InvitationsAtScale_SeederSet",
            "CrcV2_InvitationsAtScale_InviterQuotaUpdated",
            "CrcV2_InvitationsAtScale_InvitationModuleUpdated",
            "CrcV2_InvitationsAtScale_BotCreated",
            "CrcV2_InvitationsAtScale_InvitesClaimed",
            "CrcV2_InvitationsAtScale_FarmGrown",
            "CrcV2_InvitationsAtScale_QuotaPermissionGranted",
            "CrcV2_InvitationsAtScale_QuotaPermissionRevoked",
            "CrcV2_InvitationsAtScale_InviterQuotaSet",
            "CrcV2_InvitationsAtScale_InviterExtraQuotaAdded"
        },
        ["CrcV2_LBP"] = new[]
        {
            "CrcV2_CirclesBackingDeployed",
            "CrcV2_LBPDeployed",
            "CrcV2_CirclesBackingInitiated",
            "CrcV2_CirclesBackingCompleted",
            "CrcV2_Released"
        },
        ["CrcV2_OIC"] = new[]
        {
            "CrcV2_OIC_OpenMiddlewareTransfer"
        },
        ["CrcV2_PaymentGateway"] = new[]
        {
            "CrcV2_PaymentGateway_GatewayCreated",
            "CrcV2_PaymentGateway_PaymentReceived",
            "CrcV2_PaymentGateway_TrustUpdated"
        },
        ["CrcV2_StandardTreasury"] = new[]
        {
            "CrcV2_CreateVault",
            "CrcV2_CollateralLockedSingle",
            "CrcV2_CollateralLockedBatch",
            "CrcV2_GroupRedeem",
            "CrcV2_GroupRedeemCollateralReturn",
            "CrcV2_GroupRedeemCollateralBurn"
        },
        ["CrcV2_TokenOffers"] = new[]
        {
            "CrcV2_TokenOffers_AccountWeightProviderCreated",
            "CrcV2_TokenOffers_ERC20TokenOfferCreated",
            "CrcV2_TokenOffers_ERC20TokenOfferCycleCreated",
            "CrcV2_TokenOffers_CycleConfiguration",
            "CrcV2_TokenOffers_NextOfferCreated",
            "CrcV2_TokenOffers_NextOfferTokensDeposited",
            "CrcV2_TokenOffers_OfferTrustSynced",
            "CrcV2_TokenOffers_OfferClaimedFromCycle",
            "CrcV2_TokenOffers_UnclaimedTokensWithdrawn",
            "CrcV2_TokenOffers_OfferClaimed",
            "CrcV2_TokenOffers_OfferTokensDeposited",
            "CrcV2_TokenOffers_AccountWeightSet",
            "CrcV2_TokenOffers_WeightsFinalized"
        },
        ["Safe"] = new[]
        {
            "Safe_ProxyCreation",
            "Safe_SafeSetup",
            "Safe_AddedOwner",
            "Safe_RemovedOwner"
        }
    };

    public record TableStatus(string Namespace, string FullTableName, bool Exists);

    public DeploymentProber(HttpClient httpClient, ILogger<DeploymentProber> logger)
    {
        _httpClient = httpClient;
        _httpClient.Timeout = TimeSpan.FromSeconds(60);
        _logger = logger;
    }

    /// <summary>
    /// Probe a single environment: call circles_tables, then circles_query per table.
    /// Returns status for every expected table.
    /// </summary>
    public async Task<(List<TableStatus> Statuses, int SchemaCount)?> ProbeEnvironmentAsync(
        string rpcUrl, string environment, CancellationToken ct)
    {
        // 1. Get schema from circles_tables
        var schemaTables = await GetSchemaTablesAsync(rpcUrl, ct);
        if (schemaTables == null)
        {
            _logger.LogWarning("[{Environment}] circles_tables call failed — RPC unreachable?", environment);
            return null;
        }

        // Build lookup: fullName → (rpcNamespace, rpcTable) for circles_query calls
        var schemaLookup = new Dictionary<string, (string Ns, string Table)>();
        foreach (var (ns, tables) in schemaTables)
        {
            foreach (var table in tables)
            {
                schemaLookup[$"{ns}_{table}"] = (ns, table);
            }
        }

        // Build reverse lookup: fullName → our namespace label
        var fullNameToLabel = new Dictionary<string, string>();
        foreach (var (label, tableNames) in ExpectedTables)
        {
            foreach (var name in tableNames)
            {
                fullNameToLabel[name] = label;
            }
        }

        // 2. For each expected table, check schema + probe data
        var results = new List<TableStatus>();
        var semaphore = new SemaphoreSlim(10); // max 10 concurrent probes per env
        var probeTasks = new List<Task>();

        foreach (var (label, tableNames) in ExpectedTables)
        {
            foreach (var fullName in tableNames)
            {
                if (!schemaLookup.TryGetValue(fullName, out var split))
                {
                    // Not in schema — code doesn't support this table
                    results.Add(new TableStatus(label, fullName, false));
                    continue;
                }

                // In schema — probe for data existence
                var capturedLabel = label;
                var capturedFullName = fullName;
                var capturedSplit = split;

                probeTasks.Add(Task.Run(async () =>
                {
                    await semaphore.WaitAsync(ct);
                    try
                    {
                        var hasData = await ProbeTableAsync(rpcUrl, capturedSplit.Ns, capturedSplit.Table, ct);
                        lock (results)
                        {
                            results.Add(new TableStatus(capturedLabel, capturedFullName, hasData));
                        }
                    }
                    finally
                    {
                        semaphore.Release();
                    }
                }, ct));
            }
        }

        await Task.WhenAll(probeTasks);

        _logger.LogDebug("[{Environment}] Schema has {SchemaCount} tables, probed {ProbeCount} expected tables: {Existing} exist",
            environment, schemaLookup.Count, results.Count, results.Count(r => r.Exists));

        return (results, schemaLookup.Count);
    }

    /// <summary>
    /// Calls circles_tables on the RPC endpoint.
    /// Returns namespace → table name list, or null if RPC is unreachable.
    /// </summary>
    private async Task<Dictionary<string, List<string>>?> GetSchemaTablesAsync(string rpcUrl, CancellationToken ct)
    {
        try
        {
            var payload = JsonSerializer.Serialize(new Dictionary<string, object>
            {
                ["jsonrpc"] = "2.0",
                ["method"] = "circles_tables",
                ["params"] = Array.Empty<object>(),
                ["id"] = 1
            });

            var content = new StringContent(payload, System.Text.Encoding.UTF8, "application/json");
            var response = await _httpClient.PostAsync(rpcUrl, content, ct);
            var body = await response.Content.ReadAsStringAsync(ct);
            var doc = JsonDocument.Parse(body);

            if (!doc.RootElement.TryGetProperty("result", out var result))
                return null;

            var tables = new Dictionary<string, List<string>>();
            foreach (var ns in result.EnumerateArray())
            {
                var nsName = ns.GetProperty("namespace").GetString()!;
                if (!tables.TryGetValue(nsName, out var tableList))
                {
                    tableList = new List<string>();
                    tables[nsName] = tableList;
                }
                foreach (var table in ns.GetProperty("tables").EnumerateArray())
                {
                    tableList.Add(table.GetProperty("table").GetString()!);
                }
            }
            return tables;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to call circles_tables");
            return null;
        }
    }

    /// <summary>
    /// Probes a single table via circles_query with limit=1.
    /// Returns true if the query succeeds and returns at least one row.
    /// </summary>
    private async Task<bool> ProbeTableAsync(string rpcUrl, string ns, string table, CancellationToken ct)
    {
        try
        {
            var queryParams = new Dictionary<string, object>
            {
                ["namespace"] = ns,
                ["table"] = table,
                ["limit"] = 1
            };
            var payload = JsonSerializer.Serialize(new Dictionary<string, object>
            {
                ["jsonrpc"] = "2.0",
                ["method"] = "circles_query",
                ["params"] = new object[] { queryParams },
                ["id"] = 1
            });

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(10)); // per-table timeout

            var content = new StringContent(payload, System.Text.Encoding.UTF8, "application/json");
            var response = await _httpClient.PostAsync(rpcUrl, content, cts.Token);
            var body = await response.Content.ReadAsStringAsync(cts.Token);
            var doc = JsonDocument.Parse(body);

            if (doc.RootElement.TryGetProperty("result", out var result) &&
                result.TryGetProperty("rows", out var rows))
            {
                return rows.GetArrayLength() > 0;
            }

            return false;
        }
        catch
        {
            // Query error = table doesn't exist in DB or is empty
            return false;
        }
    }
}
