using System.Data;
using System.Globalization;
using Dapper;
using Whalefleet.Core.Hosts;

namespace Whalefleet.Infrastructure.Persistence;

/// <summary>
/// Dapper 全局映射注册表：列名风格映射 + 自定义类型处理器。
/// </summary>
/// <remarks>
/// <para><b>为什么必须显式初始化</b>：Dapper 的这两项都是<b>进程级全局状态</b>，
/// 一旦漏配不会报错，而是静默给错值——典型后果是 snake_case 列喂不到 PascalCase 属性，
/// 实体字段全是默认值。</para>
/// <list type="table">
///   <listheader>
///     <term>配置</term>
///     <description>漏配后果</description>
///   </listheader>
///   <item>
///     <term><c>MatchNamesWithUnderscores</c></term>
///     <description><c>creation_time</c> 匹配不到 <c>CreationTime</c>，审计字段静默为空</description>
///   </item>
///   <item>
///     <term>枚举 <c>ITypeHandler</c></term>
///     <description>枚举按整数读写，与库中的小写文本列错位</description>
///   </item>
///   <item>
///     <term><c>DateTime</c> 处理器</term>
///     <description>读回的 <see cref="DateTime.Kind"/> 为 <c>Unspecified</c>，UTC 语义丢失</description>
///   </item>
/// </list>
/// <para><b>初始化时机</b>：由 <see cref="SqliteConnectionFactory"/> 构造函数调用，
/// 保证"拿得到连接"的代码路径一定已经映射就绪。重复调用是幂等的。</para>
/// <para><b>为什么自己处理 <see cref="DateTime"/></b>：SQLite 无原生日期类型，
/// 一律以 TEXT 存储；驱动读回时 <see cref="DateTime.Kind"/> 为 <c>Unspecified</c>。
/// 本项目约定所有时间均为 UTC，故读回时统一标注为 <see cref="DateTimeKind.Utc"/>。</para>
/// <para><b>为什么自己处理 <see cref="Guid"/></b>：以规范小写 36 位文本
/// （<c>ToString("D")</c>）读写，与文档 / 运维脚本给出的主键字面量保持一致，
/// 避免驱动默认的大写形态让手写 SQL 查不到行。</para>
/// </remarks>
internal static class DapperTypeMaps
{
    private static readonly object _gate = new();

    private static bool _initialized;

    /// <summary>
    /// 注册全部映射（幂等，线程安全）。
    /// </summary>
    internal static void Initialize()
    {
        lock (_gate)
        {
            if (_initialized)
            {
                return;
            }

            // 列 snake_case ↔ 属性 PascalCase
            DefaultTypeMap.MatchNamesWithUnderscores = true;

            SqlMapper.AddTypeHandler(new GuidTypeHandler());
            SqlMapper.AddTypeHandler(new UtcDateTimeTypeHandler());
            SqlMapper.AddTypeHandler(new NullableUtcDateTimeTypeHandler());
            SqlMapper.AddTypeHandler(new HostEndpointTypeHandler());
            SqlMapper.AddTypeHandler(new EnumTextTypeHandler<DockerHostType>());
            SqlMapper.AddTypeHandler(new EnumTextTypeHandler<DockerHostAuthKind>());

            _initialized = true;
        }
    }

    /// <summary>
    /// 枚举 ↔ 规范小写文本（如 <c>socket</c> / <c>admin</c>）。
    /// </summary>
    /// <typeparam name="TEnum">枚举类型。</typeparam>
    /// <remarks>
    /// <para><b>只在读取方向生效</b>：Dapper 在参数绑定（写）路径上会绕过处理器（值类型直接装箱），
    /// 写入侧的转换由 <see cref="StorageTextExtensions"/> 显式完成——详见该类型的备注。</para>
    /// <para><see cref="Parse"/> 同时容忍文本与整数：若某条写入路径绕过了本处理器
    /// （例如手工执行的 SQL），读回时仍能正确还原，不至于抛异常。</para>
    /// </remarks>
    private sealed class EnumTextTypeHandler<TEnum> : SqlMapper.TypeHandler<TEnum>
        where TEnum : struct, Enum
    {
        public override void SetValue(IDbDataParameter parameter, TEnum value)
        {
            ArgumentNullException.ThrowIfNull(parameter);

            parameter.DbType = DbType.String;
            parameter.Value = value.ToStorageText();
        }

        public override TEnum Parse(object value) => value switch
        {
            string text => Enum.Parse<TEnum>(text, ignoreCase: true),
            TEnum typed => typed,
            long number => (TEnum)Enum.ToObject(typeof(TEnum), number),
            int number => (TEnum)Enum.ToObject(typeof(TEnum), number),
            _ => throw new DataException(
                $"无法把 {value?.GetType().Name ?? "null"} 解析为 {typeof(TEnum).Name}"),
        };
    }

    /// <summary>
    /// <see cref="Guid"/> ↔ 规范小写 36 位文本。
    /// </summary>
    private sealed class GuidTypeHandler : SqlMapper.TypeHandler<Guid>
    {
        public override void SetValue(IDbDataParameter parameter, Guid value)
        {
            ArgumentNullException.ThrowIfNull(parameter);

            parameter.DbType = DbType.String;
            parameter.Value = value.ToString("D", CultureInfo.InvariantCulture);
        }

        public override Guid Parse(object value) => value switch
        {
            Guid guid => guid,
            string text => Guid.Parse(text),
            byte[] bytes => new Guid(bytes),
            _ => throw new DataException($"无法把 {value?.GetType().Name ?? "null"} 解析为 Guid"),
        };
    }

    /// <summary>
    /// <see cref="DateTime"/> ↔ UTC TEXT。
    /// </summary>
    private sealed class UtcDateTimeTypeHandler : SqlMapper.TypeHandler<DateTime>
    {
        public override void SetValue(IDbDataParameter parameter, DateTime value)
        {
            ArgumentNullException.ThrowIfNull(parameter);

            parameter.DbType = DbType.DateTime;
            parameter.Value = UtcDateTimeValue.ToUtc(value);
        }

        public override DateTime Parse(object value) => UtcDateTimeValue.Parse(value);
    }

    /// <summary>
    /// <see cref="Nullable{DateTime}"/> ↔ UTC TEXT（<c>NULL</c> 保持为 <see langword="null"/>）。
    /// </summary>
    private sealed class NullableUtcDateTimeTypeHandler : SqlMapper.TypeHandler<DateTime?>
    {
        public override void SetValue(IDbDataParameter parameter, DateTime? value)
        {
            ArgumentNullException.ThrowIfNull(parameter);

            parameter.DbType = DbType.DateTime;
            parameter.Value = value is null ? DBNull.Value : UtcDateTimeValue.ToUtc(value.Value);
        }

        public override DateTime? Parse(object value) =>
            value is null || value is DBNull ? null : UtcDateTimeValue.Parse(value);
    }

    /// <summary>
    /// <see cref="HostEndpoint"/> ↔ TEXT。
    /// </summary>
    private sealed class HostEndpointTypeHandler : SqlMapper.TypeHandler<HostEndpoint>
    {
        public override void SetValue(IDbDataParameter parameter, HostEndpoint? value)
        {
            ArgumentNullException.ThrowIfNull(parameter);
            ArgumentNullException.ThrowIfNull(value);

            parameter.DbType = DbType.String;
            parameter.Value = value.Value;
        }

        public override HostEndpoint Parse(object value) => value switch
        {
            HostEndpoint endpoint => endpoint,
            string text => new HostEndpoint(text),
            _ => throw new DataException(
                $"无法把 {value?.GetType().Name ?? "null"} 解析为 {nameof(HostEndpoint)}"),
        };
    }

    /// <summary>
    /// <see cref="DateTime"/> 的 UTC 归一化与解析（两个处理器共用）。
    /// </summary>
    private static class UtcDateTimeValue
    {
        internal static DateTime ToUtc(DateTime value) => value.Kind switch
        {
            DateTimeKind.Utc => value,
            DateTimeKind.Local => value.ToUniversalTime(),
            _ => DateTime.SpecifyKind(value, DateTimeKind.Utc),
        };

        internal static DateTime Parse(object value) => value switch
        {
            DateTime dateTime => DateTime.SpecifyKind(dateTime, DateTimeKind.Utc),
            string text => DateTime.SpecifyKind(
                DateTime.Parse(
                    text,
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal),
                DateTimeKind.Utc),
            _ => throw new DataException($"无法把 {value?.GetType().Name ?? "null"} 解析为 DateTime"),
        };
    }
}
