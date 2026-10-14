using System.Globalization;

namespace Whalefleet.Infrastructure.Persistence;

/// <summary>
/// 「实体值 → 数据库文本形态」的显式转换助手。
/// </summary>
/// <remarks>
/// <para><b>为什么必须显式转换（Dapper 的真实陷阱）</b>：Dapper 的 <c>ITypeHandler</c>
/// 只在<b>物化（读）</b>路径生效；在<b>参数绑定（写）</b>路径上，值类型会被直接装箱绑定，
/// 注册的处理器被绕过。后果是「X 列按处理器该写成文本，实际写进了数字 / 别的大小写」，
/// 而且读回时因为处理器容忍多种形态而<b>不报错</b>——只有直接查库才看得出来。</para>
/// <para>因此凡是"库中形态有约定"的值类型，写入与查询<b>两侧</b>都要显式转换；
/// 读取侧仍由 <see cref="DapperTypeMaps"/> 的处理器兜底（同时容忍新老形态）。</para>
/// </remarks>
internal static class StorageTextExtensions
{
    /// <summary>
    /// <see cref="Guid"/> → 规范小写 36 位文本（<c>ToString("D")</c>）。
    /// </summary>
    /// <param name="value">主键值。</param>
    /// <returns>规范小写文本。</returns>
    /// <remarks>
    /// <b>为什么不能听任驱动默认</b>：驱动默认写<b>大写</b>文本，而
    /// <c>docs/architecture.md</c> 与运维脚本给出的主键字面量是<b>小写</b>；
    /// 一旦格式不一致，手写 SQL 按文档里的字面量就查不到行。
    /// 统一小写后，写入、查询、文档、手工排查四处口径一致。
    /// </remarks>
    internal static string ToStorageText(this Guid value) =>
        value.ToString("D", CultureInfo.InvariantCulture);

    /// <summary>
    /// 枚举 → 规范小写文本（如 <c>socket</c> / <c>admin</c>）。
    /// </summary>
    /// <typeparam name="TEnum">枚举类型。</typeparam>
    /// <param name="value">枚举值。</param>
    /// <returns>规范小写文本。</returns>
    internal static string ToStorageText<TEnum>(this TEnum value)
        where TEnum : struct, Enum
    {
#pragma warning disable CA1308 // 库中约定以小写文本存储枚举，小写是有意为之
        return value.ToString().ToLowerInvariant();
#pragma warning restore CA1308
    }
}
