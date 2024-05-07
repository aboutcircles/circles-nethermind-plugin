using Nethermind.Api;
using Nethermind.JsonRpc.Modules;
using Nethermind.JsonRpc.Modules.Eth;

namespace Circles.Index.Rpc;

public class RentedEthRpcModule(INethermindApi nethermindApi) : IDisposable
{
    public IEthRpcModule? RpcModule { get; private set; }

    public async Task Rent()
    {
        if (nethermindApi.RpcModuleProvider == null)
        {
            throw new Exception("RpcModuleProvider is null");
        }

        IRpcModule rpcModule = await nethermindApi.RpcModuleProvider.Rent("eth_call", false);
        RpcModule = rpcModule as IEthRpcModule ?? throw new Exception("eth_call module is not IEthRpcModule");
    }

    public void Dispose()
    {
        if (RpcModule == null)
        {
            return;
        }

        nethermindApi.RpcModuleProvider?.Return("eth_call", RpcModule);
    }
}