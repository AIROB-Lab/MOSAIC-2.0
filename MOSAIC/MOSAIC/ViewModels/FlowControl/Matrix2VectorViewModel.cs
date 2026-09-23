using CommunityToolkit.Mvvm.ComponentModel;
using MOSAIC.Models.FlowControl;
using MOSAIC.Models.SignalProcessing;

namespace MOSAIC.ViewModels.FlowControl;

/// <summary>
/// ViewModel for the Matrix2Vector card.
/// </summary>
public partial class Matrix2VectorViewModel : ObservableObject
{
    private readonly Matrix2Vector _matrix2Vector;

    public Matrix2VectorViewModel(Matrix2Vector matrix2Vector)
    {
        _matrix2Vector = matrix2Vector;
        _matrix2Vector.PropertyChanged += (_, e) => OnPropertyChanged(e.PropertyName);
    }

    /// <summary>Exposes the underlying block for direct binding from the view.</summary>
    public Matrix2Vector Matrix2Vector => _matrix2Vector;
}