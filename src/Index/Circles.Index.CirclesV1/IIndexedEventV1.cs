using System.Text.Json.Serialization;
using Circles.Common;

namespace Circles.Index.CirclesV1;

[JsonPolymorphic(TypeDiscriminatorPropertyName = "$type")]
[JsonDerivedType(typeof(Signup), "CrcV1_Signup")]
[JsonDerivedType(typeof(OrganizationSignup), "CrcV1_OrganizationSignup")]
[JsonDerivedType(typeof(Trust), "CrcV1_Trust")]
[JsonDerivedType(typeof(HubTransfer), "CrcV1_HubTransfer")]
[JsonDerivedType(typeof(Transfer), "CrcV1_Transfer")]
public interface IIndexedEventV1 : IIndexEvent;