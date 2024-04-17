using Nethermind.Api;
using Nethermind.JsonRpc.Modules;
using Nethermind.JsonRpc.Modules.Eth;

namespace Circles.Index;

public class RentedEthRpcModule : IDisposable
{
    public IEthRpcModule? RpcModule { get; private set; }
    private readonly INethermindApi _nethermindApi;

    public RentedEthRpcModule(INethermindApi nethermindApi)
    {
        _nethermindApi = nethermindApi;
    }
        
    public async Task Rent()
    {
        if (_nethermindApi.RpcModuleProvider == null)
        {
            throw new Exception("RpcModuleProvider is null");
        }
        IRpcModule rpcModule = await _nethermindApi.RpcModuleProvider.Rent("eth_call", false);
        RpcModule = rpcModule as IEthRpcModule ?? throw new Exception("eth_call module is not IEthRpcModule");
    }
        
    public void Dispose()
    {
        if (RpcModule == null)
        {
            return;
        }
        _nethermindApi.RpcModuleProvider?.Return("eth_call", RpcModule);
    }
}