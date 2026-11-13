using FluentAssertions;

using Whalefleet.Application.Common;
using Whalefleet.Application.Composes;

using Xunit;

namespace Whalefleet.UnitTests.Composes;

/// <summary>
/// <see cref="ComposeVariableResolver"/> 的行为测试。
/// </summary>
/// <remarks>
/// <para><b>为什么这些语义值得逐条钉住</b>：变量插值是「用户写的 YAML 被平台改写」，
/// 而改写的每一条规则都会直接影响容器实际拿到什么值。
/// 一旦哪条规则悄悄变了，症状是「服务起来了但配置不对」——
/// 那是最难排查的一类故障（没有任何报错）。</para>
/// <para>覆盖的规则：
/// <list type="number">
///   <item><description><c>${VAR}</c> 取值；未定义 → <c>1908</c>（绝不替换成空串）；</description></item>
///   <item><description><c>${VAR:-default}</c> 的默认值语义；</description></item>
///   <item><description><c>$$</c> 转义与落单 <c>$</c> 的字面量处理（口令里很常见）；</description></item>
///   <item><description>不受支持的 <c>:?</c> / <c>:+</c> 明确报错而不是猜语义；</description></item>
///   <item><description>变量名非法 → <c>1909</c>；引用未闭合 → <c>1904</c>；</description></item>
///   <item><description>宽松模式的占位符必须是<b>纯数字</b>（否则端口映射解析不出来）。</description></item>
/// </list>
/// </remarks>
public class ComposeVariableResolverTests
{
    /// <summary>
    /// 插值：<c>${VAR}</c> 取到变量值。
    /// </summary>
    [Fact]
    public void 插值_花括号引用_取到变量值()
    {
        var variables = new Dictionary<string, string>(StringComparer.Ordinal) { ["TAG"] = "1.2.3" };

        var result = ComposeVariableResolver.Interpolate(
            "nginx:${TAG}",
            variables,
            new HashSet<string>(StringComparer.Ordinal));

        result.Should().Be("nginx:1.2.3");
    }

    /// <summary>
    /// 插值：变量名<b>区分大小写</b>（与 Compose 一致）。
    /// </summary>
    [Fact]
    public void 插值_变量名区分大小写()
    {
        var variables = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["TAG"] = "upper",
            ["tag"] = "lower",
        };

        var result = ComposeVariableResolver.Interpolate(
            "${TAG}-${tag}",
            variables,
            new HashSet<string>(StringComparer.Ordinal));

        result.Should().Be("upper-lower");
    }

    /// <summary>
    /// 插值：未定义的变量报 <c>1908</c>，<b>绝不</b>替换成空串。
    /// </summary>
    [Fact]
    public void 插值_变量未定义_报1908()
    {
        var exception = Assert.Throws<BusinessException>(() =>
            ComposeVariableResolver.Interpolate(
                "nginx:${TAG}",
                new Dictionary<string, string>(StringComparer.Ordinal),
                new HashSet<string>(StringComparer.Ordinal)));

        exception.Code.Should().Be(ErrorCodes.VariableNotDefined);
        exception.Message.Should().Contain("TAG");
    }

    /// <summary>
    /// 插值：<c>${VAR:-default}</c> 在变量未定义时用默认值。
    /// </summary>
    [Fact]
    public void 插值_带默认值_变量未定义时用默认值()
    {
        var result = ComposeVariableResolver.Interpolate(
            "${MODE:-dev}",
            new Dictionary<string, string>(StringComparer.Ordinal),
            new HashSet<string>(StringComparer.Ordinal));

        result.Should().Be("dev");
    }

    /// <summary>
    /// 插值：<c>${VAR:-default}</c> 在变量<b>已定义</b>时用变量值（默认值不生效）。
    /// </summary>
    [Fact]
    public void 插值_带默认值_变量已定义时用变量值()
    {
        var variables = new Dictionary<string, string>(StringComparer.Ordinal) { ["MODE"] = "prod" };

        var result = ComposeVariableResolver.Interpolate(
            "${MODE:-dev}",
            variables,
            new HashSet<string>(StringComparer.Ordinal));

        result.Should().Be("prod");
    }

    /// <summary>
    /// 插值：<c>$$</c> 是转义，产出字面量 <c>$</c>。
    /// </summary>
    [Fact]
    public void 插值_双美元_转义成字面量美元()
    {
        var result = ComposeVariableResolver.Interpolate(
            "cost is $$5",
            new Dictionary<string, string>(StringComparer.Ordinal),
            new HashSet<string>(StringComparer.Ordinal));

        result.Should().Be("cost is $5");
    }

    /// <summary>
    /// 插值：落单的 <c>$</c> 按字面量处理（口令里很常见，不能当成变量起始符）。
    /// </summary>
    [Fact]
    public void 插值_落单美元_按字面量处理()
    {
        var result = ComposeVariableResolver.Interpolate(
            "P@ss$word",
            new Dictionary<string, string>(StringComparer.Ordinal),
            new HashSet<string>(StringComparer.Ordinal));

        result.Should().Be("P@ss$word");
    }

    /// <summary>
    /// 插值：不受支持的 <c>${VAR:?msg}</c> 明确报错，而不是猜一个语义。
    /// </summary>
    [Fact]
    public void 插值_问号语法_报1904()
    {
        var exception = Assert.Throws<BusinessException>(() =>
            ComposeVariableResolver.Interpolate(
                "${VAR:?必须设置}",
                new Dictionary<string, string>(StringComparer.Ordinal),
                new HashSet<string>(StringComparer.Ordinal)));

        exception.Code.Should().Be(ErrorCodes.ComposeInvalid);
        exception.Message.Should().Contain("不支持");
    }

    /// <summary>
    /// 插值：不受支持的 <c>${VAR:+alt}</c> 明确报错。
    /// </summary>
    [Fact]
    public void 插值_加号语法_报1904()
    {
        var exception = Assert.Throws<BusinessException>(() =>
            ComposeVariableResolver.Interpolate(
                "${VAR:+alt}",
                new Dictionary<string, string>(StringComparer.Ordinal),
                new HashSet<string>(StringComparer.Ordinal)));

        exception.Code.Should().Be(ErrorCodes.ComposeInvalid);
    }

    /// <summary>
    /// 插值：变量名非法报 <c>1909</c>。
    /// </summary>
    [Fact]
    public void 插值_变量名非法_报1909()
    {
        var exception = Assert.Throws<BusinessException>(() =>
            ComposeVariableResolver.Interpolate(
                "${1BAD}",
                new Dictionary<string, string>(StringComparer.Ordinal),
                new HashSet<string>(StringComparer.Ordinal)));

        exception.Code.Should().Be(ErrorCodes.InvalidVariableName);
    }

    /// <summary>
    /// 插值：引用未闭合报 <c>1904</c>（而不是「原样输出」）。
    /// </summary>
    [Fact]
    public void 插值_引用未闭合_报1904()
    {
        var exception = Assert.Throws<BusinessException>(() =>
            ComposeVariableResolver.Interpolate(
                "nginx:${TAG",
                new Dictionary<string, string>(StringComparer.Ordinal),
                new HashSet<string>(StringComparer.Ordinal)));

        exception.Code.Should().Be(ErrorCodes.ComposeInvalid);
        exception.Message.Should().Contain("闭合");
    }

    /// <summary>
    /// 插值：宽松模式下未定义的变量换成<b>纯数字</b>占位符。
    /// </summary>
    /// <remarks>
    /// <b>为什么必须是纯数字</b>：端口映射是「宿主端口:容器端口」，
    /// 占位符不是数字就解析不出来——而「只看结构」的解析（详情页、校验提示）
    /// 恰恰要能解析出端口。
    /// </remarks>
    [Fact]
    public void 插值_宽松模式_占位符是纯数字()
    {
        var unresolved = new HashSet<string>(StringComparer.Ordinal);

        var result = ComposeVariableResolver.Interpolate(
            "${PORT}:80",
            variables: null,
            unresolved);

        result.Should().MatchRegex("^[0-9]+:80$");
        unresolved.Should().Contain("PORT");
    }

    /// <summary>
    /// 插值：宽松模式下同一个变量名每次得到<b>同一个</b>占位符。
    /// </summary>
    /// <remarks>
    /// 不稳定的话，两次解析出的端口会不同——
    /// 症状是「列表页说 3 个端口、详情页说 2 个」这种毫无线索的抖动。
    /// </remarks>
    [Fact]
    public void 插值_宽松模式_占位符稳定()
    {
        var first = ComposeVariableResolver.Interpolate(
            "${PORT}:80",
            variables: null,
            new HashSet<string>(StringComparer.Ordinal));

        var second = ComposeVariableResolver.Interpolate(
            "${PORT}:80",
            variables: null,
            new HashSet<string>(StringComparer.Ordinal));

        first.Should().Be(second);
    }

    /// <summary>
    /// 收集引用：只扫名字，不解析、不报错。
    /// </summary>
    [Fact]
    public void 收集引用_返回被引用的变量名()
    {
        var names = ComposeVariableResolver.CollectReferences("nginx:${TAG} ${MODE:-dev} $${LITERAL}");

        names.Should().Contain("TAG");
        names.Should().Contain("MODE");
        names.Should().NotContain("LITERAL");
    }

    /// <summary>
    /// 收集引用：非法变量名不报（留给插值时报）。
    /// </summary>
    [Fact]
    public void 收集引用_非法变量名被跳过()
    {
        var names = ComposeVariableResolver.CollectReferences("${1BAD}");

        names.Should().BeEmpty();
    }
}
