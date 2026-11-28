using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Npgsql;
namespace Nutritionist.Api.Controllers;
[ApiController, Authorize(Roles="Administrator"), Route("api/admin/notifications")]
[ResponseCache(NoStore=true, Location=ResponseCacheLocation.None)]
public sealed class AdminNotificationCustomerController(NpgsqlDataSource db): ControllerBase
{
 [HttpGet("{id:long}/customer")]
 public async Task<IActionResult> GetCustomer(long id,CancellationToken ct){
  await using var cmd=db.CreateCommand("""
  SELECT u.user_id AS id,u.full_name AS name,u.email,u.phone_number AS phone,p.date_of_birth::text AS dob,u.created_at AS "registeredAt",u.is_active AS active,u.email_verified AS verified,p.height_cm AS height,p.current_weight_kg AS weight,p.target_weight_kg AS target,CASE WHEN p.height_cm>0 THEN round(p.current_weight_kg / power(p.height_cm/100,2)::numeric,2) END AS bmi,p.dietary_preference AS diet,p.activity_level AS activity
  FROM admin_notifications n JOIN users u ON u.user_id=n.customer_id LEFT JOIN customer_profiles p ON p.user_id=u.user_id WHERE n.id=@id AND EXISTS(SELECT 1 FROM user_roles ur JOIN roles r USING(role_id) WHERE ur.user_id=u.user_id AND r.role_name='Customer');
  SELECT u.full_name AS nutritionist,a.program_name AS program,CASE WHEN a.expires_at<=now() THEN 'expired' ELSE a.status END AS status,a.assigned_at AS start,a.expires_at AS expiry FROM admin_notifications n JOIN nutritionist_clients a ON a.client_id=n.customer_id JOIN users u ON u.user_id=a.nutritionist_id WHERE n.id=@id ORDER BY a.assigned_at DESC;
  """);
  cmd.Parameters.AddWithValue("id",id);
  await using var r=await cmd.ExecuteReaderAsync(ct);
  if(!await r.ReadAsync(ct))return NotFound();
  Dictionary<string,object?> Row(){var row=new Dictionary<string,object?>();for(int i=0;i<r.FieldCount;i++)row[r.GetName(i)]=r.IsDBNull(i)?null:r.GetValue(i);return row;}
  var profile=Row();var assignments=new List<Dictionary<string,object?>>();await r.NextResultAsync(ct);while(await r.ReadAsync(ct))assignments.Add(Row());profile["assignments"]=assignments;return Ok(profile);
 }
}
