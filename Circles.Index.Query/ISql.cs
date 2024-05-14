using Circles.Index.Common;

namespace Circles.Index.Query;

public interface ISql
{
    ParameterizedSql ToSql(IDatabaseUtils database);
}