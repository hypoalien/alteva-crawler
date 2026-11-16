using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Crawler.Shared.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class InitialSchema : Migration
    {
        /// <inheritdoc />
        // EF-generated forward migration: create the four application tables, keys, indexes, and constraints.
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "crawl_outbox",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    job_id = table.Column<Guid>(type: "uuid", nullable: true),
                    destination = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    body = table.Column<byte[]>(type: "bytea", nullable: false),
                    error = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    source_message_id = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_crawl_outbox", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "jobs",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    ext_id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    url = table.Column<string>(type: "text", nullable: false),
                    status = table.Column<string>(type: "text", nullable: false),
                    max_depth = table.Column<int>(type: "integer", nullable: false),
                    max_pages = table.Column<int>(type: "integer", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    started_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    completed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    error = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_jobs", x => x.id);
                    table.CheckConstraint("ck_jobs_max_depth", "max_depth BETWEEN 0 AND 5");
                    table.CheckConstraint("ck_jobs_max_pages", "max_pages BETWEEN 1 AND 1000");
                });

            migrationBuilder.CreateTable(
                name: "pages",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    job_id = table.Column<long>(type: "bigint", nullable: false),
                    parent_page_id = table.Column<long>(type: "bigint", nullable: true),
                    url = table.Column<string>(type: "text", nullable: false),
                    final_url = table.Column<string>(type: "text", nullable: true),
                    depth = table.Column<int>(type: "integer", nullable: false),
                    status = table.Column<string>(type: "text", nullable: false),
                    http_status = table.Column<int>(type: "integer", nullable: true),
                    content_type = table.Column<string>(type: "text", nullable: true),
                    total_link_count = table.Column<int>(type: "integer", nullable: false),
                    internal_link_count = table.Column<int>(type: "integer", nullable: false),
                    link_ratio = table.Column<decimal>(type: "numeric(7,6)", precision: 7, scale: 6, nullable: true),
                    error = table.Column<string>(type: "text", nullable: true),
                    started_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    completed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_pages", x => x.id);
                    table.CheckConstraint("ck_pages_depth", "depth >= 0");
                    table.CheckConstraint("ck_pages_link_counts", "internal_link_count >= 0 AND total_link_count >= internal_link_count");
                    table.ForeignKey(
                        name: "FK_pages_jobs_job_id",
                        column: x => x.job_id,
                        principalTable: "jobs",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_pages_pages_parent_page_id",
                        column: x => x.parent_page_id,
                        principalTable: "pages",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "links",
                columns: table => new
                {
                    source_page_id = table.Column<long>(type: "bigint", nullable: false),
                    url = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_links", x => new { x.source_page_id, x.url });
                    table.ForeignKey(
                        name: "FK_links_pages_source_page_id",
                        column: x => x.source_page_id,
                        principalTable: "pages",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_crawl_outbox_created_at_id",
                table: "crawl_outbox",
                columns: new[] { "created_at", "id" });

            migrationBuilder.CreateIndex(
                name: "IX_jobs_created_at",
                table: "jobs",
                column: "created_at",
                descending: new bool[0]);

            migrationBuilder.CreateIndex(
                name: "IX_jobs_ext_id",
                table: "jobs",
                column: "ext_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_pages_job_id_status",
                table: "pages",
                columns: new[] { "job_id", "status" });

            migrationBuilder.CreateIndex(
                name: "IX_pages_job_id_url",
                table: "pages",
                columns: new[] { "job_id", "url" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_pages_parent_page_id",
                table: "pages",
                column: "parent_page_id");
        }

        /// <inheritdoc />
        // EF-generated rollback for this migration: drop its tables in foreign-key-safe order.
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "crawl_outbox");

            migrationBuilder.DropTable(
                name: "links");

            migrationBuilder.DropTable(
                name: "pages");

            migrationBuilder.DropTable(
                name: "jobs");
        }
    }
}
