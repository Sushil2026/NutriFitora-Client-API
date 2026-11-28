using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Npgsql;
namespace Nutritionist.Api.Controllers;
[ApiController, Authorize(Roles="Administrator"), Route("api/admin/settings/security"), ResponseCache(NoStore=true,Location=ResponseCacheLocation.None)]
public sealed class LoginSecurityController(NpgsqlDataSource db, IPasswordHasher<object> hasher):ControllerBase {
 private Guid Actor => Guid.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier) ?? User.FindFirstValue("sub"),out var id)?id:Guid.Empty;
 private const string Admin="SELECT password_hash FROM users u WHERE u.user_id=@id AND u.is_active AND EXISTS(SELECT 1 FROM user_roles ur JOIN roles r USING(role_id) WHERE ur.user_id=u.user_id AND r.role_name='Administrator')";
 [HttpGet] public async Task<IActionResult> Get(CancellationToken ct){
  await using var check=db.CreateCommand(Admin);check.Parameters.AddWithValue("id",Actor);if(await check.ExecuteScalarAsync(ct) is null)return Forbid();
  await using var cmd=db.CreateCommand("SELECT role_name,enabled FROM login_security");await using var reader=await cmd.ExecuteReaderAsync(ct);var result=new Dictionary<string,bool>();while(await reader.ReadAsync(ct))result[reader.GetString(0)]=reader.GetBoolean(1);return Ok(result);
 }
 public sealed record Change(string Role,bool Enabled,string Password);
 [HttpPut] public async Task<IActionResult> Put(Change request,CancellationToken ct){
  if(request.Role is not ("Customer" or "Nutritionist" or "Administrator") || string.IsNullOrEmpty(request.Password)||request.Password.Length>128)return BadRequest();
  await using var conn=await db.OpenConnectionAsync(ct);await using var tx=await conn.BeginTransactionAsync(ct);
  await using var check=new NpgsqlCommand(Admin+" FOR UPDATE OF u",conn,tx);check.Parameters.AddWithValue("id",Actor);
  if(await check.ExecuteScalarAsync(ct) is not string hash || hasher.VerifyHashedPassword(new object(),hash,request.Password)==PasswordVerificationResult.Failed)return Unauthorized(new {message="Password confirmation failed."});
  await using var cmd=new NpgsqlCommand("WITH old AS (SELECT enabled FROM login_security WHERE role_name=@role FOR UPDATE), changed AS (UPDATE login_security SET enabled=@enabled WHERE role_name=@role AND enabled<>@enabled RETURNING role_name) INSERT INTO login_security_audit(actor,role_name,old_enabled,new_enabled) SELECT @id,changed.role_name,old.enabled,@enabled FROM changed CROSS JOIN old",conn,tx);cmd.Parameters.AddWithValue("id",Actor);cmd.Parameters.AddWithValue("role",request.Role);cmd.Parameters.AddWithValue("enabled",request.Enabled);await cmd.ExecuteNonQueryAsync(ct);await tx.CommitAsync(ct);return NoContent();
 }
}
