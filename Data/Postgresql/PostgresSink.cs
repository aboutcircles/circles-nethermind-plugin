using System.Numerics;
using Npgsql;
using NpgsqlTypes;

namespace Circles.Index.Data.Postgresql;

public class PostgresSink(string connectionString) : BufferSink, IAsyncDisposable
{
    public override async Task Flush()
    {
        try
        {
            var tasks = new List<Task>
            {
                FlushBlocksBulk(),
                FlushCirclesSignupsBulk(),
                FlushCirclesTrustsBulk(),
                FlushCirclesHubTransfersBulk(),
                FlushCrcV2RegisterOrganizationBulk(),
                FlushCrcV2RegisterGroupBulk(),
                FlushCrcV2RegisterHumanBulk(),
                FlushCrcV2PersonalMintBulk(),
                FlushCrcV2InviteHumanBulk(),
                FlushCrcV2ConvertInflationBulk(),
                FlushCrcV2TrustBulk(),
                FlushCrcV2StoppedBulk(),
                FlushErc20TransfersBulk(),
                FlushErc1155TransferSingleBulk(),
                FlushErc1155TransferBatchBulk(),
                FlushErc1155ApprovalForAllBulk(),
                FlushErc1155UriBulk()
            };

            await Task.WhenAll(tasks);
        }
        catch (Exception e)
        {
            Console.WriteLine(e);
        }
    }

    private async Task FlushErc1155UriBulk()
    {
        await using NpgsqlConnection flushConnection = new(connectionString);
        await flushConnection.OpenAsync();

        await using var writer = await flushConnection.BeginBinaryImportAsync(
            $"COPY {TableNames.Erc1155Uri} (block_number, timestamp, transaction_index, log_index, transaction_hash, token_id, uri) FROM STDIN (FORMAT BINARY)");
        foreach (var item in Erc1155Uri.TakeSnapshot())
        {
            await writer.StartRowAsync();
            await writer.WriteAsync(item.BlockNumber, NpgsqlDbType.Bigint);
            await writer.WriteAsync(item.Timestamp, NpgsqlDbType.Bigint);
            await writer.WriteAsync(item.TransactionIndex, NpgsqlDbType.Bigint);
            await writer.WriteAsync(item.LogIndex, NpgsqlDbType.Bigint);
            await writer.WriteAsync(item.TransactionHash, NpgsqlDbType.Text);
            await writer.WriteAsync(item.TokenId, NpgsqlDbType.Bigint);
            await writer.WriteAsync(item.Uri, NpgsqlDbType.Text);
        }

        await writer.CompleteAsync();
    }

    private async Task FlushErc1155ApprovalForAllBulk()
    {
        await using NpgsqlConnection flushConnection = new(connectionString);
        await flushConnection.OpenAsync();

        await using var writer = await flushConnection.BeginBinaryImportAsync(
            $"COPY {TableNames.Erc1155ApprovalForAll} (block_number, timestamp, transaction_index, log_index, transaction_hash, owner_address, operator_address, approved) FROM STDIN (FORMAT BINARY)");
        foreach (var item in Erc1155ApprovalForAll.TakeSnapshot())
        {
            await writer.StartRowAsync();
            await writer.WriteAsync(item.BlockNumber, NpgsqlDbType.Bigint);
            await writer.WriteAsync(item.Timestamp, NpgsqlDbType.Bigint);
            await writer.WriteAsync(item.TransactionIndex, NpgsqlDbType.Bigint);
            await writer.WriteAsync(item.LogIndex, NpgsqlDbType.Bigint);
            await writer.WriteAsync(item.TransactionHash, NpgsqlDbType.Text);
            await writer.WriteAsync(item.OwnerAddress, NpgsqlDbType.Text);
            await writer.WriteAsync(item.OperatorAddress, NpgsqlDbType.Text);
            await writer.WriteAsync(item.Approved, NpgsqlDbType.Boolean);
        }

        await writer.CompleteAsync();
    }

    private async Task FlushErc1155TransferBatchBulk()
    {
        await using NpgsqlConnection flushConnection = new(connectionString);
        await flushConnection.OpenAsync();

        await using var writer = await flushConnection.BeginBinaryImportAsync(
            $"COPY {TableNames.Erc1155TransferBatch} (block_number, timestamp, transaction_index, log_index, batch_index, transaction_hash, operator_address, from_address, to_address, token_id, amount) FROM STDIN (FORMAT BINARY)");
        foreach (var item in Erc1155TransferBatch.TakeSnapshot())
        {
            await writer.StartRowAsync();
            await writer.WriteAsync(item.BlockNumber, NpgsqlDbType.Bigint);
            await writer.WriteAsync(item.Timestamp, NpgsqlDbType.Bigint);
            await writer.WriteAsync(item.TransactionIndex, NpgsqlDbType.Bigint);
            await writer.WriteAsync(item.LogIndex, NpgsqlDbType.Bigint);
            await writer.WriteAsync(item.BatchIndex, NpgsqlDbType.Integer);
            await writer.WriteAsync(item.TransactionHash, NpgsqlDbType.Text);
            await writer.WriteAsync(item.OperatorAddress, NpgsqlDbType.Text);
            await writer.WriteAsync(item.FromAddress, NpgsqlDbType.Text);
            await writer.WriteAsync(item.ToAddress, NpgsqlDbType.Text);
            await writer.WriteAsync((BigInteger)item.TokenId, NpgsqlDbType.Numeric);
            await writer.WriteAsync((BigInteger)item.Amount, NpgsqlDbType.Numeric);
        }

        await writer.CompleteAsync();
    }

    private async Task FlushErc1155TransferSingleBulk()
    {
        await using NpgsqlConnection flushConnection = new(connectionString);
        await flushConnection.OpenAsync();

        await using var writer = await flushConnection.BeginBinaryImportAsync(
            $"COPY {TableNames.Erc1155TransferSingle} (block_number, timestamp, transaction_index, log_index, transaction_hash, operator_address, from_address, to_address, token_id, amount) FROM STDIN (FORMAT BINARY)");
        foreach (var item in Erc1155TransferSingle.TakeSnapshot())
        {
            await writer.StartRowAsync();
            await writer.WriteAsync(item.BlockNumber, NpgsqlDbType.Bigint);
            await writer.WriteAsync(item.Timestamp, NpgsqlDbType.Bigint);
            await writer.WriteAsync(item.TransactionIndex, NpgsqlDbType.Bigint);
            await writer.WriteAsync(item.LogIndex, NpgsqlDbType.Bigint);
            await writer.WriteAsync(item.TransactionHash, NpgsqlDbType.Text);
            await writer.WriteAsync(item.OperatorAddress, NpgsqlDbType.Text);
            await writer.WriteAsync(item.FromAddress, NpgsqlDbType.Text);
            await writer.WriteAsync(item.ToAddress, NpgsqlDbType.Text);
            await writer.WriteAsync((BigInteger)item.TokenId, NpgsqlDbType.Numeric);
            await writer.WriteAsync((BigInteger)item.Amount, NpgsqlDbType.Numeric);
        }

        await writer.CompleteAsync();
    }

    private async Task FlushCrcV2StoppedBulk()
    {
        await using NpgsqlConnection flushConnection = new(connectionString);
        await flushConnection.OpenAsync();

        await using var writer = await flushConnection.BeginBinaryImportAsync(
            $"COPY {TableNames.CrcV2Stopped} (block_number, timestamp, transaction_index, log_index, transaction_hash, address) FROM STDIN (FORMAT BINARY)");
        foreach (var item in CrcV2Stopped.TakeSnapshot())
        {
            await writer.StartRowAsync();
            await writer.WriteAsync(item.BlockNumber, NpgsqlDbType.Bigint);
            await writer.WriteAsync(item.Timestamp, NpgsqlDbType.Bigint);
            await writer.WriteAsync(item.TransactionIndex, NpgsqlDbType.Bigint);
            await writer.WriteAsync(item.LogIndex, NpgsqlDbType.Bigint);
            await writer.WriteAsync(item.TransactionHash, NpgsqlDbType.Text);
            await writer.WriteAsync(item.Address, NpgsqlDbType.Text);
        }

        await writer.CompleteAsync();
    }

    private async Task FlushCrcV2TrustBulk()
    {
        await using NpgsqlConnection flushConnection = new(connectionString);
        await flushConnection.OpenAsync();

        await using var writer = await flushConnection.BeginBinaryImportAsync(
            $"COPY {TableNames.CrcV2Trust} (block_number, timestamp, transaction_index, log_index, transaction_hash, truster_address, trustee_address, expiry_time) FROM STDIN (FORMAT BINARY)");
        foreach (var item in CrcV2Trust.TakeSnapshot())
        {
            await writer.StartRowAsync();
            await writer.WriteAsync(item.BlockNumber, NpgsqlDbType.Bigint);
            await writer.WriteAsync(item.Timestamp, NpgsqlDbType.Bigint);
            await writer.WriteAsync(item.TransactionIndex, NpgsqlDbType.Bigint);
            await writer.WriteAsync(item.LogIndex, NpgsqlDbType.Bigint);
            await writer.WriteAsync(item.TransactionHash, NpgsqlDbType.Text);
            await writer.WriteAsync(item.TrusterAddress, NpgsqlDbType.Text);
            await writer.WriteAsync(item.TrusteeAddress, NpgsqlDbType.Text);
            await writer.WriteAsync((BigInteger)item.ExpiryTime, NpgsqlDbType.Numeric);
        }

        await writer.CompleteAsync();
    }

    private async Task FlushCrcV2ConvertInflationBulk()
    {
        await using NpgsqlConnection flushConnection = new(connectionString);
        await flushConnection.OpenAsync();

        await using var writer = await flushConnection.BeginBinaryImportAsync(
            $"COPY {TableNames.CrcV2ConvertInflation} (block_number, timestamp, transaction_index, log_index, transaction_hash, inflation_value, demurrage_value, \"day\") FROM STDIN (FORMAT BINARY)");
        foreach (var item in CrcV2ConvertInflation.TakeSnapshot())
        {
            await writer.StartRowAsync();
            await writer.WriteAsync(item.BlockNumber, NpgsqlDbType.Bigint);
            await writer.WriteAsync(item.Timestamp, NpgsqlDbType.Bigint);
            await writer.WriteAsync(item.TransactionIndex, NpgsqlDbType.Bigint);
            await writer.WriteAsync(item.LogIndex, NpgsqlDbType.Bigint);
            await writer.WriteAsync(item.TransactionHash, NpgsqlDbType.Text);
            await writer.WriteAsync((BigInteger)item.InflationValue, NpgsqlDbType.Numeric);
            await writer.WriteAsync((BigInteger)item.DemurrageValue, NpgsqlDbType.Numeric);
            await writer.WriteAsync(item.Day, NpgsqlDbType.Numeric);
        }

        await writer.CompleteAsync();
    }

    private async Task FlushCrcV2InviteHumanBulk()
    {
        await using NpgsqlConnection flushConnection = new(connectionString);
        await flushConnection.OpenAsync();

        await using var writer = await flushConnection.BeginBinaryImportAsync(
            $"COPY {TableNames.CrcV2InviteHuman} (block_number, timestamp, transaction_index, log_index, transaction_hash, inviter_address, invitee_address) FROM STDIN (FORMAT BINARY)");
        foreach (var item in CrcV2InviteHuman.TakeSnapshot())
        {
            await writer.StartRowAsync();
            await writer.WriteAsync(item.BlockNumber, NpgsqlDbType.Bigint);
            await writer.WriteAsync(item.Timestamp, NpgsqlDbType.Bigint);
            await writer.WriteAsync(item.TransactionIndex, NpgsqlDbType.Bigint);
            await writer.WriteAsync(item.LogIndex, NpgsqlDbType.Bigint);
            await writer.WriteAsync(item.TransactionHash, NpgsqlDbType.Text);
            await writer.WriteAsync(item.InviterAddress, NpgsqlDbType.Text);
            await writer.WriteAsync(item.InviteeAddress, NpgsqlDbType.Text);
        }

        await writer.CompleteAsync();
    }

    private async Task FlushCrcV2PersonalMintBulk()
    {
        await using NpgsqlConnection flushConnection = new(connectionString);
        await flushConnection.OpenAsync();

        await using var writer = await flushConnection.BeginBinaryImportAsync(
            $"COPY {TableNames.CrcV2PersonalMint} (block_number, timestamp, transaction_index, log_index, transaction_hash, to_address, amount, start_period, end_period) FROM STDIN (FORMAT BINARY)");
        foreach (var item in CrcV2PersonalMint.TakeSnapshot())
        {
            await writer.StartRowAsync();
            await writer.WriteAsync(item.BlockNumber, NpgsqlDbType.Bigint);
            await writer.WriteAsync(item.Timestamp, NpgsqlDbType.Bigint);
            await writer.WriteAsync(item.TransactionIndex, NpgsqlDbType.Bigint);
            await writer.WriteAsync(item.LogIndex, NpgsqlDbType.Bigint);
            await writer.WriteAsync(item.TransactionHash, NpgsqlDbType.Text);
            await writer.WriteAsync(item.ToAddress, NpgsqlDbType.Text);
            await writer.WriteAsync((BigInteger)item.Amount, NpgsqlDbType.Numeric);
            await writer.WriteAsync((BigInteger)item.StartPeriod, NpgsqlDbType.Numeric);
            await writer.WriteAsync((BigInteger)item.EndPeriod, NpgsqlDbType.Numeric);
        }

        await writer.CompleteAsync();
    }

    private async Task FlushCrcV2RegisterHumanBulk()
    {
        await using NpgsqlConnection flushConnection = new(connectionString);
        await flushConnection.OpenAsync();

        await using var writer = await flushConnection.BeginBinaryImportAsync(
            $"COPY {TableNames.CrcV2RegisterHuman} (block_number, timestamp, transaction_index, log_index, transaction_hash, address) FROM STDIN (FORMAT BINARY)");
        foreach (var item in CrcV2RegisterHuman.TakeSnapshot())
        {
            await writer.StartRowAsync();
            await writer.WriteAsync(item.BlockNumber, NpgsqlDbType.Bigint);
            await writer.WriteAsync(item.Timestamp, NpgsqlDbType.Bigint);
            await writer.WriteAsync(item.TransactionIndex, NpgsqlDbType.Bigint);
            await writer.WriteAsync(item.LogIndex, NpgsqlDbType.Bigint);
            await writer.WriteAsync(item.TransactionHash, NpgsqlDbType.Text);
            await writer.WriteAsync(item.Address, NpgsqlDbType.Text);
        }

        await writer.CompleteAsync();
    }

    private async Task FlushCrcV2RegisterGroupBulk()
    {
        await using NpgsqlConnection flushConnection = new(connectionString);
        await flushConnection.OpenAsync();

        await using var writer = await flushConnection.BeginBinaryImportAsync(
            $"COPY {TableNames.CrcV2RegisterGroup} (block_number, timestamp, transaction_index, log_index, transaction_hash, group_address, group_mint_policy, group_treasury, group_name, group_symbol) FROM STDIN (FORMAT BINARY)");
        foreach (var item in CrcV2RegisterGroup.TakeSnapshot())
        {
            await writer.StartRowAsync();
            await writer.WriteAsync(item.BlockNumber, NpgsqlDbType.Bigint);
            await writer.WriteAsync(item.Timestamp, NpgsqlDbType.Bigint);
            await writer.WriteAsync(item.TransactionIndex, NpgsqlDbType.Bigint);
            await writer.WriteAsync(item.LogIndex, NpgsqlDbType.Bigint);
            await writer.WriteAsync(item.TransactionHash, NpgsqlDbType.Text);
            await writer.WriteAsync(item.GroupAddress, NpgsqlDbType.Text);
            await writer.WriteAsync(item.GroupMintPolicy, NpgsqlDbType.Text);
            await writer.WriteAsync(item.GroupTreasury, NpgsqlDbType.Text);
            await writer.WriteAsync(item.GroupName, NpgsqlDbType.Text);
            await writer.WriteAsync(item.GroupSymbol, NpgsqlDbType.Text);
        }

        await writer.CompleteAsync();
    }

    private async Task FlushCrcV2RegisterOrganizationBulk()
    {
        await using NpgsqlConnection flushConnection = new(connectionString);
        await flushConnection.OpenAsync();

        await using var writer = await flushConnection.BeginBinaryImportAsync(
            $"COPY {TableNames.CrcV2RegisterOrganization} (block_number, timestamp, transaction_index, log_index, transaction_hash, organization_address, organization_name) FROM STDIN (FORMAT BINARY)");
        foreach (var item in CrcV2RegisterOrganization.TakeSnapshot())
        {
            await writer.StartRowAsync();
            await writer.WriteAsync(item.BlockNumber, NpgsqlDbType.Bigint);
            await writer.WriteAsync(item.Timestamp, NpgsqlDbType.Bigint);
            await writer.WriteAsync(item.TransactionIndex, NpgsqlDbType.Bigint);
            await writer.WriteAsync(item.LogIndex, NpgsqlDbType.Bigint);
            await writer.WriteAsync(item.TransactionHash, NpgsqlDbType.Text);
            await writer.WriteAsync(item.OrganizationAddress, NpgsqlDbType.Text);
            await writer.WriteAsync(item.OrganizationName, NpgsqlDbType.Text);
        }

        await writer.CompleteAsync();
    }

    private async Task FlushBlocksBulk()
    {
        await using var flushConnection = new NpgsqlConnection(connectionString);
        flushConnection.Open();

        await using var writer =
            await flushConnection.BeginBinaryImportAsync(
                $"COPY {TableNames.Block} (block_number, timestamp, block_hash) FROM STDIN (FORMAT BINARY)");
        foreach (var item in Blocks.TakeSnapshot())
        {
            await writer.StartRowAsync();
            await writer.WriteAsync(item.BlockNumber, NpgsqlDbType.Bigint);
            await writer.WriteAsync(item.Timestamp, NpgsqlDbType.Bigint);
            await writer.WriteAsync(item.BlockHash, NpgsqlDbType.Text);
        }

        await writer.CompleteAsync();
    }

    private async Task FlushCirclesSignupsBulk()
    {
        await using NpgsqlConnection flushConnection = new(connectionString);
        await flushConnection.OpenAsync();

        await using var writer = await flushConnection.BeginBinaryImportAsync(
            $"COPY {TableNames.CrcV1Signup} (block_number, timestamp, transaction_index, log_index, transaction_hash, circles_address, token_address) FROM STDIN (FORMAT BINARY)");
        foreach (var item in CirclesSignup.TakeSnapshot())
        {
            await writer.StartRowAsync();
            await writer.WriteAsync(item.BlockNumber, NpgsqlDbType.Bigint);
            await writer.WriteAsync(item.Timestamp, NpgsqlDbType.Bigint);
            await writer.WriteAsync(item.TransactionIndex, NpgsqlDbType.Bigint);
            await writer.WriteAsync(item.LogIndex, NpgsqlDbType.Bigint);
            await writer.WriteAsync(item.TransactionHash, NpgsqlDbType.Text);
            await writer.WriteAsync(item.CirclesAddress, NpgsqlDbType.Text);
            await writer.WriteAsync(item.TokenAddress ?? string.Empty, NpgsqlDbType.Text);
        }

        await writer.CompleteAsync();
    }

    private async Task FlushCirclesTrustsBulk()
    {
        await using NpgsqlConnection flushConnection = new(connectionString);
        await flushConnection.OpenAsync();

        await using var writer = await flushConnection.BeginBinaryImportAsync(
            $"COPY {TableNames.CrcV1Trust} (block_number, timestamp, transaction_index, log_index, transaction_hash, user_address, can_send_to_address, \"limit\") FROM STDIN (FORMAT BINARY)");

        foreach (var item in CirclesTrust.TakeSnapshot())
        {
            await writer.StartRowAsync();
            await writer.WriteAsync(item.BlockNumber, NpgsqlDbType.Bigint);
            await writer.WriteAsync(item.Timestamp, NpgsqlDbType.Bigint);
            await writer.WriteAsync(item.TransactionIndex, NpgsqlDbType.Bigint);
            await writer.WriteAsync(item.LogIndex, NpgsqlDbType.Bigint);
            await writer.WriteAsync(item.TransactionHash, NpgsqlDbType.Text);
            await writer.WriteAsync(item.UserAddress, NpgsqlDbType.Text);
            await writer.WriteAsync(item.CanSendToAddress, NpgsqlDbType.Text);
            await writer.WriteAsync(item.Limit, NpgsqlDbType.Bigint);
        }

        await writer.CompleteAsync();
    }

    private async Task FlushCirclesHubTransfersBulk()
    {
        await using NpgsqlConnection flushConnection = new(connectionString);
        await flushConnection.OpenAsync();

        await using var writer = await flushConnection.BeginBinaryImportAsync(
            $"COPY {TableNames.CrcV1HubTransfer} (block_number, timestamp, transaction_index, log_index, transaction_hash, from_address, to_address, amount) FROM STDIN (FORMAT BINARY)");
        foreach (var item in CirclesHubTransfer.TakeSnapshot())
        {
            await writer.StartRowAsync();
            await writer.WriteAsync(item.BlockNumber, NpgsqlDbType.Bigint);
            await writer.WriteAsync(item.Timestamp, NpgsqlDbType.Bigint);
            await writer.WriteAsync(item.TransactionIndex, NpgsqlDbType.Bigint);
            await writer.WriteAsync(item.LogIndex, NpgsqlDbType.Bigint);
            await writer.WriteAsync(item.TransactionHash, NpgsqlDbType.Text);
            await writer.WriteAsync(item.FromAddress, NpgsqlDbType.Text);
            await writer.WriteAsync(item.ToAddress, NpgsqlDbType.Text);
            await writer.WriteAsync((BigInteger)item.Amount, NpgsqlDbType.Numeric);
        }

        await writer.CompleteAsync();
    }

    private async Task FlushErc20TransfersBulk()
    {
        await using NpgsqlConnection flushConnection = new(connectionString);
        await flushConnection.OpenAsync();

        await using var writer = await flushConnection.BeginBinaryImportAsync(
            $"COPY {TableNames.Erc20Transfer} (block_number, timestamp, transaction_index, log_index, transaction_hash, token_address, from_address, to_address, amount) FROM STDIN (FORMAT BINARY)");
        foreach (var item in Erc20Transfer.TakeSnapshot())
        {
            await writer.StartRowAsync();
            await writer.WriteAsync(item.BlockNumber, NpgsqlDbType.Bigint);
            await writer.WriteAsync(item.Timestamp, NpgsqlDbType.Bigint);
            await writer.WriteAsync(item.TransactionIndex, NpgsqlDbType.Bigint);
            await writer.WriteAsync(item.LogIndex, NpgsqlDbType.Bigint);
            await writer.WriteAsync(item.TransactionHash, NpgsqlDbType.Text);
            await writer.WriteAsync(item.TokenAddress, NpgsqlDbType.Text);
            await writer.WriteAsync(item.FromAddress, NpgsqlDbType.Text);
            await writer.WriteAsync(item.ToAddress, NpgsqlDbType.Text);
            await writer.WriteAsync((BigInteger)item.Amount, NpgsqlDbType.Numeric);
        }

        await writer.CompleteAsync();
    }

    public async ValueTask DisposeAsync()
    {
        await Flush();
    }
}