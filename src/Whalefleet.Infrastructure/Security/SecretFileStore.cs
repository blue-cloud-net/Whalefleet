using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Whalefleet.Infrastructure.Persistence;

namespace Whalefleet.Infrastructure.Security;

/// <summary>
/// 文件型密钥存储：在数据卷上按需生成并复用密钥材料（JWT 签名密钥、初始管理员密码）。
/// </summary>
/// <remarks>
/// <para><b>为什么密钥要落盘而不是每次启动重新生成</b>：JWT 签名密钥一变，此前签发的
/// access token 全部失效；refresh token 虽然存在库里，但要用<b>旧密钥</b>换新令牌，
/// 于是「容器重启」会静默地把所有在线用户踢下线。密钥因此必须<b>生成一次、此后复用</b>。</para>
/// <para><b>为什么落在数据目录而不是数据库</b>：数据目录（<c>/app/data</c>）已经是唯一的可写卷，
/// 放在这里无需为密钥单独引入一张表与一条迁移；代价是 <c>scripts/backup.sh</c> 必须一并备份，
/// 否则换机恢复后会静默换掉密钥——该脚本已同步处理。</para>
/// <para><b>同步方法而非异步</b>：调用方全部在启动路径上
/// （<see cref="JwtSecretPostConfigure"/> 的后置配置、引导器），
/// 而 <see cref="Microsoft.Extensions.Options.IPostConfigureOptions{TOptions}.PostConfigure"/>
/// 本身是同步的。用同步文件 API 可以避免 <c>await</c> 桥接带来的死锁与 <c>CA1849</c> 类告警；
/// 这里读写的是几百字节的小文件，异步带来的收益可以忽略。</para>
/// <para><b>权限</b>：Linux 下刻意以 <c>0600</c> 创建。数据卷上的数据库文件默认是 <c>0644</c>，
/// 密钥不该沿用那个更宽的权限。</para>
/// <para><b>并发</b>：写文件采用「临时文件 + 原子替换」，多实例并发启动时
/// 即使各自都判定为「不存在」，也只会有一份最终生效，不会产生半截文件。</para>
/// <para><b>不记录密钥内容</b>：本类的日志只出现路径与字节数，绝不出现密钥本身。</para>
/// </remarks>
internal sealed class SecretFileStore : ISecretProvider
{
    /// <summary>
    /// JWT 签名密钥文件名（位于数据目录下）。
    /// </summary>
    internal const string JwtSecretFileName = "jwt.key";

    /// <summary>
    /// 初始管理员凭据文件名（位于数据目录下），仅在首次引导时生成。
    /// </summary>
    internal const string InitialAdminFileName = "initial-admin.txt";

    /// <summary>
    /// 生成 JWT 签名密钥时使用的随机字节数。
    /// </summary>
    /// <remarks>
    /// 48 字节远超 <see cref="JwtOptions.MinSecretLengthBytes"/> 要求的 32 字节下限；
    /// Base64 编码后为 64 个字符，满足 HS256 的密钥强度要求。
    /// </remarks>
    internal const int JwtSecretSizeBytes = 48;

    /// <summary>
    /// 日志中用于提示查看凭据的 compose 服务名。
    /// </summary>
    private const string ServiceNameHint = "whalefleet";

    private readonly string _dataDirectory;
    private readonly ILogger<SecretFileStore> _logger;

    /// <summary>
    /// 初始化文件型密钥存储。
    /// </summary>
    /// <param name="options">持久化配置（用于确定数据目录）。</param>
    /// <param name="logger">日志。</param>
    /// <exception cref="ArgumentNullException">任一参数为 <see langword="null"/>。</exception>
    /// <remarks>
    /// 数据目录取自 <c>Persistence:DatabasePath</c> 所在目录：数据库已经必须在该目录里，
    /// 因此密钥与数据库同卷这一点由构造方式天然保证，不需要额外的配置项。
    /// </remarks>
    public SecretFileStore(IOptions<SqliteOptions> options, ILogger<SecretFileStore> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);

        var databasePath = options.Value.DatabasePath;

        // 兼容两种退化情形：未配置 DatabasePath（空串会让 Path.GetFullPath 直接抛
        // ArgumentException，而那会让「密钥自动生成」变成一个看不懂的启动崩溃），
        // 以及配置成纯文件名（拿不到目录）。两种情况下都退回当前目录，
        // 密钥仍与数据库同处一地，语义不变。
        if (String.IsNullOrWhiteSpace(databasePath))
        {
            _dataDirectory = Directory.GetCurrentDirectory();
        }
        else
        {
            var directory = Path.GetDirectoryName(Path.GetFullPath(databasePath));
            _dataDirectory = String.IsNullOrEmpty(directory) ? Directory.GetCurrentDirectory() : directory;
        }

        _logger = logger;
    }

    /// <summary>
    /// 数据目录（密钥文件的所在目录）。
    /// </summary>
    internal string DataDirectory => _dataDirectory;

    /// <summary>
    /// 读取 JWT 签名密钥；文件不存在或内容为空时生成并写入。
    /// </summary>
    /// <returns>签名密钥原文（Base64 文本）。</returns>
    public string GetJwtSecret() => this.GetOrCreateJwtSecret();

    /// <summary>
    /// 读取 JWT 签名密钥；文件不存在或内容为空时生成并写入。
    /// </summary>
    /// <returns>签名密钥原文（Base64 文本）。</returns>
    public string GetOrCreateJwtSecret()
    {
        var path = Path.Combine(_dataDirectory, JwtSecretFileName);

        var existing = TryRead(path);
        if (existing is not null)
        {
            _logger.LogDebug("复用已有 JWT 签名密钥文件 {Path}", path);
            return existing;
        }

        var generated = Convert.ToBase64String(RandomNumberGenerator.GetBytes(JwtSecretSizeBytes));
        this.WriteAtomically(path, generated);

        _logger.LogInformation(
            "未配置 JWT__SECRET，已自动生成签名密钥并保存到 {Path}（{ByteCount} 字节随机数）。"
            + "请连同数据卷一起备份该文件——丢失它会导致所有已登录用户被登出。",
            path,
            JwtSecretSizeBytes);

        return generated;
    }

    /// <summary>
    /// 把初始管理员凭据写入数据目录下的固定文件（权限 <c>0600</c>）。
    /// </summary>
    /// <param name="username">管理员用户名。</param>
    /// <param name="password">管理员密码。</param>
    /// <returns>凭据文件路径。</returns>
    /// <remarks>
    /// <b>刻意不写日志</b>：日志会被采集器上报到集中式日志系统，等价于把密码扩散出去。
    /// 只在日志里提示文件路径，由部署方自行读取。
    /// </remarks>
    public string WriteInitialAdminCredential(string username, string password)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(username);
        ArgumentException.ThrowIfNullOrWhiteSpace(password);

        var path = Path.Combine(_dataDirectory, InitialAdminFileName);

        // 内容刻意写成可直接 grep 的键值对，运维用 docker compose exec cat 就能读懂。
        // 时间戳用 "O"（往返格式），它本身与区域无关，故直接插值即可。
        var content =
            $"# Whalefleet 初始管理员凭据（首次启动时自动生成，生成时间 {DateTimeOffset.UtcNow:O}）\n"
            + "# 首次登录后请立即修改密码，并删除本文件。\n"
            + $"ADMIN__USER={username}\n"
            + $"ADMIN__PASSWORD={password}\n";

        this.WriteAtomically(path, content);

        _logger.LogInformation(
            "已生成初始管理员密码并保存到 {Path}（权限 0600）。密码**未**写入日志——"
            + "请执行 `docker compose exec {Service} cat {Path}` 查看，并在首次登录后删除该文件。",
            path,
            ServiceNameHint,
            path);

        return path;
    }

    /// <summary>
    /// 读取文件内容；文件不存在或内容为空白时返回 <see langword="null"/>。
    /// </summary>
    /// <param name="path">文件路径。</param>
    /// <returns>文件内容，或 <see langword="null"/>。</returns>
    /// <remarks>
    /// 空文件视为「不存在」：进程可能在写入过程中被强杀，留下 0 字节文件。
    /// 若把它当成有效密钥，所有令牌都会被一个空字符串签名——那比重新生成更糟。
    /// </remarks>
    private static string? TryRead(string path)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        var content = File.ReadAllText(path);

        return String.IsNullOrWhiteSpace(content) ? null : content.Trim();
    }

    /// <summary>
    /// 以「临时文件 + 原子替换」写入，确保并发或多副本启动时不会读到半截内容。
    /// </summary>
    /// <param name="path">目标文件路径。</param>
    /// <param name="content">文件内容。</param>
    private void WriteAtomically(string path, string content)
    {
        System.IO.Directory.CreateDirectory(_dataDirectory);

        // 临时名带上进程 ID 与随机串，避免两个并发启动的实例写同一个 .tmp。
        var temporaryPath =
            $"{path}.{Environment.ProcessId}.{RandomNumberGenerator.GetHexString(4, lowercase: true)}.tmp";

        try
        {
            File.WriteAllText(temporaryPath, content);
            RestrictToOwner(temporaryPath);

            // Move(overwrite: true) 在同一文件系统内是原子的。
            File.Move(temporaryPath, path, overwrite: true);
        }
        catch
        {
            TryDelete(temporaryPath);
            throw;
        }
    }

    /// <summary>
    /// 把文件权限收紧为 <c>0600</c>（仅属主可读写）。
    /// </summary>
    /// <param name="path">文件路径。</param>
    /// <remarks>
    /// Windows 没有 POSIX 权限位，直接跳过——该平台的 ACL 由继承目录决定。
    /// 权限设置失败<b>不静默降级</b>：那意味着密钥可能被同机其他用户读到，
    /// 必须让部署方知道，而不是制造「以为收紧了其实没收紧」的错觉。
    /// </remarks>
    private static void RestrictToOwner(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
            // 临时文件删不掉不该掩盖原始异常；交给备份任务或运维清理即可。
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
