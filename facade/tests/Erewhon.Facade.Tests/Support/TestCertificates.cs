// Certificates for the https backends of the forwarding tests: one certificate authority created in
// memory for the test run, the server certificates it issues, and the chain policy through which a facade
// trusts that authority alone (FacadeTestSettings.BackendCertificateChainPolicy). Everything is made with
// System.Security.Cryptography: no certificate file, certificate store or package is used, and nothing is
// added to the machine's trust, so a facade that keeps the platform's default trust rejects every
// certificate issued here.

using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace Erewhon.Facade.Tests.Support;

/// <summary>
/// The test certificate authority and the server certificates it issues for loopback https backends.
/// </summary>
public static class TestCertificates
{
    // TLS Web Server Authentication (RFC 5280, section 4.2.1.12).
    private const string ServerAuthenticationOid = "1.3.6.1.5.5.7.3.1";

    // A host name under the reserved .invalid domain, which no target is ever addressed by.
    private const string OtherHostName = "backend.invalid";

    private static readonly Lazy<X509Certificate2> Authority = new(CreateAuthority, LazyThreadSafetyMode.ExecutionAndPublication);

    private static readonly Lazy<X509Certificate2> Loopback = new(
        () => Issue(static names => names.AddIpAddress(IPAddress.Loopback)),
        LazyThreadSafetyMode.ExecutionAndPublication);

    private static readonly Lazy<X509Certificate2> OtherHost = new(
        () => Issue(static names => names.AddDnsName(OtherHostName)),
        LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>
    /// A server certificate, with its private key, for the IP address 127.0.0.1, which a target addressed
    /// as <c>https://127.0.0.1:&lt;port&gt;</c> is validated against.
    /// </summary>
    public static X509Certificate2 LoopbackServer => Loopback.Value;

    /// <summary>
    /// A server certificate, with its private key, issued by the same authority for the host name
    /// <c>backend.invalid</c> alone, so it names no loopback target.
    /// </summary>
    public static X509Certificate2 OtherHostServer => OtherHost.Value;

    /// <summary>
    /// Creates a chain policy that trusts the test authority as its only root and checks no revocation,
    /// because the authority publishes none; the certificate's host name is still validated by the TLS
    /// client. Each call returns a new policy, which the caller may change.
    /// </summary>
    /// <returns>The policy.</returns>
    public static X509ChainPolicy CreateTrustPolicy()
    {
        var policy = new X509ChainPolicy
        {
            TrustMode = X509ChainTrustMode.CustomRootTrust,
            RevocationMode = X509RevocationMode.NoCheck,
        };
        policy.CustomTrustStore.Add(X509CertificateLoader.LoadCertificate(Authority.Value.RawData));
        return policy;
    }

    // A self-signed certificate authority with its private key, valid from a day ago for 30 days, which
    // may issue end-entity certificates only.
    private static X509Certificate2 CreateAuthority()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest("CN=Erewhon facade test authority", key, HashAlgorithmName.SHA256);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(
            certificateAuthority: true, hasPathLengthConstraint: true, pathLengthConstraint: 0, critical: true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(
            X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, critical: true));
        request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, critical: false));

        DateTimeOffset now = DateTimeOffset.UtcNow;
        using X509Certificate2 created = request.CreateSelfSigned(now.AddDays(-1), now.AddDays(30));
        return X509CertificateLoader.LoadPkcs12(created.Export(X509ContentType.Pkcs12), password: null);
    }

    // A server certificate issued by the authority for the subject alternative names addNames adds, with
    // its private key, valid from a day ago for seven days, which lies inside the authority's validity. It
    // is loaded from its PKCS #12 form, so its key does not depend on the key object created here.
    private static X509Certificate2 Issue(Action<SubjectAlternativeNameBuilder> addNames)
    {
        var names = new SubjectAlternativeNameBuilder();
        addNames(names);
        X509Certificate2 authority = Authority.Value;
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest("CN=Erewhon facade test backend", key, HashAlgorithmName.SHA256);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(
            certificateAuthority: false, hasPathLengthConstraint: false, pathLengthConstraint: 0, critical: true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, critical: true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid(ServerAuthenticationOid)], critical: false));
        request.CertificateExtensions.Add(names.Build());
        request.CertificateExtensions.Add(X509AuthorityKeyIdentifierExtension.CreateFromCertificate(
            authority, includeKeyIdentifier: true, includeIssuerAndSerial: false));

        DateTimeOffset now = DateTimeOffset.UtcNow;
        byte[] serialNumber = RandomNumberGenerator.GetBytes(16);
        serialNumber[0] &= 0x7F;
        using X509Certificate2 issued = request.Create(authority, now.AddDays(-1), now.AddDays(7), serialNumber);
        using X509Certificate2 withKey = issued.CopyWithPrivateKey(key);
        return X509CertificateLoader.LoadPkcs12(withKey.Export(X509ContentType.Pkcs12), password: null);
    }
}
