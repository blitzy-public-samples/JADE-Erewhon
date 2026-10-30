using System.Buffers;
using System.Text.Encodings.Web;
using System.Text.Json;
using Erewhon.Contracts.Wire;
using Erewhon.StandIn.Dto;

namespace Erewhon.StandIn.Rest;

/// <summary>
/// Writes the neutral DTO tree that <see cref="DtoBuilder"/> builds as the JSON body JADE 2022's default
/// <c>.json</c> REST format is assumed to produce (KL-REST-FORMAT): Microsoft JSON conventions, the JADE
/// property names in catalogue order with <c>guid</c> included, null members included, <c>"__type"</c>
/// as the first member wherever the runtime class differs from the declared class, dates as
/// <c>"\/Date(ms)\/"</c>, <c>Decimal</c> values with two decimals and <c>Binary</c> as a byte array.
/// </summary>
/// <remarks>
/// <para>
/// The output is the permanent equivalence baseline, so the writer is a pure function of the tree: it
/// reads no clock, state or configuration, formats every wire value through
/// <see cref="JadeWireFormats"/>, and writes UTF-8 without a byte order mark, without indentation and
/// without whitespace between tokens. The same tree always yields the same bytes.
/// </para>
/// <para>
/// Class names, member names, member order and value kinds all come from the tree, which carries the
/// operation catalogue's definitions; nothing here names a DTO class or property.
/// </para>
/// <para>
/// Thread-safe: each thread serialises into its own cached buffer.
/// </para>
/// </remarks>
public static class JadeJsonWriter
{
    /// <summary>The content type of every body this writer produces.</summary>
    public const string ContentType = "application/json; charset=utf-8";

    // A buffer that grew past this while writing one body is released rather than kept by its thread.
    private const int MaxRetainedBufferBytes = 1024 * 1024;

    // The relaxed encoder keeps ' and other HTML-sensitive characters literal, as in
    // "12b St Michael's Place", and writes non-ASCII text as raw UTF-8; ", \ and control characters
    // are still escaped.
    private static readonly JsonWriterOptions Options = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Indented = false,
        SkipValidation = false,
    };

    // The output buffer each thread reuses for every body it serialises.
    [ThreadStatic]
    private static ArrayBufferWriter<byte>? t_buffer;

    /// <summary>
    /// Serialises a REST result: a DTO object, a DTO array, or the literal <c>null</c> a JADE method
    /// writes when it returns no object.
    /// </summary>
    /// <param name="value">A <see cref="DtoNode"/>, a <see cref="DtoArray"/> or null.</param>
    /// <param name="declaredType">
    /// The declared JADE class of the root position, the operation's
    /// <c>OperationDescriptor.ReturnType</c>: for example <c>DTOItem</c> for <c>getItemByCode</c>, so a
    /// <c>DTORetailItem</c> root carries <c>"__type"</c>. An array carries no type member, so for an array
    /// root only its items' declared class, the array's item type, decides.
    /// </param>
    /// <returns>The UTF-8 body, without a byte order mark; the four bytes <c>null</c> for a null value.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="declaredType"/> is null.</exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="declaredType"/> is empty, or <paramref name="value"/> is neither a
    /// <see cref="DtoNode"/>, a <see cref="DtoArray"/> nor null.
    /// </exception>
    /// <exception cref="InvalidOperationException">A member holds a value its value kind cannot be written as.</exception>
    public static byte[] Serialize(object? value, string declaredType)
    {
        ArgumentException.ThrowIfNullOrEmpty(declaredType);
        if (value is not (null or DtoNode or DtoArray))
        {
            throw new ArgumentException(
                $"The JADE JSON writer writes a {nameof(DtoNode)}, a {nameof(DtoArray)} or null, not a {value.GetType().Name}.",
                nameof(value));
        }

        // Taken off the thread while in use, so a nested call on the same thread never shares it; reset
        // because a write that threw part-way leaves its bytes behind.
        var buffer = t_buffer ?? new ArrayBufferWriter<byte>();
        t_buffer = null;
        buffer.ResetWrittenCount();
        try
        {
            using (var writer = new Utf8JsonWriter(buffer, Options))
            {
                switch (value)
                {
                    case DtoNode node:
                        WriteNode(writer, node, declaredType);
                        break;
                    case DtoArray array:
                        WriteArray(writer, array);
                        break;
                    default:
                        writer.WriteNullValue();
                        break;
                }
            }

            return buffer.WrittenSpan.ToArray();
        }
        finally
        {
            if (buffer.Capacity <= MaxRetainedBufferBytes)
            {
                t_buffer = buffer;
            }
        }
    }

    /// <summary>
    /// Writes a REST result as the whole response: the status, the <see cref="ContentType"/> header, the
    /// content length and the body <see cref="Serialize"/> produces. No other header is set.
    /// </summary>
    /// <param name="response">The response, not yet started.</param>
    /// <param name="statusCode">The HTTP status the JADE method sets, for example 200, 404 or 201.</param>
    /// <param name="value">A <see cref="DtoNode"/>, a <see cref="DtoArray"/> or null.</param>
    /// <param name="declaredType">The declared JADE class of the root position, as for <see cref="Serialize"/>.</param>
    /// <param name="cancellationToken">Cancels the body write when the request is aborted.</param>
    /// <returns>A task that completes when the body has been written.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="response"/> or <paramref name="declaredType"/> is null.</exception>
    /// <exception cref="ArgumentException">As for <see cref="Serialize"/>.</exception>
    /// <exception cref="InvalidOperationException">As for <see cref="Serialize"/>, or the response has already started.</exception>
    public static async Task WriteAsync(
        HttpResponse response,
        int statusCode,
        object? value,
        string declaredType,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(response);

        // Serialised before the response is touched, so a failure leaves it free for the error handler.
        var body = Serialize(value, declaredType);

        response.StatusCode = statusCode;
        response.ContentType = ContentType;
        response.ContentLength = body.Length;
        await response.Body.WriteAsync(body, cancellationToken).ConfigureAwait(false);
    }

    // One DTO object in a position declared as declaredClass: the root's declared type, the array's
    // item type for an array item, or the member's declared JADE type for a reference. This positional
    // class decides "__type"; DtoNode.DeclaredClass encodes the same rule but does not override it.
    private static void WriteNode(Utf8JsonWriter writer, DtoNode node, string declaredClass)
    {
        writer.WriteStartObject();

        if (!string.Equals(node.RuntimeClass, declaredClass, StringComparison.Ordinal))
        {
            writer.WriteString(JadeWireFormats.TypeMemberName, node.RuntimeClass);
        }

        var members = node.Members;
        for (var index = 0; index < members.Count; index++)
        {
            var member = members[index];
            writer.WritePropertyName(member.Property.Name);
            WriteValue(writer, node, member);
        }

        writer.WriteEndObject();
    }

    // A DTO array is a plain JSON array of its items in stored order, each item in a position declared
    // as the array's item type; an empty array is [].
    private static void WriteArray(Utf8JsonWriter writer, DtoArray array)
    {
        writer.WriteStartArray();

        var items = array.Items;
        for (var index = 0; index < items.Count; index++)
        {
            WriteNode(writer, items[index], array.ItemDeclaredClass);
        }

        writer.WriteEndArray();
    }

    // Encodes one member by its value kind, which the tree derives from the catalogue JadeType. A null
    // value writes JADE's null value for the kind: "" for a String, 0 for an Integer, 0.00 for a
    // Decimal, false for a Boolean, [] for a StringArray (an exclusive collection always exists), and
    // null for a Date, TimeStamp, Binary or reference.
    private static void WriteValue(Utf8JsonWriter writer, DtoNode owner, DtoMember member)
    {
        switch (member.Kind, member.Value)
        {
            case (DtoValueKind.String, string text):
                writer.WriteStringValue(text);
                return;
            case (DtoValueKind.String, null):
                writer.WriteStringValue(string.Empty);
                return;
            case (DtoValueKind.Integer, int number):
                writer.WriteNumberValue(number);
                return;
            case (DtoValueKind.Integer, null):
                writer.WriteNumberValue(0);
                return;
            case (DtoValueKind.Decimal, decimal amount):
                JadeWireFormats.WriteDecimal2(writer, amount);
                return;
            case (DtoValueKind.Decimal, null):
                JadeWireFormats.WriteDecimal2(writer, decimal.Zero);
                return;
            case (DtoValueKind.Boolean, bool flag):
                writer.WriteBooleanValue(flag);
                return;
            case (DtoValueKind.Boolean, null):
                writer.WriteBooleanValue(false);
                return;
            case (DtoValueKind.Date, DateOnly date):
                writer.WriteRawValue(JadeWireFormats.FormatMicrosoftJsonDateLiteral(date), skipInputValidation: false);
                return;
            case (DtoValueKind.TimeStamp, DateTime timestamp):
                writer.WriteRawValue(JadeWireFormats.FormatMicrosoftJsonDateLiteral(timestamp), skipInputValidation: false);
                return;
            case (DtoValueKind.Binary, byte[] bytes):
                JadeWireFormats.WriteByteArray(writer, bytes);
                return;
            case (DtoValueKind.StringArray, IEnumerable<string?> strings):
                WriteStringArray(writer, strings);
                return;
            case (DtoValueKind.StringArray, null):
                writer.WriteStartArray();
                writer.WriteEndArray();
                return;
            case (DtoValueKind.Reference, DtoNode nested):
                WriteNode(writer, nested, member.Property.JadeType);
                return;
            case (DtoValueKind.Date or DtoValueKind.TimeStamp or DtoValueKind.Binary or DtoValueKind.Reference, null):
                writer.WriteNullValue();
                return;
        }

        throw new InvalidOperationException(
            $"DTO '{owner.RuntimeClass}' member '{member.Property.Name}' (JADE type '{member.Property.JadeType}', " +
            $"kind {member.Kind}) holds {(member.Value is null ? "null" : $"a value of type {member.Value.GetType().Name}")}, " +
            "which the JADE JSON writer cannot write.");
    }

    private static void WriteStringArray(Utf8JsonWriter writer, IEnumerable<string?> strings)
    {
        writer.WriteStartArray();
        foreach (var entry in strings)
        {
            writer.WriteStringValue(entry ?? string.Empty);
        }

        writer.WriteEndArray();
    }
}
