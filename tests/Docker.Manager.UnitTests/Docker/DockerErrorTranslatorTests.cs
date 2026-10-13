using System.Net;
using System.Net.Sockets;
using Docker.DotNet;
using Docker.Manager.Application.Common;
using Docker.Manager.Application.Docker;
using FluentAssertions;
using Xunit;

namespace Docker.Manager.UnitTests.Docker;

/// <summary>
/// Docker 异常 → 业务错误码的映射。
/// </summary>
/// <remarks>
/// <para>这张映射表的价值全在细节上：「资源不存在」与「调用失败」必须分开，否则前端只能把两种情况
/// 都显示成"操作失败"，用户根本不知道那个容器其实早就被删了。</para>
/// <para><b>取消不能混进来</b>：调用方取消（用户关页面）与超时是两件事。前者若被翻译成 <c>1403</c>，
/// 每次用户中途离开都会在日志里留下一条"操作超时"，把告警指标彻底污染。因此这条也单独立用例。</para>
/// </remarks>
public class DockerErrorTranslatorTests
{
    private const string _resource = "容器 abc123";

    [Fact]
    public async Task 容器不存在_映射为1401()
    {
        var act = () => ExecuteFailing(ContainerNotFound());

        await AssertBusinessCodeAsync(act, ErrorCodes.DockerResourceNotFound);
    }

    [Fact]
    public async Task 镜像不存在_映射为1401()
    {
        var act = () => ExecuteFailing(new DockerImageNotFoundException(HttpStatusCode.NotFound, "sha256:deadbeef"));

        await AssertBusinessCodeAsync(act, ErrorCodes.DockerResourceNotFound);
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound, ErrorCodes.DockerResourceNotFound)]
    [InlineData(HttpStatusCode.Conflict, ErrorCodes.DockerResourceConflict)]
    [InlineData(HttpStatusCode.InternalServerError, ErrorCodes.DockerApiFailed)]
    public async Task 通用Api异常_按状态码分流(HttpStatusCode statusCode, int expectedCode)
    {
        var act = () => ExecuteFailing(new DockerApiException(statusCode, "boom"));

        await AssertBusinessCodeAsync(act, expectedCode);
    }

    [Fact]
    public async Task 超时_映射为1403()
    {
        // 调用方没取消，但调用超时了（HttpClient 超时同样抛 TaskCanceledException）。
        var act = () => ExecuteFailing(new TaskCanceledException("timeout"));

        await AssertBusinessCodeAsync(act, ErrorCodes.DockerTimeout);
    }

    [Fact]
    public async Task 守护进程不可达_映射为1301()
    {
        var act = () => ExecuteFailing(new HttpRequestException("connection refused"));

        await AssertBusinessCodeAsync(act, ErrorCodes.HostUnreachable);
    }

    [Fact]
    public async Task Socket异常_映射为1301()
    {
        var act = () => ExecuteFailing(new SocketException((int)SocketError.ConnectionRefused));

        await AssertBusinessCodeAsync(act, ErrorCodes.HostUnreachable);
    }

    [Fact]
    public async Task 调用方取消_原样上抛而不是算超时()
    {
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        var act = () => DockerErrorTranslator.ExecuteAsync(
            Fails(new TaskCanceledException("cancelled")),
            _resource,
            cancellation.Token);

        // 这条是「用户关掉页面」的正常路径，不该被记成故障。
        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task 业务异常_原样穿透()
    {
        var original = new BusinessException(ErrorCodes.ValidationFailed, "参数不对");

        var act = () => DockerErrorTranslator.ExecuteAsync(Fails(original), _resource, CancellationToken.None);

        (await act.Should().ThrowAsync<BusinessException>())
            .Which.Should().BeSameAs(original);
    }

    [Fact]
    public async Task 正常返回时透传结果()
    {
        var result = await DockerErrorTranslator.ExecuteAsync(
            static () => Task.FromResult(42),
            _resource,
            CancellationToken.None);

        result.Should().Be(42);
    }

    [Fact]
    public async Task 错误消息里带上资源描述()
    {
        var act = () => ExecuteFailing(ContainerNotFound());

        (await act.Should().ThrowAsync<BusinessException>())
            .Which.Message.Should().Contain("abc123");
    }

    [Fact]
    public async Task 资源描述为空时抛参数异常()
    {
        var act = () => DockerErrorTranslator.ExecuteAsync(static () => Task.FromResult(1), "   ", CancellationToken.None);

        await act.Should().ThrowAsync<ArgumentException>();
    }

    private static DockerContainerNotFoundException ContainerNotFound() =>
        new(HttpStatusCode.NotFound, "abc123");

    /// <summary>
    /// 显式声明为 <see cref="Func{TResult}"/> 返回 <see cref="Task"/>：
    /// 否则 <c>Task.FromException(sqlException)</c> 会被推断成 <c>Task&lt;TException&gt;</c>，
    /// 进而选中泛型重载，让后面的断言变成对一个"返回值的函数"断言。
    /// </summary>
    private static Func<Task> Fails(Exception exception) => () => Task.FromException(exception);

    private static Task ExecuteFailing(Exception exception) =>
        DockerErrorTranslator.ExecuteAsync(Fails(exception), _resource, CancellationToken.None);

    private static async Task AssertBusinessCodeAsync(Func<Task> act, int expectedCode)
    {
        (await act.Should().ThrowAsync<BusinessException>())
            .Which.Code.Should().Be(expectedCode);
    }
}
