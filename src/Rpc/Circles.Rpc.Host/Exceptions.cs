namespace Circles.Rpc.Host;

public class RpcMethodNotFoundException : Exception
{
    public string MethodName { get; }
    public RpcMethodNotFoundException(string methodName)
        : base($"RPC method '{methodName}' not found.")
    {
        MethodName = methodName;
    }
}
