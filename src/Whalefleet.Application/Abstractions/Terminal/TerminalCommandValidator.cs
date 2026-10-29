using Whalefleet.Application.Common;

namespace Whalefleet.Application.Abstractions.Terminal;

/// <summary>
/// 终端入参校验：shell 白名单与自定义命令。
/// </summary>
/// <remarks>
/// <para><b>为什么单独成类且是纯静态</b>：这段规则被<b>两处</b>调用——
/// 签发令牌时（<c>POST …/containers/terminal-token</c>）与建立连接时。
/// 两处各写一遍的后果是「签发的命令能过、握手时被拒」这类自相矛盾的故障。
/// 静态纯函数还能脱离宿主单测（<c>Whalefleet.UnitTests</c> 不能引用 Api / Infrastructure）。</para>
/// <para><b>校验口径</b>：只挡「不可能成立」与「明显越界」的输入，
/// 不试图解析命令内容——<c>container.write</c> 才是真正的边界，
/// 在参数层重新发明一套命令语法检查只会给出「看着更安全、实际漏得更多」的假象。</para>
/// </remarks>
public static class TerminalCommandValidator
{
    /// <summary>
    /// 自定义命令的最大长度（字符）。
    /// </summary>
    /// <remarks>
    /// 2000 足够写下带条件的多行脚本，又不会让令牌膨胀到超过多数反代的请求头上限
    /// （命令会被写进 JWT 声明，JWT 会出现在 WebSocket 的查询串里）。
    /// </remarks>
    public const int MaxCommandLength = 2000;

    /// <summary>
    /// 校验并规范化 shell 路径。
    /// </summary>
    /// <param name="shell">请求里的 shell；为空时取默认值。</param>
    /// <param name="options">终端选项（提供默认值与白名单）。</param>
    /// <returns>可直接启动的 shell 绝对路径。</returns>
    /// <exception cref="ArgumentNullException"><paramref name="options"/> 为 <see langword="null"/>。</exception>
    /// <exception cref="BusinessException">不在白名单内（<c>1000</c>）。</exception>
    public static string NormalizeShell(string? shell, TerminalOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var trimmed = shell?.Trim();

        if (String.IsNullOrEmpty(trimmed))
        {
            return options.DefaultShell;
        }

        // 用 Ordinal 比较：这是 Linux 路径，culture 敏感的忽略大小写毫无意义
        // （`/bin/SH` 与 `/bin/sh` 是两个不同的文件）。
        var allowed = options.AllowedShells.Any(
            candidate => String.Equals(candidate, trimmed, StringComparison.Ordinal));

        if (!allowed)
        {
            throw new BusinessException(
                ErrorCodes.ValidationFailed,
                $"不支持的 shell「{trimmed}」。可选值：{String.Join(" / ", options.AllowedShells)}。");
        }

        return trimmed;
    }

    /// <summary>
    /// 校验并规范化自定义命令。
    /// </summary>
    /// <param name="command">请求里的命令；为空表示开交互式 shell。</param>
    /// <param name="options">终端选项（提供自定义命令开关）。</param>
    /// <returns>去空白后的命令；未指定时返回 <see langword="null"/>。</returns>
    /// <exception cref="ArgumentNullException"><paramref name="options"/> 为 <see langword="null"/>。</exception>
    /// <exception cref="BusinessException">自定义命令被禁用（<c>1000</c>）或长度超限（<c>1000</c>）。</exception>
    /// <remarks>
    /// <b>允许换行</b>：多行命令（<c>for … do … done</c>）是 <c>sh -c</c> 的正常用法。
    /// <b>拒绝 NUL</b>：<c>\0</c> 在大多数 exec 实现里会截断参数，
    /// 让「界面上看到的命令」与「实际执行的命令」不是一回事。
    /// </remarks>
    public static string? NormalizeCommand(string? command, TerminalOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (String.IsNullOrWhiteSpace(command))
        {
            return null;
        }

        var trimmed = command.Trim();

        if (trimmed.Contains('\0', StringComparison.Ordinal))
        {
            throw new BusinessException(
                ErrorCodes.ValidationFailed,
                "命令里不能包含空字符（\\0）。");
        }

        if (trimmed.Length > MaxCommandLength)
        {
            throw new BusinessException(
                ErrorCodes.ValidationFailed,
                $"命令最长 {MaxCommandLength} 个字符。");
        }

        if (!options.AllowCustomCommand)
        {
            throw new BusinessException(
                ErrorCodes.ValidationFailed,
                "本实例未开启「在终端里直接执行命令」，请在 shell 里手动输入。");
        }

        return trimmed;
    }
}
