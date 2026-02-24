using System.Data;

namespace Circles.Common;

public record ParameterizedSql(string Sql, IEnumerable<IDbDataParameter> Parameters);
