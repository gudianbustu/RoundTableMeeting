using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RoundTableMeeting.Repository.Sqlite.Migrations
{
    /// <inheritdoc />
    public partial class AddPrivateMessagesAndAttachments : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {

            migrationBuilder.AddColumn<string>(
                name: "PayloadFingerprint",
                table: "MeetingMessages",
                type: "TEXT",
                maxLength: 64,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<Guid>(
                name: "RecipientParticipantId",
                table: "MeetingMessages",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "ReplyToMessageId",
                table: "MeetingMessages",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Visibility",
                table: "MeetingMessages",
                type: "TEXT",
                maxLength: 16,
                nullable: false,
                defaultValue: "public");

            migrationBuilder.CreateTable(
                name: "MeetingAttachments",
                columns: table => new
                {
                    ID = table.Column<Guid>(type: "TEXT", nullable: false),
                    MeetingId = table.Column<Guid>(type: "TEXT", nullable: false),
                    UploaderParticipantId = table.Column<Guid>(type: "TEXT", nullable: false),
                    UploadRequestId = table.Column<Guid>(type: "TEXT", nullable: false),
                    OriginalName = table.Column<string>(type: "TEXT", maxLength: 512, nullable: false),
                    SizeBytes = table.Column<long>(type: "INTEGER", nullable: false),
                    MediaType = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    StorageKey = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    ContentHash = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    State = table.Column<string>(type: "TEXT", nullable: false),
                    MessageId = table.Column<Guid>(type: "TEXT", nullable: true),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MeetingAttachments", x => x.ID);
                    table.CheckConstraint("CK_Attachments_Size", "SizeBytes >= 0");
                    table.CheckConstraint("CK_Attachments_State", "(State = 'staged' AND MessageId IS NULL) OR (State = 'attached' AND MessageId IS NOT NULL)");
                    table.ForeignKey(
                        name: "FK_MeetingAttachments_MeetingMessages_MessageId_MeetingId",
                        columns: x => new { x.MessageId, x.MeetingId },
                        principalTable: "MeetingMessages",
                        principalColumns: new[] { "ID", "MeetingId" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_MeetingAttachments_MeetingParticipants_UploaderParticipantId_MeetingId",
                        columns: x => new { x.UploaderParticipantId, x.MeetingId },
                        principalTable: "MeetingParticipants",
                        principalColumns: new[] { "ID", "MeetingId" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "ParticipantMessageCounters",
                columns: table => new
                {
                    ID = table.Column<Guid>(type: "TEXT", nullable: false),
                    MeetingId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ParticipantId = table.Column<Guid>(type: "TEXT", nullable: false),
                    HighWatermark = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ParticipantMessageCounters", x => x.ID);
                    table.CheckConstraint("CK_MessageCounters_Watermark", "HighWatermark BETWEEN 0 AND 9007199254740991");
                    table.ForeignKey(
                        name: "FK_ParticipantMessageCounters_MeetingParticipants_ParticipantId_MeetingId",
                        columns: x => new { x.ParticipantId, x.MeetingId },
                        principalTable: "MeetingParticipants",
                        principalColumns: new[] { "ID", "MeetingId" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "ParticipantMessageViews",
                columns: table => new
                {
                    ID = table.Column<Guid>(type: "TEXT", nullable: false),
                    MeetingId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ParticipantId = table.Column<Guid>(type: "TEXT", nullable: false),
                    MessageId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Sequence = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ParticipantMessageViews", x => x.ID);
                    table.CheckConstraint("CK_MessageViews_Sequence", "Sequence BETWEEN 1 AND 9007199254740991");
                    table.ForeignKey(
                        name: "FK_ParticipantMessageViews_MeetingMessages_MessageId_MeetingId",
                        columns: x => new { x.MessageId, x.MeetingId },
                        principalTable: "MeetingMessages",
                        principalColumns: new[] { "ID", "MeetingId" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ParticipantMessageViews_MeetingParticipants_ParticipantId_MeetingId",
                        columns: x => new { x.ParticipantId, x.MeetingId },
                        principalTable: "MeetingParticipants",
                        principalColumns: new[] { "ID", "MeetingId" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_MeetingMessages_RecipientParticipantId_MeetingId",
                table: "MeetingMessages",
                columns: new[] { "RecipientParticipantId", "MeetingId" });

            migrationBuilder.CreateIndex(
                name: "IX_MeetingMessages_ReplyToMessageId_MeetingId",
                table: "MeetingMessages",
                columns: new[] { "ReplyToMessageId", "MeetingId" });

            migrationBuilder.CreateIndex(
                name: "IX_MeetingAttachments_MeetingId_UploaderParticipantId_UploadRequestId",
                table: "MeetingAttachments",
                columns: new[] { "MeetingId", "UploaderParticipantId", "UploadRequestId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_MeetingAttachments_MessageId_MeetingId",
                table: "MeetingAttachments",
                columns: new[] { "MessageId", "MeetingId" });

            migrationBuilder.CreateIndex(
                name: "IX_MeetingAttachments_StorageKey",
                table: "MeetingAttachments",
                column: "StorageKey",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_MeetingAttachments_UploaderParticipantId_MeetingId",
                table: "MeetingAttachments",
                columns: new[] { "UploaderParticipantId", "MeetingId" });

            migrationBuilder.CreateIndex(
                name: "IX_ParticipantMessageCounters_ParticipantId",
                table: "ParticipantMessageCounters",
                column: "ParticipantId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ParticipantMessageCounters_ParticipantId_MeetingId",
                table: "ParticipantMessageCounters",
                columns: new[] { "ParticipantId", "MeetingId" });

            migrationBuilder.CreateIndex(
                name: "IX_ParticipantMessageViews_MessageId_MeetingId",
                table: "ParticipantMessageViews",
                columns: new[] { "MessageId", "MeetingId" });

            migrationBuilder.CreateIndex(
                name: "IX_ParticipantMessageViews_ParticipantId_MeetingId",
                table: "ParticipantMessageViews",
                columns: new[] { "ParticipantId", "MeetingId" });

            migrationBuilder.CreateIndex(
                name: "IX_ParticipantMessageViews_ParticipantId_MessageId",
                table: "ParticipantMessageViews",
                columns: new[] { "ParticipantId", "MessageId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ParticipantMessageViews_ParticipantId_Sequence",
                table: "ParticipantMessageViews",
                columns: new[] { "ParticipantId", "Sequence" },
                unique: true);


            migrationBuilder.Sql("""
                INSERT INTO ParticipantMessageViews (ID, MeetingId, ParticipantId, MessageId, Sequence)
                SELECT upper(hex(randomblob(4)) || '-' || hex(randomblob(2)) || '-' || hex(randomblob(2)) || '-' || hex(randomblob(2)) || '-' || hex(randomblob(6))), p.MeetingId, p.ID, m.ID,
                       row_number() OVER (PARTITION BY p.ID ORDER BY m.Sequence)
                FROM MeetingParticipants p JOIN MeetingMessages m ON m.MeetingId = p.MeetingId;
                INSERT INTO ParticipantMessageCounters (ID, MeetingId, ParticipantId, HighWatermark)
                SELECT p.ID, p.MeetingId, p.ID, count(v.ID) FROM MeetingParticipants p
                LEFT JOIN ParticipantMessageViews v ON v.ParticipantId = p.ID GROUP BY p.ID;
                UPDATE MeetingParticipants SET LeftThroughSequence = (
                 SELECT count(*) FROM ParticipantMessageViews v JOIN MeetingMessages m ON m.ID = v.MessageId
                 WHERE v.ParticipantId = MeetingParticipants.ID AND m.Sequence <= MeetingParticipants.LeftThroughSequence)
                WHERE LeftThroughSequence IS NOT NULL;
                UPDATE MeetingLeaveOperations SET VisibleThroughSequence = (
                 SELECT count(*) FROM ParticipantMessageViews v JOIN MeetingMessages m ON m.ID = v.MessageId
                 WHERE v.ParticipantId = MeetingLeaveOperations.ParticipantId AND m.Sequence <= MeetingLeaveOperations.VisibleThroughSequence);
                CREATE TRIGGER message_scope_insert BEFORE INSERT ON MeetingMessages
                WHEN NEW.Visibility NOT IN ('public', 'private')
                 OR (NEW.Visibility = 'public' AND NEW.RecipientParticipantId IS NOT NULL)
                 OR (NEW.RecipientParticipantId IS NOT NULL AND NOT EXISTS (SELECT 1 FROM MeetingParticipants p WHERE p.ID = NEW.RecipientParticipantId AND p.MeetingId = NEW.MeetingId))
                 OR (NEW.ReplyToMessageId IS NOT NULL AND NOT EXISTS (SELECT 1 FROM MeetingMessages m WHERE m.ID = NEW.ReplyToMessageId AND m.MeetingId = NEW.MeetingId))
                 OR (NEW.Visibility = 'private' AND (NEW.AuthorParticipantId IS NULL OR NEW.RecipientParticipantId IS NULL
                     OR NEW.AuthorParticipantId = NEW.RecipientParticipantId))
                BEGIN SELECT RAISE(ABORT, 'Invalid message scope'); END;
                CREATE TRIGGER message_scope_immutable BEFORE UPDATE ON MeetingMessages
                WHEN NEW.MeetingId IS NOT OLD.MeetingId OR NEW.Sequence IS NOT OLD.Sequence
                 OR NEW.Visibility IS NOT OLD.Visibility OR NEW.RecipientParticipantId IS NOT OLD.RecipientParticipantId
                 OR NEW.AuthorParticipantId IS NOT OLD.AuthorParticipantId OR NEW.ReplyToMessageId IS NOT OLD.ReplyToMessageId
                 OR NEW.Content IS NOT OLD.Content OR NEW.PayloadFingerprint IS NOT OLD.PayloadFingerprint
                BEGIN SELECT RAISE(ABORT, 'Message is immutable'); END;
                CREATE TRIGGER message_projection_insert AFTER INSERT ON MeetingMessages
                BEGIN
                 UPDATE ParticipantMessageCounters SET HighWatermark = HighWatermark + 1
                 WHERE MeetingId = NEW.MeetingId AND (NEW.Visibility = 'public'
                 OR ParticipantId = NEW.AuthorParticipantId OR ParticipantId = NEW.RecipientParticipantId);
                 INSERT INTO ParticipantMessageViews (ID, MeetingId, ParticipantId, MessageId, Sequence)
                 SELECT upper(hex(randomblob(4)) || '-' || hex(randomblob(2)) || '-' || hex(randomblob(2)) || '-' || hex(randomblob(2)) || '-' || hex(randomblob(6))), MeetingId, ParticipantId, NEW.ID, HighWatermark
                 FROM ParticipantMessageCounters WHERE MeetingId = NEW.MeetingId AND (NEW.Visibility = 'public'
                 OR ParticipantId = NEW.AuthorParticipantId OR ParticipantId = NEW.RecipientParticipantId);
                END;
                CREATE TRIGGER participant_projection_insert AFTER INSERT ON MeetingParticipants
                BEGIN
                 INSERT INTO ParticipantMessageViews (ID, MeetingId, ParticipantId, MessageId, Sequence)
                 SELECT upper(hex(randomblob(4)) || '-' || hex(randomblob(2)) || '-' || hex(randomblob(2)) || '-' || hex(randomblob(2)) || '-' || hex(randomblob(6))), NEW.MeetingId, NEW.ID, ID, row_number() OVER (ORDER BY Sequence)
                 FROM MeetingMessages WHERE MeetingId = NEW.MeetingId AND Visibility = 'public';
                 INSERT INTO ParticipantMessageCounters (ID, MeetingId, ParticipantId, HighWatermark)
                 SELECT NEW.ID, NEW.MeetingId, NEW.ID, count(*) FROM ParticipantMessageViews WHERE ParticipantId = NEW.ID;
                END;
                """);

        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                CREATE TEMP TABLE __PrivateDowngradeGuard (Allowed INTEGER CHECK (Allowed = 1));
                INSERT INTO __PrivateDowngradeGuard SELECT CASE WHEN
                 EXISTS (SELECT 1 FROM MeetingMessages WHERE Visibility != 'public' OR ReplyToMessageId IS NOT NULL OR PayloadFingerprint != '')
                 OR EXISTS (SELECT 1 FROM MeetingAttachments) THEN 0 ELSE 1 END;
                DROP TABLE __PrivateDowngradeGuard;
                UPDATE MeetingParticipants SET LeftThroughSequence = coalesce((
                 SELECT max(m.Sequence) FROM ParticipantMessageViews v JOIN MeetingMessages m ON m.ID = v.MessageId
                 WHERE v.ParticipantId = MeetingParticipants.ID AND v.Sequence <= MeetingParticipants.LeftThroughSequence), 0)
                WHERE LeftThroughSequence IS NOT NULL;
                UPDATE MeetingLeaveOperations SET VisibleThroughSequence = coalesce((
                 SELECT max(m.Sequence) FROM ParticipantMessageViews v JOIN MeetingMessages m ON m.ID = v.MessageId
                 WHERE v.ParticipantId = MeetingLeaveOperations.ParticipantId AND v.Sequence <= MeetingLeaveOperations.VisibleThroughSequence), 0);
                DROP TRIGGER message_scope_insert;
                DROP TRIGGER message_scope_immutable;
                DROP TRIGGER message_projection_insert;
                DROP TRIGGER participant_projection_insert;
                """);



            migrationBuilder.DropTable(
                name: "MeetingAttachments");

            migrationBuilder.DropTable(
                name: "ParticipantMessageCounters");

            migrationBuilder.DropTable(
                name: "ParticipantMessageViews");

            migrationBuilder.DropIndex(
                name: "IX_MeetingMessages_RecipientParticipantId_MeetingId",
                table: "MeetingMessages");

            migrationBuilder.DropIndex(
                name: "IX_MeetingMessages_ReplyToMessageId_MeetingId",
                table: "MeetingMessages");

            migrationBuilder.Sql("ALTER TABLE MeetingMessages DROP COLUMN PayloadFingerprint;");

            migrationBuilder.Sql("ALTER TABLE MeetingMessages DROP COLUMN RecipientParticipantId;");

            migrationBuilder.Sql("ALTER TABLE MeetingMessages DROP COLUMN ReplyToMessageId;");

            migrationBuilder.Sql("ALTER TABLE MeetingMessages DROP COLUMN Visibility;");

        }
    }
}
