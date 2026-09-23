using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using MathNet.Numerics.LinearAlgebra;
using MathNet.Numerics.LinearAlgebra.Double;
using MathNet.Numerics.LinearAlgebra.Factorization;
using Microsoft.Extensions.DependencyInjection;
using MOSAIC.Components.Basics;
using MOSAIC.Components.Enums;
using MOSAIC.Diagnostics;

namespace MOSAIC.Models.Analytics;

/// <summary>
/// Online Linear Discriminant Analysis using incremental learning.
/// Maximizes between-class scatter while minimizing within-class scatter.
/// 
/// Key difference from PCA:
/// - PCA finds directions of maximum variance (unsupervised)
/// - LDA finds directions of maximum class separation (supervised)
/// 
/// Inputs (like IncrementalPredictor):
/// - Input[0]: Data source block (provides feature vectors)
/// - Input[1]: Trigger block (provides class labels)
/// </summary>
/// <example>
/// <para>Block entry for a larger pipeline. Params: components (2 or 3), learning rate, re-orthonormalisation interval, stability sample count, regularization. Labels must come from a Trigger; provide labelled observations from multiple classes.</para>
/// <code language="json">
/// {
///   "OnlineLDA": {
///     "Type": "onlinelda",
///     "Inputs": ["Features", "Labels"],
///     "Params": [2, 0.1, 100, 500, 0.0001]
///   }
/// }
/// </code>
/// </example>
public partial class OnlineLDA : BaseBlock
{
    /// <summary>Needs exactly two inputs (e.g. a data stream + a Trigger/TriggerBuffer).</summary>
    public override int MinInputs => 2;

    /// <inheritdoc cref="MinInputs"/>
    public override int MaxInputs => 2;

    private readonly int _k;
    private readonly int _reorthEvery;
    private readonly double _eta0;
    private readonly int _minStableCount;
    private readonly double _regularization;
    
    private int _d;
    private long _n;
    private bool _isStable;
    
    // Global statistics
    private Vector<double> _globalMean = DenseVector.Create(0, 0.0);
    private long _globalCount;
    
    // Class-wise statistics: label -> (mean, count, scatter matrix)
    // Scatter matrix = sum of (x - class_mean)(x - class_mean)^T for online covariance
    private readonly Dictionary<string, ClassStats> _classStats = new();
    
    private class ClassStats
    {
        public Vector<double> Mean { get; set; }
        public long Count { get; set; }
        public Matrix<double> Scatter { get; set; } // Within-class scatter for this class
        
        public ClassStats(int d)
        {
            Mean = DenseVector.Create(d, 0.0);
            Count = 0;
            Scatter = DenseMatrix.Create(d, d, 0.0);
        }
    }
    
    // LDA projection matrix (d x k)
    private Matrix<double> _W = DenseMatrix.Create(0, 0, 0.0);
    
    // Between-class scatter: Sb = Σ n_c (μ_c - μ)(μ_c - μ)^T
    private Matrix<double> _Sb = DenseMatrix.Create(0, 0, 0.0);
    
    // Within-class scatter: Sw = Σ Σ (x - μ_c)(x - μ_c)^T
    private Matrix<double> _Sw = DenseMatrix.Create(0, 0, 0.0);
    
    // For sign correction
    private Vector<double>? _prevLD1;
    private Vector<double>? _prevLD2;
    private Vector<double>? _prevLD3;
    
    // Separation metrics (eigenvalues of Sw^-1 * Sb)
    private Vector<double> _separabilityScores;
    
    // Work vectors
    private Vector<double>? _xc;
    private Vector<double>? _y;
    private Vector<double>? _diff; // For scatter updates
    
    // Current label from Trigger
    private string _currentLabel = "";

    // Cluster storage for UI visualization
    public Dictionary<string, List<Vector<double>>> CapturedClusters { get; } = new();
    public Dictionary<string, string> ClusterColors { get; } = new();
    
    private readonly List<string> _availableColors = new()
    {
        "#FF6B6B", "#4ECDC4", "#45B7D1", "#FFA07A", "#98D8C8", "#F7DC6F",
        "#BB8FCE", "#85C1E2", "#F8B88B", "#AAB7B8", "#52C7B8", "#FFB6C1"
    };
    private int _colorIndex;

    [ObservableProperty]
    private bool _isCapturing;
    
    [ObservableProperty]
    private string _currentClassName = "";

    // Public properties
    public Matrix<double> Loadings => _W;
    public Vector<double> SeparabilityScores => _separabilityScores?.Count == _k 
        ? _separabilityScores 
        : DenseVector.Create(_k, 0.0);
    public long SampleCount => _n;
    public bool IsStable => _isStable;
    public int ComponentCount => _k;
    public Vector<double> GlobalMean => _globalMean;
    public int ClassCount => _classStats.Count;

    public OnlineLDA(
        string name,
        double desiredRate = 0,
        int k = 2,
        double eta0 = 0.1,
        int reorthEvery = 100,
        int minStableCount = 500,
        double regularization = 1e-4)
        : base(name, desiredRate)
    {
        if (k < 2 || k > 3)
            throw new ArgumentOutOfRangeException(nameof(k), "k must be 2 or 3 for visualization");

        _k = k;
        _eta0 = eta0;
        _reorthEvery = Math.Max(1, reorthEvery);
        _minStableCount = Math.Max(1, minStableCount);
        _regularization = regularization;

        _separabilityScores = DenseVector.Create(_k, 0.0);
        
        Console.WriteLine($"[OnlineLDA '{Name}'] Initialized (k={k}, eta0={eta0}, reg={regularization})");
    }

    /// <summary>Recordings from this block are named <c>&lt;block&gt;_projections.csv</c>.</summary>
    protected override string DumpFilePrefix => $"{Name}_projections";

    public static OnlineLDA ConfigureInput(IServiceProvider sp, JsonModel m)
    {
        var name = m.Name ?? "OnlineLDA";
        var rate = m.DesiredRate ?? 0;

        int k = TryInt(m.Params, 0, 2);
        double eta0 = TryDouble(m.Params, 1, 0.1);
        int reorth = TryInt(m.Params, 2, 100);
        int minStable = TryInt(m.Params, 3, 500);
        double reg = TryDouble(m.Params, 4, 1e-4);

        var block = new OnlineLDA(name, rate, k, eta0, reorth, minStable, reg);
        return block;
    }

    /// <summary>
    /// Exports the hyper-parameters in exactly the order
    /// <see cref="ConfigureInput"/> reads them back out of <c>Params</c>:
    /// k, eta0, reorthEvery, minStableCount, regularization.
    /// </summary>
    protected override IReadOnlyList<object>? GetJsonParams()
        => new List<object> { _k, _eta0, _reorthEvery, _minStableCount, _regularization };

    protected override void OnReceive(object sender, object data)
    {
        var senderTypeName = sender?.GetType().Name ?? "";
        var isTrigger = senderTypeName == "Trigger" || senderTypeName.Contains("Trigger");

        if (isTrigger)
        {
            if (data is Vector<double>)
            {
                var prop = sender?.GetType().GetProperty("CurrentActionName");
                var actionName = prop?.GetValue(sender)?.ToString() ?? "unknown";
                _currentLabel = actionName;
                IsCapturing = true;
                CurrentClassName = actionName;
                
                if (!CapturedClusters.ContainsKey(actionName))
                {
                    CapturedClusters[actionName] = new List<Vector<double>>();
                    ClusterColors[actionName] = _availableColors[_colorIndex % _availableColors.Count];
                    _colorIndex++;
                }
                
                Console.WriteLine($"[OnlineLDA '{Name}'] Trigger START: '{_currentLabel}'");
                OnPropertyChanged(nameof(CapturedClusters));
            }
            else if (data is null)
            {
                Console.WriteLine($"[OnlineLDA '{Name}'] Trigger STOP: was '{_currentLabel}'");
                _currentLabel = "";
                IsCapturing = false;
                CurrentClassName = "";
                OnPropertyChanged(nameof(CapturedClusters));
            }
            return;
        }

        if (data is null) return;

        switch (data)
        {
            case Vector<double> v:
                var label = IsCapturing && !string.IsNullOrEmpty(_currentLabel) ? _currentLabel : "default";
                ProcessLabeled(label, v);
                return;

            case Matrix<double> M:
                var matrixLabel = IsCapturing && !string.IsNullOrEmpty(_currentLabel) ? _currentLabel : "default";
                for (int r = 0; r < M.RowCount; r++)
                    ProcessLabeled(matrixLabel, M.Row(r));
                return;
        }
    }

    public void SetCurrentLabel(string label)
    {
        _currentLabel = label ?? "";
        IsCapturing = !string.IsNullOrEmpty(label);
        CurrentClassName = label ?? "";
        
        if (!string.IsNullOrEmpty(label) && !CapturedClusters.ContainsKey(label))
        {
            CapturedClusters[label] = new List<Vector<double>>();
            ClusterColors[label] = _availableColors[_colorIndex % _availableColors.Count];
            _colorIndex++;
            OnPropertyChanged(nameof(CapturedClusters));
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void EnsureInit(int d)
    {
        if (d <= 0) throw new ArgumentOutOfRangeException(nameof(d));

        bool needInit = _d == 0 || _d != d || _W.RowCount != d || _W.ColumnCount != _k;
        if (!needInit) return;

        _d = d;
        _n = 0;
        _globalCount = 0;
        _isStable = false;

        _globalMean = DenseVector.Create(d, 0.0);
        
        // Initialize W with random orthonormal columns
        _W = DenseMatrix.Build.Random(d, _k);
        Orthonormalize(ref _W);

        _Sb = DenseMatrix.Create(d, d, 0.0);
        _Sw = DenseMatrix.Create(d, d, 0.0);
        
        // Add regularization to Sw diagonal
        for (int i = 0; i < d; i++)
            _Sw[i, i] = _regularization;

        _xc = DenseVector.Create(d, 0.0);
        _y = DenseVector.Create(_k, 0.0);
        _diff = DenseVector.Create(d, 0.0);

        _separabilityScores = DenseVector.Create(_k, 0.0);
        _prevLD1 = _prevLD2 = _prevLD3 = null;
        _classStats.Clear();

        Console.WriteLine($"[OnlineLDA '{Name}'] Initialized d={d}, k={_k}");
    }

    private void ProcessLabeled(string label, Vector<double> x)
    {
        if (x is null) return;
        if (string.IsNullOrWhiteSpace(label)) label = "default";

        EnsureInit(x.Count);
        if (x.Count != _d) return;

        label = label.Trim();
        _n++;
        _globalCount++;

        // === Update global mean (Welford's online algorithm) ===
        var globalDelta = x - _globalMean;
        _globalMean += globalDelta / _globalCount;

        // === Update class statistics ===
        if (!_classStats.ContainsKey(label))
        {
            _classStats[label] = new ClassStats(_d);
            Console.WriteLine($"[OnlineLDA '{Name}'] New class '{label}' (total: {_classStats.Count})");
        }

        var stats = _classStats[label];
        var oldMean = stats.Mean.Clone();
        stats.Count++;
        
        // Update class mean
        var classDelta = x - stats.Mean;
        stats.Mean += classDelta / stats.Count;
        
        // === Update within-class scatter (online covariance) ===
        // Using: Scatter_new = Scatter_old + (x - old_mean)(x - new_mean)^T
        var newDelta = x - stats.Mean;
        for (int i = 0; i < _d; i++)
        {
            for (int j = 0; j < _d; j++)
            {
                double update = classDelta[i] * newDelta[j];
                stats.Scatter[i, j] += update;
                _Sw[i, j] += update;
            }
        }

        // === Update between-class scatter periodically ===
        // Sb = Σ n_c * (μ_c - μ)(μ_c - μ)^T
        if (_n % 20 == 0)
        {
            RecomputeBetweenClassScatter();
        }

        // === Update LDA projection ===
        if (_n % _reorthEvery == 0 && _classStats.Count >= 2)
        {
            UpdateProjection();
        }

        // Check stability
        if (!_isStable && _n >= _minStableCount && _classStats.Count >= 2)
        {
            _isStable = true;
            Console.WriteLine($"[OnlineLDA '{Name}'] Stable: {_n} samples, {_classStats.Count} classes");
        }

        // === Project ===
        var xc = _xc!;
        x.Subtract(_globalMean, xc);
        
        var y = _y!;
        _W.TransposeThisAndMultiply(xc, y);

        // Store in cluster
        if (label != "default" && CapturedClusters.ContainsKey(label))
        {
            CapturedClusters[label].Add(y.Clone());
        }

        Publish((label, y.Clone()));
    }

    private void RecomputeBetweenClassScatter()
    {
        if (_classStats.Count < 2) return;

        _Sb.Clear();
        
        foreach (var (_, stats) in _classStats)
        {
            if (stats.Count == 0) continue;
            
            // diff = μ_c - μ_global
            stats.Mean.Subtract(_globalMean, _diff);
            
            // Sb += n_c * diff * diff^T
            for (int i = 0; i < _d; i++)
            {
                for (int j = 0; j < _d; j++)
                {
                    _Sb[i, j] += stats.Count * _diff![i] * _diff[j];
                }
            }
        }
    }

    private void UpdateProjection()
    {
        if (_classStats.Count < 2) return;

        try
        {
            // Regularize Sw to ensure invertibility
            var SwReg = _Sw.Clone();
            for (int i = 0; i < _d; i++)
                SwReg[i, i] += _regularization;

            // Solve generalized eigenvalue problem: Sb * w = λ * Sw * w
            // Equivalent to finding eigenvectors of Sw^(-1) * Sb
            var SwInv = SwReg.PseudoInverse();
            var target = SwInv * _Sb;
            
            var evd = target.Evd();
            var eigenvalues = evd.EigenValues.Real();
            var eigenvectors = evd.EigenVectors;

            // Sort by eigenvalue (descending) - higher = better separation
            var sorted = eigenvalues
                .Select((val, idx) => (val: Math.Max(0, val), idx)) // Eigenvalues should be non-negative
                .OrderByDescending(x => x.val)
                .Take(_k)
                .ToList();

            // Update W with top k eigenvectors
            for (int j = 0; j < _k && j < sorted.Count; j++)
            {
                var idx = sorted[j].idx;
                _W.SetColumn(j, eigenvectors.Column(idx));
            }

            // Update separability scores (normalized eigenvalues)
            double totalEig = sorted.Sum(s => s.val);
            if (totalEig > 1e-10)
            {
                for (int j = 0; j < _k && j < sorted.Count; j++)
                {
                    _separabilityScores[j] = sorted[j].val / totalEig;
                }
            }

            Orthonormalize(ref _W);
            CorrectLDSigns();
            
            // Log occasionally
            if (_n % (_reorthEvery * 10) == 0)
            {
                Console.WriteLine($"[OnlineLDA '{Name}'] Updated projection. Separability: [{string.Join(", ", _separabilityScores.Take(_k).Select(v => v.ToString("F3")))}]");
            }
        }
        catch (Exception ex)
        {
            Log.Error("OnlineLDA", Name, ex, "Projection error.");
        }
    }

    public Vector<double> Project(Vector<double> x)
    {
        if (_d == 0) throw new InvalidOperationException("LDA not initialized.");
        if (x?.Count != _d) throw new ArgumentException($"Expected dim {_d}, got {x?.Count}");
        return _W.TransposeThisAndMultiply(x - _globalMean);
    }

    public void ClearCluster(string label)
    {
        if (CapturedClusters.ContainsKey(label))
        {
            CapturedClusters.Remove(label);
            ClusterColors.Remove(label);
            Console.WriteLine($"[OnlineLDA '{Name}'] Removed cluster '{label}'");
            OnPropertyChanged(nameof(CapturedClusters));
        }
    }

    public void ClearAllClusters()
    {
        CapturedClusters.Clear();
        ClusterColors.Clear();
        _colorIndex = 0; // Reset color index so colors can be reused
        Console.WriteLine($"[OnlineLDA '{Name}'] Cleared all clusters");
        OnPropertyChanged(nameof(CapturedClusters));
    }

    public void Reset()
    {
        _d = 0;
        _n = 0;
        _globalCount = 0;
        _isStable = false;
        _globalMean = DenseVector.Create(0, 0.0);
        _W = DenseMatrix.Create(0, 0, 0.0);
        _Sb = DenseMatrix.Create(0, 0, 0.0);
        _Sw = DenseMatrix.Create(0, 0, 0.0);
        _xc = _y = _diff = null;
        _separabilityScores = DenseVector.Create(_k, 0.0);
        _prevLD1 = _prevLD2 = _prevLD3 = null;
        _classStats.Clear();
        _currentLabel = "";
        Console.WriteLine($"[OnlineLDA '{Name}'] Reset");
    }

    private void CorrectLDSigns()
    {
        if (!_isStable) return;

        var ld1 = _W.Column(0);
        if (_prevLD1 != null && ld1.DotProduct(_prevLD1) < 0)
            _W.SetColumn(0, -ld1);
        _prevLD1 = _W.Column(0).Clone();

        var ld2 = _W.Column(1);
        if (_prevLD2 != null && ld2.DotProduct(_prevLD2) < 0)
            _W.SetColumn(1, -ld2);
        _prevLD2 = _W.Column(1).Clone();

        if (_k >= 3)
        {
            var ld3 = _W.Column(2);
            if (_prevLD3 != null && ld3.DotProduct(_prevLD3) < 0)
                _W.SetColumn(2, -ld3);
            _prevLD3 = _W.Column(2).Clone();
        }
    }

    private static void Orthonormalize(ref Matrix<double> W)
    {
        var qr = W.QR(QRMethod.Full);
        var Q = qr.Q;
        for (int i = 0; i < W.RowCount; i++)
            for (int j = 0; j < W.ColumnCount; j++)
                W[i, j] = Q[i, j];
    }

    private static int TryInt(IReadOnlyList<object>? p, int idx, int def)
    {
        if (p == null || idx >= p.Count) return def;
        return p[idx] switch
        {
            int i => i,
            double d => (int)Math.Round(d),
            JsonElement je when je.ValueKind == JsonValueKind.Number => je.GetInt32(),
            JsonElement je when je.ValueKind == JsonValueKind.String && int.TryParse(je.GetString(), out var v) => v,
            _ => def
        };
    }

    private static double TryDouble(IReadOnlyList<object>? p, int idx, double def)
    {
        if (p == null || idx >= p.Count) return def;
        return p[idx] switch
        {
            double d => d,
            float f => f,
            int i => i,
            JsonElement je when je.ValueKind == JsonValueKind.Number => je.GetDouble(),
            JsonElement je when je.ValueKind == JsonValueKind.String && double.TryParse(je.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) => v,
            _ => def
        };
    }

    public override void Dispose()
    {
        base.Dispose();
    }
}