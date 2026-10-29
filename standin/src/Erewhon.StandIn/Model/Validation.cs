using System.Text.RegularExpressions;

namespace Erewhon.StandIn.Model;

/// <summary>
/// JADE <c>ValidationType_*</c> constants (<c>Erewhon/ErewhonModelSchema.scm</c> 2606-2609): the kind
/// of persist a transaction agent validates for.
/// </summary>
public enum ValidationType
{
    /// <summary><c>ValidationType_Create</c>.</summary>
    Create = 1,

    /// <summary><c>ValidationType_Update</c>.</summary>
    Update = 2,

    /// <summary><c>ValidationType_Delete</c>.</summary>
    Delete = 3,

    /// <summary><c>ValidationType_Modify</c>.</summary>
    Modify = 4,
}

/// <summary>
/// JADE <c>TransactionType_*</c> constants (<c>Erewhon/ErewhonModelSchema.scm</c> 2598-2604), the
/// argument of <see cref="ModelTA.PersistEntity"/>: delete, full persist (create or update), or a
/// positive modification code.
/// </summary>
public static class TransactionType
{
    /// <summary><c>TransactionType_Delete</c>.</summary>
    public const int Delete = -1;

    /// <summary><c>TransactionType_Persist</c>: create when the agent has no model object, else update.</summary>
    public const int Persist = 0;

    /// <summary><c>TransactionType_Modify_LockEntity</c> (base modification 1000 + 1).</summary>
    public const int ModifyLockEntity = 1001;

    /// <summary><c>TransactionType_Modify_UnlockEntity</c> (1000 + 2).</summary>
    public const int ModifyUnlockEntity = 1002;

    /// <summary><c>TransactionType_Modify_AddCommissionRateForAgent</c> (1000 + 4).</summary>
    public const int ModifyAddCommissionRateForAgent = 1004;

    /// <summary><c>TransactionType_Modify_RemoveCommissionRateForAgent</c> (1000 + 5).</summary>
    public const int ModifyRemoveCommissionRateForAgent = 1005;
}

/// <summary>
/// JADE primitive behaviours the transaction agents and the REST and SOAP handlers rely on, ported so
/// that every caller trims, rounds and validates identically.
/// </summary>
public static class JadeValues
{
    /// <summary>
    /// JADE <c>RegularExpressions.EmailRegEx</c> (<c>Erewhon/ErewhonModelSchema.scm</c> 140), verbatim.
    /// </summary>
    public const string EmailRegEx = @"^\w+([-+.']\w+)*@\w+([-.]\w+)*\.\w+([-.]\w+)*$";

    // String::trimWhiteSpace constant WhiteSpaceCharacters: Cr & Lf & Tab & Space & #'00'.
    private static readonly char[] WhiteSpaceCharacters = ['\r', '\n', '\t', ' ', '\0'];

    // JadeRegex@isMatch( email, EmailRegEx, true ) ignores case (ErewhonModelSchema.scm 9876).
    // NonBacktracking gives the same match result in linear time, so an email sent in a request
    // cannot make the shared request lock wait on regex backtracking.
    private static readonly Regex EmailPattern = new(
        EmailRegEx,
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);

    /// <summary>
    /// JADE <c>String::trimWhiteSpace</c> (<c>Erewhon/ErewhonModelSchema.scm</c> 21045-21079): strips
    /// carriage returns, line feeds, tabs, spaces and NUL characters from both ends.
    /// </summary>
    /// <returns>The trimmed text; the JADE null string (<c>""</c>) for null or all-white-space input.</returns>
    public static string TrimWhiteSpace(string? value) => value?.Trim(WhiteSpaceCharacters) ?? "";

    /// <summary>JADE <c>String::trimBlanks</c>: strips leading and trailing spaces (U+0020) only.</summary>
    /// <returns>The trimmed text; the JADE null string (<c>""</c>) for null input.</returns>
    public static string TrimBlanks(string? value) => value?.Trim(' ') ?? "";

    /// <summary>
    /// Assignment to a JADE <c>Decimal[n,2]</c>: rounds to two decimal places, half away from zero.
    /// </summary>
    /// <remarks>
    /// JADE does not document its Decimal assignment rounding; half away from zero is the assumption
    /// registered as KL-STANDIN-ROUNDING.
    /// </remarks>
    public static decimal RoundScale2(decimal value) => Math.Round(value, 2, MidpointRounding.AwayFromZero);

    /// <summary>
    /// JADE <c>JadeRegex@isMatch( email, EmailRegEx, true )</c>: true when <paramref name="email"/>
    /// matches <see cref="EmailRegEx"/>, ignoring case.
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="email"/> is null.</exception>
    public static bool IsValidEmail(string email)
    {
        ArgumentNullException.ThrowIfNull(email);
        return EmailPattern.IsMatch(email);
    }

    /// <summary>
    /// Stand-in for JADE <c>Binary::ebsIsValidPicture</c> (<c>Erewhon/ErewhonBaseSchema.scm</c>
    /// 727-761): true when the bytes begin with the signature of a picture type that method accepts.
    /// </summary>
    /// <remarks>
    /// The JADE method accepts the <c>pictureType</c> values Bitmap, Icon, Metafile, Cursor, TIF, JPEG
    /// and PNG, and nothing else; GIF is not among them, so a GIF is not a valid picture here either.
    /// JADE's own type detection is not documented, so each type is recognised by its file signature.
    /// Every seed image is a JPEG or a bitmap.
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="picture"/> is null.</exception>
    public static bool IsValidPicture(byte[] picture)
    {
        ArgumentNullException.ThrowIfNull(picture);
        ReadOnlySpan<byte> bytes = picture;
        return bytes.StartsWith(JpegSignature)
            || bytes.StartsWith(BitmapSignature)
            || bytes.StartsWith(PngSignature)
            || bytes.StartsWith(IconSignature)
            || bytes.StartsWith(CursorSignature)
            || bytes.StartsWith(TiffLittleEndianSignature)
            || bytes.StartsWith(TiffBigEndianSignature)
            || IsMetafile(bytes);
    }

    private static ReadOnlySpan<byte> JpegSignature => [0xFF, 0xD8, 0xFF];

    private static ReadOnlySpan<byte> BitmapSignature => [0x42, 0x4D];

    private static ReadOnlySpan<byte> PngSignature => [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    private static ReadOnlySpan<byte> IconSignature => [0x00, 0x00, 0x01, 0x00];

    private static ReadOnlySpan<byte> CursorSignature => [0x00, 0x00, 0x02, 0x00];

    private static ReadOnlySpan<byte> TiffLittleEndianSignature => [0x49, 0x49, 0x2A, 0x00];

    private static ReadOnlySpan<byte> TiffBigEndianSignature => [0x4D, 0x4D, 0x00, 0x2A];

    // Aldus placeable metafile key.
    private static ReadOnlySpan<byte> PlaceableMetafileSignature => [0xD7, 0xCD, 0xC6, 0x9A];

    // Standard metafile header: mtType 1 (memory) or 2 (disk), then mtHeaderSize 9 words.
    private static ReadOnlySpan<byte> MemoryMetafileSignature => [0x01, 0x00, 0x09, 0x00];

    private static ReadOnlySpan<byte> DiskMetafileSignature => [0x02, 0x00, 0x09, 0x00];

    // Enhanced metafile: record type EMR_HEADER (1), and the " EMF" signature at offset 40.
    private static ReadOnlySpan<byte> EnhancedMetafileRecordType => [0x01, 0x00, 0x00, 0x00];

    private static ReadOnlySpan<byte> EnhancedMetafileSignature => [0x20, 0x45, 0x4D, 0x46];

    private const int EnhancedMetafileSignatureOffset = 40;

    private static bool IsMetafile(ReadOnlySpan<byte> bytes) =>
        bytes.StartsWith(PlaceableMetafileSignature)
        || bytes.StartsWith(MemoryMetafileSignature)
        || bytes.StartsWith(DiskMetafileSignature)
        || (bytes.StartsWith(EnhancedMetafileRecordType)
            && bytes.Length >= EnhancedMetafileSignatureOffset + EnhancedMetafileSignature.Length
            && bytes.Slice(EnhancedMetafileSignatureOffset, EnhancedMetafileSignature.Length)
                .SequenceEqual(EnhancedMetafileSignature));
}

/// <summary>
/// The JADE <c>FocusConstants</c> (<c>Erewhon/ErewhonModelSchema.scm</c> 80-110) the transaction
/// agents name as the field in error. The focus drives the JADE GUI only; no REST or SOAP response
/// carries it.
/// </summary>
internal static class FocusConstants
{
    public const string Agent = "FOCUS: Agent";
    public const string AgentCommission = "FOCUS: AgentCommission";
    public const string City = "FOCUS: City";
    public const string Client = "FOCUS: Client";
    public const string CodeNumber = "FOCUS: CodeNumber";
    public const string CodePrefix = "FOCUS: CodePrefix";
    public const string Country = "FOCUS: Country";
    public const string Date = "FOCUS: Date";
    public const string Description = "FOCUS: Description";
    public const string Email = "FOCUS: Email";
    public const string Item = "FOCUS: Item";
    public const string ItemCategory = "FOCUS: ItemCategory";
    public const string ItemDescription = "FOCUS: ItemDescription";
    public const string ItemName = "FOCUS: ItemName";
    public const string Name = "FOCUS: Name";
    public const string Offer = "FOCUS: Offer";
    public const string Percentage = "FOCUS: Percentage";
    public const string Phone = "FOCUS: Phone";
    public const string Photo = "FOCUS: Photo";
    public const string Price = "FOCUS: Price";
    public const string Region = "FOCUS: Region";
    public const string Street = "FOCUS: Street";
    public const string Tender = "FOCUS: Tender";
    public const string TenderItem = "FOCUS: TenderItem";
    public const string Timestamp = "FOCUS: Timestamp";
}

/// <summary>
/// A transaction agent that owns an <see cref="AddressTA"/>: JADE <c>myAddressTA</c> of
/// <c>AgentTA</c>, <c>ClientTA</c> and <c>CompanyTA</c>, the automatic parent side of
/// <c>AddressTA.myModelTA</c>.
/// </summary>
public interface IAddressOwnerTA
{
    /// <summary>
    /// The owned address agent. Setting either side of the pair, this property or
    /// <see cref="AddressTA.MyModelTA"/>, updates the other.
    /// </summary>
    AddressTA? MyAddressTA { get; set; }
}

/// <summary>
/// JADE <c>ModelTA</c> (<c>Erewhon/ErewhonModelSchema.scm</c> 8976-9793): the transaction agent that
/// validates a requested change and only then applies it to the model.
/// </summary>
/// <remarks>
/// <para>
/// Each persist runs the JADE sequence: clear errors and warnings, <see cref="DoPreValidate"/>
/// (trimming and defaults), <see cref="DoValidate"/>, then the subordinate agent's own pass, and only
/// when every step succeeds the commit (JADE <c>onCreate</c>, <c>onUpdate</c> or <c>onModify</c> and
/// the subordinate objects). JADE runs the subordinate pass inside the transaction and aborts it on
/// failure; here it runs before anything is written, so a failed persist changes nothing in the model
/// and consumes no guid or object number, with the same error list.
/// </para>
/// <para>
/// JADE locks (<c>lockFor*</c>, <c>checkEdition</c>) always succeed in the single-threaded stand-in,
/// so they are not ported and <see cref="Messages.CreateConflictedWithAnotherUser"/>,
/// <see cref="Messages.UpdateConflictedWithAnotherUser"/> and
/// <see cref="Messages.AnotherUserUpdatedThisEntity"/> are never emitted. The clock and new guids
/// come only from the <see cref="ModelContext"/>. Not thread-safe.
/// </para>
/// </remarks>
public abstract class ModelTA
{
    /// <summary>JADE <c>allErrors</c>: the validation errors of the last persist, in the order raised.</summary>
    public List<string> AllErrors { get; } = [];

    /// <summary>JADE <c>allWarnings</c>; no Erewhon agent raises a warning.</summary>
    public List<string> AllWarnings { get; } = [];

    /// <summary>
    /// JADE <c>focusField</c>: the <c>FOCUS: &lt;Field&gt;</c> name of the first field in error, or
    /// <c>""</c>.
    /// </summary>
    public string FocusField { get; private set; } = "";

    /// <summary>
    /// JADE <c>guid</c>: the guid the created entity takes; when <c>""</c> (the JADE null string) a new
    /// one comes from <see cref="ModelContext.NewGuid"/>. <see cref="Initialize"/> keeps it, as in JADE.
    /// </summary>
    public string Guid { get; set; } = "";

    /// <summary>JADE <c>modificationCode</c>: the code of the current modify, 0 otherwise.</summary>
    public int ModificationCode { get; protected set; }

    /// <summary>
    /// JADE <c>myModelObject</c>: the entity being updated or modified, or after a successful create
    /// the entity created; null when the next full persist is a create.
    /// </summary>
    public PersistentModel? ModelObject { get; set; }

    /// <summary>JADE <c>hasNoErrors</c>.</summary>
    public bool HasNoErrors => AllErrors.Count == 0;

    /// <summary>
    /// JADE <c>initialize</c>: clears the errors, warnings, focus, modification code and model object.
    /// <see cref="Guid"/> is kept, so a caller reusing an agent sets it for each entity.
    /// </summary>
    public virtual void Initialize()
    {
        AllErrors.Clear();
        AllWarnings.Clear();
        FocusField = "";
        ModificationCode = 0;
        ModelObject = null;
    }

    /// <summary>JADE <c>clearErrors</c>: clears the errors and focus of this and every subordinate agent.</summary>
    public void ClearErrors()
    {
        AllErrors.Clear();
        FocusField = "";
        ClearErrorsOnSubordinateTAs();
    }

    /// <summary>JADE <c>clearWarnings</c>: clears the warnings and focus of this and every subordinate agent.</summary>
    public void ClearWarnings()
    {
        AllWarnings.Clear();
        FocusField = "";
        ClearWarningsOnSubordinateTAs();
    }

    /// <summary>
    /// JADE <c>doPreValidate</c>: sanitises the agent's values (trimming, defaults) before validation.
    /// </summary>
    public virtual void DoPreValidate(ModelContext context)
    {
    }

    /// <summary>
    /// JADE <c>doValidate</c>: adds an error for each rule the agent's values break, in source order.
    /// </summary>
    /// <returns>True when the agent has no errors.</returns>
    public virtual bool DoValidate(ValidationType validationType, ModelContext context) => HasNoErrors;

    /// <summary>
    /// JADE <c>persistEntity</c>: <see cref="TransactionType.Persist"/> creates the entity when
    /// <see cref="ModelObject"/> is null and updates it otherwise; a positive code modifies it.
    /// </summary>
    /// <returns>
    /// True when the change was applied; false when validation failed (see <see cref="AllErrors"/>) or
    /// the transaction type is below <see cref="TransactionType.Delete"/>.
    /// </returns>
    /// <exception cref="NotSupportedException">
    /// <see cref="TransactionType.Delete"/>, or an update of an agent whose entity is never updated.
    /// </exception>
    public bool PersistEntity(int transactionType, ModelContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        switch (transactionType)
        {
            case TransactionType.Delete:
                // Only the out-of-scope ErewhonWebServiceAdmin deletes; no REST, SOAP or seed path does.
                throw new NotSupportedException(
                    $"{GetType().Name} does not delete: no stand-in operation deletes an entity.");
            case TransactionType.Persist:
                return Persist(ModelObject == null ? ValidationType.Create : ValidationType.Update, 0, context);
            case > TransactionType.Persist:
                return Persist(ValidationType.Modify, transactionType, context);
            default:
                return false;
        }
    }

    /// <summary>
    /// JADE <c>persistEntityInTransState</c>: as <see cref="PersistEntity"/>, inside a transaction the
    /// caller already holds. In memory both behave identically.
    /// </summary>
    public bool PersistEntityInTransState(int transactionType, ModelContext context) =>
        PersistEntity(transactionType, context);

    /// <summary>
    /// The validation half of JADE <c>createEntityWithTransactionImplementor</c>,
    /// <c>updateEntityWithTransactionImplementor</c> and <c>modifyEntityWithTransactionImplementor</c>:
    /// everything before <c>doBeginTransaction</c>, plus the subordinate agents' own passes.
    /// </summary>
    internal bool ValidateForPersist(ValidationType validationType, int modificationCode, ModelContext context)
    {
        if (validationType == ValidationType.Update)
        {
            // JADE resets the code because an update is a full persist.
            ModificationCode = 0;
        }

        ClearErrors();
        ClearWarnings();

        if (validationType == ValidationType.Modify)
        {
            ModificationCode = modificationCode;
        }

        DoPreValidate(context);
        return DoValidate(validationType, context) && ValidateSubordinates(validationType, context);
    }

    /// <summary>The commit half: JADE <c>doCreate</c>, <c>doUpdate</c> or <c>doModify</c>.</summary>
    internal void CommitPersist(ValidationType validationType, ModelContext context)
    {
        switch (validationType)
        {
            case ValidationType.Create:
                CommitCreate(context);
                break;
            case ValidationType.Update:
                CommitUpdate(context);
                break;
            case ValidationType.Modify:
                CommitModify(ModificationCode, context);
                break;
            default:
                throw new ArgumentOutOfRangeException(
                    nameof(validationType), validationType, "Only create, update and modify are committed.");
        }
    }

    /// <summary>JADE <c>addError</c>: appends the error; the first error sets the focus.</summary>
    protected void AddError(string error, string? focusField)
    {
        ArgumentNullException.ThrowIfNull(error);
        AllErrors.Add(error);
        if (FocusField.Length == 0)
        {
            FocusField = focusField ?? "";
        }
    }

    /// <summary>JADE <c>addWarning</c>.</summary>
    protected void AddWarning(string warning, string? focusField)
    {
        ArgumentNullException.ThrowIfNull(warning);
        AllWarnings.Add(warning);

        // JADE tests focusField <> null here, the opposite of addError, so a warning moves the focus
        // only once one is set. Ported as written; the focus never reaches a REST or SOAP response.
        if (FocusField.Length != 0)
        {
            FocusField = focusField ?? "";
        }
    }

    /// <summary>
    /// JADE <c>copyErrors</c>: appends every error of <paramref name="fromTA"/> in order, and adopts its
    /// focus when this agent has none.
    /// </summary>
    protected void CopyErrors(ModelTA fromTA)
    {
        ArgumentNullException.ThrowIfNull(fromTA);
        foreach (string error in fromTA.AllErrors.ToArray())
        {
            AddError(error, null);
        }

        if (FocusField.Length == 0)
        {
            FocusField = fromTA.FocusField;
        }
    }

    /// <summary>JADE <c>clearErrorsOnSubordinateTAs</c>.</summary>
    protected virtual void ClearErrorsOnSubordinateTAs()
    {
    }

    /// <summary>JADE <c>clearWarningsOnSubordinateTAs</c>.</summary>
    protected virtual void ClearWarningsOnSubordinateTAs()
    {
    }

    /// <summary>
    /// Runs each subordinate agent's own persist pass, which JADE runs in <c>createSubordinateObjects</c>,
    /// <c>updateSubordinateObjects</c> or <c>modifySubordinateObjects</c>, and copies its errors on
    /// failure.
    /// </summary>
    /// <returns>True when every subordinate agent validated.</returns>
    protected virtual bool ValidateSubordinates(ValidationType validationType, ModelContext context) => true;

    /// <summary>
    /// JADE <c>doCreate</c>: creates the entity (<c>onCreate</c>) and its subordinate objects, and sets
    /// <see cref="ModelObject"/> to it. Runs only after validation succeeded and never fails.
    /// </summary>
    protected abstract void CommitCreate(ModelContext context);

    /// <summary>
    /// JADE <c>doUpdate</c>: applies the agent's values to <see cref="ModelObject"/> (<c>onUpdate</c>)
    /// and its subordinate objects. Agents whose entity no stand-in path updates do not override it.
    /// </summary>
    /// <exception cref="NotSupportedException">The agent does not update its entity.</exception>
    protected virtual void CommitUpdate(ModelContext context) =>
        throw new NotSupportedException(
            $"{GetType().Name} does not update: no REST, SOAP or seed path updates this kind of entity.");

    /// <summary>
    /// JADE <c>doModify</c>. The inherited <c>PersistentModel.onModify</c> changes only the lock
    /// attributes, which are not ported, so the default modifies nothing.
    /// </summary>
    protected virtual void CommitModify(int modificationCode, ModelContext context)
    {
    }

    /// <summary>JADE <c>PersistentModel.onCreate</c>: the agent's guid, or a new one when it has none.</summary>
    protected string GuidForNewEntity(ModelContext context) => Guid.Length == 0 ? context.NewGuid() : Guid;

    /// <summary>
    /// The JADE type guard of each <c>getModelObject</c> (<c>inheritMethod().Agent</c> and so on).
    /// </summary>
    /// <exception cref="InvalidOperationException">The model object is of another class.</exception>
    protected T? ModelObjectAs<T>()
        where T : PersistentModel =>
        ModelObject switch
        {
            null => null,
            T typed => typed,
            PersistentModel other => throw new InvalidOperationException(
                $"{GetType().Name} holds a {other.GetType().Name} (oid {other.Oid}) where a {typeof(T).Name} is required."),
        };

    /// <summary>The model object an update or modify applies to.</summary>
    /// <exception cref="InvalidOperationException">There is none, or it is of another class.</exception>
    protected T RequireModelObject<T>()
        where T : PersistentModel =>
        ModelObjectAs<T>()
        ?? throw new InvalidOperationException($"{GetType().Name} has no {typeof(T).Name} to persist.");

    /// <summary>
    /// A reference validation guarantees by the time a commit runs; reading it there cannot fail
    /// unless a commit is reached without validation.
    /// </summary>
    protected T Validated<T>(T? value, string member)
        where T : class =>
        value ?? throw NotValidated(member);

    /// <summary>A value validation or pre-validation guarantees by the time a commit runs.</summary>
    protected T ValidatedValue<T>(T? value, string member)
        where T : struct =>
        value ?? throw NotValidated(member);

    /// <summary>
    /// JADE <c>TimeStamp</c> values are UTC throughout the model (<c>_webDefaultTimeZone Etc/UTC</c>);
    /// a local or unspecified time is rejected where it is assigned, so no commit can fail on it.
    /// </summary>
    /// <exception cref="ArgumentException">The value is not a UTC time.</exception>
    protected static DateTime? RequireUtc(DateTime? value, string member)
    {
        if (value is { Kind: not DateTimeKind.Utc } time)
        {
            throw new ArgumentException(
                $"{member} must be a UTC time; the value supplied has kind {time.Kind}.", member);
        }

        return value;
    }

    private bool Persist(ValidationType validationType, int modificationCode, ModelContext context)
    {
        if (!ValidateForPersist(validationType, modificationCode, context))
        {
            return false;
        }

        CommitPersist(validationType, context);
        return true;
    }

    private InvalidOperationException NotValidated(string member) =>
        new($"{GetType().Name} reached its commit with {member} unset, which validation rejects.");
}

/// <summary>
/// The behaviour <c>AgentTA</c>, <c>ClientTA</c> and <c>CompanyTA</c> share in JADE for their
/// <c>myAddressTA</c>: clearing its errors and warnings, validating it inline and in its own pass, and
/// creating or updating its <see cref="Address"/> after the owner's entity.
/// </summary>
/// <remarks>
/// JADE repeats these methods on each of the three agents; this base holds them once. The owner first
/// validates the address values as supplied (its <c>doValidate</c>), and the address agent then
/// validates them again after trimming them (its own <c>persistEntityInTransState</c>). A failure in
/// the first pass stops the persist, so the error list is the first pass's list, or the second pass's
/// when only trimming exposes the problem (a street of <c>" "</c>).
/// </remarks>
public abstract class AddressOwnerTA : ModelTA, IAddressOwnerTA
{
    private AddressTA? _myAddressTA;

    /// <inheritdoc />
    public AddressTA? MyAddressTA
    {
        get => _myAddressTA;
        set
        {
            if (value != null)
            {
                value.MyModelTA = this;
            }
            else if (_myAddressTA != null)
            {
                _myAddressTA.MyModelTA = null;
            }
        }
    }

    // The automatic side of the pair, maintained by AddressTA.MyModelTA.
    internal void LinkAddressTA(AddressTA? addressTA) => _myAddressTA = addressTA;

    /// <inheritdoc />
    protected override void ClearErrorsOnSubordinateTAs()
    {
        base.ClearErrorsOnSubordinateTAs();
        _myAddressTA?.ClearErrors();
    }

    /// <inheritdoc />
    protected override void ClearWarningsOnSubordinateTAs()
    {
        base.ClearWarningsOnSubordinateTAs();
        _myAddressTA?.ClearWarnings();
    }

    /// <summary>
    /// The owner's inline address check (<c>myAddressTA.doValidate( pValidationType )</c>), on the
    /// address values before trimming; copies the address errors when it fails.
    /// </summary>
    protected void ValidateAddressInline(ValidationType validationType, ModelContext context)
    {
        if (_myAddressTA != null && !_myAddressTA.DoValidate(validationType, context))
        {
            CopyErrors(_myAddressTA);
        }
    }

    /// <summary>
    /// The address agent's own pass (<c>myAddressTA.persistEntityInTransState</c>): it clears its
    /// errors, trims its values and validates them again.
    /// </summary>
    /// <exception cref="InvalidOperationException">A create has no address agent (JADE raises a null
    /// reference exception in <c>createSubordinateObjects</c>).</exception>
    protected override bool ValidateSubordinates(ValidationType validationType, ModelContext context)
    {
        AddressTA? addressTA = _myAddressTA;
        if (addressTA == null)
        {
            if (validationType == ValidationType.Create)
            {
                throw new InvalidOperationException(
                    $"{GetType().Name} cannot create its entity without an address: set AddressTA.MyModelTA to it first.");
            }

            return true;
        }

        if (!addressTA.ValidateForPersist(SubordinateValidationType(addressTA, validationType), ModificationCode, context))
        {
            CopyErrors(addressTA);
            return false;
        }

        return true;
    }

    /// <summary>
    /// JADE <c>createSubordinateObjects</c> and <c>updateSubordinateObjects</c>: creates or updates the
    /// address of <paramref name="owner"/>. A modify commits nothing on the address, because
    /// <c>Address</c> inherits <c>onModify</c>, which changes only the lock attributes.
    /// </summary>
    protected void CommitAddress(ValidationType ownerValidationType, PersistentModel owner, ModelContext context)
    {
        AddressTA? addressTA = _myAddressTA;
        if (addressTA == null || ownerValidationType == ValidationType.Modify)
        {
            return;
        }

        if (ownerValidationType == ValidationType.Create)
        {
            // JADE createSubordinateObjects: self.myAddressTA.myModel := self.getModelObject().
            addressTA.MyModel = owner;
        }

        addressTA.CommitPersist(SubordinateValidationType(addressTA, ownerValidationType), context);
    }

    // The subordinate persist is persistEntityInTransState( TransactionType_Persist ) for a create or
    // update, which creates when the address agent has no model object, and the owner's own code for a
    // modify.
    private static ValidationType SubordinateValidationType(AddressTA addressTA, ValidationType ownerValidationType) =>
        ownerValidationType == ValidationType.Modify ? ValidationType.Modify
        : addressTA.ModelObject == null ? ValidationType.Create
        : ValidationType.Update;
}

/// <summary>
/// JADE <c>AddressTA</c> (<c>Erewhon/ErewhonModelSchema.scm</c> 9794-10044): the postal and contact
/// details of an agent, client or company. Persisted only as the subordinate of its owner agent.
/// </summary>
public sealed class AddressTA : ModelTA
{
    private ModelTA? _myModelTA;

    /// <summary>JADE <c>street: String[51]</c>.</summary>
    public string Street { get; set; } = "";

    /// <summary>JADE <c>city: String[51]</c>.</summary>
    public string City { get; set; } = "";

    /// <summary>JADE <c>country: String[51]</c>.</summary>
    public string Country { get; set; } = "";

    /// <summary>JADE <c>email: String[61]</c>.</summary>
    public string Email { get; set; } = "";

    /// <summary>JADE <c>fax: String[26]</c>.</summary>
    public string Fax { get; set; } = "";

    /// <summary>JADE <c>phone: String[26]</c>.</summary>
    public string Phone { get; set; } = "";

    /// <summary>JADE <c>webSite: String[61]</c>.</summary>
    public string WebSite { get; set; } = "";

    /// <summary>
    /// JADE <c>myModel</c>: the agent, client or company entity a created address belongs to, set by
    /// the owner agent just before the address is created.
    /// </summary>
    public PersistentModel? MyModel { get; set; }

    /// <summary>
    /// JADE <c>myModelTA</c>: the owner agent. Setting it also makes this the owner's
    /// <see cref="IAddressOwnerTA.MyAddressTA"/> and releases any address agent the owner held before
    /// (JADE <c>myAddressTA ... automatic parentOf myModelTA ... manual</c>).
    /// </summary>
    public ModelTA? MyModelTA
    {
        get => _myModelTA;
        set
        {
            if (ReferenceEquals(_myModelTA, value))
            {
                return;
            }

            if (_myModelTA is AddressOwnerTA previousOwner && ReferenceEquals(previousOwner.MyAddressTA, this))
            {
                previousOwner.LinkAddressTA(null);
            }

            _myModelTA = value;
            if (value is AddressOwnerTA owner)
            {
                AddressTA? displaced = owner.MyAddressTA;
                if (displaced != null && !ReferenceEquals(displaced, this))
                {
                    displaced._myModelTA = null;
                }

                owner.LinkAddressTA(this);
            }
        }
    }

    /// <summary>JADE <c>getModelObject</c>.</summary>
    public Address? GetModelObject() => ModelObjectAs<Address>();

    /// <summary>
    /// JADE <c>initialize</c>: also resets the seven values and <see cref="MyModelTA"/>;
    /// <see cref="MyModel"/> is kept.
    /// </summary>
    public override void Initialize()
    {
        base.Initialize();
        City = "";
        Country = "";
        Email = "";
        Fax = "";
        MyModelTA = null;
        Phone = "";
        Street = "";
        WebSite = "";
    }

    /// <summary>JADE <c>populateFromObject</c>: the update starts from the address's current values.</summary>
    public void PopulateFromObject(Address address)
    {
        ArgumentNullException.ThrowIfNull(address);
        ModelObject = address;
        City = address.City;
        Country = address.Country;
        Email = address.Email;
        Fax = address.Fax;
        Phone = address.Phone;
        Street = address.Street;
        WebSite = address.WebSite;
    }

    /// <summary>JADE <c>doPreValidate</c>: <c>trimWhiteSpace</c> on all seven values.</summary>
    public override void DoPreValidate(ModelContext context)
    {
        City = JadeValues.TrimWhiteSpace(City);
        Country = JadeValues.TrimWhiteSpace(Country);
        Email = JadeValues.TrimWhiteSpace(Email);
        Fax = JadeValues.TrimWhiteSpace(Fax);
        Phone = JadeValues.TrimWhiteSpace(Phone);
        Street = JadeValues.TrimWhiteSpace(Street);
        WebSite = JadeValues.TrimWhiteSpace(WebSite);
        base.DoPreValidate(context);
    }

    /// <summary>
    /// JADE <c>doValidate</c>: street, city, country, phone and email are required, and the email must
    /// match <see cref="JadeValues.EmailRegEx"/>; fax and web site are optional.
    /// </summary>
    public override bool DoValidate(ValidationType validationType, ModelContext context)
    {
        _ = base.DoValidate(validationType, context);
        if (validationType == ValidationType.Delete)
        {
            return true;
        }

        if (string.IsNullOrEmpty(Street))
        {
            AddError(Messages.AddressStreetRequired, FocusConstants.Street);
        }

        if (string.IsNullOrEmpty(City))
        {
            AddError(Messages.AddressCityRequired, FocusConstants.City);
        }

        if (string.IsNullOrEmpty(Country))
        {
            AddError(Messages.AddressCountryRequired, FocusConstants.Country);
        }

        if (string.IsNullOrEmpty(Phone))
        {
            AddError(Messages.AddressPhoneRequired, FocusConstants.Phone);
        }

        if (string.IsNullOrEmpty(Email))
        {
            AddError(Messages.AddressEmailRequired, FocusConstants.Email);
        }
        else if (!JadeValues.IsValidEmail(Email))
        {
            AddError(Messages.AddressEmailInvalidFormat, FocusConstants.Email);
        }

        return HasNoErrors;
    }

    /// <summary>
    /// JADE <c>Address.onCreate</c>: the guid, the seven values, then <c>myModel</c>, whose automatic
    /// inverse makes the address the owner's <c>myAddress</c>.
    /// </summary>
    protected override void CommitCreate(ModelContext context)
    {
        // The owner's company allocates the object number. A company's own address is created before
        // the loader makes that company the context's (JADE app.setCompany), so it cannot come from
        // the context.
        Company company = MyModel switch
        {
            Company ownerCompany => ownerCompany,
            Agent agent => agent.MyCompany,
            Client client => client.MyCompany,
            _ => null,
        } ?? context.RequireCompany();

        var address = new Address(company.AllocateOid()) { Guid = GuidForNewEntity(context) };
        ApplyValues(address);
        address.MyModel = MyModel;
        switch (MyModel)
        {
            case Agent agent:
                agent.MyAddress = address;
                break;
            case Client client:
                client.MyAddress = address;
                break;
            case Company ownerCompany:
                ownerCompany.MyAddress = address;
                break;
        }

        ModelObject = address;
    }

    /// <summary>JADE <c>Address.onUpdate</c> (<c>setCommonProperties</c>): the seven values.</summary>
    protected override void CommitUpdate(ModelContext context) => ApplyValues(RequireModelObject<Address>());

    private void ApplyValues(Address address)
    {
        address.City = City;
        address.Country = Country;
        address.Email = Email;
        address.Fax = Fax;
        address.Phone = Phone;
        address.Street = Street;
        address.WebSite = WebSite;
    }
}

/// <summary>
/// JADE <c>AgentTA</c> (<c>Erewhon/ErewhonModelSchema.scm</c> 10045-10441): creates and updates an
/// <see cref="Agent"/> with its address, and applies the commission-rate modifications
/// <see cref="CommissionRateTA"/> issues.
/// </summary>
public sealed class AgentTA : AddressOwnerTA
{
    /// <summary>JADE <c>name: String[81]</c>, trimmed of blanks (spaces only) before validation.</summary>
    public string Name { get; set; } = "";

    /// <summary>
    /// JADE <c>myCommissionRate</c>: the rate a <see cref="TransactionType.ModifyAddCommissionRateForAgent"/>
    /// or <see cref="TransactionType.ModifyRemoveCommissionRateForAgent"/> modification applies.
    /// </summary>
    public CommissionRate? MyCommissionRate { get; set; }

    /// <summary>JADE <c>getModelObject</c>.</summary>
    public Agent? GetModelObject() => ModelObjectAs<Agent>();

    /// <summary>
    /// JADE <c>initialize</c>: also resets <see cref="Name"/>; the address agent and commission rate
    /// are kept, as in JADE.
    /// </summary>
    public override void Initialize()
    {
        base.Initialize();
        Name = "";
    }

    /// <summary>
    /// JADE <c>populateFromObject</c> and <c>populateSubordinateObjects</c>: the update starts from the
    /// agent's name and a new address agent holding its current address.
    /// </summary>
    /// <exception cref="InvalidOperationException">The agent has no address (JADE raises a null
    /// reference exception).</exception>
    public void PopulateFromObject(Agent agent)
    {
        ArgumentNullException.ThrowIfNull(agent);
        Address address = agent.MyAddress
            ?? throw new InvalidOperationException($"Agent {agent.Name} (oid {agent.Oid}) has no address.");

        ModelObject = agent;
        var addressTA = new AddressTA();
        addressTA.PopulateFromObject(address);
        addressTA.MyModelTA = this;
        Name = agent.Name;
    }

    /// <summary>
    /// JADE <c>doPreValidate</c>: <c>name.trimBlanks()</c>. The address is not trimmed here; its own
    /// pass trims it.
    /// </summary>
    public override void DoPreValidate(ModelContext context)
    {
        Name = JadeValues.TrimBlanks(Name);
        base.DoPreValidate(context);
    }

    /// <summary>
    /// JADE <c>doValidate</c>: for a create or update only, the name is required and unique, then the
    /// address values as supplied. A modify validates nothing.
    /// </summary>
    public override bool DoValidate(ValidationType validationType, ModelContext context)
    {
        _ = base.DoValidate(validationType, context);
        if (validationType == ValidationType.Delete)
        {
            return true;
        }

        if (validationType is ValidationType.Create or ValidationType.Update)
        {
            if (string.IsNullOrEmpty(Name))
            {
                AddError(Messages.AgentNameRequired, FocusConstants.Name);
            }

            if (validationType == ValidationType.Create && context.RequireCompany().HasAgentWithThisName(Name))
            {
                AddError(Messages.AgentAlreadyExists, FocusConstants.Name);
            }

            if (validationType == ValidationType.Update
                && context.RequireCompany().HasAnotherAgentWithThisName(Name, GetModelObject()))
            {
                AddError(Messages.AgentWithSameNameExists, FocusConstants.Name);
            }

            ValidateAddressInline(validationType, context);
        }

        return HasNoErrors;
    }

    /// <summary>
    /// JADE <c>Agent.onCreate</c>: the guid and name, then <c>myCompany</c> (whose inverse adds the agent
    /// to <see cref="Company.AllAgents"/>), then the address.
    /// </summary>
    protected override void CommitCreate(ModelContext context)
    {
        Company company = context.RequireCompany();
        var agent = new Agent(company.AllocateOid()) { Guid = GuidForNewEntity(context), Name = Name };
        company.AllAgents.Add(agent);
        agent.MyCompany = company;
        ModelObject = agent;
        CommitAddress(ValidationType.Create, agent, context);
    }

    /// <summary>
    /// JADE <c>Agent.onUpdate</c>: the name, re-keying every dictionary keyed by it, then the address.
    /// </summary>
    protected override void CommitUpdate(ModelContext context)
    {
        Agent agent = RequireModelObject<Agent>();
        if (!string.Equals(agent.Name, Name, StringComparison.Ordinal))
        {
            agent.Name = Name;
            agent.MyCompany?.AllAgents.Rekey(agent);
            foreach (CommissionRate rate in agent.AllCommissionRates)
            {
                rate.AllAgents.Rekey(agent);
            }
        }

        CommitAddress(ValidationType.Update, agent, context);
    }

    /// <summary>
    /// JADE <c>Agent.onModify</c>: adds or removes <see cref="MyCommissionRate"/>; the lock and unlock
    /// modifications change only the lock attributes, which are not ported.
    /// </summary>
    /// <exception cref="InvalidOperationException">An add or remove has no commission rate (JADE raises
    /// a null reference exception).</exception>
    protected override void CommitModify(int modificationCode, ModelContext context)
    {
        Agent agent = RequireModelObject<Agent>();
        switch (modificationCode)
        {
            case TransactionType.ModifyAddCommissionRateForAgent:
                agent.AddCommissionRate(RequireCommissionRate());
                break;
            case TransactionType.ModifyRemoveCommissionRateForAgent:
                agent.RemoveCommissionRate(RequireCommissionRate());
                break;
        }
    }

    private CommissionRate RequireCommissionRate() =>
        MyCommissionRate
        ?? throw new InvalidOperationException(
            $"AgentTA modification {ModificationCode} requires MyCommissionRate.");
}

/// <summary>
/// JADE <c>ClientTA</c> (<c>Erewhon/ErewhonModelSchema.scm</c> 10442-10835): creates and updates a
/// <see cref="Client"/> with its address.
/// </summary>
public sealed class ClientTA : AddressOwnerTA
{
    /// <summary>
    /// JADE <c>name: String[81]</c>, trimmed of white space (CR, LF, tab, space, NUL) before validation,
    /// unlike an agent's name.
    /// </summary>
    public string Name { get; set; } = "";

    /// <summary>JADE <c>getModelObject</c>.</summary>
    public Client? GetModelObject() => ModelObjectAs<Client>();

    /// <summary>JADE <c>initialize</c>: also resets <see cref="Name"/>; the address agent is kept.</summary>
    public override void Initialize()
    {
        base.Initialize();
        Name = "";
    }

    /// <summary>
    /// JADE <c>populateFromObject</c> and <c>populateSubordinateObjects</c>: the update starts from the
    /// client's name and a new address agent holding its current address.
    /// </summary>
    /// <exception cref="InvalidOperationException">The client has no address (JADE raises a null
    /// reference exception).</exception>
    public void PopulateFromObject(Client client)
    {
        ArgumentNullException.ThrowIfNull(client);
        Address address = client.MyAddress
            ?? throw new InvalidOperationException($"Client {client.Name} (oid {client.Oid}) has no address.");

        ModelObject = client;
        var addressTA = new AddressTA();
        addressTA.PopulateFromObject(address);
        addressTA.MyModelTA = this;
        Name = client.Name;
    }

    /// <summary>JADE <c>doPreValidate</c>: <c>name.trimWhiteSpace()</c>.</summary>
    public override void DoPreValidate(ModelContext context)
    {
        Name = JadeValues.TrimWhiteSpace(Name);
        base.DoPreValidate(context);
    }

    /// <summary>
    /// JADE <c>doValidate</c>: the name is required and unique, then the address values as supplied,
    /// for every validation type but delete.
    /// </summary>
    public override bool DoValidate(ValidationType validationType, ModelContext context)
    {
        _ = base.DoValidate(validationType, context);
        if (validationType == ValidationType.Delete)
        {
            return true;
        }

        if (string.IsNullOrEmpty(Name))
        {
            AddError(Messages.ClientNameRequired, FocusConstants.Name);
        }

        if (validationType == ValidationType.Create && context.RequireCompany().HasClientWithThisName(Name))
        {
            AddError(Messages.ClientAlreadyExists, FocusConstants.Name);
        }

        if (validationType == ValidationType.Update
            && context.RequireCompany().HasAnotherClientWithThisName(Name, GetModelObject()))
        {
            AddError(Messages.ClientWithSameNameExists, FocusConstants.Name);
        }

        ValidateAddressInline(validationType, context);
        return HasNoErrors;
    }

    /// <summary>
    /// JADE <c>Client.onCreate</c>: the guid and name, then <c>myCompany</c> (whose inverse adds the
    /// client to <see cref="Company.AllClients"/>), then the address.
    /// </summary>
    protected override void CommitCreate(ModelContext context)
    {
        Company company = context.RequireCompany();
        var client = new Client(company.AllocateOid()) { Guid = GuidForNewEntity(context), Name = Name };
        company.AllClients.Add(client);
        client.MyCompany = company;
        ModelObject = client;
        CommitAddress(ValidationType.Create, client, context);
    }

    /// <summary>JADE <c>Client.onUpdate</c>: the name, re-keying the company's clients, then the address.</summary>
    protected override void CommitUpdate(ModelContext context)
    {
        Client client = RequireModelObject<Client>();
        if (!string.Equals(client.Name, Name, StringComparison.Ordinal))
        {
            client.Name = Name;
            client.MyCompany?.AllClients.Rekey(client);
        }

        CommitAddress(ValidationType.Update, client, context);
    }
}

/// <summary>
/// JADE <c>CompanyTA</c> (<c>Erewhon/ErewhonModelSchema.scm</c> 11054-11414): creates the singleton
/// <see cref="Company"/> with its address.
/// </summary>
/// <remarks>
/// A create does not make the new company the context's: the caller does that (JADE
/// <c>app.setCompany</c>), as <c>InitialDataLoader.createCompany</c> does.
/// </remarks>
public sealed class CompanyTA : AddressOwnerTA
{
    /// <summary>JADE <c>name: String[81]</c>, trimmed of blanks before validation.</summary>
    public string Name { get; set; } = "";

    /// <summary>JADE <c>getModelObject</c>.</summary>
    public Company? GetModelObject() => ModelObjectAs<Company>();

    /// <summary>JADE <c>initialize</c>: also resets <see cref="Name"/>; the address agent is kept.</summary>
    public override void Initialize()
    {
        base.Initialize();
        Name = "";
    }

    /// <summary>
    /// JADE <c>populateFromObject</c> and <c>populateSubordinateObjects</c>: the update starts from the
    /// company's name and a new address agent holding its current address.
    /// </summary>
    /// <exception cref="InvalidOperationException">The company has no address (JADE raises a null
    /// reference exception).</exception>
    public void PopulateFromObject(Company company)
    {
        ArgumentNullException.ThrowIfNull(company);
        Address address = company.MyAddress
            ?? throw new InvalidOperationException($"Company {company.Name} has no address.");

        ModelObject = company;
        var addressTA = new AddressTA();
        addressTA.PopulateFromObject(address);
        addressTA.MyModelTA = this;
        Name = company.Name;
    }

    /// <summary>JADE <c>doPreValidate</c>: <c>name.trimBlanks()</c>.</summary>
    public override void DoPreValidate(ModelContext context)
    {
        Name = JadeValues.TrimBlanks(Name);
        base.DoPreValidate(context);
    }

    /// <summary>JADE <c>doValidate</c>: the name is required, then the address values as supplied.</summary>
    public override bool DoValidate(ValidationType validationType, ModelContext context)
    {
        _ = base.DoValidate(validationType, context);
        if (validationType == ValidationType.Delete)
        {
            return true;
        }

        if (string.IsNullOrEmpty(Name))
        {
            AddError(Messages.CompanyNameRequired, FocusConstants.Name);
        }

        ValidateAddressInline(validationType, context);
        return HasNoErrors;
    }

    /// <summary>
    /// JADE <c>doCreate</c> for a company: the guid and name, then its address, whose object number the
    /// new company allocates.
    /// </summary>
    protected override void CommitCreate(ModelContext context)
    {
        var company = new Company { Guid = GuidForNewEntity(context), Name = Name };
        ModelObject = company;
        CommitAddress(ValidationType.Create, company, context);
    }

    /// <summary>JADE <c>Company.onUpdate</c>: the name, then the address.</summary>
    protected override void CommitUpdate(ModelContext context)
    {
        Company company = RequireModelObject<Company>();
        company.Name = Name;
        CommitAddress(ValidationType.Update, company, context);
    }
}

/// <summary>
/// JADE <c>LocationTA</c> (<c>Erewhon/ErewhonModelSchema.scm</c> 12309-12452): the name shared by
/// <see cref="CountryTA"/> and <see cref="RegionTA"/>.
/// </summary>
public abstract class LocationTA : ModelTA
{
    /// <summary>JADE <c>name: String[81]</c>, trimmed of blanks before validation.</summary>
    public string Name { get; set; } = "";

    /// <summary>JADE <c>initialize</c>: also resets <see cref="Name"/>.</summary>
    public override void Initialize()
    {
        base.Initialize();
        Name = "";
    }

    /// <summary>JADE <c>doPreValidate</c>: <c>name.trimBlanks()</c>.</summary>
    public override void DoPreValidate(ModelContext context)
    {
        Name = JadeValues.TrimBlanks(Name);
        base.DoPreValidate(context);
    }

    /// <summary>JADE <c>doValidate</c>: nothing beyond the base rule.</summary>
    public override bool DoValidate(ValidationType validationType, ModelContext context)
    {
        _ = base.DoValidate(validationType, context);
        return validationType == ValidationType.Delete || HasNoErrors;
    }
}

/// <summary>
/// JADE <c>CountryTA</c> (<c>Erewhon/ErewhonModelSchema.scm</c> 12453-12584): creates a
/// <see cref="Country"/> of the company.
/// </summary>
public sealed class CountryTA : LocationTA
{
    /// <summary>JADE <c>getModelObject</c>.</summary>
    public Country? GetModelObject() => ModelObjectAs<Country>();

    /// <summary>
    /// JADE <c>doValidate</c>: the name is required and unique. As in JADE the rules follow the
    /// inherited check without its delete short-cut.
    /// </summary>
    public override bool DoValidate(ValidationType validationType, ModelContext context)
    {
        _ = base.DoValidate(validationType, context);

        if (string.IsNullOrEmpty(Name))
        {
            AddError(Messages.CountryNameRequired, FocusConstants.Name);
        }

        if (validationType == ValidationType.Create && context.RequireCompany().HasCountryWithThisName(Name))
        {
            AddError(Messages.CountryAlreadyExists, FocusConstants.Name);
        }

        if (validationType == ValidationType.Update
            && context.RequireCompany().HasAnotherCountryWithThisName(Name, GetModelObject()))
        {
            AddError(Messages.CountryWithSameNameExists, FocusConstants.Name);
        }

        return HasNoErrors;
    }

    /// <summary>
    /// JADE <c>Country.onCreate</c>: the guid and name, then <c>myCompany</c>, whose inverse adds the
    /// country to <see cref="Company.AllCountries"/>.
    /// </summary>
    protected override void CommitCreate(ModelContext context)
    {
        Company company = context.RequireCompany();
        var country = new Country(company.AllocateOid()) { Guid = GuidForNewEntity(context), Name = Name };
        country.MyCompany = company;
        company.AllCountries.Add(country);
        ModelObject = country;
    }
}

/// <summary>
/// JADE <c>RegionTA</c> (<c>Erewhon/ErewhonModelSchema.scm</c> 12585-12776): creates a
/// <see cref="Region"/> of a country.
/// </summary>
public sealed class RegionTA : LocationTA
{
    /// <summary>JADE <c>myCountry</c>.</summary>
    public Country? MyCountry { get; set; }

    /// <summary>JADE <c>getModelObject</c>.</summary>
    public Region? GetModelObject() => ModelObjectAs<Region>();

    /// <summary>JADE <c>initialize</c>: also resets <see cref="MyCountry"/>.</summary>
    public override void Initialize()
    {
        base.Initialize();
        MyCountry = null;
    }

    /// <summary>
    /// JADE <c>doValidate</c>: the name and country are required, and the name is unique within the
    /// country. As in JADE the rules follow the inherited check without its delete short-cut.
    /// </summary>
    public override bool DoValidate(ValidationType validationType, ModelContext context)
    {
        _ = base.DoValidate(validationType, context);

        if (string.IsNullOrEmpty(Name))
        {
            AddError(Messages.RegionNameRequired, FocusConstants.Name);
        }

        if (MyCountry == null)
        {
            AddError(Messages.CountryRequired, FocusConstants.Country);
        }
        else
        {
            if (validationType == ValidationType.Create && MyCountry.HasRegionWithThisName(Name))
            {
                AddError(Messages.RegionExists, FocusConstants.Name);
            }

            if (validationType == ValidationType.Update && MyCountry.HasAnotherRegionWithThisName(Name, GetModelObject()))
            {
                AddError(Messages.RegionExists, FocusConstants.Name);
            }
        }

        return HasNoErrors;
    }

    /// <summary>
    /// JADE <c>Region.onCreate</c>: the guid, name and <c>myCountry</c>, whose inverse adds the region
    /// to <see cref="Country.AllRegions"/>.
    /// </summary>
    protected override void CommitCreate(ModelContext context)
    {
        Country country = Validated(MyCountry, nameof(MyCountry));
        Company company = context.RequireCompany();
        var region = new Region(company.AllocateOid())
        {
            Guid = GuidForNewEntity(context),
            Name = Name,
            MyCountry = country,
        };
        country.AllRegions.Add(region);
        ModelObject = region;
    }
}

/// <summary>
/// JADE <c>ItemCategoryTA</c> (<c>Erewhon/ErewhonModelSchema.scm</c> 11415-11706): creates an
/// <see cref="ItemCategory"/>.
/// </summary>
public sealed class ItemCategoryTA : ModelTA
{
    /// <summary>JADE <c>name: String[41]</c>, trimmed of blanks before validation.</summary>
    public string Name { get; set; } = "";

    /// <summary>JADE <c>description: String</c>, trimmed of blanks before validation.</summary>
    public string Description { get; set; } = "";

    /// <summary>
    /// JADE <c>prefix: String[5]</c>, trimmed of blanks and upper-cased before validation.
    /// </summary>
    public string Prefix { get; set; } = "";

    /// <summary>JADE <c>getModelObject</c>.</summary>
    public ItemCategory? GetModelObject() => ModelObjectAs<ItemCategory>();

    /// <summary>JADE <c>initialize</c>: also resets the prefix, description and name.</summary>
    public override void Initialize()
    {
        base.Initialize();
        Prefix = "";
        Description = "";
        Name = "";
    }

    /// <summary>
    /// JADE <c>doPreValidate</c>: <c>trimBlanks</c> on the name, description and prefix, then
    /// <c>prefix.toUpper()</c>.
    /// </summary>
    public override void DoPreValidate(ModelContext context)
    {
        Name = JadeValues.TrimBlanks(Name);
        Description = JadeValues.TrimBlanks(Description);
        Prefix = JadeValues.TrimBlanks(Prefix).ToUpperInvariant();
        base.DoPreValidate(context);
    }

    /// <summary>
    /// JADE <c>doValidate</c>: the prefix is required, at least four characters and unique ignoring
    /// case; the name is required and unique; the description is required.
    /// </summary>
    public override bool DoValidate(ValidationType validationType, ModelContext context)
    {
        _ = base.DoValidate(validationType, context);
        if (validationType == ValidationType.Delete)
        {
            return true;
        }

        if (string.IsNullOrEmpty(Prefix))
        {
            AddError(Messages.ItemCategoryPrefixRequired, FocusConstants.CodePrefix);
        }
        else if (Prefix.Length < 4)
        {
            AddError(Messages.ItemCategoryPrefixInvalid, FocusConstants.CodePrefix);
        }
        else
        {
            ItemCategory? samePrefix = context.RequireCompany().AllItemCategoriesByPrefix.GetAtKey(Prefix);
            if (samePrefix != null && !ReferenceEquals(samePrefix, ModelObject))
            {
                AddError(Messages.ItemCategoryPrefixExists, FocusConstants.Name);
            }
        }

        if (string.IsNullOrEmpty(Name))
        {
            AddError(Messages.ItemCategoryNameRequired, FocusConstants.Name);
        }
        else
        {
            ItemCategory? sameName = context.RequireCompany().AllItemCategoriesByName.GetAtKey(Name);
            if (sameName != null && !ReferenceEquals(sameName, ModelObject))
            {
                AddError(Messages.ItemCategoryNameExists, FocusConstants.Name);
            }
        }

        if (string.IsNullOrEmpty(Description))
        {
            AddError(Messages.ItemCategoryDescriptionRequired, FocusConstants.Description);
        }

        return HasNoErrors;
    }

    /// <summary>
    /// JADE <c>ItemCategory.onCreate</c>: the guid, name, description and prefix, then
    /// <c>myCompany</c>, whose inverses add the category to both company dictionaries.
    /// </summary>
    protected override void CommitCreate(ModelContext context)
    {
        Company company = context.RequireCompany();
        var category = new ItemCategory(company.AllocateOid())
        {
            Guid = GuidForNewEntity(context),
            Name = Name,
            Description = Description,
            Prefix = Prefix,
        };
        category.MyCompany = company;
        company.AllItemCategoriesByName.Add(category);
        company.AllItemCategoriesByPrefix.Add(category);
        ModelObject = category;
    }
}

/// <summary>
/// JADE <c>CommissionRateTA</c> (<c>Erewhon/ErewhonModelSchema.scm</c> 10836-11053): creates a
/// commission rate of a category, or updates one, and brings its agents in line with
/// <see cref="AllAgents"/>.
/// </summary>
public sealed class CommissionRateTA : ModelTA
{
    private decimal _percentage;

    /// <summary>JADE <c>percentage: Decimal[5,2]</c>, rounded to two places on assignment.</summary>
    public decimal Percentage
    {
        get => _percentage;
        set => _percentage = JadeValues.RoundScale2(value);
    }

    /// <summary>JADE <c>myItemCategory</c>.</summary>
    public ItemCategory? MyItemCategory { get; set; }

    /// <summary>
    /// JADE <c>allAgents</c>: the agents who are to use the rate once it is persisted. Agents missing
    /// from the rate are added to it, and agents of the rate missing from here are removed.
    /// </summary>
    public AgentsByNameDict AllAgents { get; } = new();

    /// <summary>JADE <c>getModelObject</c>.</summary>
    public CommissionRate? GetModelObject() => ModelObjectAs<CommissionRate>();

    /// <summary>JADE <c>initialize</c>: also clears the agents, category and percentage.</summary>
    public override void Initialize()
    {
        base.Initialize();
        ClearAgents();
        MyItemCategory = null;
        Percentage = 0m;
    }

    /// <summary>
    /// JADE <c>populateFromObject</c>: the update starts from the rate's percentage, category and
    /// agents.
    /// </summary>
    public void PopulateFromObject(CommissionRate rate)
    {
        ArgumentNullException.ThrowIfNull(rate);
        ModelObject = rate;
        Percentage = rate.Percentage;
        MyItemCategory = rate.MyItemCategory;
        ClearAgents();
        foreach (Agent agent in rate.AllAgents)
        {
            AllAgents.Add(agent);
        }
    }

    /// <summary>
    /// JADE <c>doValidate</c>: the category is required, the percentage is above 0 and at most 100,
    /// and no other rate of the category has the same percentage.
    /// </summary>
    public override bool DoValidate(ValidationType validationType, ModelContext context)
    {
        _ = base.DoValidate(validationType, context);
        if (validationType == ValidationType.Delete)
        {
            return true;
        }

        if (MyItemCategory == null)
        {
            AddError(Messages.ItemCategoryRequired, FocusConstants.ItemCategory);
        }

        if (Percentage <= 0m || Percentage > 100m)
        {
            AddError(Messages.CommissionRatePercentageInvalid, FocusConstants.Percentage);
        }

        CommissionRate? samePercentage = MyItemCategory?.AllCommissionRates.GetAtKey(Percentage);
        if (samePercentage != null && !ReferenceEquals(samePercentage, ModelObject))
        {
            AddError(Messages.ItemCategoryHasCommissionRate, FocusConstants.Percentage);
        }

        return HasNoErrors;
    }

    /// <summary>
    /// JADE <c>onCreate</c> and <c>CommissionRate.setCommonProperties</c>: the guid, percentage and
    /// category (whose inverse adds the rate to the category's rates), then the agents.
    /// </summary>
    protected override void CommitCreate(ModelContext context)
    {
        ItemCategory category = Validated(MyItemCategory, nameof(MyItemCategory));
        Company company = context.RequireCompany();
        var rate = new CommissionRate(company.AllocateOid())
        {
            Guid = GuidForNewEntity(context),
            Percentage = Percentage,
            MyItemCategory = category,
        };
        category.AllCommissionRates.Add(rate);
        ModelObject = rate;
        AddOrRemoveAgents(rate, context);
    }

    /// <summary>
    /// JADE <c>CommissionRate.onUpdate</c> (<c>setCommonProperties</c>): the percentage and category,
    /// re-keying or moving the rate in every dictionary keyed by them, then the agents.
    /// </summary>
    protected override void CommitUpdate(ModelContext context)
    {
        CommissionRate rate = RequireModelObject<CommissionRate>();
        ItemCategory category = Validated(MyItemCategory, nameof(MyItemCategory));
        ItemCategory? previousCategory = rate.MyItemCategory;

        rate.Percentage = Percentage;
        if (ReferenceEquals(previousCategory, category))
        {
            category.AllCommissionRates.Rekey(rate);
        }
        else
        {
            previousCategory?.AllCommissionRates.Remove(rate);
            rate.MyItemCategory = category;
            category.AllCommissionRates.Add(rate);

            // Agent.allCommissionRates is keyed by the rate's category name.
            foreach (Agent agent in rate.AllAgents)
            {
                agent.AllCommissionRates.Rekey(rate);
            }
        }

        AddOrRemoveAgents(rate, context);
    }

    /// <summary>
    /// JADE <c>CommissionRate.addOrRemoveAgents</c> (6572-6609): one agent modification per agent to
    /// add, in name order, then one per agent to remove. JADE ignores each modification's result.
    /// </summary>
    private void AddOrRemoveAgents(CommissionRate rate, ModelContext context)
    {
        foreach (Agent agent in AllAgents)
        {
            if (!rate.AllAgents.Includes(agent))
            {
                ModifyAgent(agent, rate, TransactionType.ModifyAddCommissionRateForAgent, context);
            }
        }

        foreach (Agent agent in rate.AllAgents)
        {
            if (!AllAgents.Includes(agent))
            {
                ModifyAgent(agent, rate, TransactionType.ModifyRemoveCommissionRateForAgent, context);
            }
        }
    }

    private static void ModifyAgent(Agent agent, CommissionRate rate, int modificationCode, ModelContext context)
    {
        var agentTA = new AgentTA();
        agentTA.Initialize();
        agentTA.ModelObject = agent;
        agentTA.MyCommissionRate = rate;
        _ = agentTA.PersistEntityInTransState(modificationCode, context);
    }

    // AgentsByNameDict has no clear; its enumerator is a snapshot, so removing while iterating is safe.
    private void ClearAgents()
    {
        foreach (Agent agent in AllAgents)
        {
            AllAgents.Remove(agent);
        }
    }
}

/// <summary>
/// JADE <c>ItemTA</c> (<c>Erewhon/ErewhonModelSchema.scm</c> 11707-12092): creates a retail or tender
/// item. No stand-in path updates an item.
/// </summary>
public abstract class ItemTA : ModelTA
{
    private decimal _price;

    /// <summary>JADE <c>codeNumber: Integer</c>; 0 (the JADE null) takes the next code at pre-validation.</summary>
    public int CodeNumber { get; set; }

    /// <summary>JADE <c>description: String</c>, trimmed of white space before validation.</summary>
    public string Description { get; set; } = "";

    /// <summary>JADE <c>listedDate: Date</c>; null takes the server date at pre-validation.</summary>
    public DateOnly? ListedDate { get; set; }

    /// <summary>JADE <c>name: String[31]</c>, trimmed of white space before validation.</summary>
    public string Name { get; set; } = "";

    /// <summary>JADE <c>photo: Binary</c>: the image file bytes, or null.</summary>
    public byte[]? Photo { get; set; }

    /// <summary>
    /// JADE <c>price: Decimal[12,2]</c> (a tender item's reserve), rounded to two places on assignment.
    /// </summary>
    public decimal Price
    {
        get => _price;
        set => _price = JadeValues.RoundScale2(value);
    }

    /// <summary>JADE <c>myAgent</c>.</summary>
    public Agent? MyAgent { get; set; }

    /// <summary>JADE <c>myItemCategory</c>.</summary>
    public ItemCategory? MyItemCategory { get; set; }

    /// <summary>JADE <c>myRegion</c>.</summary>
    public Region? MyRegion { get; set; }

    /// <summary>JADE <c>mySale</c>; carried by the agent but never applied to the item.</summary>
    public Sale? MySale { get; set; }

    /// <summary>JADE <c>getModelObject</c>.</summary>
    public Item? GetModelObject() => ModelObjectAs<Item>();

    /// <summary>
    /// JADE <c>ItemTA.getNextCodeNumber</c> (11845): the next code of the company's shared retail and
    /// tender sequence.
    /// </summary>
    public int GetNextCodeNumber(ModelContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return context.RequireCompany().GetNextCodeNumber();
    }

    /// <summary>JADE <c>initialize</c>: also resets every item value.</summary>
    public override void Initialize()
    {
        base.Initialize();
        CodeNumber = 0;
        Description = "";
        ListedDate = null;
        MyAgent = null;
        MyRegion = null;
        MySale = null;
        MyItemCategory = null;
        Photo = null;
        Price = 0m;
        Name = "";
    }

    /// <summary>
    /// JADE <c>doPreValidate</c>: the next code number and the server date as defaults, then
    /// <c>trimWhiteSpace</c> on the description and name.
    /// </summary>
    public override void DoPreValidate(ModelContext context)
    {
        base.DoPreValidate(context);

        if (CodeNumber == 0)
        {
            CodeNumber = GetNextCodeNumber(context);
        }

        ListedDate ??= context.Today;
        Description = JadeValues.TrimWhiteSpace(Description);
        Name = JadeValues.TrimWhiteSpace(Name);
    }

    /// <summary>
    /// JADE <c>doValidate</c>: code, agent, category, region, name and description are required, a
    /// photo must be a valid picture, and the price must be above 0 (JADE <c>price = null or price &lt;
    /// 0</c>, the null Decimal being 0).
    /// </summary>
    public override bool DoValidate(ValidationType validationType, ModelContext context)
    {
        _ = base.DoValidate(validationType, context);
        if (validationType == ValidationType.Delete)
        {
            return true;
        }

        if (CodeNumber == 0)
        {
            AddError(Messages.ItemNumberRequired, FocusConstants.CodeNumber);
        }

        if (MyAgent == null)
        {
            AddError(Messages.AgentRequired, FocusConstants.Agent);
        }

        if (MyItemCategory == null)
        {
            AddError(Messages.ItemCategoryRequired, FocusConstants.ItemCategory);
        }

        if (MyRegion == null)
        {
            AddError(Messages.RegionRequired, FocusConstants.Region);
        }

        if (string.IsNullOrEmpty(Name))
        {
            AddError(Messages.ItemNameRequired, FocusConstants.ItemName);
        }

        if (string.IsNullOrEmpty(Description))
        {
            AddError(Messages.ItemDescriptionRequired, FocusConstants.ItemDescription);
        }

        if (Photo is { Length: > 0 } photo && !JadeValues.IsValidPicture(photo))
        {
            AddError(Messages.ImageNotValid, FocusConstants.Photo);
        }

        if (Price <= 0m)
        {
            AddError(Messages.ItemPriceNotValid, FocusConstants.Price);
        }

        return HasNoErrors;
    }

    /// <summary>
    /// JADE <c>Item.onCreate</c>: the guid (the seed's, supplied by the loader), the item values, then
    /// <c>myCompany</c> and <c>myAgent</c>. Every key is set before the item joins the dictionaries the
    /// JADE inverses maintain.
    /// </summary>
    protected sealed override void CommitCreate(ModelContext context)
    {
        Agent agent = Validated(MyAgent, nameof(MyAgent));
        ItemCategory category = Validated(MyItemCategory, nameof(MyItemCategory));
        Region region = Validated(MyRegion, nameof(MyRegion));
        Company company = context.RequireCompany();

        Item item = NewItem(company.AllocateOid());
        item.Guid = GuidForNewEntity(context);
        item.MyItemCategory = category;
        item.CodeNumber = CodeNumber;
        item.Description = Description;
        item.MyRegion = region;
        item.Photo = Photo;
        item.Price = Price;
        item.Name = Name;
        item.ListedDate = ValidatedValue(ListedDate, nameof(ListedDate));
        item.MyAgent = agent;
        item.MyCompany = company;

        company.AllItems.Add(item);
        switch (item)
        {
            case RetailItem retailItem:
                company.AllRetailItems.Add(retailItem);
                break;
            case TenderItem tenderItem:
                company.AllTenderItems.Add(tenderItem);
                company.AllTenderItemsBySaleDate.Add(tenderItem);
                break;
        }

        category.AllItems.Add(item);
        region.AllItems.Add(item);
        agent.AllItems.Add(item);
        ModelObject = item;
    }

    /// <summary>
    /// Creates the subclass entity with the subclass values set (JADE <c>create ... as
    /// getModelObjectClass()</c> and the subclass <c>setCommonProperties</c>).
    /// </summary>
    protected abstract Item NewItem(long oid);
}

/// <summary>
/// JADE <c>RetailItemTA</c> (<c>Erewhon/ErewhonModelSchema.scm</c> 12093-12120): creates a
/// <see cref="RetailItem"/>.
/// </summary>
public sealed class RetailItemTA : ItemTA
{
    /// <summary>JADE <c>getModelObject</c>.</summary>
    public new RetailItem? GetModelObject() => ModelObjectAs<RetailItem>();

    /// <inheritdoc />
    protected override Item NewItem(long oid) => new RetailItem(oid);
}

/// <summary>
/// JADE <c>TenderItemTA</c> (<c>Erewhon/ErewhonModelSchema.scm</c> 12121-12308): creates a
/// <see cref="TenderItem"/> with its closure date.
/// </summary>
public sealed class TenderItemTA : ItemTA
{
    /// <summary>JADE <c>closureDate: Date</c>, required.</summary>
    public DateOnly? ClosureDate { get; set; }

    /// <summary>JADE <c>getModelObject</c>.</summary>
    public new TenderItem? GetModelObject() => ModelObjectAs<TenderItem>();

    /// <summary>JADE <c>initialize</c>: also resets <see cref="ClosureDate"/>.</summary>
    public override void Initialize()
    {
        base.Initialize();
        ClosureDate = null;
    }

    /// <summary>JADE <c>doValidate</c>: the item rules, then the closure date is required.</summary>
    public override bool DoValidate(ValidationType validationType, ModelContext context)
    {
        _ = base.DoValidate(validationType, context);
        if (validationType == ValidationType.Delete)
        {
            return true;
        }

        if (ClosureDate == null)
        {
            AddError(Messages.TenderItemClosureDateNotValid, FocusConstants.Date);
        }

        return HasNoErrors;
    }

    /// <summary>
    /// JADE <c>TenderItem.setCommonProperties</c>: the closure date, a key of
    /// <see cref="Company.AllTenderItemsBySaleDate"/>, is set before the item is indexed.
    /// </summary>
    protected override Item NewItem(long oid) =>
        new TenderItem(oid) { ClosureDate = ValidatedValue(ClosureDate, nameof(ClosureDate)) };
}

/// <summary>
/// JADE <c>SaleTA</c> (<c>Erewhon/ErewhonModelSchema.scm</c> 12777-12948): the rules shared by retail
/// and tender sales. A sale is never updated once created.
/// </summary>
public abstract class SaleTA : ModelTA
{
    private decimal _agentCommission;

    /// <summary>
    /// JADE <c>agentCommission: Decimal[12,2]</c>, rounded to two places on assignment. The sale
    /// replaces it with the computed commission when it is created.
    /// </summary>
    public decimal AgentCommission
    {
        get => _agentCommission;
        set => _agentCommission = JadeValues.RoundScale2(value);
    }

    /// <summary>JADE <c>myClient</c>: the buyer.</summary>
    public Client? MyClient { get; set; }

    /// <summary>JADE <c>myItem</c>: the item sold.</summary>
    public Item? MyItem { get; set; }

    /// <summary>JADE <c>getModelObject</c>.</summary>
    public Sale? GetModelObject() => ModelObjectAs<Sale>();

    /// <summary>JADE <c>initialize</c>: also resets the commission, client and item.</summary>
    public override void Initialize()
    {
        base.Initialize();
        AgentCommission = 0m;
        MyClient = null;
        MyItem = null;
    }

    /// <summary>
    /// JADE <c>doValidate</c>: client and item are required, the commission cannot be negative, and a
    /// create needs an unsold item.
    /// </summary>
    public override bool DoValidate(ValidationType validationType, ModelContext context)
    {
        _ = base.DoValidate(validationType, context);
        if (validationType == ValidationType.Delete)
        {
            return true;
        }

        if (MyClient == null)
        {
            AddError(Messages.ClientRequired, FocusConstants.Client);
        }

        if (MyItem == null)
        {
            AddError(Messages.ItemRequired, FocusConstants.Item);
        }

        if (AgentCommission < 0m)
        {
            AddError(Messages.SaleAgentCommissionInvalid, FocusConstants.AgentCommission);
        }

        if (validationType == ValidationType.Create && MyItem?.MySale != null)
        {
            AddError(Messages.ItemSold, FocusConstants.Item);
        }

        return HasNoErrors;
    }

    /// <summary>
    /// JADE <c>Sale.onCreate</c> after the subclass values: <c>myCompany</c>, <c>myClient</c> and
    /// <c>myItem</c>, whose automatic inverse sets the item's <c>mySale</c>.
    /// </summary>
    protected static void LinkSale(Sale sale, Company company, Client client, Item item)
    {
        sale.MyCompany = company;
        sale.MyClient = client;
        sale.MyItem = item;
        item.MySale = sale;
    }

    /// <summary>
    /// The dictionaries a sale changes through the JADE inverses once its keys are set: the
    /// company's sales, the agent's sold items, and the item's position among the tender items by sale.
    /// The client's own dictionary depends on the kind of sale and is maintained by the subclass.
    /// </summary>
    protected static void IndexSale(Sale sale, Company company, Item item)
    {
        company.AllSalesByItem.Add(sale);

        // Agent.allSoldItems holds the agent's items where Item::isSold.
        if (item.MyAgent is { } agent && !agent.AllSoldItems.Includes(item))
        {
            agent.AllSoldItems.Add(item);
        }

        // Company.allTenderItemsBySaleDate is keyed by mySale first.
        if (item is TenderItem tenderItem)
        {
            (tenderItem.MyCompany ?? company).AllTenderItemsBySaleDate.Rekey(tenderItem);
        }
    }
}

/// <summary>
/// JADE <c>RetailSaleTA</c> (<c>Erewhon/ErewhonModelSchema.scm</c> 12949-13166): creates a
/// <see cref="RetailSale"/>.
/// </summary>
public sealed class RetailSaleTA : SaleTA
{
    private DateTime? _dateSold;
    private decimal _salePrice;

    /// <summary>
    /// JADE <c>dateSold: TimeStamp</c> (UTC); null takes the server timestamp at pre-validation.
    /// </summary>
    /// <exception cref="ArgumentException">The value assigned is not a UTC time.</exception>
    public DateTime? DateSold
    {
        get => _dateSold;
        set => _dateSold = RequireUtc(value, nameof(DateSold));
    }

    /// <summary>JADE <c>salePrice: Decimal[12,2]</c>, rounded to two places on assignment.</summary>
    public decimal SalePrice
    {
        get => _salePrice;
        set => _salePrice = JadeValues.RoundScale2(value);
    }

    /// <summary>JADE <c>getModelObject</c>.</summary>
    public new RetailSale? GetModelObject() => ModelObjectAs<RetailSale>();

    /// <summary>
    /// JADE <c>initialize</c>: also resets the price, and the sold time to unset. JADE assigns a fresh
    /// local timestamp, the current time; pre-validation gives the same server time here.
    /// </summary>
    public override void Initialize()
    {
        base.Initialize();
        SalePrice = 0m;
        DateSold = null;
    }

    /// <summary>JADE <c>doPreValidate</c>: the server timestamp when no sold time is set.</summary>
    public override void DoPreValidate(ModelContext context)
    {
        base.DoPreValidate(context);
        DateSold ??= context.UtcNow;
    }

    /// <summary>
    /// JADE <c>doValidate</c>: the sale rules, then a price (0 is the JADE null) that is not negative,
    /// and a sold time.
    /// </summary>
    public override bool DoValidate(ValidationType validationType, ModelContext context)
    {
        _ = base.DoValidate(validationType, context);
        if (validationType == ValidationType.Delete)
        {
            return true;
        }

        if (SalePrice == 0m)
        {
            AddError(Messages.RetailSalePriceRequired, FocusConstants.Price);
        }
        else if (SalePrice < 0m)
        {
            AddError(Messages.RetailPriceInvalid, FocusConstants.Price);
        }

        if (DateSold == null)
        {
            AddError(Messages.SoldDateNotValid, FocusConstants.Timestamp);
        }

        return HasNoErrors;
    }

    /// <summary>
    /// JADE <c>RetailSale.onCreate</c>: the guid, commission, price and sold time, the references, then
    /// <c>calculateAgentCommission( salePrice )</c>.
    /// </summary>
    protected override void CommitCreate(ModelContext context)
    {
        Client client = Validated(MyClient, nameof(MyClient));
        Item item = Validated(MyItem, nameof(MyItem));
        Company company = context.RequireCompany();

        var sale = new RetailSale(company.AllocateOid())
        {
            Guid = GuidForNewEntity(context),
            AgentCommission = AgentCommission,
            SalePrice = SalePrice,
            DateSold = ValidatedValue(DateSold, nameof(DateSold)),
        };
        LinkSale(sale, company, client, item);
        sale.CalculateAgentCommission(sale.SalePrice);

        IndexSale(sale, company, item);
        client.AllRetailSales.Add(sale);
        ModelObject = sale;
    }
}

/// <summary>
/// JADE <c>TenderSaleTA</c> (<c>Erewhon/ErewhonModelSchema.scm</c> 13167-13357): creates the
/// <see cref="TenderSale"/> of a tender item for its winning tender.
/// </summary>
public sealed class TenderSaleTA : SaleTA
{
    /// <summary>JADE <c>myTender</c>: the winning tender.</summary>
    public Tender? MyTender { get; set; }

    /// <summary>JADE <c>getModelObject</c>.</summary>
    public new TenderSale? GetModelObject() => ModelObjectAs<TenderSale>();

    /// <summary>JADE <c>initialize</c>: also resets <see cref="MyTender"/>.</summary>
    public override void Initialize()
    {
        base.Initialize();
        MyTender = null;
    }

    /// <summary>JADE <c>doValidate</c>: the sale rules, then the tender is required.</summary>
    public override bool DoValidate(ValidationType validationType, ModelContext context)
    {
        _ = base.DoValidate(validationType, context);
        if (validationType == ValidationType.Delete)
        {
            return true;
        }

        if (MyTender == null)
        {
            AddError(Messages.TenderRequired, FocusConstants.Tender);
        }

        return HasNoErrors;
    }

    /// <summary>
    /// JADE <c>TenderSale.onCreate</c>: the guid and commission, the references, <c>myTender</c> (whose
    /// automatic peer sets the tender's <c>myTenderSale</c>), then
    /// <c>calculateAgentCommission( myTender.offer )</c>.
    /// </summary>
    protected override void CommitCreate(ModelContext context)
    {
        Client client = Validated(MyClient, nameof(MyClient));
        Item item = Validated(MyItem, nameof(MyItem));
        Tender tender = Validated(MyTender, nameof(MyTender));
        Company company = context.RequireCompany();

        var sale = new TenderSale(company.AllocateOid())
        {
            Guid = GuidForNewEntity(context),
            AgentCommission = AgentCommission,
        };
        LinkSale(sale, company, client, item);
        sale.MyTender = tender;
        tender.MyTenderSale = sale;
        sale.CalculateAgentCommission(tender.Offer);

        IndexSale(sale, company, item);

        // Client.allTenderSales is keyed by myTender.dateTendered, so the tender is set first.
        client.AllTenderSales.Add(sale);
        ModelObject = sale;
    }
}

/// <summary>
/// JADE <c>TenderTA</c> (<c>Erewhon/ErewhonModelSchema.scm</c> 13853-14146): places a client's
/// <see cref="Tender"/> on an open tender item.
/// </summary>
/// <remarks>
/// The rules apply to the seed tenders as well: a tender on an item whose closure date is before the
/// server date is rejected with <see cref="Messages.TenderItemAlreadyClosed"/>, at load as for a
/// request.
/// </remarks>
public sealed class TenderTA : ModelTA
{
    private DateTime? _dateTendered;
    private decimal _offer;

    /// <summary>JADE <c>offer: Decimal[12,2]</c>, rounded to two places on assignment.</summary>
    public decimal Offer
    {
        get => _offer;
        set => _offer = JadeValues.RoundScale2(value);
    }

    /// <summary>
    /// JADE <c>dateTendered: TimeStamp</c> (UTC); null takes the server timestamp at pre-validation.
    /// </summary>
    /// <exception cref="ArgumentException">The value assigned is not a UTC time.</exception>
    public DateTime? DateTendered
    {
        get => _dateTendered;
        set => _dateTendered = RequireUtc(value, nameof(DateTendered));
    }

    /// <summary>JADE <c>myClient</c>: the bidder.</summary>
    public Client? MyClient { get; set; }

    /// <summary>JADE <c>myTenderItem</c>: the item bid for.</summary>
    public TenderItem? MyTenderItem { get; set; }

    /// <summary>JADE <c>myTenderSale</c>; carried by the agent but never applied to the tender.</summary>
    public TenderSale? MyTenderSale { get; set; }

    /// <summary>JADE <c>getModelObject</c>.</summary>
    public Tender? GetModelObject() => ModelObjectAs<Tender>();

    /// <summary>JADE <c>initialize</c>: also resets every tender value.</summary>
    public override void Initialize()
    {
        base.Initialize();
        MyClient = null;
        MyTenderSale = null;
        MyTenderItem = null;
        Offer = 0m;
        DateTendered = null;
    }

    /// <summary>JADE <c>doPreValidate</c>: the server timestamp when no tender time is set.</summary>
    public override void DoPreValidate(ModelContext context)
    {
        base.DoPreValidate(context);
        DateTendered ??= context.UtcNow;
    }

    /// <summary>
    /// JADE <c>doValidate</c>: the item is required, unsold and open (either failure ends validation at
    /// once), the offer is above 0 and at least the reserve, and the client has not already tendered the
    /// same offer; then the client and tender time are required.
    /// </summary>
    public override bool DoValidate(ValidationType validationType, ModelContext context)
    {
        _ = base.DoValidate(validationType, context);
        if (validationType == ValidationType.Delete)
        {
            return true;
        }

        if (MyTenderItem == null)
        {
            AddError(Messages.TenderItemRequired, FocusConstants.TenderItem);
        }
        else
        {
            if (MyTenderItem.MySale != null)
            {
                AddError(Messages.TenderItemAlreadySold, FocusConstants.TenderItem);
                return false;
            }

            if (MyTenderItem.IsClosed(context.Today))
            {
                AddError(Messages.TenderItemAlreadyClosed, FocusConstants.TenderItem);
                return false;
            }

            if (Offer <= 0m)
            {
                AddError(Messages.TenderOfferLessThanZero, FocusConstants.TenderItem);
            }
            else if (Offer < MyTenderItem.Price)
            {
                AddError(Messages.TenderOfferLessThanReserve, FocusConstants.TenderItem);
            }

            // JADE: myClient <> null and offer <> null, the null Decimal being 0; one error per match.
            if (MyClient != null && Offer != 0m)
            {
                foreach (Tender tender in MyTenderItem.AllTendersByOfferTime)
                {
                    if (ReferenceEquals(tender.MyClient, MyClient) && Offer == tender.Offer)
                    {
                        AddError(Messages.TenderAlreadyPlaced, FocusConstants.Offer);
                    }
                }
            }
        }

        if (MyClient == null)
        {
            AddError(Messages.ClientRequired, FocusConstants.Client);
        }

        if (DateTendered == null)
        {
            AddError(Messages.TimeStampInvalid, FocusConstants.Timestamp);
        }

        return HasNoErrors;
    }

    /// <summary>
    /// JADE <c>Tender.onCreate</c>: the guid, offer and tender time, then <c>myClient</c>,
    /// <c>myTenderItem</c> and <c>myCompany</c>, whose inverses add the tender to the item's, the
    /// client's and the company's tender dictionaries.
    /// </summary>
    protected override void CommitCreate(ModelContext context)
    {
        Client client = Validated(MyClient, nameof(MyClient));
        TenderItem tenderItem = Validated(MyTenderItem, nameof(MyTenderItem));
        Company company = context.RequireCompany();

        var tender = new Tender(company.AllocateOid())
        {
            Guid = GuidForNewEntity(context),
            Offer = Offer,
            DateTendered = ValidatedValue(DateTendered, nameof(DateTendered)),
            MyClient = client,
            MyTenderItem = tenderItem,
            MyCompany = company,
        };
        tenderItem.AllTendersByOfferTime.Add(tender);
        tenderItem.AllTendersByTimeOffer.Add(tender);
        client.AllTenders.Add(tender);
        company.AllTenders.Add(tender);
        ModelObject = tender;
    }
}
