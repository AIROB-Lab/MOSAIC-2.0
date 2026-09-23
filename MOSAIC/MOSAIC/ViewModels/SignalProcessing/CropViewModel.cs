using CommunityToolkit.Mvvm.ComponentModel;
using MOSAIC.Models.SignalProcessing;

namespace MOSAIC.ViewModels.SignalProcessing;

/// <summary>
/// ViewModel for the Crop card.
/// </summary>
/// <remarks>
/// All parameters bind directly to the block's observable properties —
/// changes take effect on the next incoming frame. The view shows input/output
/// shapes so the user can verify the crop range is valid.
/// </remarks>
public partial class CropViewModel : ObservableObject
{
    private readonly Crop _crop;

    /// <summary>
    /// Creates a new ViewModel wrapping the given <see cref="Crop"/> block.
    /// </summary>
    public CropViewModel(Crop crop)
    {
        _crop = crop;
        _crop.PropertyChanged += (_, e) => OnPropertyChanged(e.PropertyName);
    }

    /// <summary>Exposes the underlying block for direct binding from the view.</summary>
    public Crop Crop => _crop;
}