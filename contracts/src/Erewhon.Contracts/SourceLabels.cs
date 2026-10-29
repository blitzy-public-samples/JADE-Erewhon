namespace Erewhon.Contracts;

/// <summary>
/// The labels that mark JADE stand-in output wherever it is recorded or shown, shared so that the
/// stand-in and the harness never restate them.
/// </summary>
/// <remarks>
/// These labels belong in metadata, files and console output only. None is ever added to a REST or
/// SOAP response body or header, because the facade must forward the backend's responses unaltered.
/// </remarks>
public static class SourceLabels
{
    /// <summary>
    /// The source identifier of stand-in output: the <c>source</c> value of every baseline record,
    /// of <c>baseline/manifest.json</c> and of the stand-in's <c>/admin/info</c> response. The
    /// harness requires <c>/admin/info</c> to report exactly this value after every state reset.
    /// </summary>
    public const string StandIn = "jade-standin";

    /// <summary>
    /// The banner the stand-in prints at startup and on every state reset.
    /// </summary>
    public const string Banner = "JADE STAND-IN — output is not recorded JADE behaviour";

    /// <summary>
    /// The label text <c>baseline/manifest.json</c> carries beside its <c>source</c> value.
    /// </summary>
    public const string BaselineLabel = "Recorded from the JADE stand-in, not from live JADE";

    /// <summary>
    /// The text of the XML comment that heads <c>contracts/wsdl/ErewhonWebService.wsdl</c>.
    /// </summary>
    /// <remarks>
    /// It must never contain <c>--</c>, which XML forbids inside a comment. The XML comparers drop
    /// comments, so the label does not affect the WSDL contract check.
    /// </remarks>
    public const string WsdlHeaderComment =
        "Modelled by the JADE stand-in on JADE 2022 default conventions; not captured from live JADE";

    /// <summary>
    /// The prefix of every baseline-related line the harness writes to the console.
    /// </summary>
    public const string ConsolePrefix = "[jade-standin]";
}
