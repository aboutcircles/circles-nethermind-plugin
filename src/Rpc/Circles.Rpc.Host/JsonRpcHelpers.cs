namespace Circles.Rpc.Host;

public static class JsonRpcHelpers
{
    public static object CreateError(string message, int code = -1)
    {
        return new { error = message, code = code };
    }
}