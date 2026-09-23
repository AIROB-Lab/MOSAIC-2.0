using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MOSAIC.Models;

namespace MOSAIC.ViewModels;

public partial class JoinerViewModel(Joiner joiner) : ObservableObject
{
    public Joiner Joiner => joiner;


    [ObservableProperty] private bool _isJsonConfigExpanded = false;

    partial void OnIsJsonConfigExpandedChanged(bool value)
    {
        if (!value) return;
        RefreshSources();
    }

    [RelayCommand]
    private void ToggleJsonConfigExpanded() => IsJsonConfigExpanded = !IsJsonConfigExpanded;

    public ObservableCollection<string> AvailableTimerSources { get; } = new(joiner.Inputs ?? []);

    [ObservableProperty]
    private string? _selectedTimerSource = joiner.TimerSourceName;

    partial void OnSelectedTimerSourceChanged(string? value)
    {
        if (value is not null)
            joiner.SetTimerSource(value);
    }

    private void RefreshSources()
    {
        AvailableTimerSources.Clear();
        if (joiner.Inputs != null)
            foreach (var name in joiner.Inputs)
                AvailableTimerSources.Add(name);

        if (SelectedTimerSource is null || !AvailableTimerSources.Contains(SelectedTimerSource))
            SelectedTimerSource = joiner.TimerSourceName;
    }
}