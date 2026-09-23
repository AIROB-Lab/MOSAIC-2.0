using System;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using MOSAIC.Converters;
using MOSAIC.ViewModels.Graph;

namespace MOSAIC.Views;

public partial class GraphVertexGroupView : UserControl
{
    private static readonly IBrush SelectionBrush = new SolidColorBrush(Color.Parse("#4A9EF5"));
    private static readonly BoxShadows SelectedShadow = BoxShadows.Parse("0 0 0 4 #884A9EF5");
    private static readonly BoxShadows DefaultShadow = BoxShadows.Parse("0 3 12 0 #50000000");
    private static readonly StatusToColorConverter StatusConverter = new();

    private TextBlock? _nameLabel;
    private TextBox? _nameEditor;
    private bool _isEditing;

    public GraphVertexGroupView() => InitializeComponent();

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);

        if (DataContext is not VertexGroupViewModel vm) return;

        var border = this.FindControl<Border>("RootBorder");
        _nameLabel = this.FindControl<TextBlock>("NameLabel");
        _nameEditor = this.FindControl<TextBox>("NameEditor");

        if (border is not null)
        {
            ApplySelectionVisual(border, vm.IsSelected);
            vm.PropertyChanged += (_, a) =>
            {
                if (a.PropertyName == nameof(VertexGroupViewModel.IsSelected))
                    ApplySelectionVisual(border, vm.IsSelected);
            };
        }

        // Wire up edit mode events
        if (_nameEditor is not null)
        {
            _nameEditor.KeyDown += OnEditorKeyDown;
            _nameEditor.LostFocus += (_, __) => CommitRename();
        }
    }

    #region Inline rename

    /// <summary>
    /// Enters edit mode — hides the label, shows the textbox, focuses it.
    /// Called from GraphCanvas on double-click.
    /// </summary>
    public void BeginRename()
    {
        if (_nameLabel is null || _nameEditor is null || _isEditing) return;
        if (DataContext is not VertexGroupViewModel vm) return;

        _isEditing = true;
        _nameLabel.IsVisible = false;
        _nameEditor.IsVisible = true;
        _nameEditor.Text = vm.Name;
        _nameEditor.SelectAll();

        // Focus on next layout pass so the TextBox is measured first
        Dispatcher.UIThread.Post(() => _nameEditor.Focus(), DispatcherPriority.Input);
    }

    private void CommitRename()
    {
        if (!_isEditing || _nameLabel is null || _nameEditor is null) return;
        if (DataContext is not VertexGroupViewModel vm) return;

        _isEditing = false;

        var newName = _nameEditor.Text?.Trim();
        if (!string.IsNullOrEmpty(newName))
            vm.Name = newName;

        _nameEditor.IsVisible = false;
        _nameLabel.IsVisible = true;
    }

    private void CancelRename()
    {
        if (!_isEditing || _nameLabel is null || _nameEditor is null) return;

        _isEditing = false;
        _nameEditor.IsVisible = false;
        _nameLabel.IsVisible = true;
    }

    private void OnEditorKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            CommitRename();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            CancelRename();
            e.Handled = true;
        }
    }

    /// <summary>
    /// Returns whether inline editing is currently active.
    /// GraphCanvas checks this to avoid starting a drag during rename.
    /// </summary>
    public bool IsEditing => _isEditing;

    #endregion

    #region Selection visual

    private static void ApplySelectionVisual(Border border, bool selected)
    {
        if (selected)
        {
            border.BorderBrush = SelectionBrush;
            border.BoxShadow = SelectedShadow;
        }
        else
        {
            border.Bind(Border.BorderBrushProperty, new Binding("AggregateStatus")
            {
                Converter = StatusConverter
            });
            border.BoxShadow = DefaultShadow;
        }
    }

    #endregion
}