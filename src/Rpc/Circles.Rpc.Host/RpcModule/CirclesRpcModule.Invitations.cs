using System.Text;
using Npgsql;

namespace Circles.Rpc.Host;

/// <summary>
/// Invitation-related methods for CirclesRpcModule.
/// </summary>
public partial class CirclesRpcModule
{
    /// <summary>
    /// Gets list of valid inviters for an address (addresses that trust them and have sufficient balance).
    /// Useful for invitation flows and invitation escrow scenarios.
    /// </summary>
    public async Task<PagedValidInvitersResponse> GetValidInviters(
        string address,
        string? minimumBalance = null,
        int? limit = null,
        string? cursor = null)
    {
        // Apply pagination limits
        const int defaultLimit = 50;
        const int maxLimit = 200;
        var effectiveLimit = Math.Min(limit ?? defaultLimit, maxLimit);

        var trustRelations = await GetTrustRelations(address);
        var trustedByAddresses = trustRelations.TrustedBy?.Select(t => t.User).OrderBy(a => a).ToList() ?? new List<string>();

        if (trustedByAddresses.Count == 0)
        {
            return new PagedValidInvitersResponse
            {
                Address = address,
                Results = Array.Empty<InviterInfo>(),
                HasMore = false,
                NextCursor = null
            };
        }

        // Decode cursor (using address as cursor)
        string? cursorAddress = null;
        if (!string.IsNullOrEmpty(cursor))
        {
            try
            {
                cursorAddress = Encoding.UTF8.GetString(Convert.FromBase64String(cursor));
            }
            catch
            {
                // Invalid cursor, ignore
            }
        }

        // Filter by cursor
        if (cursorAddress != null)
        {
            trustedByAddresses = trustedByAddresses.Where(a => string.Compare(a, cursorAddress, StringComparison.Ordinal) > 0).ToList();
        }

        // Process addresses and collect valid inviters until we have enough
        var validInviters = new List<InviterInfo>();
        var processedCount = 0;

        foreach (var inviterAddress in trustedByAddresses)
        {
            if (validInviters.Count > effectiveLimit)
            {
                break; // We have enough (including the extra one for hasMore check)
            }

            try
            {
                // Get avatar info to determine version
                var avatarInfo = await GetAvatarInfoBatchInternal(new[] { inviterAddress });
                var avatar = avatarInfo.FirstOrDefault();

                if (avatar == null)
                {
                    processedCount++;
                    continue;
                }

                // Get balance (try both v1 and v2)
                TotalBalanceResponse? balance = null;

                if (avatar.Version == 2)
                {
                    try
                    {
                        balance = await GetTotalBalance(inviterAddress, 2, true);
                    }
                    catch { }
                }
                else if (avatar.HasV1 == true)
                {
                    try
                    {
                        balance = await GetTotalBalance(inviterAddress, 1, true);
                    }
                    catch { }
                }

                if (balance != null)
                {
                    // Check minimum balance if specified
                    if (string.IsNullOrEmpty(minimumBalance) ||
                        decimal.TryParse(balance.Balance, out var balanceValue) &&
                        decimal.TryParse(minimumBalance, out var minValue) &&
                        balanceValue >= minValue)
                    {
                        validInviters.Add(new InviterInfo
                        {
                            Address = inviterAddress,
                            Balance = balance.Balance,
                            AvatarInfo = avatar
                        });
                    }
                }
            }
            catch
            {
                // Skip inviters with errors
            }

            processedCount++;
        }

        // Determine if there are more results
        var hasMore = validInviters.Count > effectiveLimit;
        if (hasMore)
        {
            validInviters.RemoveAt(validInviters.Count - 1);
        }

        // Generate next cursor from last address
        string? nextCursor = null;
        if (hasMore && validInviters.Count > 0)
        {
            nextCursor = Convert.ToBase64String(Encoding.UTF8.GetBytes(validInviters[^1].Address));
        }

        return new PagedValidInvitersResponse
        {
            Address = address,
            Results = validInviters.ToArray(),
            HasMore = hasMore,
            NextCursor = nextCursor
        };
    }

    /// <summary>
    /// Gets the invitation origin for an address, reconstructing how they were invited to Circles.
    /// Checks multiple invitation mechanisms in order of specificity:
    /// 1. InvitationsAtScale.RegisterHuman (most specific - has originInviter + proxyInviter)
    /// 2. InvitationEscrow.InvitationRedeemed (escrow invitation)
    /// 3. CrcV2.RegisterHuman (standard V2 invitation)
    /// 4. CrcV1.Signup (V1 self-signup)
    /// </summary>
    public async Task<InvitationOriginResponse?> GetInvitationOrigin(string address)
    {
        var normalizedAddress = address.ToLowerInvariant();
        await using var connection = await CreateConnectionAsync();

        // 1. Check InvitationsAtScale.RegisterHuman (most specific - has originInviter + proxyInviter)
        var atScaleResult = await QueryInvitationsAtScale(connection, normalizedAddress);
        if (atScaleResult != null) return atScaleResult;

        // 2. Check InvitationEscrow.InvitationRedeemed (escrow invitation)
        var escrowResult = await QueryEscrowInvitation(connection, normalizedAddress);
        if (escrowResult != null) return escrowResult;

        // 3. Check CrcV2.RegisterHuman (standard V2 invitation)
        var v2Result = await QueryV2RegisterHuman(connection, normalizedAddress);
        if (v2Result != null) return v2Result;

        // 4. Check CrcV1.Signup (V1 self-signup)
        var v1Result = await QueryV1Signup(connection, normalizedAddress);
        if (v1Result != null) return v1Result;

        return null;
    }

    /// <summary>
    /// Queries the InvitationsAtScale.RegisterHuman table for registration with origin/proxy inviters.
    /// </summary>
    private async Task<InvitationOriginResponse?> QueryInvitationsAtScale(NpgsqlConnection connection, string address)
    {
        const string sql = @"
            SELECT ""blockNumber"", ""timestamp"", ""transactionHash"", ""human"", ""originInviter"", ""proxyInviter""
            FROM ""CrcV2_InvitationsAtScale_RegisterHuman""
            WHERE ""human"" = @address
            LIMIT 1";

        await using var cmd = new NpgsqlCommand(sql, connection);
        cmd.Parameters.AddWithValue("address", address);

        await using var reader = await cmd.ExecuteReaderAsync();
        if (await reader.ReadAsync())
        {
            var blockNumber = reader.GetInt64(0);
            var timestamp = reader.GetInt64(1);
            var transactionHash = reader.GetString(2);
            var human = reader.GetString(3);
            var originInviter = reader.IsDBNull(4) ? null : reader.GetString(4);
            var proxyInviter = reader.IsDBNull(5) ? null : reader.GetString(5);

            // Check if originInviter is the zero address (no inviter)
            var zeroAddress = "0x0000000000000000000000000000000000000000";
            var effectiveInviter = originInviter == zeroAddress ? null : originInviter;
            var effectiveProxyInviter = proxyInviter == zeroAddress ? null : proxyInviter;

            return new InvitationOriginResponse(
                Address: human,
                InvitationType: "v2_at_scale",
                Inviter: effectiveInviter,
                ProxyInviter: effectiveProxyInviter,
                EscrowAmount: null,
                BlockNumber: blockNumber,
                Timestamp: timestamp,
                TransactionHash: transactionHash,
                Version: 2
            );
        }

        return null;
    }

    /// <summary>
    /// Queries the InvitationEscrow.InvitationRedeemed table for escrow-based invitations.
    /// </summary>
    private async Task<InvitationOriginResponse?> QueryEscrowInvitation(NpgsqlConnection connection, string address)
    {
        const string sql = @"
            SELECT ""blockNumber"", ""timestamp"", ""transactionHash"", ""inviter"", ""invitee"", ""amount""
            FROM ""CrcV2_InvitationEscrow_InvitationRedeemed""
            WHERE ""invitee"" = @address
            LIMIT 1";

        await using var cmd = new NpgsqlCommand(sql, connection);
        cmd.Parameters.AddWithValue("address", address);

        await using var reader = await cmd.ExecuteReaderAsync();
        if (await reader.ReadAsync())
        {
            var blockNumber = reader.GetInt64(0);
            var timestamp = reader.GetInt64(1);
            var transactionHash = reader.GetString(2);
            var inviter = reader.GetString(3);
            var invitee = reader.GetString(4);
            var amount = reader.GetDecimal(5);

            return new InvitationOriginResponse(
                Address: invitee,
                InvitationType: "v2_escrow",
                Inviter: inviter,
                ProxyInviter: null,
                EscrowAmount: amount.ToString("F0", System.Globalization.CultureInfo.InvariantCulture),
                BlockNumber: blockNumber,
                Timestamp: timestamp,
                TransactionHash: transactionHash,
                Version: 2
            );
        }

        return null;
    }

    /// <summary>
    /// Queries the CrcV2.RegisterHuman table for standard V2 registrations.
    /// </summary>
    private async Task<InvitationOriginResponse?> QueryV2RegisterHuman(NpgsqlConnection connection, string address)
    {
        const string sql = @"
            SELECT ""blockNumber"", ""timestamp"", ""transactionHash"", ""avatar"", ""inviter""
            FROM ""CrcV2_RegisterHuman""
            WHERE ""avatar"" = @address
            LIMIT 1";

        await using var cmd = new NpgsqlCommand(sql, connection);
        cmd.Parameters.AddWithValue("address", address);

        await using var reader = await cmd.ExecuteReaderAsync();
        if (await reader.ReadAsync())
        {
            var blockNumber = reader.GetInt64(0);
            var timestamp = reader.GetInt64(1);
            var transactionHash = reader.GetString(2);
            var avatar = reader.GetString(3);
            var inviter = reader.IsDBNull(4) ? null : reader.GetString(4);

            // Check if inviter is the zero address (no inviter)
            var zeroAddress = "0x0000000000000000000000000000000000000000";
            var effectiveInviter = inviter == zeroAddress ? null : inviter;

            return new InvitationOriginResponse(
                Address: avatar,
                InvitationType: "v2_standard",
                Inviter: effectiveInviter,
                ProxyInviter: null,
                EscrowAmount: null,
                BlockNumber: blockNumber,
                Timestamp: timestamp,
                TransactionHash: transactionHash,
                Version: 2
            );
        }

        return null;
    }

    /// <summary>
    /// Queries the CrcV1.Signup table for V1 self-signup registrations.
    /// </summary>
    private async Task<InvitationOriginResponse?> QueryV1Signup(NpgsqlConnection connection, string address)
    {
        const string sql = @"
            SELECT ""blockNumber"", ""timestamp"", ""transactionHash"", ""user"", ""token""
            FROM ""CrcV1_Signup""
            WHERE ""user"" = @address
            LIMIT 1";

        await using var cmd = new NpgsqlCommand(sql, connection);
        cmd.Parameters.AddWithValue("address", address);

        await using var reader = await cmd.ExecuteReaderAsync();
        if (await reader.ReadAsync())
        {
            var blockNumber = reader.GetInt64(0);
            var timestamp = reader.GetInt64(1);
            var transactionHash = reader.GetString(2);
            var user = reader.GetString(3);
            // token at index 4 is available but not used in response

            return new InvitationOriginResponse(
                Address: user,
                InvitationType: "v1_signup",
                Inviter: null,  // V1 signups have no inviter
                ProxyInviter: null,
                EscrowAmount: null,
                BlockNumber: blockNumber,
                Timestamp: timestamp,
                TransactionHash: transactionHash,
                Version: 1
            );
        }

        return null;
    }

    /// <summary>
    /// Gets the list of accounts invited by a specific avatar.
    /// When accepted=true: returns accounts that registered using this avatar as inviter.
    /// When accepted=false: returns accounts this avatar trusts that are NOT yet registered (pending).
    /// </summary>
    public async Task<InvitationsFromResponse> GetInvitationsFrom(string address, bool accepted = false)
    {
        var normalizedAddress = address.ToLowerInvariant();
        await using var connection = await CreateConnectionAsync();

        if (accepted)
        {
            // Query RegisterHuman where inviter = this address
            const string sql = @"
                SELECT ""avatar"", ""blockNumber"", ""timestamp""
                FROM ""CrcV2_RegisterHuman""
                WHERE ""inviter"" = @address
                ORDER BY ""blockNumber"" DESC
                LIMIT 200";

            await using var cmd = new NpgsqlCommand(sql, connection);
            cmd.Parameters.AddWithValue("address", normalizedAddress);

            var results = new List<InvitedAccountInfo>();
            await using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                results.Add(new InvitedAccountInfo
                {
                    Address = reader.GetString(0),
                    BlockNumber = reader.GetInt64(1),
                    Timestamp = reader.GetInt64(2),
                    Status = "accepted"
                });
            }

            // Batch fetch avatar info
            if (results.Count > 0)
            {
                var addresses = results.Select(r => r.Address).ToArray();
                var avatarInfos = await GetAvatarInfoBatchInternal(addresses);
                for (int i = 0; i < results.Count; i++)
                {
                    results[i] = results[i] with { AvatarInfo = avatarInfos[i] };
                }
            }

            return new InvitationsFromResponse
            {
                Address = address,
                Accepted = true,
                Results = results.ToArray()
            };
        }
        else
        {
            // Find accounts this avatar trusts (V2) that are NOT registered
            const string sql = @"
                SELECT t.""trustee""
                FROM ""CrcV2_Trust"" t
                WHERE t.""truster"" = @address
                  AND t.""expiryTime"" > EXTRACT(EPOCH FROM NOW())::bigint
                  AND NOT EXISTS (
                      SELECT 1 FROM ""CrcV2_RegisterHuman"" rh
                      WHERE rh.""avatar"" = t.""trustee""
                  )
                  AND NOT EXISTS (
                      SELECT 1 FROM ""CrcV2_RegisterGroup"" rg
                      WHERE rg.""group"" = t.""trustee""
                  )
                  AND NOT EXISTS (
                      SELECT 1 FROM ""CrcV2_RegisterOrganization"" ro
                      WHERE ro.""organization"" = t.""trustee""
                  )
                ORDER BY t.""blockNumber"" DESC
                LIMIT 200";

            await using var cmd = new NpgsqlCommand(sql, connection);
            cmd.Parameters.AddWithValue("address", normalizedAddress);

            var pendingAddresses = new List<string>();
            await using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                pendingAddresses.Add(reader.GetString(0));
            }

            var results = pendingAddresses.Select(addr => new InvitedAccountInfo
            {
                Address = addr,
                Status = "pending"
            }).ToArray();

            return new InvitationsFromResponse
            {
                Address = address,
                Accepted = false,
                Results = results
            };
        }
    }

    /// <summary>
    /// Gets all available invitations for an address from all sources (trust, escrow, at-scale).
    /// Combines multiple invitation mechanisms into a single optimized response.
    /// </summary>
    public async Task<AllInvitationsResponse> GetAllInvitations(string address, string? minimumBalance = null)
    {
        var normalizedAddress = address.ToLowerInvariant();

        // Run all queries in parallel for efficiency
        // Note: Each query must use its own connection because Npgsql doesn't support concurrent operations on one connection
        var trustTask = GetTrustInvitations(normalizedAddress, minimumBalance);
        var escrowTask = GetEscrowInvitations(normalizedAddress);
        var atScaleTask = GetAtScaleInvitations(normalizedAddress);

        await Task.WhenAll(trustTask, escrowTask, atScaleTask);

        return new AllInvitationsResponse
        {
            Address = address,
            TrustInvitations = await trustTask,
            EscrowInvitations = await escrowTask,
            AtScaleInvitations = await atScaleTask
        };
    }

    /// <summary>
    /// Gets trust-based invitations (addresses that trust the invitee and have sufficient balance).
    /// </summary>
    public async Task<TrustInvitation[]> GetTrustInvitations(string address, string? minimumBalance = null)
    {
        // Reuse existing GetValidInviters logic but transform to TrustInvitation format
        var validInviters = await GetValidInviters(address, minimumBalance, 100, null);

        return validInviters.Results.Select(inviter => new TrustInvitation
        {
            Address = inviter.Address,
            Source = "trust",
            Balance = inviter.Balance,
            AvatarInfo = inviter.AvatarInfo
        }).ToArray();
    }

    /// <summary>
    /// Gets escrow-based invitations using optimized SQL with JOINs.
    /// Filters out redeemed, revoked, and refunded escrows in a single query.
    /// </summary>
    public async Task<EscrowInvitation[]> GetEscrowInvitations(string address)
    {
        const string sql = @"
            SELECT e.""inviter"", e.""amount"", e.""blockNumber"", e.""timestamp""
            FROM ""CrcV2_InvitationEscrow_InvitationEscrowed"" e
            WHERE e.""invitee"" = @address
              AND NOT EXISTS (
                  SELECT 1 FROM ""CrcV2_InvitationEscrow_InvitationRedeemed"" r
                  WHERE r.""inviter"" = e.""inviter"" AND r.""invitee"" = e.""invitee""
              )
              AND NOT EXISTS (
                  SELECT 1 FROM ""CrcV2_InvitationEscrow_InvitationRevoked"" v
                  WHERE v.""inviter"" = e.""inviter"" AND v.""invitee"" = e.""invitee""
              )
              AND NOT EXISTS (
                  SELECT 1 FROM ""CrcV2_InvitationEscrow_InvitationRefunded"" f
                  WHERE f.""inviter"" = e.""inviter"" AND f.""invitee"" = e.""invitee""
              )
            ORDER BY e.""blockNumber"" DESC
            LIMIT 100";

        var escrows = new List<(string inviter, decimal amount, long blockNumber, long timestamp)>();

        await using var connection = await CreateConnectionAsync();
        await using var cmd = new NpgsqlCommand(sql, connection);
        cmd.Parameters.AddWithValue("address", address);

        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            escrows.Add((
                reader.GetString(0),
                reader.GetDecimal(1),
                reader.GetInt64(2),
                reader.GetInt64(3)
            ));
        }

        if (escrows.Count == 0)
        {
            return Array.Empty<EscrowInvitation>();
        }

        // Get avatar info for all inviters
        var inviterAddresses = escrows.Select(e => e.inviter).ToArray();
        var avatarInfos = await GetAvatarInfoBatchInternal(inviterAddresses);
        var avatarInfoDict = avatarInfos.ToDictionary(a => a?.Avatar?.ToLowerInvariant() ?? "", a => a);

        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        return escrows.Select(e =>
        {
            var daysSinceEscrow = (int)((now - e.timestamp) / 86400);
            avatarInfoDict.TryGetValue(e.inviter.ToLowerInvariant(), out var avatarInfo);

            return new EscrowInvitation
            {
                Address = e.inviter,
                Source = "escrow",
                EscrowedAmount = e.amount.ToString("F0", System.Globalization.CultureInfo.InvariantCulture),
                EscrowDays = daysSinceEscrow,
                BlockNumber = e.blockNumber,
                Timestamp = e.timestamp,
                AvatarInfo = avatarInfo
            };
        }).ToArray();
    }

    /// <summary>
    /// Gets at-scale invitations (pre-created accounts that haven't been claimed).
    /// </summary>
    public async Task<AtScaleInvitation[]> GetAtScaleInvitations(string address)
    {
        // Check if account was pre-created but not claimed
        const string sql = @"
            SELECT c.""account"", c.""blockNumber"", c.""timestamp""
            FROM ""CrcV2_InvitationsAtScale_AccountCreated"" c
            WHERE c.""account"" = @address
              AND NOT EXISTS (
                  SELECT 1 FROM ""CrcV2_InvitationsAtScale_AccountClaimed"" cl
                  WHERE cl.""account"" = c.""account""
              )
            LIMIT 1";

        await using var connection = await CreateConnectionAsync();
        await using var cmd = new NpgsqlCommand(sql, connection);
        cmd.Parameters.AddWithValue("address", address);

        await using var reader = await cmd.ExecuteReaderAsync();
        if (await reader.ReadAsync())
        {
            var account = reader.GetString(0);
            var blockNumber = reader.GetInt64(1);
            var timestamp = reader.GetInt64(2);

            return new[]
            {
                new AtScaleInvitation
                {
                    Address = account,
                    Source = "atScale",
                    BlockNumber = blockNumber,
                    Timestamp = timestamp,
                    OriginInviter = null // Will be set when account is used for registration
                }
            };
        }

        return Array.Empty<AtScaleInvitation>();
    }
}
