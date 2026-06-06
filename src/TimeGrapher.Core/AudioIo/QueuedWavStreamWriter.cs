using System.Collections.Concurrent;
using System.Buffers;

namespace TimeGrapher.Core.AudioIo;

/// <summary>
/// Bounded asynchronous WAV writer used by live analysis. File I/O stays off the
/// analysis thread; full queues drop recording blocks instead of stalling analysis.
/// </summary>
public sealed class QueuedWavStreamWriter : ISampleWriter
{
    private const int DefaultQueueCapacity = 128;
    private static readonly TimeSpan CloseTimeout = TimeSpan.FromSeconds(5);

    private readonly int _queueCapacity;
    private readonly object _stateLock = new();
    private BlockingCollection<QueuedSampleBlock>? _queue;
    private WavStreamWriter? _inner;
    private Thread? _thread;
    private volatile bool _writerFailed;
    private ulong _droppedBlocks;

    private sealed class QueuedSampleBlock
    {
        public float[] Buffer = Array.Empty<float>();
        public int Length;
    }

    public QueuedWavStreamWriter(int queueCapacity = DefaultQueueCapacity)
    {
        _queueCapacity = Math.Max(1, queueCapacity);
    }

    public ulong DroppedBlocks => _droppedBlocks;
    public bool IsOpen => _inner?.IsOpen == true && _queue != null;

    public bool Open(string filePath, int sampleRate, int channels)
    {
        lock (_stateLock)
        {
            if (_inner != null)
            {
                return false;
            }

            var inner = new WavStreamWriter();
            if (!inner.Open(filePath, sampleRate, channels))
            {
                inner.Dispose();
                return false;
            }

            _writerFailed = false;
            _droppedBlocks = 0;
            _inner = inner;
            _queue = new BlockingCollection<QueuedSampleBlock>(boundedCapacity: _queueCapacity);
            _thread = new Thread(WriterLoop)
            {
                Name = "WavWriter",
                IsBackground = true,
                Priority = ThreadPriority.Normal,
            };
            _thread.Start();
            return true;
        }
    }

    public bool Write(ReadOnlySpan<float> samples)
    {
        if (samples.Length == 0)
        {
            return true;
        }

        BlockingCollection<QueuedSampleBlock>? queue = _queue;
        if (queue == null || queue.IsAddingCompleted || _writerFailed)
        {
            return false;
        }

        float[] buffer = ArrayPool<float>.Shared.Rent(samples.Length);
        samples.CopyTo(buffer);
        var block = new QueuedSampleBlock
        {
            Buffer = buffer,
            Length = samples.Length,
        };

        try
        {
            if (queue.TryAdd(block))
            {
                return true;
            }
        }
        catch (InvalidOperationException)
        {
        }

        ArrayPool<float>.Shared.Return(buffer);
        Interlocked.Increment(ref _droppedBlocks);
        return false;
    }

    public bool Close()
    {
        BlockingCollection<QueuedSampleBlock>? queue;
        Thread? thread;
        WavStreamWriter? inner;

        lock (_stateLock)
        {
            queue = _queue;
            thread = _thread;
            inner = _inner;
        }

        if (queue == null || inner == null)
        {
            return true;
        }

        queue.CompleteAdding();
        bool joined = thread == null || thread.Join(CloseTimeout);
        if (!joined)
        {
            Console.Error.WriteLine("QueuedWavStreamWriter: writer thread did not stop before timeout");
            return false;
        }

        lock (_stateLock)
        {
            if (ReferenceEquals(_queue, queue))
            {
                _queue = null;
                _thread = null;
                _inner = null;
            }
        }

        bool closed = inner.Close();
        queue.Dispose();
        inner.Dispose();
        return joined && closed && !_writerFailed;
    }

    public void Dispose()
    {
        Close();
    }

    private void WriterLoop()
    {
        BlockingCollection<QueuedSampleBlock>? queue;
        WavStreamWriter? inner;
        lock (_stateLock)
        {
            queue = _queue;
            inner = _inner;
        }

        if (queue == null || inner == null)
        {
            return;
        }

        try
        {
            foreach (QueuedSampleBlock block in queue.GetConsumingEnumerable())
            {
                try
                {
                    if (!_writerFailed && !inner.Write(block.Buffer.AsSpan(0, block.Length)))
                    {
                        _writerFailed = true;
                    }
                }
                finally
                {
                    ArrayPool<float>.Shared.Return(block.Buffer);
                }

                if (_writerFailed)
                {
                    DrainQueuedBlocks(queue);
                    return;
                }
            }
        }
        catch (InvalidOperationException)
        {
            _writerFailed = true;
        }
    }

    private static void DrainQueuedBlocks(BlockingCollection<QueuedSampleBlock> queue)
    {
        while (queue.TryTake(out QueuedSampleBlock? block))
        {
            ArrayPool<float>.Shared.Return(block.Buffer);
        }
    }
}
