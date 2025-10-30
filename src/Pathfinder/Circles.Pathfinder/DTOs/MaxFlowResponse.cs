namespace Circles.Pathfinder.DTOs;

public class MaxFlowResponse
{
    public string MaxFlow { get; set; }
    public List<TransferPathStep> Transfers { get; set; }

    public MaxFlowResponse(string maxFlow, List<TransferPathStep> transfers)
    {
        MaxFlow = maxFlow;
        Transfers = transfers;
    }
}