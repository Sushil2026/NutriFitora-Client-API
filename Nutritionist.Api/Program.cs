using System.Text;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Identity;
using Microsoft.IdentityModel.Tokens;
using Npgsql;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

var cs = builder.Configuration.GetConnectionString("Neon")
    ?? throw new InvalidOperationException("Configure ConnectionStrings:Neon.");
var jwtKey = builder.Configuration["Jwt:Key"]
    ?? throw new InvalidOperationException("Configure Jwt:Key.");
if (Encoding.UTF8.GetByteCount(jwtKey) < 32)
    throw new InvalidOperationException("Jwt:Key must be at least 32 bytes.");

builder.Services.AddSingleton(_ => NpgsqlDataSource.Create(cs));
builder.Services.AddScoped<IPasswordHasher<object>, PasswordHasher<object>>();
builder.Services.AddCors(options => options.AddPolicy("Frontend", policy =>
    policy.WithOrigins(builder.Configuration.GetSection("Cors:AllowedOrigins").Get<string[]>()
        ?? ["http://localhost:8081", "http://localhost:19006"])
    .AllowAnyHeader().AllowAnyMethod()));

var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtKey));
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
.AddJwtBearer(options => options.TokenValidationParameters = new TokenValidationParameters {
    ValidateIssuer = true, ValidIssuer = builder.Configuration["Jwt:Issuer"] ?? "Nutritionist.Api",
    ValidateAudience = true, ValidAudience = builder.Configuration["Jwt:Audience"] ?? "Nutritionist.Pwa",
    ValidateIssuerSigningKey = true, IssuerSigningKey = key,
    ValidateLifetime = true, ClockSkew = TimeSpan.FromSeconds(30)
});
builder.Services.AddAuthorization();

var app = builder.Build();
app.UseSwagger();
app.UseSwaggerUI();
app.UseHttpsRedirection();
app.UseCors("Frontend");
app.UseAuthentication();
app.UseAuthorization();
app.MapControllers();
app.Run();
