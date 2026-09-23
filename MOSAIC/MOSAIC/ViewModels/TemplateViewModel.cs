using System;
using MOSAIC.Models;

namespace MOSAIC.ViewModels;

/// <summary>Exposes an existing TemplateViz model to a card through Block.</summary>
/// <remarks>
/// Pass the pipeline's model from the card selector; do not create a second processing block.
/// This wrapper owns no subscriptions or visualization resources. The model owns its bundle.
/// Bind VisualizationPanel.Source to Block.Viz in the view.
/// </remarks>
public class TemplateViewModel : ViewModelBase
{
    /// <summary>The model instance belonging to the pipeline.</summary>
    public TemplateViz Block { get; }

    /// <summary>Wraps the pipeline's model for binding.</summary>
    public TemplateViewModel(TemplateViz block) =>
        Block = block ?? throw new ArgumentNullException(nameof(block));
}
