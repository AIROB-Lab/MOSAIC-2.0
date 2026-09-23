using CommunityToolkit.Mvvm.ComponentModel;
using MOSAIC.Models;
using SinGenerator = MOSAIC.Models.Streaming.SinGenerator;

namespace MOSAIC.ViewModels;

public class SinGeneratorViewModel(SinGenerator sinGenerator) : ObservableObject
{

    public SinGenerator SinGenerator => sinGenerator;
}