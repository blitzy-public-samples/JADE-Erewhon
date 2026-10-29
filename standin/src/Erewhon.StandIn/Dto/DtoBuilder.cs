using System.Collections;
using System.Collections.Frozen;
using System.Collections.ObjectModel;
using Erewhon.Contracts.Catalogue;
using Erewhon.StandIn.Model;

namespace Erewhon.StandIn.Dto;

/// <summary>
/// The value kind of a DTO member, derived from its catalogue <see cref="DtoProperty.JadeType"/>. Each
/// kind holds one CLR type: <see cref="String"/> a <see cref="string"/>, <see cref="Integer"/> an
/// <see cref="int"/>, <see cref="Decimal"/> a <see cref="decimal"/>, <see cref="Boolean"/> a
/// <see cref="bool"/>, <see cref="Date"/> a <see cref="DateOnly"/> or null, <see cref="TimeStamp"/> a UTC
/// <see cref="DateTime"/> or null, <see cref="Binary"/> a <see cref="byte"/> array or null,
/// <see cref="StringArray"/> an <see cref="IReadOnlyList{T}"/> of strings and <see cref="Reference"/> a
/// <see cref="DtoNode"/> or null.
/// </summary>
public enum DtoValueKind
{
    /// <summary>JADE <c>String</c> or <c>String[n]</c>; JADE's null string is <c>""</c>.</summary>
    String,

    /// <summary>JADE <c>Integer</c>; JADE's null integer is 0.</summary>
    Integer,

    /// <summary>JADE <c>Decimal</c> or <c>Decimal[p,s]</c>; JADE's null decimal is 0.</summary>
    Decimal,

    /// <summary>JADE <c>Boolean</c>; JADE's null boolean is false.</summary>
    Boolean,

    /// <summary>JADE <c>Date</c>, held as a <see cref="DateOnly"/>; null when unset.</summary>
    Date,

    /// <summary>JADE <c>TimeStamp</c>, held as a UTC <see cref="DateTime"/>; null when unset.</summary>
    TimeStamp,

    /// <summary>JADE <c>Binary</c>, held as the raw bytes; null when unset or zero-length.</summary>
    Binary,

    /// <summary>JADE <c>StringArray</c>, held as a read-only list in stored order; empty when unset.</summary>
    StringArray,

    /// <summary>A reference to a nested DTO, held as a <see cref="DtoNode"/>; null when unset.</summary>
    Reference,
}

/// <summary>One member of a <see cref="DtoNode"/>: its catalogue property, value kind and current value.</summary>
/// <param name="Property">The catalogue property, carrying the JADE name, declared type and SOAP exposure.</param>
/// <param name="Kind">The value kind derived from <see cref="DtoProperty.JadeType"/>.</param>
/// <param name="Value">The value, of the CLR type <paramref name="Kind"/> holds.</param>
public readonly record struct DtoMember(DtoProperty Property, DtoValueKind Kind, object? Value);

/// <summary>
/// The catalogue facts of one DTO class, resolved once per <see cref="DtoBuilder"/>: the flattened
/// property list, each property's value kind, the declared class of each reference, and the JADE null
/// value of each property. Immutable, so one shape serves every node of its class on every thread.
/// </summary>
internal sealed class DtoShape
{
    private static readonly object NullInteger = 0;
    private static readonly object NullDecimal = 0m;
    private static readonly object NullBoolean = false;

    private readonly FrozenDictionary<string, int> _indexByName;
    private readonly string?[] _referenceTypes;

    internal DtoShape(OperationCatalogue catalogue, DtoDefinition definition)
    {
        Definition = definition;

        var properties = definition.Properties;
        var kinds = new DtoValueKind[properties.Count];
        _referenceTypes = new string?[properties.Count];
        for (var index = 0; index < properties.Count; index++)
        {
            (kinds[index], _referenceTypes[index]) = Classify(catalogue, definition, properties[index]);
        }

        Kinds = kinds;
        _indexByName = properties
            .Select((property, index) => (property.Name, index))
            .ToFrozenDictionary(entry => entry.Name, entry => entry.index, StringComparer.Ordinal);
    }

    internal DtoDefinition Definition { get; }

    internal string Name => Definition.Name;

    internal IReadOnlyList<DtoProperty> Properties => Definition.Properties;

    internal IReadOnlyList<DtoValueKind> Kinds { get; }

    internal bool TryGetIndex(string name, out int index) => _indexByName.TryGetValue(name, out index);

    /// <summary>The declared DTO class of the reference at <paramref name="index"/>; null for any other kind.</summary>
    internal string? ReferenceTypeAt(int index) => _referenceTypes[index];

    /// <summary>
    /// The declared DTO class of a reference property, the class JADE's <c>create self.&lt;property&gt;
    /// transient</c> instantiates.
    /// </summary>
    /// <exception cref="InvalidOperationException">The class has no reference property of that name.</exception>
    internal string ReferenceTypeOf(string property) =>
        TryGetIndex(property, out var index) && _referenceTypes[index] is { } declared
            ? declared
            : throw new InvalidOperationException($"DTO '{Name}' has no reference property '{property}'.");

    /// <summary>A new value array holding JADE's null value for every property, in catalogue order.</summary>
    internal object?[] CreateNullValues()
    {
        var values = new object?[Kinds.Count];
        for (var index = 0; index < values.Length; index++)
        {
            values[index] = Kinds[index] switch
            {
                DtoValueKind.String => "",
                DtoValueKind.Integer => NullInteger,
                DtoValueKind.Decimal => NullDecimal,
                DtoValueKind.Boolean => NullBoolean,
                DtoValueKind.StringArray => ReadOnlyCollection<string>.Empty,
                _ => null,
            };
        }

        return values;
    }

    // Maps a property to its value kind by its JadeType, accepting the bare and the sized spellings
    // (String/String[39], Decimal/Decimal[12,2]); a DTO name, or a declared reference, is a Reference.
    private static (DtoValueKind Kind, string? ReferenceType) Classify(
        OperationCatalogue catalogue, DtoDefinition definition, DtoProperty property)
    {
        var jadeType = property.JadeType;
        var bracket = jadeType.IndexOf('[', StringComparison.Ordinal);
        var baseType = bracket < 0 ? jadeType : jadeType[..bracket];

        DtoValueKind? primitive = baseType switch
        {
            "String" => DtoValueKind.String,
            "Integer" => DtoValueKind.Integer,
            "Decimal" => DtoValueKind.Decimal,
            "Boolean" => DtoValueKind.Boolean,
            "Date" => DtoValueKind.Date,
            "TimeStamp" => DtoValueKind.TimeStamp,
            "Binary" => DtoValueKind.Binary,
            "StringArray" => DtoValueKind.StringArray,
            _ => null,
        };

        var isDto = bracket < 0 && catalogue.TryGetDto(baseType, out _);
        switch (property.Kind)
        {
            case PropertyKind.Reference when isDto:
            case PropertyKind.Attribute when primitive is null && isDto:
                return (DtoValueKind.Reference, baseType);
            case PropertyKind.Collection when primitive == DtoValueKind.StringArray:
            case PropertyKind.Attribute when primitive is not null:
                return (primitive.Value, null);
        }

        throw new InvalidDataException(
            $"DTO '{definition.Name}' property '{property.Name}' has JADE type '{jadeType}' ({property.Kind}), " +
            "which the stand-in DTO tree cannot hold.");
    }
}

/// <summary>
/// One DTO object of the neutral tree that <see cref="DtoBuilder"/> populates and both writers serialise:
/// an instance of <see cref="RuntimeClass"/> sitting in a position declared as <see cref="DeclaredClass"/>.
/// </summary>
/// <remarks>
/// Every catalogue property of <see cref="RuntimeClass"/> is always present, in
/// <see cref="DtoDefinition.Properties"/> order: a property the population code never assigns keeps
/// JADE's null value for its type (<c>""</c>, 0, 0m, false, an empty string list, or null for a date,
/// timestamp, binary or reference). Values stay typed; formatting belongs to the writers. A node is
/// mutable only through <see cref="Set"/> while it is being built and is not thread-safe while that
/// happens; once built it is only read.
/// </remarks>
public sealed class DtoNode
{
    private readonly object?[] _values;

    internal DtoNode(DtoShape shape, string declaredClass)
    {
        Shape = shape;
        DeclaredClass = declaredClass;
        _values = shape.CreateNullValues();
        Members = new MemberList(this);
    }

    /// <summary>The class of the object, for example <c>DTORetailItem</c>.</summary>
    public string RuntimeClass => Shape.Name;

    /// <summary>
    /// The declared class of the position holding the object, for example <c>DTOItem</c> for the
    /// <c>getItemByCode</c> result or <c>DTOSaleDetail</c> for <c>DTOItem.sale</c>: the operation's
    /// return type for a root, the array's item type for an array item, or the property's declared type
    /// for a nested object. <see cref="RuntimeClass"/> is always that class or one of its subclasses.
    /// </summary>
    public string DeclaredClass { get; }

    /// <summary>The catalogue definition of <see cref="RuntimeClass"/>.</summary>
    public DtoDefinition Definition => Shape.Definition;

    /// <summary>
    /// True when <see cref="RuntimeClass"/> differs ordinally from <see cref="DeclaredClass"/>, which is
    /// exactly when the JSON writer emits the <c>__type</c> member first.
    /// </summary>
    public bool EmitsTypeMember => !string.Equals(RuntimeClass, DeclaredClass, StringComparison.Ordinal);

    /// <summary>Every member of the object, in <see cref="DtoDefinition.Properties"/> order, <c>guid</c> included.</summary>
    public IReadOnlyList<DtoMember> Members { get; }

    internal DtoShape Shape { get; }

    /// <summary>The value of a member, of the CLR type its <see cref="DtoValueKind"/> holds.</summary>
    /// <param name="name">The JADE property name, compared ordinally and case-sensitively.</param>
    /// <exception cref="ArgumentNullException"><paramref name="name"/> is null.</exception>
    /// <exception cref="KeyNotFoundException"><see cref="RuntimeClass"/> has no property of that name.</exception>
    public object? this[string name] => _values[IndexOf(name)];

    /// <summary>The value of a member as <typeparamref name="T"/>, for example <c>Get&lt;string&gt;("name")</c>.</summary>
    /// <typeparam name="T">The CLR type of the member's kind, or a nullable form of it.</typeparam>
    /// <param name="name">The JADE property name, compared ordinally and case-sensitively.</param>
    /// <returns>The value; the default of <typeparamref name="T"/> for a null value of a nullable type.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="name"/> is null.</exception>
    /// <exception cref="KeyNotFoundException"><see cref="RuntimeClass"/> has no property of that name.</exception>
    /// <exception cref="InvalidCastException">The value is not a <typeparamref name="T"/>.</exception>
    public T Get<T>(string name)
    {
        var value = this[name];
        return value switch
        {
            T typed => typed,
            null when default(T) is null => default!,
            _ => throw new InvalidCastException(
                $"DTO '{RuntimeClass}' property '{name}' holds {DescribeValue(value)}, not {typeof(T).Name}."),
        };
    }

    /// <summary>
    /// Assigns a member, as a JADE <c>self.&lt;name&gt; := value</c> does, after checking the value
    /// against the member's kind.
    /// </summary>
    /// <remarks>
    /// A null <c>String</c> is stored as <c>""</c>, JADE's null string, and a zero-length
    /// <c>Binary</c> as null, because JADE has no empty binary distinct from its null binary. A
    /// <c>TimeStamp</c> of <see cref="DateTimeKind.Unspecified"/> kind is taken as UTC, the zone of the
    /// stand-in clock and of <c>_webDefaultTimeZone Etc/UTC</c>
    /// (Erewhon/ErehwhonExternalInterfacesSchema.ddx:92). A <c>StringArray</c> accepts any sequence of
    /// strings and stores a read-only copy in the given order, with a null entry stored as <c>""</c>.
    /// Binary bytes are stored as given, never copied.
    /// </remarks>
    /// <param name="name">The JADE property name, compared ordinally and case-sensitively.</param>
    /// <param name="value">The value, of the CLR type the member's kind holds.</param>
    /// <exception cref="ArgumentNullException"><paramref name="name"/> is null.</exception>
    /// <exception cref="InvalidOperationException">
    /// <see cref="RuntimeClass"/> has no catalogue property of that name; the value's CLR type does not
    /// match the member's kind; a timestamp is a local time; or a nested node's
    /// <see cref="DeclaredClass"/> is not the property's declared type.
    /// </exception>
    public void Set(string name, object? value)
    {
        ArgumentNullException.ThrowIfNull(name);
        if (!Shape.TryGetIndex(name, out var index))
        {
            throw new InvalidOperationException(
                $"DTO '{RuntimeClass}' has no property '{name}' in the operation catalogue.");
        }

        _values[index] = Coerce(index, value);
    }

    private int IndexOf(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        return Shape.TryGetIndex(name, out var index)
            ? index
            : throw new KeyNotFoundException($"DTO '{RuntimeClass}' has no property '{name}'.");
    }

    private object? Coerce(int index, object? value)
    {
        switch (Shape.Kinds[index], value)
        {
            case (DtoValueKind.String, null):
                return "";
            case (DtoValueKind.String, string):
            case (DtoValueKind.Integer, int):
            case (DtoValueKind.Decimal, decimal):
            case (DtoValueKind.Boolean, bool):
            case (DtoValueKind.Date, null or DateOnly):
            case (DtoValueKind.TimeStamp, null):
            case (DtoValueKind.Binary, null):
            case (DtoValueKind.Reference, null):
                return value;
            case (DtoValueKind.TimeStamp, DateTime timestamp):
                return timestamp.Kind switch
                {
                    DateTimeKind.Utc => timestamp,
                    DateTimeKind.Unspecified => DateTime.SpecifyKind(timestamp, DateTimeKind.Utc),
                    _ => throw new InvalidOperationException(
                        $"DTO '{RuntimeClass}' property '{Shape.Properties[index].Name}' is a TimeStamp and " +
                        "takes a UTC time; the value supplied is a local time."),
                };
            case (DtoValueKind.Binary, byte[] bytes):
                return bytes.Length == 0 ? null : bytes;
            case (DtoValueKind.StringArray, IEnumerable<string?> strings):
                return Array.AsReadOnly(strings.Select(entry => entry ?? "").ToArray());
            case (DtoValueKind.Reference, DtoNode node):
                var declared = Shape.ReferenceTypeAt(index);
                if (!string.Equals(node.DeclaredClass, declared, StringComparison.Ordinal))
                {
                    throw new InvalidOperationException(
                        $"DTO '{RuntimeClass}' property '{Shape.Properties[index].Name}' is declared '{declared}'; " +
                        $"a '{node.RuntimeClass}' node declared '{node.DeclaredClass}' cannot be assigned to it.");
                }

                return node;
        }

        throw new InvalidOperationException(
            $"DTO '{RuntimeClass}' property '{Shape.Properties[index].Name}' is of kind {Shape.Kinds[index]} " +
            $"(JADE type '{Shape.Properties[index].JadeType}'); it cannot hold {DescribeValue(value)}.");
    }

    private static string DescribeValue(object? value) =>
        value is null ? "null" : $"a value of type {value.GetType().Name}";

    // A live view over the node's values, so Members never goes stale and costs no copy.
    private sealed class MemberList(DtoNode node) : IReadOnlyList<DtoMember>
    {
        public int Count => node._values.Length;

        public DtoMember this[int index] =>
            new(node.Shape.Properties[index], node.Shape.Kinds[index], node._values[index]);

        public IEnumerator<DtoMember> GetEnumerator()
        {
            for (var index = 0; index < Count; index++)
            {
                yield return this[index];
            }
        }

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
}

/// <summary>
/// One DTO array of the neutral tree, for example the <c>DTOItemsArray</c> that
/// <c>getItemsByPrice</c> returns: its items in insertion order, each sitting in a position declared
/// as the array's item type.
/// </summary>
public sealed class DtoArray
{
    internal DtoArray(ArrayDefinition definition, IReadOnlyList<DtoNode> items)
    {
        Definition = definition;
        Items = items;
    }

    /// <summary>The JADE array class, for example <c>DTOAgentArray</c> or <c>DTONameArray</c>.</summary>
    public string ArrayType => Definition.Name;

    /// <summary>The catalogue definition of <see cref="ArrayType"/>.</summary>
    public ArrayDefinition Definition { get; }

    /// <summary>
    /// The declared class of every item position, the array's <see cref="ArrayDefinition.ItemType"/>; it
    /// equals the <see cref="DtoNode.DeclaredClass"/> of every item.
    /// </summary>
    public string ItemDeclaredClass => Definition.ItemType;

    /// <summary>The items, in the order they were added.</summary>
    public IReadOnlyList<DtoNode> Items { get; }
}


/// <summary>
/// Ports every JADE <c>populateFromObject</c> method of the reference schema
/// (Erewhon/ErehwhonExternalInterfacesSchema.scm:2085-2325) into one neutral, typed DTO tree, which
/// <c>Rest/JadeJsonWriter.cs</c> and <c>Soap/SoapEnvelopeWriter.cs</c> both serialise, so the REST and
/// SOAP surfaces never populate a DTO differently. The handlers port the method bodies (lookups,
/// <c>isKindOf</c> branches, status codes) and call the factories here.
/// </summary>
/// <remarks>
/// <para>
/// Class names, property names, JADE types and member order come only from the operation catalogue; the
/// only names written here are those the ported JADE assignments use, and the constructor checks each
/// of them against the catalogue before any tree is built.
/// </para>
/// <para>
/// Guarantees to the writers:
/// </para>
/// <list type="bullet">
/// <item>No writer decides on <c>__type</c> from model types: it uses <see cref="DtoNode.EmitsTypeMember"/>,
/// and the JSON writer names the member <c>JadeWireFormats.TypeMemberName</c>.</item>
/// <item>Every catalogue property of a node's runtime class is filled, <c>guid</c> included; the SOAP
/// writer filters members by <see cref="DtoProperty.SoapExposed"/>.</item>
/// <item>A built tree holds no reference to a model object (only immutable values and the photo bytes,
/// which the model never mutates), so it can be written outside the state lock.</item>
/// </list>
/// <para>
/// The builder only reads model state: it never mutates the model, reads a clock, generates a GUID,
/// sorts or reorders a sequence, or logs. It is immutable after construction and safe for concurrent
/// use; each call returns new nodes that belong to the caller.
/// </para>
/// <para>
/// <c>DTOTenderSaleDetail::populateFromObject</c> (Erewhon/ErehwhonExternalInterfacesSchema.scm:2291-2304)
/// is not ported: no in-scope method body creates a <c>DTOTenderSaleDetail</c>, because a sold tender
/// item's <c>sale</c> is created as the declared plain <c>DTOSaleDetail</c> (scm:2208-2209). Nothing
/// here creates a <c>DTOError</c> either. Conformance registers both as unreachable in source.
/// </para>
/// </remarks>
public sealed class DtoBuilder
{
    private static readonly Lazy<DtoBuilder> _default =
        new(() => new DtoBuilder(OperationCatalogue.Default), LazyThreadSafetyMode.ExecutionAndPublication);

    // The address-backed members the agent, client and company populate methods assign, in source
    // order (scm:2105-2112, 2125-2132, 2145-2152). Declared before Assignments, which reads it.
    private static readonly string[] ContactProperties =
    [
        Props.Name, Props.Street, Props.City, Props.Country, Props.Phone, Props.Fax, Props.Email, Props.Website,
    ];

    // Every property the population code below assigns, under the class whose populateFromObject (or,
    // for DTOName, DTOPhoto and DTOResult, whose method body) assigns it. A reference names the class
    // JADE creates for it: `create self.<p> transient` creates the property's declared type, and
    // `create self.<p> as <C> transient` (CreateAs) creates a subclass of it. The constructor checks
    // every entry against the catalogue.
    private static readonly Assignment[] Assignments =
    [
        new(DtoClasses.Object, Props.Guid),
        .. ContactProperties.Select(property => new Assignment(DtoClasses.Agent, property)),
        .. ContactProperties.Select(property => new Assignment(DtoClasses.Client, property)),
        .. ContactProperties.Select(property => new Assignment(DtoClasses.Company, property)),
        new(DtoClasses.Item, Props.Agent, Creates: DtoClasses.Agent),
        new(DtoClasses.Item, Props.Number),
        new(DtoClasses.Item, Props.Description),
        new(DtoClasses.Item, Props.ListedDate),
        new(DtoClasses.Item, Props.Price),
        new(DtoClasses.Item, Props.Region),
        new(DtoClasses.Item, Props.ItemCategory),
        new(DtoClasses.Item, Props.Name),
        new(DtoClasses.RetailItem, Props.Sale, Creates: DtoClasses.RetailSaleDetail, CreateAs: true),
        new(DtoClasses.TenderItem, Props.ClosureDate),
        new(DtoClasses.TenderItem, Props.Sale, Creates: DtoClasses.SaleDetail),
        new(DtoClasses.Sale, Props.AgentCommission),
        new(DtoClasses.Sale, Props.Client, Creates: DtoClasses.Client),
        new(DtoClasses.Sale, Props.Item, Creates: DtoClasses.Item),
        new(DtoClasses.RetailSale, Props.Timestamp),
        new(DtoClasses.TenderSale, Props.Tender, Creates: DtoClasses.Tender),
        new(DtoClasses.SaleDetail, Props.Client, Creates: DtoClasses.Client),
        new(DtoClasses.SaleDetail, Props.AgentCommission),
        new(DtoClasses.RetailSaleDetail, Props.SoldDate),
        new(DtoClasses.RetailSaleDetail, Props.Price),
        new(DtoClasses.Tender, Props.Client, Creates: DtoClasses.Client),
        new(DtoClasses.Tender, Props.Offer),
        new(DtoClasses.Tender, Props.TenderItem, Creates: DtoClasses.TenderItem),
        new(DtoClasses.Tender, Props.Timestamp),
        new(DtoClasses.Name, Props.Name),
        new(DtoClasses.Photo, Props.Photo),
        new(DtoClasses.Result, Props.AllErrors),
        new(DtoClasses.Result, Props.AllWarnings),
        new(DtoClasses.Result, Props.StatusCode),
        new(DtoClasses.Result, Props.Guid),
    ];

    // The runtime classes each array the handlers fill receives (scm:2366-2371, 2388-2393, 2412-2426,
    // 2577-2591, 6576-6581, 6635-6640); each must be a kind of the array's item type.
    private static readonly (string Array, string[] ItemClasses)[] ArrayItems =
    [
        (DtoClasses.AgentArray, [DtoClasses.Agent]),
        (DtoClasses.ClientArray, [DtoClasses.Client]),
        (DtoClasses.ItemsArray, [DtoClasses.RetailItem, DtoClasses.TenderItem]),
        (DtoClasses.SaleArray, [DtoClasses.RetailSale, DtoClasses.TenderSale]),
        (DtoClasses.NameArray, [DtoClasses.Name]),
    ];

    private readonly OperationCatalogue _catalogue;
    private readonly FrozenDictionary<string, DtoShape> _shapes;

    /// <summary>
    /// Creates a builder over a catalogue, resolving every DTO class's members and value kinds once and
    /// checking that the catalogue defines every class, array and property the population code uses.
    /// </summary>
    /// <param name="catalogue">The operation catalogue, normally <see cref="OperationCatalogue.Default"/>.</param>
    /// <exception cref="ArgumentNullException"><paramref name="catalogue"/> is null.</exception>
    /// <exception cref="InvalidDataException">
    /// The catalogue lacks a class, array or property the population code uses, declares one with a type
    /// the code does not expect, or gives a DTO property a JADE type the tree cannot hold.
    /// </exception>
    public DtoBuilder(OperationCatalogue catalogue)
    {
        ArgumentNullException.ThrowIfNull(catalogue);
        _catalogue = catalogue;
        _shapes = catalogue.Dtos.ToFrozenDictionary(
            dto => dto.Name, dto => new DtoShape(catalogue, dto), StringComparer.Ordinal);

        ValidateAssignments();
        ValidateArrays();
    }

    /// <summary>
    /// The builder over <see cref="OperationCatalogue.Default"/>, created and validated once on first
    /// use and shared by every caller.
    /// </summary>
    /// <exception cref="InvalidDataException">The embedded catalogue is invalid or lacks what the population code uses.</exception>
    public static DtoBuilder Default => _default.Value;

    /// <summary>
    /// Creates a node of <paramref name="runtimeClass"/> in a position declared as
    /// <paramref name="declaredClass"/>, every member holding JADE's null value for its type. JADE
    /// <c>create x transient</c> is <c>Create(&lt;declared type of x&gt;)</c>, and
    /// <c>create x as C transient</c> is <c>Create(C, &lt;declared type of x&gt;)</c>.
    /// </summary>
    /// <param name="runtimeClass">A concrete catalogue DTO class, for example <c>DTORetailItem</c>.</param>
    /// <param name="declaredClass">
    /// The declared class of the position, <paramref name="runtimeClass"/> or one of its ancestors;
    /// <paramref name="runtimeClass"/> when null.
    /// </param>
    /// <returns>A new node.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="runtimeClass"/> is null.</exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="runtimeClass"/> is not a catalogue DTO or is abstract, or it is not a kind of
    /// <paramref name="declaredClass"/>.
    /// </exception>
    public DtoNode Create(string runtimeClass, string? declaredClass = null)
    {
        ArgumentNullException.ThrowIfNull(runtimeClass);
        if (!_shapes.TryGetValue(runtimeClass, out var shape))
        {
            throw new ArgumentException($"The operation catalogue has no DTO '{runtimeClass}'.", nameof(runtimeClass));
        }

        if (shape.Definition.Abstract)
        {
            throw new ArgumentException(
                $"DTO '{runtimeClass}' is abstract, so JADE cannot create an instance of it.", nameof(runtimeClass));
        }

        declaredClass ??= runtimeClass;
        if (!_catalogue.IsKindOf(runtimeClass, declaredClass))
        {
            throw new ArgumentException(
                $"DTO '{runtimeClass}' is not a kind of '{declaredClass}', so it cannot sit in a position declared as it.",
                nameof(declaredClass));
        }

        return new DtoNode(shape, declaredClass);
    }

    private void ValidateAssignments()
    {
        foreach (var assignment in Assignments)
        {
            var shape = RequireShape(assignment.Class);
            if (!shape.TryGetIndex(assignment.Property, out var index))
            {
                throw Invalid($"DTO '{assignment.Class}' has no property '{assignment.Property}', which its population code assigns.");
            }

            var declared = shape.ReferenceTypeAt(index);
            if (assignment.Creates is null)
            {
                if (declared is not null)
                {
                    throw Invalid(
                        $"DTO '{assignment.Class}' property '{assignment.Property}' is a reference to '{declared}', " +
                        "but its population code assigns it a primitive value.");
                }

                continue;
            }

            var created = RequireShape(assignment.Creates);
            var fits = declared is not null && (assignment.CreateAs
                ? _catalogue.IsKindOf(created.Name, declared)
                : string.Equals(created.Name, declared, StringComparison.Ordinal));
            if (!fits || created.Definition.Abstract)
            {
                throw Invalid(
                    $"DTO '{assignment.Class}' property '{assignment.Property}' is declared '{declared ?? "as a primitive"}', " +
                    $"but its population code creates a '{assignment.Creates}' for it.");
            }
        }
    }

    private void ValidateArrays()
    {
        foreach (var (arrayType, itemClasses) in ArrayItems)
        {
            var array = RequireArray(arrayType);
            foreach (var itemClass in itemClasses)
            {
                if (!_catalogue.IsKindOf(RequireShape(itemClass).Name, array.ItemType))
                {
                    throw Invalid(
                        $"Array '{arrayType}' holds '{array.ItemType}', but its handler adds '{itemClass}' items.");
                }
            }
        }
    }

    private DtoShape RequireShape(string dtoClass) =>
        _shapes.TryGetValue(dtoClass, out var shape)
            ? shape
            : throw Invalid($"the operation catalogue has no DTO '{dtoClass}', which the population code creates or assigns.");

    private ArrayDefinition RequireArray(string arrayType) =>
        _catalogue.TryGetArray(arrayType, out var array) && _shapes.ContainsKey(array.ItemType)
            ? array
            : throw Invalid($"the operation catalogue has no DTO array '{arrayType}' of a DTO item type.");

    private static InvalidDataException Invalid(string message) =>
        new($"DtoBuilder: {message}");


    /// <summary>
    /// A <c>DTOAgent</c> populated from <paramref name="agent"/>: REST <c>getAgent</c> (scm:2350-2351),
    /// <c>getAllAgents</c> items (scm:2369-2370) and SOAP <c>getAgent</c> (scm:6552-6553).
    /// </summary>
    /// <param name="agent">The agent.</param>
    /// <param name="declaredClass">The declared class of the position, by default <c>DTOAgent</c>.</param>
    /// <exception cref="ArgumentNullException"><paramref name="agent"/> is null.</exception>
    /// <exception cref="ArgumentException"><c>DTOAgent</c> is not a kind of <paramref name="declaredClass"/>.</exception>
    /// <exception cref="InvalidOperationException">The agent has no address.</exception>
    public DtoNode Agent(Agent agent, string declaredClass = DtoClasses.Agent)
    {
        ArgumentNullException.ThrowIfNull(agent);
        var node = Create(DtoClasses.Agent, declaredClass);
        PopulateAgent(node, agent);
        return node;
    }

    /// <summary>
    /// A <c>DTOClient</c> populated from <paramref name="client"/>: REST <c>getClient</c> (scm:2452-2453),
    /// <c>getAllClients</c> items (scm:2391-2392) and SOAP <c>getClient</c> (scm:6611-6612).
    /// </summary>
    /// <param name="client">The client.</param>
    /// <param name="declaredClass">The declared class of the position, by default <c>DTOClient</c>.</param>
    /// <exception cref="ArgumentNullException"><paramref name="client"/> is null.</exception>
    /// <exception cref="ArgumentException"><c>DTOClient</c> is not a kind of <paramref name="declaredClass"/>.</exception>
    /// <exception cref="InvalidOperationException">The client has no address.</exception>
    public DtoNode Client(Client client, string declaredClass = DtoClasses.Client)
    {
        ArgumentNullException.ThrowIfNull(client);
        var node = Create(DtoClasses.Client, declaredClass);
        PopulateClient(node, client);
        return node;
    }

    /// <summary>A <c>DTOCompany</c> populated from <paramref name="company"/>: REST <c>getCompany</c> (scm:2475-2476).</summary>
    /// <param name="company">The company.</param>
    /// <exception cref="ArgumentNullException"><paramref name="company"/> is null.</exception>
    /// <exception cref="InvalidOperationException">The company has no address.</exception>
    public DtoNode Company(Company company)
    {
        ArgumentNullException.ThrowIfNull(company);
        var node = Create(DtoClasses.Company);
        PopulateCompany(node, company);
        return node;
    }

    /// <summary>
    /// A <c>DTORetailItem</c> populated from <paramref name="retailItem"/>, carrying its
    /// <c>DTORetailSaleDetail</c> when sold: REST <c>getItemByCode</c> (scm:2519-2520) and
    /// <c>getItemsByPrice</c> items (scm:2581-2582), both in <c>DTOItem</c> positions, so the node emits
    /// its type member.
    /// </summary>
    /// <param name="retailItem">The retail item.</param>
    /// <param name="declaredClass">The declared class of the position, normally <c>DTOItem</c>.</param>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    /// <exception cref="ArgumentException"><c>DTORetailItem</c> is not a kind of <paramref name="declaredClass"/>.</exception>
    /// <exception cref="InvalidOperationException">A reference the population code reads is null.</exception>
    public DtoNode RetailItem(RetailItem retailItem, string declaredClass)
    {
        ArgumentNullException.ThrowIfNull(retailItem);
        var node = Create(DtoClasses.RetailItem, declaredClass);
        PopulateRetailItem(node, retailItem);
        return node;
    }

    /// <summary>
    /// A <c>DTOTenderItem</c> populated from <paramref name="tenderItem"/>, carrying a plain
    /// <c>DTOSaleDetail</c> when sold: REST <c>getItemByCode</c> (scm:2527-2528) and
    /// <c>getItemsByPrice</c> items (scm:2588-2589), both in <c>DTOItem</c> positions.
    /// </summary>
    /// <param name="tenderItem">The tender item.</param>
    /// <param name="declaredClass">The declared class of the position, normally <c>DTOItem</c>.</param>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    /// <exception cref="ArgumentException"><c>DTOTenderItem</c> is not a kind of <paramref name="declaredClass"/>.</exception>
    /// <exception cref="InvalidOperationException">A reference the population code reads is null.</exception>
    public DtoNode TenderItem(TenderItem tenderItem, string declaredClass)
    {
        ArgumentNullException.ThrowIfNull(tenderItem);
        var node = Create(DtoClasses.TenderItem, declaredClass);
        PopulateTenderItem(node, tenderItem);
        return node;
    }

    /// <summary>
    /// The item DTO JADE's <c>isKindOf(RetailItem)</c> then <c>isKindOf(TenderItem)</c> branches build
    /// (scm:2517-2530, 2580-2591): <see cref="RetailItem"/> or <see cref="TenderItem"/>.
    /// </summary>
    /// <param name="item">The item.</param>
    /// <param name="declaredClass">The declared class of the position, normally <c>DTOItem</c>.</param>
    /// <returns>The node, or null for an item of neither kind, which <c>getItemByCode</c> answers with 500 (scm:2532-2533).</returns>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    /// <exception cref="ArgumentException">The item DTO class is not a kind of <paramref name="declaredClass"/>.</exception>
    /// <exception cref="InvalidOperationException">A reference the population code reads is null.</exception>
    public DtoNode? Item(Item item, string declaredClass)
    {
        ArgumentNullException.ThrowIfNull(item);
        return item switch
        {
            RetailItem retailItem => RetailItem(retailItem, declaredClass),
            TenderItem tenderItem => TenderItem(tenderItem, declaredClass),
            _ => null,
        };
    }

    /// <summary>
    /// A <c>DTORetailSale</c> populated from <paramref name="retailSale"/>: REST <c>getAllSales</c> items
    /// (scm:2416-2417) and <c>getSale</c> (scm:2635-2636), both in <c>DTOSale</c> positions.
    /// </summary>
    /// <param name="retailSale">The retail sale.</param>
    /// <param name="declaredClass">The declared class of the position, normally <c>DTOSale</c>.</param>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    /// <exception cref="ArgumentException"><c>DTORetailSale</c> is not a kind of <paramref name="declaredClass"/>.</exception>
    /// <exception cref="InvalidOperationException">A reference the population code reads is null.</exception>
    public DtoNode RetailSale(RetailSale retailSale, string declaredClass)
    {
        ArgumentNullException.ThrowIfNull(retailSale);
        var node = Create(DtoClasses.RetailSale, declaredClass);
        PopulateRetailSale(node, retailSale);
        return node;
    }

    /// <summary>
    /// A <c>DTOTenderSale</c> populated from <paramref name="tenderSale"/>: REST <c>getAllSales</c> items
    /// (scm:2423-2424) and <c>getSale</c> (scm:2643-2644), both in <c>DTOSale</c> positions.
    /// </summary>
    /// <param name="tenderSale">The tender sale.</param>
    /// <param name="declaredClass">The declared class of the position, normally <c>DTOSale</c>.</param>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    /// <exception cref="ArgumentException"><c>DTOTenderSale</c> is not a kind of <paramref name="declaredClass"/>.</exception>
    /// <exception cref="InvalidOperationException">A reference the population code reads is null.</exception>
    public DtoNode TenderSale(TenderSale tenderSale, string declaredClass)
    {
        ArgumentNullException.ThrowIfNull(tenderSale);
        var node = Create(DtoClasses.TenderSale, declaredClass);
        PopulateTenderSale(node, tenderSale);
        return node;
    }

    /// <summary>
    /// The sale DTO JADE's <c>isKindOf(RetailSale)</c> then <c>isKindOf(TenderSale)</c> branches build
    /// (scm:2415-2426, 2633-2646): <see cref="RetailSale"/> or <see cref="TenderSale"/>.
    /// </summary>
    /// <param name="sale">The sale.</param>
    /// <param name="declaredClass">The declared class of the position, normally <c>DTOSale</c>.</param>
    /// <returns>The node, or null for a sale of neither kind, which <c>getSale</c> answers with 500 (scm:2648-2649).</returns>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    /// <exception cref="ArgumentException">The sale DTO class is not a kind of <paramref name="declaredClass"/>.</exception>
    /// <exception cref="InvalidOperationException">A reference the population code reads is null.</exception>
    public DtoNode? Sale(Sale sale, string declaredClass)
    {
        ArgumentNullException.ThrowIfNull(sale);
        return sale switch
        {
            RetailSale retailSale => RetailSale(retailSale, declaredClass),
            TenderSale tenderSale => TenderSale(tenderSale, declaredClass),
            _ => null,
        };
    }

    /// <summary>A <c>DTOTender</c> populated from <paramref name="tender"/> (scm:2305-2325).</summary>
    /// <param name="tender">The tender.</param>
    /// <param name="declaredClass">The declared class of the position, by default <c>DTOTender</c>.</param>
    /// <exception cref="ArgumentNullException"><paramref name="tender"/> is null.</exception>
    /// <exception cref="ArgumentException"><c>DTOTender</c> is not a kind of <paramref name="declaredClass"/>.</exception>
    /// <exception cref="InvalidOperationException">A reference the population code reads is null.</exception>
    public DtoNode Tender(Tender tender, string declaredClass = DtoClasses.Tender)
    {
        ArgumentNullException.ThrowIfNull(tender);
        var node = Create(DtoClasses.Tender, declaredClass);
        PopulateTender(node, tender);
        return node;
    }

    /// <summary>
    /// A <c>DTOName</c> holding only <c>name</c>, as SOAP <c>getAgentNames</c> and <c>getClientNames</c>
    /// build it (scm:6579-6580, 6638-6639); <c>guid</c> keeps the null string.
    /// </summary>
    /// <param name="name">The agent or client name; null is stored as the null string.</param>
    public DtoNode Name(string name) => NameNode(name, DtoClasses.Name);

    /// <summary>
    /// A <c>DTOPhoto</c> as SOAP <c>getPhoto</c> builds it (scm:6671-6676): <c>photo</c> holds the given
    /// bytes, uncopied, or stays null; <c>guid</c> keeps the null string.
    /// </summary>
    /// <param name="photo">The photo bytes of the client's last retail sale with a photo, or null for none.</param>
    public DtoNode Photo(byte[]? photo)
    {
        var node = Create(DtoClasses.Photo);
        node.Set(Props.Photo, photo);
        return node;
    }

    /// <summary>
    /// A <c>DTOResult</c> as the write operations build it: <c>allErrors</c> and <c>allWarnings</c> as
    /// copies in the given order, <c>statusCode</c>, and <c>guid</c> only when given.
    /// </summary>
    /// <remarks>
    /// The handlers decide the outcome. On success they pass the model object's guid (scm:2686-2687,
    /// 6748-6749); on failure they pass the TA errors in order (scm:2679-2680, 6743-6744) or the
    /// not-found message (scm:6721-6722) and no guid, so an error result keeps the null string, which the
    /// N1 pairwise rule relies on.
    /// </remarks>
    /// <param name="statusCode">The status: the HTTP status on REST, the SOAP status constant on SOAP.</param>
    /// <param name="errors">The errors, or null for none.</param>
    /// <param name="warnings">The warnings, or null for none.</param>
    /// <param name="guid">The created or updated object's guid, or null to leave <c>guid</c> unset.</param>
    public DtoNode Result(
        int statusCode,
        IEnumerable<string>? errors = null,
        IEnumerable<string>? warnings = null,
        string? guid = null)
    {
        var node = Create(DtoClasses.Result);
        if (errors is not null)
        {
            node.Set(Props.AllErrors, errors);
        }

        if (warnings is not null)
        {
            node.Set(Props.AllWarnings, warnings);
        }

        node.Set(Props.StatusCode, statusCode);
        if (guid is not null)
        {
            node.Set(Props.Guid, guid);
        }

        return node;
    }

    /// <summary>
    /// A DTO array of <paramref name="items"/> in the order given, as JADE's <c>add</c> loops build it.
    /// The handler supplies the ordered enumeration, for example <c>Company.AllItems</c> or
    /// <c>Company.AllSalesByItem</c>.
    /// </summary>
    /// <param name="arrayType">A catalogue DTO array, for example <c>DTOItemsArray</c>.</param>
    /// <param name="items">The items, each created with the array's item type as its declared class.</param>
    /// <returns>The array; empty when <paramref name="items"/> is.</returns>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="arrayType"/> is not a catalogue DTO array, or an item is null, is not a kind of the
    /// item type, or is declared as another class.
    /// </exception>
    public DtoArray Array(string arrayType, IEnumerable<DtoNode> items)
    {
        ArgumentNullException.ThrowIfNull(arrayType);
        ArgumentNullException.ThrowIfNull(items);
        if (!_catalogue.TryGetArray(arrayType, out var definition) || !_shapes.ContainsKey(definition.ItemType))
        {
            throw new ArgumentException($"The operation catalogue has no DTO array '{arrayType}'.", nameof(arrayType));
        }

        var list = new List<DtoNode>();
        foreach (var item in items)
        {
            if (item is null)
            {
                throw new ArgumentException($"Array '{arrayType}' cannot hold a null item.", nameof(items));
            }

            if (!string.Equals(item.DeclaredClass, definition.ItemType, StringComparison.Ordinal)
                || !_catalogue.IsKindOf(item.RuntimeClass, definition.ItemType))
            {
                throw new ArgumentException(
                    $"Array '{arrayType}' holds '{definition.ItemType}' items; a '{item.RuntimeClass}' node " +
                    $"declared '{item.DeclaredClass}' cannot be added to it.",
                    nameof(items));
            }

            list.Add(item);
        }

        return new DtoArray(definition, list.AsReadOnly());
    }

    /// <summary>A <c>DTOAgentArray</c> of the agents in the order given (REST <c>getAllAgents</c>, scm:2366-2372).</summary>
    /// <param name="agents">The agents, in <c>Company.AllAgents</c> order.</param>
    /// <exception cref="ArgumentNullException"><paramref name="agents"/> or one of them is null.</exception>
    /// <exception cref="InvalidOperationException">An agent has no address.</exception>
    public DtoArray AgentArray(IEnumerable<Agent> agents)
    {
        ArgumentNullException.ThrowIfNull(agents);
        var itemType = RequireArray(DtoClasses.AgentArray).ItemType;
        return Array(DtoClasses.AgentArray, agents.Select(agent => Agent(agent, itemType)));
    }

    /// <summary>A <c>DTOClientArray</c> of the clients in the order given (REST <c>getAllClients</c>, scm:2388-2394).</summary>
    /// <param name="clients">The clients, in <c>Company.AllClients</c> order.</param>
    /// <exception cref="ArgumentNullException"><paramref name="clients"/> or one of them is null.</exception>
    /// <exception cref="InvalidOperationException">A client has no address.</exception>
    public DtoArray ClientArray(IEnumerable<Client> clients)
    {
        ArgumentNullException.ThrowIfNull(clients);
        var itemType = RequireArray(DtoClasses.ClientArray).ItemType;
        return Array(DtoClasses.ClientArray, clients.Select(client => Client(client, itemType)));
    }

    /// <summary>
    /// A <c>DTONameArray</c> of the names in the order given (SOAP <c>getAgentNames</c> and
    /// <c>getClientNames</c>, scm:6576-6582, 6635-6641).
    /// </summary>
    /// <param name="names">The agent or client names, in <c>Company.AllAgents</c> or <c>AllClients</c> order.</param>
    /// <exception cref="ArgumentNullException"><paramref name="names"/> is null.</exception>
    public DtoArray NameArray(IEnumerable<string> names)
    {
        ArgumentNullException.ThrowIfNull(names);
        var itemType = RequireArray(DtoClasses.NameArray).ItemType;
        return Array(DtoClasses.NameArray, names.Select(name => NameNode(name, itemType)));
    }


    /// <summary><c>DTOObject::populateFromObject</c> (scm:2087-2094): <c>guid := pPersistentModel.guid</c>.</summary>
    private static void PopulateObject(DtoNode node, PersistentModel model) => node.Set(Props.Guid, model.Guid);

    /// <summary><c>DTOAgent::populateFromObject</c> (scm:2096-2115): inherits, then name and address members.</summary>
    private static void PopulateAgent(DtoNode node, Agent agent)
    {
        PopulateObject(node, agent);
        AssignContact(node, agent, agent.Name, agent.MyAddress);
    }

    /// <summary><c>DTOClient::populateFromObject</c> (scm:2116-2135): inherits, then name and address members.</summary>
    private static void PopulateClient(DtoNode node, Client client)
    {
        PopulateObject(node, client);
        AssignContact(node, client, client.Name, client.MyAddress);
    }

    /// <summary><c>DTOCompany::populateFromObject</c> (scm:2136-2155): inherits, then name and address members.</summary>
    private static void PopulateCompany(DtoNode node, Company company)
    {
        PopulateObject(node, company);
        AssignContact(node, company, company.Name, company.MyAddress);
    }

    /// <summary>
    /// <c>DTOItem::populateFromObject</c> (scm:2156-2178): inherits, creates and populates
    /// <c>agent</c>, then the item members. <c>categoryPrefix</c> and <c>photo</c> are never assigned
    /// there, so they keep the null string and null.
    /// </summary>
    private void PopulateItem(DtoNode node, Item item)
    {
        PopulateObject(node, item);

        // create self.agent transient; self.agent.populateFromObject(pItem.myAgent) (scm:2166-2167)
        PopulateAgent(CreateIn(node, Props.Agent, DtoClasses.Agent), Required(item.MyAgent, item, "myAgent"));

        node.Set(Props.Number, item.CodeNumber);
        node.Set(Props.Description, item.Description);
        node.Set(Props.ListedDate, item.ListedDate);
        node.Set(Props.Price, item.Price);
        node.Set(Props.Region, Required(item.MyRegion, item, "myRegion").Name);
        node.Set(Props.ItemCategory, Required(item.MyItemCategory, item, "myItemCategory").Name);
        node.Set(Props.Name, item.Name);
    }

    /// <summary>
    /// <c>DTORetailItem::populateFromObject</c> (scm:2179-2195): inherits, then for a sold item
    /// <c>create self.sale as DTORetailSaleDetail transient</c> populated from
    /// <c>mySale.RetailSale</c> (scm:2189-2192).
    /// </summary>
    private void PopulateRetailItem(DtoNode node, RetailItem retailItem)
    {
        PopulateItem(node, retailItem);

        if (retailItem.MySale is { } sale)
        {
            PopulateRetailSaleDetail(
                CreateIn(node, Props.Sale, DtoClasses.RetailSaleDetail),
                TypeGuard<RetailSale>(sale, retailItem, "mySale"));
        }
    }

    /// <summary>
    /// <c>DTOTenderItem::populateFromObject</c> (scm:2196-2214): inherits, assigns
    /// <c>closureDate</c>, then for a sold item <c>create self.sale transient</c>, a plain
    /// <c>DTOSaleDetail</c> (never a <c>DTOTenderSaleDetail</c>), populated from
    /// <c>mySale.TenderSale</c> (scm:2208-2211).
    /// </summary>
    private void PopulateTenderItem(DtoNode node, TenderItem tenderItem)
    {
        PopulateItem(node, tenderItem);

        node.Set(Props.ClosureDate, tenderItem.ClosureDate);

        if (tenderItem.MySale is { } sale)
        {
            PopulateSaleDetail(
                CreateIn(node, Props.Sale, DtoClasses.SaleDetail),
                TypeGuard<TenderSale>(sale, tenderItem, "mySale"));
        }
    }

    /// <summary>
    /// <c>DTOSale::populateFromObject</c> (scm:2215-2233): inherits, assigns
    /// <c>agentCommission</c>, then creates and populates <c>client</c> and <c>item</c>.
    /// </summary>
    /// <remarks>
    /// <c>create self.item transient</c> creates the declared plain <c>DTOItem</c> (scm:2229), and JADE
    /// dispatches its <c>populateFromObject</c> on that class, so the nested item is populated by
    /// <see cref="PopulateItem"/> alone: no <c>sale</c>, no <c>closureDate</c> and no type member,
    /// although the sold item is a retail or tender item.
    /// </remarks>
    private void PopulateSale(DtoNode node, Sale sale)
    {
        PopulateObject(node, sale);

        node.Set(Props.AgentCommission, sale.AgentCommission);

        // create self.client transient; self.client.populateFromObject(pSale.myClient) (scm:2226-2227)
        PopulateClient(CreateIn(node, Props.Client, DtoClasses.Client), Required(sale.MyClient, sale, "myClient"));

        // create self.item transient; self.item.populateFromObject(pSale.myItem) (scm:2229-2230)
        PopulateItem(CreateIn(node, Props.Item, DtoClasses.Item), Required(sale.MyItem, sale, "myItem"));
    }

    /// <summary><c>DTORetailSale::populateFromObject</c> (scm:2234-2246): inherits, then <c>timestamp := dateSold</c>.</summary>
    private void PopulateRetailSale(DtoNode node, RetailSale retailSale)
    {
        PopulateSale(node, retailSale);

        node.Set(Props.Timestamp, retailSale.DateSold);
    }

    /// <summary>
    /// <c>DTOTenderSale::populateFromObject</c> (scm:2247-2260): inherits, then creates and populates
    /// <c>tender</c>. Its own <c>offer</c> is never assigned and keeps 0.
    /// </summary>
    private void PopulateTenderSale(DtoNode node, TenderSale tenderSale)
    {
        PopulateSale(node, tenderSale);

        // create self.tender transient; self.tender.populateFromObject(pTenderSale.myTender) (scm:2256-2257)
        PopulateTender(
            CreateIn(node, Props.Tender, DtoClasses.Tender),
            Required(tenderSale.MyTender, tenderSale, "myTender"));
    }

    /// <summary>
    /// <c>DTOSaleDetail::populateFromObject</c> (scm:2261-2276): inherits, so <c>guid</c> is the sale's,
    /// then creates and populates <c>client</c> and assigns <c>agentCommission</c>.
    /// </summary>
    private void PopulateSaleDetail(DtoNode node, Sale sale)
    {
        PopulateObject(node, sale);

        // create self.client transient; self.client.populateFromObject(pSale.myClient) (scm:2270-2271)
        PopulateClient(CreateIn(node, Props.Client, DtoClasses.Client), Required(sale.MyClient, sale, "myClient"));

        node.Set(Props.AgentCommission, sale.AgentCommission);
    }


    /// <summary>
    /// <c>DTORetailSaleDetail::populateFromObject</c> (scm:2277-2290): inherits, then
    /// <c>soldDate := dateSold</c> and <c>price := salePrice</c>.
    /// </summary>
    private void PopulateRetailSaleDetail(DtoNode node, RetailSale retailSale)
    {
        PopulateSaleDetail(node, retailSale);

        node.Set(Props.SoldDate, retailSale.DateSold);
        node.Set(Props.Price, retailSale.SalePrice);
    }

    /// <summary>
    /// <c>DTOTender::populateFromObject</c> (scm:2305-2325): inherits, creates and populates
    /// <c>client</c>, assigns <c>offer</c>, creates and populates <c>tenderItem</c> (so a sold tender
    /// item carries its plain <c>DTOSaleDetail</c>), then <c>timestamp := dateTendered</c>.
    /// </summary>
    private void PopulateTender(DtoNode node, Tender tender)
    {
        PopulateObject(node, tender);

        // create self.client transient; self.client.populateFromObject(pTender.myClient) (scm:2314-2315)
        PopulateClient(CreateIn(node, Props.Client, DtoClasses.Client), Required(tender.MyClient, tender, "myClient"));

        node.Set(Props.Offer, tender.Offer);

        // create self.tenderItem transient; self.tenderItem.populateFromObject(pTender.myTenderItem) (scm:2319-2320)
        PopulateTenderItem(
            CreateIn(node, Props.TenderItem, DtoClasses.TenderItem),
            Required(tender.MyTenderItem, tender, "myTenderItem"));

        node.Set(Props.Timestamp, tender.DateTendered);
    }

    // The name and address assignments the agent, client and company populate methods share
    // (scm:2105-2112, 2125-2132, 2145-2152), in source order; the DTO member is "website" and the
    // address attribute "webSite".
    private static void AssignContact(DtoNode node, PersistentModel owner, string name, Address? myAddress)
    {
        node.Set(Props.Name, name);

        var address = Required(myAddress, owner, "myAddress");
        node.Set(Props.Street, address.Street);
        node.Set(Props.City, address.City);
        node.Set(Props.Country, address.Country);
        node.Set(Props.Phone, address.Phone);
        node.Set(Props.Fax, address.Fax);
        node.Set(Props.Email, address.Email);
        node.Set(Props.Website, address.WebSite);
    }

    private DtoNode NameNode(string? name, string declaredClass)
    {
        var node = Create(DtoClasses.Name, declaredClass);
        node.Set(Props.Name, name);
        return node;
    }

    // JADE `create self.<property> [as <runtimeClass>] transient`: a new node of runtimeClass in the
    // position the catalogue declares for the property, assigned before it is populated, as in JADE.
    private DtoNode CreateIn(DtoNode owner, string property, string runtimeClass)
    {
        var node = Create(runtimeClass, owner.Shape.ReferenceTypeOf(property));
        owner.Set(property, node);
        return node;
    }

    // A null reference read by a populate method is where JADE raises a null-object exception; the
    // stand-in raises too rather than invent a value. Seed and TA invariants make it unreachable.
    private static T Required<T>(T? value, PersistentModel owner, string reference)
        where T : class =>
        value ?? throw new InvalidOperationException(
            $"{owner.GetType().Name} (guid '{owner.Guid}', oid {owner.Oid}) has no {reference}; JADE raises a " +
            "null-object exception populating its DTO, and the stand-in does not invent a value.");

    // JADE's type guard `mySale.RetailSale` / `mySale.TenderSale` raises for a sale of another class.
    private static T TypeGuard<T>(Sale sale, PersistentModel owner, string reference)
        where T : Sale =>
        sale as T ?? throw new InvalidOperationException(
            $"{owner.GetType().Name} (guid '{owner.Guid}', oid {owner.Oid}) has a {sale.GetType().Name} as " +
            $"{reference}, where the JADE type guard requires a {typeof(T).Name}.");

    // One entry of Assignments: Class's population code assigns Property; for a reference it creates
    // a Creates node, of the declared type or, with CreateAs, of a subclass of it.
    private readonly record struct Assignment(string Class, string Property, string? Creates = null, bool CreateAs = false);

    // The DTO and array classes the population code and the handler factories use. DTOError and
    // DTOTenderSaleDetail are absent on purpose: no in-scope method body creates either.
    private static class DtoClasses
    {
        public const string Object = "DTOObject";
        public const string Agent = "DTOAgent";
        public const string Client = "DTOClient";
        public const string Company = "DTOCompany";
        public const string Item = "DTOItem";
        public const string RetailItem = "DTORetailItem";
        public const string TenderItem = "DTOTenderItem";
        public const string Sale = "DTOSale";
        public const string RetailSale = "DTORetailSale";
        public const string TenderSale = "DTOTenderSale";
        public const string SaleDetail = "DTOSaleDetail";
        public const string RetailSaleDetail = "DTORetailSaleDetail";
        public const string Tender = "DTOTender";
        public const string Result = "DTOResult";
        public const string Name = "DTOName";
        public const string Photo = "DTOPhoto";

        public const string AgentArray = "DTOAgentArray";
        public const string ClientArray = "DTOClientArray";
        public const string ItemsArray = "DTOItemsArray";
        public const string SaleArray = "DTOSaleArray";
        public const string NameArray = "DTONameArray";
    }

    // The DTO property names the ported JADE assignments use, exactly as the reference schema
    // declares them; the constructor checks each one through Assignments.
    private static class Props
    {
        public const string Guid = "guid";
        public const string Name = "name";
        public const string Street = "street";
        public const string City = "city";
        public const string Country = "country";
        public const string Phone = "phone";
        public const string Fax = "fax";
        public const string Email = "email";
        public const string Website = "website";
        public const string Agent = "agent";
        public const string Number = "number";
        public const string Description = "description";
        public const string ListedDate = "listedDate";
        public const string Price = "price";
        public const string Region = "region";
        public const string ItemCategory = "itemCategory";
        public const string Sale = "sale";
        public const string ClosureDate = "closureDate";
        public const string AgentCommission = "agentCommission";
        public const string Client = "client";
        public const string Item = "item";
        public const string Timestamp = "timestamp";
        public const string Tender = "tender";
        public const string SoldDate = "soldDate";
        public const string Offer = "offer";
        public const string TenderItem = "tenderItem";
        public const string Photo = "photo";
        public const string AllErrors = "allErrors";
        public const string AllWarnings = "allWarnings";
        public const string StatusCode = "statusCode";
    }
}
