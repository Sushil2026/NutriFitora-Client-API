using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Npgsql;
namespace Nutritionist.Api.Controllers;

[ApiController, Authorize(Roles="Administrator"), Route("api/admin/notifications")]
[ResponseCache(NoStore=true, Location=ResponseCacheLocation.None)]
public sealed class AdminNotificationsController(NpgsqlDataSource db): ControllerBase
{
    private bool AdminId(out Guid id) => Guid.TryParse(User.FindFirst(ClaimTypes.NameIdentifier)?.Value ?? User.FindFirst("sub")?.Value, out id);
    [HttpGet]
    public async Task<IActionResult> Get(CancellationToken ct)
    {
        if (!AdminId(out var admin)) return Unauthorized();
        await using var cmd = db.CreateCommand("""
            SELECT n.id::text, u.full_name, n.created_at, (r.notification_id IS NOT NULL) AS is_read
            FROM admin_notifications n JOIN users u ON u.user_id=n.customer_id
            LEFT JOIN admin_notification_reads r ON r.notification_id=n.id AND r.admin_id=@admin
            ORDER BY (r.notification_id IS NOT NULL), n.created_at DESC,n.id DESC LIMIT 100;
            SELECT count(*) FROM admin_notifications n WHERE NOT EXISTS
            (SELECT 1 FROM admin_notification_reads r WHERE r.notification_id=n.id AND r.admin_id=@admin);
            """);
        cmd.Parameters.AddWithValue("admin",admin);
        await using var reader=await cmd.ExecuteReaderAsync(ct);
        var items=new List<object>();
        while(await reader.ReadAsync(ct)) items.Add(new {id=reader.GetString(0),customerName=reader.GetString(1),createdAt=reader.GetDateTime(2),isRead=reader.GetBoolean(3)});
        await reader.NextResultAsync(ct); await reader.ReadAsync(ct);
        return Ok(new {items,unreadCount=reader.GetInt64(0)});
    }
    [HttpGet("unread-count")]
    public async Task<IActionResult> Count(CancellationToken ct)
    {
        if (!AdminId(out var admin)) return Unauthorized();
        await using var cmd=db.CreateCommand("SELECT count(*) FROM admin_notifications n WHERE NOT EXISTS (SELECT 1 FROM admin_notification_reads r WHERE r.notification_id=n.id AND r.admin_id=@admin)");
        cmd.Parameters.AddWithValue("admin",admin);
        return Ok(new {unreadCount=(long)(await cmd.ExecuteScalarAsync(ct) ?? 0L)});
    }
    [HttpPatch("{id:long}/read")]
    public async Task<IActionResult> Read(long id,CancellationToken ct)
    {
        if (!AdminId(out var admin)) return Unauthorized();
        await using var cmd=db.CreateCommand("INSERT INTO admin_notification_reads(notification_id,admin_id) SELECT id,@admin FROM admin_notifications WHERE id=@id ON CONFLICT DO NOTHING");
        cmd.Parameters.AddWithValue("admin",admin);cmd.Parameters.AddWithValue("id",id);
        await cmd.ExecuteNonQueryAsync(ct);return NoContent();
    }
    [HttpPatch("read-all")]
    public async Task<IActionResult> ReadAll(CancellationToken ct)
    {
        if (!AdminId(out var admin)) return Unauthorized();
        await using var cmd=db.CreateCommand("INSERT INTO admin_notification_reads(notification_id,admin_id) SELECT id,@admin FROM admin_notifications ON CONFLICT DO NOTHING");
        cmd.Parameters.AddWithValue("admin",admin);await cmd.ExecuteNonQueryAsync(ct);return NoContent();
    }
}
