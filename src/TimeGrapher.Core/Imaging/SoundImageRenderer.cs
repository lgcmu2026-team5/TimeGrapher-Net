using System;
using System.Collections.Generic;
using TimeGrapher.Core.Shared;

namespace TimeGrapher.Core.Imaging;

/// <summary>
/// Real-time folded sound-image renderer for watch/timegrapher-style acoustic displays.
///
/// SoundImageRenderer converts incoming single-channel floating-point PCM into a
/// scrolling/folded image where each image column represents one beat period.
/// Within a column, vertical position represents time inside that beat period,
/// and pixel intensity represents normalized signal magnitude.
///
/// Coordinate systems
/// ------------------
/// QImage physical coordinates (here: <see cref="PixelBuffer"/>) are always:
///     x=0,y=0      top-left, x increases right, y increases downward.
///
/// The renderer uses a logical "bucket" coordinate inside each beat column:
///     natural_bucket = 0   start of the beat column, increasing later in the beat.
///
/// Final conversion from logical bucket to image y depends on
/// <see cref="Config.Direction"/>:
///     BottomUp:  y = height - 1 - display_bucket
///     TopDown:   y = display_bucket
/// Both signal drawing and marker drawing use the same conversion path.
///
/// Port note: QImage scanLine(y)[x] writes become Pixels[y*Width + x].
/// </summary>
public sealed class SoundImageRenderer
{
    /// <summary>
    /// Vertical direction for time within a beat column. Affects only the final
    /// logical-bucket to image-y conversion. Sound and markers both use this setting.
    /// </summary>
    public enum VerticalTimeDirection
    {
        /// <summary>Logical bucket 0 at the bottom of the image; later samples higher.</summary>
        BottomUp = 0,

        /// <summary>Logical bucket 0 at the top of the image; later samples lower.</summary>
        TopDown = 1
    }

    /// <summary>
    /// Runtime configuration for the renderer. Most fields are safe to set once
    /// before <see cref="Initialize"/>. Some values can be changed later through
    /// dedicated setters, which clear render state while preserving the sample
    /// counter where appropriate.
    /// </summary>
    public sealed class Config
    {
        /// <summary>
        /// PCM input sample rate in Hz. Required at init (used for DC tracker
        /// coefficient and samples-per-column once BPH is known).
        /// </summary>
        public double SampleRateHz = 48000.0;

        /// <summary>
        /// Beats per hour. May be &lt;= 0 at init if BPH is not yet known.
        /// If &lt;= 0, ProcessSamples counts samples but does not render.
        /// </summary>
        public double Bph = 0.0;

        /// <summary>Foreground color used for the rendered sound intensity.</summary>
        public uint SoundColor = Argb.Rgba(255, 0, 0, 255);

        /// <summary>Background color used when clearing the image/columns.</summary>
        public uint BackgroundColor = Argb.Rgba(255, 255, 255, 255);

        /// <summary>Controls whether time inside each column visually moves up or down.</summary>
        public VerticalTimeDirection Direction = VerticalTimeDirection.BottomUp;

        /// <summary>
        /// Number of completed columns after BPH lock to process but not draw
        /// (lets DC tracker / peak envelope settle, avoids startup transients).
        /// </summary>
        public int WarmupColumns = 2;

        /// <summary>
        /// Number of completed post-warmup columns used to compute automatic
        /// vertical centering. Buffered, then flushed once the dominant band is found.
        /// </summary>
        public int AnchorColumns = 12;

        /// <summary>Time constant for simple one-pole DC removal.</summary>
        public double DcTimeConstantSec = 0.25;

        /// <summary>Decay multiplier for peak-envelope normalization.</summary>
        public float PeakDecay = 0.99995f;

        /// <summary>
        /// Display gamma for brightness shaping only: displayed = pow(normalized, gamma).
        /// Does not affect timing, marker placement, BPH, or sync.
        /// </summary>
        public float Gamma = 0.5f;

        /// <summary>
        /// If true, redraw the active in-progress column at the end of each
        /// ProcessSamples call after centering has locked.
        /// </summary>
        public bool LivePreviewCurrentColumn = true;
    }

    /// <summary>
    /// Persistent marker overlay entry. Markers are stored so they can be reapplied
    /// whenever sound columns are redrawn or rebuilt after wrap.
    /// </summary>
    private struct Marker
    {
        public ulong AbsoluteSampleIndex;
        public uint Color;
        public int Side;
    }

    /// <summary>
    /// Metadata for one visible image column. Invariant:
    ///     start_sample &lt;= rendered samples in this column &lt; end_sample
    /// Marker lookup uses this range to find the column that owns a sample index.
    /// </summary>
    private struct RenderedColumn
    {
        public bool Valid;
        public long ColumnIndex;
        public ulong StartSample;
        public ulong EndSample;
        public int VerticalOffsetRows;
    }

    /// <summary>Buffered startup column used before automatic centering is locked.</summary>
    private struct AnchorColumnMeta
    {
        public bool Valid;
        public long ColumnIndex;
        public ulong StartSample;
        public ulong EndSample;
    }

    // Caller-owned output image. Must be ARGB32 (PixelBuffer).
    private PixelBuffer? _image;

    // Current configuration.
    private Config _cfg = new();

    // Image dimensions cached from _image.
    private int _width;
    private int _height;

    // Rounded integer sample rate for coefficient calculations.
    private ulong _sampleRateInt = 48000;

    // Rounded integer BPH; valid only when _bphValid is true.
    private ulong _bphInt;

    // True once _cfg.Bph is valid and rendering may occur.
    private bool _bphValid;

    // Exact floating-point samples per displayed beat/column.
    private double _samplesPerColumnExact;

    // Absolute sample index of the first sample after reset(origin).
    private ulong _streamOriginSampleIndex;

    // Number of samples consumed since reset(origin).
    private ulong _processedSamplesSinceReset;

    // Absolute sample index where post-BPH rendering starts.
    private ulong _renderEpochSampleIndex;

    // Whether active start/end describe a live column.
    private bool _haveActiveColumn;

    // Logical column index since _renderEpochSampleIndex.
    private long _activeColumnIndex;

    // Absolute sample range for the active column.
    private ulong _activeStartSample;
    private ulong _activeEndSample;

    // State for simple DC removal.
    private double _dcMean;
    private double _dcAlpha;

    // Peak-envelope state for display normalization.
    private float _peakEnv = 1.0e-6f;

    // Current column bins, indexed by natural bucket.
    private float[] _currentColumn = Array.Empty<float>();

    // Number of post-BPH columns skipped for warmup.
    private int _warmupColumnsConsumed;

    // True after automatic center offset has been chosen.
    private bool _centerLocked;

    // Vertical offset applied to natural buckets before BucketToY().
    private int _internalVerticalOffsetRows;

    // Number of anchor columns collected so far.
    private int _anchorUsed;

    // Sum of anchor columns used to find dominant band.
    private float[] _anchorSum = Array.Empty<float>();

    // Buffered anchor column bin data.
    private float[] _anchorColumnsBuffer = Array.Empty<float>();

    // Metadata for buffered anchor columns.
    private AnchorColumnMeta[] _anchorColumnsMeta = Array.Empty<AnchorColumnMeta>();

    // Next screen column to write. Wraps modulo _width.
    private int _writeColumn;

    // Most recent completed screen column.
    private int _lastCompletedColumn = -1;

    // Metadata for each visible screen column.
    private RenderedColumn[] _renderedColumns = Array.Empty<RenderedColumn>();

    // Screen column that last rendered logical column k, indexed by k % _width
    // (-1 = none). Only one logical column per k % _width can be visible at a
    // time, so marker lookup can resolve a sample to its screen column without
    // scanning all _width column metadata entries.
    private int[] _screenColumnByLogicalIndex = Array.Empty<int>();

    // Stored sound bins for each visible screen column.
    // Layout: _renderedBins[x * _height + natural_bucket]. Used for marker-bleed cleanup.
    private float[] _renderedBins = Array.Empty<float>();

    // Active marker overlay entries.
    private readonly List<Marker> _activeMarkers = new();

    /*
        Initialize()
        ------------
        Allocates internal buffers sized to the caller-provided image.

        Intentionally does NOT require BPH. If cfg.Bph <= 0, the renderer starts in
        count-only mode (the app may need a few samples to estimate BPH first).
    */
    public bool Initialize(PixelBuffer imageArgb32, Config cfg)
    {
        if (imageArgb32 == null)
        {
            return false;
        }
        if (imageArgb32.Width <= 0 || imageArgb32.Height <= 0)
        {
            return false;
        }
        if (cfg.SampleRateHz <= 0.0 ||
            cfg.WarmupColumns < 0 || cfg.AnchorColumns <= 0)
        {
            return false;
        }

        _image = imageArgb32;
        _cfg = cfg;

        _width = _image.Width;
        _height = _image.Height;

        _currentColumn = new float[_height];
        _anchorSum = new float[_height];
        _anchorColumnsBuffer = new float[_cfg.AnchorColumns * _height];
        _anchorColumnsMeta = new AnchorColumnMeta[_cfg.AnchorColumns];
        _renderedColumns = new RenderedColumn[_width];
        _screenColumnByLogicalIndex = new int[_width];
        _renderedBins = new float[_width * _height];
        _activeMarkers.Clear();

        RecomputeDerived();
        ResetStateOnly(0);
        ClearWholeImage(_cfg.BackgroundColor);

        return true;
    }

    public void Reset()
    {
        Reset(0);
    }

    public void Reset(ulong nextInputAbsoluteSampleIndex)
    {
        if (_image == null)
        {
            return;
        }

        ResetStateOnly(nextInputAbsoluteSampleIndex);
        ClearWholeImage(_cfg.BackgroundColor);
    }

    /*
        RecomputeDerived()
        ------------------
        Recomputes values derived from configuration.
        sample_rate_hz is always required; bph is optional. If BPH is invalid,
        _bphValid becomes false and ProcessSamples only advances the sample counter.
    */
    private void RecomputeDerived()
    {
        _sampleRateInt = (ulong)Llround(_cfg.SampleRateHz);
        if (_sampleRateInt == 0)
        {
            _sampleRateInt = 1;
        }

        if (_cfg.Bph > 0.0)
        {
            _bphInt = (ulong)Llround(_cfg.Bph);
            if (_bphInt == 0)
            {
                _bphInt = 1;
            }

            _bphValid = true;
            _samplesPerColumnExact = (_cfg.SampleRateHz * 3600.0) / _cfg.Bph;
            if (_samplesPerColumnExact < 1.0)
            {
                _samplesPerColumnExact = 1.0;
            }
        }
        else
        {
            _bphInt = 0;
            _bphValid = false;
            _samplesPerColumnExact = 0.0;
        }

        double tc = Math.Max(1.0e-6, _cfg.DcTimeConstantSec);
        _dcAlpha = 1.0 / (tc * (double)_sampleRateInt);
        if (_dcAlpha > 1.0)
        {
            _dcAlpha = 1.0;
        }
    }

    private void ResetStateOnly(ulong nextInputAbsoluteSampleIndex)
    {
        _streamOriginSampleIndex = nextInputAbsoluteSampleIndex;
        _processedSamplesSinceReset = 0;

        _renderEpochSampleIndex = nextInputAbsoluteSampleIndex;

        _haveActiveColumn = false;
        _activeColumnIndex = 0;

        _activeStartSample = _renderEpochSampleIndex;
        _activeEndSample = _renderEpochSampleIndex + 1;

        _dcMean = 0.0;
        _peakEnv = 1.0e-6f;

        _warmupColumnsConsumed = 0;
        _centerLocked = false;
        _internalVerticalOffsetRows = 0;
        _anchorUsed = 0;

        _writeColumn = 0;
        _lastCompletedColumn = -1;

        Array.Clear(_currentColumn);
        Array.Clear(_anchorSum);
        Array.Clear(_anchorColumnsBuffer);
        Array.Clear(_anchorColumnsMeta);
        Array.Clear(_renderedColumns);
        Array.Fill(_screenColumnByLogicalIndex, -1);
        Array.Clear(_renderedBins);
        _activeMarkers.Clear();
    }

    /*
        ClearRenderStateKeepingSampleCounter()
        --------------------------------------
        Clears all visible/rendering state but preserves the absolute sample clock.
        Used when BPH becomes known/changes, sample rate changes, or vertical
        direction changes. Preserving the sample counter is the key sync requirement.
    */
    private void ClearRenderStateKeepingSampleCounter()
    {
        _renderEpochSampleIndex = NextInputAbsoluteSampleIndex();

        _haveActiveColumn = false;
        _activeColumnIndex = 0;

        _activeStartSample = _renderEpochSampleIndex;
        _activeEndSample = _renderEpochSampleIndex + 1;

        _dcMean = 0.0;
        _peakEnv = 1.0e-6f;

        _warmupColumnsConsumed = 0;
        _centerLocked = false;
        _internalVerticalOffsetRows = 0;
        _anchorUsed = 0;

        _writeColumn = 0;
        _lastCompletedColumn = -1;

        Array.Clear(_currentColumn);
        Array.Clear(_anchorSum);
        Array.Clear(_anchorColumnsBuffer);
        Array.Clear(_anchorColumnsMeta);
        Array.Clear(_renderedColumns);
        Array.Fill(_screenColumnByLogicalIndex, -1);
        Array.Clear(_renderedBins);
        _activeMarkers.Clear();
    }

    public void SetSoundColor(uint color)
    {
        _cfg.SoundColor = color;
    }

    /// <summary>
    /// Enables/disables the live redraw of the in-progress column. Used by the
    /// deadline-degradation ladder; call on the rendering thread between frames.
    /// </summary>
    public void SetLivePreviewCurrentColumn(bool enabled)
    {
        _cfg.LivePreviewCurrentColumn = enabled;
    }

    public void SetBackgroundColor(uint color)
    {
        _cfg.BackgroundColor = color;
    }

    /// <summary>
    /// Re-tints the whole image to a new background color, rebuilding every stored
    /// column from the retained intensity bins. Cheap (one small bitmap) and meant to
    /// be called between frames on the rendering thread. Wave/marker colors are kept.
    /// </summary>
    public void Recolor(uint backgroundColor)
    {
        _cfg.BackgroundColor = backgroundColor;
        if (_image == null)
        {
            return;
        }

        for (int x = 0; x < _width; ++x)
        {
            ClearColumn(x, _cfg.BackgroundColor);
        }

        for (int x = 0; x < _width; ++x)
        {
            RenderedColumn meta = _renderedColumns[x];
            if (!meta.Valid)
            {
                continue;
            }

            int binsBase = RenderedBinsBase(x);
            if (binsBase < 0)
            {
                continue;
            }

            RenderBinsToColumn(x, _renderedBins, binsBase, meta);
        }
    }

    public void SetBph(double bph)
    {
        bool wasValid = _bphValid;
        double oldBph = _cfg.Bph;

        _cfg.Bph = bph;
        RecomputeDerived();

        if (wasValid != _bphValid || oldBph != bph)
        {
            ClearRenderStateKeepingSampleCounter();
            if (_image != null)
            {
                ClearWholeImage(_cfg.BackgroundColor);
            }
        }
    }

    public void SetSampleRate(double sampleRateHz)
    {
        if (sampleRateHz <= 0.0)
        {
            return;
        }

        double oldRate = _cfg.SampleRateHz;
        _cfg.SampleRateHz = sampleRateHz;
        RecomputeDerived();

        if (oldRate != sampleRateHz && _bphValid)
        {
            ClearRenderStateKeepingSampleCounter();
            if (_image != null)
            {
                ClearWholeImage(_cfg.BackgroundColor);
            }
        }
    }

    public void SetVerticalTimeDirection(VerticalTimeDirection direction)
    {
        if (_cfg.Direction == direction)
        {
            return;
        }

        _cfg.Direction = direction;

        if (_image != null)
        {
            ClearRenderStateKeepingSampleCounter();
            ClearWholeImage(_cfg.BackgroundColor);
        }
    }

    /*
        ColumnBoundarySample()
        ----------------------
        Computes absolute sample index for a column boundary.
        Boundary 0 is _renderEpochSampleIndex.
        Boundary k:  _renderEpochSampleIndex + round(k * samples_per_column_exact)
        Supports non-integer samples per column without per-sample phase math.
    */
    private ulong ColumnBoundarySample(long boundaryIndex)
    {
        if (boundaryIndex <= 0 || !_bphValid)
        {
            return _renderEpochSampleIndex;
        }

        // long double on the Windows target collapses to double; use double here.
        double exactOffset = (double)boundaryIndex * _samplesPerColumnExact;

        ulong offset = (ulong)Llround(exactOffset);
        return _renderEpochSampleIndex + offset;
    }

    private void StartActiveColumn(long columnIndex)
    {
        _haveActiveColumn = true;
        _activeColumnIndex = columnIndex;

        _activeStartSample = ColumnBoundarySample(columnIndex);
        _activeEndSample = ColumnBoundarySample(columnIndex + 1);

        if (_activeEndSample <= _activeStartSample)
        {
            _activeEndSample = _activeStartSample + 1;
        }

        ClearCurrentBuckets();
    }

    private void ClearWholeImage(uint color)
    {
        if (_image == null)
        {
            return;
        }

        uint[] pixels = _image.Pixels;
        for (int y = 0; y < _height; ++y)
        {
            int rowBase = y * _width;
            for (int x = 0; x < _width; ++x)
            {
                pixels[rowBase + x] = color;
            }
        }
    }

    private void ClearColumn(int x, uint color)
    {
        if (_image == null || x < 0 || x >= _width)
        {
            return;
        }

        uint[] pixels = _image.Pixels;
        for (int y = 0; y < _height; ++y)
        {
            pixels[y * _width + x] = color;
        }
    }

    private void ClearCurrentBuckets()
    {
        Array.Clear(_currentColumn);
    }

    /*
        SampleToBucketInRange()
        -----------------------
        Converts an absolute sample index into a natural bucket inside one column.
        Range:   start_sample <= sample < end_sample
        Output:  0 <= bucket < _height
        Used by both signal sample placement and marker sample placement, which is
        what keeps markers aligned with signal pixels.

        Port note: the original __uint128_t fast path is unavailable on the Windows
        (MSVC) target, so the long-double fallback branch is the active path. C# has
        no long double, so double is used (matching MSVC where long double == double).
    */
    private int SampleToBucketInRange(ulong absoluteSampleIndex,
                                      ulong startSample,
                                      ulong endSample)
    {
        if (_height <= 0 || endSample <= startSample)
        {
            return 0;
        }

        if (absoluteSampleIndex < startSample)
        {
            absoluteSampleIndex = startSample;
        }
        if (absoluteSampleIndex >= endSample)
        {
            absoluteSampleIndex = endSample - 1;
        }

        ulong offset = absoluteSampleIndex - startSample;
        ulong length = endSample - startSample;

        int bucket = (int)(((double)offset * (double)_height) / (double)length);
        if (bucket < 0)
        {
            bucket = 0;
        }
        if (bucket >= _height)
        {
            bucket = _height - 1;
        }
        return bucket;
    }

    private int ApplyVerticalOffset(int naturalBucket, int verticalOffsetRows)
    {
        if (_height <= 0)
        {
            return 0;
        }

        return ((naturalBucket + verticalOffsetRows) % _height + _height) % _height;
    }

    /*
        BucketToY()
        -----------
        Converts logical display bucket to physical image y coordinate.
        y=0 at the top. Either visual time direction is supported.
    */
    private int BucketToY(int bucket)
    {
        if (_cfg.Direction == VerticalTimeDirection.TopDown)
        {
            return bucket;
        }

        return (_height - 1) - bucket;
    }

    private static uint LerpColor(uint bg, uint fg, float t)
    {
        if (t < 0.0f)
        {
            t = 0.0f;
        }
        if (t > 1.0f)
        {
            t = 1.0f;
        }

        float tt = t;
        int Lerp1(int a, int b) => (int)Lround(a + (b - a) * tt);

        return Argb.Rgba(
            (byte)Lerp1(Argb.R(bg), Argb.R(fg)),
            (byte)Lerp1(Argb.G(bg), Argb.G(fg)),
            (byte)Lerp1(Argb.B(bg), Argb.B(fg)),
            (byte)Lerp1(Argb.A(bg), Argb.A(fg)));
    }

    private static int ArgmaxSmoothed5(float[] v)
    {
        if (v.Length == 0)
        {
            return 0;
        }

        int bestIdx = 0;
        float bestVal = float.NegativeInfinity;

        for (int i = 0; i < v.Length; ++i)
        {
            int lo = Math.Max(0, i - 2);
            int hi = Math.Min(v.Length - 1, i + 2);

            float sum = 0.0f;
            int n = 0;
            for (int j = lo; j <= hi; ++j)
            {
                sum += v[j];
                ++n;
            }

            float avg = (n > 0) ? (sum / (float)n) : 0.0f;
            if (avg > bestVal)
            {
                bestVal = avg;
                bestIdx = i;
            }
        }

        return bestIdx;
    }

    private static int NormalizeMarkerSidePixels(int requestedSide)
    {
        if (requestedSide <= 1)
        {
            return 1;
        }

        int d1 = Math.Abs(requestedSide - 1);
        int d3 = Math.Abs(requestedSide - 3);
        int d9 = Math.Abs(requestedSide - 9);

        if (d1 <= d3 && d1 <= d9)
        {
            return 1;
        }
        if (d3 <= d1 && d3 <= d9)
        {
            return 3;
        }
        return 9;
    }

    private static int MarkerRadiusColumnsFromSide(int normalizedSide)
    {
        return normalizedSide / 2;
    }

    private static int MaxSupportedMarkerRadiusColumns()
    {
        return MarkerRadiusColumnsFromSide(9);
    }

    // Returns the base offset into _renderedBins for a column, or -1 if out of range.
    private int RenderedBinsBase(int column)
    {
        if (column < 0 || column >= _width)
        {
            return -1;
        }
        return column * _height;
    }

    private void ClearRenderedColumnStorage(int column)
    {
        if (column < 0 || column >= _width)
        {
            return;
        }

        _renderedColumns[column] = new RenderedColumn();
        int dstBase = RenderedBinsBase(column);
        if (dstBase >= 0)
        {
            Array.Clear(_renderedBins, dstBase, _height);
        }
    }

    /*
        RenderBinsToColumn()
        --------------------
        Renders a complete column from bin data. Also stores a copy of the bins in
        _renderedBins so the column can be rebuilt later for marker-bleed cleanup.
    */
    private void RenderBinsToColumn(int x, float[] bins, int binsBase, RenderedColumn meta)
    {
        if (_image == null || bins == null || x < 0 || x >= _width)
        {
            return;
        }

        int storedBase = RenderedBinsBase(x);
        if (storedBase >= 0)
        {
            Array.Copy(bins, binsBase, _renderedBins, storedBase, _height);
        }

        ClearColumn(x, _cfg.BackgroundColor);

        uint[] pixels = _image.Pixels;
        for (int naturalBucket = 0; naturalBucket < _height; ++naturalBucket)
        {
            float v = bins[binsBase + naturalBucket];
            if (v < 0.0f)
            {
                v = 0.0f;
            }
            if (v > 1.0f)
            {
                v = 1.0f;
            }

            // std::pow(float, float) resolves to the float overload -> MathF.Pow.
            v = MathF.Pow(v, _cfg.Gamma);

            int displayBucket = ApplyVerticalOffset(naturalBucket, meta.VerticalOffsetRows);
            int y = BucketToY(displayBucket);

            pixels[y * _width + x] = LerpColor(_cfg.BackgroundColor, _cfg.SoundColor, v);
        }

        _renderedColumns[x] = meta;
        if (meta.Valid)
        {
            _screenColumnByLogicalIndex[(int)(meta.ColumnIndex % _width)] = x;
        }
        ReapplyMarkersForColumn(x);
    }

    private void RenderCurrentColumnToImage()
    {
        if (!_centerLocked || !_haveActiveColumn)
        {
            return;
        }

        RenderedColumn meta = new RenderedColumn
        {
            Valid = true,
            ColumnIndex = _activeColumnIndex,
            StartSample = _activeStartSample,
            EndSample = _activeEndSample,
            VerticalOffsetRows = _internalVerticalOffsetRows
        };

        RenderBinsToColumn(_writeColumn, _currentColumn, 0, meta);
    }

    /*
        CommitAnchorColumn()
        --------------------
        Buffers one completed post-warmup column while automatic centering is still
        being computed. Once enough anchor columns are collected, the dominant
        vertical band is found and shifted toward the middle of the image.
    */
    private void CommitAnchorColumn(float[] column, int columnBase, AnchorColumnMeta meta)
    {
        if (column == null || _anchorUsed >= _cfg.AnchorColumns)
        {
            return;
        }

        int dstBase = _anchorUsed * _height;
        Array.Copy(column, columnBase, _anchorColumnsBuffer, dstBase, _height);
        _anchorColumnsMeta[_anchorUsed] = meta;

        for (int i = 0; i < _height; ++i)
        {
            _anchorSum[i] += column[columnBase + i];
        }

        ++_anchorUsed;

        if (_anchorUsed == _cfg.AnchorColumns)
        {
            int dominantBucket = ArgmaxSmoothed5(_anchorSum);
            _internalVerticalOffsetRows = (_height / 2) - dominantBucket;
            _centerLocked = true;
            FlushBufferedAnchorColumns();
        }
    }

    private void FlushBufferedAnchorColumns()
    {
        if (!_centerLocked)
        {
            return;
        }

        for (int c = 0; c < _cfg.AnchorColumns; ++c)
        {
            AnchorColumnMeta am = _anchorColumnsMeta[c];
            if (!am.Valid)
            {
                continue;
            }

            int binsBase = c * _height;

            RenderedColumn meta = new RenderedColumn
            {
                Valid = true,
                ColumnIndex = am.ColumnIndex,
                StartSample = am.StartSample,
                EndSample = am.EndSample,
                VerticalOffsetRows = _internalVerticalOffsetRows
            };

            RenderBinsToColumn(_writeColumn, _anchorColumnsBuffer, binsBase, meta);

            _lastCompletedColumn = _writeColumn;
            _writeColumn = (_writeColumn + 1) % _width;

            ClearRenderedColumnStorage(_writeColumn);
            RebuildColumnNeighborhood(_writeColumn);
        }

        ClearCurrentBuckets();
        PruneOldMarkers();
    }

    private void RebuildColumnNeighborhood(int centerColumn)
    {
        /*
            Marker-bleed cleanup
            --------------------
            A marker is not always confined to its center column. A 9x9 marker has a
            horizontal radius of 4 columns. If column X wraps and is reused, an old
            marker that touched X may also have left pixels in X-1, X+1, etc.
            Clearing only X is therefore insufficient.

            This rebuilds a small neighborhood around the reused column:
                1) clear all columns in the neighborhood
                2) redraw the still-valid sound columns from _renderedBins
                3) reapply markers column-by-column
            Since ReapplyMarkersForColumn(column) only draws the portion of each
            marker that belongs in that column, the final overlay is clean and
            deterministic.
        */
        if (_image == null || centerColumn < 0 || centerColumn >= _width)
        {
            return;
        }

        int radius = MaxSupportedMarkerRadiusColumns();

        int x0 = Math.Max(0, centerColumn - radius);
        int x1 = Math.Min(_width - 1, centerColumn + radius);

        for (int x = x0; x <= x1; ++x)
        {
            ClearColumn(x, _cfg.BackgroundColor);
        }

        for (int x = x0; x <= x1; ++x)
        {
            RenderedColumn meta = _renderedColumns[x];
            if (!meta.Valid)
            {
                continue;
            }

            int binsBase = RenderedBinsBase(x);
            if (binsBase < 0)
            {
                continue;
            }

            RenderBinsToColumn(x, _renderedBins, binsBase, meta);
        }
    }

    /*
        FinalizeCurrentColumnAndAdvance()
        ---------------------------------
        Handles a completed active column.
        Stages:
            1. warmup columns are ignored
            2. anchor columns are buffered until center offset is locked
            3. normal columns are rendered to the circular image
        After a screen column is consumed, the next screen column is invalidated and
        its marker-bleed neighborhood is rebuilt.
    */
    private void FinalizeCurrentColumnAndAdvance()
    {
        if (!_haveActiveColumn)
        {
            return;
        }

        AnchorColumnMeta completed = new AnchorColumnMeta
        {
            Valid = true,
            ColumnIndex = _activeColumnIndex,
            StartSample = _activeStartSample,
            EndSample = _activeEndSample
        };

        if (_warmupColumnsConsumed < _cfg.WarmupColumns)
        {
            ++_warmupColumnsConsumed;
            ClearCurrentBuckets();
            PruneOldMarkers();
            return;
        }

        if (!_centerLocked)
        {
            CommitAnchorColumn(_currentColumn, 0, completed);
            ClearCurrentBuckets();
            PruneOldMarkers();
            return;
        }

        RenderedColumn meta = new RenderedColumn
        {
            Valid = true,
            ColumnIndex = completed.ColumnIndex,
            StartSample = completed.StartSample,
            EndSample = completed.EndSample,
            VerticalOffsetRows = _internalVerticalOffsetRows
        };

        RenderBinsToColumn(_writeColumn, _currentColumn, 0, meta);

        _lastCompletedColumn = _writeColumn;
        _writeColumn = (_writeColumn + 1) % _width;

        ClearRenderedColumnStorage(_writeColumn);
        RebuildColumnNeighborhood(_writeColumn);

        ClearCurrentBuckets();
        PruneOldMarkers();
    }

    /*
        ProcessSamples()
        ----------------
        Main streaming hot path.
        Before BPH: count samples only.
        After BPH, for each sample:
            - start a column if needed
            - finalize columns crossed by this sample
            - remove DC
            - rectify magnitude
            - normalize by peak envelope
            - update the strongest value in the proper bucket
            - advance the absolute sample counter
    */
    public void ProcessSamples(ReadOnlySpan<float> samples)
    {
        if (_image == null || samples.Length == 0)
        {
            return;
        }

        int count = samples.Length;

        if (!_bphValid)
        {
            _processedSamplesSinceReset += (ulong)count;
            return;
        }

        float[] currentColumn = _currentColumn;

        for (int i = 0; i < count; ++i)
        {
            ulong absIndex = _streamOriginSampleIndex + _processedSamplesSinceReset;

            if (!_haveActiveColumn)
            {
                StartActiveColumn(0);
            }

            while (absIndex >= _activeEndSample)
            {
                FinalizeCurrentColumnAndAdvance();
                StartActiveColumn(_activeColumnIndex + 1);
            }

            float x = samples[i];

            _dcMean += _dcAlpha * ((double)x - _dcMean);
            float dcRemoved = x - (float)_dcMean;
            float mag = Math.Abs(dcRemoved);

            if (mag > _peakEnv)
            {
                _peakEnv = mag;
            }
            else
            {
                _peakEnv *= _cfg.PeakDecay;
                if (_peakEnv < 1.0e-6f)
                {
                    _peakEnv = 1.0e-6f;
                }
            }

            float z = mag / _peakEnv;
            if (z > 1.0f)
            {
                z = 1.0f;
            }

            int naturalBucket =
                SampleToBucketInRange(absIndex, _activeStartSample, _activeEndSample);

            if (z > currentColumn[naturalBucket])
            {
                currentColumn[naturalBucket] = z;
            }

            ++_processedSamplesSinceReset;
        }

        if (_cfg.LivePreviewCurrentColumn && _centerLocked && _haveActiveColumn)
        {
            RenderCurrentColumnToImage();
        }
    }

    /*
        LookupRenderedColumnBySampleIndex()
        -----------------------------------
        Finds which visible column owns the supplied absolute sample index.
        Deterministic range lookup. A column owns sample N if:
            start_sample <= N < end_sample

        Columns own contiguous, disjoint sample ranges whose boundaries are
        round(k * samples_per_column_exact) from the render epoch, so the owning
        logical column index is within +/-1 of the closed-form estimate below
        (boundary rounding shifts each edge by at most 0.5 samples and
        samples_per_column_exact >= 1). The metadata range check stays
        authoritative, so the result is identical to a full linear scan.
    */
    private int LookupRenderedColumnBySampleIndex(ulong absoluteSampleIndex)
    {
        if (_width <= 0 || !_bphValid || _samplesPerColumnExact <= 0.0 ||
            absoluteSampleIndex < _renderEpochSampleIndex)
        {
            return -1;
        }

        ulong relativeSample = absoluteSampleIndex - _renderEpochSampleIndex;
        long estimate = (long)((double)relativeSample / _samplesPerColumnExact);

        for (long k = estimate - 1; k <= estimate + 1; ++k)
        {
            if (k < 0)
            {
                continue;
            }

            int x = _screenColumnByLogicalIndex[(int)(k % _width)];
            if (x < 0)
            {
                continue;
            }

            RenderedColumn meta = _renderedColumns[x];
            if (meta.Valid &&
                meta.ColumnIndex == k &&
                absoluteSampleIndex >= meta.StartSample &&
                absoluteSampleIndex < meta.EndSample)
            {
                return x;
            }
        }

        return -1;
    }

    /*
        MapRenderedSampleToPixel()
        --------------------------
        Converts an absolute sample index to an image pixel using the metadata of
        the column that actually rendered that sample. This is the marker alignment
        guarantee: marker sample N maps through the same column range as signal
        sample N.
    */
    private bool MapRenderedSampleToPixel(ulong absoluteSampleIndex,
                                          out int outX,
                                          out int outY)
    {
        int x = LookupRenderedColumnBySampleIndex(absoluteSampleIndex);
        if (x < 0)
        {
            outX = -1;
            outY = -1;
            return false;
        }

        RenderedColumn meta = _renderedColumns[x];

        int naturalBucket =
            SampleToBucketInRange(absoluteSampleIndex, meta.StartSample, meta.EndSample);
        int displayBucket = ApplyVerticalOffset(naturalBucket, meta.VerticalOffsetRows);
        int y = BucketToY(displayBucket);

        outX = x;
        outY = y;

        return true;
    }

    private void DrawCenteredMarkerBlock(int x, int y, uint color, int markerSidePixels)
    {
        if (_image == null)
        {
            return;
        }

        int side = NormalizeMarkerSidePixels(markerSidePixels);
        int radius = side / 2;

        uint[] pixels = _image.Pixels;
        for (int dy = -radius; dy <= radius; ++dy)
        {
            int yy = y + dy;
            if (yy < 0 || yy >= _height)
            {
                continue;
            }

            int rowBase = yy * _width;
            for (int dx = -radius; dx <= radius; ++dx)
            {
                int xx = x + dx;
                if (xx < 0 || xx >= _width)
                {
                    continue;
                }
                pixels[rowBase + xx] = color;
            }
        }
    }

    private void DrawCenteredMarkerContributionToColumn(int targetColumn,
                                                        int centerX,
                                                        int centerY,
                                                        uint color,
                                                        int markerSidePixels)
    {
        if (_image == null || targetColumn < 0 || targetColumn >= _width)
        {
            return;
        }

        int side = NormalizeMarkerSidePixels(markerSidePixels);
        int radius = side / 2;

        if (targetColumn < centerX - radius || targetColumn > centerX + radius)
        {
            return;
        }

        uint[] pixels = _image.Pixels;
        for (int dy = -radius; dy <= radius; ++dy)
        {
            int yy = centerY + dy;
            if (yy < 0 || yy >= _height)
            {
                continue;
            }

            pixels[yy * _width + targetColumn] = color;
        }
    }

    private void ReapplyMarkersForColumn(int column)
    {
        if (_image == null || column < 0 || column >= _width)
        {
            return;
        }

        foreach (Marker m in _activeMarkers)
        {
            if (!MapRenderedSampleToPixel(m.AbsoluteSampleIndex, out int cx, out int cy))
            {
                continue;
            }

            DrawCenteredMarkerContributionToColumn(column, cx, cy, m.Color, m.Side);
        }
    }

    private void AddPersistentMarkerFromAbsoluteSample(ulong absoluteSampleIndex,
                                                       uint color,
                                                       int markerSidePixels)
    {
        /*
            Markers are stored persistently so they can be redrawn after sound
            columns are refreshed.

            Important: absolute_sample_index is not adjusted here. The caller must
            pass the original event sample index in the same clock used by
            ProcessSamples().
        */
        Marker m = new Marker
        {
            AbsoluteSampleIndex = absoluteSampleIndex,
            Color = color,
            Side = NormalizeMarkerSidePixels(markerSidePixels)
        };
        _activeMarkers.Add(m);

        if (MapRenderedSampleToPixel(absoluteSampleIndex, out int x, out int y))
        {
            DrawCenteredMarkerBlock(x, y, color, m.Side);
        }
    }

    /*
        PruneOldMarkers()
        -----------------
        Removes marker entries that are older than the oldest visible column.
        Keeps _activeMarkers bounded during long-running streams.
    */
    private void PruneOldMarkers()
    {
        ulong oldestVisible = 0;
        bool haveVisible = false;

        foreach (RenderedColumn meta in _renderedColumns)
        {
            if (!meta.Valid)
            {
                continue;
            }

            if (!haveVisible || meta.StartSample < oldestVisible)
            {
                oldestVisible = meta.StartSample;
                haveVisible = true;
            }
        }

        if (!haveVisible)
        {
            return;
        }

        _activeMarkers.RemoveAll(m => m.AbsoluteSampleIndex < oldestVisible);
    }

    public void MarkAEventAbsoluteSampleIndex(ulong absoluteSampleIndex,
                                              uint color,
                                              int markerSidePixels)
    {
        AddPersistentMarkerFromAbsoluteSample(absoluteSampleIndex, color, markerSidePixels);
    }

    public void MarkCEventAbsoluteSampleIndex(ulong absoluteSampleIndex,
                                              uint color,
                                              int markerSidePixels)
    {
        AddPersistentMarkerFromAbsoluteSample(absoluteSampleIndex, color, markerSidePixels);
    }

    // --- Accessors (mirror the C++ inline getters) ---

    public int ImageWidth => _width;
    public int ImageHeight => _height;
    public int CurrentColumn => _writeColumn;
    public int LastCompletedColumn => _lastCompletedColumn;

    public ulong StreamOriginSampleIndex => _streamOriginSampleIndex;
    public ulong ProcessedSamplesSinceReset => _processedSamplesSinceReset;
    public ulong NextInputAbsoluteSampleIndex() => _streamOriginSampleIndex + _processedSamplesSinceReset;

    public long CurrentBeatIndex => _activeColumnIndex;
    public bool BandCenterLocked => _centerLocked;
    public int CurrentVerticalOffsetRows => _internalVerticalOffsetRows;

    public bool BphValid => _bphValid;
    public double CurrentBph => _bphValid ? _cfg.Bph : 0.0;
    public double SamplesPerColumnExact => _samplesPerColumnExact;

    public VerticalTimeDirection Direction => _cfg.Direction;

    // --- Rounding helpers matching C++ std::llround / std::lround (round half away from zero) ---

    private static long Llround(double value)
        => (long)Math.Round(value, MidpointRounding.AwayFromZero);

    private static long Lround(float value)
        => (long)Math.Round((double)value, MidpointRounding.AwayFromZero);
}
