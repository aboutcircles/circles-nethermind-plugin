// using System.Data;
//
// namespace Circles.Index.Common;
//
// public class LogicalOr : IQuery
// {
//     public readonly IQuery[] Elements;
//
//     internal LogicalOr(params IQuery[] elements)
//     {
//         Elements = elements;
//     }
//
//     public string ToSql(IDatabaseSchema schema) => $"({string.Join(" OR ", Elements.Select(e => e.ToSql(schema)))})";
//
//     public IEnumerable<IDataParameter> GetParameters(IDatabaseSchema schema)
//     {
//         foreach (var element in Elements)
//         {
//             foreach (var param in element.GetParameters(schema))
//             {
//                 yield return param;
//             }
//         }
//     }
// }