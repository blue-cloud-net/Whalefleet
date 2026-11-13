using Dapper;

using Whalefleet.Application.Abstractions;
using Whalefleet.Application.Abstractions.Persistence;

namespace Whalefleet.Infrastructure.Persistence.Auth;

/// <summary>
/// 安全策略存储的 SQLite / Dapper 实现（0.13.0）。
/// </summary>
/// <remarks>
/// <para><b>单行表</b>：<c>security_settings</c> 的 <c>id</c> 固定为 1 并有 CHECK 约束，
/// 因此「只有一份设置」由数据库保证，而不是靠应用层自觉。</para>
/// <para><b>不跨请求缓存</b>：密码策略是安全项，改了就该立刻生效。
/// 读取是单行主键查询，成本可忽略。</para>
/// </remarks>
internal sealed class SqliteSecuritySettingsStore(IDbConnectionFactory connections) : ISecuritySettingsStore
{
    /// <inheritdoc />
    public async Task<SecurityPolicyOptions> GetAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = connections.Create();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        var row = await connection.QuerySingleAsync<SecuritySettingsRow>(new CommandDefinition(
            """
            SELECT min_length, require_letter, require_digit_or_symbol,
                   password_expiry_days, must_change_on_first_login
            FROM security_settings
            WHERE id = 1
            """,
            cancellationToken: cancellationToken)).ConfigureAwait(false);

        return new SecurityPolicyOptions(
            row.MinLength,
            row.RequireLetter,
            row.RequireDigitOrSymbol,
            row.PasswordExpiryDays,
            row.MustChangeOnFirstLogin);
    }

    /// <inheritdoc />
    public async Task SetAsync(SecurityPolicyOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (!options.IsValid())
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                "安全策略参数非法：最小长度需在 1-1024 之间，有效期不能为负");
        }

        await using var connection = connections.Create();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        await connection.ExecuteAsync(new CommandDefinition(
            """
            UPDATE security_settings
            SET min_length = @MinLength,
                require_letter = @RequireLetter,
                require_digit_or_symbol = @RequireDigitOrSymbol,
                password_expiry_days = @PasswordExpiryDays,
                must_change_on_first_login = @MustChangeOnFirstLogin
            WHERE id = 1
            """,
            new
            {
                options.MinLength,
                options.RequireLetter,
                options.RequireDigitOrSymbol,
                options.PasswordExpiryDays,
                options.MustChangeOnFirstLogin,
            },
            cancellationToken: cancellationToken)).ConfigureAwait(false);
    }

    /// <summary>
    /// <c>security_settings</c> 单行的行投影。
    /// </summary>
    private sealed class SecuritySettingsRow
    {
        /// <summary>最小长度。</summary>
        public int MinLength { get; set; }

        /// <summary>是否要求包含字母。</summary>
        public bool RequireLetter { get; set; }

        /// <summary>是否要求包含数字或符号。</summary>
        public bool RequireDigitOrSymbol { get; set; }

        /// <summary>密码有效期（天）。</summary>
        public int PasswordExpiryDays { get; set; }

        /// <summary>首次登录是否强制改密。</summary>
        public bool MustChangeOnFirstLogin { get; set; }
    }
}
