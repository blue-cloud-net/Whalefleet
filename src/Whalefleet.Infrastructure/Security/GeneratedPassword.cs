using System.Security.Cryptography;

namespace Whalefleet.Infrastructure.Security;

/// <summary>
/// 初始管理员密码的自动生成。
/// </summary>
/// <remarks>
/// <para><b>为什么需要它</b>：<c>ADMIN__PASSWORD</c> 一旦缺失而又不允许自举，
/// 部署方就必须先编辑 <c>.env</c> 才能启动——也就是把「装完就能用」的第一步变成手工步骤。
/// 自动生成让零配置启动成为可能。</para>
/// <para><b>随机源用 <see cref="RandomNumberGenerator"/> 而非 <see cref="Random"/></b>：
/// 这是要长期使用的管理凭据，不能接受可预测的伪随机序列。</para>
/// <para><b>字符集刻意剔除易混淆字符</b>（<c>0/O</c>、<c>1/l/I</c>）：
/// 运维大概率要把它抄进浏览器或终端，混淆字符会直接变成登录失败。</para>
/// <para><b>保证满足密码强度策略</b>：先各取一个「字母 / 数字 / 符号」，
/// 再填充剩余长度后洗牌——因此产出必然过策略，调用方无需再校验。</para>
/// </remarks>
internal static class GeneratedPassword
{
    /// <summary>
    /// 生成密码的长度。
    /// </summary>
    /// <remarks>
    /// 20 字符远超策略下限（10），给出足够余量；再长对人工抄写也没有额外收益。
    /// </remarks>
    internal const int DefaultLength = 20;

    private const string _letters = "abcdefghijkmnopqrstuvwxyzABCDEFGHJKLMNPQRSTUVWXYZ";
    private const string _digits = "23456789";
    private const string _symbols = "!@#$%^&*-_=+";

    /// <summary>
    /// 生成一个满足密码强度策略的随机密码。
    /// </summary>
    /// <param name="length">密码长度；小于 4 时按 4 处理（至少容纳一个字母、数字、符号及一个填充位）。</param>
    /// <returns>随机密码。</returns>
    public static string Create(int length = DefaultLength)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(length, 1);

        // 至少要能放下「一个字母 + 一个数字 + 一个符号」，否则后面的洗牌无从保证类别齐全。
        var effectiveLength = Math.Max(length, 4);

        var buffer = new char[effectiveLength];
        var characters = new List<char>(effectiveLength)
        {
            Pick(_letters),
            Pick(_digits),
            Pick(_symbols),
        };

        while (characters.Count < effectiveLength)
        {
            // 三类字符均匀取，避免偏向数字或符号。
            characters.Add(Pick(_letters, _digits, _symbols));
        }

        Shuffle(characters);
        characters.CopyTo(0, buffer, 0, effectiveLength);

        return new string(buffer);
    }

    private static char Pick(params string[] sets)
    {
        var totalLength = 0;
        foreach (var set in sets)
        {
            totalLength += set.Length;
        }

        var roll = RandomNumberGenerator.GetInt32(totalLength);

        foreach (var set in sets)
        {
            if (roll < set.Length)
            {
                return set[roll];
            }

            roll -= set.Length;
        }

        // 上面的循环必然返回；保底取字母表首字符只为满足编译器，
        // 走不到这里（totalLength 恒为各集合长度之和）。
        return sets[0][0];
    }

    /// <summary>
    /// Fisher–Yates 洗牌（用加密随机数作索引）。
    /// </summary>
    /// <param name="values">待洗牌的字符列表。</param>
    private static void Shuffle(List<char> values)
    {
        for (var i = values.Count - 1; i > 0; i--)
        {
            var j = RandomNumberGenerator.GetInt32(i + 1);
            (values[i], values[j]) = (values[j], values[i]);
        }
    }
}
