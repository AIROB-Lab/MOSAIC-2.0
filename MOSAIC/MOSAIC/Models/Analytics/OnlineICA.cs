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
/// Online Independent Component Analysis (ICA) using incremental FastICA learning.
/// </summary>
/// <remarks>
/// <para>
/// <b>Overview:</b><br/>
/// ICA is a computational technique for separating a multivariate signal into additive, 
/// statistically independent non-Gaussian source signals. This implementation provides 
/// real-time blind source separation using an online (incremental) learning approach.
/// </para>
/// 
/// <para>
/// <b>Mathematical Model:</b><br/>
/// ICA assumes the observed data follows the linear mixing model:
/// <code>
///     x = A · s
/// </code>
/// where:
/// <list type="bullet">
///     <item><description><c>x</c> is the observed d-dimensional signal vector</description></item>
///     <item><description><c>s</c> is the k-dimensional vector of independent source signals</description></item>
///     <item><description><c>A</c> is the unknown d×k mixing matrix</description></item>
/// </list>
/// The goal is to find the unmixing matrix <c>W</c> such that:
/// <code>
///     y = W · x ≈ s
/// </code>
/// where <c>y</c> contains the estimated independent components.
/// </para>
/// 
/// <para>
/// <b>Algorithm Pipeline:</b>
/// <list type="number">
///     <item><description><b>Centering:</b> Remove mean: x_centered = x - μ</description></item>
///     <item><description><b>Whitening:</b> Decorrelate and normalize variance: z = W_white · x_centered</description></item>
///     <item><description><b>ICA Rotation:</b> Find rotation maximizing independence: y = W · z</description></item>
/// </list>
/// </para>
/// 
/// <para>
/// <b>Whitening (PCA-based):</b><br/>
/// Whitening transforms data to have identity covariance. Given covariance matrix C with 
/// eigendecomposition C = V·D·V^T, the whitening matrix is:
/// <code>
///     W_white = D^(-1/2) · V^T
/// </code>
/// This ensures E[z·z^T] = I, simplifying the ICA optimization to finding an orthogonal rotation.
/// </para>
/// 
/// <para>
/// <b>FastICA Update Rule:</b><br/>
/// The natural gradient update for the unmixing matrix W is:
/// <code>
///     w_i ← w_i + η · (z · g(w_i^T · z) - g'(w_i^T · z) · w_i)
/// </code>
/// where:
/// <list type="bullet">
///     <item><description><c>g(y)</c> is the derivative of the contrast function G(y)</description></item>
///     <item><description><c>g'(y)</c> is the second derivative of G(y)</description></item>
///     <item><description><c>η</c> is the learning rate</description></item>
/// </list>
/// </para>
/// 
/// <para>
/// <b>Contrast Functions:</b><br/>
/// The contrast function G(y) measures non-Gaussianity. Available options:
/// <list type="table">
///     <listheader>
///         <term>Function</term>
///         <description>G(y), g(y), g'(y)</description>
///     </listheader>
///     <item>
///         <term>LogCosh (default, robust)</term>
///         <description>G(y) = log(cosh(y)), g(y) = tanh(y), g'(y) = 1 - tanh²(y)</description>
///     </item>
///     <item>
///         <term>Exp (super-Gaussian)</term>
///         <description>G(y) = -exp(-y²/2), g(y) = y·exp(-y²/2), g'(y) = (1-y²)·exp(-y²/2)</description>
///     </item>
///     <item>
///         <term>Cube (sub-Gaussian, fast)</term>
///         <description>G(y) = y⁴/4, g(y) = y³, g'(y) = 3y²</description>
///     </item>
/// </list>
/// </para>
/// 
/// <para>
/// <b>Online Covariance Estimation:</b><br/>
/// Uses Welford's numerically stable algorithm for incremental mean and covariance:
/// <code>
///     δ = x - μ_old
///     μ_new = μ_old + δ/n
///     δ' = x - μ_new
///     M2 = M2 + outer(δ, δ')
///     C = M2 / (n-1)
/// </code>
/// </para>
/// 
/// <para>
/// <b>Independence Metrics:</b>
/// <list type="bullet">
///     <item>
///         <description>
///             <b>Excess Kurtosis:</b> κ = E[y⁴] - 3. Zero for Gaussian, positive for 
///             super-Gaussian (heavy tails), negative for sub-Gaussian (light tails).
///         </description>
///     </item>
///     <item>
///         <description>
///             <b>Negentropy:</b> J(y) ≈ [E{G(y)} - E{G(ν)}]² where ν ~ N(0,1).
///             Always non-negative, zero only for Gaussian distributions.
///         </description>
///     </item>
/// </list>
/// </para>
/// 
/// <para>
/// <b>Numerical Stability Features:</b>
/// <list type="bullet">
///     <item><description>Symmetric orthonormalization: W ← W·(W·W^T)^(-1/2)</description></item>
///     <item><description>Learning rate warm-up and decay schedule</description></item>
///     <item><description>Gradient clipping to prevent exploding updates</description></item>
///     <item><description>Eigenvalue validation before whitening updates</description></item>
///     <item><description>Sign correction for component stability</description></item>
/// </list>
/// </para>
/// 
/// <para>
/// <b>References:</b>
/// <list type="bullet">
///     <item><description>Hyvärinen, A. (1999). Fast and Robust Fixed-Point Algorithms for Independent Component Analysis. IEEE Trans. Neural Networks.</description></item>
///     <item><description>Hyvärinen, A., Karhunen, J., Oja, E. (2001). Independent Component Analysis. Wiley.</description></item>
///     <item><description>Welford, B.P. (1962). Note on a Method for Calculating Corrected Sums of Squares and Products. Technometrics.</description></item>
/// </list>
/// </para>
/// </remarks>
/// <example>
/// JSON configuration:
/// <code>
/// {
///   "EMG_ICA": {
///     "Type": "OnlineICA",
///     "Inputs": [ "SlidingWindow1" ],
///     "Params": [ 3, 0.1, 50, 1000, 0, 500, 1, 0, 0.005 ],
///     "DesiredRate": 1000,
///     "Path": "output/emg_ica"
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
///     <description><c>k</c> (int) — Number of independent components to extract. Default: <c>2</c>.</description>
///   </item>
///   <item>
///     <term>1</term>
///     <description><c>eta0</c> (double) — Initial learning rate η₀. Range: 0.01–0.2. Default: <c>0.1</c>.</description>
///   </item>
///   <item>
///     <term>2</term>
///     <description><c>reorthEvery</c> (int) — Orthonormalization frequency in samples. Default: <c>50</c>.</description>
///   </item>
///   <item>
///     <term>3</term>
///     <description><c>minStableCount</c> (int) — Minimum samples before model is stable. Default: <c>1000</c>.</description>
///   </item>
///   <item>
///     <term>4</term>
///     <description><c>contrastFunction</c> (double) — Contrast function: <c>0</c> = LogCosh, <c>1</c> = Exp, <c>2</c> = Cube. Default: <c>0</c>.</description>
///   </item>
///   <item>
///     <term>5</term>
///     <description><c>warmupSamples</c> (int) — Warm-up samples (whitening only). Set <c>0</c> to disable. Default: <c>500</c>.</description>
///   </item>
///   <item>
///     <term>6</term>
///     <description><c>freezeWhiteningAfterWarmup</c> (int) — <c>1</c> = freeze whitening after warm-up, <c>0</c> = keep updating. Default: <c>1</c>.</description>
///   </item>
///   <item>
///     <term>7</term>
///     <description><c>adaptiveWhitening</c> (int) — <c>1</c> = enable adaptive whitening for non-stationary signals, <c>0</c> = off. Default: <c>0</c>.</description>
///   </item>
///   <item>
///     <term>8</term>
///     <description><c>covarianceDecay</c> (double) — EWMA decay for adaptive covariance. Range: 0.0001–0.5. Default: <c>0.005</c>.</description>
///   </item>
/// </list>
/// </para>
/// <para>
/// <b>Path:</b> Optional. If provided, CSV logs of projected outputs are written to this directory.
/// </para>
/// </example>
public partial class OnlineICA : BaseBlock
{
    #region Private Fields
    
    /// <summary>Number of independent components to extract.</summary>
    private readonly int _k;
    
    /// <summary>Frequency of orthonormalization (every N samples).</summary>
    private readonly int _reorthEvery;
    
    /// <summary>Initial learning rate η₀.</summary>
    private readonly double _eta0;
    
    /// <summary>Minimum samples before considering model stable.</summary>
    private readonly int _minStableCount;
    
    /// <summary>Contrast function selector: 0=logcosh, 1=exp, 2=cube.</summary>
    private readonly double _contrastFunction;
    
    /// <summary>Number of samples for warm-up phase (whitening estimation only).</summary>
    private readonly int _warmupSamples;
    
    /// <summary>Whether whitening is frozen after warm-up.</summary>
    private readonly bool _freezeWhiteningAfterWarmup;
    
    /// <summary>Adaptive whitening mode for non-stationary signals (EMG, ultrasound).</summary>
    private readonly bool _adaptiveWhitening;
    
    /// <summary>Exponential decay factor for adaptive covariance (0 = no decay, 1 = instant).</summary>
    private readonly double _covarianceDecay;
    
    /// <summary>Input dimensionality (set on first sample).</summary>
    private int _d;
    
    /// <summary>Total number of processed samples.</summary>
    private long _n;
    
    /// <summary>Whether model has reached stable state.</summary>
    private bool _isStable;
    
    /// <summary>Whether warm-up phase is complete.</summary>
    private bool _warmupComplete;
    
    /// <summary>Previous whitening matrix for detecting significant changes.</summary>
    private Matrix<double>? _prevWhiteningMatrix;
    
    /// <summary>Exponentially weighted covariance for drift detection.</summary>
    private Matrix<double>? _ewmaCov;
    
    /// <summary>Baseline covariance from warm-up for drift detection.</summary>
    private Matrix<double>? _baselineCov;
    
    /// <summary>Current drift metric (Frobenius norm of covariance change).</summary>
    private double _driftMetric;
    
    /// <summary>Threshold for triggering adaptation.</summary>
    private readonly double _driftThreshold = 0.2;
    
    /// <summary>Running mean estimate μ (d-dimensional).</summary>
    private Vector<double> _mean = DenseVector.Create(0, 0.0);
    
    /// <summary>
    /// Sum of squared deviations M₂ for Welford's algorithm.
    /// Used to compute covariance: C = M₂ / (n-1).
    /// </summary>
    private Matrix<double> _M2 = DenseMatrix.Create(0, 0, 0.0);
    
    /// <summary>Covariance matrix estimate C (d×d).</summary>
    private Matrix<double> _C = DenseMatrix.Create(0, 0, 0.0);
    
    /// <summary>
    /// Whitening matrix W_white (k×d).
    /// Transforms centered data to whitened space: z = W_white · (x - μ).
    /// Computed as D^(-1/2) · V^T from eigendecomposition of C.
    /// </summary>
    private Matrix<double> _whiteningMatrix = DenseMatrix.Create(0, 0, 0.0);
    
    /// <summary>
    /// Dewhitening matrix (d×k) for inverse transformation.
    /// Computed as V · D^(1/2) using top k eigenvectors.
    /// </summary>
    private Matrix<double> _dewhiteningMatrix = DenseMatrix.Create(0, 0, 0.0);
    
    /// <summary>
    /// Throttles for the whitening diagnostics below. They are reached from Process() on the data
    /// thread, so an unguarded failure would report itself at sample rate.
    /// </summary>
    private LogRate _smallEigenvalueRate = new(TimeSpan.FromSeconds(2));
    private LogRate _invalidWhiteningRate = new(TimeSpan.FromSeconds(2));
    private LogRate _whiteningErrorRate = new(TimeSpan.FromSeconds(2));
    
    /// <summary>
    /// ICA unmixing/rotation matrix W (k×k).
    /// Operates in whitened space: y = W · z.
    /// </summary>
    private Matrix<double> _W = DenseMatrix.Create(0, 0, 0.0);
    
    /// <summary>Exponential moving average of excess kurtosis per component.</summary>
    private Vector<double> _kurtosisEma;
    
    /// <summary>EMA of y^4 for proper kurtosis calculation.</summary>
    private Vector<double> _y4Ema;
    
    /// <summary>EMA of y^2 for proper kurtosis calculation.</summary>
    private Vector<double> _y2Ema;
    
    /// <summary>EMA of G(y) for proper negentropy calculation.</summary>
    private Vector<double> _gEma;
    
    /// <summary>Exponential moving average of negentropy per component.</summary>
    private Vector<double> _negentropyEma;
    
    /// <summary>Previous IC directions for sign correction (generic for any k).</summary>
    private Vector<double>[]? _prevICs;
    
    /// <summary>EMA smoothing factor α (0 &lt; α &lt; 1).</summary>
    private readonly double _emaAlpha = 0.02;
    
    /// <summary>Preallocated whitened input vector.</summary>
    private Vector<double>? _xWhitened;
    
    /// <summary>Preallocated output (independent components) vector.</summary>
    private Vector<double>? _y;
    
    /// <summary>Preallocated contrast function values g(y).</summary>
    private Vector<double>? _gY;
    
    /// <summary>Preallocated contrast derivative values g'(y).</summary>
    private Vector<double>? _gPrimeY;
    
    /// <summary>Optional CSV dumper for logging projections.</summary>
    
    #endregion

    #region Public Properties
    
    /// <summary>
    /// Gets the full unmixing matrix A⁻¹ = W · W_white (k×d).
    /// </summary>
    /// <remarks>
    /// This matrix directly transforms centered input to independent components:
    /// <code>y = UnmixingMatrix · (x - Mean)</code>
    /// </remarks>
    /// <value>The combined whitening and ICA rotation matrix.</value>
    public Matrix<double> UnmixingMatrix => _W * _whiteningMatrix;
    
    /// <summary>
    /// Gets the whitening matrix W_white (k×d).
    /// </summary>
    /// <remarks>
    /// Transforms centered data to have identity covariance in k-dimensional space.
    /// Computed from eigendecomposition of covariance: W_white = D^(-1/2) · V^T.
    /// </remarks>
    /// <value>The whitening transformation matrix.</value>
    public Matrix<double> WhiteningMatrix => _whiteningMatrix;
    
    /// <summary>
    /// Gets the estimated excess kurtosis for each independent component.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Excess kurtosis is properly computed as:
    /// <code>κ = E[y⁴] / E[y²]² - 3</code>
    /// This normalization ensures scale-invariance.
    /// </para>
    /// <para>
    /// Interpretation:
    /// <list type="bullet">
    ///     <item><description>κ = 0: Gaussian (mesokurtic)</description></item>
    ///     <item><description>κ &gt; 0: Super-Gaussian / leptokurtic (heavy tails, e.g., Laplace)</description></item>
    ///     <item><description>κ &lt; 0: Sub-Gaussian / platykurtic (light tails, e.g., uniform)</description></item>
    /// </list>
    /// </para>
    /// <para>Values are smoothed using exponential moving average.</para>
    /// </remarks>
    /// <value>Vector of excess kurtosis values, one per component.</value>
    public Vector<double> Kurtosis
    {
        get
        {
            if (_y4Ema == null || _y2Ema == null || _y4Ema.Count != _k)
                return DenseVector.Create(_k, 0.0);
            
            var result = DenseVector.Create(_k, 0.0);
            for (int i = 0; i < _k; i++)
            {
                double y2 = _y2Ema[i];
                if (y2 > 1e-10)
                {
                    // Proper kurtosis: E[y^4] / E[y^2]^2 - 3
                    result[i] = _y4Ema[i] / (y2 * y2) - 3.0;
                }
            }
            return result;
        }
    }
    
    /// <summary>
    /// Gets the estimated negentropy for each independent component.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Negentropy measures the distance from Gaussianity:
    /// <code>J(y) ≈ [E{G(y)} - E{G(ν)}]²</code>
    /// where ν ~ N(0,1) is a standard normal reference.
    /// </para>
    /// <para>
    /// Properties:
    /// <list type="bullet">
    ///     <item><description>J(y) ≥ 0 always</description></item>
    ///     <item><description>J(y) = 0 if and only if y is Gaussian</description></item>
    ///     <item><description>Higher values indicate more non-Gaussian (more independent) components</description></item>
    /// </list>
    /// </para>
    /// <para>Values are smoothed using exponential moving average.</para>
    /// </remarks>
    /// <value>Vector of negentropy values, one per component.</value>
    public Vector<double> Negentropy => (_negentropyEma.Count == _k) ? _negentropyEma : DenseVector.Create(_k, 0.0);
    
    /// <summary>
    /// Gets the total number of samples processed.
    /// </summary>
    /// <value>Sample count since initialization or last reset.</value>
    public long SampleCount => _n;
    
    /// <summary>
    /// Gets whether the model has reached a stable state.
    /// </summary>
    /// <remarks>
    /// The model is considered stable after processing <c>minStableCount</c>
    /// samples. Sign correction is only applied after stability is reached.
    /// </remarks>
    /// <value><c>true</c> if stable; otherwise, <c>false</c>.</value>
    public bool IsStable => _isStable;
    
    /// <summary>
    /// Gets whether the warm-up phase is complete.
    /// </summary>
    /// <remarks>
    /// During warm-up, only mean/covariance/whitening are estimated. 
    /// ICA rotation (W) learning begins after warm-up completes.
    /// </remarks>
    /// <value><c>true</c> if warm-up is complete; otherwise, <c>false</c>.</value>
    public bool WarmupComplete => _warmupComplete;
    
    /// <summary>
    /// Gets the current signal drift metric.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Measures how much the signal statistics have drifted from the baseline
    /// established during warm-up. Computed as relative Frobenius norm:
    /// <code>drift = ||C_current - C_baseline||_F / ||C_baseline||_F</code>
    /// </para>
    /// <para>
    /// Useful for EMG/ultrasound applications to detect:
    /// <list type="bullet">
    ///     <item><description>Electrode movement or contact changes</description></item>
    ///     <item><description>Muscle fatigue affecting signal characteristics</description></item>
    ///     <item><description>Need for recalibration</description></item>
    /// </list>
    /// </para>
    /// </remarks>
    /// <value>Relative drift from baseline (0 = no drift, >0.2 = significant drift).</value>
    public double DriftMetric => _driftMetric;
    
    /// <summary>
    /// Gets the number of independent components (k).
    /// </summary>
    /// <value>The dimensionality of the output space.</value>
    public int ComponentCount => _k;
    
    /// <summary>
    /// Gets the current mean estimate μ.
    /// </summary>
    /// <remarks>
    /// Updated online using Welford's algorithm: μ_new = μ_old + (x - μ_old) / n.
    /// </remarks>
    /// <value>The d-dimensional mean vector.</value>
    public Vector<double> Mean => _mean;
    
    /// <summary>
    /// Gets the current covariance matrix estimate C.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Computed using Welford's numerically stable algorithm:
    /// <code>C = M₂ / (n - 1)</code>
    /// where M₂ accumulates outer products of deviations.
    /// </para>
    /// <para>Includes small diagonal regularization (1e-8) for numerical stability.</para>
    /// </remarks>
    /// <value>The d×d sample covariance matrix.</value>
    public Matrix<double> Covariance => _C;
    
    #endregion

    #region Constructor
    
    /// <summary>
    /// Initializes a new instance of the <see cref="OnlineICA"/> class.
    /// </summary>
    /// <param name="name">Unique identifier for this processing block.</param>
    /// <param name="desiredRate">Target processing rate in Hz (0 = unlimited).</param>
    /// <param name="k">
    /// Number of independent components to extract. 
    /// For complete source separation, use k = d (input dimension).
    /// For EMG: typically 2-4 components.
    /// </param>
    /// <param name="eta0">
    /// Initial learning rate η₀ ∈ (0, 1]. Typical values: 0.01-0.2.
    /// Higher values converge faster but may be unstable.
    /// </param>
    /// <param name="reorthEvery">
    /// Orthonormalization frequency. W is orthonormalized every N samples.
    /// Lower values improve stability but increase computation. Typical: 10-100.
    /// </param>
    /// <param name="minStableCount">
    /// Minimum samples before model is considered stable.
    /// Sign correction only activates after this threshold. Typical: 500-2000.
    /// </param>
    /// <param name="contrastFunction">
    /// Contrast function selector:
    /// <list type="bullet">
    ///     <item><description>0 = LogCosh (default, robust for most signals including EMG)</description></item>
    ///     <item><description>1 = Exp (good for super-Gaussian sources)</description></item>
    ///     <item><description>2 = Cube (fast, good for sub-Gaussian sources)</description></item>
    /// </list>
    /// </param>
    /// <param name="warmupSamples">
    /// Number of samples for warm-up phase. During warm-up, only whitening is 
    /// estimated; ICA rotation learning begins after. Typical: 200-1000.
    /// Set to 0 to disable warm-up.
    /// </param>
    /// <param name="freezeWhiteningAfterWarmup">
    /// If true, whitening matrix is frozen after warm-up phase.
    /// Set to false for adaptive whitening with non-stationary signals.
    /// </param>
    /// <param name="adaptiveWhitening">
    /// If true, enables adaptive whitening for non-stationary signals (EMG, ultrasound).
    /// Uses exponentially weighted covariance and smooth W transformation.
    /// </param>
    /// <param name="covarianceDecay">
    /// Decay factor for exponentially weighted covariance in adaptive mode.
    /// Range: 0.001-0.1. Lower = more stable, higher = faster adaptation.
    /// Typical for EMG at 1kHz: 0.001-0.01.
    /// </param>
    /// <param name="dumper">Optional CSV dumper for logging output projections.</param>
    /// <exception cref="ArgumentOutOfRangeException">
    /// Thrown when <paramref name="k"/> is less than 1.
    /// </exception>
    /// <example>
    /// <code>
    /// // Setup for EMG source separation with adaptive whitening
    /// var ica = new OnlineICA(
    ///     name: "EMG_ICA",
    ///     k: 3,
    ///     eta0: 0.1,
    ///     warmupSamples: 500,
    ///     freezeWhiteningAfterWarmup: false,
    ///     adaptiveWhitening: true,
    ///     covarianceDecay: 0.005
    /// );
    /// </code>
    /// </example>
    public OnlineICA(
        string name,
        double desiredRate = 0,
        int k = 2,
        double eta0 = 0.1,
        int reorthEvery = 50,
        int minStableCount = 1000,
        double contrastFunction = 0.0,
        int warmupSamples = 500,
        bool freezeWhiteningAfterWarmup = true,
        bool adaptiveWhitening = false,
        double covarianceDecay = 0.005)
        : base(name, desiredRate)
    {
        if (k < 1)
            throw new ArgumentOutOfRangeException(nameof(k), "k must be >= 1");

        _k = k;
        _eta0 = eta0;
        _reorthEvery = Math.Max(1, reorthEvery);
        _minStableCount = Math.Max(1, minStableCount);
        _contrastFunction = contrastFunction;
        _warmupSamples = Math.Max(0, warmupSamples);
        _freezeWhiteningAfterWarmup = freezeWhiteningAfterWarmup && !adaptiveWhitening;
        _adaptiveWhitening = adaptiveWhitening;
        _covarianceDecay = Math.Clamp(covarianceDecay, 0.0001, 0.5);

        _kurtosisEma = DenseVector.Create(_k, 0.0);
        _y4Ema = DenseVector.Create(_k, 0.0);
        _y2Ema = DenseVector.Create(_k, 1.0);
        _gEma = DenseVector.Create(_k, 0.0);
        _negentropyEma = DenseVector.Create(_k, 0.0);
        _prevICs = null;
        
        string contrast = contrastFunction switch
        {
            0 => "logcosh",
            1 => "exp",
            2 => "cube",
            _ => "logcosh"
        };
        
        string mode = adaptiveWhitening ? "adaptive" : (freezeWhiteningAfterWarmup ? "frozen" : "updating");
        Console.WriteLine($"[OnlineICA '{Name}'] Initialized (k={k}, eta0={eta0}, contrast={contrast}, warmup={warmupSamples}, whitening={mode})");
    }
    
    #endregion

    #region Factory Method
    
    /// <summary>Recordings from this block are named <c>&lt;block&gt;_projections.csv</c>.</summary>
    protected override string DumpFilePrefix => $"{Name}_projections";

    /// <summary>
    /// Creates an <see cref="OnlineICA"/> instance from JSON configuration.
    /// </summary>
    /// <param name="sp">Service provider for dependency injection.</param>
    /// <param name="m">
    /// JSON model containing configuration. Expected Params order:
    /// [0] k (int), [1] eta0 (double), [2] reorthEvery (int), 
    /// [3] minStableCount (int), [4] contrastFunction (double),
    /// [5] warmupSamples (int), [6] freezeWhiteningAfterWarmup (0/1),
    /// [7] adaptiveWhitening (0/1), [8] covarianceDecay (double).
    /// </param>
    /// <returns>Configured OnlineICA instance.</returns>
    /// <example>
    /// JSON configuration for EMG with adaptive whitening:
    /// <code>
    /// {
    ///   "Name": "EMG_ICA",
    ///   "DesiredRate": 1000,
    ///   "Params": [3, 0.1, 50, 1000, 0, 500, 0, 1, 0.005],
    ///   "Path": "output/emg_ica"
    /// }
    /// </code>
    /// </example>
    public static OnlineICA ConfigureInput(IServiceProvider sp, JsonModel m)
    {
        var name = m.Name ?? "OnlineICA";
        var rate = m.DesiredRate ?? 0;

        int k = TryInt(m.Params, 0, 2);
        double eta0 = TryDouble(m.Params, 1, 0.1);
        int reorth = TryInt(m.Params, 2, 50);
        int minStable = TryInt(m.Params, 3, 1000);
        double contrast = TryDouble(m.Params, 4, 0.0);
        int warmup = TryInt(m.Params, 5, 500);
        bool freezeWhitening = TryInt(m.Params, 6, 1) != 0;
        bool adaptiveWhitening = TryInt(m.Params, 7, 0) != 0;
        double covDecay = TryDouble(m.Params, 8, 0.005);

        var block = new OnlineICA(name, rate, k, eta0, reorth, minStable, contrast, warmup, freezeWhitening, adaptiveWhitening, covDecay);
        return block;
    }
    
    /// <summary>
    /// Exports the live hyper-parameters in the exact order <see cref="ConfigureInput"/> reads them:
    /// [0] k, [1] eta0, [2] reorthEvery, [3] minStableCount, [4] contrastFunction,
    /// [5] warmupSamples, [6] freezeWhiteningAfterWarmup, [7] adaptiveWhitening, [8] covarianceDecay.
    /// Inherited unchanged by <c>SupervisedICA</c>, whose parameter layout is identical.
    /// </summary>
    /// <returns>The values for <see cref="JsonModel.Params"/>.</returns>
    // The two whitening flags are emitted as 0/1 because ConfigureInput reads them with TryInt,
    // which has no case for a JSON boolean and would silently fall back to the default.
    protected override IReadOnlyList<object>? GetJsonParams()
        => new List<object>
        {
            _k, _eta0, _reorthEvery, _minStableCount, _contrastFunction, _warmupSamples,
            _freezeWhiteningAfterWarmup ? 1 : 0, _adaptiveWhitening ? 1 : 0, _covarianceDecay
        };

    #endregion

    #region Data Reception
    
    /// <summary>
    /// Handles incoming data from the observable pipeline.
    /// </summary>
    /// <param name="sender">The source of the data.</param>
    /// <param name="data">
    /// Input data. Supported types:
    /// <list type="bullet">
    ///     <item><description><see cref="Vector{T}"/>: Single observation vector</description></item>
    ///     <item><description><see cref="Matrix{T}"/>: Multiple observations (one per row)</description></item>
    /// </list>
    /// </param>
    /// <remarks>
    /// For matrix input, each row is processed sequentially as an independent observation.
    /// Unsupported data types are logged and ignored.
    /// </remarks>
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

            default:
                Console.WriteLine($"[OnlineICA '{Name}'] Unhandled data type: {data.GetType().FullName}");
                break;
        }
    }
    
    #endregion

    #region Initialization
    
    /// <summary>
    /// Ensures all internal state is properly initialized for the given input dimension.
    /// </summary>
    /// <param name="d">Input vector dimensionality.</param>
    /// <exception cref="ArgumentOutOfRangeException">
    /// Thrown when d ≤ 0 or d &lt; k.
    /// </exception>
    /// <remarks>
    /// Called automatically on first sample or when input dimension changes.
    /// Reinitializes all matrices and resets sample counter.
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void EnsureInit(int d)
    {
        if (d <= 0) throw new ArgumentOutOfRangeException(nameof(d), "Input dimension must be > 0.");
        if (d < _k) throw new ArgumentOutOfRangeException(nameof(d), $"Input dimension must be >= k ({_k}).");

        bool needInit =
            _d == 0 ||
            _d != d ||
            _W.RowCount != _k ||
            _W.ColumnCount != _k ||
            _whiteningMatrix.RowCount != _k ||
            _whiteningMatrix.ColumnCount != d ||
            _xWhitened is null ||
            _y is null ||
            _gY is null ||
            _gPrimeY is null ||
            _kurtosisEma is null ||
            _negentropyEma is null ||
            _mean is null;

        if (!needInit) return;

        _d = d;
        _n = 0;
        _isStable = false;
        _warmupComplete = (_warmupSamples == 0);  // No warmup if warmupSamples = 0

        _mean = DenseVector.Create(d, 0.0);
        
        // Initialize covariance tracking
        _M2 = DenseMatrix.Create(d, d, 0.0);
        _C = DenseMatrix.CreateIdentity(d);  // Start with identity (regularized)
        
        // Whitening matrix: projects d-dimensional input to k-dimensional whitened space
        // Initialize with simple projection (will be updated via eigendecomposition)
        _whiteningMatrix = DenseMatrix.Create(_k, d, 0.0);
        for (int i = 0; i < _k; i++)
            _whiteningMatrix[i, i] = 1.0;
        
        _dewhiteningMatrix = _whiteningMatrix.Transpose();
        _prevWhiteningMatrix = null;

        // Unmixing matrix: k x k rotation in whitened space
        _W = DenseMatrix.CreateIdentity(_k);

        _xWhitened = DenseVector.Create(_k, 0.0);
        _y = DenseVector.Create(_k, 0.0);
        _gY = DenseVector.Create(_k, 0.0);
        _gPrimeY = DenseVector.Create(_k, 0.0);

        _kurtosisEma = DenseVector.Create(_k, 0.0);
        _y4Ema = DenseVector.Create(_k, 0.0);
        _y2Ema = DenseVector.Create(_k, 1.0);
        _gEma = DenseVector.Create(_k, 0.0);
        _negentropyEma = DenseVector.Create(_k, 0.0);

        _prevICs = new Vector<double>[_k];

        Console.WriteLine($"[OnlineICA '{Name}'] Initialized/Reinitialized with d={d}, k={_k}");
    }
    
    #endregion

    #region Core Processing
    
    /// <summary>
    /// Processes a single input vector through the online ICA pipeline.
    /// </summary>
    /// <param name="x">Input observation vector (d-dimensional).</param>
    /// <exception cref="ArgumentNullException">Thrown when x is null.</exception>
    /// <remarks>
    /// <para><b>Processing Pipeline:</b></para>
    /// <para><b>Phase 1 - Warm-up (samples 1 to warmupSamples):</b></para>
    /// <list type="bullet">
    ///     <item><description>Estimate mean and covariance only</description></item>
    ///     <item><description>At end of warm-up: compute initial whitening matrix</description></item>
    ///     <item><description>W remains identity during warm-up</description></item>
    /// </list>
    /// <para><b>Phase 2 - ICA Learning (after warm-up):</b></para>
    /// <list type="number">
    ///     <item><description>Update online mean/covariance estimates</description></item>
    ///     <item><description>Center and whiten input</description></item>
    ///     <item><description>Apply ICA rotation: y = W · z</description></item>
    ///     <item><description>Update independence metrics</description></item>
    ///     <item><description>Apply FastICA natural gradient update to W</description></item>
    ///     <item><description>Periodic: Orthonormalize W, correct signs</description></item>
    ///     <item><description>Optional: Update whitening (with W reset if significant change)</description></item>
    /// </list>
    /// </remarks>
    protected virtual void Process(Vector<double> x)
    {
        if (x is null) throw new ArgumentNullException(nameof(x));

        EnsureInit(x.Count);

        if (x.Count != _d) return;

        _n++;

        // ═══════════════════════════════════════════════════════════════════
        // STEP 1: Online mean update (Welford's algorithm)
        // μ_new = μ_old + (x - μ_old) / n
        // ═══════════════════════════════════════════════════════════════════
        var delta = x - _mean;
        _mean += delta / _n;
        var delta2 = x - _mean;
        
        // ═══════════════════════════════════════════════════════════════════
        // STEP 2: Online covariance update
        // M₂ += outer(δ_old, δ_new)
        // C = M₂ / (n - 1)
        // ═══════════════════════════════════════════════════════════════════
        for (int i = 0; i < _d; i++)
        {
            for (int j = 0; j < _d; j++)
            {
                _M2[i, j] += delta[i] * delta2[j];
            }
        }
        
        if (_n > 1)
        {
            var scale = 1.0 / (_n - 1);
            for (int i = 0; i < _d; i++)
            {
                for (int j = 0; j < _d; j++)
                {
                    _C[i, j] = _M2[i, j] * scale;
                }
                _C[i, i] += 1e-8;  // Regularization for numerical stability
            }
            
            // Symmetrize covariance to prevent numerical drift causing complex eigenvalues
            for (int i = 0; i < _d; i++)
            {
                for (int j = i + 1; j < _d; j++)
                {
                    double avg = (_C[i, j] + _C[j, i]) / 2.0;
                    _C[i, j] = avg;
                    _C[j, i] = avg;
                }
            }
        }

        // ═══════════════════════════════════════════════════════════════════
        // WARM-UP PHASE: Only estimate statistics, don't learn W yet
        // ═══════════════════════════════════════════════════════════════════
        if (!_warmupComplete)
        {
            if (_n >= _warmupSamples)
            {
                // End of warm-up: compute initial whitening
                UpdateWhiteningMatrix();
                _prevWhiteningMatrix = _whiteningMatrix.Clone();
                _baselineCov = _C.Clone();  // Store baseline for drift detection
                _ewmaCov = _C.Clone();       // Initialize EWMA covariance
                _warmupComplete = true;
                Console.WriteLine($"[OnlineICA '{Name}'] Warm-up complete at n={_n}, starting ICA learning");
            }
            else
            {
                // During warm-up: just output centered data projected to k dims
                var xCentered = x - _mean;
                var y = _y!;
                _whiteningMatrix.Multiply(xCentered, y);
                PublishProjection(y.Clone());
                return;
            }
        }
        
        // ═══════════════════════════════════════════════════════════════════
        // ADAPTIVE MODE: Update EWMA covariance and detect drift
        // ═══════════════════════════════════════════════════════════════════
        if (_adaptiveWhitening && _ewmaCov != null && _baselineCov != null)
        {
            // Update EWMA covariance: C_ewma = (1-α)·C_ewma + α·C
            for (int i = 0; i < _d; i++)
            {
                for (int j = 0; j < _d; j++)
                {
                    _ewmaCov[i, j] = (1 - _covarianceDecay) * _ewmaCov[i, j] + _covarianceDecay * _C[i, j];
                }
            }
            
            // Compute drift metric: relative Frobenius norm
            double driftNorm = 0, baselineNorm = 0;
            for (int i = 0; i < _d; i++)
            {
                for (int j = 0; j < _d; j++)
                {
                    double diff = _ewmaCov[i, j] - _baselineCov[i, j];
                    driftNorm += diff * diff;
                    baselineNorm += _baselineCov[i, j] * _baselineCov[i, j];
                }
            }
            _driftMetric = baselineNorm > 1e-10 ? Math.Sqrt(driftNorm / baselineNorm) : 0;
        }

        // ═══════════════════════════════════════════════════════════════════
        // STEP 3: Center and whiten
        // z = W_white · (x - μ)
        // ═══════════════════════════════════════════════════════════════════
        var xCenteredPost = x - _mean;
        var xWhite = _xWhitened!;
        _whiteningMatrix.Multiply(xCenteredPost, xWhite);

        // ═══════════════════════════════════════════════════════════════════
        // STEP 4: Apply ICA rotation
        // y = W · z
        // ═══════════════════════════════════════════════════════════════════
        var yOut = _y!;
        _W.Multiply(xWhite, yOut);

        // ═══════════════════════════════════════════════════════════════════
        // STEP 5: Compute contrast function derivatives
        // g(y) = G'(y), g'(y) = G''(y)
        // ═══════════════════════════════════════════════════════════════════
        ComputeContrastGradient(yOut, _gY!, _gPrimeY!);
        
        // ═══════════════════════════════════════════════════════════════════
        // STEP 6: Update independence metrics
        // ═══════════════════════════════════════════════════════════════════
        UpdateKurtosisEstimate(yOut);
        UpdateNegentropyEstimate(yOut);

        if (!_isStable && _n >= _minStableCount)
        {
            _isStable = true;
        }

        // ═══════════════════════════════════════════════════════════════════
        // STEP 7: FastICA natural gradient update
        // w_i ← w_i + η · (z · g(y_i) - g'(y_i) · w_i)
        // ═══════════════════════════════════════════════════════════════════
        
        // Adaptive learning rate based on samples since warm-up
        long nSinceWarmup = _n - _warmupSamples;
        double eta;
        if (nSinceWarmup < 100)
        {
            // Learning rate warm-up: linearly increase from 0 to η₀
            eta = _eta0 * (nSinceWarmup / 100.0);
        }
        else
        {
            // Decay phase: η = η₀ / √(n/100)
            eta = _eta0 / Math.Sqrt(nSinceWarmup / 100.0);
        }
        eta = Math.Clamp(eta, 1e-6, _eta0);
        
        for (int i = 0; i < _k; i++)
        {
            double gi = _gY![i];
            double gPrimei = _gPrimeY![i];
            
            for (int j = 0; j < _k; j++)
            {
                double grad = xWhite[j] * gi - gPrimei * _W[i, j];
                
                // Gradient clipping for numerical stability
                grad = Math.Clamp(grad, -5.0, 5.0);
                
                _W[i, j] += eta * grad;
            }
        }

        // ═══════════════════════════════════════════════════════════════════
        // STEP 8: Periodic maintenance
        // ═══════════════════════════════════════════════════════════════════
        if (_n % _reorthEvery == 0)
        {
            Orthonormalize(_W);
            CorrectICSigns();
        }
        
        // ═══════════════════════════════════════════════════════════════════
        // STEP 9: Whitening update (mode-dependent)
        // ═══════════════════════════════════════════════════════════════════
        if (_adaptiveWhitening && _n % (_reorthEvery * 5) == 0)
        {
            // Adaptive mode: smooth whitening update based on EWMA covariance
            UpdateWhiteningAdaptive();
        }
        else if (!_freezeWhiteningAfterWarmup && _n % (_reorthEvery * 20) == 0)
        {
            // Non-frozen mode: periodic update with W reset on large changes
            UpdateWhiteningMatrixWithConsistency();
        }
        // Frozen mode: no whitening update (best for stationary signals)

        // ═══════════════════════════════════════════════════════════════════
        // STEP 10: Publish results
        // ═══════════════════════════════════════════════════════════════════
        PublishProjection(yOut.Clone());
    }
    
    #endregion

    #region Contrast Functions
    
    /// <summary>
    /// Computes the contrast function derivatives g(y) and g'(y) for all components.
    /// </summary>
    /// <param name="y">Independent component values.</param>
    /// <param name="g">Output: g(y) = G'(y) values.</param>
    /// <param name="gPrime">Output: g'(y) = G''(y) values.</param>
    /// <remarks>
    /// <para><b>Contrast Functions:</b></para>
    /// <list type="table">
    ///     <listheader>
    ///         <term>Type</term>
    ///         <description>G(y), g(y), g'(y)</description>
    ///     </listheader>
    ///     <item>
    ///         <term>LogCosh</term>
    ///         <description>
    ///             G(y) = log(cosh(y))<br/>
    ///             g(y) = tanh(y)<br/>
    ///             g'(y) = 1 - tanh²(y) = sech²(y)
    ///         </description>
    ///     </item>
    ///     <item>
    ///         <term>Exp</term>
    ///         <description>
    ///             G(y) = -exp(-y²/2)<br/>
    ///             g(y) = y · exp(-y²/2)<br/>
    ///             g'(y) = (1 - y²) · exp(-y²/2)
    ///         </description>
    ///     </item>
    ///     <item>
    ///         <term>Cube</term>
    ///         <description>
    ///             G(y) = y⁴/4<br/>
    ///             g(y) = y³<br/>
    ///             g'(y) = 3y²
    ///         </description>
    ///     </item>
    /// </list>
    /// </remarks>
    private void ComputeContrastGradient(Vector<double> y, Vector<double> g, Vector<double> gPrime)
    {
        for (int i = 0; i < _k; i++)
        {
            var yi = y[i];
            
            switch (_contrastFunction)
            {
                case 0: // LogCosh: g(y) = tanh(y), g'(y) = 1 - tanh²(y)
                    var tanh = Math.Tanh(yi);
                    g[i] = tanh;
                    gPrime[i] = 1.0 - tanh * tanh;
                    break;
                    
                case 1: // Exp: g(y) = y·exp(-y²/2), g'(y) = (1-y²)·exp(-y²/2)
                    var expTerm = Math.Exp(-yi * yi / 2);
                    g[i] = yi * expTerm;
                    gPrime[i] = (1.0 - yi * yi) * expTerm;
                    break;
                    
                case 2: // Cube: g(y) = y³, g'(y) = 3y²
                    g[i] = yi * yi * yi;
                    gPrime[i] = 3.0 * yi * yi;
                    break;
                    
                default:
                    goto case 0;
            }
        }
    }
    
    /// <summary>
    /// Numerically stable computation of log(cosh(y)).
    /// </summary>
    /// <param name="y">Input value.</param>
    /// <returns>log(cosh(y)) without overflow for large |y|.</returns>
    /// <remarks>
    /// <para>
    /// For large |y|, cosh(y) ≈ exp(|y|)/2, so log(cosh(y)) ≈ |y| - log(2).
    /// We use the identity:
    /// <code>log(cosh(y)) = |y| + log(1 + exp(-2|y|)) - log(2)</code>
    /// which is numerically stable for all y.
    /// </para>
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static double StableLogCosh(double y)
    {
        double absY = Math.Abs(y);
        if (absY > 20)
        {
            // For large |y|: log(cosh(y)) ≈ |y| - log(2)
            return absY - 0.693147180559945;  // log(2)
        }
        return Math.Log(Math.Cosh(y));
    }
    
    #endregion

    #region Independence Metrics
    
    /// <summary>
    /// Updates the kurtosis estimate using exponential moving average.
    /// </summary>
    /// <param name="y">Current independent component values.</param>
    /// <remarks>
    /// <para>
    /// Properly normalized excess kurtosis:
    /// <code>κ = E[y⁴] / E[y²]² - 3</code>
    /// This formula is scale-invariant, unlike the simpler E[y⁴] - 3.
    /// </para>
    /// <para>
    /// We track E[y²] and E[y⁴] separately via EMA, then compute κ on demand
    /// in the <see cref="Kurtosis"/> property.
    /// </para>
    /// </remarks>
    private void UpdateKurtosisEstimate(Vector<double> y)
    {
        for (int i = 0; i < _k; i++)
        {
            double yi = y[i];
            double y2 = yi * yi;
            double y4 = y2 * y2;
            
            // Update EMAs for y² and y⁴
            _y2Ema[i] = (1 - _emaAlpha) * _y2Ema[i] + _emaAlpha * y2;
            _y4Ema[i] = (1 - _emaAlpha) * _y4Ema[i] + _emaAlpha * y4;
        }
    }
    
    /// <summary>
    /// Updates the negentropy estimate using exponential moving average.
    /// </summary>
    /// <param name="y">Current independent component values.</param>
    /// <remarks>
    /// <para>
    /// Correct negentropy approximation:
    /// <code>J(y) ≈ [E{G(y)} - E{G(ν)}]²</code>
    /// where ν ~ N(0,1).
    /// </para>
    /// <para>
    /// We track G_ema = E[G(y)] via EMA, then compute J = (G_ema - c)².
    /// This is more stable than tracking (G-c)² directly.
    /// </para>
    /// <para>
    /// Gaussian reference values E{G(ν)}:
    /// <list type="bullet">
    ///     <item><description>LogCosh: 0.3746</description></item>
    ///     <item><description>Exp: -1/√2 ≈ -0.7071</description></item>
    ///     <item><description>Cube: E[ν⁴]/4 = 3/4 = 0.75</description></item>
    /// </list>
    /// </para>
    /// </remarks>
    private void UpdateNegentropyEstimate(Vector<double> y)
    {
        // E{G(ν)} for standard normal ν ~ N(0,1)
        double gaussianExpectation = _contrastFunction switch
        {
            0 => 0.3746,  // logcosh: E[log(cosh(ν))]
            1 => -0.7071, // exp: E[-exp(-ν²/2)] = -1/√2
            2 => 0.75,    // cube: E[ν⁴]/4 = 3/4
            _ => 0.3746
        };
        
        for (int i = 0; i < _k; i++)
        {
            double yi = y[i];
            
            // Compute G(y) with numerical stability
            double G = _contrastFunction switch
            {
                // Numerically stable log(cosh(y)) to avoid overflow for large |y|
                // log(cosh(y)) = |y| + log(1 + exp(-2|y|)) - log(2)
                0 => StableLogCosh(yi),
                1 => -Math.Exp(-yi * yi / 2),
                2 => yi * yi * yi * yi / 4,
                _ => StableLogCosh(yi)
            };
            
            // Update EMA of G(y): G_ema = (1-α)·G_ema + α·G
            _gEma[i] = (1 - _emaAlpha) * _gEma[i] + _emaAlpha * G;
            
            // Negentropy: J = (E[G(y)] - E[G(ν)])²
            double diff = _gEma[i] - gaussianExpectation;
            _negentropyEma[i] = diff * diff;
        }
    }
    
    #endregion

    #region Whitening
    
    /// <summary>
    /// Updates the whitening matrix using eigendecomposition of the covariance matrix.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Given covariance C with eigendecomposition C = V·D·V^T:
    /// <code>
    /// W_white = D^(-1/2) · V^T     (whitening)
    /// W_dewhite = V · D^(1/2)       (dewhitening)
    /// </code>
    /// </para>
    /// <para>
    /// Only the top k eigenvectors (by eigenvalue magnitude) are used,
    /// providing dimensionality reduction combined with whitening.
    /// </para>
    /// <para>
    /// Includes validation checks:
    /// <list type="bullet">
    ///     <item><description>Minimum eigenvalue threshold (1e-10)</description></item>
    ///     <item><description>NaN/Inf detection in resulting matrix</description></item>
    /// </list>
    /// </para>
    /// </remarks>
    private void UpdateWhiteningMatrix()
    {
        try
        {
            // Symmetrize covariance to avoid complex eigenvalues from numerical errors
            // C = (C + C^T) / 2
            var Csym = (_C + _C.Transpose()) / 2.0;
            
            var evd = Csym.Evd();
            var eigenvalues = evd.EigenValues;
            var V = evd.EigenVectors;
            
            // Sort by eigenvalue magnitude (descending) and take top k
            var sortedIndices = Enumerable.Range(0, _d)
                .OrderByDescending(i => eigenvalues[i].Real)
                .ToArray();
            
            // Validate minimum eigenvalue
            double minEigenval = eigenvalues[sortedIndices[_k - 1]].Real;
            if (minEigenval < 1e-10)
            {
                if (_smallEigenvalueRate.Allow())
                    Log.Warn("OnlineICA", Name, $"Small eigenvalue ({minEigenval:E2}), skipping whitening update ({_smallEigenvalueRate.Suppressed} suppressed)");
                return;
            }
            
            // Build whitening matrix: W_white[i,j] = V[j, idx] / √λ
            var newWhitening = DenseMatrix.Create(_k, _d, 0.0);
            var newDewhitening = DenseMatrix.Create(_d, _k, 0.0);
            
            for (int i = 0; i < _k; i++)
            {
                int idx = sortedIndices[i];
                double eigenval = eigenvalues[idx].Real;
                double scale = 1.0 / Math.Sqrt(eigenval);
                double invScale = Math.Sqrt(eigenval);
                
                for (int j = 0; j < _d; j++)
                {
                    newWhitening[i, j] = V[j, idx] * scale;
                    newDewhitening[j, i] = V[j, idx] * invScale;
                }
            }
            
            // Validate for NaN/Inf
            bool isValid = true;
            for (int i = 0; i < _k && isValid; i++)
            {
                for (int j = 0; j < _d && isValid; j++)
                {
                    if (double.IsNaN(newWhitening[i, j]) || double.IsInfinity(newWhitening[i, j]))
                        isValid = false;
                }
            }
            
            if (isValid)
            {
                _whiteningMatrix = newWhitening;
                _dewhiteningMatrix = newDewhitening;
            }
            else
            {
                if (_invalidWhiteningRate.Allow())
                    Log.Warn("OnlineICA", Name, $"Invalid whitening matrix, keeping previous ({_invalidWhiteningRate.Suppressed} suppressed)");
            }
        }
        catch (Exception ex)
        {
            if (_whiteningErrorRate.Allow())
                Log.Error("OnlineICA", Name, ex, $"Error updating whitening matrix ({_whiteningErrorRate.Suppressed} suppressed)");
        }
    }
    
    /// <summary>
    /// Updates the whitening matrix with consistency handling for W.
    /// </summary>
    /// <remarks>
    /// <para>
    /// When the whitening matrix changes significantly, the old W is no longer valid
    /// because it was learned in a different whitened space. This method handles
    /// two strategies:
    /// </para>
    /// <list type="bullet">
    ///     <item>
    ///         <description>
    ///             <b>Transform W:</b> W_new = W_old · W_white_old · W_white_new^(-1)
    ///             Preserves learned rotations but may accumulate numerical errors.
    ///         </description>
    ///     </item>
    ///     <item>
    ///         <description>
    ///             <b>Reset W:</b> If change is too large, reset W = I and restart learning.
    ///             More robust but loses learned information.
    ///         </description>
    ///     </item>
    /// </list>
    /// </remarks>
    private void UpdateWhiteningMatrixWithConsistency()
    {
        if (_prevWhiteningMatrix == null)
        {
            UpdateWhiteningMatrix();
            _prevWhiteningMatrix = _whiteningMatrix.Clone();
            return;
        }
        
        // Store old whitening
        var oldWhitening = _whiteningMatrix.Clone();
        
        // Compute new whitening
        UpdateWhiteningMatrix();
        
        // Check if whitening changed significantly
        double change = 0;
        for (int i = 0; i < _k; i++)
        {
            for (int j = 0; j < _d; j++)
            {
                double diff = _whiteningMatrix[i, j] - oldWhitening[i, j];
                change += diff * diff;
            }
        }
        change = Math.Sqrt(change / (_k * _d));
        
        const double resetThreshold = 0.2;     // Reset if >20% relative change
        const double transformThreshold = 0.02; // Transform if 2-20% change
        
        if (change > resetThreshold)
        {
            // Large change: reset W to identity and clear sign history
            _W = DenseMatrix.CreateIdentity(_k);
            _prevICs = new Vector<double>[_k];
            Console.WriteLine($"[OnlineICA '{Name}'] Whitening changed significantly ({change:F4}), resetting W");
        }
        else if (change > transformThreshold)
        {
            // Moderate change: transform W to maintain consistency
            // W_new = W_old · (W_white_old · pinv(W_white_new))
            var T = ComputeBasisTransform(oldWhitening, _whiteningMatrix);
            var Wnew = _W * T;
            
            // Copy back
            for (int i = 0; i < _k; i++)
            {
                for (int j = 0; j < _k; j++)
                {
                    _W[i, j] = Wnew[i, j];
                }
            }
            
            // Re-orthonormalize
            Orthonormalize(_W);
            
            Console.WriteLine($"[OnlineICA '{Name}'] Whitening updated ({change:F4}), W transformed");
        }
        // Small change: do nothing, W is still approximately valid
        
        _prevWhiteningMatrix = _whiteningMatrix.Clone();
    }
    
    /// <summary>
    /// Updates the whitening matrix adaptively for non-stationary signals.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Designed for EMG/ultrasound applications where signal statistics drift slowly.
    /// </para>
    /// <para>
    /// <b>Key insight:</b> When whitening changes from W_old to W_new, we must transform W
    /// to maintain the same overall unmixing:
    /// <code>
    /// y = W · z_old = W · W_white_old · x
    /// y = W_new · z_new = W_new · W_white_new · x
    /// Therefore: W_new = W · W_white_old · pinv(W_white_new)
    /// </code>
    /// </para>
    /// <para>
    /// For k = d (full separation), pinv = inverse. For k &lt; d, we use pseudoinverse.
    /// </para>
    /// </remarks>
    private void UpdateWhiteningAdaptive()
    {
        if (_ewmaCov == null || _prevWhiteningMatrix == null) return;
        
        try
        {
            // Symmetrize EWMA covariance
            var ewmaSym = (_ewmaCov + _ewmaCov.Transpose()) / 2.0;
            
            var evd = ewmaSym.Evd();
            var eigenvalues = evd.EigenValues;
            var V = evd.EigenVectors;
            
            // Sort by eigenvalue (descending)
            var sortedIndices = Enumerable.Range(0, _d)
                .OrderByDescending(i => eigenvalues[i].Real)
                .ToArray();
            
            // Validate eigenvalues
            double minEigenval = eigenvalues[sortedIndices[_k - 1]].Real;
            if (minEigenval < 1e-10)
            {
                Console.WriteLine($"[OnlineICA '{Name}'] Adaptive whitening: eigenvalue too small, skipping");
                return;
            }
            
            // Compute new whitening matrix
            var newWhitening = DenseMatrix.Create(_k, _d, 0.0);
            var newDewhitening = DenseMatrix.Create(_d, _k, 0.0);
            
            for (int i = 0; i < _k; i++)
            {
                int idx = sortedIndices[i];
                double eigenval = eigenvalues[idx].Real;
                double scale = 1.0 / Math.Sqrt(eigenval);
                double invScale = Math.Sqrt(eigenval);
                
                for (int j = 0; j < _d; j++)
                {
                    newWhitening[i, j] = V[j, idx] * scale;
                    newDewhitening[j, i] = V[j, idx] * invScale;
                }
            }
            
            // ═══════════════════════════════════════════════════════════════════
            // Transform W to maintain consistency when whitening changes
            // W_new = W_old · (W_white_old · pinv(W_white_new))
            // ═══════════════════════════════════════════════════════════════════
            
            // Compute transformation matrix T = W_white_old · pinv(W_white_new)
            // For k×d whitening matrices:
            // pinv(W_white_new) = W_white_new^T · (W_white_new · W_white_new^T)^(-1)
            // Since whitening is already orthonormal in rows, W·W^T ≈ I
            // So pinv(W) ≈ W^T for well-conditioned whitening
            
            Matrix<double> T;
            if (_k == _d)
            {
                // Square case: direct inverse
                try
                {
                    var newWhiteningInv = newWhitening.Inverse();
                    T = _prevWhiteningMatrix * newWhiteningInv;
                }
                catch
                {
                    // Fallback to pseudoinverse
                    T = ComputeBasisTransform(_prevWhiteningMatrix, newWhitening);
                }
            }
            else
            {
                // Rectangular case: use pseudoinverse approximation
                T = ComputeBasisTransform(_prevWhiteningMatrix, newWhitening);
            }
            
            // Apply transformation: W_new = W_old · T
            var Wnew = _W * T;
            
            // Copy back
            for (int i = 0; i < _k; i++)
            {
                for (int j = 0; j < _k; j++)
                {
                    _W[i, j] = Wnew[i, j];
                }
            }
            
            // Re-orthonormalize W
            Orthonormalize(_W);
            
            // Update whitening matrices
            _whiteningMatrix = newWhitening;
            _dewhiteningMatrix = newDewhitening;
            _prevWhiteningMatrix = newWhitening.Clone();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[OnlineICA '{Name}'] Adaptive whitening error: {ex.Message}");
        }
    }
    
    /// <summary>
    /// Computes the transformation matrix T = W_old · pinv(W_new) for basis change.
    /// </summary>
    /// <param name="Wold">Old whitening matrix (k×d).</param>
    /// <param name="Wnew">New whitening matrix (k×d).</param>
    /// <returns>Transformation matrix T (k×k).</returns>
    /// <remarks>
    /// <para>
    /// For k×d matrices where k ≤ d:
    /// <code>pinv(W) = W^T · (W · W^T)^(-1)</code>
    /// </para>
    /// <para>
    /// This gives T = W_old · W_new^T · (W_new · W_new^T)^(-1)
    /// </para>
    /// </remarks>
    private Matrix<double> ComputeBasisTransform(Matrix<double> Wold, Matrix<double> Wnew)
    {
        // Compute W_new · W_new^T (k×k)
        var WnewWnewT = Wnew * Wnew.Transpose();
        
        // Add regularization for stability
        for (int i = 0; i < _k; i++)
        {
            WnewWnewT[i, i] += 1e-8;
        }
        
        // Compute (W_new · W_new^T)^(-1)
        Matrix<double> WnewWnewT_inv;
        try
        {
            WnewWnewT_inv = WnewWnewT.Inverse();
        }
        catch
        {
            // If inversion fails, return identity (no transform)
            Console.WriteLine($"[OnlineICA '{Name}'] Basis transform: inversion failed, using identity");
            return DenseMatrix.CreateIdentity(_k);
        }
        
        // pinv(W_new) = W_new^T · (W_new · W_new^T)^(-1)  [d×k]
        var pinvWnew = Wnew.Transpose() * WnewWnewT_inv;
        
        // T = W_old · pinv(W_new)  [k×k]
        return Wold * pinvWnew;
    }
    
    #endregion

    #region Publishing
    
    /// <summary>
    /// Publishes the independent components to downstream subscribers.
    /// </summary>
    /// <param name="projection">The k-dimensional independent component vector.</param>
    /// <remarks>
    /// Protected virtual to allow interception or modification in derived classes.
    /// </remarks>
    protected virtual void PublishProjection(Vector<double> projection)
    {
        Publish(projection);
    }
    
    #endregion

    #region Transform Methods
    
    /// <summary>
    /// Transforms a single observation to independent components using the learned unmixing matrix.
    /// </summary>
    /// <param name="x">Input observation vector (d-dimensional).</param>
    /// <returns>Independent components vector (k-dimensional).</returns>
    /// <exception cref="InvalidOperationException">
    /// Thrown when ICA has not been initialized (no data processed yet).
    /// </exception>
    /// <exception cref="ArgumentNullException">Thrown when x is null.</exception>
    /// <exception cref="InvalidOperationException">
    /// Thrown when x dimension doesn't match expected input dimension.
    /// </exception>
    /// <remarks>
    /// <para>
    /// Applies the transformation:
    /// <code>y = W · W_white · (x - μ)</code>
    /// </para>
    /// <para>
    /// Unlike <see cref="Process"/>, this method does not update any internal state.
    /// Use for transforming new data after training.
    /// </para>
    /// </remarks>
    /// <example>
    /// <code>
    /// // After processing training data...
    /// var newSample = DenseVector.OfArray(new[] { 1.0, 2.0, 3.0 });
    /// var components = ica.Transform(newSample);
    /// </code>
    /// </example>
    public Vector<double> Transform(Vector<double> x)
    {
        if (_d == 0)
            throw new InvalidOperationException("ICA not initialized yet.");
        if (x is null) throw new ArgumentNullException(nameof(x));
        if (x.Count != _d)
            throw new InvalidOperationException($"Dimension mismatch: expected {_d}, got {x.Count}");

        var xCentered = x - _mean;
        var xWhite = _whiteningMatrix * xCentered;
        return _W * xWhite;
    }

    /// <summary>
    /// Reconstructs an approximation of the original signal from independent components.
    /// </summary>
    /// <param name="y">Independent components vector (k-dimensional).</param>
    /// <returns>Reconstructed observation vector (d-dimensional).</returns>
    /// <exception cref="InvalidOperationException">
    /// Thrown when ICA has not been initialized.
    /// </exception>
    /// <exception cref="ArgumentNullException">Thrown when y is null.</exception>
    /// <exception cref="InvalidOperationException">
    /// Thrown when y dimension doesn't match k.
    /// </exception>
    /// <remarks>
    /// <para>
    /// Applies the inverse transformation:
    /// <code>x ≈ W_dewhite · W^T · y + μ</code>
    /// </para>
    /// <para>
    /// <b>Note:</b> This is an approximate inverse. If k &lt; d, information is lost
    /// and perfect reconstruction is not possible.
    /// </para>
    /// </remarks>
    /// <example>
    /// <code>
    /// var components = ica.Transform(sample);
    /// // Modify components (e.g., remove artifact)
    /// components[0] = 0;
    /// var cleaned = ica.InverseTransform(components);
    /// </code>
    /// </example>
    public Vector<double> InverseTransform(Vector<double> y)
    {
        if (_d == 0)
            throw new InvalidOperationException("ICA not initialized yet.");
        if (y is null) throw new ArgumentNullException(nameof(y));
        if (y.Count != _k)
            throw new InvalidOperationException($"Component dimension mismatch: expected {_k}, got {y.Count}");

        var xWhite = _W.Transpose() * y;
        return _dewhiteningMatrix * xWhite + _mean;
    }
    
    #endregion

    #region Reset
    
    /// <summary>
    /// Resets all learned parameters to initial state.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Clears:
    /// <list type="bullet">
    ///     <item><description>Mean and covariance estimates</description></item>
    ///     <item><description>Whitening and unmixing matrices</description></item>
    ///     <item><description>Independence metrics</description></item>
    ///     <item><description>Sample counter and stability flag</description></item>
    /// </list>
    /// </para>
    /// <para>
    /// After reset, the next <see cref="Process"/> call will reinitialize
    /// all state based on the input dimension.
    /// </para>
    /// </remarks>
    public void Reset()
    {
        _d = 0;
        _n = 0;
        _isStable = false;
        _warmupComplete = (_warmupSamples == 0);

        _mean = DenseVector.Create(0, 0.0);
        _M2 = DenseMatrix.Create(0, 0, 0.0);
        _C = DenseMatrix.Create(0, 0, 0.0);
        _whiteningMatrix = DenseMatrix.Create(0, 0, 0.0);
        _dewhiteningMatrix = DenseMatrix.Create(0, 0, 0.0);
        _prevWhiteningMatrix = null;
        _W = DenseMatrix.Create(0, 0, 0.0);

        _xWhitened = null;
        _y = null;
        _gY = null;
        _gPrimeY = null;

        _kurtosisEma = DenseVector.Create(_k, 0.0);
        _y4Ema = DenseVector.Create(_k, 0.0);
        _y2Ema = DenseVector.Create(_k, 1.0);
        _gEma = DenseVector.Create(_k, 0.0);
        _negentropyEma = DenseVector.Create(_k, 0.0);

        _prevICs = new Vector<double>[_k];

        Console.WriteLine($"[OnlineICA '{Name}'] Model reset");
    }
    
    #endregion

    #region Sign Correction
    
    /// <summary>
    /// Corrects the sign of independent components for temporal consistency.
    /// </summary>
    /// <remarks>
    /// <para>
    /// ICA has inherent sign ambiguity: if w is a valid unmixing vector, so is -w.
    /// This can cause sign flips between updates, making visualization difficult.
    /// </para>
    /// <para>
    /// This method tracks the previous direction of each component and flips
    /// the sign if the dot product with the previous direction is negative.
    /// Only active after <see cref="IsStable"/> becomes true.
    /// </para>
    /// <para>
    /// Generic implementation works for any k (number of components).
    /// </para>
    /// </remarks>
    private void CorrectICSigns()
    {
        if (!_isStable) return;
        if (_prevICs == null) return;

        for (int i = 0; i < _k; i++)
        {
            var ic = _W.Row(i);
            
            if (_prevICs[i] != null && ic.DotProduct(_prevICs[i]) < 0)
            {
                _W.SetRow(i, -ic);
                ic = -ic;
            }
            
            _prevICs[i] = ic.Clone();
        }
    }
    
    #endregion

    #region Orthonormalization
    
    /// <summary>
    /// Orthonormalizes the unmixing matrix W using symmetric decorrelation.
    /// </summary>
    /// <param name="W">The matrix to orthonormalize (modified in place).</param>
    /// <remarks>
    /// <para>
    /// Uses symmetric orthonormalization:
    /// <code>W ← (W · W^T)^(-1/2) · W</code>
    /// </para>
    /// <para>
    /// This is computed via eigendecomposition:
    /// <code>
    /// W·W^T = V · D · V^T
    /// (W·W^T)^(-1/2) = V · D^(-1/2) · V^T
    /// </code>
    /// </para>
    /// <para>
    /// Symmetric orthonormalization is preferred over Gram-Schmidt because it
    /// treats all directions equally, avoiding accumulation of errors in later components.
    /// Falls back to Gram-Schmidt if eigendecomposition fails.
    /// </para>
    /// </remarks>
    private static void Orthonormalize(Matrix<double> W)
    {
        try
        {
            var WWT = W * W.Transpose();
            var evd = WWT.Evd();
            
            var V = evd.EigenVectors;
            var eigenvalues = evd.EigenValues;
            
            // Compute D^(-1/2)
            var DinvSqrt = DenseMatrix.Create(W.RowCount, W.RowCount, 0.0);
            for (int i = 0; i < W.RowCount; i++)
            {
                double eval = eigenvalues[i].Real;
                DinvSqrt[i, i] = eval > 1e-10 ? 1.0 / Math.Sqrt(eval) : 0.0;
            }
            
            // (W·W^T)^(-1/2) = V · D^(-1/2) · V^T
            var WWT_invSqrt = V * DinvSqrt * V.Transpose();
            var Wnew = WWT_invSqrt * W;
            
            // Copy back to W
            for (int i = 0; i < W.RowCount; i++)
                for (int j = 0; j < W.ColumnCount; j++)
                    W[i, j] = Wnew[i, j];
        }
        catch
        {
            // Fallback to Gram-Schmidt if symmetric fails
            OrthonormalizeGramSchmidt(W);
        }
    }
    
    /// <summary>
    /// Orthonormalizes matrix rows using the Gram-Schmidt process (fallback method).
    /// </summary>
    /// <param name="W">The matrix to orthonormalize (modified in place).</param>
    /// <remarks>
    /// <para>
    /// For each row w_i:
    /// <code>
    /// w_i ← w_i - Σ_{j&lt;i} (w_i · w_j) · w_j
    /// w_i ← w_i / ||w_i||
    /// </code>
    /// </para>
    /// <para>
    /// Less numerically stable than symmetric orthonormalization for ICA,
    /// but useful as a fallback when eigendecomposition fails.
    /// </para>
    /// </remarks>
    private static void OrthonormalizeGramSchmidt(Matrix<double> W)
    {
        for (int i = 0; i < W.RowCount; i++)
        {
            var row = W.Row(i).Clone();
            
            // Subtract projections onto previous rows
            for (int j = 0; j < i; j++)
            {
                var prevRow = W.Row(j);
                var proj = row.DotProduct(prevRow);
                row -= proj * prevRow;
            }
            
            // Normalize
            var norm = row.L2Norm();
            if (norm > 1e-10)
            {
                row /= norm;
                W.SetRow(i, row);
            }
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
    
    /// <summary>
    /// Releases all resources used by this instance.
    /// </summary>
    public override void Dispose()
    {
        base.Dispose();
    }
    
    #endregion
}
