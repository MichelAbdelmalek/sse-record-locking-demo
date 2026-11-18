using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace LockDemo.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class Initial : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "records",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    reference = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    title = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    priority = table.Column<int>(type: "integer", nullable: false),
                    notes = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_by = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_records", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "tab_sessions",
                columns: table => new
                {
                    tab_id = table.Column<Guid>(type: "uuid", nullable: false),
                    public_id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    connected_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    last_seen = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_tab_sessions", x => x.tab_id);
                });

            migrationBuilder.CreateTable(
                name: "record_locks",
                columns: table => new
                {
                    record_id = table.Column<int>(type: "integer", nullable: false),
                    holder_tab_id = table.Column<Guid>(type: "uuid", nullable: false),
                    holder_name = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    acquired_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_record_locks", x => x.record_id);
                    table.ForeignKey(
                        name: "fk_record_locks_records_record_id",
                        column: x => x.record_id,
                        principalTable: "records",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_record_locks_holder_tab_id",
                table: "record_locks",
                column: "holder_tab_id");

            migrationBuilder.CreateIndex(
                name: "ix_records_priority_id",
                table: "records",
                columns: new[] { "priority", "id" });

            migrationBuilder.CreateIndex(
                name: "ix_records_reference",
                table: "records",
                column: "reference",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_tab_sessions_last_seen",
                table: "tab_sessions",
                column: "last_seen");

            migrationBuilder.CreateIndex(
                name: "ix_tab_sessions_public_id",
                table: "tab_sessions",
                column: "public_id",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "record_locks");

            migrationBuilder.DropTable(
                name: "tab_sessions");

            migrationBuilder.DropTable(
                name: "records");
        }
    }
}
