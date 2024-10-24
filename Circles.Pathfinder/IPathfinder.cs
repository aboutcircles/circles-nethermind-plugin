using Circles.Pathfinder.DTOs;

namespace Circles.Pathfinder;

public interface IPathfinder
{
    public Task<MaxFlowResponse> ComputeMaxFlow(FlowRequest request);
}