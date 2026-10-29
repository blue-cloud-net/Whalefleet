using Whalefleet.Application.Abstractions.Terminal;
using Whalefleet.Application.Common;
using FluentAssertions;
using Xunit;

namespace Whalefleet.UnitTests.Terminal;

/// <summary>
/// 终端入参校验（0.8.0）。
/// </summary>
/// <remarks>
/// 这段规则同时被「签发令牌」与「建立连接」两处调用。两处若分叉，
/// 症状是「签发时能过、握手时被拒」——而握手失败的原因浏览器根本拿不到。
/// 因此这里逐条钉住。
/// </remarks>
public class TerminalCommandValidatorTests
{
    private readonly TerminalOptions _options = new();

    [Fact]
    public void Shell省略时取默认值()
    {
        TerminalCommandValidator.NormalizeShell(null, _options).Should().Be("/bin/sh");
        TerminalCommandValidator.NormalizeShell("   ", _options).Should().Be("/bin/sh");
    }

    [Fact]
    public void Shell必须命中白名单()
    {
        var act = () => TerminalCommandValidator.NormalizeShell("/usr/bin/fish", _options);

        act.Should().Throw<BusinessException>()
            .Which.Code.Should().Be(ErrorCodes.ValidationFailed);
    }

    [Fact]
    public void Shell比较区分大小写()
    {
        // Linux 路径里 /bin/SH 与 /bin/sh 是两个不同的文件，不能忽略大小写放行。
        var act = () => TerminalCommandValidator.NormalizeShell("/BIN/SH", _options);

        act.Should().Throw<BusinessException>()
            .Which.Code.Should().Be(ErrorCodes.ValidationFailed);
    }

    [Fact]
    public void Shell允许带首尾空白()
    {
        TerminalCommandValidator.NormalizeShell("  /bin/bash ", _options).Should().Be("/bin/bash");
    }

    [Fact]
    public void 命令为空时表示开交互式shell()
    {
        TerminalCommandValidator.NormalizeCommand(null, _options).Should().BeNull();
        TerminalCommandValidator.NormalizeCommand("   ", _options).Should().BeNull();
    }

    [Fact]
    public void 命令允许换行但拒绝空字符()
    {
        TerminalCommandValidator
            .NormalizeCommand("for i in 1 2; do\n echo $i\ndone", _options)
            .Should().NotBeNull();

        var act = () => TerminalCommandValidator.NormalizeCommand("ls\0rm", _options);

        act.Should().Throw<BusinessException>()
            .Which.Code.Should().Be(ErrorCodes.ValidationFailed);
    }

    [Fact]
    public void 命令超过长度上限时拒绝()
    {
        var tooLong = new string('a', TerminalCommandValidator.MaxCommandLength + 1);

        var act = () => TerminalCommandValidator.NormalizeCommand(tooLong, _options);

        act.Should().Throw<BusinessException>()
            .Which.Code.Should().Be(ErrorCodes.ValidationFailed);
    }

    [Fact]
    public void 关闭自定义命令后拒绝任何命令()
    {
        var options = new TerminalOptions { AllowCustomCommand = false };

        var act = () => TerminalCommandValidator.NormalizeCommand("ls", options);

        act.Should().Throw<BusinessException>()
            .Which.Code.Should().Be(ErrorCodes.ValidationFailed);
    }
}
