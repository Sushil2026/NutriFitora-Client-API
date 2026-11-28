using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Npgsql;
using NpgsqlTypes;
namespace Nutritionist.Api.Controllers;
[ApiController, ResponseCache(NoStore=true,Location=ResponseCacheLocation.None)]
public sealed class ApplicationSettingsController(NpgsqlDataSource db):ControllerBase {
 public sealed record Settings(string CompanyName,string Tagline,string Theme,string? Logo);
 [AllowAnonymous,HttpGet("api/settings/theme")]
 public async Task<IActionResult> Get(CancellationToken ct){
  await using var cmd=db.CreateCommand("SELECT company_name,tagline,theme,logo FROM application_settings WHERE id=1");
  await using var r=await cmd.ExecuteReaderAsync(ct);if(!await r.ReadAsync(ct))return NotFound();return Ok(new Settings(r.GetString(0),r.GetString(1),r.GetString(2),r.IsDBNull(3)?null:r.GetString(3)));
 }
 [Authorize(Roles="Administrator"),HttpPut("api/admin/settings/theme"),RequestSizeLimit(400000)]
 public async Task<IActionResult> Put(Settings s,CancellationToken ct){
  if(string.IsNullOrWhiteSpace(s.CompanyName)||s.CompanyName.Trim().Length>80||s.Tagline is null||s.Tagline.Length>160||s.Theme is not ("mint" or "ocean" or "lavender" or "peach" or "rose" or "classic")||!ValidLogo(s.Logo))return BadRequest(new {message="Invalid branding settings"});
  s=s with {CompanyName=s.CompanyName.Trim(),Tagline=s.Tagline.Trim()};
  await using var cmd=db.CreateCommand("UPDATE application_settings SET company_name=@name,tagline=@tag,theme=@theme,logo=@logo,updated_at=now() WHERE id=1");
  cmd.Parameters.AddWithValue("name",s.CompanyName);cmd.Parameters.AddWithValue("tag",s.Tagline);cmd.Parameters.AddWithValue("theme",s.Theme);cmd.Parameters.AddWithValue("logo",NpgsqlDbType.Text,(object?)s.Logo??DBNull.Value);
  if(await cmd.ExecuteNonQueryAsync(ct)!=1)return NotFound();return Ok(s);
 }
 static bool ValidLogo(string? logo){if(logo is null)return true;if(logo.Length>350000)return false;var i=logo.IndexOf(',');if(i<0)return false;try{var b=Convert.FromBase64String(logo[(i+1)..]);if(b.Length>262144||b.Length<12)return false;return logo[..i] switch {"data:image/png;base64"=>b.AsSpan(0,8).SequenceEqual(new byte[]{137,80,78,71,13,10,26,10}),"data:image/jpeg;base64"=>b[0]==255&&b[1]==216&&b[2]==255,"data:image/webp;base64"=>b.AsSpan(0,4).SequenceEqual("RIFF"u8)&&b.AsSpan(8,4).SequenceEqual("WEBP"u8),_=>false};}catch(FormatException){return false;}}
}
