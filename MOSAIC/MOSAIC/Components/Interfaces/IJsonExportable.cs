using MOSAIC.Components.Basics;

namespace MOSAIC.Components.Interfaces;

/// <summary>
/// Interface for blocks that can export their configuration to JsonModel.
/// </summary>
/// <remarks>
/// Implement this interface to enable saving block configurations to JSON files.
/// The exported JsonModel should contain all parameters needed to recreate the block
/// in its current state.
/// </remarks>
public interface IJsonExportable
{
    /// <summary>
    /// Exports the current block configuration to a JsonModel.
    /// </summary>
    /// <returns>A JsonModel representing the current state of the block.</returns>
    /// <remarks>
    /// The returned JsonModel should include:
    /// <list type="bullet">
    /// <item><description>Type - the block type identifier</description></item>
    /// <item><description>Name - the block's name</description></item>
    /// <item><description>Params - all configurable parameters</description></item>
    /// <item><description>DesiredRate - if applicable</description></item>
    /// <item><description>Path - if the block uses external files</description></item>
    /// </list>
    /// Note: Inputs are typically preserved from the original model during serialization,
    /// as they represent the graph structure rather than block state.
    /// </remarks>
    JsonModel ToJsonModel();
}