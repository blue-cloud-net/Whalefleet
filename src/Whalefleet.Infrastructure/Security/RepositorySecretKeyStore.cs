using System.Security.Cryptography;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Whalefleet.Infrastructure.Persistence;

namespace Whalefleet.Infrastructure.Security;

/// <summary>
/// 仓库凭据加密密钥的来源：在数据目录下按需生成并复用一份 32 字节密钥。
/// </summary>
/// <remarks>
/// <para><b>为什么与 JWT 密钥分开存</b>：两者用途不同（一个签令牌，一个加密口令），
/// 分开存意味着「泄漏了 JWT 密钥」不等于「能解出口令」。代价是多一个文件要备份——
/// <c>scripts/backup.sh</c> 会把它一并带走。</para>
/// <para><b>为什么必须落盘</b>：密钥每次启动重新生成的话，
/// 库里所有已保存的口令都会解不开（表现为「仓库认证失败」），
/// 而管理员完全无从判断是自己的口令错了还是系统把密钥换了。</para>
/// <para><b>与 <see cref="SecretFileStore"/> 同源</b>：数据目录取自
/// <c>Persistence:DatabasePath</c> 所在目录，因此密钥与数据库天然同卷。</para>
/// <para><b>为什么保留旧文件名回退</b>：本文件从 <c>registry.key</c> 改名为
/// <c>repository.key</c>，但已部署的数据卷上仍是旧名字。
/// 若只认新名字，旧卷会**静默生成一把新密钥**——库里已保存的口令从此全部解不开，
/// 症状是「认证失败」，根因却完全看不出来。因此旧名存在时一律优先读它。</para>
/// </remarks>
internal sealed class RepositorySecretKeyStore
{
    /// <summary>
    /// 仓库加密密钥文件名（位于数据目录下）。
    /// </summary>
    internal const string KeyFileName = "repository.key";

    /// <summary>
    /// 改名前的密钥文件名（仅用于兼容已存在的数据卷，**不再新写**）。
    /// </summary>
    internal const string LegacyKeyFileName = "registry.key";

    private readonly string _dataDirectory;
    private readonly ILogger<RepositorySecretKeyStore> _logger;

    /// <summary>
    /// 初始化密钥存储。
    /// </summary>
    /// <param name="options">持久化配置（用于确定数据目录）。</param>
    /// <param name="logger">日志。</param>
    /// <exception cref="ArgumentNullException">任一参数为 <see langword="null"/>。</exception>
    public RepositorySecretKeyStore(IOptions<SqliteOptions> options, ILogger<RepositorySecretKeyStore> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);

        var databasePath = options.Value.DatabasePath;

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
    /// 取得加密密钥；文件不存在或内容不合法时生成并写入。
    /// </summary>
    /// <returns>32 字节密钥材料。</returns>
    /// <remarks>
    /// <b>不复用 SecretFileStore 的写入逻辑</b>：那个类存的是 Base64 文本密钥，
    /// 本类要的是<b>原始字节</b>（AES 密钥直接取字节比先编码再解码少一层转换，
    /// 也少一个「Base64 文本被换行符污染」的失败模式）。
    /// </remarks>
    public byte[] GetKey()
    {
        var path = Path.Combine(_dataDirectory, KeyFileName);

        if (File.Exists(path))
        {
            var existing = TryRead(path);
            if (existing is not null)
            {
                _logger.LogDebug("复用已有仓库加密密钥文件 {Path}", path);
                return existing;
            }

            _logger.LogWarning(
                "仓库加密密钥文件 {Path} 内容不合法（长度或格式不对），将重新生成。"
                + "已保存的仓库口令将无法再解密——需要重新填写。",
                path);
        }

        // ⚠️ 旧文件名回退：已部署的数据卷上是 registry.key。
        // 认不出它就会静默生成新密钥 → 库里所有已保存的口令从此解不开。
        var legacyPath = Path.Combine(_dataDirectory, LegacyKeyFileName);
        if (File.Exists(legacyPath))
        {
            var legacy = TryRead(legacyPath);
            if (legacy is not null)
            {
                _logger.LogWarning(
                    "检测到旧版密钥文件 {LegacyPath}，将继续使用它以保证已保存的仓库口令可解密。"
                    + "建议手动改名为 {Path}。",
                    legacyPath,
                    path);

                return legacy;
            }

            _logger.LogWarning(
                "旧版密钥文件 {LegacyPath} 内容不合法（长度或格式不对），将重新生成。"
                + "已保存的仓库口令将无法再解密——需要重新填写。",
                legacyPath);
        }

        var generated = RandomNumberGenerator.GetBytes(RepositorySecretCipher.KeySizeBytes);
        this.WriteAtomically(path, generated);

        _logger.LogInformation(
            "已生成仓库加密密钥并保存到 {Path}（{ByteCount} 字节随机数）。"
            + "请连同数据卷一起备份该文件——丢失它会导致已保存的仓库口令全部无法解密。",
            path,
            generated.Length);

        return generated;
    }

    /// <summary>
    /// 读取密钥文件；不存在或长度不对时返回 <see langword="null"/>。
    /// </summary>
    private static byte[]? TryRead(string path)
    {
        byte[] content;

        try
        {
            content = File.ReadAllBytes(path);
        }
        catch (IOException)
        {
            return null;
        }

        return content.Length == RepositorySecretCipher.KeySizeBytes ? content : null;
    }

    /// <summary>
    /// 以「临时文件 + 原子替换」写入密钥（权限 <c>0600</c>）。
    /// </summary>
    private void WriteAtomically(string path, byte[] content)
    {
        Directory.CreateDirectory(_dataDirectory);

        var temporaryPath =
            $"{path}.{Environment.ProcessId}.{RandomNumberGenerator.GetHexString(4, lowercase: true)}.tmp";

        try
        {
            File.WriteAllBytes(temporaryPath, content);
            RestrictToOwner(temporaryPath);
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
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
