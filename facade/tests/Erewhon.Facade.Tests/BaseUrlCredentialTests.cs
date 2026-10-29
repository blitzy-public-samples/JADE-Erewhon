// Credential handling of the routing table's target base URLs. The facade carries and adds no
// credential, so a base URL carrying user information is rejected at start-up and on reload, and no
// validation failure, rejected-reload log entry or /admin/info body quotes a configured base URL, or a
// target name that may hold one. A failure names the entry by its configuration path and the rule the
// value breaks; the start-up error log, the line Program.cs writes to standard error and the
// rejected-reload log entry are all built from those failures.
//
// Every case runs in-process against RoutingOptionsValidator, OperationRouter and
// AdminEndpoints.DisplayBaseUrl directly: a table the validator rejects never starts a host, and the
// sanitiser is the only part of /admin/info that a URL passes through.
//
// Facade test failures are identified across runs by a signature over the exception type, the failing
// line and the assertion message, so every value here is a fixed literal: the URLs are synthetic, use
// no live port and hold no real credential.

using Erewhon.Contracts.Catalogue;
using Erewhon.Facade.Admin;
using Erewhon.Facade.Routing;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Xunit;

namespace Erewhon.Facade.Tests;

/// <summary>
/// Proves that a base URL carrying user information is rejected without being echoed, that a target
/// name holding an at-sign is never quoted, that a rejected reload logs no credential while routing keeps
/// the last valid table, and that <c>/admin/info</c> never shows user information.
/// </summary>
public sealed class BaseUrlCredentialTests
{
    private const string TargetName = "backend";
    private const string RestBaseUrl = "http://standin:65000";
    private const string SoapBaseUrl = "http://standin:21001";

    // The fragments of the synthetic credentials that must never reach a message or a log. "user" alone
    // is not one, because the rejection itself says "must not contain user information".
    private const string SecretFragment = "secret";
    private const string UserNameFragment = "user:";
    private const string AtSign = "@";

    // The rejected-reload log entry OperationRouter writes.
    private const int ReloadRejectedEventId = 1101;

    /// <summary>
    /// A base URL carrying user information, on either surface's property, is rejected with one failure
    /// that names the entry and the rule it breaks and quotes no part of the value.
    /// </summary>
    /// <param name="property">The target property holding the URL, <c>RestBaseUrl</c> or <c>SoapBaseUrl</c>.</param>
    /// <param name="url">The configured URL.</param>
    /// <param name="reason">The rule the URL breaks first, as the failure words it.</param>
    [Theory]
    [InlineData(nameof(RoutingTarget.RestBaseUrl), "http://user:secret@backend:65000", "must not contain user information")]
    [InlineData(nameof(RoutingTarget.SoapBaseUrl), "http://user:secret@backend:65000", "must not contain user information")]
    [InlineData(nameof(RoutingTarget.RestBaseUrl), "https://user@backend", "must not contain user information")]
    [InlineData(nameof(RoutingTarget.SoapBaseUrl), "https://user@backend", "must not contain user information")]
    [InlineData(nameof(RoutingTarget.RestBaseUrl), "http://user:secret@backend/base?bad=1", "must not contain user information")]
    [InlineData(nameof(RoutingTarget.SoapBaseUrl), "http://user:secret@backend/base?bad=1", "must not contain user information")]
    [InlineData(nameof(RoutingTarget.RestBaseUrl), "http://user:secret@backend:99999", "is not an absolute URL")]
    [InlineData(nameof(RoutingTarget.SoapBaseUrl), "http://user:secret@backend:99999", "is not an absolute URL")]
    [InlineData(nameof(RoutingTarget.RestBaseUrl), "user:secret@backend", "is not an http or https URL")]
    [InlineData(nameof(RoutingTarget.SoapBaseUrl), "user:secret@backend", "is not an http or https URL")]
    public void ValidatorRejectsBaseUrlWithUserInformationWithoutEchoingIt(string property, string url, string reason)
    {
        RoutingOptions options = Table(RestBaseUrl, SoapBaseUrl);
        RoutingTarget target = options.Targets[TargetName];
        if (property == nameof(RoutingTarget.RestBaseUrl))
        {
            target.RestBaseUrl = url;
        }
        else
        {
            target.SoapBaseUrl = url;
        }

        ValidateOptionsResult result = new RoutingOptionsValidator(OperationCatalogue.Default).Validate(Options.DefaultName, options);

        Assert.True(result.Failed);
        Assert.Equal(new[] { $"Routing:Targets:{TargetName}:{property} {reason}" }, result.Failures);
        AssertHoldsNoCredential(result.FailureMessage);

        // The text Program.cs writes to standard error and the host logs when start-up stops.
        AssertHoldsNoCredential(new OptionsValidationException(Options.DefaultName, typeof(RoutingOptions), result.Failures).Message);
    }

    /// <summary>
    /// Shipped and documented base URLs, which carry no user information, still validate, and the
    /// router's parser accepts them.
    /// </summary>
    /// <param name="restBaseUrl">The target's REST base URL.</param>
    /// <param name="soapBaseUrl">The target's SOAP base URL.</param>
    [Theory]
    [InlineData("http://standin:65000", "http://standin:21001")]
    [InlineData("http://orders:8080", "http://orders:8081")]
    [InlineData("http://127.0.0.1:1/base", "http://127.0.0.1:2/base")]
    public void ValidatorAcceptsBaseUrlsWithoutUserInformation(string restBaseUrl, string soapBaseUrl)
    {
        ValidateOptionsResult result = new RoutingOptionsValidator(OperationCatalogue.Default)
            .Validate(Options.DefaultName, Table(restBaseUrl, soapBaseUrl));

        Assert.True(result.Succeeded);
        Assert.True(RoutingOptionsValidator.TryParseBaseUrl(restBaseUrl, out _));
        Assert.True(RoutingOptionsValidator.TryParseBaseUrl(soapBaseUrl, out _));
    }

    /// <summary>
    /// The reason helper and <see cref="RoutingOptionsValidator.TryParseBaseUrl"/> agree on every value:
    /// null for a usable base URL, otherwise the first rule broken, in a fixed phrase.
    /// </summary>
    /// <param name="url">The configured value.</param>
    /// <param name="reason">The expected phrase; null when the value is usable.</param>
    [Theory]
    [InlineData("http://standin:65000", null)]
    [InlineData(" https://standin/base/ ", null)]
    [InlineData("   ", "is empty")]
    [InlineData("standin:65000/base?x", "is not an http or https URL")]
    [InlineData("not a url", "is not an absolute URL")]
    [InlineData("ftp://user:secret@backend", "is not an http or https URL")]
    [InlineData("http://user:@backend", "must not contain user information")]
    [InlineData("http://user\uFF20backend:65000", "must not contain user information")]
    [InlineData("http://user\uFE6Bbackend", "must not contain user information")]
    [InlineData("http://backend/base?x=1", "must not contain a query")]
    [InlineData("http://backend?", "must not contain a query")]
    [InlineData("http://backend/base#top", "must not contain a fragment")]
    public void ReasonNamesTheFirstRuleBrokenAndAgreesWithTheParser(string url, string? reason)
    {
        Assert.Equal(reason, RoutingOptionsValidator.DescribeBaseUrlProblem(url));
        Assert.Equal(reason is null, RoutingOptionsValidator.TryParseBaseUrl(url, out Uri? parsed));
        Assert.Equal(reason is null, parsed is not null);
    }

    /// <summary>
    /// An undefined target name is quoted as configured unless it holds an at-sign, by the validator's
    /// default-target and operation rules and by the router's failure for an unresolvable entry.
    /// </summary>
    /// <param name="targetName">The undefined target name both entries hold.</param>
    /// <param name="quoted">Whether the failures quote the name.</param>
    [Theory]
    [InlineData("nowhere", true)]
    [InlineData("http://user:secret@backend:65000", false)]
    [InlineData("user\uFF20backend", false)]
    public void UndefinedTargetNameIsQuotedOnlyWithoutAtSign(string targetName, bool quoted)
    {
        string notShown = "names an undefined target, not shown because it may contain user information";
        string defaultFailure = quoted
            ? $"Routing:DefaultTarget names undefined target '{targetName}'"
            : $"Routing:DefaultTarget {notShown}";
        string operationFailure = quoted
            ? $"Routing:Operations:Soap:getPhoto names undefined target '{targetName}'"
            : $"Routing:Operations:Soap:getPhoto {notShown}";

        RoutingOptions options = Table(RestBaseUrl, SoapBaseUrl);
        options.DefaultTarget = targetName;
        options.Operations.Soap["getPhoto"] = targetName;

        ValidateOptionsResult result = new RoutingOptionsValidator(OperationCatalogue.Default).Validate(Options.DefaultName, options);

        Assert.True(result.Failed);
        Assert.Equal(new[] { defaultFailure, operationFailure }, result.Failures);
        AssertHoldsNoCredential(result.FailureMessage);

        // The router builds the same failure for an entry it cannot resolve. Only a table that skipped
        // validation can reach it, so the monitor here serves the table unvalidated.
        RoutingOptions routed = Table(RestBaseUrl, SoapBaseUrl);
        routed.Operations.Soap["getPhoto"] = targetName;
        OperationRouter router = Router(new TestOptionsMonitor<RoutingOptions>(routed), new CapturingLogger());

        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(
            () => router.Resolve(Surface.Soap, OperationCatalogue.Default.Find(Surface.Soap, "getPhoto")));
        Assert.Equal($"The routing table cannot route Soap.getPhoto: {operationFailure}.", exception.Message);
        AssertHoldsNoCredential(exception.Message);
    }

    /// <summary>
    /// A reload rejected for a base URL carrying user information and a query keeps the last valid table
    /// in service and is logged once, naming the entry and holding no credential, however many requests
    /// arrive while it stays rejected.
    /// </summary>
    [Fact]
    public void RejectedReloadKeepsLastValidTableAndLogsNoCredential()
    {
        var routing = new TestOptionsMonitor<RoutingOptions>(Table(RestBaseUrl, SoapBaseUrl));
        var logger = new CapturingLogger();
        OperationRouter router = Router(routing, logger);

        Uri restBaseUrl = router.Resolve(Surface.Rest, operation: null).BaseUrl;
        Assert.Equal(new Uri(RestBaseUrl), restBaseUrl);

        // The exception the options monitor throws for the reloaded table, produced by the real validator.
        RoutingOptions reloaded = Table("http://USER:SECRET@backend/base?bad=1", SoapBaseUrl);
        ValidateOptionsResult result = new RoutingOptionsValidator(OperationCatalogue.Default).Validate(Options.DefaultName, reloaded);
        Assert.True(result.Failed);
        routing.Rejection = new OptionsValidationException(Options.DefaultName, typeof(RoutingOptions), result.Failures);

        Assert.Equal(restBaseUrl, router.Resolve(Surface.Rest, operation: null).BaseUrl);
        Assert.Equal(restBaseUrl, router.Resolve(Surface.Rest, OperationCatalogue.Default.Find(Surface.Rest, "getCompany")).BaseUrl);
        Assert.Equal(new Uri(SoapBaseUrl), router.Resolve(Surface.Soap, operation: null).BaseUrl);

        LogEntry rejected = Assert.Single(logger.Entries);
        Assert.Equal(LogLevel.Error, rejected.Level);
        Assert.Equal(ReloadRejectedEventId, rejected.EventId);
        Assert.Contains("Routing:Targets:backend:RestBaseUrl must not contain user information", rejected.Message, StringComparison.Ordinal);
        Assert.All(logger.Entries, static entry =>
        {
            AssertHoldsNoCredential(entry.Message);
            Assert.All(entry.State, static value => AssertHoldsNoCredential(value));
        });
    }

    /// <summary>
    /// <see cref="AdminEndpoints.DisplayBaseUrl"/> reports an accepted URL exactly as configured, strips
    /// user information, query and fragment from an http or https URL carrying user information, and
    /// reports null for anything it cannot show safely.
    /// </summary>
    /// <param name="configured">The configured base URL.</param>
    /// <param name="expected">The <c>/admin/info</c> value.</param>
    [Theory]
    [InlineData(null, null)]
    [InlineData("http://standin:65000", "http://standin:65000")]
    [InlineData("http://standin:21001", "http://standin:21001")]
    [InlineData("http://127.0.0.1:1/base", "http://127.0.0.1:1/base")]
    [InlineData("http://user:secret@backend:65000", "http://backend:65000/")]
    [InlineData("https://user@backend", "https://backend/")]
    [InlineData("http://user:secret@backend/base?bad=1#top", "http://backend/base")]
    [InlineData("http://user:secret@backend:99999", null)]
    [InlineData("user:secret@backend", null)]
    [InlineData("ftp://user:secret@backend", null)]
    [InlineData("http://user\uFF20backend:65000", null)]
    [InlineData("http://backend/base?x=1", null)]
    public void AdminInfoBaseUrlNeverShowsUserInformation(string? configured, string? expected)
    {
        string? shown = AdminEndpoints.DisplayBaseUrl(configured);

        Assert.Equal(expected, shown);
        AssertHoldsNoCredential(shown);
    }

    // The shipped shape: one target with both base URLs, the default target, no operation listed.
    private static RoutingOptions Table(string restBaseUrl, string soapBaseUrl) => new()
    {
        DefaultTarget = TargetName,
        Targets = new Dictionary<string, RoutingTarget>(StringComparer.OrdinalIgnoreCase)
        {
            [TargetName] = new RoutingTarget { RestBaseUrl = restBaseUrl, SoapBaseUrl = soapBaseUrl },
        },
    };

    private static OperationRouter Router(IOptionsMonitor<RoutingOptions> routing, ILogger<OperationRouter> logger) =>
        new(routing, new TestOptionsMonitor<BackendOptions>(new BackendOptions()), OperationCatalogue.Default, logger);

    private static void AssertHoldsNoCredential(string? text)
    {
        if (text is null)
        {
            return;
        }

        Assert.DoesNotContain(SecretFragment, text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(UserNameFragment, text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(AtSign, text, StringComparison.Ordinal);
        Assert.DoesNotContain("\uFF20", text, StringComparison.Ordinal);
    }

    /// <summary>One captured log call.</summary>
    /// <param name="Level">The entry's level.</param>
    /// <param name="EventId">The entry's event id.</param>
    /// <param name="Message">The formatted message.</param>
    /// <param name="State">Every structured state value, as text.</param>
    private sealed record LogEntry(LogLevel Level, int EventId, string Message, IReadOnlyList<string?> State);

    /// <summary>An <see cref="ILogger{TCategoryName}"/> that records every entry, enabled at every level.</summary>
    private sealed class CapturingLogger : ILogger<OperationRouter>
    {
        private readonly List<LogEntry> _entries = [];
        private readonly Lock _gate = new();

        /// <summary>The entries logged so far, in order.</summary>
        public IReadOnlyList<LogEntry> Entries
        {
            get
            {
                lock (_gate)
                {
                    return [.. _entries];
                }
            }
        }

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            ArgumentNullException.ThrowIfNull(formatter);

            IReadOnlyList<string?> values = state is IEnumerable<KeyValuePair<string, object?>> pairs
                ? [.. pairs.Select(static pair => pair.Value?.ToString())]
                : [];
            var entry = new LogEntry(logLevel, eventId.Id, formatter(state, exception), values);
            lock (_gate)
            {
                _entries.Add(entry);
            }
        }
    }

    /// <summary>
    /// An <see cref="IOptionsMonitor{TOptions}"/> serving one fixed value until <see cref="Rejection"/> is
    /// set, and from then on throwing it, as the options monitor does for a reload that fails validation.
    /// </summary>
    /// <param name="value">The value served while no rejection is set.</param>
    private sealed class TestOptionsMonitor<TOptions>(TOptions value) : IOptionsMonitor<TOptions>
    {
        private volatile OptionsValidationException? _rejection;

        /// <summary>The exception every read throws once set; null serves the value.</summary>
        public OptionsValidationException? Rejection
        {
            get => _rejection;
            set => _rejection = value;
        }

        public TOptions CurrentValue => _rejection is { } rejection ? throw rejection : value;

        public TOptions Get(string? name) => CurrentValue;

        public IDisposable? OnChange(Action<TOptions, string?> listener) => null;
    }
}
