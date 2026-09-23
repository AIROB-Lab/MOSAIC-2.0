using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.VisualTree;
using MOSAIC.Components.Basics;
using MOSAIC.Components.Interfaces;
using MOSAIC.Diagnostics;
using MOSAIC.ViewModels;
using MOSAIC.ViewModels.Graph;

namespace MOSAIC.Views;

public partial class BuildingComponentsPanel : UserControl
{
    /// <summary>
    /// Defines the Blocks property for binding available blocks.
    /// </summary>
    public static readonly StyledProperty<ObservableCollection<object>> BlocksProperty =
        AvaloniaProperty.Register<BuildingComponentsPanel, ObservableCollection<object>>(
            nameof(Blocks), new ObservableCollection<object>());

    /// <summary>
    /// Defines the Vertices property for binding graph vertices.
    /// </summary>
    public static readonly StyledProperty<ObservableCollection<VertexViewModel>> VerticesProperty =
        AvaloniaProperty.Register<BuildingComponentsPanel, ObservableCollection<VertexViewModel>>(
            nameof(Vertices), new ObservableCollection<VertexViewModel>());

    /// <summary>
    /// Defines the BlockFactory property.
    /// </summary>
    public static readonly StyledProperty<IBlockFactory?> BlockFactoryProperty =
        AvaloniaProperty.Register<BuildingComponentsPanel, IBlockFactory?>(nameof(BlockFactory));

    /// <summary>
    /// Defines the GraphCanvas property for refreshing the graph.
    /// </summary>
    public static readonly StyledProperty<GraphCanvas?> GraphCanvasProperty =
        AvaloniaProperty.Register<BuildingComponentsPanel, GraphCanvas?>(nameof(GraphCanvas));

    /// <summary>
    /// Defines the ModelsPath property for the directory containing model files.
    /// Default is empty — resolved at runtime in the constructor.
    /// </summary>
    public static readonly StyledProperty<string> ModelsPathProperty =
        AvaloniaProperty.Register<BuildingComponentsPanel, string>(nameof(ModelsPath), string.Empty);

    /// <summary>
    /// Gets or sets the collection of available blocks.
    /// </summary>
    public ObservableCollection<object> Blocks
    {
        get => GetValue(BlocksProperty);
        set => SetValue(BlocksProperty, value);
    }

    /// <summary>
    /// Gets or sets the collection of graph vertices.
    /// </summary>
    public ObservableCollection<VertexViewModel> Vertices
    {
        get => GetValue(VerticesProperty);
        set => SetValue(VerticesProperty, value);
    }

    /// <summary>
    /// Gets or sets the block factory for creating new blocks.
    /// </summary>
    public IBlockFactory? BlockFactory
    {
        get => GetValue(BlockFactoryProperty);
        set => SetValue(BlockFactoryProperty, value);
    }

    /// <summary>
    /// Gets or sets the graph canvas for refreshing visualization.
    /// </summary>
    public GraphCanvas? GraphCanvas
    {
        get => GetValue(GraphCanvasProperty);
        set => SetValue(GraphCanvasProperty, value);
    }

    /// <summary>
    /// Gets or sets the path to the models directory.
    /// </summary>
    public string ModelsPath
    {
        get => GetValue(ModelsPathProperty);
        set => SetValue(ModelsPathProperty, value);
    }

    /// <summary>
    /// Event raised when the close button is clicked.
    /// </summary>
    public event EventHandler? CloseRequested;

    /// <summary>
    /// Event raised when a resize drag occurs.
    /// </summary>
    public event EventHandler<VectorEventArgs>? ResizeDrag;

    /// <summary>
    /// Cache for parsed input constraints from source files.
    /// </summary>
    private static readonly Dictionary<string, (int Min, int Max, List<string> AllowableBlocks)> _constraintsCache =
        new();

    private DateTime _lastResizeUpdate = DateTime.MinValue;
    private const double ResizeThrottleMs = 16;
    private ItemsControl? _modelFilesList;

    public BuildingComponentsPanel()
    {
        ModelsPath = FindModelsPath();
        Log.Debug("BuildingComponents", $"BaseDirectory: {AppContext.BaseDirectory}");
        Log.Debug("BuildingComponents", $"ModelsPath: {ModelsPath}");
        Log.Debug("BuildingComponents", $"Directory exists: {Directory.Exists(ModelsPath)}");
        InitializeComponent();
    }

    /// <summary>
    /// Walks up the directory tree from the executable to find the MOSAIC solution,
    /// then builds the Models path relative to the solution root.
    /// Falls back to a path next to the executable if not found.
    /// </summary>
    private static string FindModelsPath()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            // Do not enumerate every parent directory. On Android the walk eventually reaches
            // /data/user/0, which belongs to the system and cannot be listed by an application.
            // File.Exists safely reports false for an inaccessible path and is sufficient because
            // this repository's solution name is known.
            if (File.Exists(Path.Combine(dir.FullName, "MOSAIC.sln")))
            {
                var candidate = Path.Combine(dir.FullName, "MOSAIC", "Models");
                if (Directory.Exists(candidate))
                    return candidate;
            }
        }

        return Path.Combine(AppContext.BaseDirectory, "Models");
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _modelFilesList = this.FindControl<ItemsControl>("ModelFilesList");
        LoadModelFiles();
    }

    /// <summary>
    /// Loads model files from the configured path.
    /// </summary>
    public void LoadModelFiles()
    {
        if (_modelFilesList == null || !Directory.Exists(ModelsPath))
        {
            Log.Warn("BuildingComponents", $"Models directory not found: {ModelsPath}");
            return;
        }

        var files = Directory.GetFiles(ModelsPath, "*.cs", SearchOption.AllDirectories);

        var groups = files
            .Select(f =>
            {
                var relativePath = Path.GetRelativePath(ModelsPath, f);
                var parts = relativePath.Split(Path.DirectorySeparatorChar);
                var group = parts.Length > 1 ? parts[0] : "Root";
                return new ModelFileItem
                {
                    Name = Path.GetFileNameWithoutExtension(f),
                    FullPath = f,
                    Group = group
                };
            })
            .GroupBy(f => f.Group)
            .OrderBy(g => g.Key)
            .Select(g => new ModelGroupItem
            {
                GroupName = g.Key,
                Count = g.Count(),
                Files = g.OrderBy(f => f.Name).ToList()
            })
            .ToList();

        _modelFilesList.ItemsSource = groups;
    }

    private void OnCloseClicked(object? sender, RoutedEventArgs e)
    {
        CloseRequested?.Invoke(this, EventArgs.Empty);
    }

    private void OnResizeDragDelta(object? sender, VectorEventArgs e)
    {
        var now = DateTime.Now;
        if ((now - _lastResizeUpdate).TotalMilliseconds < ResizeThrottleMs)
            return;

        _lastResizeUpdate = now;
        ResizeDrag?.Invoke(this, e);
    }

    private void OnInputBlockChecked(object? sender, RoutedEventArgs e)
    {
        if (sender is not CheckBox checkBox) return;

        var expander = checkBox.FindAncestorOfType<Expander>();
        if (expander == null) return;

        var modelFile = GetModelFileItemFromExpander(expander);
        var (minInputs, maxInputs, _) = GetInputConstraints(modelFile); // discard AllowableBlocks here

        // Only count visible checkboxes (hidden ones are filtered out by AllowableBlocks)
        var allCheckBoxes = expander.GetVisualDescendants()
            .OfType<CheckBox>()
            .Where(cb => cb.IsVisible)
            .ToList();

        var checkedCount = allCheckBoxes.Count(cb => cb.IsChecked == true);

        // Enable/disable based on MaxInputs
        if (maxInputs < int.MaxValue)
        {
            foreach (var cb in allCheckBoxes)
            {
                cb.IsEnabled = cb.IsChecked == true || checkedCount < maxInputs;
            }
        }

        // Show AddButton once minimum selections are met
        if (expander.Header is Grid headerGrid)
        {
            var addButton = headerGrid.Children
                .OfType<Button>()
                .FirstOrDefault(b => b.Tag?.ToString() == "AddButton");

            if (addButton != null)
            {
                addButton.IsVisible = checkedCount >= minInputs && checkedCount > 0;
            }
        }
    }

    private void OnModelFileExpanderExpanded(object? sender, RoutedEventArgs e)
    {
        if (sender is not Expander expander) return;

        var modelFile = GetModelFileItemFromExpander(expander);
        var (minInputs, maxInputs, allowableBlocks) = GetInputConstraints(modelFile);

        // Find the "Input Blocks" section
        var inputBlocksSection = expander.GetVisualDescendants()
            .OfType<StackPanel>()
            .FirstOrDefault(sp => sp.Children.OfType<TextBlock>()
                .Any(tb => tb.Text == "Input Blocks"));

        if (inputBlocksSection != null)
        {
            inputBlocksSection.IsVisible = maxInputs > 0;

            // Apply AllowableBlocks filter to checkboxes
            if (maxInputs > 0)
            {
                var checkBoxes = expander.GetVisualDescendants()
                    .OfType<CheckBox>()
                    .ToList();

// First pass: stamp the class name into Tag using the Vertices lookup
                foreach (var cb in checkBoxes)
                {
                    var displayName = cb.Content?.ToString() ?? string.Empty;
                    var vertex = Vertices.FirstOrDefault(v => v.Value.Name == displayName);
                    cb.Tag = vertex?.Value.GetType().Name ?? string.Empty;
                }

// Second pass: apply AllowableBlocks filter
                bool hasRestriction = allowableBlocks.Count > 0;

                foreach (var cb in checkBoxes)
                {
                    var className = cb.Tag?.ToString() ?? string.Empty;
                    bool isAllowed = !hasRestriction || allowableBlocks.Contains(className);
                    cb.IsVisible = isAllowed;
                    if (!isAllowed) cb.IsChecked = false;
                }
            }
        }

        // Update the header AddButton for source blocks (maxInputs == 0)
        if (expander.Header is Grid headerGrid)
        {
            var addButton = headerGrid.Children
                .OfType<Button>()
                .FirstOrDefault(b => b.Tag?.ToString() == "AddButton");

            if (addButton != null && maxInputs == 0)
            {
                addButton.IsVisible = true;
            }
        }
    }

    private void OnAddBlockClicked(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button button) return;
        if (BlockFactory == null) return;

        var expander = button.FindAncestorOfType<Expander>();
        if (expander == null) return;

        var modelFile = GetModelFileItemFromExpander(expander);
        if (modelFile == null) return;

        try
        {
            var textBoxes = expander.GetVisualDescendants().OfType<TextBox>().ToList();
            var numericUpDowns = expander.GetVisualDescendants().OfType<NumericUpDown>().ToList();
            var checkBoxes = expander.GetVisualDescendants().OfType<CheckBox>().ToList();

            // Get name
            var name = textBoxes.FirstOrDefault()?.Text ?? modelFile.Name;
            if (string.IsNullOrWhiteSpace(name))
                name = $"{modelFile.Name}_{Blocks.Count + 1}";

            // Ensure unique name
            var baseName = name;
            var counter = 1;
            while (Blocks.OfType<BaseBlock>().Any(b => b.Name == name))
            {
                name = $"{baseName}_{counter++}";
            }

            // Get desired rate
            var desiredRate = numericUpDowns.FirstOrDefault()?.Value ?? 0;

            // Get selected input blocks
            var selectedInputNames = checkBoxes
                .Where(cb => cb.IsChecked == true)
                .Select(cb => cb.Content?.ToString())
                .Where(n => !string.IsNullOrEmpty(n))
                .Cast<string>()
                .ToList();

            // Create JsonModel
            var jsonModel = new JsonModel
            {
                Type = modelFile.Name,
                Name = name,
                DesiredRate = (double)desiredRate,
                Inputs = selectedInputNames.Count > 0 ? selectedInputNames! : null
            };

            // Create the block
            var blockInstance = BlockFactory.Create(jsonModel);

            if (blockInstance is not BaseBlock newBlock)
            {
                Log.Error("BuildingComponents", $"Failed to create block: {modelFile.Name}");
                return;
            }

            newBlock.SetInputsFromConfig(jsonModel);
            Blocks.Add(newBlock);

            // Create VertexViewModel
            var newVertex = new VertexViewModel(newBlock);

            // Set up neighbors and data flow
            foreach (var inputName in selectedInputNames)
            {
                var inputVertex = Vertices.FirstOrDefault(v => v.Value.Name == inputName);
                if (inputVertex != null)
                {
                    newVertex.Neighbors.Add(inputVertex);

                    if (inputVertex.Value is IPublisher publisher)
                    {
                        publisher.AddSubscriber(newBlock);
                    }
                }
            }

            Vertices.Add(newVertex);

            // Reposition and refresh graph
            var canvasSize = GraphCanvas?.Bounds.Size ?? new Size(1200, 800);
            GraphUtility.PositionNodes(Vertices, canvasSize);
            GraphCanvas?.InvalidateVisual();
            GraphCanvas?.FitToView();

            // Collapse the expander
            expander.IsExpanded = false;

            Log.Info("BuildingComponents",
                $"Added block '{name}' of type '{modelFile.Name}' with {selectedInputNames.Count} inputs");
        }
        catch (Exception ex)
        {
            Log.Error("BuildingComponents", ex, "Error adding block.");
        }
    }

    private static ModelFileItem? GetModelFileItemFromExpander(Expander expander)
    {
        return expander.DataContext as ModelFileItem;
    }

    private static (int Min, int Max, List<string> AllowableBlocks) GetInputConstraints(ModelFileItem? modelFile)
    {
        if (modelFile == null || string.IsNullOrEmpty(modelFile.FullPath))
            return (1, 1, new List<string>());

        if (_constraintsCache.TryGetValue(modelFile.FullPath, out var cached))
            return cached;

        var constraints = ParseInputConstraintsFromFile(modelFile.FullPath);
        _constraintsCache[modelFile.FullPath] = constraints;
        return constraints;
    }

    private static (int Min, int Max, List<string> AllowableBlocks) ParseInputConstraintsFromFile(string filePath)
    {
        if (!File.Exists(filePath))
            return (1, 1, new List<string>());

        try
        {
            var content = File.ReadAllText(filePath);

            int minInputs = 1;
            int maxInputs = 1;

            var minMatch = Regex.Match(content, @"override\s+int\s+MinInputs\s*=>\s*(\d+|int\.MaxValue)");
            if (minMatch.Success)
            {
                var value = minMatch.Groups[1].Value;
                minInputs = value == "int.MaxValue" ? int.MaxValue : int.Parse(value);
            }

            var maxMatch = Regex.Match(content, @"override\s+int\s+MaxInputs\s*=>\s*(\d+|int\.MaxValue)");
            if (maxMatch.Success)
            {
                var value = maxMatch.Groups[1].Value;
                maxInputs = value == "int.MaxValue" ? int.MaxValue : int.Parse(value);
            }

            var allowableBlocks = new List<string>();

// Style 1: collection expression  =>  ["ClockBlock", "OtherBlock"]
            var collectionMatch = Regex.Match(content,
                @"AllowableBlocks\s*(?:=>|=)\s*\[([^\]]*)\]");

// Style 2: array initializer  =>  new[] { "ClockBlock" }  /  new string[] { "ClockBlock" }
            var arrayInitMatch = Regex.Match(content,
                @"AllowableBlocks\s*(?:=>|=)\s*new\s*(?:string\s*)?\[\s*\]\s*\{([^}]*)\}");

            var body = collectionMatch.Success
                ? collectionMatch.Groups[1].Value
                : arrayInitMatch.Success
                    ? arrayInitMatch.Groups[1].Value
                    : null;

            if (body != null)
            {
                var stringMatches = Regex.Matches(body, @"""([^""]+)""");
                foreach (Match m in stringMatches)
                    allowableBlocks.Add(m.Groups[1].Value);
            }

            return (minInputs, maxInputs, allowableBlocks);
        }
        catch (Exception ex)
        {
            // One of the two ways a block lands on the canvas with the wrong port count, and it
            // used to happen without a word.
            Log.Warn("BuildingComponents", ex, $"Could not read input constraints from '{filePath}'; using the defaults.");
            return (1, 1, new List<string>());
        }
    }
}
