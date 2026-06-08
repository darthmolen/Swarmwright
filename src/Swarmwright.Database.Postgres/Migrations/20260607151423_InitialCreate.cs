using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Swarmwright.Database.Postgres.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "swarm_agents",
                columns: table => new
                {
                    swarm_id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    role = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    display_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    session_id = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    status = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    tasks_completed = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_swarm_agents", x => new { x.swarm_id, x.name });
                });

            migrationBuilder.CreateTable(
                name: "swarm_events",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    swarm_id = table.Column<Guid>(type: "uuid", nullable: true),
                    event_type = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    data_json = table.Column<string>(type: "jsonb", nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_swarm_events", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "swarm_files",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    swarm_id = table.Column<Guid>(type: "uuid", nullable: false),
                    path = table.Column<string>(type: "text", nullable: false),
                    size_bytes = table.Column<long>(type: "bigint", nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_swarm_files", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "swarm_messages",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    swarm_id = table.Column<Guid>(type: "uuid", nullable: false),
                    sender = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    recipient = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    content = table.Column<string>(type: "text", nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_swarm_messages", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "swarm_state_transitions",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    swarm_id = table.Column<Guid>(type: "uuid", nullable: false),
                    from_state = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    to_state = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    reason = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: false),
                    actor = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    note = table.Column<string>(type: "text", nullable: true),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_swarm_state_transitions", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "swarm_tasks",
                columns: table => new
                {
                    swarm_id = table.Column<Guid>(type: "uuid", nullable: false),
                    id = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    subject = table.Column<string>(type: "text", nullable: false),
                    description = table.Column<string>(type: "text", nullable: false),
                    worker_role = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    worker_name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    state = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    retry_count = table.Column<int>(type: "integer", nullable: false),
                    blocked_by_json = table.Column<string>(type: "jsonb", nullable: false),
                    result = table.Column<string>(type: "text", nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_swarm_tasks", x => new { x.swarm_id, x.id });
                });

            migrationBuilder.CreateTable(
                name: "swarms",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    goal = table.Column<string>(type: "text", nullable: false),
                    qa_refined_goal = table.Column<string>(type: "text", nullable: true),
                    state = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    template_key = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    synthesis_session_id = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    context_json = table.Column<string>(type: "text", nullable: false),
                    report = table.Column<string>(type: "text", nullable: true),
                    current_round = table.Column<int>(type: "integer", nullable: false),
                    max_rounds = table.Column<int>(type: "integer", nullable: false),
                    auto_smart_continue_count = table.Column<int>(type: "integer", nullable: false),
                    locked_by = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    locked_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    completed_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_swarms", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "task_state_transitions",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    swarm_id = table.Column<Guid>(type: "uuid", nullable: false),
                    task_id = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    from_state = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    to_state = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    reason = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: false),
                    actor = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    retry_count_after = table.Column<int>(type: "integer", nullable: false),
                    note = table.Column<string>(type: "text", nullable: true),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_task_state_transitions", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "idx_agents_swarm_id",
                table: "swarm_agents",
                column: "swarm_id");

            migrationBuilder.CreateIndex(
                name: "idx_events_swarm_created",
                table: "swarm_events",
                columns: new[] { "swarm_id", "created_at" });

            migrationBuilder.CreateIndex(
                name: "idx_events_swarm_type",
                table: "swarm_events",
                columns: new[] { "swarm_id", "event_type" });

            migrationBuilder.CreateIndex(
                name: "idx_files_swarm_path",
                table: "swarm_files",
                columns: new[] { "swarm_id", "path" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "idx_messages_swarm_created",
                table: "swarm_messages",
                columns: new[] { "swarm_id", "created_at" });

            migrationBuilder.CreateIndex(
                name: "idx_swarm_transitions_swarm_created",
                table: "swarm_state_transitions",
                columns: new[] { "swarm_id", "created_at" });

            migrationBuilder.CreateIndex(
                name: "idx_tasks_swarm_id",
                table: "swarm_tasks",
                column: "swarm_id");

            migrationBuilder.CreateIndex(
                name: "idx_task_transitions_task_created",
                table: "task_state_transitions",
                columns: new[] { "swarm_id", "task_id", "created_at" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "swarm_agents");

            migrationBuilder.DropTable(
                name: "swarm_events");

            migrationBuilder.DropTable(
                name: "swarm_files");

            migrationBuilder.DropTable(
                name: "swarm_messages");

            migrationBuilder.DropTable(
                name: "swarm_state_transitions");

            migrationBuilder.DropTable(
                name: "swarm_tasks");

            migrationBuilder.DropTable(
                name: "swarms");

            migrationBuilder.DropTable(
                name: "task_state_transitions");
        }
    }
}
