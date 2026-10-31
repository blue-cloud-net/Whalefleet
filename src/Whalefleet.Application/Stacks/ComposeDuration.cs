using System.Globalization;
using Whalefleet.Application.Common;

namespace Whalefleet.Application.Stacks;

/// <summary>
/// Compose 时长解析：把 <c>500ms</c> / <c>30s</c> / <c>1m30s</c> / <c>1h</c> 这类写法换成秒。
/// </summary>
/// <remarks>
/// <para><b>为什么要自己写而不是用现成的库</b>：现成的时长解析器大多接受 <c>1.5h</c>、
/// <c>1d</c>、负数等一堆 Compose 并不接受的写法，而 Compose 的规则很窄：
/// 「若干个「数字 + 单位」的串联」，单位取 <c>h</c> / <c>m</c> / <c>s</c> / <c>ms</c>。
/// 自己写能把「什么算合法」写死在一处，不必每次都担心某个库的宽松解析放过了非法值。</para>
/// <para><b>亚秒向上取整</b>：Docker 的健康检查间隔以纳秒存储，但本项目的对外契约
/// 「秒」是整数（见 <c>ContainerHealthCheck.IntervalSeconds</c>）。
/// 因此 <c>500ms</c> 会变成 <b>1 秒</b> 而不是 0——0 会被下游当成「没设置」，
/// 症状是健康检查按默认值跑，而用户明明写了 500ms。</para>
/// <para><b>为什么拒绝天（<c>d</c>）</b>：Compose 也不支持天。但更重要的是，
/// 允许 <c>1d</c> 会让人以为「一天」是合法单位，而守护进程侧没有这个概念——
/// 间隔超过一天时应该显式写成 <c>24h</c>。</para>
/// </remarks>
internal static class ComposeDuration
{
    /// <summary>Compose 时长解析失败时抛出的错误码。</summary>
    /// <remarks>与「Compose 文件非法」共用 1904：时长的非法本质上就是 YAML 里的值非法。</remarks>
    private const int ErrorCode = ErrorCodes.ComposeInvalid;

    /// <summary>
    /// 解析时长。
    /// </summary>
    /// <param name="value">原始文本（如 <c>500ms</c>）。</param>
    /// <param name="path">字段路径（用于错误消息，如 <c>services.api.healthcheck.interval</c>）。</param>
    /// <returns>秒数（向上取整，最小 1 秒）；<paramref name="value"/> 为 <see langword="null"/> 时返回 <see langword="null"/>。</returns>
    /// <exception cref="BusinessException">时长非法（<c>1904</c>）。</exception>
    internal static int? Parse(string? value, string path)
    {
        if (value is null)
        {
            return null;
        }

        var trimmed = value.Trim();

        if (trimmed.Length == 0)
        {
            return null;
        }

        // 纯数字按秒处理（Compose 的历史写法：interval: 30）
        if (trimmed.All(static c => char.IsAsciiDigit(c)))
        {
            return ParseNumber(trimmed, path);
        }

        var totalMilliseconds = 0L;
        var index = 0;

        while (index < trimmed.Length)
        {
            var numberStart = index;

            while (index < trimmed.Length && char.IsAsciiDigit(trimmed[index]))
            {
                index++;
            }

            if (numberStart == index)
            {
                throw Invalid(path, value, "数字与单位之间缺少分隔");
            }

            var numberText = trimmed[numberStart..index];
            var unitStart = index;

            while (index < trimmed.Length && !char.IsAsciiDigit(trimmed[index]))
            {
                index++;
            }

            var unit = trimmed[unitStart..index];

            if (!long.TryParse(numberText, NumberStyles.None, CultureInfo.InvariantCulture, out var number))
            {
                throw Invalid(path, value, "数值超出可表示范围");
            }

            totalMilliseconds += unit switch
            {
                "h" => number * 3_600_000,
                "m" => number * 60_000,
                "s" => number * 1_000,
                "ms" => number,
                _ => throw Invalid(path, value, $"未知的时间单位「{unit}」，只能是 h / m / s / ms"),
            };

            if (totalMilliseconds <= 0)
            {
                throw Invalid(path, value, "时长必须大于 0");
            }
        }

        // 向上取整到秒，且至少 1 秒：0 会被下游当成「没设置」
        return (int)Math.Max(1, (totalMilliseconds + 999) / 1000);
    }

    /// <summary>
    /// 解析纯数字时长（秒）。
    /// </summary>
    /// <param name="text">数字文本。</param>
    /// <param name="path">字段路径。</param>
    /// <returns>秒数。</returns>
    /// <exception cref="BusinessException">数值非法（<c>1904</c>）。</exception>
    private static int ParseNumber(string text, string path)
    {
        if (!long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var seconds) || seconds <= 0)
        {
            throw Invalid(path, text, "时长必须是一个正整数（秒）");
        }

        if (seconds > int.MaxValue)
        {
            throw Invalid(path, text, "时长超出可表示范围");
        }

        return (int)seconds;
    }

    /// <summary>
    /// 构造时长非法异常。
    /// </summary>
    /// <param name="path">字段路径。</param>
    /// <param name="value">原始值。</param>
    /// <param name="reason">原因说明。</param>
    /// <returns>业务异常。</returns>
    private static BusinessException Invalid(string path, string? value, string reason) =>
        new(
            ErrorCode,
            $"Compose 文件的「{path}」取值「{value}」非法：{reason}");
}
