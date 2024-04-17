using System.Data;

namespace Circles.Index.Data.Query;

public interface IQuery
{
    string ToSql();
    IEnumerable<IDataParameter> GetParameters();
}