using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Npgsql;

namespace Nutritionist.Api.Controllers;

[ApiController]
[Authorize(Roles = "Administrator")]
[Route("api/admin/client-assignments")]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public sealed class AdminClientAssignmentsController(NpgsqlDataSource db) : ControllerBase
{
    public sealed record AssignRequest(Guid ClientId, Guid NutritionistId, string ProgramCode);
    public sealed record StatusRequest(string Status);

    [HttpGet]
    public async Task<IActionResult> Get(CancellationToken ct)
    {
        await using var cmd = db.CreateCommand("""
            SELECT u.user_id,u.full_name,r.role_name FROM users u
            JOIN user_roles ur USING(user_id) JOIN roles r USING(role_id)
            WHERE u.is_active AND r.role_name IN ('Customer','Nutritionist') ORDER BY u.full_name;
            SELECT program_code,program_name,duration_weeks FROM nutrition_programs
            WHERE is_active AND duration_weeks>0 ORDER BY display_order;
            SELECT a.assignment_id,c.full_name,n.full_name,a.program_name,
            CASE WHEN a.expires_at<=now() THEN 'expired' ELSE a.status END,
            a.assigned_at,a.expires_at FROM nutritionist_clients a
            JOIN users c ON c.user_id=a.client_id JOIN users n ON n.user_id=a.nutritionist_id
            ORDER BY a.assigned_at DESC;
            """);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        var customers=new List<object>(); var nutritionists=new List<object>();
        while(await r.ReadAsync(ct)) (r.GetString(2)=="Customer"?customers:nutritionists).Add(new {id=r.GetGuid(0),name=r.GetString(1)});
        await r.NextResultAsync(ct); var programs=new List<object>();
        while(await r.ReadAsync(ct)) programs.Add(new {code=r.GetString(0),name=r.GetString(1),weeks=r.GetInt32(2)});
        await r.NextResultAsync(ct); var assignments=new List<object>();
        while(await r.ReadAsync(ct)) assignments.Add(new {id=r.GetGuid(0),client=r.GetString(1),nutritionist=r.GetString(2),program=r.GetString(3),status=r.GetString(4),assignedAt=r.GetDateTime(5),expiresAt=r.IsDBNull(6)?(DateTime?)null:r.GetDateTime(6)});
        return Ok(new {customers,nutritionists,programs,assignments});
    }

    [HttpPost]
    public async Task<IActionResult> Assign(AssignRequest request,CancellationToken ct)
    {
        if(request.ClientId==Guid.Empty || request.NutritionistId==Guid.Empty || request.ClientId==request.NutritionistId || string.IsNullOrWhiteSpace(request.ProgramCode)) return BadRequest(new {message="Select a valid customer, nutritionist and program."});
        await using var cmd=db.CreateCommand("""
            INSERT INTO nutritionist_clients(assignment_id,nutritionist_id,client_id,program_name,status,assigned_at,expires_at)
            SELECT @id,@nutritionist,@client,p.program_name,'active',now(),now()+make_interval(weeks=>p.duration_weeks)
            FROM nutrition_programs p WHERE p.program_code=@code AND p.is_active AND p.duration_weeks>0
            AND EXISTS(SELECT 1 FROM users u JOIN user_roles ur USING(user_id) JOIN roles r USING(role_id) WHERE u.user_id=@client AND u.is_active AND r.role_name='Customer')
            AND EXISTS(SELECT 1 FROM users u JOIN user_roles ur USING(user_id) JOIN roles r USING(role_id) WHERE u.user_id=@nutritionist AND u.is_active AND r.role_name='Nutritionist')
            ON CONFLICT(nutritionist_id,client_id) DO NOTHING RETURNING assignment_id;
            """);
        cmd.Parameters.AddWithValue("id",Guid.NewGuid()); cmd.Parameters.AddWithValue("client",request.ClientId); cmd.Parameters.AddWithValue("nutritionist",request.NutritionistId); cmd.Parameters.AddWithValue("code",request.ProgramCode.Trim());
        var result=await cmd.ExecuteScalarAsync(ct);
        return result is Guid id ? Ok(new {id}) : Conflict(new {message="Assignment already exists or selected user/program is no longer eligible."});
    }

    [HttpPatch("{id:guid}/status")]
    public async Task<IActionResult> Status(Guid id,StatusRequest request,CancellationToken ct)
    {
        if(request.Status is not ("active" or "paused" or "expired")) return BadRequest(new {message="Invalid status."});
        await using var cmd=db.CreateCommand("""
            UPDATE nutritionist_clients SET status=@status,
            expires_at=CASE WHEN @status='expired' THEN LEAST(COALESCE(expires_at,now()),now()) ELSE expires_at END
            WHERE assignment_id=@id AND (@status='expired' OR (status<>'expired' AND (expires_at IS NULL OR expires_at>now())))
            RETURNING assignment_id;
            """);
        cmd.Parameters.AddWithValue("id",id); cmd.Parameters.AddWithValue("status",request.Status);
        return await cmd.ExecuteScalarAsync(ct) is Guid ? Ok(new {id}) : Conflict(new {message="Assignment not found or expired. Renewal is not supported in this phase."});
    }
}
