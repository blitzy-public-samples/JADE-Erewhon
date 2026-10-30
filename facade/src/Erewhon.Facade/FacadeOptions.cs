using System.Net;
using Erewhon.Contracts.Admin;

namespace Erewhon.Facade;

/// <summary>
/// The <c>Facade</c> configuration section: the three listeners the facade owns and the WSDL it
/// publishes.
/// </summary>
/// <remarks>
/// The property names are the configuration keys, so <c>appsettings.json</c>, environment variables
/// such as <c>Facade__RestPort=15500</c> and test settings such as
/// <c>UseSetting("Facade:AdminPort", ...)</c> all bind to them. Every listener is plain HTTP, as the
/// JADE applications are (<c>_webIsSecure false</c>), so there is no TLS or credential setting.
/// </remarks>
public sealed class FacadeOptions
{
    /// <summary>The configuration section this class binds to.</summary>
    public const string SectionName = "Facade";

    /// <summary>The REST listener port, the address of the JADE <c>ErewhonREST</c> application.</summary>
    public int RestPort { get; set; } = 65000;

    /// <summary>The SOAP listener port, the address of the JADE <c>WebServiceHTTP</c> application.</summary>
    public int SoapPort { get; set; } = 21001;

    /// <summary>The internal admin listener port, which Compose never publishes.</summary>
    public int AdminPort { get; set; } = AdminRoutes.DefaultPort;

    /// <summary>
    /// The address every listener binds to. <c>harness/scripts/run-local.sh</c> sets <c>127.0.0.1</c>,
    /// so the unauthenticated admin listener is reachable only from the loopback interface there.
    /// </summary>
    public string BindAddress { get; set; } = "0.0.0.0";

    /// <summary>
    /// The WSDL file the facade publishes, absolute or relative to the application base directory.
    /// The default resolves to <c>/app/contracts/wsdl/ErewhonWebService.wsdl</c> in the image, whose
    /// runtime stage copies the whole <c>contracts/</c> directory; <c>run-local.sh</c> sets the
    /// repository's absolute path.
    /// </summary>
    public string WsdlPath { get; set; } = "contracts/wsdl/ErewhonWebService.wsdl";

    /// <summary>Returns the absolute path of <see cref="WsdlPath"/>.</summary>
    /// <remarks>
    /// The file may be absent, because capture can end without a WSDL. That never stops the facade:
    /// the WSDL holder in <c>Soap/SoapEndpoints.cs</c> reports it absent, and WSDL requests are then
    /// forwarded to the default target unchanged. A relative path is resolved against
    /// <see cref="AppContext.BaseDirectory"/> rather than the working directory, so it names the same
    /// file however the process is started.
    /// </remarks>
    /// <returns><see cref="WsdlPath"/> unchanged when it is rooted; otherwise its full path under the base directory.</returns>
    public string ResolveWsdlPath() =>
        Path.IsPathRooted(WsdlPath)
            ? WsdlPath
            : Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, WsdlPath));

    /// <summary>Parses <see cref="BindAddress"/>.</summary>
    /// <returns>The IPv4 or IPv6 address the listeners bind to.</returns>
    /// <exception cref="InvalidOperationException"><see cref="BindAddress"/> is not an IP address.</exception>
    public IPAddress GetBindAddress() =>
        IPAddress.TryParse(BindAddress, out IPAddress? address)
            ? address
            : throw new InvalidOperationException(
                $"{SectionName}:{nameof(BindAddress)} must be an IP address; the value is '{BindAddress}'.");
}

/// <summary>
/// The <c>Backend</c> configuration section: the deadline of every forwarded call.
/// </summary>
/// <remarks>
/// <c>Program.cs</c> validates <see cref="TimeoutSeconds"/> at start-up with a message naming
/// <see cref="TimeoutKey"/>. A routing target may override the deadline with its own
/// <c>TimeoutSeconds</c>.
/// </remarks>
public sealed class BackendOptions
{
    /// <summary>The configuration section this class binds to.</summary>
    public const string SectionName = "Backend";

    /// <summary>The configuration key of <see cref="TimeoutSeconds"/>, for validation messages.</summary>
    public const string TimeoutKey = SectionName + ":" + nameof(TimeoutSeconds);

    /// <summary>
    /// Seconds a forwarded call may take before the facade answers REST 504 or a SOAP Fault; it must
    /// be greater than 0.
    /// </summary>
    public int TimeoutSeconds { get; set; } = 30;

    /// <summary><see cref="TimeoutSeconds"/> as a <see cref="TimeSpan"/>.</summary>
    public TimeSpan Timeout => TimeSpan.FromSeconds(TimeoutSeconds);
}
