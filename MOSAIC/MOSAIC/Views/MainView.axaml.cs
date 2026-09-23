using System;
using System.Collections.ObjectModel;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Microsoft.Extensions.DependencyInjection;
using MOSAIC.Components.Basics;
using MOSAIC.Components.Factory;
using MOSAIC.Components.Interfaces;
using MOSAIC.Diagnostics;
using MOSAIC.Services;
using MOSAIC.ViewModels;
using MOSAIC.ViewModels.Graph;
using MOSAIC.ViewModels.Dialogs;
using MOSAIC.Views.Dialogs;
using VertexViewModel = MOSAIC.ViewModels.Graph.VertexViewModel;
using System.Collections.Specialized;
using Avalonia.Media;

namespace MOSAIC.Views;

public partial class MainView : UserControl
{
    public ObservableCollection<object> Blocks { get; } = new();
    public ObservableCollection<VertexViewModel> Vertices { get; } = new();

    private readonly PipelineSession _pipeline = new();
    private readonly SemaphoreSlim _pipelineGate = new(1, 1);
    private bool _shuttingDown;

    private readonly IBlockFactory _factory;
    private readonly RecordingService? _recording;
    private readonly IFilePickerService? _picker;

    private Viewbox? _moonIcon;
    private Viewbox? _sunIcon;
    private Button? _monitorFab;
    private bool _mobileLayoutConfigured;

    public MainView()
    {
        InitializeComponent();
        WireBlockCollection();

        if (!Design.IsDesignMode && Application.Current is App app)
        {
            _factory = app.Services.GetRequiredService<IBlockFactory>();
            _recording = app.Services.GetRequiredService<RecordingService>();
            _picker = app.Services.GetRequiredService<IFilePickerService>();
            DataContext = app.Services.GetRequiredService<MainViewModel>();
        }
        else
        {
            _factory = null!;
            DataContext = new MainViewModel();
        }
    }

    public MainView(IBlockFactory factory)
    {
        InitializeComponent();
        WireBlockCollection();
        _factory = factory;
        DataContext = this;
    }

    /// <summary>
    /// Subscribes to <see cref="Blocks"/> so the scope-monitor FAB and any open monitor window
    /// follow blocks being added and removed.
    /// </summary>
    /// <remarks>
    /// Called from both constructors that run <c>InitializeComponent</c>, and from neither more than
    /// once - the three-argument overload chains to the one above rather than starting its own.
    /// It used to be a bare line in the parameterless constructor, which the running app never calls:
    /// the container picks the greediest overload, so nothing was listening to the collection and the
    /// FAB only appeared on the paths that happen to call <see cref="UpdateFabVisibility"/> by hand.
    /// Building a pipeline by dragging blocks onto the canvas is not one of them, so the monitor
    /// button stayed hidden however many scopes the pipeline had.
    /// </remarks>
    private void WireBlockCollection() => Blocks.CollectionChanged += OnBlocksChanged;

    /// <summary>
    /// The constructor the container actually picks, and therefore the one that has to carry every
    /// service this view uses.
    /// </summary>
    /// <remarks>
    /// Microsoft.Extensions.DependencyInjection selects the greediest constructor whose parameters it
    /// can all resolve, so adding a service to the parameterless overload above has no effect in the
    /// running app — the recording controls stayed dead that way. Anything new belongs here.
    /// </remarks>
    public MainView(IBlockFactory factory, RecordingService recording, IFilePickerService picker)
        : this(factory)
    {
        _recording = recording;
        _picker = picker;
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);

        if (Graph != null)
        {
            Graph.NodeClicked += OnNodeClicked;
            Graph.GroupCreated += OnGroupCreated;
            Graph.GroupRemoved += OnGroupRemoved;
            Graph.BlockDescriptorDropped += OnBlockDescriptorDropped;
            Graph.ConnectionCreated += OnConnectionCreated;
        }

        // Set up the BuildingComponentsPanel
        if (BuildingComponents != null)
        {
            BuildingComponents.BlockFactory = _factory;
        }

        // Get icon references
        _moonIcon = this.FindControl<Viewbox>("MoonIcon");
        _sunIcon = this.FindControl<Viewbox>("SunIcon");
        _monitorFab = this.FindControl<Button>("MonitorFab");

        // Subscribe to theme changes
        App.ThemeChanged += OnThemeChanged;

        // Set initial icon state
        UpdateThemeIcon(App.CurrentTheme);

        // Update FAB visibility for any blocks already loaded
        UpdateFabVisibility();

        UpdateRecordingFolderDisplay();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        App.ThemeChanged -= OnThemeChanged;

        if (Graph != null)
        {
            Graph.NodeClicked -= OnNodeClicked;
            Graph.GroupCreated -= OnGroupCreated;
            Graph.GroupRemoved -= OnGroupRemoved;
            Graph.BlockDescriptorDropped -= OnBlockDescriptorDropped;
            Graph.ConnectionCreated -= OnConnectionCreated;
        }
    }

    /// <summary>
    /// Switches this reusable pipeline workspace to overlay panes and removes the desktop toolbar.
    /// <see cref="MobileMainView"/> supplies touch-sized Android/iOS navigation around it.
    /// </summary>
    public void ConfigureForMobile()
    {
        if (_mobileLayoutConfigured) return;
        _mobileLayoutConfigured = true;
        UpdateFabVisibility();

        RenderTransform = null;
        DesktopHeader.IsVisible = false;
        Drawer.DisplayMode = SplitViewDisplayMode.Overlay;
        RightDrawer.DisplayMode = SplitViewDisplayMode.Overlay;
        SizeChanged += (_, _) => UpdateMobilePaneWidths();
        UpdateMobilePaneWidths();
    }

    private void UpdateMobilePaneWidths()
    {
        if (!_mobileLayoutConfigured) return;

        var available = Bounds.Width;
        if (available <= 0) return;

        // Leave a sliver of canvas visible so it is obvious that these are dismissible sheets.
        var paneWidth = Math.Clamp(available - 28, 280, 520);
        Drawer.OpenPaneLength = paneWidth;
        RightDrawer.OpenPaneLength = paneWidth;
    }

    public void ShowMobileCanvas()
    {
        Drawer.IsPaneOpen = false;
        RightDrawer.IsPaneOpen = false;
        LogPanel.IsVisible = false;
        App.SetBuildMode(false);
    }

    public void ToggleMobileBlocks()
    {
        var open = !Drawer.IsPaneOpen;
        RightDrawer.IsPaneOpen = false;
        LogPanel.IsVisible = false;
        Drawer.IsPaneOpen = open;
        App.SetBuildMode(false);
    }

    public void ToggleMobilePalette()
    {
        var open = !RightDrawer.IsPaneOpen;
        Drawer.IsPaneOpen = false;
        LogPanel.IsVisible = false;
        RightDrawer.IsPaneOpen = open;
        App.SetBuildMode(open);
    }

    public void ToggleMobileLog()
    {
        var open = !LogPanel.IsVisible;
        Drawer.IsPaneOpen = false;
        RightDrawer.IsPaneOpen = false;
        App.SetBuildMode(false);
        LogPanel.IsVisible = open;
    }

    private void OnThemeChanged(object? sender, AppTheme theme)
    {
        Dispatcher.UIThread.Post(() => UpdateThemeIcon(theme));
    }

    private void UpdateThemeIcon(AppTheme theme)
    {
        if (_moonIcon == null || _sunIcon == null) return;

        // In Dark mode, show Moon (to indicate "click to go to light")
        // In Light mode, show Sun (to indicate "click to go to dark")
        _moonIcon.IsVisible = theme == AppTheme.Dark;
        _sunIcon.IsVisible = theme == AppTheme.Light;
    }

    private void OnThemeToggleClicked(object? sender, RoutedEventArgs e)
    {
        App.ToggleTheme();
    }

    private void OnGridStyleChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (sender is not ComboBox comboBox) return;
        if (Graph == null) return;

        // Find the GridPatternControl in the GraphCanvas
        var gridPattern = Graph.FindControl<GridPatternControl>("GridPattern");
        if (gridPattern == null) return;

        gridPattern.PatternStyle = comboBox.SelectedIndex switch
        {
            0 => GridPatternStyle.Dots,
            1 => GridPatternStyle.Lines,
            2 => GridPatternStyle.Crosshairs,
            3 => GridPatternStyle.None,
            _ => GridPatternStyle.Dots
        };
    }

    private void OnNodeClicked(object? sender, VertexViewModel vertex)
    {
        Drawer.IsPaneOpen = true;
        var block = vertex.Value;
        ExpandAndScrollToBlock(block);
    }

    #region Graph grouping ↔ flyout sync

    /// <summary>
    /// When blocks are grouped in the graph, replace their individual cards
    /// in the flyout with a single collapsible group card.
    /// </summary>
    private void OnGroupCreated(object? sender, VertexGroupViewModel group)
    {
        // Find the earliest position of any member block in the Blocks list
        int insertAt = Blocks.Count;
        var memberBlocks = new HashSet<object>(group.Members.Select(m => m.Value));

        for (int i = 0; i < Blocks.Count; i++)
        {
            if (memberBlocks.Contains(Blocks[i]))
                insertAt = Math.Min(insertAt, i);
        }

        // Remove member blocks (iterate backwards to keep indices valid)
        for (int i = Blocks.Count - 1; i >= 0; i--)
        {
            if (memberBlocks.Contains(Blocks[i]))
                Blocks.RemoveAt(i);
        }

        // Clamp after removals
        insertAt = Math.Min(insertAt, Blocks.Count);

        // Insert the group VM — BlockTemplateSelector renders it as GroupCardView
        Blocks.Insert(insertAt, group);
    }

    /// <summary>
    /// When a group is dissolved, remove the group card and re-insert
    /// the individual member block cards at the same position.
    /// </summary>
    private void OnGroupRemoved(object? sender, VertexGroupViewModel group)
    {
        int insertAt = Blocks.IndexOf(group);
        if (insertAt >= 0)
            Blocks.RemoveAt(insertAt);
        else
            insertAt = Blocks.Count;

        // Re-insert member blocks at the same position
        foreach (var member in group.Members)
        {
            Blocks.Insert(insertAt, member.Value);
            insertAt++;
        }
    }

    #endregion

    private void ExpandAndScrollToBlock(object block)
    {
        if (Drawer.Pane is not Visual pane) return;

        foreach (var expander in pane.GetVisualDescendants().OfType<ExpanderItem>())
        {
            if (ReferenceEquals(expander.DataContext, block))
            {
                expander.IsExpanded = true;
                Dispatcher.UIThread.Post(() =>
                {
                    // Scroll to the bottom of the expanded content
                    expander.BringIntoView(new Rect(0, 0, expander.Bounds.Width, expander.Bounds.Height));
                }, DispatcherPriority.Background);
                break;
            }
        }
    }

    #region Pane resize

    private DateTime _lastResizeUpdate = DateTime.MinValue;
    private const double ResizeThrottleMs = 16;

    // While a splitter is being dragged we pause live visualization rendering so the layout pass
    // stays cheap (the charts don't fight the drag) and the resize stays smooth. A short debounce
    // resumes rendering once the drag stops.
    private DispatcherTimer? _resizeResumeTimer;
    private bool _vizSuspendedForResize;

    private void SuspendVizDuringResize()
    {
        if (!_vizSuspendedForResize)
        {
            _vizSuspendedForResize = true;
            VisualizationTimer.Instance.SuspendTicks();
        }

        if (_resizeResumeTimer is null)
        {
            _resizeResumeTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(150) };
            _resizeResumeTimer.Tick += (_, _) =>
            {
                _resizeResumeTimer!.Stop();
                if (_vizSuspendedForResize)
                {
                    _vizSuspendedForResize = false;
                    VisualizationTimer.Instance.ResumeTicks();
                }
            };
        }

        // Restart the debounce — it fires ~150 ms after the last resize event (drag end/pause).
        _resizeResumeTimer.Stop();
        _resizeResumeTimer.Start();
    }

    private void OnResizeDragDelta(object? sender, VectorEventArgs e)
    {
        SuspendVizDuringResize();

        var now = DateTime.Now;
        if ((now - _lastResizeUpdate).TotalMilliseconds < ResizeThrottleMs)
            return;
        _lastResizeUpdate = now;

        var newWidth = Drawer.OpenPaneLength + e.Vector.X;
        newWidth = Math.Clamp(newWidth, 100, 8000);
        Drawer.OpenPaneLength = newWidth;
    }

    private void OnBuildingComponentsResizeDrag(object? sender, VectorEventArgs e)
    {
        SuspendVizDuringResize();

        var now = DateTime.Now;
        if ((now - _lastResizeUpdate).TotalMilliseconds < ResizeThrottleMs)
            return;
        _lastResizeUpdate = now;

        var newWidth = RightDrawer.OpenPaneLength - e.Vector.X;
        newWidth = Math.Clamp(newWidth, 100, 8000);
        RightDrawer.OpenPaneLength = newWidth;
    }

    #endregion

    private void OnHamburgerClicked(object? sender, RoutedEventArgs e)
        => Drawer.IsPaneOpen = !Drawer.IsPaneOpen;

    private void OnRightDrawerClicked(object? sender, RoutedEventArgs e)
    {
        RightDrawer.IsPaneOpen = !RightDrawer.IsPaneOpen;
        RightDrawerButton.IsVisible = !RightDrawer.IsPaneOpen;
        App.SetBuildMode(RightDrawer.IsPaneOpen);
    }

    private void OnBuildingComponentsCloseRequested(object? sender, EventArgs e)
    {
        RightDrawer.IsPaneOpen = false;
        RightDrawerButton.IsVisible = true;
        App.SetBuildMode(false);
    }

    #region Log panel

    /// <summary>Height of <see cref="LogPanelView"/> plus the FAB's own bottom margin.</summary>
    private const double FabClearanceWithLog = 244;
    private const double FabClearanceWithoutLog = 24;

    private void OnLogToggleClicked(object? sender, RoutedEventArgs e)
    {
        LogPanel.IsVisible = !LogPanel.IsVisible;
        UpdateFabClearance();
    }

    private void OnLogPanelCloseRequested(object? sender, EventArgs e)
    {
        LogPanel.IsVisible = false;
        UpdateFabClearance();
    }

    /// <summary>
    /// Lifts the scope-monitor FAB clear of the log strip.
    /// </summary>
    /// <remarks>
    /// The FAB lives in the outer <c>Panel</c> at ZIndex 100, so it is not laid out by the grid the
    /// log strip is a row of and would otherwise float on top of the newest rows — exactly the ones
    /// being read.
    /// </remarks>
    private void UpdateFabClearance()
    {
        _monitorFab ??= this.FindControl<Button>("MonitorFab");
        if (_monitorFab == null) return;

        var bottom = LogPanel.IsVisible ? FabClearanceWithLog : FabClearanceWithoutLog;
        _monitorFab.Margin = new Thickness(0, 0, 24, bottom);
    }

    #endregion

    /// <summary>
    /// A block was dragged from the palette and dropped on the canvas: ask the user to name/configure
    /// it, then instantiate it via the factory and add it to the graph at the drop point. Inputs are
    /// wired afterwards on the canvas by dragging output→input ports (see <see cref="OnConnectionCreated"/>).
    /// </summary>
    private async void OnBlockDescriptorDropped(object? sender, (BlockDescriptor Descriptor, Point CanvasPosition) e)
    {
        try
        {
            var existingNames = Vertices.Select(v => v.Value.Name).ToList();

            var dialog = new AddBlockDialog
            {
                DataContext = new AddBlockDialogViewModel(
                    e.Descriptor, existingNames, _recording?.IsConfigured ?? false)
            };

            if (TopLevel.GetTopLevel(this) is not Window owner) return;
            await dialog.ShowDialog(owner);

            if (dialog.Result is not { } model) return;

            if (_shuttingDown || !IsEnabled) return;

            if (_factory.Create(model) is not BaseBlock newBlock)
            {
                // Silently returning here is what made a failed drag look like a dropped drag.
                Log.Error("MainView", $"Factory returned no BaseBlock for '{model.Type}'.");
                await ShowErrorAsync("Could not add block",
                    $"'{e.Descriptor.DisplayName}' could not be created. The block type '{model.Type}' " +
                    "is registered but produced nothing — see the log panel for details.");
                return;
            }

            newBlock.SetInputsFromConfig(model);

            // Recording is a session setting rather than part of the config, so it is applied to the
            // block here instead of travelling through the JsonModel the factory was given.
            if (dialog.StartRecording)
                newBlock.IsRecording = true;

            // Place the block at the drop point with no inputs; the user wires it via port drag.
            var vertex = new VertexViewModel(newBlock)
            {
                X = (float)e.CanvasPosition.X,
                Y = (float)e.CanvasPosition.Y
            };

            _pipeline.Add(newBlock);
            Blocks.Add(newBlock);
            Vertices.Add(vertex);
            Graph?.InvalidateVisual();
        }
        catch (Exception ex)
        {
            // No dialog here on purpose: this is the catch of an async void handler, where a second
            // exception (a closing owner window, a non-Panel content root) has nowhere to go. Log.Error
            // never throws and the log panel now shows the failure.
            Log.Error("MainView", ex, $"Adding '{e.Descriptor.DisplayName}' from the palette failed");
        }
    }

    /// <summary>
    /// The user drew a wire from <c>Source</c>'s output to <c>Target</c>'s input in the graph. The
    /// canvas already validated it (capacity / accepted type / no cycle); here we make it real:
    /// subscribe the target to the source publisher, record the graph edge, and persist the input name.
    /// </summary>
    private void OnConnectionCreated(object? sender, (VertexViewModel Source, VertexViewModel Target) e)
    {
        var (src, tgt) = e;

        // Live dataflow.
        if (src.Value is IPublisher pub && tgt.Value is ISubscriber sub)
            pub.AddSubscriber(sub);

        // Graph edge (Neighbors are the block's inputs).
        if (!tgt.Neighbors.Contains(src))
            tgt.Neighbors.Add(src);

        // Persisted input list — kept as a mutable List<string> so block deletion can prune it.
        var inputs = tgt.Value.Inputs is { } existing ? new List<string>(existing) : new List<string>();
        if (!inputs.Contains(src.Value.Name, StringComparer.Ordinal))
            inputs.Add(src.Value.Name);
        tgt.Value.Inputs = inputs;

        tgt.NotifyInputsChanged();
        Graph?.InvalidateVisual();
    }

    /// <summary>
    /// Opens a config file using file picker dialog.
    /// </summary>
    public async Task<string?> OpenConfigAsync()
    {
        try
        {
            var result = await PickAndLoadConfigAsync();
            if (result is null) return null;

            var (models, filePath) = result.Value;
            await LoadConfigModelsAsync(models);

            if (!string.IsNullOrEmpty(filePath) && DataContext is MainViewModel vm)
            {
                vm.AddRecentFile(filePath);
            }

            return filePath;
        }
        catch (Exception ex)
        {
            await ShowErrorAsync("Error opening config", ex.Message);
            return null;
        }
    }

    /// <summary>
    /// Opens a specific config file by path (used by recent files menu).
    /// </summary>
    public async Task<string?> OpenConfigFileAsync(string filePath)
    {
        try
        {
            var vm = DataContext as MainViewModel;

            if (!File.Exists(filePath))
            {
                await ShowErrorAsync("File Not Found",
                    $"The file '{Path.GetFileName(filePath)}' no longer exists.");

                vm?.RemoveRecentFile(filePath);
                return null;
            }

            await using var stream = File.OpenRead(filePath);
            var models = await JsonParser.ParseStreamAsync(stream);

            if (models is null)
            {
                await ShowErrorAsync("Parse Error",
                    $"Failed to parse '{Path.GetFileName(filePath)}'");
                return null;
            }

            await LoadConfigModelsAsync(models);

            vm?.AddRecentFile(filePath);
            return filePath;
        }
        catch (Exception ex)
        {
            await ShowErrorAsync("Error opening config", ex.Message);
            return null;
        }
    }

    /// <summary>Releases the active pipeline before the desktop window finishes closing.</summary>
    public async Task ShutdownAsync()
    {
        _shuttingDown = true;
        IsEnabled = false;
        await _pipelineGate.WaitAsync();
        try
        {
            Graph?.ClearGroups();
            Blocks.Clear();
            Vertices.Clear();
            await _pipeline.ClearAsync();
            _monitorWindow?.Close();
        }
        finally { _pipelineGate.Release(); }
    }

    private async Task LoadConfigModelsAsync(IReadOnlyDictionary<string, JsonModel> models)
    {
        await _pipelineGate.WaitAsync();
        try
        {
            if (_shuttingDown) return;
            IsEnabled = false;
            Graph?.ClearGroups();
            Blocks.Clear();
            Vertices.Clear();
            var graph = await _pipeline.ReplaceAsync(models, _factory);

            var instancesByName = graph.Instances.Values
                .OfType<BaseBlock>()
                .ToDictionary(b => b.Name, b => b, StringComparer.Ordinal);
        
            foreach (var rig in instancesByName.Values.OfType<Models.Devices.BodyRig>())
                rig.DescribeSensorSources(Models.Devices.BodyRigSensorSources.Resolve(rig, instancesByName));

            Blocks.Clear();
            foreach (var instance in graph.Instances.Values)
                Blocks.Add(instance);

            var size = Graph?.Bounds.Size ?? new Size(1200, 800);

            var newVertices = GraphUtility.GraphFromBuilder.Create(
                models,
                instancesByName,
                size);

            Vertices.Clear();
            foreach (var v in newVertices)
                Vertices.Add(v);

            Graph?.InvalidateVisual();
            Graph?.FitToView();

            UpdateFabVisibility();
            RefreshMonitorWindow();

            if (graph.Failures.Count > 0)
            {
                // Shown after the rest is on screen, so what did load is visible behind the message.
                var lines = graph.Failures.Select(f => $"{f.Key} ({f.Type ?? "?"}): {f.Reason}");
                await ShowErrorAsync(
                    graph.Failures.Count == 1 ? "One block could not be loaded" : $"{graph.Failures.Count} blocks could not be loaded",
                    string.Join(Environment.NewLine, lines));
            }
        }
        finally
        {
            IsEnabled = !_shuttingDown;
            _pipelineGate.Release();
        }
    }

    /// <summary>
    /// If a scope-monitor window is open, rebind it to the current block set so blocks added or
    /// removed while it is open are reflected. When no scope-capable blocks remain the window shows
    /// its empty-state placeholder rather than closing (it was opened explicitly by the user).
    /// </summary>
    private void RefreshMonitorWindow()
    {
        if (_monitorWindow is null) return;
        _monitorWindow.Rebind(_pipeline.Blocks);
    }

    /// <summary>
    /// The example pipelines deployed under <c>Assets\Examples</c>, or <see langword="null"/>
    /// when they were not deployed.
    /// </summary>
    /// <remarks>
    /// Without this the picker opens wherever the OS last left it, and the shipped examples are
    /// only findable by knowing the source tree. Null is a fine answer — the picker then behaves
    /// exactly as it did before.
    /// </remarks>
    private static async Task<IStorageFolder?> ExamplesFolderAsync(TopLevel top)
    {
        try
        {
            var dir = Path.Combine(AppContext.BaseDirectory, "Assets", "Examples");
            return Directory.Exists(dir)
                ? await top.StorageProvider.TryGetFolderFromPathAsync(dir)
                : null;
        }
        catch
        {
            // Opening the dialog somewhere unhelpful beats not opening it at all.
            return null;
        }
    }

    private async Task<(IReadOnlyDictionary<string, JsonModel> models, string filePath)?> PickAndLoadConfigAsync(CancellationToken ct = default)
    {
        var top = TopLevel.GetTopLevel(this);
        if (top?.StorageProvider is null)
        {
            await ShowErrorAsync("Open config", "File picker is not available yet.");
            return null;
        }

        var files = await top.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Select MOSAIC config (.json)",
            AllowMultiple = false,
            SuggestedStartLocation = await ExamplesFolderAsync(top),
            FileTypeFilter = new[]
            {
                new FilePickerFileType("JSON files")
                {
                    Patterns = new[] { "*.json" },
                    MimeTypes = new[] { "application/json", "text/json" },
                    AppleUniformTypeIdentifiers = new[] { "public.json" }
                }
            }
        });

        var file = files?.FirstOrDefault();
        if (file is null)
            return null;

        var filePath = file.Path.LocalPath ?? file.Path.ToString();

        await using var stream = await file.OpenReadAsync();
        var models = await JsonParser.ParseStreamAsync(stream, ct);

        return (models, filePath);
    }

    #region Recording folder

    /// <summary>
    /// Picks the one folder every recording writes into. Blocks then only carry an on/off switch,
    /// which is what makes recording usable from the palette — no path to retype per block.
    /// </summary>
    private async void OnRecordingFolderClicked(object? sender, RoutedEventArgs e)
        => await ChooseRecordingFolderAsync();

    public async Task ChooseRecordingFolderAsync()
    {
        if (_recording is null || _picker is null)
        {
            Log.Error("MainView", "Recording services were not injected; the folder button is inert.");
            return;
        }

        try
        {
            var picked = await _picker.PickFolderAsync("Choose the folder for recordings", _recording.RootFolder);
            if (picked is null) return;   // cancelled — keep whatever was set before

            _recording.RootFolder = picked;
            UpdateRecordingFolderDisplay();
        }
        catch (Exception ex)
        {
            await ShowErrorAsync("Recording folder", $"Could not set the recording folder: {ex.Message}");
        }
    }

    /// <summary>Shows the chosen folder's leaf name on the header button, full path in its tooltip.</summary>
    /// <remarks>
    /// The dot is dimmed until a folder is set, so the header answers "can anything record right
    /// now?" without being read word by word.
    /// </remarks>
    private void UpdateRecordingFolderDisplay()
    {
        if (RecordingFolderText is null) return;

        var root = _recording?.RootFolder;

        if (string.IsNullOrEmpty(root))
        {
            RecordingFolderText.Text = "Set recording folder\u2026";
            RecordingFolderDot.Opacity = 0.45;
            ToolTip.SetTip(RecordingFolderButton, "No recording folder set \u2014 blocks cannot record yet");
            return;
        }

        // Leaf name only: a lab path is long and the header is not where it needs reading in full.
        var leaf = Path.GetFileName(root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        RecordingFolderText.Text = string.IsNullOrEmpty(leaf) ? root : leaf;
        RecordingFolderDot.Opacity = 1.0;
        ToolTip.SetTip(RecordingFolderButton, $"Recordings go to {root}");
    }

    #endregion

    private async Task ShowErrorAsync(string title, string message)
    {
        var top = TopLevel.GetTopLevel(this);

        // Desktop: use a modal dialog window
        if (top is Window owner)
        {
            var dialog = new Window
            {
                Title = title,
                Width = 480,
                Height = 200,
                CanResize = false,
                WindowStartupLocation = WindowStartupLocation.CenterOwner
            };

            var panel = new StackPanel { Margin = new Thickness(16), Spacing = 12 };
            panel.Children.Add(new TextBlock { Text = message, TextWrapping = Avalonia.Media.TextWrapping.Wrap });

            var ok = new Button
                { Content = "OK", HorizontalAlignment = HorizontalAlignment.Right, MinWidth = 80 };
            ok.Click += (_, __) => dialog.Close();

            panel.Children.Add(ok);
            dialog.Content = panel;

            await dialog.ShowDialog(owner);
            return;
        }

        // Android/mobile: show an inline overlay instead
        var titleText = new TextBlock
        {
            Text = title,
            FontWeight = FontWeight.Bold,
            FontSize = 19,
            Foreground = new SolidColorBrush(Color.Parse("#F8FAFC"))
        };
        var messageText = new TextBlock
        {
            Text = message,
            TextWrapping = TextWrapping.Wrap,
            Foreground = new SolidColorBrush(Color.Parse("#CBD5E1"))
        };
        var okButton = new Button
        {
            Content = "OK",
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            MinHeight = 48,
            Margin = new Thickness(0, 4, 0, 0),
            Background = new SolidColorBrush(Color.Parse("#3B82F6")),
            Foreground = Brushes.White
        };

        var overlay = new Border
        {
            Background = new SolidColorBrush(Colors.Black, 0.6),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
            ZIndex = 1000,
            Child = new Border
            {
                Background = new SolidColorBrush(Color.Parse("#182235")),
                BorderBrush = new SolidColorBrush(Color.Parse("#334155")),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(14),
                Padding = new Thickness(20),
                Margin = new Thickness(20),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                MinWidth = 280,
                MaxWidth = 400,
                MaxHeight = 520,
                Child = new ScrollViewer
                {
                    Content = new StackPanel
                    {
                        Spacing = 14,
                        Children =
                        {
                            titleText,
                            messageText,
                            okButton
                        }
                    }
                }

            }
        };

        // Wire OK button to remove the overlay
        var tcs = new TaskCompletionSource();
        okButton.Click += (_, _) =>
        {
            if (this.Content is Panel rootPanel)
                rootPanel.Children.Remove(overlay);
            tcs.SetResult();
        };

        // Add overlay on top of existing content
        if (this.Content is Panel panel2)
            panel2.Children.Add(overlay);

        await tcs.Task;
    }

    private async void OnOpenConfigClicked(object? sender, RoutedEventArgs e)
    {
        await OpenConfigAsync();
    }

    private async void OnBlockDeleteRequested(object? sender, RoutedEventArgs e)
    {
        if (sender is not ExpanderItem expander) return;
        if (expander.DataContext is not BaseBlock blockToDelete) return;

        await _pipelineGate.WaitAsync();
        try
        {
            if (_shuttingDown) return;
            IsEnabled = false;
            // Collect all blocks to delete (the block and all its children/consumers)
            var blocksToDelete = new HashSet<BaseBlock>();
            CollectChildren(blockToDelete, blocksToDelete);
            Graph?.UngroupBlocks(blocksToDelete);

            // Remove from Blocks collection
            foreach (var block in blocksToDelete)
                Blocks.Remove(block);

            // Remove corresponding vertices
            var verticesToRemove = Vertices.Where(v => blocksToDelete.Contains(v.Value)).ToList();

            try { await _pipeline.RemoveAsync(blocksToDelete); }
            catch (Exception ex) { Log.Error("MainView", ex, "Block cleanup failed."); }

            foreach (var vertex in verticesToRemove)
                Vertices.Remove(vertex);

            // Clean up neighbor references in remaining vertices
            foreach (var vertex in Vertices)
                vertex.Neighbors.RemoveAll(n => blocksToDelete.Contains(n.Value));

            // Update Inputs of remaining blocks that referenced deleted blocks
            var deletedNames = blocksToDelete.Select(b => b.Name).ToHashSet(StringComparer.Ordinal);
            foreach (var block in Blocks.OfType<BaseBlock>())
            {
                if (block.Inputs is List<string> inputs)
                    inputs.RemoveAll(name => deletedNames.Contains(name));
            }

            // Refresh the graph
            Graph?.InvalidateVisual();
        }
        finally
        {
            IsEnabled = !_shuttingDown;
            _pipelineGate.Release();
        }
    }

    /// <summary>
    /// Recursively collects a block and all its children (blocks that use this block as input).
    /// In the graph structure, Neighbors contains parent/input blocks, so children are
    /// vertices that have this block in their Neighbors list.
    /// </summary>
    private void CollectChildren(BaseBlock block, HashSet<BaseBlock> collected)
    {
        if (!collected.Add(block))
            return; // Already processed

        // Find the vertex for this block
        var vertexToDelete = Vertices.FirstOrDefault(v => ReferenceEquals(v.Value, block));
        if (vertexToDelete == null)
            return;

        // Find all vertices that have this block as a neighbor (parent/input)
        // Those are the children that depend on this block
        var children = Vertices
            .Where(v => v.Neighbors.Contains(vertexToDelete))
            .Select(v => v.Value)
            .ToList();

        foreach (var child in children)
            CollectChildren(child, collected);
    }

    #region Scope Monitor

    private ScopeMonitorWindow? _monitorWindow;

    private bool CanOpenMonitorWindow => PopoutHelper.SupportsDesktopWindows(
        Application.Current?.ApplicationLifetime, _mobileLayoutConfigured);

    private void UpdateFabVisibility()
    {
        _monitorFab ??= this.FindControl<Button>("MonitorFab");
        if (_monitorFab == null) return;
        _monitorFab.IsEnabled = CanOpenMonitorWindow;
        _monitorFab.IsVisible = CanOpenMonitorWindow &&
            Blocks.OfType<BaseBlock>().Any(ScopeMonitorWindow.HasScopeProperty);
    }

    private bool _monitorSyncQueued;

    private void OnBlocksChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        // Coalesce bursts (deleting a block with children removes several in a row) into a single
        // deferred refresh so the FAB and any open monitor window sync exactly once.
        if (_monitorSyncQueued) return;
        _monitorSyncQueued = true;
        Dispatcher.UIThread.Post(() =>
        {
            _monitorSyncQueued = false;
            UpdateFabVisibility();
            RefreshMonitorWindow();
        }, DispatcherPriority.Background);
    }

    private void OnMonitorFabClicked(object? sender, RoutedEventArgs e)
    {
        // Guard the action too: hiding the FAB alone does not prevent routed/programmatic clicks.
        if (!CanOpenMonitorWindow) return;

        if (_monitorWindow is not null)
        {
            _monitorWindow.Activate();
            return;
        }

        _monitorWindow = new ScopeMonitorWindow(Blocks.OfType<BaseBlock>());
        _monitorWindow.Closed += (_, _) => _monitorWindow = null;
        _monitorWindow.Show();
    }

    #endregion
}
