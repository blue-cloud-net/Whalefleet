using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Docker.DotNet;
using Whalefleet.Application.Abstractions;
using Whalefleet.Application.Common;
using Whalefleet.Core.Hosts;
using Whalefleet.Infrastructure.Docker;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Whalefleet.IntegrationTests.Infrastructure;

/// <summary>
/// <see cref="DockerClientFactory"/> 的集成测试：把连接描述构建成可用的客户端。
/// </summary>
/// <remarks>
/// <para><b>为什么需要它</b>：若只做编译验证，底层 Docker SDK 的用法错误要等到 0.0.9
/// 第一次真正调用时才暴露。本测试实际执行客户端构建，把「能编译」升级为「能构造出可用客户端」。</para>
/// <para>测试不依赖真实 Docker 守护进程：构建客户端本身不建立连接（只有 Ping 才需要守护进程）。</para>
/// <para><b>职责边界</b>：本类只测「新建」。客户端的复用、空闲回收、失效与释放见
/// <see cref="DockerClientPoolTests"/>。</para>
/// </remarks>
public class DockerClientFactoryTests
{
    [Theory]
    [InlineData(DockerHostType.Tcp, "tcp://127.0.0.1:2375")]
    [InlineData(DockerHostType.Socket, "/var/run/docker.sock")]
    [InlineData(DockerHostType.Socket, "unix:///var/run/docker.sock")]
    public void CreateClient_ShouldBuildClientForSupportedEndpoints(DockerHostType type, string endpoint)
    {
        var factory = CreateFactory();

        using var client = factory.CreateClient(Connection("local", type, endpoint));

        client.Should().NotBeNull();
    }

    [Fact]
    public void CreateClient_ShouldReturnDistinctInstances()
    {
        // 工厂是无状态的纯构建器：每次调用都新建，不复用任何东西。
        var factory = CreateFactory();
        var connection = Connection("local", DockerHostType.Socket, "/var/run/docker.sock");

        using var first = factory.CreateClient(connection);
        using var second = factory.CreateClient(connection);

        second.Should().NotBeSameAs(first);
    }

    [Fact]
    public void CreateClient_ShouldThrowHostUnreachableForUnsupportedHostType()
    {
        var factory = CreateFactory();

        var act = () => factory.CreateClient(Connection("local", (DockerHostType)999, "tcp://127.0.0.1:2375"));

        (act.Should().Throw<BusinessException>())
            .Which.Code.Should().Be(ErrorCodes.HostUnreachable);
    }

    [Theory]
    [InlineData("relative/docker.sock")]
    [InlineData("http://127.0.0.1:2375")]
    [InlineData("")]
    [InlineData("   ")]
    public void CreateClient_ShouldThrowHostUnreachableForInvalidEndpoint(string endpoint)
    {
        var factory = CreateFactory();

        var act = () => factory.CreateClient(Connection("local", DockerHostType.Socket, endpoint));

        (act.Should().Throw<BusinessException>())
            .Which.Code.Should().Be(ErrorCodes.HostUnreachable);
    }

    [Fact]
    public void CreateClient_ShouldThrowForNullConnection()
    {
        var factory = CreateFactory();

        var act = () => factory.CreateClient(null!);

        act.Should().Throw<ArgumentNullException>();
    }

    private static DockerClientFactory CreateFactory() => new(NullLogger<DockerClientFactory>.Instance);

    private static DockerHostConnection Connection(string hostId, DockerHostType type, string endpoint) => new()
    {
        HostId = hostId,
        Type = type,
        Endpoint = endpoint,
    };

    [Fact]
    public void CreateClient_ShouldBuildClientWithBasicAuthOverTls()
    {
        var factory = CreateFactory();
        var connection = Connection("remote", DockerHostType.Tcp, "https://docker.example")
            with { Auth = BasicAuth("admin", "s3cret") };

        using var client = factory.CreateClient(connection);

        client.Should().NotBeNull();
    }

    [Fact]
    public void CreateClient_ShouldBuildClientWithBasicAuthOverPlainTcp()
    {
        // 明文 + Basic 不阻断（内网专线上确实有人这么用），但必须在日志里留痕——
        // 这条用例同时守住"不因为不安全就拒绝连接"这个选择。
        var factory = CreateFactory();
        var connection = Connection("remote", DockerHostType.Tcp, "tcp://docker.example:2375")
            with { Auth = BasicAuth("admin", "s3cret") };

        using var client = factory.CreateClient(connection);

        client.Should().NotBeNull();
    }

    [Fact]
    public void CreateClient_ShouldBuildClientWithTlsCertificates()
    {
        using var files = TlsCertificateFiles.Create();
        var factory = CreateFactory();
        var connection = Connection("remote", DockerHostType.Tcp, "https://docker.example:2376")
            with { Auth = TlsAuth(files) };

        // 先自证夹具是配套的：证书与私钥必须能配对，否则失败会指向工厂而浪费排查时间。
        using (var probe = X509Certificate2.CreateFromPemFile(files.CertificatePath, files.KeyPath))
        {
            probe.HasPrivateKey.Should().BeTrue();
        }

        // 客户端构建会真正读取 PEM（因此这条用例同时验证了证书加载路径）；
        // 建立连接要等 Ping，不在本测试范围内。
        using var client = factory.CreateClient(connection);

        client.Should().NotBeNull();
    }

    [Fact]
    public void CreateClient_ShouldRejectTlsCredentialsOnPlainTcpEndpoint()
    {
        // 明文端点配了客户端证书：库不会把证书发出去，连接会"看起来启用了 TLS"却是明文——
        // 这种静默降级必须在构建阶段就拦下。
        using var files = TlsCertificateFiles.Create();
        var factory = CreateFactory();
        var connection = Connection("remote", DockerHostType.Tcp, "tcp://docker.example:2375")
            with { Auth = TlsAuth(files) };

        var act = () => factory.CreateClient(connection);

        (act.Should().Throw<BusinessException>())
            .Which.Code.Should().Be(ErrorCodes.HostUnreachable);
        act.Should().Throw<BusinessException>().WithMessage("*https://*");
    }

    [Fact]
    public void CreateClient_ShouldThrowHostUnreachableWhenCertificateFilesMissing()
    {
        var missing = Path.Combine(Path.GetTempPath(), "dm-missing-cert", Guid.NewGuid().ToString("N"));

        var factory = CreateFactory();
        var connection = Connection("remote", DockerHostType.Tcp, "https://docker.example:2376")
            with
            {
                Auth = TlsAuthWithPaths(
                    Path.Combine(missing, "ca.pem"),
                    Path.Combine(missing, "cert.pem"),
                    Path.Combine(missing, "key.pem")),
            };

        var act = () => factory.CreateClient(connection);

        (act.Should().Throw<BusinessException>())
            .Which.Code.Should().Be(ErrorCodes.HostUnreachable);
    }

    [Fact]
    public void CreateClient_ShouldThrowWhenAuthPayloadMissing()
    {
        // 直接构造出"声明了认证但没带凭据"的记录：应得到可读的业务错误，而不是空引用异常。
        var factory = CreateFactory();
        var connection = Connection("remote", DockerHostType.Tcp, "https://docker.example:2376")
            with { Auth = new DockerHostAuth { Kind = DockerHostAuthKind.Tls } };

        var act = () => factory.CreateClient(connection);

        (act.Should().Throw<BusinessException>())
            .Which.Code.Should().Be(ErrorCodes.HostUnreachable);
    }

    private static DockerHostAuth BasicAuth(string username, string password) =>
        DockerHostAuth.From(HostCredentials.FromJson(
            DockerHostAuthKind.Basic,
            $$"""{ "username": "{{username}}", "password": "{{password}}" }"""));

    private static DockerHostAuth TlsAuth(TlsCertificateFiles files) =>
        TlsAuthWithPaths(files.CaPath, files.CertificatePath, files.KeyPath);

    private static DockerHostAuth TlsAuthWithPaths(string caPath, string certPath, string keyPath) =>
        DockerHostAuth.From(HostCredentials.FromJson(
            DockerHostAuthKind.Tls,
            $$"""
            {
              "caCertificatePath": "{{caPath}}",
              "clientCertificatePath": "{{certPath}}",
              "clientKeyPath": "{{keyPath}}"
            }
            """));

    /// <summary>
    /// 临时目录里的一套自签证书（CA + 客户端证书 + 私钥，均为 PEM）。
    /// </summary>
    /// <remarks>
    /// 真造证书而不是造假路径：证书加载本身就是被测路径的一部分，
    /// 用不存在的路径测只能证明"会报错"，证明不了"合法证书能连上"。
    /// </remarks>
    private sealed class TlsCertificateFiles : IDisposable
    {
        private readonly string _directory;

        private TlsCertificateFiles(string directory, string caPath, string certificatePath, string keyPath)
        {
            _directory = directory;
            this.CaPath = caPath;
            this.CertificatePath = certificatePath;
            this.KeyPath = keyPath;
        }

        internal string CaPath { get; }

        internal string CertificatePath { get; }

        internal string KeyPath { get; }

        internal static TlsCertificateFiles Create()
        {
            var directory = Path.Combine(
                Path.GetTempPath(),
                "whalefleet-tls-tests",
                Guid.NewGuid().ToString("N"));

            Directory.CreateDirectory(directory);

            using var caKey = RSA.Create(2048);
            var caRequest = new CertificateRequest(
                "CN=whalefleet-test-ca",
                caKey,
                HashAlgorithmName.SHA256,
                RSASignaturePadding.Pkcs1);
            using var caCertificate = caRequest.CreateSelfSigned(
                DateTimeOffset.UtcNow.AddDays(-1),
                DateTimeOffset.UtcNow.AddDays(1));

            using var clientKey = RSA.Create(2048);
            var clientRequest = new CertificateRequest(
                "CN=whalefleet-test-client",
                clientKey,
                HashAlgorithmName.SHA256,
                RSASignaturePadding.Pkcs1);
            using var clientCertificate = clientRequest.CreateSelfSigned(
                DateTimeOffset.UtcNow.AddDays(-1),
                DateTimeOffset.UtcNow.AddDays(1));

            var caPath = Path.Combine(directory, "ca.pem");
            var certificatePath = Path.Combine(directory, "cert.pem");
            var keyPath = Path.Combine(directory, "key.pem");

            File.WriteAllText(caPath, caCertificate.ExportCertificatePem());
            File.WriteAllText(certificatePath, clientCertificate.ExportCertificatePem());
            File.WriteAllText(keyPath, clientKey.ExportPkcs8PrivateKeyPem());

            return new TlsCertificateFiles(directory, caPath, certificatePath, keyPath);
        }

        public void Dispose()
        {
            if (Directory.Exists(_directory))
            {
                Directory.Delete(_directory, recursive: true);
            }
        }
    }
}
