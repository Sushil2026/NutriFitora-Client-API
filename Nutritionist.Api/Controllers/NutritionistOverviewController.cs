using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Npgsql;

namespace Nutritionist.Api.Controllers;

[ApiController]
[Route("api/nutritionist/overview")]
[Authorize(Roles = "Nutritionist")]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public sealed class NutritionistOverviewController(NpgsqlDataSource db) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> Get(CancellationToken ct)
    {
        if (!Guid.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier) ?? User.FindFirstValue("sub"), out var id)) return Unauthorized();
        // Business day is IST; timestamps are stored in UTC.
        var now = DateTimeOffset.UtcNow;
        var day = new DateTimeOffset(now.ToOffset(TimeSpan.FromMinutes(330)).Date, TimeSpan.FromMinutes(330)).UtcDateTime;
        await using var conn = await db.OpenConnectionAsync(ct);
        await using var cmd = new NpgsqlCommand("""
            SELECT count(*) FILTER (WHERE status='active' AND (expires_at IS NULL OR expires_at > now())),
                   count(*) FILTER (WHERE assigned_at >= now()-interval '15 days'),
                   count(*) FILTER (WHERE status='paused' AND (expires_at IS NULL OR expires_at > now())),
                   count(*) FILTER (WHERE expires_at <= now() AND expires_at >= now()-interval '15 days')
            FROM nutritionist_clients WHERE nutritionist_id=@id;
            SELECT t.kind,u.full_name,a.program_name,t.due_at,t.task_id
            FROM nutritionist_dashboard_tasks t JOIN nutritionist_clients a USING(assignment_id)
            JOIN users u ON u.user_id=a.client_id
            WHERE a.nutritionist_id=@id AND a.status='active' AND (a.expires_at IS NULL OR a.expires_at > now())
              AND t.status='pending' AND (t.kind='diet' OR (t.due_at>=@day AND t.due_at<@end))
            ORDER BY t.due_at,t.task_id;
            SELECT u.full_name,e.activity_type,e.occurred_at,e.activity_id
            FROM nutritionist_client_activity e JOIN nutritionist_clients a USING(assignment_id)
            JOIN users u ON u.user_id=a.client_id
            WHERE a.nutritionist_id=@id AND a.status='active' AND (a.expires_at IS NULL OR a.expires_at > now())
            ORDER BY e.occurred_at DESC LIMIT 10;
            """, conn);
        cmd.Parameters.AddWithValue("id",id);
        cmd.Parameters.AddWithValue("day",day);
        cmd.Parameters.AddWithValue("end",day.AddDays(1));
        await using var r = await cmd.ExecuteReaderAsync(ct);
        await r.ReadAsync(ct);
        var activeClients=r.GetInt64(0); var newClients=r.GetInt64(1); var pausedClients=r.GetInt64(2); var expiredClients=r.GetInt64(3);
        await r.NextResultAsync(ct);
        var tasks=new List<object>();
        while(await r.ReadAsync(ct)) tasks.Add(new { kind=r.GetString(0), name=r.GetString(1), program=r.GetString(2), dueAt=r.GetDateTime(3), id=r.GetGuid(4) });
        await r.NextResultAsync(ct);
        var activity=new List<object>();
        while(await r.ReadAsync(ct)) activity.Add(new { name=r.GetString(0), type=r.GetString(1), occurredAt=r.GetDateTime(2), id=r.GetGuid(3) });
        return Ok(new {activeClients,newClients,pausedClients,expiredClients,tasks,activity});
    }
}
