using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace MOSAIC.Views.Cards.Streaming;

/// <summary>
/// Card view for <c>SerialSender</c>.
/// </summary>
public partial class SerialSenderCardView : PopoutCardBase
{
    /// <summary>Initializes the card and loads its AXAML.</summary>
    public SerialSenderCardView()
    {
        InitializeComponent();
    }
}
