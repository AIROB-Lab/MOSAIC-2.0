using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using MOSAIC.Components.Basics;
using MOSAIC.Components.Factory;

namespace MOSAIC.ViewModels.Dialogs;

public partial class ParamField : ObservableObject
{
    public ParamHint Hint { get; }
    public string[] Choices => Hint.Choices ?? Array.Empty<string>();
    public bool IsEnum => Hint.Kind == ParamKind.Enum;

    [ObservableProperty] private string _value;

    /// <summary>False when a conditional param is hidden because its controlling param isn't set to a
    /// value that needs it. Hidden fields are still emitted (at their current/default value).</summary>
    [ObservableProperty] private bool _isVisible = true;

    public ParamField(ParamHint hint)
    {
        Hint = hint;
        _value = hint.Default?.ToString() ?? "";
    }
}

/// <summary>
/// Backs the (slimmed) Add-Block dialog: just a name, an optional desired rate, and the block's
/// parameters. Inputs are no longer chosen here — they're wired on the canvas by dragging an
/// output port to an input port, so a freshly added block simply shows its required-but-empty ports.
/// </summary>
public partial class AddBlockDialogViewModel : ObservableObject
{
    public BlockDescriptor Descriptor { get; }

    private readonly IReadOnlyList<string> _existingNames;

    [ObservableProperty] private string _blockName;
    [ObservableProperty] private string _desiredRateText = "";
    [ObservableProperty] private string _validationError = "";
    [ObservableProperty] private bool _canAdd;

    /// <summary>Whether this block should start out recording once it has been created.</summary>
    /// <remarks>
    /// Applied to the block after the factory builds it, not written into the config: the pipeline
    /// JSON has no recording field, so this is a convenience for the drop, not a saved setting.
    /// </remarks>
    [ObservableProperty] private bool _record;

    /// <summary>False until a recording folder has been chosen, which is what a recording needs.</summary>
    public bool CanRecord { get; }

    /// <summary>Sub-label under the record checkbox: what it does, or what is missing.</summary>
    public string RecordHint => CanRecord
        ? "Writes to the recording folder for this session; not saved with the pipeline."
        : "Set a recording folder in the header first.";

    public ObservableCollection<ParamField> ParamFields { get; }

    public bool HasParams => ParamFields.Count > 0;

    public AddBlockDialogViewModel(
        BlockDescriptor descriptor,
        IReadOnlyList<string> existingNames,
        bool canRecord = false)
    {
        Descriptor = descriptor;
        _existingNames = existingNames;
        CanRecord = canRecord;
        _blockName = MakeUniqueName(descriptor.DisplayName.Replace(" ", ""), _existingNames);

        ParamFields = new ObservableCollection<ParamField>(
            descriptor.Params.Select(p => new ParamField(p)));
        foreach (var f in ParamFields)
            f.PropertyChanged += OnParamFieldChanged;

        RecomputeParamVisibility();
        Recompute();
    }

    partial void OnBlockNameChanged(string value) => Recompute();

    private void Recompute() => CanAdd = !string.IsNullOrWhiteSpace(BlockName);

    private void OnParamFieldChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ParamField.Value))
            RecomputeParamVisibility();
    }

    /// <summary>Shows/hides conditional params based on the current value of their controlling param.</summary>
    private void RecomputeParamVisibility()
    {
        var byName = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var f in ParamFields)
            byName[f.Hint.Name] = f.Value;

        foreach (var f in ParamFields)
        {
            if (f.Hint.VisibleWhen is not { } controller)
            {
                f.IsVisible = true;
                continue;
            }
            f.IsVisible = byName.TryGetValue(controller, out var current)
                       && f.Hint.VisibleWhenValues is { } allowed
                       && allowed.Contains(current, StringComparer.OrdinalIgnoreCase);
        }
    }

    /// <summary>Validates all fields and returns a <see cref="JsonModel"/> on success, else null.</summary>
    public JsonModel? TryBuildModel()
    {
        ValidationError = "";

        var name = BlockName.Trim();
        if (string.IsNullOrWhiteSpace(name))
        {
            ValidationError = "Block name is required.";
            return null;
        }
        if (_existingNames.Contains(name, StringComparer.OrdinalIgnoreCase))
        {
            ValidationError = $"A block named '{name}' already exists.";
            return null;
        }

        double? rate = null;
        if (!string.IsNullOrWhiteSpace(DesiredRateText))
        {
            if (!double.TryParse(DesiredRateText, NumberStyles.Float, CultureInfo.InvariantCulture, out var r) || r <= 0)
            {
                ValidationError = "Desired rate must be a positive number.";
                return null;
            }
            rate = r;
        }

        var @params = BuildParams();

        return new JsonModel
        {
            Type = Descriptor.TypeKey,
            Name = name,
            DesiredRate = rate,
            Inputs = null,                 // wired later via port drag
            Params = @params.Count > 0 ? @params : null
        };
    }

    private List<object> BuildParams()
    {
        var result = new List<object>();
        foreach (var field in ParamFields)
        {
            var raw = field.Value.Trim();
            object parsed = field.Hint.Kind switch
            {
                ParamKind.Int    => int.TryParse(raw, out var i) ? i : 0,
                ParamKind.Double => double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : 0.0,
                ParamKind.Bool   => raw.Equals("true", StringComparison.OrdinalIgnoreCase),
                _                => raw
            };
            result.Add(parsed);
        }
        return result;
    }

    private static string MakeUniqueName(string baseName, IReadOnlyList<string> existing)
    {
        if (!existing.Contains(baseName, StringComparer.OrdinalIgnoreCase))
            return baseName;
        for (int i = 1; i < 100; i++)
        {
            var candidate = $"{baseName}{i}";
            if (!existing.Contains(candidate, StringComparer.OrdinalIgnoreCase))
                return candidate;
        }
        return baseName;
    }
}
