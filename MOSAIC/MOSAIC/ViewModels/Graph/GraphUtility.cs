using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using Avalonia;
using MOSAIC.Components.Basics;
using MOSAIC.Components.Factory;
using MOSAIC.Components.Interfaces;

namespace MOSAIC.ViewModels.Graph;

public static class GraphUtility
{
    /// <summary>
    /// Positions graph nodes in a hierarchical layout using topological sorting algorithm.
    /// </summary>
    public static void PositionNodes(ObservableCollection<VertexViewModel> vertices, Size canvasSize)
    {
        var graph = new Dictionary<string, List<string>>();
        var inDegree = new Dictionary<string, int>();
        var nodeMap = vertices.ToDictionary(n => n.Value.Name);

        // Initialize graph
        foreach (var node in vertices)
        {
            if (!inDegree.ContainsKey(node.Value.Name))
                inDegree[node.Value.Name] = 0;

            foreach (var parent in node.Neighbors)
            {
                if (!graph.ContainsKey(parent.Value.Name))
                    graph[parent.Value.Name] = [];

                graph[parent.Value.Name].Add(node.Value.Name);

                if (!inDegree.ContainsKey(node.Value.Name))
                    inDegree[node.Value.Name] = 0;

                inDegree[node.Value.Name]++;
            }
        }

        // Topological Sort (Kahn's algorithm)
        var queue = new Queue<string>(inDegree.Where(kv => kv.Value == 0).Select(kv => kv.Key));
        var layerMap = new Dictionary<string, int>();

        while (queue.Count > 0)
        {
            var nodeId = queue.Dequeue();
            var node = nodeMap[nodeId];

            var layer = 0;
            if (node.Neighbors.Count > 0)
                layer = node.Neighbors.Select(p => layerMap[p.Value.Name]).Max() + 1;

            layerMap[nodeId] = layer;

            if (!graph.ContainsKey(nodeId)) continue;
            foreach (var childId in graph[nodeId])
            {
                inDegree[childId]--;
                if (inDegree[childId] == 0)
                    queue.Enqueue(childId);
            }
        }

        // ── Push roots down: place each root just above its earliest child ──
        // This moves nodes like ground_truth (layer 0, but only child is at layer 7)
        // down to layer 6 so they sit right next to what they feed into.
        foreach (var node in vertices)
        {
            var name = node.Value.Name;
            // Only adjust root nodes (no parents)
            if (node.Neighbors.Count > 0) continue;
            if (!graph.ContainsKey(name) || graph[name].Count == 0) continue;

            // Find the minimum layer among this node's children
            int minChildLayer = graph[name].Select(c => layerMap[c]).Min();

            // Only push down if there's a gap (child is more than 1 layer below)
            if (minChildLayer > layerMap[name] + 1)
                layerMap[name] = minChildLayer - 1;
        }

        // Group nodes by layer
        var layers = new Dictionary<int, List<VertexViewModel>>();
        foreach (var node in vertices)
        {
            var layer = layerMap[node.Value.Name];
            if (!layers.ContainsKey(layer))
                layers[layer] = [];

            layers[layer].Add(node);
        }

        // Sort within each layer: nodes with parents first (part of main flow),
        // parentless nodes last (standalone sources pushed to the right edge)
        foreach (var layer in layers.Values)
        {
            layer.Sort((a, b) =>
            {
                bool aRoot = a.Neighbors.Count == 0;
                bool bRoot = b.Neighbors.Count == 0;
                if (aRoot != bRoot) return aRoot ? 1 : -1; // roots go right
                return string.Compare(a.Value.Name, b.Value.Name, StringComparison.Ordinal);
            });
        }

        // Assign positions in natural graph-space at a constant, readable spacing. The canvas
        // frames the whole graph with a zoom-to-fit transform (GraphCanvas.FitToView), so the
        // layout no longer compresses itself into the canvas size (which made nodes overlap) —
        // it lets the view scale to the window and to the network's complexity instead.
        const double hGap = 40;
        const double layerHeight = 120;   // node height (45) + clear arrow/label gap

        foreach (var layer in layers)
        {
            var nodes = layer.Value;
            var count = nodes.Count;

            // Width of the row, with an extra gap before parentless (root) nodes
            double totalWidth = 0;
            for (int i = 0; i < count; i++)
            {
                totalWidth += nodes[i].Width;
                if (i < count - 1)
                {
                    bool nextIsRoot = nodes[i + 1].Neighbors.Count == 0;
                    totalWidth += nextIsRoot ? hGap * 3 : hGap;
                }
            }

            // Center each row around x = 0 so the layers line up vertically
            double x = -totalWidth / 2.0;
            for (int i = 0; i < count; i++)
            {
                nodes[i].X = (float)x;
                nodes[i].Y = (float)(layer.Key * layerHeight);
                x += nodes[i].Width;
                if (i < count - 1)
                {
                    bool nextIsRoot = nodes[i + 1].Neighbors.Count == 0;
                    x += nextIsRoot ? hGap * 3 : hGap;
                }
            }
        }
    }

    /// <summary>
    /// Ensures all nodes are visible within the canvas by scaling their
    /// positions (X, Y). Node widths and heights are NOT scaled because the
    /// AXAML views render at their natural content size regardless of the VM
    /// properties. Instead, we treat each node as a fixed-size box and
    /// compute where its top-left corner must land so the whole graph
    /// (including node bodies) fits with a margin.
    /// If the graph can't fit without making arrows unreadable, it overflows
    /// (the canvas supports panning).
    /// </summary>
    public static void FitToCanvas(ObservableCollection<VertexViewModel> vertices, Size canvasSize)
    {
        if (vertices.Count == 0 || canvasSize.Width <= 0 || canvasSize.Height <= 0) return;

        const double margin = 16;

        // Minimum clear gap between the bottom edge of one node and the top
        // edge of the next — must fit the arrowhead + rate label box comfortably.
        const double minArrowGap = 40;

        // Bounding box of node CENTERS
        double minCx = double.MaxValue, minCy = double.MaxValue;
        double maxCx = double.MinValue, maxCy = double.MinValue;

        foreach (var v in vertices)
        {
            double cx = v.X + v.Width / 2.0;
            double cy = v.Y + v.Height / 2.0;
            if (cx < minCx) minCx = cx;
            if (cy < minCy) minCy = cy;
            if (cx > maxCx) maxCx = cx;
            if (cy > maxCy) maxCy = cy;
        }

        double spanCx = maxCx - minCx;
        double spanCy = maxCy - minCy;

        // Maximum node half-size (for edge clearance)
        double maxHalfW = 0, maxHalfH = 0;
        foreach (var v in vertices)
        {
            if (v.Width  / 2.0 > maxHalfW) maxHalfW = v.Width  / 2.0;
            if (v.Height / 2.0 > maxHalfH) maxHalfH = v.Height / 2.0;
        }

        // Available space for center positions
        double availW = canvasSize.Width  - 2 * (margin + maxHalfW);
        double availH = canvasSize.Height - 2 * (margin + maxHalfH);
        if (availW <= 0) availW = canvasSize.Width * 0.5;
        if (availH <= 0) availH = canvasSize.Height * 0.5;

        // Scale X freely
        double scaleX = spanCx > 0 ? Math.Min(availW / spanCx, 1.0) : 1.0;

        // Scale Y — but enforce minimum arrow gap.
        // Find the smallest center-to-center gap between consecutive layers.
        var layerYs = vertices.Select(v => v.Y + v.Height / 2.0).Distinct().OrderBy(y => y).ToList();
        double minOriginalGap = double.MaxValue;
        for (int i = 1; i < layerYs.Count; i++)
        {
            double gap = layerYs[i] - layerYs[i - 1];
            if (gap > 0 && gap < minOriginalGap) minOriginalGap = gap;
        }

        // The minimum center-to-center distance after scaling must be at least
        // nodeHeight + minArrowGap (so there's clear space for the arrow).
        double minCenterGap = maxHalfH * 2 + minArrowGap;
        double minScaleY = (minOriginalGap > 0 && minOriginalGap < double.MaxValue)
            ? minCenterGap / minOriginalGap
            : 0;

        double scaleY = spanCy > 0 ? Math.Min(availH / spanCy, 1.0) : 1.0;

        // Don't compress below the arrow-readability floor.
        // If that means the graph overflows, so be it — canvas supports pan.
        scaleY = Math.Max(scaleY, minScaleY);

        // Target center of the canvas
        double canvasCx = canvasSize.Width / 2.0;
        double canvasCy = canvasSize.Height / 2.0;

        // Center of the original graph
        double graphCx = (minCx + maxCx) / 2.0;
        double graphCy = (minCy + maxCy) / 2.0;

        // Reposition each node
        foreach (var v in vertices)
        {
            double cx = v.X + v.Width / 2.0;
            double cy = v.Y + v.Height / 2.0;

            double newCx = canvasCx + (cx - graphCx) * scaleX;
            double newCy = canvasCy + (cy - graphCy) * scaleY;

            v.X = (float)(newCx - v.Width / 2.0);
            v.Y = (float)(newCy - v.Height / 2.0);
        }
    }

    /// <summary>
    /// Recenters and rescales the graph for a new canvas size.
    /// Adjusts both X centering and Y spacing proportionally.
    /// </summary>
    public static void CenterNodesHorizontally(ObservableCollection<VertexViewModel> vertices, Size canvasSize)
    {
        if (vertices.Count == 0) return;

        const double hGap = 40;

        // ── Recenter X per layer ──
        var layers = vertices.GroupBy(v => v.Y).OrderBy(g => g.Key).ToList();

        foreach (var layer in layers)
        {
            var nodes = layer.OrderBy(v => v.X).ToList();
            var count = nodes.Count;

            double totalWidth = 0;
            for (int i = 0; i < count; i++)
            {
                totalWidth += nodes[i].Width;
                if (i < count - 1)
                {
                    bool nextIsRoot = nodes[i + 1].Neighbors.Count == 0;
                    totalWidth += nextIsRoot ? hGap * 3 : hGap;
                }
            }

            double startX = (canvasSize.Width - totalWidth) / 2;
            double x = startX;

            for (int i = 0; i < count; i++)
            {
                nodes[i].X = (float)x;
                x += nodes[i].Width;
                if (i < count - 1)
                {
                    bool nextIsRoot = nodes[i + 1].Neighbors.Count == 0;
                    x += nextIsRoot ? hGap * 3 : hGap;
                }
            }
        }

        // ── Rescale Y spacing ──
        if (layers.Count > 1)
        {
            const double minArrowGap = 40;
            double layerHeight = canvasSize.Height / (layers.Count + 1);
            layerHeight = Math.Clamp(layerHeight, 45 + minArrowGap, 120);

            for (int i = 0; i < layers.Count; i++)
            {
                float y = (float)(i * layerHeight);
                foreach (var v in layers[i])
                    v.Y = y;
            }
        }

        // Scale everything down if the graph overflows the canvas
        FitToCanvas(vertices, canvasSize);
    }

    public static class GraphFromBuilder
    {
        public static List<VertexViewModel> Create(
            IReadOnlyDictionary<string, JsonModel> models,
            IReadOnlyDictionary<string, BaseBlock> instancesByName,
            Size canvasSize)
        {
            ArgumentNullException.ThrowIfNull(models);
            ArgumentNullException.ThrowIfNull(instancesByName);

            var vertices = new Dictionary<string, VertexViewModel>(StringComparer.Ordinal);

            foreach (var (key, model) in models)
            {
                var logicalName = !string.IsNullOrWhiteSpace(model.Name) ? model.Name! : key;

                if (!instancesByName.TryGetValue(logicalName, out var block))
                    throw new InvalidOperationException(
                        $"No existing block instance found for '{logicalName}'. " +
                        $"Ensure BaseBlock.Name matches JsonModel.Name (or key '{key}').");

                vertices[logicalName] = new VertexViewModel(block);
            }

            foreach (var (key, model) in models)
            {
                var currentName = !string.IsNullOrWhiteSpace(model.Name) ? model.Name! : key;
                var vm = vertices[currentName];

                var parents = model.Inputs ?? Array.Empty<string>();
                foreach (var parentRef in parents)
                {
                    var parentName =
                        models.TryGetValue(parentRef, out var parentModel) && !string.IsNullOrWhiteSpace(parentModel.Name)
                            ? parentModel.Name!
                            : parentRef;

                    if (!vertices.TryGetValue(parentName, out var parentVm))
                        throw new InvalidOperationException(
                            $"Parent '{parentName}' referenced by '{currentName}' was not found among existing vertices.");

                    vm.Neighbors.Add(parentVm);
                }
            }

            var list = vertices.Values.ToList();
            PositionNodes(new ObservableCollection<VertexViewModel>(list), canvasSize);
            return list;
        }
    }
}