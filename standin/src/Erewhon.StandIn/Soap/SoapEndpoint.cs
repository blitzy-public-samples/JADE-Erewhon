using System.Collections.Frozen;
using System.Diagnostics.CodeAnalysis;
using System.Xml;
using System.Xml.Linq;
using Erewhon.Contracts.Catalogue;
using Erewhon.Contracts.Hosting;
using Erewhon.Contracts.Matching;
using Erewhon.StandIn.Dto;
using Erewhon.StandIn.Model;
using Erewhon.StandIn.State;

namespace Erewhon.StandIn.Soap;

/// <summary>
/// The SOAP listener of the JADE stand-in: the 9 <c>webService</c> methods of <c>ErewhonWebService</c>
/// exposed through the document/literal list <c>WebServiceHTTP</c>
/// (Erewhon/ErehwhonExternalInterfacesSchema.scm:6526-6942), each method body ported onto the in-memory
/// model, plus the WSDL. Its output becomes the equivalence baseline, so no label or extra header is added
/// to a successful response; only the stand-in's own Faults name it.
/// </summary>
/// <remarks>
/// <para>
/// <c>POST</c> on the catalogue's SOAP path, with any query string, is resolved to its operation by the
/// shared <see cref="SoapOperationResolver"/> (<c>SOAPAction</c> first, then the first <c>Body</c> child),
/// and the whole buffered body is then parsed for the wrapped request element and its parameters. Each
/// handler runs under <see cref="StandInState.Mutate{T}"/> exactly when the operation is
/// <see cref="OperationDescriptor.StateChanging"/>, and under <see cref="StandInState.Read{T}"/>
/// otherwise, building its DTO tree inside the lock; <see cref="SoapEnvelopeWriter"/> serialises it after
/// the lock is released. Business errors travel as a <c>DTOResult</c> with HTTP 200, never as a Fault.
/// </para>
/// <para>
/// HTTP 500 Faults are reserved for requests the stand-in cannot model: an unresolved operation, malformed
/// XML, a missing request element or DTO parameter (<c>soap:Client</c>), and the empty state with no
/// company (<c>soap:Server</c>). An unexpected exception is a stand-in defect and is not caught, so the
/// framework's 500 surfaces it during capture.
/// </para>
/// <para>
/// Every endpoint, the catch-all included, is dispatched only for requests arriving on the SOAP socket
/// (<see cref="ListenerKind.Soap"/>), whatever <c>Host</c> header they carry. No caller header is read
/// except through the resolver, nothing is authenticated, and no payload is logged.
/// </para>
/// </remarks>
public static class SoapEndpoint
{
    // A plain Map catch-all: the framework's fallback mapping adds a nonfile constraint that skips
    // dotted paths such as /company.json.
    private const string CatchAllPattern = "/{**path}";

    /// <summary>
    /// Maps the SOAP <c>POST</c>, the WSDL <c>GET</c> and the SOAP listener's catch-all, each restricted to
    /// <see cref="ListenerKind.Soap"/>. The catalogue, resolver and state come from
    /// <see cref="IEndpointRouteBuilder.ServiceProvider"/>; the DTO builder is the registered one when
    /// there is one, otherwise <see cref="DtoBuilder.Default"/>.
    /// </summary>
    /// <param name="endpoints">The stand-in's endpoint route builder.</param>
    /// <exception cref="ArgumentNullException"><paramref name="endpoints"/> is null.</exception>
    /// <exception cref="InvalidOperationException">
    /// A required service is not registered, the catalogue's SOAP namespaces differ from those
    /// <see cref="SoapEnvelopeWriter"/> writes, it lacks a status constant the handlers assign, or the
    /// handler table does not port exactly the catalogue's SOAP operations with their declared signatures;
    /// the message names the entry.
    /// </exception>
    public static void Map(IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        IServiceProvider services = endpoints.ServiceProvider;
        var service = new SoapService(
            services.GetRequiredService<OperationCatalogue>(),
            services.GetRequiredService<SoapOperationResolver>(),
            services.GetRequiredService<StandInState>(),
            services.GetService<DtoBuilder>() ?? DtoBuilder.Default);

        RequestDelegate post = service.HandlePostAsync;
        RequestDelegate get = service.HandleGetAsync;
        RequestDelegate unmatched = WriteNotFoundAsync;

        endpoints.MapPost(service.EndpointPath, post).RequireListener(ListenerKind.Soap);
        endpoints.MapGet(service.EndpointPath, get).RequireListener(ListenerKind.Soap);

        // Every other method or path on the SOAP socket, REST paths sent to 21001 included.
        endpoints.Map(CatchAllPattern, unmatched)
            .WithOrder(int.MaxValue)
            .RequireListener(ListenerKind.Soap);
    }

    // 404 with no body and no Content-Type.
    private static Task WriteNotFoundAsync(HttpContext context)
    {
        context.Response.StatusCode = StatusCodes.Status404NotFound;
        return Task.CompletedTask;
    }

    // The one writer of every SOAP and WSDL body: status, Content-Type and length, then the bytes. No other
    // header is set; Program.cs disables the Server header.
    private static async Task WriteXmlAsync(HttpContext context, int status, byte[] body)
    {
        HttpResponse response = context.Response;
        response.StatusCode = status;
        response.ContentType = SoapEnvelopeWriter.ContentType;
        response.ContentLength = body.Length;
        await response.Body.WriteAsync(body, context.RequestAborted).ConfigureAwait(false);
    }

    private static Task WriteFaultAsync(HttpContext context, string faultCode, string faultString) =>
        WriteXmlAsync(context, StatusCodes.Status500InternalServerError, SoapEnvelopeWriter.WriteFault(faultCode, faultString));

    /// <summary>What a handler produced under the state lock: its result, or no company to run against.</summary>
    /// <param name="Result">A <see cref="DtoNode"/>, a <see cref="DtoArray"/> or null, which is written as <c>xsi:nil</c>.</param>
    /// <param name="CompanyMissing">True in the empty state, where every body's <c>app.myCompany</c> is null.</param>
    private readonly record struct SoapOutcome(object? Result, bool CompanyMissing)
    {
        public static SoapOutcome NoCompany => new(null, true);
    }

    private delegate object? ReadHandler(Company company, SoapArguments arguments);

    private delegate object? WriteHandler(Company company, MutationContext context, SoapArguments arguments);

    /// <summary>
    /// The address values one update applies, in the order the JADE bodies assign them, read from the
    /// eight <c>String</c> parameters of <c>updateAgent</c>/<c>updateClient</c> or from the
    /// <c>DTOAgent</c>/<c>DTOClient</c> of the <c>WithDTO</c> forms; <see cref="Name"/> is the lookup key.
    /// </summary>
    private sealed record ContactValues(
        string Name,
        string Street,
        string City,
        string Country,
        string Phone,
        string Fax,
        string Email,
        string Website);

    /// <summary>The request pipeline and the 9 ported method bodies; immutable after construction.</summary>
    private sealed class SoapService
    {
        private const string OperationNotRecognised = $"{SoapEnvelopeWriter.FaultStringPrefix} operation not recognised";
        private const string MalformedRequest = $"{SoapEnvelopeWriter.FaultStringPrefix} malformed request XML";
        private const string RequestElementNotFound = $"{SoapEnvelopeWriter.FaultStringPrefix} request element not found";
        private const string NoCompanyLoaded = $"{SoapEnvelopeWriter.FaultStringPrefix} no company loaded";

        private static readonly XName EnvelopeName = XName.Get("Envelope", SoapEnvelopeWriter.SoapEnvelopeNamespace);
        private static readonly XName BodyName = XName.Get("Body", SoapEnvelopeWriter.SoapEnvelopeNamespace);
        private static readonly XName NilName = XName.Get("nil", SoapEnvelopeWriter.XsiNamespace);

        // The XML white space characters an xsd:boolean value may carry around its literal.
        private static readonly char[] XmlWhitespace = [' ', '\t', '\n', '\r'];

        // The whole body is parsed; the reader resolves nothing external and expands no DTD. Kestrel forbids
        // synchronous reads of a request body, so the reader runs in async mode. Whitespace is reported, so
        // a parameter's text reaches the TA exactly as sent; the TAs do their own trimming.
        private static readonly XmlReaderSettings RequestSettings = new()
        {
            Async = true,
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            IgnoreComments = true,
        };

        private readonly OperationCatalogue _catalogue;
        private readonly SoapOperationResolver _resolver;
        private readonly StandInState _state;
        private readonly DtoBuilder _dto;
        private readonly SoapStatuses _status;
        private readonly FrozenDictionary<string, OperationBinding> _bindings;

        public SoapService(OperationCatalogue catalogue, SoapOperationResolver resolver, StandInState state, DtoBuilder dto)
        {
            _catalogue = catalogue;
            _resolver = resolver;
            _state = state;
            _dto = dto;

            SoapAddress address = catalogue.Addresses.Soap;
            RequireNamespace("addresses.soap.envelopeNamespace", address.EnvelopeNamespace, SoapEnvelopeWriter.SoapEnvelopeNamespace);
            RequireNamespace("addresses.soap.targetNamespace", address.TargetNamespace, SoapEnvelopeWriter.ServiceNamespace);
            EndpointPath = address.Path;

            _status = SoapStatuses.Resolve(catalogue);
            _bindings = Bind(catalogue, CreateHandlers());
        }

        /// <summary>The SOAP endpoint path, <c>/jade/jadehttp.dll</c>; its query string is never validated.</summary>
        public string EndpointPath { get; }

        // GET on the endpoint: the WSDL when the query has a wsdl key, otherwise 404 with no body.
        public Task HandleGetAsync(HttpContext context)
        {
            if (!SoapOperationResolver.IsWsdlRequest(context.Request))
            {
                return WriteNotFoundAsync(context);
            }

            string location = WsdlGenerator.BuildAddressLocation(context.Request, _catalogue);
            return WriteXmlAsync(context, StatusCodes.Status200OK, WsdlGenerator.Generate(_catalogue, location));
        }

        public async Task HandlePostAsync(HttpContext context)
        {
            HttpRequest request = context.Request;
            CancellationToken aborted = context.RequestAborted;

            // No buffer limit: any body Kestrel accepts is parsed in full.
            request.EnableBuffering();
            SoapResolution resolution = await _resolver.ResolveAsync(request, aborted).ConfigureAwait(false);
            if (resolution.Operation is not { } operation || !_bindings.TryGetValue(operation.JadeMethod, out OperationBinding? binding))
            {
                await WriteFaultAsync(context, SoapEnvelopeWriter.FaultCodeClient, OperationNotRecognised).ConfigureAwait(false);
                return;
            }

            request.Body.Position = 0;
            XDocument document;
            try
            {
                using XmlReader reader = XmlReader.Create(request.Body, RequestSettings);
                document = await XDocument.LoadAsync(reader, LoadOptions.None, aborted).ConfigureAwait(false);
            }
            catch (XmlException)
            {
                await WriteFaultAsync(context, SoapEnvelopeWriter.FaultCodeClient, MalformedRequest).ConfigureAwait(false);
                return;
            }

            if (FindRequestElement(document, binding.Soap.RequestElement) is not { } wrapper)
            {
                await WriteFaultAsync(context, SoapEnvelopeWriter.FaultCodeClient, RequestElementNotFound).ConfigureAwait(false);
                return;
            }

            if (!TryReadArguments(wrapper, binding, out SoapArguments? arguments, out string? fault))
            {
                await WriteFaultAsync(context, SoapEnvelopeWriter.FaultCodeClient, fault).ConfigureAwait(false);
                return;
            }

            SoapOutcome outcome = Execute(binding, arguments);
            if (outcome.CompanyMissing)
            {
                await WriteFaultAsync(context, SoapEnvelopeWriter.FaultCodeServer, NoCompanyLoaded).ConfigureAwait(false);
                return;
            }

            byte[] body = SoapEnvelopeWriter.WriteResponse(operation, outcome.Result);
            await WriteXmlAsync(context, StatusCodes.Status200OK, body).ConfigureAwait(false);
        }

        // Each of the 9 bodies dereferences app.myCompany before producing any result, so the empty state is
        // answered before a handler runs; a Mutate in the empty state still advances the clock, as
        // StandInState defines.
        private SoapOutcome Execute(OperationBinding binding, SoapArguments arguments)
        {
            // Bind checked that a state-changing operation has a write handler and any other a read handler.
            OperationHandler handler = binding.Handler;
            return binding.Operation.StateChanging
                ? _state.Mutate((company, context) =>
                    company is null ? SoapOutcome.NoCompany : new SoapOutcome(handler.Write!(company, context, arguments), false))
                : _state.Read(company =>
                    company is null ? SoapOutcome.NoCompany : new SoapOutcome(handler.Read!(company, arguments), false));
        }

        private FrozenDictionary<string, OperationHandler> CreateHandlers() =>
            new Dictionary<string, OperationHandler>(StringComparer.Ordinal)
            {
                ["getAgent"] = OperationHandler.ForRead(Signatures.Name, GetAgent),
                ["getAgentNames"] = OperationHandler.ForRead([], GetAgentNames),
                ["getClient"] = OperationHandler.ForRead(Signatures.Name, GetClient),
                ["getClientNames"] = OperationHandler.ForRead([], GetClientNames),
                ["getPhoto"] = OperationHandler.ForRead(Signatures.Name, GetPhoto),
                ["updateAgent"] = OperationHandler.ForWrite(Signatures.Contact, UpdateAgent),
                ["updateAgentWithDTO"] = OperationHandler.ForWrite(Signatures.AgentDto, UpdateAgentWithDto),
                ["updateClient"] = OperationHandler.ForWrite(Signatures.Contact, UpdateClient),
                ["updateClientWithDTO"] = OperationHandler.ForWrite(Signatures.ClientDto, UpdateClientWithDto),
            }.ToFrozenDictionary(StringComparer.Ordinal);

        // getAgent (scm:6528-6557): exact, untrimmed name lookup; not found is a nil result.
        private object? GetAgent(Company company, SoapArguments arguments) =>
            company.AllAgents[arguments.Text(0)] is { } agent ? _dto.Agent(agent) : null;

        // getAgentNames (scm:6558-6586): one DTOName per agent in allAgents (name) order.
        private object? GetAgentNames(Company company, SoapArguments arguments) =>
            _dto.NameArray(company.AllAgents.Select(agent => agent.Name));

        // getClient (scm:6587-6616): the name is trimmed of blanks before the lookup (scm:6605).
        private object? GetClient(Company company, SoapArguments arguments) =>
            company.AllClients[JadeValues.TrimBlanks(arguments.Text(0))] is { } client ? _dto.Client(client) : null;

        // getClientNames (scm:6617-6645): one DTOName per client in allClients (name) order.
        private object? GetClientNames(Company company, SoapArguments arguments) =>
            _dto.NameArray(company.AllClients.Select(client => client.Name));

        // getPhoto (scm:6646-6681): exact, untrimmed lookup (scm:6665). The loop assigns every photo it
        // meets, so the photo of the LAST retail sale in allRetailSales (dateSold, prefix, code) order wins;
        // the code is followed over the method header's "first available photo". JADE's null Binary is
        // empty, so an empty photo is skipped like a missing one, and no photo leaves photo nil.
        private object? GetPhoto(Company company, SoapArguments arguments)
        {
            if (company.AllClients[arguments.Text(0)] is not { } client)
            {
                return null;
            }

            byte[]? photo = null;
            foreach (RetailSale sale in client.AllRetailSales)
            {
                Item item = sale.MyItem
                    ?? throw new InvalidOperationException($"Retail sale (oid {sale.Oid}) of client {client.Name} has no item.");
                if (item.Photo is { Length: > 0 } bytes)
                {
                    photo = bytes;
                }
            }

            return _dto.Photo(photo);
        }

        // updateAgent (scm:6682-6753): the eight String parameters in signature order.
        private object? UpdateAgent(Company company, MutationContext context, SoapArguments arguments) =>
            UpdateAgent(company, context, arguments.ContactFromTexts());

        // updateAgentWithDTO (scm:6754-6811): the same body, keyed by pDTOAgent.name.
        private object? UpdateAgentWithDto(Company company, MutationContext context, SoapArguments arguments) =>
            UpdateAgent(company, context, arguments.Contact(0));

        // updateClient (scm:6812-6885): exact, untrimmed lookup (scm:6848).
        private object? UpdateClient(Company company, MutationContext context, SoapArguments arguments) =>
            UpdateClient(company, context, arguments.ContactFromTexts());

        // updateClientWithDTO (scm:6886-6942): the same body, keyed by pDTOClient.name.
        private object? UpdateClientWithDto(Company company, MutationContext context, SoapArguments arguments) =>
            UpdateClient(company, context, arguments.Contact(0));

        // The shared agent update: not found is 404 with "Agent <name> not found" and no change; otherwise
        // AgentTA populated from the agent, the seven address values assigned, then persistEntity. A failed
        // persist changes nothing and answers 400 with the TA errors in order; success answers 200 with the
        // agent's guid, which the SOAP surface never emits. allWarnings stays empty: JADE copies errors only.
        private DtoNode UpdateAgent(Company company, MutationContext context, ContactValues values)
        {
            if (company.AllAgents[values.Name] is not { } agent)
            {
                return _dto.Result(_status.NotFound, [Messages.AgentNameNotFound(values.Name)]);
            }

            var agentTA = new AgentTA();
            agentTA.PopulateFromObject(agent);
            AssignAddress(agentTA, values);

            if (!agentTA.PersistEntity(TransactionType.Persist, context.Model))
            {
                return _dto.Result(_status.BadRequest, agentTA.AllErrors);
            }

            Agent updated = agentTA.GetModelObject()
                ?? throw new InvalidOperationException("AgentTA reported a successful update but holds no agent.");
            return _dto.Result(_status.Success, guid: updated.Guid);
        }

        // The shared client update, as UpdateAgent with ClientTA and "Client <name> not found".
        private DtoNode UpdateClient(Company company, MutationContext context, ContactValues values)
        {
            if (company.AllClients[values.Name] is not { } client)
            {
                return _dto.Result(_status.NotFound, [Messages.ClientNameNotFound(values.Name)]);
            }

            var clientTA = new ClientTA();
            clientTA.PopulateFromObject(client);
            AssignAddress(clientTA, values);

            if (!clientTA.PersistEntity(TransactionType.Persist, context.Model))
            {
                return _dto.Result(_status.BadRequest, clientTA.AllErrors);
            }

            Client updated = clientTA.GetModelObject()
                ?? throw new InvalidOperationException("ClientTA reported a successful update but holds no client.");
            return _dto.Result(_status.Success, guid: updated.Guid);
        }

        // The myAddressTA assignments of the four update bodies, in source order; the DTO member and
        // parameter are "website", the address attribute "webSite".
        private static void AssignAddress(AddressOwnerTA owner, ContactValues values)
        {
            AddressTA address = owner.MyAddressTA
                ?? throw new InvalidOperationException($"{owner.GetType().Name} was populated without an address agent.");
            address.Street = values.Street;
            address.City = values.City;
            address.Country = values.Country;
            address.Phone = values.Phone;
            address.Fax = values.Fax;
            address.Email = values.Email;
            address.WebSite = values.Website;
        }

        // soap:Envelope/soap:Body, then the first Body child that is the operation's request element.
        private static XElement? FindRequestElement(XDocument document, string requestElement) =>
            document.Root is { } root && root.Name == EnvelopeName && root.Element(BodyName) is { } body
                ? FindChild(body, requestElement)
                : null;

        // The parameters in signature order. A missing or nil String parameter is the JADE null string, any
        // other its full text unaltered. A DTO parameter is read into its contact members by the same rule;
        // a missing or nil DTO is not a characterised request and answers a Fault. guid and any unknown
        // child are ignored.
        private static bool TryReadArguments(
            XElement wrapper,
            OperationBinding binding,
            [NotNullWhen(true)] out SoapArguments? arguments,
            [NotNullWhen(false)] out string? fault)
        {
            IReadOnlyList<ParameterDescriptor> parameters = binding.Parameters;
            var values = new object[parameters.Count];
            for (int index = 0; index < parameters.Count; index++)
            {
                ParameterDescriptor parameter = parameters[index];
                XElement? element = FindChild(wrapper, parameter.Name);
                if (string.Equals(parameter.JadeType, JadeTypes.String, StringComparison.Ordinal))
                {
                    values[index] = TextOf(element);
                    continue;
                }

                // Bind admitted no other parameter type than a DTO carrying every contact member.
                if (element is null || IsNil(element))
                {
                    arguments = null;
                    fault = $"{SoapEnvelopeWriter.FaultStringPrefix} {parameter.Name} missing";
                    return false;
                }

                values[index] = new ContactValues(
                    TextOf(FindChild(element, ContactMembers.Name)),
                    TextOf(FindChild(element, ContactMembers.Street)),
                    TextOf(FindChild(element, ContactMembers.City)),
                    TextOf(FindChild(element, ContactMembers.Country)),
                    TextOf(FindChild(element, ContactMembers.Phone)),
                    TextOf(FindChild(element, ContactMembers.Fax)),
                    TextOf(FindChild(element, ContactMembers.Email)),
                    TextOf(FindChild(element, ContactMembers.Website)));
            }

            arguments = new SoapArguments(values);
            fault = null;
            return true;
        }

        // A message element: the local name compared ordinally, in the service namespace or unqualified.
        private static XElement? FindChild(XElement parent, string localName) =>
            parent.Elements().FirstOrDefault(child =>
                string.Equals(child.Name.LocalName, localName, StringComparison.Ordinal)
                && (child.Name.NamespaceName.Length == 0
                    || string.Equals(child.Name.NamespaceName, SoapEnvelopeWriter.ServiceNamespace, StringComparison.Ordinal)));

        private static string TextOf(XElement? element) =>
            element is null || IsNil(element) ? "" : element.Value;

        // xsi:nil is an xsd:boolean, whose true forms are "true" and "1" after whitespace collapsing.
        private static bool IsNil(XElement element) =>
            element.Attribute(NilName)?.Value.Trim(XmlWhitespace) is "true" or "1";

        private static void RequireNamespace(string key, string actual, string expected)
        {
            if (!string.Equals(actual, expected, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"The operation catalogue's {key} is '{actual}', but the stand-in's SOAP surface reads and writes '{expected}'.");
            }
        }

        // Pairs every catalogue SOAP operation with its handler, failing start-up unless the handler table
        // ports exactly those operations, each with the catalogue's kind and signature.
        private static FrozenDictionary<string, OperationBinding> Bind(
            OperationCatalogue catalogue,
            FrozenDictionary<string, OperationHandler> handlers)
        {
            IReadOnlyList<OperationDescriptor> operations = catalogue.GetBySurface(Surface.Soap);
            string[] missing = [.. operations.Select(operation => operation.JadeMethod).Where(method => !handlers.ContainsKey(method))];
            string[] extra =
            [
                .. handlers.Keys
                    .Where(method => !operations.Any(operation => string.Equals(operation.JadeMethod, method, StringComparison.Ordinal)))
                    .Order(StringComparer.Ordinal),
            ];
            if (missing.Length > 0 || extra.Length > 0)
            {
                throw new InvalidOperationException(
                    "The stand-in SOAP handler table does not match the catalogue's SOAP operations: " +
                    $"missing [{string.Join(", ", missing)}], extra [{string.Join(", ", extra)}].");
            }

            return operations.ToFrozenDictionary(
                operation => operation.JadeMethod,
                operation => CreateBinding(catalogue, operation, handlers[operation.JadeMethod]),
                StringComparer.Ordinal);
        }

        private static OperationBinding CreateBinding(OperationCatalogue catalogue, OperationDescriptor operation, OperationHandler handler)
        {
            SoapBinding soap = operation.Soap ?? throw Inconsistent(operation, "has no SOAP binding");
            if (handler.StateChanging != operation.StateChanging)
            {
                throw Inconsistent(
                    operation,
                    $"is {(operation.StateChanging ? "" : "not ")}state-changing, but its handler {(handler.StateChanging ? "writes" : "only reads")}");
            }

            ParameterDescriptor[] parameters = [.. operation.Parameters.OrderBy(parameter => parameter.Position)];
            (string Name, string JadeType)[] declared = [.. parameters.Select(parameter => (parameter.Name, parameter.JadeType))];
            if (!declared.SequenceEqual(handler.Signature))
            {
                throw Inconsistent(operation, $"declares ({Describe(declared)}), but its handler ports ({Describe(handler.Signature)})");
            }

            foreach (ParameterDescriptor parameter in parameters)
            {
                if (parameter.Location != ParameterLocation.Element)
                {
                    throw Inconsistent(
                        operation, $"parameter '{parameter.Name}' is a {parameter.Location} parameter, which the SOAP stand-in cannot read");
                }

                if (string.Equals(parameter.JadeType, JadeTypes.String, StringComparison.Ordinal))
                {
                    continue;
                }

                if (!catalogue.TryGetDto(parameter.JadeType, out DtoDefinition? dto))
                {
                    throw Inconsistent(operation, $"parameter '{parameter.Name}' has type '{parameter.JadeType}', which is no catalogue DTO");
                }

                foreach (string member in ContactMembers.All)
                {
                    if (!dto.TryGetProperty(member, out DtoProperty? property)
                        || !property.SoapExposed
                        || !string.Equals(property.JadeType, JadeTypes.String, StringComparison.Ordinal))
                    {
                        throw Inconsistent(
                            operation, $"parameter DTO '{dto.Name}' exposes no String property '{member}' on SOAP, which its handler reads");
                    }
                }
            }

            return new OperationBinding(operation, soap, handler, parameters);
        }

        private static string Describe(IEnumerable<(string Name, string JadeType)> signature) =>
            string.Join(", ", signature.Select(parameter => $"{parameter.Name}: {parameter.JadeType}"));

        private static InvalidOperationException Inconsistent(OperationDescriptor operation, string problem) =>
            new($"SOAP operation {operation.Id} {problem}.");
    }

    /// <summary>The SOAP status constants the bodies assign to <c>DTOResult.statusCode</c>, resolved by name from the catalogue.</summary>
    private sealed record SoapStatuses(int Success, int NotFound, int BadRequest)
    {
        public static SoapStatuses Resolve(OperationCatalogue catalogue)
        {
            IReadOnlyList<StatusConstant> constants = catalogue.GetConstants(Surface.Soap);

            int Value(string name) =>
                constants.FirstOrDefault(constant => string.Equals(constant.Name, name, StringComparison.Ordinal))?.Value
                ?? throw new InvalidOperationException(
                    $"The operation catalogue declares no SOAP status constant '{name}', which the stand-in's SOAP handlers assign.");

            // scm:1139-1146. No public method assigns the other five, so they are never emitted.
            return new SoapStatuses(Value("Status_Success"), Value("Error_Not_Found"), Value("Error_Bad_Request"));
        }
    }

    /// <summary>One ported method body: its JADE signature and its read or write delegate.</summary>
    private sealed class OperationHandler
    {
        private OperationHandler(IReadOnlyList<(string Name, string JadeType)> signature, ReadHandler? read, WriteHandler? write)
        {
            Signature = signature;
            Read = read;
            Write = write;
        }

        /// <summary>The parameter names and JADE types in signature order, as the ported method declares them.</summary>
        public IReadOnlyList<(string Name, string JadeType)> Signature { get; }

        public ReadHandler? Read { get; }

        public WriteHandler? Write { get; }

        public bool StateChanging => Write is not null;

        public static OperationHandler ForRead((string Name, string JadeType)[] signature, ReadHandler read) => new(signature, read, null);

        public static OperationHandler ForWrite((string Name, string JadeType)[] signature, WriteHandler write) => new(signature, null, write);
    }

    /// <summary>A catalogue SOAP operation with its message binding, its handler and its parameters in signature order.</summary>
    private sealed record OperationBinding(
        OperationDescriptor Operation,
        SoapBinding Soap,
        OperationHandler Handler,
        IReadOnlyList<ParameterDescriptor> Parameters);

    /// <summary>The read arguments of one request, by place in signature order.</summary>
    private sealed class SoapArguments(object[] values)
    {
        public string Text(int index) => (string)values[index];

        public ContactValues Contact(int index) => (ContactValues)values[index];

        /// <summary>The eight String parameters of <c>updateAgent</c>/<c>updateClient</c>, in the order <see cref="Signatures.Contact"/> fixes.</summary>
        public ContactValues ContactFromTexts() =>
            new(Text(0), Text(1), Text(2), Text(3), Text(4), Text(5), Text(6), Text(7));
    }

    /// <summary>The JADE types the ported signatures declare.</summary>
    private static class JadeTypes
    {
        public const string String = "String";
        public const string DtoAgent = "DTOAgent";
        public const string DtoClient = "DTOClient";
    }

    /// <summary>The signatures of the ported methods (scm:1148-1307), checked against the catalogue at start-up.</summary>
    private static class Signatures
    {
        public static readonly (string Name, string JadeType)[] Name = [("pName", JadeTypes.String)];

        // Matches the ContactValues order: name, then the seven address values as the bodies assign them.
        public static readonly (string Name, string JadeType)[] Contact =
        [
            ("pName", JadeTypes.String),
            ("pStreet", JadeTypes.String),
            ("pCity", JadeTypes.String),
            ("pCountry", JadeTypes.String),
            ("pPhone", JadeTypes.String),
            ("pFax", JadeTypes.String),
            ("pEmail", JadeTypes.String),
            ("pWebsite", JadeTypes.String),
        ];

        public static readonly (string Name, string JadeType)[] AgentDto = [("pDTOAgent", JadeTypes.DtoAgent)];

        public static readonly (string Name, string JadeType)[] ClientDto = [("pDTOClient", JadeTypes.DtoClient)];
    }

    /// <summary>
    /// The <c>DTOAgent</c>/<c>DTOClient</c> members the <c>WithDTO</c> bodies read (scm:6775-6796, 6907-6928),
    /// as the reference schema declares them.
    /// </summary>
    private static class ContactMembers
    {
        public const string Name = "name";
        public const string Street = "street";
        public const string City = "city";
        public const string Country = "country";
        public const string Phone = "phone";
        public const string Fax = "fax";
        public const string Email = "email";
        public const string Website = "website";

        public static readonly string[] All = [Name, Street, City, Country, Phone, Fax, Email, Website];
    }
}
