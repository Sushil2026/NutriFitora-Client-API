using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Identity;
using Npgsql;
using NpgsqlTypes;
namespace Nutritionist.Api.Controllers;
[ApiController, Authorize(Roles="Administrator"), Route("api/admin/foundation")]
[ResponseCache(NoStore=true,Location=ResponseCacheLocation.None)]
public sealed class AdminFoundationController(NpgsqlDataSource db, IPasswordHasher<object> hasher):ControllerBase
{
    [HttpGet("{page}")]
    public async Task<IActionResult> Get(string page,CancellationToken ct){
        var sql=page switch {
            "dashboard"=>"""
            SELECT (SELECT count(*) FROM users u WHERE EXISTS(SELECT 1 FROM user_roles ur JOIN roles r USING(role_id) WHERE ur.user_id=u.user_id AND r.role_name='Customer')) AS customers,
            (SELECT count(*) FROM users u WHERE u.is_active AND EXISTS(SELECT 1 FROM user_roles ur JOIN roles r USING(role_id) WHERE ur.user_id=u.user_id AND r.role_name='Nutritionist')) AS nutritionists,
            (SELECT count(DISTINCT client_id) FROM nutritionist_clients WHERE status='active' AND (expires_at IS NULL OR expires_at>now())) AS active_clients,
            (SELECT count(*) FROM users u WHERE u.is_active AND EXISTS(SELECT 1 FROM user_roles ur JOIN roles r USING(role_id) WHERE ur.user_id=u.user_id AND r.role_name='Customer') AND NOT EXISTS(SELECT 1 FROM nutritionist_clients a WHERE a.client_id=u.user_id AND a.status IN ('active','paused') AND (a.expires_at IS NULL OR a.expires_at>now()))) AS unassigned,
            (SELECT count(*) FROM nutrition_programs WHERE is_active) AS active_plans,
            (SELECT count(*) FROM admin_events WHERE is_active AND starts_at>now()) AS upcoming_events
            """,
            "customers"=>"SELECT u.user_id AS id,u.full_name AS name,u.email,u.is_active AS active FROM users u WHERE EXISTS(SELECT 1 FROM user_roles ur JOIN roles r USING(role_id) WHERE ur.user_id=u.user_id AND r.role_name='Customer') ORDER BY u.full_name",
            "nutritionists"=>"SELECT u.user_id AS id,u.full_name AS name,u.email,u.is_active AS active,p.professional_title AS title,p.specialization,p.bio FROM users u LEFT JOIN nutritionist_profiles p USING(user_id) WHERE EXISTS(SELECT 1 FROM user_roles ur JOIN roles r USING(role_id) WHERE ur.user_id=u.user_id AND r.role_name='Nutritionist') ORDER BY u.full_name",
            "plans"=>"SELECT program_code AS id,program_name AS name,duration_weeks AS weeks,price_inr AS price,description,is_active AS active,is_purchasable AS purchasable FROM nutrition_programs ORDER BY display_order",
            "goals"=>"SELECT id,name,unit,description,is_active AS active FROM admin_goal_templates ORDER BY name",
            "events"=>"SELECT id,title AS name,description,starts_at AS start,ends_at AS end,is_active AS active FROM admin_events ORDER BY starts_at",
            _=>null};
        if(sql is null)return NotFound();
        await using var cmd=db.CreateCommand(sql); await using var reader=await cmd.ExecuteReaderAsync(ct); var rows=new List<Dictionary<string,object?>>();
        while(await reader.ReadAsync(ct)){var row=new Dictionary<string,object?>();for(int i=0;i<reader.FieldCount;i++)row[reader.GetName(i)]=reader.IsDBNull(i)?null:reader.GetValue(i);rows.Add(row);}return Ok(rows);
    }
    public record Person(string Name,string Email,string Password,string? Title,string? Specialization,string? Bio);
    [HttpPost("nutritionists")]
    public async Task<IActionResult> Onboard(Person p,CancellationToken ct){
        if(string.IsNullOrWhiteSpace(p.Name)||string.IsNullOrWhiteSpace(p.Email)||!new System.ComponentModel.DataAnnotations.EmailAddressAttribute().IsValid(p.Email)||p.Password is null||p.Password.Length<12||p.Password.Length>128)return BadRequest(new{message="Valid name, email and password of 12–128 characters required."});
        await using var conn=await db.OpenConnectionAsync(ct);await using var tx=await conn.BeginTransactionAsync(ct);
        await using var cmd=new NpgsqlCommand("""
        INSERT INTO users(user_id,full_name,email,password_hash,is_active,email_verified,created_at,updated_at) VALUES(@id,@name,@email,@hash,false,false,now(),now());
        INSERT INTO user_roles(user_id,role_id,assigned_at) VALUES(@id,(SELECT role_id FROM roles WHERE role_name='Nutritionist'),now());
        INSERT INTO nutritionist_profiles(user_id,professional_title,specialization,bio,created_at) VALUES(@id,@title,@specialization,@bio,now());
        """,conn,tx);
        var id=Guid.NewGuid();cmd.Parameters.AddWithValue("id",id);cmd.Parameters.AddWithValue("name",p.Name.Trim());cmd.Parameters.AddWithValue("email",p.Email.Trim().ToLowerInvariant());cmd.Parameters.AddWithValue("hash",hasher.HashPassword(new object(),p.Password));cmd.Parameters.AddWithValue("title",p.Title??"");cmd.Parameters.AddWithValue("specialization",p.Specialization??"");cmd.Parameters.AddWithValue("bio",p.Bio??"");
        try{await cmd.ExecuteNonQueryAsync(ct);await tx.CommitAsync(ct);}catch(PostgresException e) when(e.SqlState=="23505"){return Conflict(new{message="Account already exists."});}return Ok(new{id});
    }
    public record Activation(bool Active);
    [HttpPatch("nutritionists/{id:guid}")]
    public async Task<IActionResult> Activate(Guid id,Activation p,CancellationToken ct){await using var cmd=db.CreateCommand("UPDATE users SET is_active=@active,updated_at=now() WHERE user_id=@id AND EXISTS(SELECT 1 FROM user_roles ur JOIN roles r USING(role_id) WHERE ur.user_id=@id AND r.role_name='Nutritionist') AND NOT EXISTS(SELECT 1 FROM user_roles ur JOIN roles r USING(role_id) WHERE ur.user_id=@id AND r.role_name='Administrator')");cmd.Parameters.AddWithValue("id",id);cmd.Parameters.AddWithValue("active",p.Active);return await cmd.ExecuteNonQueryAsync(ct)==1?Ok():NotFound();}
    public record Plan(string Name,int Weeks,decimal? Price,string? Description,bool Active,bool Purchasable);
    [HttpPut("plans/{code}")]
    public async Task<IActionResult> SavePlan(string code,Plan p,CancellationToken ct){if(string.IsNullOrWhiteSpace(p.Name)||p.Weeks<1||p.Weeks>520||p.Price<0|| (p.Purchasable && (!p.Active||p.Price is null)))return BadRequest(new{message="Invalid plan details."});await using var cmd=db.CreateCommand("UPDATE nutrition_programs SET program_name=@name,duration_weeks=@weeks,price_inr=@price,description=@description,is_active=@active,is_purchasable=@purchase,updated_at=now() WHERE program_code=@code");cmd.Parameters.AddWithValue("code",code);cmd.Parameters.AddWithValue("name",p.Name.Trim());cmd.Parameters.AddWithValue("weeks",p.Weeks);cmd.Parameters.Add("price",NpgsqlDbType.Numeric).Value=(object?)p.Price??DBNull.Value;cmd.Parameters.AddWithValue("description",p.Description??"");cmd.Parameters.AddWithValue("active",p.Active);cmd.Parameters.AddWithValue("purchase",p.Purchasable);return await cmd.ExecuteNonQueryAsync(ct)==1?Ok():NotFound();}
    public record Goal(string Name,string Unit,string? Description,bool Active);
    [HttpPut("goals/{id:guid}")]
    public async Task<IActionResult> SaveGoal(Guid id,Goal p,CancellationToken ct){if(string.IsNullOrWhiteSpace(p.Name)||string.IsNullOrWhiteSpace(p.Unit))return BadRequest(new{message="Name and unit required."});await using var cmd=db.CreateCommand("INSERT INTO admin_goal_templates(id,name,unit,description,is_active) VALUES(@id,@name,@unit,@description,@active) ON CONFLICT(id) DO UPDATE SET name=excluded.name,unit=excluded.unit,description=excluded.description,is_active=excluded.is_active");cmd.Parameters.AddWithValue("id",id);cmd.Parameters.AddWithValue("name",p.Name.Trim());cmd.Parameters.AddWithValue("unit",p.Unit.Trim());cmd.Parameters.AddWithValue("description",p.Description??"");cmd.Parameters.AddWithValue("active",p.Active);await cmd.ExecuteNonQueryAsync(ct);return Ok();}
    public record Event(string Name,string? Description,DateTimeOffset Start,DateTimeOffset End,bool Active);
    [HttpPut("events/{id:guid}")]
    public async Task<IActionResult> SaveEvent(Guid id,Event p,CancellationToken ct){if(string.IsNullOrWhiteSpace(p.Name)||p.Start==default||p.End<=p.Start)return BadRequest(new{message="Valid title and event dates required."});await using var cmd=db.CreateCommand("INSERT INTO admin_events(id,title,description,starts_at,ends_at,is_active) VALUES(@id,@name,@description,@start,@end,@active) ON CONFLICT(id) DO UPDATE SET title=excluded.title,description=excluded.description,starts_at=excluded.starts_at,ends_at=excluded.ends_at,is_active=excluded.is_active");cmd.Parameters.AddWithValue("id",id);cmd.Parameters.AddWithValue("name",p.Name.Trim());cmd.Parameters.AddWithValue("description",p.Description??"");cmd.Parameters.AddWithValue("start",p.Start.UtcDateTime);cmd.Parameters.AddWithValue("end",p.End.UtcDateTime);cmd.Parameters.AddWithValue("active",p.Active);await cmd.ExecuteNonQueryAsync(ct);return Ok();}
    [HttpPost("goals")] public Task<IActionResult> NewGoal(Goal p,CancellationToken ct)=>SaveGoal(Guid.NewGuid(),p,ct);
    [HttpPost("events")] public Task<IActionResult> NewEvent(Event p,CancellationToken ct)=>SaveEvent(Guid.NewGuid(),p,ct);
}
