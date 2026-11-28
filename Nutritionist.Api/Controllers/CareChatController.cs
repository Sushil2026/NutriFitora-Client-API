using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Npgsql;

namespace Nutritionist.Api.Controllers;

[ApiController, Authorize(Roles="Customer,Nutritionist"), Route("api/chat")]
[ResponseCache(NoStore=true, Location=ResponseCacheLocation.None)]
public sealed class CareChatController(NpgsqlDataSource db) : ControllerBase
{
    private Guid Actor => Guid.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier) ?? User.FindFirstValue("sub"), out var id) ? id : Guid.Empty;
    private bool Customer => User.IsInRole("Customer");
    // Every operation checks the actual assignment and current account/role, not a client-supplied user ID.
    private const string Scope = """
      FROM nutritionist_clients a JOIN users c ON c.user_id=a.client_id
      JOIN users n ON n.user_id=a.nutritionist_id
      WHERE ((@customer AND a.client_id=@actor) OR (NOT @customer AND a.nutritionist_id=@actor))
      AND CASE WHEN @customer THEN c.is_active ELSE n.is_active END
      AND EXISTS(SELECT 1 FROM user_roles ur JOIN roles r USING(role_id)
        WHERE ur.user_id=@actor AND r.role_name=CASE WHEN @customer THEN 'Customer' ELSE 'Nutritionist' END)
      """;
    private const string Writable = "a.status='active' AND (a.expires_at IS NULL OR a.expires_at>clock_timestamp()) AND c.is_active AND n.is_active AND EXISTS(SELECT 1 FROM user_roles ur JOIN roles r USING(role_id) WHERE ur.user_id=a.client_id AND r.role_name='Customer') AND EXISTS(SELECT 1 FROM user_roles ur JOIN roles r USING(role_id) WHERE ur.user_id=a.nutritionist_id AND r.role_name='Nutritionist')";
    private NpgsqlCommand Command(string sql, Guid? assignment=null) {
      var cmd=db.CreateCommand(sql); cmd.Parameters.AddWithValue("actor",Actor);cmd.Parameters.AddWithValue("customer",Customer);
      if(assignment.HasValue)cmd.Parameters.AddWithValue("assignment",assignment.Value);return cmd;
    }
    [HttpGet("conversations")]
    [HttpGet("/api/customer/care-team")]
    public async Task<IActionResult> Conversations(CancellationToken ct) {
      if(Actor==Guid.Empty)return Unauthorized();
      if(Request.Path.StartsWithSegments("/api/customer")&&!Customer)return Forbid();
      await using var cmd=Command("""
        SELECT a.assignment_id, CASE WHEN @customer THEN n.full_name ELSE c.full_name END,
        a.program_name, CASE WHEN a.expires_at<=now() THEN 'expired' ELSE a.status END,
        a.assigned_at,a.expires_at,
        (SELECT count(*) FROM chat_messages m WHERE m.assignment_id=a.assignment_id AND m.sender_id<>@actor AND m.read_at IS NULL),
        CASE WHEN @customer THEN (SELECT professional_title FROM nutritionist_profiles WHERE user_id=n.user_id LIMIT 1) END,
        CASE WHEN @customer THEN (SELECT specialization FROM nutritionist_profiles WHERE user_id=n.user_id LIMIT 1) END,
        CASE WHEN @customer THEN (SELECT bio FROM nutritionist_profiles WHERE user_id=n.user_id LIMIT 1) END,
        (
        """+Writable+") "+Scope+" ORDER BY a.assigned_at DESC");
      await using var r=await cmd.ExecuteReaderAsync(ct);var rows=new List<object>();
      while(await r.ReadAsync(ct))rows.Add(new {id=r.GetGuid(0),name=r.GetString(1),program=r.GetString(2),status=r.GetString(3),assignedAt=r.GetDateTime(4),expiresAt=r.IsDBNull(5)?(DateTime?)null:r.GetDateTime(5),unread=r.GetInt64(6),title=r.IsDBNull(7)?null:r.GetString(7),specialization=r.IsDBNull(8)?null:r.GetString(8),bio=r.IsDBNull(9)?null:r.GetString(9),canSend=r.GetBoolean(10)});
      return Ok(rows);
    }
    [HttpGet("unread-count")]
    public async Task<IActionResult> Unread(CancellationToken ct){
      if(Actor==Guid.Empty)return Unauthorized();
      await using var cmd=Command("SELECT COALESCE(sum((SELECT count(*) FROM chat_messages m WHERE m.assignment_id=a.assignment_id AND m.sender_id<>@actor AND m.read_at IS NULL)),0)::bigint "+Scope);
      return Ok(new {count=(long)(await cmd.ExecuteScalarAsync(ct)??0L)});
    }
    [HttpGet("{id:guid}/messages")]
    public async Task<IActionResult> Messages(Guid id,long? before,CancellationToken ct){
      if(Actor==Guid.Empty)return Unauthorized();if(before is <=0)return BadRequest();
      await using var cmd=Command("SELECT a.assignment_id, ("+Writable+"), m.message_id,m.body,m.sent_at,m.read_at,m.sender_id=@actor "+Scope.Replace("WHERE ((@customer"," LEFT JOIN LATERAL (SELECT * FROM chat_messages WHERE assignment_id=a.assignment_id AND message_id<@before ORDER BY message_id DESC LIMIT 51) m ON true WHERE ((@customer")+" AND a.assignment_id=@assignment ORDER BY m.message_id DESC",id);
      cmd.Parameters.AddWithValue("before",before??long.MaxValue);
      await using var r=await cmd.ExecuteReaderAsync(ct);if(!await r.ReadAsync(ct))return NotFound();
      var canSend=r.GetBoolean(1);var rows=new List<object>();
      do {if(!r.IsDBNull(2))rows.Add(new {id=r.GetInt64(2).ToString(),body=r.GetString(3),sentAt=r.GetDateTime(4),readAt=r.IsDBNull(5)?(DateTime?)null:r.GetDateTime(5),mine=r.GetBoolean(6)});}while(await r.ReadAsync(ct));
      return Ok(new {canSend,hasMore=rows.Count>50,items=rows.Take(50).Reverse()});
    }
    public sealed record SendRequest(string Body,Guid ClientKey);
    [HttpPost("{id:guid}/messages")]
    public async Task<IActionResult> Send(Guid id,SendRequest request,CancellationToken ct){
      if(Actor==Guid.Empty)return Unauthorized();var body=request.Body?.Trim()??"";
      if(body.Length is <1 or >2000 || request.ClientKey==Guid.Empty)return BadRequest(new {message="Enter a message of 1–2000 characters."});
      // Lock assignment so admin status updates serialize with sending.
      await using var cmd=Command("WITH eligible AS (SELECT a.assignment_id "+Scope+" AND a.assignment_id=@assignment AND "+Writable+" FOR UPDATE OF a) INSERT INTO chat_messages(assignment_id,sender_id,body,client_key) SELECT assignment_id,@actor,@body,@key FROM eligible ON CONFLICT(assignment_id,sender_id,client_key) DO UPDATE SET client_key=EXCLUDED.client_key RETURNING message_id",id);
      cmd.Parameters.AddWithValue("body",body);cmd.Parameters.AddWithValue("key",request.ClientKey);
      var result=await cmd.ExecuteScalarAsync(ct);return result is long mid?Ok(new {id=mid.ToString()}):Conflict(new {message="Chat is unavailable or assignment is read-only."});
    }
    public sealed record ReadRequest(string Through);
    [HttpPatch("{id:guid}/read")]
    public async Task<IActionResult> Read(Guid id,ReadRequest request,CancellationToken ct){
      if(Actor==Guid.Empty)return Unauthorized();if(!long.TryParse(request.Through,out var through)||through<=0)return BadRequest();
      await using var cmd=Command("UPDATE chat_messages SET read_at=now() WHERE assignment_id IN (SELECT a.assignment_id "+Scope+" AND a.assignment_id=@assignment) AND sender_id<>@actor AND read_at IS NULL AND message_id<=@through",id);
      cmd.Parameters.AddWithValue("through",through);await cmd.ExecuteNonQueryAsync(ct);return NoContent();
    }
}
