using System.Buffers;
using System.Text;

namespace RepoQL.DevHarness.Proxy;

/// <summary>
/// Purpose: Reads newline-delimited JSON from a stream while handling partial reads.
/// Complexity: Buffers bytes across reads and normalizes CRLF so proxy loops can stay simple.
/// </summary>
internal sealed class LineBufferReader
{
    private readonly Stream _stream;
    private readonly byte[] _buffer;
    private readonly ArrayBufferWriter<byte> _lineBuffer = new();
    private int _bufferCount;
    private int _bufferOffset;

    public LineBufferReader(Stream stream, int bufferSize = 4096)
    {
        _stream = stream;
        _buffer = new byte[bufferSize];
    }

    public async ValueTask<string?> ReadLineAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            if (_bufferOffset < _bufferCount)
            {
                var newlineIndex = Array.IndexOf(_buffer, (byte)'\n', _bufferOffset, _bufferCount - _bufferOffset);
                if (newlineIndex >= 0)
                {
                    AppendSegment(_bufferOffset, newlineIndex - _bufferOffset);
                    _bufferOffset = newlineIndex + 1;

                    var line = DecodeLine(_lineBuffer.WrittenSpan);
                    _lineBuffer.Clear();

                    if (_bufferOffset >= _bufferCount)
                    {
                        _bufferOffset = 0;
                        _bufferCount = 0;
                    }

                    return line;
                }

                AppendSegment(_bufferOffset, _bufferCount - _bufferOffset);
                _bufferOffset = 0;
                _bufferCount = 0;
            }

            _bufferCount = await _stream.ReadAsync(_buffer, cancellationToken).ConfigureAwait(false);
            if (_bufferCount == 0)
            {
                if (_lineBuffer.WrittenCount == 0)
                    return null;

                var finalLine = DecodeLine(_lineBuffer.WrittenSpan);
                _lineBuffer.Clear();
                return finalLine;
            }

            _bufferOffset = 0;
        }
    }

    private void AppendSegment(int offset, int count)
    {
        if (count <= 0)
            return;

        _lineBuffer.Write(_buffer.AsSpan(offset, count));
    }

    private static string DecodeLine(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length > 0 && bytes[^1] == '\r')
            bytes = bytes[..^1];

        return Encoding.UTF8.GetString(bytes);
    }
}
