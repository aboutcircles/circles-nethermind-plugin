using System.Text.Json.Serialization;
using Circles.Common;

namespace Circles.Index.CirclesV2;

[JsonPolymorphic(TypeDiscriminatorPropertyName = "$type")]
[JsonDerivedType(typeof(RegisterOrganization), "CrcV2_RegisterOrganization")]
[JsonDerivedType(typeof(RegisterGroup), "CrcV2_RegisterGroup")]
[JsonDerivedType(typeof(RegisterHuman), "CrcV2_RegisterHuman")]
[JsonDerivedType(typeof(PersonalMint), "CrcV2_PersonalMint")]
[JsonDerivedType(typeof(Trust), "CrcV2_Trust")]
[JsonDerivedType(typeof(Stopped), "CrcV2_Stopped")]
[JsonDerivedType(typeof(ApprovalForAll), "CrcV2_ApprovalForAll")]
[JsonDerivedType(typeof(TransferSingle), "CrcV2_TransferSingle")]
[JsonDerivedType(typeof(TransferBatch), "CrcV2_TransferBatch")]
[JsonDerivedType(typeof(ERC20WrapperDeployed), "CrcV2_ERC20WrapperDeployed")]
[JsonDerivedType(typeof(Erc20WrapperTransfer), "CrcV2_Erc20WrapperTransfer")]
[JsonDerivedType(typeof(DepositInflationary), "CrcV2_DepositInflationary")]
[JsonDerivedType(typeof(WithdrawInflationary), "CrcV2_WithdrawInflationary")]
[JsonDerivedType(typeof(DepositDemurraged), "CrcV2_DepositDemurraged")]
[JsonDerivedType(typeof(WithdrawDemurraged), "CrcV2_WithdrawDemurraged")]
[JsonDerivedType(typeof(StreamCompleted), "CrcV2_StreamCompleted")]
[JsonDerivedType(typeof(DiscountCost), "CrcV2_DiscountCost")]
[JsonDerivedType(typeof(GroupMint), "CrcV2_GroupMint")]
[JsonDerivedType(typeof(FlowEdgesScopeSingleStarted), "CrcV2_FlowEdgesScopeSingleStarted")]
[JsonDerivedType(typeof(FlowEdgesScopeLastEnded), "CrcV2_FlowEdgesScopeLastEnded")]
[JsonDerivedType(typeof(TransferData), "CrcV2_TransferData")]
public interface IIndexedEventV2 : IIndexEvent;
