using System.Collections.Frozen;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text.Json;
using System.Text.Unicode;
using Erewhon.Contracts.Catalogue;
using Erewhon.Contracts.Hosting;
using Erewhon.Contracts.Matching;
using Erewhon.StandIn.Dto;
using Erewhon.StandIn.Model;
using Erewhon.StandIn.State;
using Microsoft.AspNetCore.Http.Features;

namespace Erewhon.StandIn.Rest;

/// <summary>
/// The REST listener of the JADE stand-in: the 13 <c>ErewhonRestService</c> operations of the reference
/// schema (Erewhon/ErehwhonExternalInterfacesSchema.scm:2326-2868), each method body ported onto the
/// in-memory model. Its output becomes the equivalence baseline, so it is never presented as JADE output
/// and no label or extra header is added to a response.
/// </summary>
/// <remarks>
/// <para>
/// One catch-all endpoint on the REST listener receives every request. The shared
/// <see cref="RestOperationMatcher"/> decides which catalogue operation the raw request target is, so no
/// verb, path or format suffix is restated here; anything unmatched answers 404 with no body. The
/// <c>.xml</c> and <c>.jsonn</c> output formats are not modelled (KL-REST-FORMAT) and answer 501 before any
/// parsing or model access; every other response is JSON, whatever <c>Accept</c> says.
/// </para>
/// <para>
/// A state-changing request is counted once in <see cref="StandInState.Mutations"/>, which moves the
/// clock, however it is answered: by <see cref="StandInState.Mutate{T}"/> when its handler runs, and by
/// <see cref="StandInState.CountRejected"/> when it is answered without it, with 501 for a suffix, 400
/// with <c>null</c> for a value that does not convert, or 500 for a failure before the handler. A read is
/// never counted, nor is a state-changing request that its client abandons, or Kestrel refuses, before
/// its handler runs.
/// </para>
/// <para>
/// Path parameters are converted by their catalogue JADE types and the <c>DTOAgent</c>/<c>DTOClient</c>
/// body is read as JSON before the state lock is taken; a value that does not convert answers 400 with
/// <c>null</c>. Each handler then runs under <see cref="StandInState.Mutate{T}"/> exactly when the
/// operation is <see cref="OperationDescriptor.StateChanging"/>, and under
/// <see cref="StandInState.Read{T}"/> otherwise, building its DTO tree inside the lock; the tree is
/// serialised by <see cref="JadeJsonWriter"/> after the lock is released. Status codes are the catalogue's
/// <c>HTTP_*</c> constants. No clock, GUID or randomness is read here: the state owns both.
/// </para>
/// </remarks>
public static class RestEndpoints
{
    private const string LoggerCategory = "Erewhon.StandIn.Rest";

    /// <summary>
    /// Registers the REST catch-all on the REST listener. The matcher, catalogue and state come from
    /// <see cref="IEndpointRouteBuilder.ServiceProvider"/>; the DTO builder is the registered one when
    /// there is one, otherwise <see cref="DtoBuilder.Default"/>.
    /// </summary>
    /// <remarks>
    /// <c>Map("/{**path}")</c> is used rather than <c>MapFallback</c>, whose <c>nonfile</c> constraint
    /// skips dotted paths such as <c>/company.json</c>.
    /// </remarks>
    /// <param name="endpoints">The application's endpoint route builder.</param>
    /// <exception cref="ArgumentNullException"><paramref name="endpoints"/> is null.</exception>
    /// <exception cref="InvalidOperationException">
    /// A required service is not registered, the catalogue lacks a status constant or the JSON format
    /// suffix the handlers use, or the handler table does not port exactly the catalogue's REST
    /// operations with their declared signatures; the message names the entry.
    /// </exception>
    public static void Map(IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        IServiceProvider services = endpoints.ServiceProvider;
        var service = new RestService(
            services.GetRequiredService<OperationCatalogue>(),
            services.GetRequiredService<RestOperationMatcher>(),
            services.GetRequiredService<StandInState>(),
            services.GetService<DtoBuilder>() ?? DtoBuilder.Default,
            services.GetRequiredService<ILoggerFactory>().CreateLogger(LoggerCategory));

        RequestDelegate handler = service.HandleAsync;
        endpoints.Map("/{**path}", handler)
            .WithOrder(int.MaxValue)
            .RequireListener(ListenerKind.Rest);
    }

    /// <summary>The status a handler sets and the DTO tree it returns: a <see cref="DtoNode"/>, a <see cref="DtoArray"/> or null.</summary>
    private readonly record struct RestOutcome(int Status, object? Body);

    private delegate RestOutcome ReadHandler(Company? company, RestArguments arguments);

    private delegate RestOutcome WriteHandler(Company? company, MutationContext context, RestArguments arguments);

    /// <summary>The request pipeline and the 13 ported method bodies; immutable after construction.</summary>
    private sealed class RestService
    {
        // codePrefix.length() > 4 in getItemByCode and getSale (scm:2497, 2613).
        private const int MaxCodePrefixLength = 4;

        private const string PlainTextContentType = "text/plain; charset=utf-8";

        private static readonly byte[] FormatNotModelledBody = "jade-standin: output format not modelled"u8.ToArray();

        // U+FEFF in UTF-8; a JSON body may start with one.
        private static ReadOnlySpan<byte> Utf8ByteOrderMark => [0xEF, 0xBB, 0xBF];

        private readonly RestOperationMatcher _matcher;
        private readonly StandInState _state;
        private readonly DtoBuilder _dto;
        private readonly ILogger _logger;
        private readonly RestStatuses _status;
        private readonly string _jsonSuffix;
        private readonly FrozenDictionary<string, OperationBinding> _bindings;

        public RestService(
            OperationCatalogue catalogue,
            RestOperationMatcher matcher,
            StandInState state,
            DtoBuilder dto,
            ILogger logger)
        {
            _matcher = matcher;
            _state = state;
            _dto = dto;
            _logger = logger;
            _status = RestStatuses.Resolve(catalogue);
            _jsonSuffix = ResolveJsonSuffix(catalogue.Addresses.Rest);
            _bindings = Bind(catalogue, CreateHandlers());
        }

        public async Task HandleAsync(HttpContext context)
        {
            if (!TryGetRawPath(context, out var rawPath)
                || !_matcher.TryMatch(context.Request.Method, rawPath, out var match))
            {
                // Unmatched targets, verbs and request-target forms: 404 with no body and no Content-Type.
                context.Response.StatusCode = _status.NotFound;
                return;
            }

            OperationDescriptor operation = match.Operation;
            RequestLogEntry log = RequestLogEntry.For(context);
            log.Operation = operation.Id;
            CancellationToken aborted = context.RequestAborted;

            // True once the request is in the mutation count, or its handler is reached and Mutate counts
            // it, so that a state-changing request is counted exactly once however it is answered.
            bool accounted = false;
            try
            {
                OperationBinding binding = _bindings[operation.JadeMethod];

                // Before any parsing or model access, so a suffixed write changes no business state; it is
                // still counted, and moves the clock, as a state-changing request answered without its handler.
                if (!IsJsonFormat(match.FormatSuffix))
                {
                    accounted = true;
                    CountRejected(operation);
                    await WriteFormatNotModelledAsync(context.Response, aborted).ConfigureAwait(false);
                    return;
                }

                RestArguments? arguments = await ReadArgumentsAsync(context.Request, binding, match, aborted).ConfigureAwait(false);
                if (arguments is null)
                {
                    CountRejected(operation);
                }

                // Counted just above, or by Mutate as Execute enters it.
                accounted = true;
                RestOutcome outcome = arguments is null ? new RestOutcome(_status.BadRequest, null) : Execute(binding, arguments);

                await JadeJsonWriter.WriteAsync(context.Response, outcome.Status, outcome.Body, operation.ReturnType, aborted)
                    .ConfigureAwait(false);
            }
            catch (Exception exception) when (aborted.IsCancellationRequested && exception is OperationCanceledException or IOException)
            {
                log.Outcome = RequestOutcome.Aborted;
                _logger.LogDebug("REST {OperationId}: the client aborted the request.", operation.Id);
            }
            catch (BadHttpRequestException exception)
            {
                // Kestrel rejected the request body (413 over the size limit, 400 malformed framing, 408 too
                // slow). That is the client's error: its status, with no body and no Content-Type, as Kestrel
                // answers the rejections it makes itself.
                _logger.LogDebug(
                    exception,
                    "REST {OperationId}: the request body was rejected; answering {Status} with no body.",
                    operation.Id,
                    exception.StatusCode);
                if (context.Response.HasStarted)
                {
                    // Part of a response is already on the wire; aborting keeps it from passing as complete.
                    context.Abort();
                }
                else
                {
                    context.Response.Clear();
                    context.Response.StatusCode = exception.StatusCode;
                }
            }
            catch (Exception exception)
            {
                // A failure before the handler still answers the request, so it is counted; a request
                // Kestrel refused while its body was read is answered above and is not.
                if (!accounted)
                {
                    CountRejected(operation);
                }

                log.Outcome = RequestOutcome.Failed;
                _logger.LogError(
                    exception,
                    "REST {OperationId} failed unexpectedly; answering {Status} with null.",
                    operation.Id,
                    _status.ServerError);
                await WriteServerErrorAsync(context, operation).ConfigureAwait(false);
            }
        }

        private RestOutcome Execute(OperationBinding binding, RestArguments arguments)
        {
            // Bind checked that a state-changing operation has a write handler and any other a read handler.
            OperationHandler handler = binding.Handler;
            return binding.Operation.StateChanging
                ? _state.Mutate((company, context) => handler.Write!(company, context, arguments))
                : _state.Read(company => handler.Read!(company, arguments));
        }

        // A request answered without its handler: counted, as Mutate would have counted it, exactly when
        // the operation is state-changing.
        private void CountRejected(OperationDescriptor operation)
        {
            if (operation.StateChanging)
            {
                _state.CountRejected();
            }
        }

        private FrozenDictionary<string, OperationHandler> CreateHandlers() =>
            new Dictionary<string, OperationHandler>(StringComparer.Ordinal)
            {
                ["getAgent"] = OperationHandler.ForRead([JadeTypes.String], GetAgent),
                ["getAllAgents"] = OperationHandler.ForRead([], GetAllAgents),
                ["getAllClients"] = OperationHandler.ForRead([], GetAllClients),
                ["getAllSales"] = OperationHandler.ForRead([], GetAllSales),
                ["getClient"] = OperationHandler.ForRead([JadeTypes.String], GetClient),
                ["getCompany"] = OperationHandler.ForRead([], GetCompany),
                ["getItemByCode"] = OperationHandler.ForRead([JadeTypes.String, JadeTypes.Integer], GetItemByCode),
                ["getItemsByPrice"] = OperationHandler.ForRead([JadeTypes.Integer, JadeTypes.Integer], GetItemsByPrice),
                ["getSale"] = OperationHandler.ForRead([JadeTypes.String, JadeTypes.Integer], GetSale),
                ["postCreateAgent"] = OperationHandler.ForWrite([JadeTypes.DtoAgent], PostCreateAgent, DtoMembers.Contact),
                ["postCreateClient"] = OperationHandler.ForWrite([JadeTypes.DtoClient], PostCreateClient, DtoMembers.Contact),
                ["postCreateRetailSale"] = OperationHandler.ForWrite(
                    [JadeTypes.String, JadeTypes.String, JadeTypes.Integer], PostCreateRetailSale),
                ["postCreateTender"] = OperationHandler.ForWrite(
                    [JadeTypes.String, JadeTypes.String, JadeTypes.Integer, JadeTypes.Decimal], PostCreateTender),
            }.ToFrozenDictionary(StringComparer.Ordinal);

        // getAgent (scm:2328-2355): exact, untrimmed name lookup.
        private RestOutcome GetAgent(Company? company, RestArguments arguments)
        {
            if (company is null)
            {
                return NoCompany();
            }

            Agent? agent = company.AllAgents[arguments.Text(0)];
            return agent is null
                ? NullBody(_status.NotFound)
                : Success(_dto.Agent(agent, arguments.Operation.ReturnType));
        }

        // getAllAgents (scm:2356-2377): every agent in allAgents (name) order.
        private RestOutcome GetAllAgents(Company? company, RestArguments arguments)
        {
            if (company is null)
            {
                return NoCompany();
            }

            string itemClass = arguments.ArrayItemClass;
            return Success(_dto.Array(
                arguments.Operation.ReturnType,
                company.AllAgents.Select(agent => _dto.Agent(agent, itemClass))));
        }

        // getAllClients (scm:2378-2399): every client in allClients (name) order.
        private RestOutcome GetAllClients(Company? company, RestArguments arguments)
        {
            if (company is null)
            {
                return NoCompany();
            }

            string itemClass = arguments.ArrayItemClass;
            return Success(_dto.Array(
                arguments.Operation.ReturnType,
                company.AllClients.Select(client => _dto.Client(client, itemClass))));
        }

        // getAllSales (scm:2400-2434): allSalesByItem in (category prefix, code number) order; the
        // isKindOf( RetailSale ) and isKindOf( TenderSale ) branches skip a sale of neither kind.
        private RestOutcome GetAllSales(Company? company, RestArguments arguments)
        {
            if (company is null)
            {
                return NoCompany();
            }

            // Array enumerates the projection at once, inside the state's Read lock, into its only list;
            // OfType drops the null that a sale of neither kind yields.
            string itemClass = arguments.ArrayItemClass;
            return Success(_dto.Array(
                arguments.Operation.ReturnType,
                company.AllSalesByItem.Select(sale => _dto.Sale(sale, itemClass)).OfType<DtoNode>()));
        }

        // getClient (scm:2435-2458): the name is trimmed of blanks before the lookup (scm:2444).
        private RestOutcome GetClient(Company? company, RestArguments arguments)
        {
            if (company is null)
            {
                return NoCompany();
            }

            Client? client = company.AllClients[JadeValues.TrimBlanks(arguments.Text(0))];
            return client is null
                ? NullBody(_status.NotFound)
                : Success(_dto.Client(client, arguments.Operation.ReturnType));
        }

        // getCompany (scm:2459-2482): the one body that tests for a missing company itself.
        private RestOutcome GetCompany(Company? company, RestArguments arguments) =>
            company is null ? NullBody(_status.ServerError) : Success(_dto.Company(company));

        // getItemByCode (scm:2483-2535): the trimmed prefix and the code are validated, but the item is
        // found by code number alone (scm:2508); the JADE null string and null Integer are "" and 0.
        private RestOutcome GetItemByCode(Company? company, RestArguments arguments)
        {
            string codePrefix = JadeValues.TrimBlanks(arguments.Text(0));
            int codeNumber = arguments.Integer(1);
            if (codePrefix.Length == 0 || codePrefix.Length > MaxCodePrefixLength || codeNumber == 0)
            {
                return NullBody(_status.BadRequest);
            }

            if (company is null)
            {
                return NoCompany();
            }

            Item? item = company.GetItem(codeNumber);
            if (item is null)
            {
                return NullBody(_status.NotFound);
            }

            DtoNode? node = _dto.Item(item, arguments.Operation.ReturnType);
            return node is null ? NullBody(_status.ServerError) : Success(node);
        }

        // getItemsByPrice (scm:2536-2597): min < 0, max < 0 and max < min each answer 400 null before
        // allItems is read; the range is inclusive, compared as Decimal against Integer, in code order.
        private RestOutcome GetItemsByPrice(Company? company, RestArguments arguments)
        {
            int minPrice = arguments.Integer(0);
            int maxPrice = arguments.Integer(1);
            if (minPrice < 0 || maxPrice < 0 || maxPrice < minPrice)
            {
                return NullBody(_status.BadRequest);
            }

            if (company is null)
            {
                return NoCompany();
            }

            // An empty allItems takes JADE's early return of the empty array, which the walk also yields.
            // Array enumerates the walk at once, inside the state's Read lock, into its only list; an item
            // is built only when in range, and OfType drops the null that an item of neither kind yields.
            string itemClass = arguments.ArrayItemClass;
            return Success(_dto.Array(
                arguments.Operation.ReturnType,
                company.AllItems
                    .Where(item => item.Price >= minPrice && item.Price <= maxPrice)
                    .Select(item => _dto.Item(item, itemClass))
                    .OfType<DtoNode>()));
        }

        // getSale (scm:2598-2651): the trimmed prefix is validated (scm:2610), but the lookup uses the
        // untrimmed pCodePrefix, ordinal and case-sensitive (scm:2624).
        private RestOutcome GetSale(Company? company, RestArguments arguments)
        {
            string pCodePrefix = arguments.Text(0);
            string codePrefix = JadeValues.TrimBlanks(pCodePrefix);
            int codeNumber = arguments.Integer(1);
            if (codePrefix.Length == 0 || codePrefix.Length > MaxCodePrefixLength || codeNumber == 0)
            {
                return NullBody(_status.BadRequest);
            }

            if (company is null)
            {
                return NoCompany();
            }

            Sale? sale = company.AllSalesByItem.GetAtKey(pCodePrefix, codeNumber);
            if (sale is null)
            {
                return NullBody(_status.NotFound);
            }

            DtoNode? node = _dto.Sale(sale, arguments.Operation.ReturnType);
            return node is null ? NullBody(_status.ServerError) : Success(node);
        }

        // postCreateAgent (scm:2652-2691). The AgentTA create validation reads app.myCompany
        // (hasAgentWithThisName), so a missing company always ends the body there.
        private RestOutcome PostCreateAgent(Company? company, MutationContext context, RestArguments arguments)
        {
            if (company is null)
            {
                return NoCompany();
            }

            IReadOnlyDictionary<string, string> pAgent = arguments.Body(0);
            var agentTA = new AgentTA { Name = pAgent[DtoMembers.Name] };
            NewAddressTA(pAgent).MyModelTA = agentTA;

            return agentTA.PersistEntity(TransactionType.Persist, context.Model)
                ? CreatedResult(agentTA)
                : Result(_status.BadRequest, agentTA.AllErrors);
        }

        // postCreateClient (scm:2692-2731): as postCreateAgent, through ClientTA.
        private RestOutcome PostCreateClient(Company? company, MutationContext context, RestArguments arguments)
        {
            if (company is null)
            {
                return NoCompany();
            }

            IReadOnlyDictionary<string, string> pClient = arguments.Body(0);
            var clientTA = new ClientTA { Name = pClient[DtoMembers.Name] };
            NewAddressTA(pClient).MyModelTA = clientTA;

            return clientTA.PersistEntity(TransactionType.Persist, context.Model)
                ? CreatedResult(clientTA)
                : Result(_status.BadRequest, clientTA.AllErrors);
        }

        // postCreateRetailSale (scm:2732-2796): client by exact, untrimmed name, then the item by code
        // number alone (pCodePrefix is never read, scm:2757), then the retail check. The uninitialised
        // TimeStamp local JADE assigns to dateSold holds the current time, the request's "now".
        private RestOutcome PostCreateRetailSale(Company? company, MutationContext context, RestArguments arguments)
        {
            if (company is null)
            {
                return NoCompany();
            }

            Client? client = company.AllClients[arguments.Text(0)];
            if (client is null)
            {
                return Result(_status.NotFound, [Messages.ClientNotFound]);
            }

            Item? item = company.GetItem(arguments.Integer(2));
            if (item is null)
            {
                return Result(_status.NotFound, [Messages.ItemNotFound]);
            }

            if (item is not RetailItem)
            {
                return Result(_status.BadRequest, [Messages.ItemNotRetailItem]);
            }

            var retailSaleTA = new RetailSaleTA
            {
                MyItem = item,
                MyClient = client,
                SalePrice = item.Price,
                DateSold = context.Now,
            };

            return retailSaleTA.PersistEntity(TransactionType.Persist, context.Model)
                ? CreatedResult(retailSaleTA)
                : Result(_status.BadRequest, retailSaleTA.AllErrors);
        }

        // postCreateTender (scm:2797-2860): the same lookups as postCreateRetailSale, then TenderTA, whose
        // closure rule compares with the request's date through the model context.
        private RestOutcome PostCreateTender(Company? company, MutationContext context, RestArguments arguments)
        {
            if (company is null)
            {
                return NoCompany();
            }

            Client? client = company.AllClients[arguments.Text(0)];
            if (client is null)
            {
                return Result(_status.NotFound, [Messages.ClientNotFound]);
            }

            Item? item = company.GetItem(arguments.Integer(2));
            if (item is null)
            {
                return Result(_status.NotFound, [Messages.ItemNotFound]);
            }

            if (item is not TenderItem tenderItem)
            {
                return Result(_status.BadRequest, [Messages.ItemNotTenderItem]);
            }

            var tenderTA = new TenderTA();
            try
            {
                tenderTA.Offer = arguments.Amount(3);
            }
            catch (JadeDecimalOverflowException exception)
            {
                // tenderTA.offer := pOffer (scm:2841) raises JADE exception 4033 for an offer that does
                // not fit Decimal[12,2]. The method does not handle it, and an unhandled exception in a
                // JadeRestService method answers HTTP 500; nothing has been persisted. A modelled JADE
                // outcome, not a stand-in fault, so it is not logged as an error.
                _logger.LogDebug(
                    "REST {OperationId}: {Message} Answering {Status} with null.",
                    arguments.Operation.Id,
                    exception.Message,
                    _status.ServerError);
                return NullBody(_status.ServerError);
            }

            tenderTA.DateTendered = context.Now;
            tenderTA.MyClient = client;
            tenderTA.MyTenderItem = tenderItem;

            return tenderTA.PersistEntity(TransactionType.Persist, context.Model)
                ? CreatedResult(tenderTA)
                : Result(_status.BadRequest, tenderTA.AllErrors);
        }

        // The addressTA assignments of postCreateAgent and postCreateClient (scm:2668-2674, 2708-2714);
        // the DTO member is "website" and the agent attribute "webSite".
        private static AddressTA NewAddressTA(IReadOnlyDictionary<string, string> dto) => new()
        {
            Street = dto[DtoMembers.Street],
            City = dto[DtoMembers.City],
            Country = dto[DtoMembers.Country],
            Email = dto[DtoMembers.Email],
            Fax = dto[DtoMembers.Fax],
            Phone = dto[DtoMembers.Phone],
            WebSite = dto[DtoMembers.Website],
        };

        // Where a body dereferences a null app.myCompany, JADE raises a null-object exception; the stand-in
        // answers it as 500 with null. It is answered at the point the ported body first reads the company,
        // not on entry, so getItemByCode, getItemsByPrice and getSale still give their 400 validation
        // results first: the stand-in follows the method bodies.
        private RestOutcome NoCompany() => NullBody(_status.ServerError);

        private RestOutcome Success(object body) => new(_status.Success, body);

        private static RestOutcome NullBody(int status) => new(status, null);

        // A DTOResult as the write bodies build it on failure: statusCode := httpStatusCode and the errors
        // copied in order; guid stays the JADE null string and allWarnings empty.
        private RestOutcome Result(int status, IEnumerable<string> errors) =>
            new(status, _dto.Result(status, errors));

        // dtoResult.guid := <agent>.getModelObject().guid; dtoResult.statusCode := HTTP_Created.
        private RestOutcome CreatedResult(ModelTA agent)
        {
            PersistentModel created = agent.ModelObject
                ?? throw new InvalidOperationException(
                    $"{agent.GetType().Name} reported a successful create but holds no model object.");
            return new(_status.Created, _dto.Result(_status.Created, guid: created.Guid));
        }

        private bool IsJsonFormat(string? formatSuffix) =>
            formatSuffix is null || string.Equals(formatSuffix, _jsonSuffix, StringComparison.Ordinal);

        // The raw request target, still percent-encoded and without its query: origin-form as sent, or
        // the path of an absolute-form target. Any other form (asterisk, authority) is unmatched.
        private static bool TryGetRawPath(HttpContext context, [NotNullWhen(true)] out string? rawPath)
        {
            rawPath = null;
            string? target = context.Features.Get<IHttpRequestFeature>()?.RawTarget;
            if (string.IsNullOrEmpty(target))
            {
                return false;
            }

            int pathStart = 0;
            if (target[0] != '/')
            {
                int schemeEnd = target.IndexOf("://", StringComparison.Ordinal);
                if (schemeEnd <= 0 || !Uri.CheckSchemeName(target[..schemeEnd]))
                {
                    return false;
                }

                int authorityEnd = target.IndexOfAny(['/', '?', '#'], schemeEnd + 3);
                if (authorityEnd < 0 || target[authorityEnd] != '/')
                {
                    return false;
                }

                pathStart = authorityEnd;
            }

            int queryStart = target.IndexOf('?', pathStart);
            rawPath = queryStart < 0 ? target[pathStart..] : target[pathStart..queryStart];
            return true;
        }

        // Path parameters first, in signature order, then the one body parameter; null when any value
        // does not convert, which the pipeline answers with 400 and null without running the business
        // handler. A state-changing request rejected this way is still counted in the mutation count,
        // which moves the clock.
        private static async Task<RestArguments?> ReadArgumentsAsync(
            HttpRequest request,
            OperationBinding binding,
            RestMatch match,
            CancellationToken cancellationToken)
        {
            IReadOnlyList<ParameterDescriptor> parameters = binding.Parameters;
            var values = new object[parameters.Count];

            for (int index = 0; index < parameters.Count; index++)
            {
                ParameterDescriptor parameter = parameters[index];
                if (parameter.Location != ParameterLocation.Path)
                {
                    continue;
                }

                if (!TryConvertPrimitive(parameter.JadeType, match.GetDecoded(parameter.Name), out object? value))
                {
                    return null;
                }

                values[index] = value;
            }

            for (int index = 0; index < parameters.Count; index++)
            {
                if (parameters[index].Location != ParameterLocation.Body)
                {
                    continue;
                }

                IReadOnlyDictionary<string, string>? body =
                    await ReadBodyAsync(request, binding.Handler.BodyMembers, cancellationToken).ConfigureAwait(false);
                if (body is null)
                {
                    return null;
                }

                values[index] = body;
            }

            return new RestArguments(binding.Operation, binding.ArrayItemClass, values);
        }

        // A path segment as its JADE primitive, in the invariant culture: a String exactly as decoded (only
        // the bodies that trim do so); an Integer as an optional leading sign and digits; a Decimal as the
        // same with at most one '.' among the digits. Both allow surrounding white space. No group
        // separator, trailing sign, parentheses, exponent or currency symbol is accepted, so "2600000,00"
        // or "100-" is not read as another number. A Decimal with more fraction digits than decimal holds
        // is rounded.
        private static bool TryConvertPrimitive(string jadeType, string text, [NotNullWhen(true)] out object? value)
        {
            const NumberStyles DecimalLiteral = NumberStyles.AllowLeadingWhite | NumberStyles.AllowTrailingWhite
                | NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint;

            switch (jadeType)
            {
                case JadeTypes.String:
                    value = text;
                    return true;
                case JadeTypes.Integer when int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int integer):
                    value = integer;
                    return true;
                case JadeTypes.Decimal when decimal.TryParse(text, DecimalLiteral, CultureInfo.InvariantCulture, out decimal amount):
                    value = amount;
                    return true;
                default:
                    value = null;
                    return false;
            }
        }

        // The JSON object parameter: the whole body, after at most one leading UTF-8 byte order mark, under
        // the default (strict) reader options. Each member the body reads is taken by ordinal name, the
        // last duplicate winning; a missing member or JSON null is the JADE null string. Malformed JSON
        // (invalid UTF-8 included), an unpaired surrogate escape in any member name or in a read member's
        // value, an empty body, a root that is not an object, or a read member of another JSON kind gives
        // null. guid, __type and unknown members are ignored, and their values are not decoded.
        private static async Task<IReadOnlyDictionary<string, string>?> ReadBodyAsync(
            HttpRequest request,
            IReadOnlyList<string> members,
            CancellationToken cancellationToken)
        {
            using var buffer = new MemoryStream();
            await request.Body.CopyToAsync(buffer, cancellationToken).ConfigureAwait(false);

            // The JSON reader checks only the structure, so text that is not UTF-8 would otherwise surface
            // as a failure to read a member's value instead of as a malformed body.
            ReadOnlyMemory<byte> utf8Json = buffer.GetBuffer().AsMemory(0, (int)buffer.Length);
            if (!Utf8.IsValid(utf8Json.Span))
            {
                return null;
            }

            // The body is parsed in place: the document references the buffer, which is disposed after it.
            // Parsing memory, unlike parsing a stream, does not skip a byte order mark, so one is skipped
            // here; a second one still fails the parse.
            if (utf8Json.Span.StartsWith(Utf8ByteOrderMark))
            {
                utf8Json = utf8Json[Utf8ByteOrderMark.Length..];
            }

            JsonDocument document;
            try
            {
                document = JsonDocument.Parse(utf8Json);
            }
            catch (JsonException)
            {
                return null;
            }

            using (document)
            {
                JsonElement root = document.RootElement;
                if (root.ValueKind != JsonValueKind.Object)
                {
                    return null;
                }

                // One pass in document order decodes every member name, so an invalid name anywhere makes the
                // body malformed, and a later duplicate of a read member replaces an earlier one.
                var elements = new Dictionary<string, JsonElement>(members.Count, StringComparer.Ordinal);
                foreach (JsonProperty property in root.EnumerateObject())
                {
                    if (!TryGetName(property, out string? name))
                    {
                        return null;
                    }

                    if (members.Contains(name, StringComparer.Ordinal))
                    {
                        elements[name] = property.Value;
                    }
                }

                var values = new Dictionary<string, string>(members.Count, StringComparer.Ordinal);
                foreach (string member in members)
                {
                    if (!elements.TryGetValue(member, out JsonElement element))
                    {
                        values[member] = "";
                        continue;
                    }

                    switch (element.ValueKind)
                    {
                        case JsonValueKind.String when TryGetText(element, out string? text):
                            values[member] = text;
                            break;
                        case JsonValueKind.Null:
                            values[member] = "";
                            break;
                        default:
                            return null;
                    }
                }

                return values;
            }
        }

        // A JSON string escape such as an unpaired \ud800 is valid JSON syntax but no string.
        private static bool TryGetText(JsonElement element, [NotNullWhen(true)] out string? text)
        {
            try
            {
                text = element.GetString() ?? "";
                return true;
            }
            catch (InvalidOperationException)
            {
                text = null;
                return false;
            }
        }

        // A member name is a JSON string as well, so the same escapes make it valid syntax but no name.
        private static bool TryGetName(JsonProperty property, [NotNullWhen(true)] out string? name)
        {
            try
            {
                name = property.Name;
                return true;
            }
            catch (InvalidOperationException)
            {
                name = null;
                return false;
            }
        }

        private static async Task WriteFormatNotModelledAsync(HttpResponse response, CancellationToken cancellationToken)
        {
            response.StatusCode = StatusCodes.Status501NotImplemented;
            response.ContentType = PlainTextContentType;
            response.ContentLength = FormatNotModelledBody.Length;
            await response.Body.WriteAsync(FormatNotModelledBody, cancellationToken).ConfigureAwait(false);
        }

        private async Task WriteServerErrorAsync(HttpContext context, OperationDescriptor operation)
        {
            if (context.Response.HasStarted)
            {
                // Part of a response is already on the wire; aborting keeps it from passing as complete.
                context.Abort();
                return;
            }

            try
            {
                context.Response.Clear();
                await JadeJsonWriter.WriteAsync(context.Response, _status.ServerError, null, operation.ReturnType, CancellationToken.None)
                    .ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                _logger.LogError(exception, "REST {OperationId}: the {Status} response could not be written.", operation.Id, _status.ServerError);
                context.Abort();
            }
        }

        private static string ResolveJsonSuffix(RestAddress rest)
        {
            string suffix = "." + rest.DefaultFormat;
            return rest.FormatSuffixes.Contains(suffix, StringComparer.Ordinal)
                ? suffix
                : throw new InvalidOperationException(
                    $"The operation catalogue's REST format suffixes ({string.Join(", ", rest.FormatSuffixes)}) do not include " +
                    $"'{suffix}', the suffix of its default format '{rest.DefaultFormat}', which the stand-in serves.");
        }

        // Pairs every catalogue REST operation with its handler, failing start-up unless the handler table
        // ports exactly those operations, each with the catalogue's kind and signature.
        private static FrozenDictionary<string, OperationBinding> Bind(
            OperationCatalogue catalogue,
            FrozenDictionary<string, OperationHandler> handlers)
        {
            IReadOnlyList<OperationDescriptor> operations = catalogue.GetBySurface(Surface.Rest);
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
                    "The stand-in REST handler table does not match the catalogue's REST operations: " +
                    $"missing [{string.Join(", ", missing)}], extra [{string.Join(", ", extra)}].");
            }

            return operations.ToFrozenDictionary(
                operation => operation.JadeMethod,
                operation => CreateBinding(catalogue, operation, handlers[operation.JadeMethod]),
                StringComparer.Ordinal);
        }

        private static OperationBinding CreateBinding(OperationCatalogue catalogue, OperationDescriptor operation, OperationHandler handler)
        {
            if (handler.StateChanging != operation.StateChanging)
            {
                throw Inconsistent(
                    operation,
                    $"is {(operation.StateChanging ? "" : "not ")}state-changing, but its handler {(handler.StateChanging ? "writes" : "only reads")}");
            }

            ParameterDescriptor[] parameters = [.. operation.Parameters.OrderBy(parameter => parameter.Position)];
            string[] declared = [.. parameters.Select(parameter => parameter.JadeType)];
            if (!declared.SequenceEqual(handler.Signature, StringComparer.Ordinal))
            {
                throw Inconsistent(
                    operation,
                    $"declares ({string.Join(", ", declared)}), but its handler ports ({string.Join(", ", handler.Signature)})");
            }

            int bodyParameters = 0;
            foreach (ParameterDescriptor parameter in parameters)
            {
                switch (parameter.Location)
                {
                    case ParameterLocation.Path when parameter.JadeType is JadeTypes.String or JadeTypes.Integer or JadeTypes.Decimal:
                        break;
                    case ParameterLocation.Body when catalogue.TryGetDto(parameter.JadeType, out DtoDefinition? dto):
                        bodyParameters++;
                        foreach (string member in handler.BodyMembers)
                        {
                            if (!dto.TryGetProperty(member, out var property)
                                || !string.Equals(property.JadeType, JadeTypes.String, StringComparison.Ordinal))
                            {
                                throw Inconsistent(operation, $"body DTO '{dto.Name}' has no String property '{member}', which its handler reads");
                            }
                        }

                        break;
                    default:
                        throw Inconsistent(
                            operation,
                            $"parameter '{parameter.Name}' is a {parameter.Location} parameter of type '{parameter.JadeType}', which the REST stand-in cannot read");
                }
            }

            if (bodyParameters > 1 || (bodyParameters == 1) != (handler.BodyMembers.Count > 0))
            {
                throw Inconsistent(operation, $"has {bodyParameters} body parameter(s), but its handler reads {handler.BodyMembers.Count} body member(s)");
            }

            string? arrayItemClass = catalogue.TryGetArray(operation.ReturnType, out ArrayDefinition? array) ? array.ItemType : null;
            return new OperationBinding(operation, handler, parameters, arrayItemClass);
        }

        private static InvalidOperationException Inconsistent(OperationDescriptor operation, string problem) =>
            new($"REST operation {operation.Id} {problem}.");
    }

    /// <summary>The <c>HTTP_*</c> constants the REST bodies assign, resolved by name from the catalogue.</summary>
    private sealed record RestStatuses(int Success, int Created, int BadRequest, int NotFound, int ServerError)
    {
        public static RestStatuses Resolve(OperationCatalogue catalogue)
        {
            IReadOnlyList<StatusConstant> constants = catalogue.GetConstants(Surface.Rest);

            int Value(string name) =>
                constants.FirstOrDefault(constant => string.Equals(constant.Name, name, StringComparison.Ordinal))?.Value
                ?? throw new InvalidOperationException(
                    $"The operation catalogue declares no REST status constant '{name}', which the stand-in's REST handlers assign.");

            return new RestStatuses(
                Value("HTTP_Success"),
                Value("HTTP_Created"),
                Value("HTTP_Bad_Request"),
                Value("HTTP_Not_Found"),
                Value("HTTP_Server_Error"));
        }
    }

    /// <summary>One ported method body: its JADE signature, the body members it reads, and its read or write delegate.</summary>
    private sealed class OperationHandler
    {
        private OperationHandler(IReadOnlyList<string> signature, IReadOnlyList<string> bodyMembers, ReadHandler? read, WriteHandler? write)
        {
            Signature = signature;
            BodyMembers = bodyMembers;
            Read = read;
            Write = write;
        }

        /// <summary>The JADE parameter types in signature order, as the ported method declares them.</summary>
        public IReadOnlyList<string> Signature { get; }

        /// <summary>The members of the object parameter the body reads; empty when it has none.</summary>
        public IReadOnlyList<string> BodyMembers { get; }

        public ReadHandler? Read { get; }

        public WriteHandler? Write { get; }

        public bool StateChanging => Write is not null;

        public static OperationHandler ForRead(string[] signature, ReadHandler read) => new(signature, [], read, null);

        public static OperationHandler ForWrite(string[] signature, WriteHandler write, string[]? bodyMembers = null) =>
            new(signature, bodyMembers ?? [], null, write);
    }

    /// <summary>A catalogue REST operation with its handler, its parameters in signature order and, for an array result, the item class.</summary>
    private sealed record OperationBinding(
        OperationDescriptor Operation,
        OperationHandler Handler,
        IReadOnlyList<ParameterDescriptor> Parameters,
        string? ArrayItemClass);

    /// <summary>The converted arguments of one request, by place in signature order.</summary>
    private sealed class RestArguments(OperationDescriptor operation, string? arrayItemClass, object[] values)
    {
        public OperationDescriptor Operation { get; } = operation;

        /// <summary>The declared class of the result array's items, <see cref="ArrayDefinition.ItemType"/>.</summary>
        public string ArrayItemClass =>
            arrayItemClass ?? throw new InvalidOperationException($"REST operation {Operation.Id} does not return a DTO array.");

        public string Text(int index) => (string)values[index];

        public int Integer(int index) => (int)values[index];

        public decimal Amount(int index) => (decimal)values[index];

        public IReadOnlyDictionary<string, string> Body(int index) => (IReadOnlyDictionary<string, string>)values[index];
    }

    /// <summary>The JADE types the ported signatures declare.</summary>
    private static class JadeTypes
    {
        public const string String = "String";
        public const string Integer = "Integer";
        public const string Decimal = "Decimal";
        public const string DtoAgent = "DTOAgent";
        public const string DtoClient = "DTOClient";
    }

    /// <summary>
    /// The <c>DTOAgent</c> and <c>DTOClient</c> members <c>postCreateAgent</c> and <c>postCreateClient</c>
    /// read (scm:2667-2674, 2707-2714), as the reference schema declares them.
    /// </summary>
    private static class DtoMembers
    {
        public const string Name = "name";
        public const string Street = "street";
        public const string City = "city";
        public const string Country = "country";
        public const string Email = "email";
        public const string Fax = "fax";
        public const string Phone = "phone";
        public const string Website = "website";

        /// <summary>The members in the order the JADE bodies assign them.</summary>
        public static readonly string[] Contact = [Name, Street, City, Country, Email, Fax, Phone, Website];
    }
}
