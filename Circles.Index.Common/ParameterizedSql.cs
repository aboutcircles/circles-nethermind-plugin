using System.Data;

namespace Circles.Index.Common;

public record ParameterizedSql(string Sql, IEnumerable<IDbDataParameter> Parameters);