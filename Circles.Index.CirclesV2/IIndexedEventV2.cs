using System.Text.Json.Serialization;
using Circles.Index.CirclesV2.CMGroupDeployer;
using Circles.Index.CirclesV2.Hub;
using Circles.Index.CirclesV2.LBP;
using Circles.Index.CirclesV2.NameRegistry;
using Circles.Index.CirclesV2.StandardTreasury;
using Circles.Index.Common;

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
[JsonDerivedType(typeof(CMGroupCreated), "CrcV2_CMGroupCreated")]
[JsonDerivedType(typeof(CirclesBackingDeployed), "CrcV2_CirclesBackingDeployed")]
[JsonDerivedType(typeof(LbpDeployed), "CrcV2_LbpDeployed")]
[JsonDerivedType(typeof(CirclesBackingInitiated), "CrcV2_CirclesBackingInitiated")]
[JsonDerivedType(typeof(CirclesBackingCompleted), "CrcV2_CirclesBackingCompleted")]
[JsonDerivedType(typeof(Released), "CrcV2_Released")]
[JsonDerivedType(typeof(RegisterShortName), "CrcV2_RegisterShortName")]
[JsonDerivedType(typeof(UpdateMetadataDigest), "CrcV2_UpdateMetadataDigest")]
[JsonDerivedType(typeof(CidV0), "CrcV2_CidV0")]
[JsonDerivedType(typeof(CreateVault), "CrcV2_CreateVault")]
[JsonDerivedType(typeof(CollateralLockedSingle), "CrcV2_CollateralLockedSingle")]
[JsonDerivedType(typeof(CollateralLockedBatch), "CrcV2_CollateralLockedBatch")]
[JsonDerivedType(typeof(GroupRedeem), "CrcV2_GroupRedeem")]
[JsonDerivedType(typeof(GroupRedeemCollateralReturn), "CrcV2_GroupRedeemCollateralReturn")]
[JsonDerivedType(typeof(GroupRedeemCollateralBurn), "CrcV2_GroupRedeemCollateralBurn")]
public interface IIndexedEventV2 : IIndexEvent;