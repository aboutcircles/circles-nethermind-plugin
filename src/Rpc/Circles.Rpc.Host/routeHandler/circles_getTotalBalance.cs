using Circles.Index.Common;
using Nethermind.Core;

namespace Circles.Rpc.Host;

public static partial class CirclesRpcHandlers
{
    public static async Task<object> HandleGetTotalBalance(JsonRpcRequest request, Settings settings, ILogger logger, CirclesRpcModule rpcModule)
    {
        // Use .EnumerateArray() to safely extract parameters from the 'Params' JsonElement
        var parameters = request.Params.EnumerateArray().ToArray();

        if (parameters.Length < 1)
        {
            throw new ArgumentException("Missing 'address' parameter.");
        }

        // 1. Extract and validate parameters
        var address = parameters[0].GetString()?.ToLowerInvariant();
        var asTimeCircles = parameters.Length > 1 ? parameters[1].GetBoolean() : false; // Safely get optional param

        if (string.IsNullOrEmpty(address))
        {
            throw new ArgumentException("Invalid 'address' parameter.");
        }

        // 2. Execute Business Logic (Your original code)
        // Convert the string address parameter to the expected Address type
        Address addressObject = new Address(address);

        // Call the core business function
        var resultWrapper = await rpcModule.circles_getTotalBalance(addressObject, asTimeCircles);

        // The handler now returns the unwrapped result from the module
        return resultWrapper.Result;
    }
}


// "/circles_getTotalBalance", async (
//     string address,
//     bool? asTimeCircles,
//     Settings settings,
//     ILogger<Program> logger) =>
// {
//     try
//     {
//         await using var connection = new NpgsqlConnection(settings.IndexReadonlyDbConnectionString);
//         await connection.OpenAsync();

//         decimal totalBalance = 0;

//         Console.WriteLine("SAY WHAT");
//         Console.WriteLine("SAY WHAT");
//         Console.WriteLine("SAY WHAT");
//         Console.WriteLine("SAY WHAT");
//         Console.WriteLine("SAY WHAT");

//         return Results.Ok(new { result = "SUCCESS" });
//     }
//     catch (Exception ex)
//     {
//         logger.LogError(ex, "Error in circles_getTotalBalance");
//         return Results.StatusCode(StatusCodes.Status500InternalServerError);
//     }
// }}