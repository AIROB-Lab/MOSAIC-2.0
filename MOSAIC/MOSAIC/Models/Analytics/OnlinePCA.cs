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

namespace MOSAIC.Models.Analytics;

/// <summary>
/// Online Principal Component Analysis using incremental (streaming) Oja's learning rule.
/// </summary>
/// <remarks>
/// <para>
/// <b>Overview:</b> PCA finds orthogonal directions of maximum variance in the input data.
/// This implementation learns the top <c>k</c> principal components incrementally, one sample
/// at a time, making it suitable for real-time dimensionality reduction of streaming signals.
/// </para>
/// <para>
/// <b>Algorithm:</b> Uses a modified Oja's rule with anti-Hebbian lateral connections:
/// <code>
///     y = Wᵀ · (x − μ)                     (project)
///     W ← W + η · (x − μ) · yᵀ − η · W · yyᵀ  (update)
/// </code>
/// The second term enforces decorrelation between components. Periodic QR orthonormalisation
/// prevents numerical drift.
/// </para>
/// <para>
/// <b>Learning Rate:</b> Decays as <c>η = η₀ / √n</c> where <c>n</c> is the sample count.
/// </para>
/// <para>
/// <b>Variance Tracking:</b> Per-component and total variance are estimated via exponential
/// moving averages (EMA), enabling real-time explained-variance-ratio monitoring.
/// </para>
/// <para>
/// <b>Input:</b> <see cref="Vector{T}"/> or <see cref="Matrix{T}"/> of <see cref="double"/>
/// (matrix rows processed sequentially).
/// </para>
/// <para>
/// <b>Output:</b> <see cref="Vector{T}"/> of <see cref="double"/> — the k-dimensional projection.
/// </para>
/// </remarks>
/// <example>
/// JSON configuration:
/// <code>
/// {
///   "MyPCA": {
///     "Type": "OnlinePCA",
///     "Inputs": [ "SlidingWindow1" ],
///     "Params": [ 2, 0.2, 100, 500 ],
///     "DesiredRate": 100,
///     "Path": "output/pca_log"
///   }
/// }
/// </code>
/// <para>
/// <b>Params:</b>
/// <list type="table">
///   <listheader>
///     <term>Index</term>
///     <description>Description</description>
///   </listheader>
///   <item>
///     <term>0</term>
///     <description><c>k</c> (int) — Number of principal components (2 or 3). Default: <c>2</c>.</description>
///   </item>
///   <item>
///     <term>1</term>
///     <description><c>eta0</c> (double) — Initial learning rate η₀. Default: <c>0.2</c>.</description>
///   </item>
///   <item>
///     <term>2</term>
///     <description><c>reorthEvery</c> (int) — QR orthonormalisation frequency in samples. Default: <c>100</c>.</description>
///   </item>
///   <item>
///     <term>3</term>
///     <description><c>minStableCount</c> (int) — Minimum samples before model is stable. Default: <c>500</c>.</description>
///   </item>
/// </list>
/// </para>
/// <para>
/// <b>Path:</b> Optional. If provided, CSV logs of projected outputs are written to this directory.
/// </para>
/// </example>
public partial class OnlinePCA : BaseBlock
{
    #region Private Fields

    /// <summary>Number of principal components to extract.</summary>
    private readonly int _k;

    /// <summary>Frequency of QR orthonormalisation (every N samples).</summary>
    private readonly int _reorthEvery;

    /// <summary>Initial learning rate η₀. Actual rate decays as η₀/√n.</summary>
    private readonly double _eta0;

    /// <summary>Minimum samples before considering model stable.</summary>
    private readonly int _minStableCount;

    /// <summary>Input dimensionality (set on first sample).</summary>
    private int _d;

    /// <summary>Total number of processed samples.</summary>
    private long _n;

    /// <summary>Whether model has reached stable state.</summary>
    private bool _isStable;

    /// <summary>Running mean estimate μ (d-dimensional), updated via Welford's algorithm.</summary>
    private Vector<double> _mean = DenseVector.Create(0, 0.0);

    /// <summary>Loadings matrix W (d × k). Columns are the principal component directions.</summary>
    private Matrix<double> _W = DenseMatrix.Create(0, 0, 0.0);

    /// <summary>Previous PC1 direction for sign correction.</summary>
    private Vector<double>? _prevPC1;

    /// <summary>Previous PC2 direction for sign correction.</summary>
    private Vector<double>? _prevPC2;

    /// <summary>Previous PC3 direction for sign correction.</summary>
    private Vector<double>? _prevPC3;

    /// <summary>EMA of total input variance (‖x − μ‖²).</summary>
    private double _totalVarEma;

    /// <summary>EMA of per-component variance (y² per component).</summary>
    private Vector<double> _pcVarEma;

    /// <summary>EMA smoothing factor α (0 &lt; α &lt; 1).</summary>
    private readonly double _emaAlpha = 0.02;

    /// <summary>Preallocated centred input vector.</summary>
    private Vector<double>? _xc;

    /// <summary>Preallocated projected output vector.</summary>
    private Vector<double>? _y;

    /// <summary>Preallocated outer product matrix y·yᵀ (k × k).</summary>
    private Matrix<double>? _yyT;

    /// <summary>Optional CSV dumper for logging projections.</summary>

    #endregion

    #region Public Properties

    /// <summary>
    /// Gets the loadings matrix W (d × k). Columns are the principal component directions.
    /// </summary>
    public Matrix<double> Loadings => _W;

    /// <summary>
    /// Gets the EMA-estimated variance captured by each principal component.
    /// </summary>
    public Vector<double> ExplainedVariance => (_pcVarEma.Count == _k) ? _pcVarEma : DenseVector.Create(_k, 0.0);

    /// <summary>
    /// Gets the EMA-estimated total input variance.
    /// </summary>
    public double TotalVariance => _totalVarEma;

    /// <summary>Gets the total number of samples processed.</summary>
    public long SampleCount => _n;

    /// <summary>Gets whether the model has reached a stable state.</summary>
    public bool IsStable => _isStable;

    /// <summary>Gets the number of principal components (k).</summary>
    public int ComponentCount => _k;

    /// <summary>Gets the current mean estimate μ.</summary>
    public Vector<double> Mean => _mean;

    /// <summary>
    /// Gets the ratio of variance explained by each component relative to total variance.
    /// </summary>
    /// <remarks>
    /// Each element is <c>Var(PCᵢ) / TotalVar</c>. Returns zeros if total variance is negligible.
    /// </remarks>
    public Vector<double> ExplainedVarianceRatio
    {
        get
        {
            if (_totalVarEma < 1e-12) return DenseVector.Create(_k, 0.0);
            var ev = ExplainedVariance;
            return ev / _totalVarEma;
        }
    }

    #endregion

    #region Constructor

    /// <summary>
    /// Initializes a new <see cref="OnlinePCA"/> instance.
    /// </summary>
    /// <param name="name">Display name of the block.</param>
    /// <param name="desiredRate">Desired processing rate in Hz. 0 = inherited from upstream.</param>
    /// <param name="k">Number of principal components (must be 2 or 3 for visualisation).</param>
    /// <param name="eta0">Initial learning rate η₀. Decays as η₀/√n.</param>
    /// <param name="reorthEvery">QR orthonormalisation frequency in samples.</param>
    /// <param name="minStableCount">Minimum samples before the model is considered stable.</param>
    /// <param name="dumper">Optional CSV dumper for logging projected outputs.</param>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when <paramref name="k"/> is not 2 or 3.</exception>
    public OnlinePCA(
        string name,
        double desiredRate = 0,
        int k = 2,
        double eta0 = 0.2,
        int reorthEvery = 100,
        int minStableCount = 500)
        : base(name, desiredRate)
    {
        if (k < 2 || k > 3)
            throw new ArgumentOutOfRangeException(nameof(k), "k must be 2 or 3 for visualization");

        _k = k;
        _eta0 = eta0;
        _reorthEvery = Math.Max(1, reorthEvery);
        _minStableCount = Math.Max(1, minStableCount);

        _pcVarEma = DenseVector.Create(_k, 1e-9);

        Console.WriteLine($"[OnlinePCA '{Name}'] Initialized (k={k}, eta0={eta0})");
    }

    #endregion

    #region Factory Method

    /// <summary>Recordings from this block are named <c>&lt;block&gt;_projection.csv</c>.</summary>
    protected override string DumpFilePrefix => $"{Name}_projection";

    /// <summary>
    /// Creates and configures an <see cref="OnlinePCA"/> instance from a JSON model definition.
    /// </summary>
    /// <param name="sp">Service provider for dependency injection.</param>
    /// <param name="m">
    /// The JSON model. See class-level <c>&lt;example&gt;</c> for expected Params layout.
    /// </param>
    /// <returns>A fully configured <see cref="OnlinePCA"/> instance.</returns>
    public static OnlinePCA ConfigureInput(IServiceProvider sp, JsonModel m)
    {
        var name = m.Name ?? "OnlinePCA";
        var rate = m.DesiredRate ?? 0;

        int k = TryInt(m.Params, 0, 2);
        double eta0 = TryDouble(m.Params, 1, 0.2);
        int reorth = TryInt(m.Params, 2, 100);
        int minStable = TryInt(m.Params, 3, 500);

        var block = new OnlinePCA(name, rate, k, eta0, reorth, minStable);
        return block;
    }

    /// <summary>
    /// Exports the block's parameters in the exact order <see cref="ConfigureInput"/> reads them,
    /// so a saved graph reloads with the same configuration. <see cref="SupervisedPCA"/> inherits
    /// this override because its own <c>ConfigureInput</c> reads the same four values in the same order.
    /// </summary>
    /// <returns>k, η₀, reorthonormalisation interval and minimum stable count.</returns>
    protected override IReadOnlyList<object>? GetJsonParams()
        => new List<object> { _k, _eta0, _reorthEvery, _minStableCount };

    #endregion

    #region Data Reception

    /// <summary>
    /// Handles incoming data from the pipeline.
    /// </summary>
    /// <param name="sender">The upstream block.</param>
    /// <param name="data">
    /// <see cref="Vector{T}"/> (single observation) or <see cref="Matrix{T}"/> (rows processed sequentially).
    /// </param>
    protected override void OnReceive(object sender, object data)
    {
        if (data is null)
            return;

        switch (data)
        {
            case Vector<double> v:
                Process(v);
                return;

            case Matrix<double> M:
                for (int r = 0; r < M.RowCount; r++)
                    Process(M.Row(r));
                return;
        }
    }

    #endregion

    #region Initialisation

    /// <summary>
    /// Ensures all internal state is properly initialised for the given input dimension.
    /// </summary>
    /// <param name="d">Input vector dimensionality.</param>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when <paramref name="d"/> ≤ 0.</exception>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void EnsureInit(int d)
    {
        if (d <= 0) throw new ArgumentOutOfRangeException(nameof(d), "Input dimension must be > 0.");

        bool needInit =
            _d == 0 ||
            _d != d ||
            _W.RowCount != d ||
            _W.ColumnCount != _k ||
            _xc is null ||
            _y is null ||
            _yyT is null ||
            _pcVarEma is null ||
            _mean is null;

        if (!needInit) return;

        _d = d;
        _n = 0;
        _isStable = false;

        _mean = DenseVector.Create(d, 0.0);
        _W = DenseMatrix.Build.Random(d, _k);
        Orthonormalize(ref _W);

        _xc = DenseVector.Create(d, 0.0);
        _y = DenseVector.Create(_k, 0.0);
        _yyT = DenseMatrix.Create(_k, _k, 0.0);

        _pcVarEma = DenseVector.Create(_k, 1e-9);
        _totalVarEma = 1e-9;

        _prevPC1 = null;
        _prevPC2 = null;
        _prevPC3 = null;

        Console.WriteLine($"[OnlinePCA '{Name}'] Initialized/Reinitialized with d={d}, k={_k}");
    }

    #endregion

    #region Core Processing

    /// <summary>
    /// Processes a single input vector through the online PCA pipeline.
    /// </summary>
    /// <param name="x">Input observation vector (d-dimensional).</param>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="x"/> is null.</exception>
    /// <remarks>
    /// <para>Processing steps:</para>
    /// <list type="number">
    ///   <item><description>Update running mean (Welford's algorithm).</description></item>
    ///   <item><description>Centre the input: xc = x − μ.</description></item>
    ///   <item><description>Project: y = Wᵀ · xc.</description></item>
    ///   <item><description>Update EMA variance estimates.</description></item>
    ///   <item><description>Oja update: W ← W + η·xc·yᵀ − η·W·yyᵀ.</description></item>
    ///   <item><description>Periodic: QR orthonormalise W and correct PC signs.</description></item>
    ///   <item><description>Publish the k-dimensional projection.</description></item>
    /// </list>
    /// </remarks>
    protected virtual void Process(Vector<double> x)
    {
        if (x is null) throw new ArgumentNullException(nameof(x));

        EnsureInit(x.Count);

        if (x.Count != _d) return;

        _n++;

        if (!_isStable && _n >= _minStableCount)
        {
            _isStable = true;
            Console.WriteLine($"[OnlinePCA '{Name}'] Model stabilized after {_n} samples");
            Console.WriteLine($"[OnlinePCA '{Name}'] Explained variance ratios: {ExplainedVarianceRatio}");
        }

        // ── 1. Update running mean (Welford) ──────────────────────────────
        var delta = x - _mean;
        _mean += delta / _n;

        // ── 2. Centre ──────────────────────────────────────────────────────
        var xc = _xc!;
        xc.SetSubVector(0, _d, x - _mean);

        // ── 3. Project ─────────────────────────────────────────────────────
        var y = _y!;
        _W.TransposeThisAndMultiply(xc, y);

        // ── 4. Variance tracking (EMA) ─────────────────────────────────────
        var y2 = y.PointwiseMultiply(y);
        _pcVarEma *= (1 - _emaAlpha);
        _pcVarEma += _emaAlpha * y2;

        var xcNorm2 = xc.DotProduct(xc);

        // Seed the EMA from the first real sample so it starts at the
        // correct order of magnitude instead of climbing from 1e-9.
        if (_n == 1 && xcNorm2 > 1e-20)
            _totalVarEma = xcNorm2;
        else
            _totalVarEma = (1 - _emaAlpha) * _totalVarEma + _emaAlpha * xcNorm2;

        // ── 5. Oja update ───────────────────────────────────────────────
        {
            var eta = _eta0 / Math.Sqrt(_n);

            var yyT = _yyT!;
            OuterProductTo(yyT, y);

            // Hebbian term: W += η · xc · yᵀ
            for (int i = 0; i < _d; i++)
            {
                var xi = xc[i];
                for (int j = 0; j < _k; j++)
                    _W[i, j] += eta * xi * y[j];
            }

            // Anti-Hebbian term: W -= η · W · yyᵀ
            var Wyyt = _W * yyT;
            _W -= Wyyt * eta;
        }

        // ── 6. Periodic orthonormalisation + sign correction ───────────────
        if (_n % _reorthEvery == 0)
        {
            Orthonormalize(ref _W);
            CorrectPCSigns();
        }

        // ── 7. Publish (BaseBlock logs it on the way out when recording is on) ──
        PublishProjection(y.Clone());
    }

    #endregion

    #region Publishing

    /// <summary>
    /// Publishes the principal component projection to downstream subscribers.
    /// </summary>
    /// <param name="projection">The k-dimensional projection vector.</param>
    /// <remarks>
    /// Protected virtual to allow interception or modification in derived classes
    /// (e.g., <see cref="SupervisedPCA"/>).
    /// </remarks>
    protected virtual void PublishProjection(Vector<double> projection)
    {
        Publish(projection);
    }

    #endregion

    #region Transform Methods

    /// <summary>
    /// Projects a single observation into the PCA subspace using the current loadings.
    /// </summary>
    /// <param name="x">Input observation vector (d-dimensional).</param>
    /// <returns>The k-dimensional PCA projection.</returns>
    /// <exception cref="InvalidOperationException">Thrown when PCA has not been initialised.</exception>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="x"/> is null.</exception>
    /// <exception cref="InvalidOperationException">Thrown when dimension does not match.</exception>
    /// <remarks>Does not update any internal state. Use for inference after training.</remarks>
    public Vector<double> Project(Vector<double> x)
    {
        if (_d == 0)
            throw new InvalidOperationException("PCA not initialized yet.");
        if (x is null) throw new ArgumentNullException(nameof(x));
        if (x.Count != _d)
            throw new InvalidOperationException($"Dimension mismatch: expected {_d}, got {x.Count}");

        var xc = x - _mean;
        return _W.TransposeThisAndMultiply(xc);
    }

    /// <summary>
    /// Reconstructs an approximation of the original signal from principal component projections.
    /// </summary>
    /// <param name="y">Principal component vector (k-dimensional).</param>
    /// <returns>Reconstructed observation vector (d-dimensional).</returns>
    /// <exception cref="InvalidOperationException">Thrown when PCA has not been initialised.</exception>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="y"/> is null.</exception>
    /// <exception cref="InvalidOperationException">Thrown when dimension does not match k.</exception>
    /// <remarks>
    /// Applies <c>x̂ = W · y + μ</c>. If k &lt; d, information is lost and perfect reconstruction
    /// is not possible.
    /// </remarks>
    public Vector<double> Reconstruct(Vector<double> y)
    {
        if (_d == 0)
            throw new InvalidOperationException("PCA not initialized yet.");
        if (y is null) throw new ArgumentNullException(nameof(y));
        if (y.Count != _k)
            throw new InvalidOperationException($"Projection dimension mismatch: expected {_k}, got {y.Count}");

        return _W * y + _mean;
    }

    #endregion

    #region Reset

    /// <summary>
    /// Resets all learned parameters to initial state.
    /// </summary>
    /// <remarks>
    /// Clears mean, loadings, variance estimates, and sign-correction history.
    /// The next <see cref="Process"/> call will trigger full re-initialisation.
    /// </remarks>
    public void Reset()
    {
        _d = 0;
        _n = 0;
        _isStable = false;

        _mean = DenseVector.Create(0, 0.0);
        _W = DenseMatrix.Create(0, 0, 0.0);

        _xc = null;
        _y = null;
        _yyT = null;

        _pcVarEma = DenseVector.Create(_k, 1e-9);
        _totalVarEma = 1e-9;

        _prevPC1 = null;
        _prevPC2 = null;
        _prevPC3 = null;

        Console.WriteLine($"[OnlinePCA '{Name}'] Model reset");
    }

    #endregion

    #region Sign Correction

    /// <summary>
    /// Corrects the sign of principal component directions for temporal consistency.
    /// </summary>
    /// <remarks>
    /// PCA eigenvectors have inherent sign ambiguity. This method tracks the previous direction
    /// of each component and flips the sign if the dot product with the previous direction is negative.
    /// Only active after <see cref="IsStable"/> becomes true.
    /// </remarks>
    private void CorrectPCSigns()
    {
        if (!_isStable) return;

        var pc1 = _W.Column(0);
        if (_prevPC1 != null && pc1.DotProduct(_prevPC1) < 0)
        {
            _W.SetColumn(0, -pc1);
            pc1 = -pc1;
        }
        _prevPC1 = pc1.Clone();

        var pc2 = _W.Column(1);
        if (_prevPC2 != null && pc2.DotProduct(_prevPC2) < 0)
        {
            _W.SetColumn(1, -pc2);
            pc2 = -pc2;
        }
        _prevPC2 = pc2.Clone();

        if (_k >= 3)
        {
            var pc3 = _W.Column(2);
            if (_prevPC3 != null && pc3.DotProduct(_prevPC3) < 0)
            {
                _W.SetColumn(2, -pc3);
                pc3 = -pc3;
            }
            _prevPC3 = pc3.Clone();
        }
    }

    #endregion

    #region Orthonormalisation

    /// <summary>
    /// Orthonormalises the columns of <paramref name="W"/> using QR decomposition.
    /// </summary>
    /// <param name="W">Matrix to orthonormalise (modified in place).</param>
    private static void Orthonormalize(ref Matrix<double> W)
    {
        var qr = W.QR(QRMethod.Full);
        var Q = qr.Q;

        for (int i = 0; i < W.RowCount; i++)
            for (int j = 0; j < W.ColumnCount; j++)
                W[i, j] = Q[i, j];
    }

    /// <summary>
    /// Computes the outer product y·yᵀ and writes the result into <paramref name="dst"/>.
    /// </summary>
    /// <param name="dst">Destination matrix (k × k). Overwritten in place.</param>
    /// <param name="v">Source vector (k-dimensional).</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void OuterProductTo(Matrix<double> dst, Vector<double> v)
    {
        for (int i = 0; i < v.Count; i++)
        {
            var vi = v[i];
            for (int j = 0; j < v.Count; j++)
                dst[i, j] = vi * v[j];
        }
    }

    #endregion

    #region Helper Methods

    /// <summary>
    /// Attempts to parse an integer from a parameter list.
    /// </summary>
    /// <param name="p">Parameter list.</param>
    /// <param name="idx">Index to read.</param>
    /// <param name="def">Default value if parsing fails.</param>
    /// <returns>Parsed integer or default.</returns>
    private static int TryInt(IReadOnlyList<object>? p, int idx, int def)
    {
        if (p == null || idx >= p.Count) return def;

        return p[idx] switch
        {
            int i => i,
            double d => (int)Math.Round(d),
            JsonElement je when je.ValueKind == JsonValueKind.Number => je.GetInt32(),
            JsonElement je when je.ValueKind == JsonValueKind.String
                              && int.TryParse(je.GetString(), out var v) => v,
            _ => def
        };
    }

    /// <summary>
    /// Attempts to parse a double from a parameter list.
    /// </summary>
    /// <param name="p">Parameter list.</param>
    /// <param name="idx">Index to read.</param>
    /// <param name="def">Default value if parsing fails.</param>
    /// <returns>Parsed double or default.</returns>
    private static double TryDouble(IReadOnlyList<object>? p, int idx, double def)
    {
        if (p == null || idx >= p.Count) return def;

        return p[idx] switch
        {
            double d => d,
            float f => f,
            int i => i,
            JsonElement je when je.ValueKind == JsonValueKind.Number => je.GetDouble(),
            JsonElement je when je.ValueKind == JsonValueKind.String
                              && double.TryParse(je.GetString(), NumberStyles.Float,
                                  CultureInfo.InvariantCulture, out var v) => v,
            _ => def
        };
    }

    #endregion

    #region Disposal

    public override void Dispose()
    {
        base.Dispose();
    }

    #endregion
}