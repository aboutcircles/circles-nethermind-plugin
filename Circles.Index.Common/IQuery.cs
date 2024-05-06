using System.Data;

namespace Circles.Index.Common;

public interface IQuery
{
    string ToSql();
    IEnumerable<IDataParameter> GetParameters(IDatabaseSchema schema);
}