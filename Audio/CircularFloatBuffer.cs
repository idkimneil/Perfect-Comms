using System;

namespace VoiceChatPlugin.Audio;

/// <summary>
/// Fix: Removed the internal _lock from CircularFloatBuffer.
/// Previously BufferedSampleProvider held _stateLock then called into this class
/// which took its own _lock — nested locking that deadlocks under CrossOver's
/// lock emulation. All callers (BufferedSampleProvider) already serialize access
/// via their own lock, so the inner lock here was redundant and dangerous.
/// </summary>
internal class CircularFloatBuffer
{
    private readonly float[] _buffer;
    private int _writePos;
    private int _readPos;
    private int _count;

    public int MaxLength => _buffer.Length;

    // NOTE: Count is now unsynchronized. Callers must hold their own lock.
    public int Count => _count;

    public CircularFloatBuffer(int size) => _buffer = new float[size];

    public int Write(float[] data, int offset, int count)
    {
        int avail = _buffer.Length - _count;
        if (count > avail) count = avail;
        int part1 = Math.Min(_buffer.Length - _writePos, count);
        Array.Copy(data, offset, _buffer, _writePos, part1);
        _writePos = (_writePos + part1) % _buffer.Length;
        if (part1 < count)
        {
            int part2 = count - part1;
            Array.Copy(data, offset + part1, _buffer, _writePos, part2);
            _writePos += part2;
        }
        _count += count;
        return count;
    }

    public int Read(float[] data, int offset, int count)
    {
        if (count > _count) count = _count;
        int part1 = Math.Min(_buffer.Length - _readPos, count);
        Buffer.BlockCopy(_buffer, _readPos * 4, data, offset * 4, part1 * 4);
        _readPos = (_readPos + part1) % _buffer.Length;
        if (part1 < count)
        {
            int part2 = count - part1;
            Buffer.BlockCopy(_buffer, _readPos * 4, data, (offset + part1) * 4, part2 * 4);
            _readPos += part2;
        }
        _count -= count;
        return count;
    }

    public void Discard(int count)
    {
        count = Math.Min(_count, count);
        _readPos = (_readPos + count) % _buffer.Length;
        _count -= count;
    }

    public void Reset()
    {
        _count = 0;
        _readPos = 0;
        _writePos = 0;
    }
}