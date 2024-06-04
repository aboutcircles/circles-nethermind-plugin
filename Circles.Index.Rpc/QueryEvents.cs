using System.Collections.Concurrent;
using System.Collections.Immutable;
using Circles.Index.Common;
using Circles.Index.Query;
using Nethermind.Core;

namespace Circles.Index.Rpc;

public class QueryEvents(Context context)
{
    public static readonly ImmutableHashSet<string> AddressColumns = new HashSet<string>
    {
        "user", "avatar", "organization", "from", "to", "canSendTo", "account", "group", "human", "invited",
        "inviter", "truster", "trustee"
    }.ToImmutableHashSet();

    public CirclesEvent[] CirclesEvents(Address? address, long fromBlock, long? toBlock = null)
    {
        long currentHead = context.NethermindApi.BlockTree?.Head?.Number
                           ?? throw new Exception("BlockTree or Head is null");

        string? addressString = address?.ToString(true, false);

        ValidateInputs(addressString, fromBlock, toBlock, currentHead);

        var queries = BuildQueries(addressString, fromBlock, toBlock);

        var events = ExecuteQueries(queries);

        var sortedEvents = SortEvents(events);

        return sortedEvents;
    }

    private void ValidateInputs(string? address, long fromBlock, long? toBlock, long currentHead)
    {
        if (address == "0x0000000000000000000000000000000000000000")
            throw new Exception("The zero address cannot be queried.");

        if (fromBlock < 0)
            throw new Exception("The fromBlock parameter must be greater than or equal to 0.");

        if (toBlock.HasValue && toBlock.Value < fromBlock)
            throw new Exception("The toBlock parameter must be greater than or equal to fromBlock.");

        if (toBlock.HasValue && toBlock.Value > currentHead)
            throw new Exception(
                "The toBlock parameter must be less than or equal to the current head. Leave it empty to query all blocks until the current head.");
    }

    private List<Select> BuildQueries(string? address, long fromBlock, long? toBlock)
    {
        var queries = new List<Select>();

        foreach (var table in context.Database.Schema.Tables)
        {
            if (table.Key.Namespace.StartsWith("V_") || table.Key.Namespace == "System")
                continue;

            var addressColumnFilters = address == null
                ? []
                : table.Value.Columns
                    .Where(column => AddressColumns.Contains(column.Column))
                    .Select(column => new FilterPredicate(column.Column, FilterType.Equals, address))
                    .Cast<IFilterPredicate>()
                    .ToList();

            var filters = new List<IFilterPredicate>
            {
                new FilterPredicate("blockNumber", FilterType.GreaterThanOrEquals, fromBlock),
            };

            if (addressColumnFilters.Count > 0)
            {
                filters.Add(addressColumnFilters.Count == 1
                    ? addressColumnFilters[0]
                    : new Conjunction(ConjunctionType.Or, addressColumnFilters.ToArray()));
            }

            if (toBlock.HasValue)
            {
                filters.Add(new FilterPredicate("blockNumber", FilterType.LessThanOrEquals, toBlock.Value));
            }

            queries.Add(new Select(table.Key.Namespace, table.Key.Table, Array.Empty<string>(),
                filters.Count > 1
                    ? new[] { new Conjunction(ConjunctionType.And, filters.ToArray()) }
                    : filters,
                new[]
                {
                    new OrderBy("blockNumber", "ASC"), new OrderBy("transactionIndex", "ASC"),
                    new OrderBy("logIndex", "ASC")
                },
                null, true, int.MaxValue));
        }

        return queries;
    }

    private ConcurrentDictionary<(long BlockNo, long TransactionIndex, long LogIndex), CirclesEvent> ExecuteQueries(
        List<Select> queries)
    {
        var events = new ConcurrentDictionary<(long BlockNo, long TransactionIndex, long LogIndex), CirclesEvent>();
        var tasks = queries.Select(query => Task.Run(() =>
        {
            var sql = query.ToSql(context.Database);
            var result = context.Database.Select(sql);

            foreach (var row in result.Rows)
            {
                var eventName = $"{query.Namespace}_{query.Table}";
                var values = result.Columns.Select((col, i) => new { col, value = row[i] })
                    .ToDictionary(x => x.col, x => x.value);

                var key = ((long)(row[0] ?? new Exception("Block number is null")),
                    (long)(row[2] ?? throw new Exception("Transaction index is null")),
                    (long)(row[3] ?? throw new Exception("Log index is null")));

                events.TryAdd(key, new CirclesEvent(eventName, values));
            }
        })).ToArray();

        Task.WaitAll(tasks);

        return events;
    }

    private CirclesEvent[] SortEvents(
        ConcurrentDictionary<(long BlockNo, long TransactionIndex, long LogIndex), CirclesEvent> events)
    {
        return events
            .OrderBy(o => o.Key.BlockNo)
            .ThenBy(o => o.Key.TransactionIndex)
            .ThenBy(o => o.Key.LogIndex)
            .Select(o => o.Value)
            .ToArray();
    }
}