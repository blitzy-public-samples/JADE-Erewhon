using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Erewhon.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class InitialSchema : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "companies",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    name = table.Column<string>(type: "character varying(81)", maxLength: 81, nullable: false),
                    address_street = table.Column<string>(type: "character varying(51)", maxLength: 51, nullable: false),
                    address_city = table.Column<string>(type: "character varying(51)", maxLength: 51, nullable: false),
                    address_country = table.Column<string>(type: "character varying(51)", maxLength: 51, nullable: false),
                    address_phone = table.Column<string>(type: "character varying(26)", maxLength: 26, nullable: false),
                    address_fax = table.Column<string>(type: "character varying(26)", maxLength: 26, nullable: false),
                    address_email = table.Column<string>(type: "character varying(61)", maxLength: 61, nullable: false),
                    address_web_site = table.Column<string>(type: "character varying(61)", maxLength: 61, nullable: false),
                    address_guid = table.Column<string>(type: "character varying(39)", maxLength: 39, nullable: false),
                    guid = table.Column<string>(type: "character varying(39)", maxLength: 39, nullable: false),
                    locked_by_session = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    locked_timestamp = table.Column<DateTime>(type: "timestamp(3) without time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_companies", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "agents",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    company_id = table.Column<long>(type: "bigint", nullable: false),
                    name = table.Column<string>(type: "character varying(81)", maxLength: 81, nullable: false),
                    address_street = table.Column<string>(type: "character varying(51)", maxLength: 51, nullable: false),
                    address_city = table.Column<string>(type: "character varying(51)", maxLength: 51, nullable: false),
                    address_country = table.Column<string>(type: "character varying(51)", maxLength: 51, nullable: false),
                    address_phone = table.Column<string>(type: "character varying(26)", maxLength: 26, nullable: false),
                    address_fax = table.Column<string>(type: "character varying(26)", maxLength: 26, nullable: false),
                    address_email = table.Column<string>(type: "character varying(61)", maxLength: 61, nullable: false),
                    address_web_site = table.Column<string>(type: "character varying(61)", maxLength: 61, nullable: false),
                    address_guid = table.Column<string>(type: "character varying(39)", maxLength: 39, nullable: false),
                    guid = table.Column<string>(type: "character varying(39)", maxLength: 39, nullable: false),
                    locked_by_session = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    locked_timestamp = table.Column<DateTime>(type: "timestamp(3) without time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_agents", x => x.id);
                    table.ForeignKey(
                        name: "fk_agents_companies_company_id",
                        column: x => x.company_id,
                        principalTable: "companies",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "clients",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    company_id = table.Column<long>(type: "bigint", nullable: false),
                    name = table.Column<string>(type: "character varying(81)", maxLength: 81, nullable: false),
                    address_street = table.Column<string>(type: "character varying(51)", maxLength: 51, nullable: false),
                    address_city = table.Column<string>(type: "character varying(51)", maxLength: 51, nullable: false),
                    address_country = table.Column<string>(type: "character varying(51)", maxLength: 51, nullable: false),
                    address_phone = table.Column<string>(type: "character varying(26)", maxLength: 26, nullable: false),
                    address_fax = table.Column<string>(type: "character varying(26)", maxLength: 26, nullable: false),
                    address_email = table.Column<string>(type: "character varying(61)", maxLength: 61, nullable: false),
                    address_web_site = table.Column<string>(type: "character varying(61)", maxLength: 61, nullable: false),
                    address_guid = table.Column<string>(type: "character varying(39)", maxLength: 39, nullable: false),
                    guid = table.Column<string>(type: "character varying(39)", maxLength: 39, nullable: false),
                    locked_by_session = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    locked_timestamp = table.Column<DateTime>(type: "timestamp(3) without time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_clients", x => x.id);
                    table.ForeignKey(
                        name: "fk_clients_companies_company_id",
                        column: x => x.company_id,
                        principalTable: "companies",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "countries",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    company_id = table.Column<long>(type: "bigint", nullable: false),
                    guid = table.Column<string>(type: "character varying(39)", maxLength: 39, nullable: false),
                    locked_by_session = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    locked_timestamp = table.Column<DateTime>(type: "timestamp(3) without time zone", nullable: true),
                    name = table.Column<string>(type: "character varying(81)", maxLength: 81, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_countries", x => x.id);
                    table.ForeignKey(
                        name: "fk_countries_companies_company_id",
                        column: x => x.company_id,
                        principalTable: "companies",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "item_categories",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    company_id = table.Column<long>(type: "bigint", nullable: false),
                    name = table.Column<string>(type: "character varying(41)", maxLength: 41, nullable: false),
                    prefix = table.Column<string>(type: "character varying(5)", maxLength: 5, nullable: false),
                    description = table.Column<string>(type: "text", nullable: false),
                    guid = table.Column<string>(type: "character varying(39)", maxLength: 39, nullable: false),
                    locked_by_session = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    locked_timestamp = table.Column<DateTime>(type: "timestamp(3) without time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_item_categories", x => x.id);
                    table.ForeignKey(
                        name: "fk_item_categories_companies_company_id",
                        column: x => x.company_id,
                        principalTable: "companies",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "regions",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    country_id = table.Column<long>(type: "bigint", nullable: false),
                    guid = table.Column<string>(type: "character varying(39)", maxLength: 39, nullable: false),
                    locked_by_session = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    locked_timestamp = table.Column<DateTime>(type: "timestamp(3) without time zone", nullable: true),
                    name = table.Column<string>(type: "character varying(81)", maxLength: 81, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_regions", x => x.id);
                    table.ForeignKey(
                        name: "fk_regions_countries_country_id",
                        column: x => x.country_id,
                        principalTable: "countries",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "commission_rates",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    item_category_id = table.Column<long>(type: "bigint", nullable: false),
                    percentage = table.Column<decimal>(type: "numeric(5,2)", precision: 5, scale: 2, nullable: false),
                    guid = table.Column<string>(type: "character varying(39)", maxLength: 39, nullable: false),
                    locked_by_session = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    locked_timestamp = table.Column<DateTime>(type: "timestamp(3) without time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_commission_rates", x => x.id);
                    table.ForeignKey(
                        name: "fk_commission_rates_item_categories_item_category_id",
                        column: x => x.item_category_id,
                        principalTable: "item_categories",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "items",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    company_id = table.Column<long>(type: "bigint", nullable: false),
                    code_number = table.Column<int>(type: "integer", nullable: false),
                    name = table.Column<string>(type: "character varying(31)", maxLength: 31, nullable: false),
                    description = table.Column<string>(type: "text", nullable: false),
                    listed_date = table.Column<DateOnly>(type: "date", nullable: false),
                    photo = table.Column<byte[]>(type: "bytea", nullable: true),
                    price = table.Column<decimal>(type: "numeric(12,2)", precision: 12, scale: 2, nullable: false),
                    agent_id = table.Column<long>(type: "bigint", nullable: true),
                    item_category_id = table.Column<long>(type: "bigint", nullable: false),
                    region_id = table.Column<long>(type: "bigint", nullable: true),
                    item_type = table.Column<string>(type: "character varying(1)", maxLength: 1, nullable: false),
                    closure_date = table.Column<DateOnly>(type: "date", nullable: true),
                    guid = table.Column<string>(type: "character varying(39)", maxLength: 39, nullable: false),
                    locked_by_session = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    locked_timestamp = table.Column<DateTime>(type: "timestamp(3) without time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_items", x => x.id);
                    table.ForeignKey(
                        name: "fk_items_agents_agent_id",
                        column: x => x.agent_id,
                        principalTable: "agents",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "fk_items_companies_company_id",
                        column: x => x.company_id,
                        principalTable: "companies",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_items_item_categories_item_category_id",
                        column: x => x.item_category_id,
                        principalTable: "item_categories",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_items_regions_region_id",
                        column: x => x.region_id,
                        principalTable: "regions",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "agent_commission_rates",
                columns: table => new
                {
                    agent_id = table.Column<long>(type: "bigint", nullable: false),
                    commission_rate_id = table.Column<long>(type: "bigint", nullable: false),
                    item_category_id = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_agent_commission_rates", x => new { x.agent_id, x.commission_rate_id });
                    table.ForeignKey(
                        name: "fk_agent_commission_rates_agents_agent_id",
                        column: x => x.agent_id,
                        principalTable: "agents",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_agent_commission_rates_commission_rates_commission_rate_id",
                        column: x => x.commission_rate_id,
                        principalTable: "commission_rates",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "tenders",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    company_id = table.Column<long>(type: "bigint", nullable: false),
                    client_id = table.Column<long>(type: "bigint", nullable: false),
                    tender_item_id = table.Column<long>(type: "bigint", nullable: false),
                    offer = table.Column<decimal>(type: "numeric(12,2)", precision: 12, scale: 2, nullable: false),
                    date_tendered = table.Column<DateTime>(type: "timestamp(3) without time zone", nullable: false),
                    guid = table.Column<string>(type: "character varying(39)", maxLength: 39, nullable: false),
                    locked_by_session = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    locked_timestamp = table.Column<DateTime>(type: "timestamp(3) without time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_tenders", x => x.id);
                    table.ForeignKey(
                        name: "fk_tenders_clients_client_id",
                        column: x => x.client_id,
                        principalTable: "clients",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_tenders_companies_company_id",
                        column: x => x.company_id,
                        principalTable: "companies",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_tenders_items_tender_item_id",
                        column: x => x.tender_item_id,
                        principalTable: "items",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "sales",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    company_id = table.Column<long>(type: "bigint", nullable: false),
                    client_id = table.Column<long>(type: "bigint", nullable: false),
                    item_id = table.Column<long>(type: "bigint", nullable: false),
                    agent_commission = table.Column<decimal>(type: "numeric(12,2)", precision: 12, scale: 2, nullable: false),
                    sale_type = table.Column<string>(type: "character varying(1)", maxLength: 1, nullable: false),
                    sale_price = table.Column<decimal>(type: "numeric(12,2)", precision: 12, scale: 2, nullable: true),
                    date_sold = table.Column<DateTime>(type: "timestamp(3) without time zone", nullable: true),
                    tender_id = table.Column<long>(type: "bigint", nullable: true),
                    guid = table.Column<string>(type: "character varying(39)", maxLength: 39, nullable: false),
                    locked_by_session = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    locked_timestamp = table.Column<DateTime>(type: "timestamp(3) without time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_sales", x => x.id);
                    table.ForeignKey(
                        name: "fk_sales_clients_client_id",
                        column: x => x.client_id,
                        principalTable: "clients",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_sales_companies_company_id",
                        column: x => x.company_id,
                        principalTable: "companies",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_sales_items_item_id",
                        column: x => x.item_id,
                        principalTable: "items",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_sales_tenders_tender_id",
                        column: x => x.tender_id,
                        principalTable: "tenders",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateIndex(
                name: "ix_agent_commission_rates_agent_id_item_category_id",
                table: "agent_commission_rates",
                columns: new[] { "agent_id", "item_category_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_agent_commission_rates_commission_rate_id",
                table: "agent_commission_rates",
                column: "commission_rate_id");

            migrationBuilder.CreateIndex(
                name: "ix_agents_company_id_name",
                table: "agents",
                columns: new[] { "company_id", "name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_clients_company_id_name",
                table: "clients",
                columns: new[] { "company_id", "name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_commission_rates_item_category_id_percentage",
                table: "commission_rates",
                columns: new[] { "item_category_id", "percentage" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_countries_company_id_name",
                table: "countries",
                columns: new[] { "company_id", "name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_item_categories_company_id_name",
                table: "item_categories",
                columns: new[] { "company_id", "name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_items_agent_id_item_category_id_code_number",
                table: "items",
                columns: new[] { "agent_id", "item_category_id", "code_number" });

            migrationBuilder.CreateIndex(
                name: "ix_items_company_id_closure_date_tender",
                table: "items",
                columns: new[] { "company_id", "closure_date" },
                filter: "item_type = 'T'");

            migrationBuilder.CreateIndex(
                name: "ix_items_company_id_code_number",
                table: "items",
                columns: new[] { "company_id", "code_number" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_items_company_id_guid_retail",
                table: "items",
                columns: new[] { "company_id", "guid" },
                unique: true,
                filter: "item_type = 'R'");

            migrationBuilder.CreateIndex(
                name: "ix_items_company_id_guid_tender",
                table: "items",
                columns: new[] { "company_id", "guid" },
                unique: true,
                filter: "item_type = 'T'");

            migrationBuilder.CreateIndex(
                name: "ix_items_item_category_id_code_number",
                table: "items",
                columns: new[] { "item_category_id", "code_number" });

            migrationBuilder.CreateIndex(
                name: "ix_items_region_id_code_number",
                table: "items",
                columns: new[] { "region_id", "code_number" });

            migrationBuilder.CreateIndex(
                name: "ix_regions_country_id_name",
                table: "regions",
                columns: new[] { "country_id", "name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_sales_client_id",
                table: "sales",
                column: "client_id");

            migrationBuilder.CreateIndex(
                name: "ix_sales_client_id_date_sold_retail",
                table: "sales",
                columns: new[] { "client_id", "date_sold" },
                filter: "sale_type = 'R'");

            migrationBuilder.CreateIndex(
                name: "ix_sales_client_id_tender_id_tender",
                table: "sales",
                columns: new[] { "client_id", "tender_id" },
                filter: "sale_type = 'T'");

            migrationBuilder.CreateIndex(
                name: "ix_sales_company_id",
                table: "sales",
                column: "company_id");

            migrationBuilder.CreateIndex(
                name: "ix_sales_item_id",
                table: "sales",
                column: "item_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_sales_tender_id",
                table: "sales",
                column: "tender_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_tenders_client_id_tender_item_id_date_tendered_offer",
                table: "tenders",
                columns: new[] { "client_id", "tender_item_id", "date_tendered", "offer" },
                unique: true,
                descending: new[] { false, false, false, true });

            migrationBuilder.CreateIndex(
                name: "ix_tenders_company_id_date_tendered_offer",
                table: "tenders",
                columns: new[] { "company_id", "date_tendered", "offer" },
                unique: true,
                descending: new[] { false, false, true });

            migrationBuilder.CreateIndex(
                name: "ix_tenders_tender_item_id_offer_date_tendered",
                table: "tenders",
                columns: new[] { "tender_item_id", "offer", "date_tendered" },
                unique: true,
                descending: new[] { false, true, false });

            migrationBuilder.Sql("CREATE UNIQUE INDEX ix_item_categories_company_id_upper_prefix ON item_categories (company_id, upper(prefix));");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP INDEX IF EXISTS ix_item_categories_company_id_upper_prefix;");

            migrationBuilder.DropTable(
                name: "agent_commission_rates");

            migrationBuilder.DropTable(
                name: "sales");

            migrationBuilder.DropTable(
                name: "commission_rates");

            migrationBuilder.DropTable(
                name: "tenders");

            migrationBuilder.DropTable(
                name: "clients");

            migrationBuilder.DropTable(
                name: "items");

            migrationBuilder.DropTable(
                name: "agents");

            migrationBuilder.DropTable(
                name: "item_categories");

            migrationBuilder.DropTable(
                name: "regions");

            migrationBuilder.DropTable(
                name: "countries");

            migrationBuilder.DropTable(
                name: "companies");
        }
    }
}
