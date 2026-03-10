namespace Circles.Index.CirclesViews.Tests;

/// <summary>
/// Regression tests ensuring performance-critical indexes are registered in DatabaseSchema.
/// Each test guards against accidental removal of an index that was added to fix a
/// specific query performance issue identified during load testing.
/// </summary>
[TestFixture, Parallelizable]
public class DatabaseSchemaIndexTests
{
    private DatabaseSchema _schema = null!;

    [OneTimeSetUp]
    public void Setup()
    {
        _schema = new DatabaseSchema();
    }

    [Test]
    public void Indexes_ContainsErc20WrapperTransferFromIndex()
    {
        Assert.That(_schema.Indexes.ContainsKey("idx_CrcV2_Erc20WrapperTransfer_from_tokenAddress"),
            "Missing index on CrcV2_Erc20WrapperTransfer(from, tokenAddress) — " +
            "required for getTokenBalances OR clause performance");
    }

    [Test]
    public void Indexes_ContainsTransferSingleFromIndex()
    {
        Assert.That(_schema.Indexes.ContainsKey("idx_CrcV2_TransferSingle_from_tokenAddress"),
            "Missing index on CrcV2_TransferSingle(from, tokenAddress) — " +
            "required for BalancesByAccountAndToken view performance");
    }

    [Test]
    public void Indexes_ContainsTransferBatchFromIndex()
    {
        Assert.That(_schema.Indexes.ContainsKey("idx_CrcV2_TransferBatch_from_tokenAddress"),
            "Missing index on CrcV2_TransferBatch(from, tokenAddress) — " +
            "required for BalancesByAccountAndToken view performance");
    }

    [Test]
    public void Indexes_ContainsCreateVaultGroupIndex()
    {
        Assert.That(_schema.Indexes.ContainsKey("idx_CrcV2_CreateVault_group"),
            "Missing index on CrcV2_CreateVault(group) — " +
            "required for circles_query CreateVault lookups");
    }

    /// <summary>
    /// Ensure each new index targets the correct table and columns.
    /// Guards against copy-paste errors where the index name is correct but the SQL is wrong.
    /// </summary>
    [TestCase("idx_CrcV2_Erc20WrapperTransfer_from_tokenAddress", "CrcV2_Erc20WrapperTransfer", "\"from\", \"tokenAddress\"")]
    [TestCase("idx_CrcV2_TransferSingle_from_tokenAddress", "CrcV2_TransferSingle", "\"from\", \"tokenAddress\"")]
    [TestCase("idx_CrcV2_TransferBatch_from_tokenAddress", "CrcV2_TransferBatch", "\"from\", \"tokenAddress\"")]
    [TestCase("idx_CrcV2_CreateVault_group", "CrcV2_CreateVault", "\"group\"")]
    public void Indexes_SqlTargetsCorrectTableAndColumns(string indexName, string tableName, string columns)
    {
        var sql = _schema.Indexes[indexName];
        Assert.That(sql, Does.Contain(tableName),
            $"Index {indexName} SQL does not reference expected table {tableName}");
        Assert.That(sql, Does.Contain(columns),
            $"Index {indexName} SQL does not contain expected columns {columns}");
    }

    /// <summary>
    /// Ensure complementary 'to' indexes still exist alongside the new 'from' indexes.
    /// These are the original indexes — removing them would break the 'to' side of queries.
    /// </summary>
    [Test]
    public void Indexes_ComplementaryToIndexesStillExist()
    {
        Assert.That(_schema.Indexes.ContainsKey("idx_CrcV2_Erc20WrapperTransfer_to_tokenAddress"),
            "Original 'to' index for Erc20WrapperTransfer was removed");
        Assert.That(_schema.Indexes.ContainsKey("idx_CrcV2_TransferSingle_to_tokenAddress"),
            "Original 'to' index for TransferSingle was removed");
        Assert.That(_schema.Indexes.ContainsKey("idx_CrcV2_TransferBatch_to_tokenAddress"),
            "Original 'to' index for TransferBatch was removed");
    }
}
