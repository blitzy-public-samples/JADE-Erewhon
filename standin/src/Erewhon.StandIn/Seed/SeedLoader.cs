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

    /// <summary>
    /// Loads the seed with a new load context: no company yet, guids from <paramref name="newGuid"/>
    /// and the clock at <paramref name="referenceDate"/> 12:00:00 UTC.
    /// </summary>
    /// <param name="seedDirectory">The directory holding the seed files and item images.</param>
    /// <param name="referenceDate">The date that replaces JADE's current date.</param>
    /// <param name="newGuid">
    /// Generates the guid of every entity created without a seed guid, in creation order.
    /// </param>
    /// <returns>The loaded model, its context, the counts and the rows the agents rejected.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="newGuid"/> is null.</exception>
    /// <inheritdoc cref="Load(string, DateOnly, ModelContext)" path="/exception"/>
    public static SeedLoadResult Load(string seedDirectory, DateOnly referenceDate, Func<string> newGuid)
    {
        ArgumentNullException.ThrowIfNull(newGuid);
        var context = new ModelContext(null, newGuid, referenceDate.ToDateTime(LoadTimeOfDay, DateTimeKind.Utc));
        return Load(seedDirectory, referenceDate, context);
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
    /// <exception cref="InvalidDataException">
    /// A seed value does not convert, an image file is missing, or an agent rate names a missing
    /// agent, category or rate (each a JADE runtime exception).
    /// </exception>
    /// <exception cref="InvalidOperationException">The company could not be created.</exception>
    /// <remarks>
    /// When the load throws, the context is left with no company, as JADE's aborted load leaves the
    /// database after the committed deletion of the previous company.
    /// </remarks>
    public static SeedLoadResult Load(string seedDirectory, DateOnly referenceDate, ModelContext context)
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

        // JADE: delete Company.firstInstance(), committed before the load begins.
        context.Company = null;
        try
        {
            Company company = CreateCompany(context);

            // dateOffset (8466-8470): the days from 1 January 2000 to the current date.
            int dateOffsetDays = referenceDate.DayNumber - OffsetStartDate.DayNumber;
            var load = new LoadState(directory, context, company, dateOffsetDays);

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
            tenderItemTA.ClosureDate = closureDate.AddDays(load.DateOffsetDays);

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
                throw RowError(AgentRatesFile, line, $"no agent is named '{agentName}'");
            }

            if (commissionRateTA.AllAgents.Includes(agent))
            {
                throw RowError(AgentRatesFile, line, $"agent '{agentName}' already uses this commission rate");
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
            tenderTA.DateTendered = ToTimeStamp(load, date, time);
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
            retailSaleTA.DateSold = ToTimeStamp(load, date, time);
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
        itemTA.ListedDate = row.ListedDate.AddDays(load.DateOffsetDays);
    }

    /// <summary>
    /// JADE <c>app.loadPicture( self.dirPath &amp; fileName )</c> (<c>ErewhonModelSchema.scm:8657,
    /// 8827</c>): the raw bytes of the image file (KL-STANDIN-PHOTO).
    /// </summary>
    /// <remarks>
    /// The name is appended to the directory as JADE concatenates it, so a rooted name cannot select
    /// a file outside the seed directory; <see cref="Path.Combine(string, string)"/> would discard the
    /// directory for one. The name's case is used as written.
    /// </remarks>
    private static byte[] LoadPicture(LoadState load, string photoFileName, string fileName, SeedLine line)
    {
        string path = Path.Join(load.SeedDirectory, photoFileName);
        if (photoFileName.Length == 0 || !File.Exists(path))
        {
            throw RowError(fileName, line, $"the image file '{photoFileName}' does not exist in the seed directory");
        }

        return File.ReadAllBytes(path);
    }

    /// <summary>
    /// Persists one row's agent with <see cref="TransactionType.Persist"/>. JADE ignores the result,
    /// so a rejected row is simply not created; here it is also recorded with the agent's errors.
    /// </summary>
    /// <returns>True when the agent applied the row.</returns>
    private static bool Persist(LoadState load, ModelTA transactionAgent, string fileName, SeedLine line)
    {
        if (transactionAgent.PersistEntityInTransState(TransactionType.Persist, load.Context))
        {
            return true;
        }

        load.Rejections.Add(new SeedRowRejection(fileName, line.LineNumber, transactionAgent.AllErrors.ToArray()));
        return false;
    }

    /// <summary>Reads the data lines of one seed file, named explicitly, from the seed directory.</summary>
    private static IReadOnlyList<SeedLine> ReadDataLines(LoadState load, string fileName) =>
        SeedTokenizer.ReadLines(Path.Combine(load.SeedDirectory, fileName));

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
    private static DateTime ToTimeStamp(LoadState load, DateOnly date, TimeOnly time) =>
        date.AddDays(load.DateOffsetDays).ToDateTime(time, DateTimeKind.Utc);

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

    private static InvalidDataException RowError(string fileName, SeedLine line, string problem) =>
        new(string.Create(CultureInfo.InvariantCulture, $"{fileName} line {line.LineNumber}: {problem}."));

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
    private sealed class LoadState(string seedDirectory, ModelContext context, Company company, int dateOffsetDays)
    {
        public string SeedDirectory { get; } = seedDirectory;

        public ModelContext Context { get; } = context;

        public Company Company { get; } = company;

        public int DateOffsetDays { get; } = dateOffsetDays;

        public List<SeedRowRejection> Rejections { get; } = [];

        public int AgentRatesUpdated { get; set; }
    }
}

/// <summary>The outcome of <see cref="SeedLoader.Load(string, DateOnly, ModelContext)"/>.</summary>
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

