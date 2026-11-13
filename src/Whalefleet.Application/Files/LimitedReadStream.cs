using Whalefleet.Application.Common;

namespace Whalefleet.Application.Files;

/// <summary>
/// 带上限的读取流：超过字节上限就中止，并抛出 <see cref="BusinessException"/>。
/// </summary>
/// <remarks>
/// <para><b>为什么必须在读的过程中计数</b>：Kestrel 的 <c>MaxRequestBodySize</c> 与
/// <c>FormOptions.MultipartBodyLengthLimit</c> 都是<b>框架级</b>上限，
/// 而本项目要支持 20 GB 的上传——框架默认值（30 MB / 128 MB）远不够，
/// 必须放开。可放开之后，限制就得由应用层自己执行。</para>
/// <para><b>为什么不能「读完整份再判大小」</b>：那样一个超限的请求已经把
/// 磁盘（若是落盘方案）或内存（若是缓冲方案）写满了——
/// 检查发生得太晚，损失已经造成。</para>
/// </remarks>
public sealed class LimitedReadStream : Stream
{
    private readonly Stream _inner;
    private readonly long _limit;
    private long _read;

    /// <summary>
    /// 初始化受限读取流。
    /// </summary>
    /// <param name="inner">底层流。</param>
    /// <param name="limit">字节上限。</param>
    /// <exception cref="ArgumentNullException"><paramref name="inner"/> 为 <see langword="null"/>。</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="limit"/> 非正数。</exception>
    public LimitedReadStream(Stream inner, long limit)
    {
        ArgumentNullException.ThrowIfNull(inner);

        if (limit <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(limit), limit, "字节上限必须为正数。");
        }

        _inner = inner;
        _limit = limit;
    }

    /// <summary>
    /// 已读取的字节数。
    /// </summary>
    public long BytesRead => _read;

    /// <inheritdoc />
    public override bool CanRead => _inner.CanRead;

    /// <inheritdoc />
    public override bool CanSeek => false;

    /// <inheritdoc />
    public override bool CanWrite => false;

    /// <inheritdoc />
    public override long Length => throw new NotSupportedException();

    /// <inheritdoc />
    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    /// <inheritdoc />
    public override void Flush() => _inner.Flush();

    /// <inheritdoc />
    public override int Read(byte[] buffer, int offset, int count)
    {
        var read = _inner.Read(buffer, offset, count);
        this.Account(read);

        return read;
    }

    /// <inheritdoc />
    public override async ValueTask<int> ReadAsync(
        Memory<byte> buffer,
        CancellationToken cancellationToken = default)
    {
        var read = await _inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
        this.Account(read);

        return read;
    }

    /// <inheritdoc />
    public override async Task<int> ReadAsync(
        byte[] buffer,
        int offset,
        int count,
        CancellationToken cancellationToken)
    {
        var read = await _inner.ReadAsync(buffer.AsMemory(offset, count), cancellationToken).ConfigureAwait(false);
        this.Account(read);

        return read;
    }

    /// <inheritdoc />
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    /// <inheritdoc />
    public override void SetLength(long value) => throw new NotSupportedException();

    /// <inheritdoc />
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    /// <summary>
    /// 累加已读字节数并在超限时中止。
    /// </summary>
    /// <param name="count">本次读到的字节数。</param>
    /// <exception cref="BusinessException">
    /// 超过上限（<c>1407</c>）。
    /// </exception>
    private void Account(int count)
    {
        if (count <= 0)
        {
            return;
        }

        _read += count;

        if (_read > _limit)
        {
            throw new BusinessException(
                ErrorCodes.UploadTooLarge,
                $"上传体积超过上限（{_limit} 字节）");
        }
    }

    /// <summary>
    /// 判断一个异常（及其内层异常）是不是「上传超限」。
    /// </summary>
    /// <param name="exception">异常。</param>
    /// <returns>是上传超限返回 <see langword="true"/>。</returns>
    /// <remarks>
    /// <para><b>为什么需要它</b>：流式读取时抛出的异常发生在
    /// <b>HTTP 客户端写请求体</b>的过程中，会被 <c>HttpClient</c> 包成
    /// <c>HttpRequestException</c>——于是它会被翻译成「主机不可达」（<c>1301</c>），
    /// 而真正的原因（上传太大）就丢了。</para>
    /// <para>因此调用方在捕获异常后要<b>顺着内层异常链找一找</b>，
    /// 找到那个带 <c>1407</c> 的原始异常就把它还原回去。</para>
    /// </remarks>
    public static bool IsUploadTooLarge(Exception? exception)
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (current is BusinessException business && business.Code == ErrorCodes.UploadTooLarge)
            {
                return true;
            }
        }

        return false;
    }
}
