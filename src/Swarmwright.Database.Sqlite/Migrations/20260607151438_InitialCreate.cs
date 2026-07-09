using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Swarmwright.Database.Sqlite.Migrations
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
                    swarm_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    name = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    role = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    display_name = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    session_id = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    status = table.Column<string>(type: "TEXT", maxLength: 30, nullable: false),
                    tasks_completed = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_swarm_agents", x => new { x.swarm_id, x.name });
                });

            migrationBuilder.CreateTable(
                name: "swarm_events",
                columns: table => new
                {
                    id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    swarm_id = table.Column<Guid>(type: "TEXT", nullable: true),
                    event_type = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    data_json = table.Column<string>(type: "TEXT", nullable: false),
                    created_at = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_swarm_events", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "swarm_files",
                columns: table => new
                {
                    id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    swarm_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    path = table.Column<string>(type: "TEXT", nullable: false),
                    size_bytes = table.Column<long>(type: "INTEGER", nullable: false),
                    created_at = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_swarm_files", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "swarm_messages",
                columns: table => new
                {
                    id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    swarm_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    sender = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    recipient = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    content = table.Column<string>(type: "TEXT", nullable: false),
                    created_at = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_swarm_messages", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "swarm_state_transitions",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "TEXT", nullable: false),
                    swarm_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    from_state = table.Column<string>(type: "TEXT", maxLength: 40, nullable: false),
                    to_state = table.Column<string>(type: "TEXT", maxLength: 40, nullable: false),
                    reason = table.Column<string>(type: "TEXT", maxLength: 60, nullable: false),
                    actor = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    note = table.Column<string>(type: "TEXT", nullable: true),
                    created_at = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_swarm_state_transitions", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "swarm_tasks",
                columns: table => new
                {
                    swarm_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    id = table.Column<string>(type: "TEXT", maxLength: 50, nullable: false),
                    subject = table.Column<string>(type: "TEXT", nullable: false),
                    description = table.Column<string>(type: "TEXT", nullable: false),
                    worker_role = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    worker_name = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    state = table.Column<string>(type: "TEXT", maxLength: 40, nullable: false),
                    retry_count = table.Column<int>(type: "INTEGER", nullable: false),
                    blocked_by_json = table.Column<string>(type: "TEXT", nullable: false),
                    result = table.Column<string>(type: "TEXT", nullable: false),
                    created_at = table.Column<DateTime>(type: "TEXT", nullable: false),
                    updated_at = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_swarm_tasks", x => new { x.swarm_id, x.id });
                });

            migrationBuilder.CreateTable(
                name: "swarms",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "TEXT", nullable: false),
                    goal = table.Column<string>(type: "TEXT", nullable: false),
                    qa_refined_goal = table.Column<string>(type: "TEXT", nullable: true),
                    state = table.Column<string>(type: "TEXT", maxLength: 40, nullable: false),
                    template_key = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                    synthesis_session_id = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    context_json = table.Column<string>(type: "TEXT", nullable: false),
                    report = table.Column<string>(type: "TEXT", nullable: true),
                    current_round = table.Column<int>(type: "INTEGER", nullable: false),
                    max_rounds = table.Column<int>(type: "INTEGER", nullable: false),
                    auto_smart_continue_count = table.Column<int>(type: "INTEGER", nullable: false),
                    locked_by = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    locked_at = table.Column<DateTime>(type: "TEXT", nullable: true),
                    created_at = table.Column<DateTime>(type: "TEXT", nullable: false),
                    updated_at = table.Column<DateTime>(type: "TEXT", nullable: false),
                    completed_at = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_swarms", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "task_state_transitions",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "TEXT", nullable: false),
                    swarm_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    task_id = table.Column<string>(type: "TEXT", maxLength: 50, nullable: false),
                    from_state = table.Column<string>(type: "TEXT", maxLength: 40, nullable: false),
                    to_state = table.Column<string>(type: "TEXT", maxLength: 40, nullable: false),
                    reason = table.Column<string>(type: "TEXT", maxLength: 60, nullable: false),
                    actor = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    retry_count_after = table.Column<int>(type: "INTEGER", nullable: false),
                    note = table.Column<string>(type: "TEXT", nullable: true),
                    created_at = table.Column<DateTime>(type: "TEXT", nullable: false)
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
