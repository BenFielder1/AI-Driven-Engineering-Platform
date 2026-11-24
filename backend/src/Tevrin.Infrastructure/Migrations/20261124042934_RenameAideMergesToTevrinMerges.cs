using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Tevrin.Infrastructure.Migrations
{
    /// <summary>
    /// FR-326 (§4.77, D-0.8.0-10): the merge strategy <c>aide_merges</c> becomes <c>tevrin_merges</c> (data only): the
    /// column default and every project's value. Stored event and job payloads are left as written (events are the audit
    /// trail); the app reads <c>aide_merges</c> wherever it is parsed, so they keep working. Down writes <c>aide_merges</c>
    /// back.
    /// </summary>
    public partial class RenameAideMergesToTevrinMerges : Migration
    {
        internal static string RewriteSql(string from, string to) =>
            $"UPDATE projects SET merge_strategy = '{to}' WHERE merge_strategy = '{from}';";

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<string>(
                name: "merge_strategy",
                table: "projects",
                type: "character varying(32)",
                maxLength: 32,
                nullable: false,
                defaultValue: "tevrin_merges",
                oldClrType: typeof(string),
                oldType: "character varying(32)",
                oldMaxLength: 32,
                oldDefaultValue: "aide_merges");

            migrationBuilder.Sql(RewriteSql("aide_merges", "tevrin_merges"));
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<string>(
                name: "merge_strategy",
                table: "projects",
                type: "character varying(32)",
                maxLength: 32,
                nullable: false,
                defaultValue: "aide_merges",
                oldClrType: typeof(string),
                oldType: "character varying(32)",
                oldMaxLength: 32,
                oldDefaultValue: "tevrin_merges");

            migrationBuilder.Sql(RewriteSql("tevrin_merges", "aide_merges"));
        }
    }
}
