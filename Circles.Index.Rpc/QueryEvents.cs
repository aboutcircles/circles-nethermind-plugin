using System.Collections.Concurrent;
using System.Collections.Immutable;
using Circles.Index.Common;
using Circles.Index.Query;
using Circles.Index.Query.Dto;
using Nethermind.Core;

namespace Circles.Index.Rpc;

public class QueryEvents(Context context)
{
    public static readonly ImmutableHashSet<string> AddressColumns = new HashSet<string>
    {
        "user", "avatar", "organization", "from", "to", "canSendTo", "account", "group", "human", "invited",
        "inviter", "truster", "trustee", "account"
    }.ToImmutableHashSet();

    /// <summary>
    /// Queries all events affecting the specified account since block N.
    /// If 'address' is null, all events are queried.
    /// </summary>
    /// <param name="address">If specified, only events concerning this account are queried</param>
    /// <param name="fromBlock">HEAD - 1000 if not specified otherwise</param>
    /// <param name="toBlock">HEAD if not specified otherwise</param>
    /// <param name="onlyTheseTypes">An array of event types to include in the query</param>
    /// <param name="additionalFilters">Additional filters to apply to the query. The filtered columns must be present in all queried events!</param>
    /// <returns>An array of CirclesEvent objects</returns>
    /// <exception cref="Exception">Thrown when the zero address is queried, fromBlock is less than 0, toBlock is less than fromBlock, or toBlock is greater than the current head</exception>
    public CirclesEvent[] CirclesEvents(
        Address? address
        , long? fromBlock
        , long? toBlock = null
        , string[]? onlyTheseTypes = null
        , FilterPredicateDto[]? additionalFilters = null
        , bool? sortAscending = false)
    {
        long currentHead = context.NethermindApi.BlockTree?.Head?.Number
                           ?? throw new Exception("BlockTree or Head is null");

        fromBlock ??= currentHead - 1000;

        string? addressString = address?.ToString(true, false);

        ValidateInputs(addressString, fromBlock.Value, toBlock, currentHead);

        var queries = BuildQueries(addressString, fromBlock.Value, toBlock, onlyTheseTypes, additionalFilters);

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

    private List<Select> BuildQueries(string? address, long fromBlock, long? toBlock,
        string[]? onlyTheseTypes = null, FilterPredicateDto[]? additionalFilters = null)
    {
        var queries = new List<Select>();

        foreach (var table in context.Database.Schema.Tables)
        {
            if (table.Key.Namespace.StartsWith("V_") || table.Key.Namespace == "System")
            {
                continue;
            }

            if (onlyTheseTypes != null && !onlyTheseTypes.Contains($"{table.Key.Namespace}_{table.Key.Table}"))
            {
                continue;
            }

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

            if (additionalFilters != null)
            {
                IFilterPredicate ToFilterPredicate(IFilterPredicateDto dto)
                {
                    if (dto.Type == "FilterPredicate")
                    {
                        var predicate = (FilterPredicateDto)dto;
                        return new FilterPredicate(predicate.Column ?? throw new Exception("Column is null"),
                            predicate.FilterType, predicate.Value);
                    }

                    if (dto.Type == "Conjunction")
                    {
                        var conjunction = (ConjunctionDto)dto;
                        var predicates = conjunction.Predicates?.Select(ToFilterPredicate).ToArray();
                        return new Conjunction(conjunction.ConjunctionType, predicates ?? []);
                    }

                    throw new ArgumentException($"Unknown filter predicate type: {dto.Type}");
                }

                filters.AddRange(additionalFilters.Select(ToFilterPredicate));
            }

            var query = new Select(table.Key.Namespace, table.Key.Table, Array.Empty<string>(),
                filters.Count > 1
                    ? new[] { new Conjunction(ConjunctionType.And, filters.ToArray()) }
                    : filters,
                [
                    new OrderBy("blockNumber", "ASC"),
                    new OrderBy("transactionIndex", "ASC"),
                    new OrderBy("logIndex", "ASC")
                ],
                null, true, int.MaxValue);

            queries.Add(query);
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
        ConcurrentDictionary<(long BlockNo, long TransactionIndex, long LogIndex), CirclesEvent> events,
        bool? sortAscending = false)
    {
        if (sortAscending == null || sortAscending == false)
        {
            return events
                .OrderByDescending(o => o.Key.BlockNo)
                .ThenByDescending(o => o.Key.TransactionIndex)
                .ThenByDescending(o => o.Key.LogIndex)
                .Select(o => o.Value)
                .ToArray();
        }

        return events
            .OrderBy(o => o.Key.BlockNo)
            .ThenBy(o => o.Key.TransactionIndex)
            .ThenBy(o => o.Key.LogIndex)
            .Select(o => o.Value)
            .ToArray();
    }
}