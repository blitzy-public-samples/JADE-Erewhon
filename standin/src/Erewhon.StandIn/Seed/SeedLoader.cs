using System.Globalization;
using System.Text;
using Erewhon.StandIn.Model;

namespace Erewhon.StandIn.Seed;

/// <summary>
/// Port of JADE <c>InitialDataLoader.loadData</c> (<c>Erewhon/ErewhonModelSchema.scm:8428-8508</c>):
/// rebuilds the whole Erewhon model from the seed files in <c>Erewhon/DataFiles</c>, persisting every
/// row through the transaction agents of <c>Model/Validation.cs</c>, then closes the tenders due at the
/// reference date.
/// </summary>
/// <remarks>
/// <para>
/// The reference date stands in for JADE's current date (KL-STANDIN-DATE): it fixes the date offset
/// added to every seed date, the server date the agents validate against and the closure date. The
/// loader only reads the seed directory, writes nothing and prints nothing.
/// </para>
/// <para>
/// Each call builds an independent graph, so concurrent calls on different contexts are safe; one
/// context must not be shared between concurrent calls.
/// </para>
/// </remarks>
public static class SeedLoader
{
    /// <summary>JADE <c>DataFileNames.AgentDataFile</c> (<c>ErewhonModelSchema.scm:51</c>).</summary>
    public const string AgentsFile = "Agents.txt";

    /// <summary>JADE <c>DataFileNames.AgentRatesDataFile</c> (<c>ErewhonModelSchema.scm:52</c>).</summary>
    public const string AgentRatesFile = "AgentRates.txt";

    /// <summary>JADE <c>DataFileNames.CategoriesDataFile</c> (<c>ErewhonModelSchema.scm:53</c>).</summary>
    public const string CategoriesFile = "Categories.txt";

    /// <summary>JADE <c>DataFileNames.CategoryRatesDataFile</c> (<c>ErewhonModelSchema.scm:54</c>).</summary>
    public const string CategoryRatesFile = "CategoryRates.txt";

    /// <summary>JADE <c>DataFileNames.ClientsDataFile</c> (<c>ErewhonModelSchema.scm:55</c>).</summary>
    public const string ClientsFile = "Clients.txt";

    /// <summary>JADE <c>DataFileNames.LocationsDataFile</c> (<c>ErewhonModelSchema.scm:56</c>).</summary>
    public const string LocationsFile = "Locations.txt";

    /// <summary>JADE <c>DataFileNames.RetailItemsDataFile</c> (<c>ErewhonModelSchema.scm:57</c>).</summary>
    public const string RetailItemsFile = "RetailItems.txt";

    /// <summary>JADE <c>DataFileNames.RetailSalesDataFile</c> (<c>ErewhonModelSchema.scm:58</c>).</summary>
    public const string RetailSalesFile = "RetailSales.txt";

    /// <summary>JADE <c>DataFileNames.TenderItemsDataFile</c> (<c>ErewhonModelSchema.scm:59</c>).</summary>
    public const string TenderItemsFile = "TenderItems.txt";

    /// <summary>JADE <c>DataFileNames.TendersDataFile</c> (<c>ErewhonModelSchema.scm:60</c>).</summary>
    public const string TendersFile = "Tenders.txt";

    /// <summary>
    /// JADE <c>offsetStartDate.setDate( 01, 01, 2000 )</c> (<c>ErewhonModelSchema.scm:8468</c>): every
    /// seed date is moved forward by the days from this date to the reference date.
    /// </summary>
    public static readonly DateOnly OffsetStartDate = new(2000, 1, 1);

    // The stand-in "now" during a load: the reference date at 12:00:00 UTC. Loading is not a request,
    // so the clock never advances here.
    private static readonly TimeOnly LoadTimeOfDay = new(12, 0, 0);

    // Item dates are written "October 1 1999"; tender and sale dates "23 March 2000" / "02 August 2000".
    private static readonly string[] DateFormats = ["MMMM d yyyy", "d MMMM yyyy", "MMMM dd yyyy", "dd MMMM yyyy"];

    private const string TimeFormat = "HH:mm:ss";

    private const NumberStyles DecimalStyles = NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint;

    // The chunk an image file is read in, between two observations of the load's cancellation
    // token: the buffer size Stream.CopyTo uses.
    private const int PictureReadChunkSize = 81920;

    /// <summary>
    /// Loads the seed with a new load context: no company yet, guids from <paramref name="newGuid"/>
    /// and the clock at <paramref name="referenceDate"/> 12:00:00 UTC.
    /// </summary>
    /// <param name="seedDirectory">The directory holding the seed files and item images.</param>
    /// <param name="referenceDate">The date that replaces JADE's current date.</param>
    /// <param name="newGuid">
    /// Generates the guid of every entity created without a seed guid, in creation order.
    /// </param>
    /// <param name="cancellationToken">Stops the load between seed files and between rows.</param>
    /// <returns>The loaded model, its context, the counts and the rows the agents rejected.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="newGuid"/> is null.</exception>
    /// <inheritdoc cref="Load(string, DateOnly, ModelContext, CancellationToken)" path="/exception"/>
    public static SeedLoadResult Load(
        string seedDirectory,
        DateOnly referenceDate,
        Func<string> newGuid,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(newGuid);
        var context = new ModelContext(null, newGuid, referenceDate.ToDateTime(LoadTimeOfDay, DateTimeKind.Utc));
        return Load(seedDirectory, referenceDate, context, cancellationToken);
    }

    /// <summary>
    /// Loads the seed into a context the caller owns, in the order of JADE <c>loadData</c>: the
    /// company, locations, agents, clients, categories, retail items, tender items, category rates,
    /// agent rates, tenders, retail sales, then the closure of the tenders due at the reference date.
    /// </summary>
    /// <param name="seedDirectory">
    /// The directory holding the seed files and item images; leading and trailing blanks are ignored,
    /// as in JADE <c>validateDirectoryPath</c>.
    /// </param>
    /// <param name="referenceDate">The date that replaces JADE's current date.</param>
    /// <param name="context">
    /// The load context. Its company is replaced by the new one (JADE deletes the existing company
    /// first), and its date must be <paramref name="referenceDate"/>. Its clock is not advanced.
    /// </param>
    /// <param name="cancellationToken">
    /// Stops the load: it is observed before the existing company is deleted, before each seed file
    /// is read, once per physical line of each seed file, before each chunk of each image file,
    /// before each row is persisted and before the tender closure.
    /// </param>
    /// <returns>The loaded model, its context, the counts and the rows the agents rejected.</returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="seedDirectory"/> or <paramref name="context"/> is null.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="seedDirectory"/> is empty or blank, or the context's date is not
    /// <paramref name="referenceDate"/>.
    /// </exception>
    /// <exception cref="DirectoryNotFoundException">The seed directory does not exist.</exception>
    /// <exception cref="FileNotFoundException">A seed file does not exist.</exception>
    /// <exception cref="IOException">
    /// A seed file is not a regular file (the message names the file, not its directory), or it
    /// cannot be read.
    /// </exception>
    /// <exception cref="InvalidDataException">
    /// A seed row cannot be loaded: a seed value does not convert, an image file is missing, or an
    /// agent rate names a missing agent, category or rate or repeats an agent the rate already holds
    /// (each a JADE runtime exception); a seed date plus the day offset is outside the range of
    /// <see cref="DateOnly"/>; an image file is not a regular file or cannot be read; or a photo
    /// name is not a plain file name directly inside the seed directory, or is a symbolic link that
    /// cannot be resolved or whose final target is not an existing file directly inside it. The
    /// message names the seed file, its line and the field, category or rate at fault; a range, link
    /// or read error is the inner exception.
    /// </exception>
    /// <exception cref="InvalidOperationException">The company could not be created.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled.</exception>
    /// <remarks>
    /// <para>
    /// The arguments, the seed directory and the context's date are validated, and
    /// <paramref name="cancellationToken"/> is checked, before the context's company is deleted, so a
    /// load that throws at that point leaves the context unchanged. A load that throws once loading
    /// has begun, a cancellation included, leaves the context with no company, as JADE's aborted load
    /// leaves the database after the committed deletion of the previous company.
    /// </para>
    /// <para>
    /// A seed file or image file of length 0 is read as empty without being opened. A FIFO or a
    /// device also reports length 0, so none can block the load waiting for a writer.
    /// </para>
    /// </remarks>
    public static SeedLoadResult Load(
        string seedDirectory,
        DateOnly referenceDate,
        ModelContext context,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        string directory = ValidateDirectoryPath(seedDirectory);

        // JADE takes the offset, the agents' server date and the closure date from one current date.
        if (context.Today != referenceDate)
        {
            throw new ArgumentException(
                $"The load context's date {FormatDate(context.Today)} is not the reference date {FormatDate(referenceDate)}.",
                nameof(context));
        }

        cancellationToken.ThrowIfCancellationRequested();

        // JADE: delete Company.firstInstance(), committed before the load begins.
        context.Company = null;
        try
        {
            Company company = CreateCompany(context);

            // dateOffset (8466-8470): the days from 1 January 2000 to the current date.
            int dateOffsetDays = referenceDate.DayNumber - OffsetStartDate.DayNumber;
            var load = new LoadState(directory, context, company, dateOffsetDays, cancellationToken);

            // Each stage observes the token before it reads its file and before each row it persists.
            LoadLocations(load);
            LoadAgents(load);
            LoadClients(load);
            LoadCategories(load);
            LoadRetailItems(load);
            LoadTenderItems(load);
            LoadCategoryRates(load);
            LoadAgentRates(load);
            LoadTenders(load);
            LoadRetailSales(load);

            cancellationToken.ThrowIfCancellationRequested();

            // closeTendersAtCurrentDate (7891-7911): closeTendersAtDate( currentDate, total ).
            _ = company.CloseTendersAtDate(referenceDate, context);

            return new SeedLoadResult(
                company,
                context,
                referenceDate,
                dateOffsetDays,
                CountObjects(load),
                load.Rejections.ToArray());
        }
        catch
        {
            context.Company = null;
            throw;
        }
    }

    /// <summary>
    /// JADE <c>validateDirectoryPath</c> (<c>ErewhonModelSchema.scm:8928-8975</c>): the path trimmed of
    /// blanks must be non-empty and name an existing directory.
    /// </summary>
    private static string ValidateDirectoryPath(string seedDirectory)
    {
        ArgumentNullException.ThrowIfNull(seedDirectory);
        string directory = SeedTokenizer.TrimBlanks(seedDirectory);
        if (directory.Length == 0)
        {
            throw new ArgumentException("The seed directory path is empty.", nameof(seedDirectory));
        }

        if (!Directory.Exists(directory))
        {
            throw new DirectoryNotFoundException($"The seed directory '{directory}' does not exist.");
        }

        return directory;
    }

    /// <summary>
    /// JADE <c>createCompany</c> (<c>ErewhonModelSchema.scm:7912-7957</c>): the singleton company with
    /// the address constants of <c>ErewhonModelSchema.scm:2692-2701</c>, made the context's company
    /// (JADE <c>app.setCompany</c>).
    /// </summary>
    private static Company CreateCompany(ModelContext context)
    {
        var addressTA = new AddressTA();
        var companyTA = new CompanyTA();

        companyTA.Initialize();
        addressTA.Initialize();

        addressTA.MyModelTA = companyTA;

        companyTA.Name = Messages.CompanyName;

        addressTA.Street = Messages.CompanyAddress1;
        addressTA.City = Messages.CompanyAddress2;
        addressTA.Country = Messages.CompanyAddress3;
        addressTA.Email = Messages.CompanyEMail;
        addressTA.Fax = Messages.CompanyFax;
        addressTA.Phone = Messages.CompanyPhone;
        addressTA.WebSite = Messages.CompanyWebSite;

        bool persisted = companyTA.PersistEntityInTransState(TransactionType.Persist, context);
        Company company = (persisted ? companyTA.GetModelObject() : null)
            ?? throw new InvalidOperationException(
                $"The company could not be created: {string.Join("; ", companyTA.AllErrors)}");

        context.Company = company;
        return company;
    }

    /// <summary>
    /// JADE <c>loadLocations</c> (<c>ErewhonModelSchema.scm:8510-8585</c>): country name, region name.
    /// A country is created the first time it is named; every line creates a region of it.
    /// </summary>
    private static void LoadLocations(LoadState load)
    {
        foreach (SeedLine line in ReadDataLines(load, LocationsFile))
        {
            var cursor = new SeedTokenCursor(line.Text);

            var countryTA = new CountryTA();
            countryTA.Initialize();
            countryTA.Name = cursor.Next();

            var regionTA = new RegionTA();
            regionTA.Initialize();
            regionTA.Name = cursor.Next();

            Country? country = load.Company.AllCountries[countryTA.Name];
            if (country == null)
            {
                _ = Persist(load, countryTA, LocationsFile, line);
                country = countryTA.GetModelObject();
            }

            regionTA.MyCountry = country;
            _ = Persist(load, regionTA, LocationsFile, line);
        }
    }

    /// <summary>
    /// JADE <c>loadAgents</c> (<c>ErewhonModelSchema.scm:8162-8232</c>): name, street, city, country,
    /// email, fax, phone, web site.
    /// </summary>
    /// <remarks>
    /// Field 6 is the fax and field 7 the phone, as the loader assigns them, so Hank Williams
    /// (<c>Agents.txt:4</c>) has fax <c>1 8 6354 7364</c> and phone <c>1 8 2938 4756</c>; a column
    /// reading of the file as phone then fax disagrees, and the loader is followed.
    /// </remarks>
    private static void LoadAgents(LoadState load)
    {
        foreach (SeedLine line in ReadDataLines(load, AgentsFile))
        {
            var cursor = new SeedTokenCursor(line.Text);

            var agentTA = new AgentTA();
            agentTA.Initialize();
            agentTA.Name = cursor.Next();

            var addressTA = new AddressTA();
            addressTA.Initialize();
            addressTA.MyModelTA = agentTA;
            ReadAddress(addressTA, cursor);

            _ = Persist(load, agentTA, AgentsFile, line);
        }
    }

    /// <summary>
    /// JADE <c>loadClients</c> (<c>ErewhonModelSchema.scm:8354-8425</c>): the fields of
    /// <see cref="LoadAgents"/>, in the same order.
    /// </summary>
    /// <remarks>
    /// Pauline Wild's web site token <c>null</c> (<c>Clients.txt:1</c>) is read as the null string
    /// <c>""</c>, and Brian Olsen's email <c>"bo@wol.com "</c> (<c>Clients.txt:2</c>) as
    /// <c>bo@wol.com</c>, by the tokenizer.
    /// </remarks>
    private static void LoadClients(LoadState load)
    {
        foreach (SeedLine line in ReadDataLines(load, ClientsFile))
        {
            var cursor = new SeedTokenCursor(line.Text);

            var clientTA = new ClientTA();
            clientTA.Initialize();
            clientTA.Name = cursor.Next();

            var addressTA = new AddressTA();
            addressTA.Initialize();
            addressTA.MyModelTA = clientTA;
            ReadAddress(addressTA, cursor);

            _ = Persist(load, clientTA, ClientsFile, line);
        }
    }

    /// <summary>
    /// JADE <c>loadCategories</c> (<c>ErewhonModelSchema.scm:8233-8290</c>): prefix, name, description.
    /// </summary>
    private static void LoadCategories(LoadState load)
    {
        foreach (SeedLine line in ReadDataLines(load, CategoriesFile))
        {
            var cursor = new SeedTokenCursor(line.Text);

            var itemCategoryTA = new ItemCategoryTA();
            itemCategoryTA.Initialize();
            itemCategoryTA.Prefix = cursor.Next();
            itemCategoryTA.Name = cursor.Next();
            itemCategoryTA.Description = cursor.Next();

            _ = Persist(load, itemCategoryTA, CategoriesFile, line);
        }
    }

    /// <summary>
    /// JADE <c>loadRetailItems</c> (<c>ErewhonModelSchema.scm:8586-8680</c>): guid, short description,
    /// full description, image file, price, listed date, agent, country, region, category.
    /// </summary>
    /// <remarks>
    /// Codes continue one shared sequence starting at 1, so retail line n gets code n (<c>ANQU/1</c>
    /// is the Lamp, <c>RetailItems.txt:1</c>).
    /// </remarks>
    private static void LoadRetailItems(LoadState load)
    {
        foreach (SeedLine line in ReadDataLines(load, RetailItemsFile))
        {
            var cursor = new SeedTokenCursor(line.Text);

            string guid = cursor.Next();
            string shortDescription = cursor.Next();
            string fullDescription = cursor.Next();
            string photoFileName = cursor.Next();
            decimal price = ToDecimal(cursor.Next(), RetailItemsFile, line, "price");
            DateOnly listedDate = ToDate(cursor.Next(), RetailItemsFile, line, "listedDate");
            string agentName = cursor.Next();
            string countryName = cursor.Next();
            string regionName = cursor.Next();
            string categoryName = cursor.Next();

            var retailItemTA = new RetailItemTA();
            retailItemTA.Initialize();
            AssignItem(
                load,
                retailItemTA,
                new ItemRow(guid, shortDescription, fullDescription, photoFileName, price, listedDate, agentName, countryName, regionName, categoryName),
                RetailItemsFile,
                line);

            _ = Persist(load, retailItemTA, RetailItemsFile, line);
        }
    }

    /// <summary>
    /// JADE <c>loadTenderItems</c> (<c>ErewhonModelSchema.scm:8755-8851</c>): guid, short description,
    /// full description, image file, reserve price, listed date, closure date, agent, country,
    /// region, category.
    /// </summary>
    /// <remarks>
    /// The codes follow the 58 retail items, so tender line n gets code 58 + n (<c>PROP/59</c> and
    /// <c>PROP/70</c> are the Castles of <c>TenderItems.txt:1</c> and <c>:12</c>).
    /// </remarks>
    private static void LoadTenderItems(LoadState load)
    {
        foreach (SeedLine line in ReadDataLines(load, TenderItemsFile))
        {
            var cursor = new SeedTokenCursor(line.Text);

            string guid = cursor.Next();
            string shortDescription = cursor.Next();
            string fullDescription = cursor.Next();
            string photoFileName = cursor.Next();
            decimal reservePrice = ToDecimal(cursor.Next(), TenderItemsFile, line, "reservePrice");
            DateOnly listedDate = ToDate(cursor.Next(), TenderItemsFile, line, "listedDate");
            DateOnly closureDate = ToDate(cursor.Next(), TenderItemsFile, line, "closureDate");
            string agentName = cursor.Next();
            string countryName = cursor.Next();
            string regionName = cursor.Next();
            string categoryName = cursor.Next();

            var tenderItemTA = new TenderItemTA();
            tenderItemTA.Initialize();
            AssignItem(
                load,
                tenderItemTA,
                new ItemRow(guid, shortDescription, fullDescription, photoFileName, reservePrice, listedDate, agentName, countryName, regionName, categoryName),
                TenderItemsFile,
                line);
            tenderItemTA.ClosureDate = AddDateOffset(load, closureDate, TenderItemsFile, line, "closureDate");

            _ = Persist(load, tenderItemTA, TenderItemsFile, line);
        }
    }

    /// <summary>
    /// JADE <c>loadCategoryRates</c> (<c>ErewhonModelSchema.scm:8291-8353</c>): category, percentage.
    /// </summary>
    private static void LoadCategoryRates(LoadState load)
    {
        foreach (SeedLine line in ReadDataLines(load, CategoryRatesFile))
        {
            var cursor = new SeedTokenCursor(line.Text);

            string categoryName = cursor.Next();
            decimal percentage = ToDecimal(cursor.Next(), CategoryRatesFile, line, "percentage");

            var commissionRateTA = new CommissionRateTA();
            commissionRateTA.Initialize();
            commissionRateTA.MyItemCategory = load.Company.AllItemCategoriesByName[categoryName];
            commissionRateTA.Percentage = percentage;

            _ = Persist(load, commissionRateTA, CategoryRatesFile, line);
        }
    }

    /// <summary>
    /// JADE <c>loadAgentRates</c> (<c>ErewhonModelSchema.scm:8086-8161</c>): agent, category, rate
    /// percentage. Each line updates the category's existing rate of that percentage so that it is
    /// also the agent's rate.
    /// </summary>
    /// <remarks>
    /// JADE dereferences the category and the rate and adds the agent without checking any of them,
    /// so a missing one raises a runtime exception; here it is an <see cref="InvalidDataException"/>,
    /// as is a line repeating an agent the rate already holds (JADE raises on the duplicate add).
    /// </remarks>
    private static void LoadAgentRates(LoadState load)
    {
        foreach (SeedLine line in ReadDataLines(load, AgentRatesFile))
        {
            var cursor = new SeedTokenCursor(line.Text);

            string agentName = cursor.Next();
            string categoryName = cursor.Next();
            decimal ratePercent = ToDecimal(cursor.Next(), AgentRatesFile, line, "ratePercent");

            Agent? agent = load.Company.AllAgents[agentName];
            ItemCategory category = load.Company.AllItemCategoriesByName[categoryName]
                ?? throw RowError(AgentRatesFile, line, $"no item category is named '{categoryName}'");
            CommissionRate rate = category.AllCommissionRates.GetAtKey(ratePercent)
                ?? throw RowError(
                    AgentRatesFile,
                    line,
                    $"category '{categoryName}' has no commission rate of {ratePercent.ToString(CultureInfo.InvariantCulture)}");

            var commissionRateTA = new CommissionRateTA();
            commissionRateTA.Initialize();
            commissionRateTA.PopulateFromObject(rate);

            if (agent == null)
            {
                // Agent names are personal data, so the row and field identify the reference.
                throw RowError(AgentRatesFile, line, "field 'agent' names no loaded agent");
            }

            if (commissionRateTA.AllAgents.Includes(agent))
            {
                throw RowError(AgentRatesFile, line, "field 'agent' names an agent that already uses this commission rate");
            }

            commissionRateTA.AllAgents.Add(agent);

            if (Persist(load, commissionRateTA, AgentRatesFile, line))
            {
                load.AgentRatesUpdated++;
            }
        }
    }

    // Tender load follows TenderTA, not the tender-sale count stated for the seed (KL-STANDIN-TENDER-LOAD).
    //
    // TenderTA.doValidate (ErewhonModelSchema.scm:13869-13948) rejects a tender whose item isClosed(),
    // that is whose closure date is before the current date (7207-7222). Closure dates are offset like
    // every other seed date, so at load three items have already closed: item 80 (Settee, seed closure
    // 29 November 1999, TenderItems.txt:22), item 92 (Coffee Mill, 19 November 1999, TenderItems.txt:34)
    // and item 106 (Camel trekking, 12 December 1999, TenderItems.txt:48). Their 60 tenders
    // (Tenders.txt:21-40, 81-100 and 141-160) are rejected with Messages.TenderItemAlreadyClosed.
    // Item 70 (Castle, seed closure 1 January 2000, which the offset makes the reference date itself,
    // TenderItems.txt:12) is still open, keeps its 20 tenders and is closed by the load's tender
    // closure; items 64, 86, 98 and 100 stay open.
    //
    // The agents therefore give 100 tenders and one tender sale (item 70, won by Philip Jenkins with
    // 6500931.00, Tenders.txt:121-140), not the 160 tenders and four tender sales (items 70, 80, 92 and
    // 106) stated for the seed. The rule is ported as written and validation is never bypassed to reach
    // four sales; the difference is a registered assumption. PROP/70 is still a sold tender item.
    /// <summary>
    /// JADE <c>loadTenders</c> (<c>ErewhonModelSchema.scm:8852-8927</c>): offer, date, time, client,
    /// tender-item guid.
    /// </summary>
    private static void LoadTenders(LoadState load)
    {
        foreach (SeedLine line in ReadDataLines(load, TendersFile))
        {
            var cursor = new SeedTokenCursor(line.Text);

            decimal offer = ToDecimal(cursor.Next(), TendersFile, line, "offer");
            DateOnly date = ToDate(cursor.Next(), TendersFile, line, "date");
            TimeOnly time = ToTime(cursor.Next(), TendersFile, line, "time");
            string clientName = cursor.Next();
            string guid = cursor.Next();

            var tenderTA = new TenderTA();
            tenderTA.Initialize();
            tenderTA.Offer = offer;
            tenderTA.DateTendered = ToTimeStamp(load, date, time, TendersFile, line, "date");
            tenderTA.MyClient = load.Company.AllClients[clientName];
            tenderTA.MyTenderItem = load.Company.AllTenderItems.GetAtKey(guid);

            _ = Persist(load, tenderTA, TendersFile, line);
        }
    }

    /// <summary>
    /// JADE <c>loadRetailSales</c> (<c>ErewhonModelSchema.scm:8681-8754</c>): date, time, price,
    /// client, retail-item guid. The sale computes its agent commission when it is created.
    /// </summary>
    private static void LoadRetailSales(LoadState load)
    {
        foreach (SeedLine line in ReadDataLines(load, RetailSalesFile))
        {
            var cursor = new SeedTokenCursor(line.Text);

            DateOnly date = ToDate(cursor.Next(), RetailSalesFile, line, "date");
            TimeOnly time = ToTime(cursor.Next(), RetailSalesFile, line, "time");
            decimal price = ToDecimal(cursor.Next(), RetailSalesFile, line, "price");
            string clientName = cursor.Next();
            string guid = cursor.Next();

            var retailSaleTA = new RetailSaleTA();
            retailSaleTA.Initialize();
            retailSaleTA.DateSold = ToTimeStamp(load, date, time, RetailSalesFile, line, "date");
            retailSaleTA.SalePrice = price;
            retailSaleTA.MyClient = load.Company.AllClients[clientName];
            retailSaleTA.MyItem = load.Company.AllRetailItems.GetAtKey(guid);

            _ = Persist(load, retailSaleTA, RetailSalesFile, line);
        }
    }

    /// <summary>
    /// The address fields every agent and client line holds after the name, in loader order: street,
    /// city, country, email, fax, phone, web site.
    /// </summary>
    private static void ReadAddress(AddressTA addressTA, SeedTokenCursor cursor)
    {
        addressTA.Street = cursor.Next();
        addressTA.City = cursor.Next();
        addressTA.Country = cursor.Next();
        addressTA.Email = cursor.Next();
        addressTA.Fax = cursor.Next();
        addressTA.Phone = cursor.Next();
        addressTA.WebSite = cursor.Next();
    }

    /// <summary>
    /// The item values the retail and tender loaders assign alike, in loader order: the seed guid,
    /// the next code, the description, the agent, region and category looked up by name (null when
    /// missing, which the agent rejects), the image bytes, the price, the name and the offset listed
    /// date.
    /// </summary>
    private static void AssignItem(LoadState load, ItemTA itemTA, in ItemRow row, string fileName, SeedLine line)
    {
        itemTA.Guid = row.Guid;
        itemTA.CodeNumber = itemTA.GetNextCodeNumber(load.Context);
        itemTA.Description = row.FullDescription;
        itemTA.MyAgent = load.Company.AllAgents[row.AgentName];
        itemTA.MyRegion = load.Company.GetRegion(row.CountryName, row.RegionName);
        itemTA.MyItemCategory = load.Company.AllItemCategoriesByName[row.CategoryName];
        itemTA.Photo = LoadPicture(load, row.PhotoFileName, fileName, line);
        itemTA.Price = row.Price;
        itemTA.Name = row.ShortDescription;
        itemTA.ListedDate = AddDateOffset(load, row.ListedDate, fileName, line, "listedDate");
    }

    /// <summary>
    /// JADE <c>app.loadPicture( self.dirPath &amp; fileName )</c> (<c>ErewhonModelSchema.scm:8657,
    /// 8827</c>): the raw bytes of the image file (KL-STANDIN-PHOTO).
    /// </summary>
    /// <remarks>
    /// <para>
    /// The name is appended to the directory as JADE concatenates it, so a rooted name cannot select
    /// a file outside the seed directory; <see cref="Path.Combine(string, string)"/> would discard the
    /// directory for one. The name's case is used as written.
    /// </para>
    /// <para>
    /// Only a file directly inside the seed directory is read, as the seed set is flat. The name must
    /// be a plain file name (<see cref="IsPlainFileName"/>), so <c>../</c> and every other path form
    /// is refused before anything is looked up, and a name that is a symbolic link is read only
    /// through a final target that is an existing file directly inside the seed directory
    /// (<see cref="ResolveContainedLink"/>). A refused name is a row error for field <c>photo</c>.
    /// </para>
    /// </remarks>
    /// <exception cref="InvalidDataException">
    /// The name is refused, names no existing file or no regular file, or the file cannot be read
    /// (the read error is the inner exception). The message names the seed file, its line and field
    /// <c>photo</c>.
    /// </exception>
    /// <exception cref="OperationCanceledException">The load was cancelled while the image was read.</exception>
    private static byte[] LoadPicture(LoadState load, string photoFileName, string fileName, SeedLine line)
    {
        if (!IsPlainFileName(photoFileName))
        {
            throw RowError(fileName, line, $"field 'photo' value '{photoFileName}' is not a file name directly inside the seed directory");
        }

        var image = new FileInfo(Path.Join(load.SeedDirectory, photoFileName));
        if (!image.Exists)
        {
            throw RowError(fileName, line, $"field 'photo' image file '{photoFileName}' does not exist in the seed directory");
        }

        try
        {
            if (image.LinkTarget != null)
            {
                image = ResolveContainedLink(load, image, photoFileName, fileName, line);
            }

            return ReadPicture(load, image, photoFileName, fileName, line);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The message names the row and the seed's own file name only; the inner exception
            // keeps the host path.
            string reason = ex switch
            {
                UnauthorizedAccessException => "access is denied",
                FileNotFoundException or DirectoryNotFoundException => "it no longer exists",
                _ => "an I/O error occurred",
            };
            throw RowError(fileName, line, $"field 'photo' image file '{photoFileName}' could not be read: {reason}", ex);
        }
    }

    /// <summary>
    /// The bytes <see cref="File.ReadAllBytes(string)"/> returns for an image file, read so that a
    /// reset can stop: an entry of length 0 is not opened, an opened entry that cannot seek is
    /// refused, and the file is read in chunks of <see cref="PictureReadChunkSize"/> bytes with the
    /// load's cancellation token observed before each.
    /// </summary>
    /// <remarks>
    /// An empty file gives the empty image, which <see cref="Item.Photo"/> stores as null. A FIFO or a
    /// device also reports length 0, and opening one for reading waits for a writer, which no
    /// cancellation can interrupt, so it gives the empty image too instead of blocking the reset.
    /// </remarks>
    /// <param name="load">The load, whose cancellation token is observed.</param>
    /// <param name="image">The image file, a symbolic link already resolved to its final target.</param>
    /// <param name="photoFileName">The image name as the seed line gives it, for messages.</param>
    /// <param name="fileName">The seed file of the row, for messages.</param>
    /// <param name="line">The seed line of the row, for messages.</param>
    /// <returns>The file's bytes; empty for an entry of length 0.</returns>
    /// <exception cref="InvalidDataException">
    /// The opened entry is not a regular file. The message names the seed file, its line and field
    /// <c>photo</c>.
    /// </exception>
    /// <exception cref="IOException">
    /// The file cannot be read, is longer than an array can hold, or ends before its length
    /// (<see cref="EndOfStreamException"/>), as with <see cref="File.ReadAllBytes(string)"/>.
    /// </exception>
    /// <exception cref="UnauthorizedAccessException">The file cannot be opened for reading.</exception>
    /// <exception cref="OperationCanceledException">The load was cancelled.</exception>
    private static byte[] ReadPicture(LoadState load, FileInfo image, string photoFileName, string fileName, SeedLine line)
    {
        load.CancellationToken.ThrowIfCancellationRequested();

        // Taken now rather than from the state cached when the name was checked, so the length
        // describes the entry as it is just before the open.
        image.Refresh();
        if (image.Length == 0)
        {
            return [];
        }

        // Unbuffered: each chunk is read straight into the result.
        using var stream = new FileStream(image.FullName, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize: 0, FileOptions.SequentialScan);
        if (!stream.CanSeek)
        {
            throw RowError(fileName, line, $"field 'photo' image file '{photoFileName}' is not a regular file");
        }

        // The length of the opened file, as File.ReadAllBytes takes it, not the one checked above.
        long length = stream.Length;
        if (length > Array.MaxLength)
        {
            throw new IOException(string.Create(
                CultureInfo.InvariantCulture,
                $"The image file '{photoFileName}' is longer than the {Array.MaxLength} bytes an image can hold."));
        }

        var bytes = new byte[length];
        var offset = 0;
        while (offset < bytes.Length)
        {
            load.CancellationToken.ThrowIfCancellationRequested();
            int read = stream.Read(bytes, offset, Math.Min(PictureReadChunkSize, bytes.Length - offset));
            if (read == 0)
            {
                // File.ReadAllBytes fails the same way when the file shrinks while it is read.
                throw new EndOfStreamException();
            }

            offset += read;
        }

        return bytes;
    }

    /// <summary>
    /// Whether an image name is a plain file name: not empty, <c>.</c> or <c>..</c>, free of both
    /// directory separators on every platform and of every other character
    /// <see cref="Path.GetInvalidFileNameChars"/> lists, and its own <see cref="Path.GetFileName(string)"/>.
    /// </summary>
    private static bool IsPlainFileName(string name) =>
        name.Length > 0
        && name is not ("." or "..")
        && !name.Contains('/', StringComparison.Ordinal)
        && !name.Contains('\\', StringComparison.Ordinal)
        && name.IndexOfAny(Path.GetInvalidFileNameChars()) < 0
        && string.Equals(Path.GetFileName(name), name, StringComparison.Ordinal);

    /// <summary>
    /// The final target of an image name that is a symbolic link, accepted only when it is an
    /// existing file that is not itself a link and lies directly inside the seed directory, which
    /// is the flat set of files <c>SeedFingerprint</c> hashes.
    /// </summary>
    /// <returns>The target, read in place of the link.</returns>
    /// <exception cref="InvalidDataException">
    /// The link cannot be resolved (the resolution error is the inner exception), or its target is
    /// anything else.
    /// </exception>
    private static FileInfo ResolveContainedLink(LoadState load, FileInfo link, string photoFileName, string fileName, SeedLine line)
    {
        FileInfo? target;
        try
        {
            // The target's path is normalised, so it is checked again as a file and as a non-link,
            // and that checked path, not the link, is the one read.
            target = link.ResolveLinkTarget(returnFinalTarget: true) as FileInfo;
            if (target != null
                && (!target.Exists
                    || target.LinkTarget != null
                    || !IsSeedDirectory(load.SeedDirectory, Path.GetDirectoryName(target.FullName))))
            {
                target = null;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw RowError(fileName, line, $"field 'photo' value '{photoFileName}' is a symbolic link that cannot be resolved", ex);
        }

        return target
            ?? throw RowError(fileName, line, $"field 'photo' value '{photoFileName}' is a symbolic link to no file directly inside the seed directory");
    }

    /// <summary>
    /// Whether <paramref name="directory"/> is the seed directory itself, compared ordinally with any
    /// trailing separator removed, against the directory as configured and, when that is a symbolic
    /// link, against its final target.
    /// </summary>
    private static bool IsSeedDirectory(string seedDirectory, string? directory)
    {
        if (directory == null)
        {
            return false;
        }

        string candidate = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
        var seed = new DirectoryInfo(Path.TrimEndingDirectorySeparator(Path.GetFullPath(seedDirectory)));
        if (string.Equals(candidate, seed.FullName, StringComparison.Ordinal))
        {
            return true;
        }

        return seed.LinkTarget != null
            && seed.ResolveLinkTarget(returnFinalTarget: true) is { } canonicalSeed
            && string.Equals(candidate, Path.TrimEndingDirectorySeparator(canonicalSeed.FullName), StringComparison.Ordinal);
    }

    /// <summary>
    /// Persists one row's agent with <see cref="TransactionType.Persist"/>. JADE ignores the result,
    /// so a rejected row is simply not created; here it is also recorded with the agent's errors.
    /// A cancelled load stops here, before the row is applied.
    /// </summary>
    /// <returns>True when the agent applied the row.</returns>
    private static bool Persist(LoadState load, ModelTA transactionAgent, string fileName, SeedLine line)
    {
        load.CancellationToken.ThrowIfCancellationRequested();
        if (transactionAgent.PersistEntityInTransState(TransactionType.Persist, load.Context))
        {
            return true;
        }

        load.Rejections.Add(new SeedRowRejection(fileName, line.LineNumber, transactionAgent.AllErrors.ToArray()));
        return false;
    }

    /// <summary>
    /// Reads the data lines of one seed file, named explicitly, from the seed directory. Each load
    /// stage starts here, so a cancelled load stops before it opens the next file, and the tokenizer
    /// observes the token once per physical line of the file.
    /// </summary>
    private static IReadOnlyList<SeedLine> ReadDataLines(LoadState load, string fileName)
    {
        load.CancellationToken.ThrowIfCancellationRequested();
        return SeedTokenizer.ReadLines(Path.Combine(load.SeedDirectory, fileName), load.CancellationToken);
    }

    /// <summary>
    /// JADE <c>String.Decimal</c> into a <c>Decimal[n,2]</c> local: an optionally signed decimal
    /// number, rounded to two places.
    /// </summary>
    private static decimal ToDecimal(string token, string fileName, SeedLine line, string field)
    {
        if (!decimal.TryParse(token, DecimalStyles, CultureInfo.InvariantCulture, out decimal value))
        {
            throw ConversionError(fileName, line, field, token, "decimal number");
        }

        return JadeValues.RoundScale2(value);
    }

    /// <summary>
    /// JADE <c>String.Date</c> for the seed's long-month forms. Runs of spaces are read as one
    /// (<c>"September 9  1999"</c>, <c>TenderItems.txt:6</c>).
    /// </summary>
    private static DateOnly ToDate(string token, string fileName, SeedLine line, string field)
    {
        string text = string.Join(' ', token.Split(' ', StringSplitOptions.RemoveEmptyEntries));
        if (!DateOnly.TryParseExact(text, DateFormats, CultureInfo.InvariantCulture, DateTimeStyles.None, out DateOnly date))
        {
            throw ConversionError(fileName, line, field, token, "date");
        }

        return date;
    }

    /// <summary>JADE <c>String.Time</c> for the seed's <c>HH:mm:ss</c> form.</summary>
    private static TimeOnly ToTime(string token, string fileName, SeedLine line, string field)
    {
        if (!TimeOnly.TryParseExact(token, TimeFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out TimeOnly time))
        {
            throw ConversionError(fileName, line, field, token, "time");
        }

        return time;
    }

    /// <summary>
    /// JADE <c>timeStamp.setDate( date + self.dateOffset ); timeStamp.setTime( time )</c>, in UTC
    /// because the JADE applications run in <c>Etc/UTC</c>.
    /// </summary>
    /// <exception cref="InvalidDataException">The offset date is out of range (<see cref="AddDateOffset"/>).</exception>
    private static DateTime ToTimeStamp(LoadState load, DateOnly date, TimeOnly time, string fileName, SeedLine line, string field) =>
        AddDateOffset(load, date, fileName, line, field).ToDateTime(time, DateTimeKind.Utc);

    /// <summary>
    /// JADE <c>date + self.dateOffset</c>: a seed date moved forward by the load's day offset.
    /// </summary>
    /// <exception cref="InvalidDataException">
    /// The offset date is outside the range of <see cref="DateOnly"/>, for example
    /// <c>December 31 9999</c> with a positive offset; the range error is the inner exception.
    /// </exception>
    private static DateOnly AddDateOffset(LoadState load, DateOnly date, string fileName, SeedLine line, string field)
    {
        try
        {
            return date.AddDays(load.DateOffsetDays);
        }
        catch (ArgumentOutOfRangeException ex)
        {
            throw RowError(
                fileName,
                line,
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"field '{field}' value '{FormatDate(date)}' plus the date offset of {load.DateOffsetDays} days is outside the supported date range"),
                ex);
        }
    }

    /// <summary>
    /// The counts of the objects the loaded company holds. Agent rates are the agent-rate lines
    /// whose update succeeded, and tender sales are the sales the closure created.
    /// </summary>
    private static SeedLoadCounts CountObjects(LoadState load)
    {
        Company company = load.Company;
        return new SeedLoadCounts(
            Countries: company.AllCountries.Count,
            Regions: company.AllCountries.Sum(country => country.AllRegions.Count),
            Agents: company.AllAgents.Count,
            Clients: company.AllClients.Count,
            Categories: company.AllItemCategoriesByName.Count,
            CommissionRates: company.AllItemCategoriesByName.Sum(category => category.AllCommissionRates.Count),
            AgentRates: load.AgentRatesUpdated,
            RetailItems: company.AllRetailItems.Count,
            TenderItems: company.AllTenderItems.Count,
            Tenders: company.AllTenders.Count,
            RetailSales: company.AllSalesByItem.OfType<RetailSale>().Count(),

            // Counted from the sales that exist rather than from CloseTendersAtDate's result, which
            // also counts a winning tender whose sale TenderSaleTA rejected.
            TenderSales: company.AllSalesByItem.OfType<TenderSale>().Count());
    }

    private static InvalidDataException ConversionError(string fileName, SeedLine line, string field, string token, string kind) =>
        RowError(fileName, line, $"field '{field}' value '{token}' is not a valid {kind}");

    private static InvalidDataException RowError(string fileName, SeedLine line, string problem, Exception? innerException = null) =>
        new(string.Create(CultureInfo.InvariantCulture, $"{fileName} line {line.LineNumber}: {problem}."), innerException);

    private static string FormatDate(DateOnly date) => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    /// <summary>The values one retail or tender item line supplies to <see cref="AssignItem"/>.</summary>
    private readonly record struct ItemRow(
        string Guid,
        string ShortDescription,
        string FullDescription,
        string PhotoFileName,
        decimal Price,
        DateOnly ListedDate,
        string AgentName,
        string CountryName,
        string RegionName,
        string CategoryName);

    /// <summary>
    /// The state of one load: JADE <c>InitialDataLoader</c>'s <c>dirPath</c>, <c>myCompany</c> and
    /// <c>dateOffset</c>, plus what this port reports. A new instance per call keeps loads independent.
    /// </summary>
    private sealed class LoadState(
        string seedDirectory,
        ModelContext context,
        Company company,
        int dateOffsetDays,
        CancellationToken cancellationToken)
    {
        public string SeedDirectory { get; } = seedDirectory;

        public ModelContext Context { get; } = context;

        public Company Company { get; } = company;

        public int DateOffsetDays { get; } = dateOffsetDays;

        // Observed by ReadDataLines once per file, by SeedTokenizer.ReadLines once per line, by
        // ReadPicture before each image chunk and by Persist once per row.
        public CancellationToken CancellationToken { get; } = cancellationToken;

        public List<SeedRowRejection> Rejections { get; } = [];

        public int AgentRatesUpdated { get; set; }
    }
}

/// <summary>The outcome of <see cref="SeedLoader.Load(string, DateOnly, ModelContext, CancellationToken)"/>.</summary>
/// <param name="Company">The loaded company, also the context's company.</param>
/// <param name="Context">The load context; its clock is still the load time.</param>
/// <param name="ReferenceDate">The date that replaced JADE's current date.</param>
/// <param name="DateOffsetDays">The days from 1 January 2000 to the reference date, added to every seed date.</param>
/// <param name="Counts">The objects the company holds after the load.</param>
/// <param name="Rejections">The rows the transaction agents rejected, in load order.</param>
public sealed record SeedLoadResult(
    Company Company,
    ModelContext Context,
    DateOnly ReferenceDate,
    int DateOffsetDays,
    SeedLoadCounts Counts,
    IReadOnlyList<SeedRowRejection> Rejections)
{
    /// <summary>
    /// One invariant-culture line with the counts and the rejected rows, grouped by file and errors
    /// with their line ranges, for example
    /// <c>seed loaded: 6 countries, ...; 60 rows rejected by TA validation [Tenders.txt lines 21-40,
    /// 81-100, 141-160: The Tender Item has already closed]</c>.
    /// </summary>
    public string Describe()
    {
        SeedLoadCounts counts = Counts;
        var text = new StringBuilder();
        text.Append(
            CultureInfo.InvariantCulture,
            $"seed loaded: {counts.Countries} countries, {counts.Regions} regions, {counts.Agents} agents, {counts.Clients} clients, {counts.Categories} categories, {counts.CommissionRates} commission rates, {counts.AgentRates} agent rates, {counts.RetailItems} retail items, {counts.TenderItems} tender items, {counts.Tenders} tenders, {counts.RetailSales} retail sales, {counts.TenderSales} tender sales; {Rejections.Count} rows rejected by TA validation");

        var groups = Rejections
            .GroupBy(rejection => (rejection.FileName, Errors: string.Join(" | ", rejection.Errors)))
            .Select(group => (group.Key.FileName, group.Key.Errors, Lines: group.Select(rejection => rejection.LineNumber).Distinct().Order().ToArray()));
        foreach ((string fileName, string errors, int[] lines) in groups)
        {
            text.Append(
                CultureInfo.InvariantCulture,
                $" [{fileName} {(lines.Length == 1 ? "line" : "lines")} {FormatLineRanges(lines)}: {errors}]");
        }

        return text.ToString();
    }

    // Consecutive line numbers as ranges: 21, 22, 23, 81 gives "21-23, 81".
    private static string FormatLineRanges(int[] lines)
    {
        var ranges = new List<string>();
        int start = 0;
        for (int i = 1; i <= lines.Length; i++)
        {
            if (i == lines.Length || lines[i] != lines[i - 1] + 1)
            {
                ranges.Add(i - 1 == start
                    ? lines[start].ToString(CultureInfo.InvariantCulture)
                    : string.Create(CultureInfo.InvariantCulture, $"{lines[start]}-{lines[i - 1]}"));
                start = i;
            }
        }

        return string.Join(", ", ranges);
    }
}

/// <summary>The objects the company holds after a seed load.</summary>
/// <param name="Countries">Countries.</param>
/// <param name="Regions">Regions, over every country.</param>
/// <param name="Agents">Agents.</param>
/// <param name="Clients">Clients.</param>
/// <param name="Categories">Item categories.</param>
/// <param name="CommissionRates">Commission rates, over every category.</param>
/// <param name="AgentRates">Agent-rate lines whose commission-rate update succeeded.</param>
/// <param name="RetailItems">Retail items.</param>
/// <param name="TenderItems">Tender items.</param>
/// <param name="Tenders">Tenders.</param>
/// <param name="RetailSales">Retail sales.</param>
/// <param name="TenderSales">Tender sales, all created by the load's tender closure.</param>
public sealed record SeedLoadCounts(
    int Countries,
    int Regions,
    int Agents,
    int Clients,
    int Categories,
    int CommissionRates,
    int AgentRates,
    int RetailItems,
    int TenderItems,
    int Tenders,
    int RetailSales,
    int TenderSales);

/// <summary>A seed row whose transaction agent rejected it, so its object was not created.</summary>
/// <param name="FileName">The seed file, for example <c>Tenders.txt</c>.</param>
/// <param name="LineNumber">The 1-based physical line number.</param>
/// <param name="Errors">The agent's errors, in the order raised.</param>
public sealed record SeedRowRejection(string FileName, int LineNumber, IReadOnlyList<string> Errors);
