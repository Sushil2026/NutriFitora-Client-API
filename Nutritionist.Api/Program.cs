using System.Text;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Identity;
using Microsoft.IdentityModel.Tokens;
using Npgsql;

var builder = WebApplication.CreateBuilder(args);

// Controllers and Swagger
builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

// Neon PostgreSQL
var cs = builder.Configuration.GetConnectionString("Neon")
    ?? throw new InvalidOperationException(
        "Configure ConnectionStrings:Neon.");

// JWT configuration
var jwtKey = builder.Configuration["Jwt:Key"]
    ?? throw new InvalidOperationException(
        "Configure Jwt:Key.");

if (Encoding.UTF8.GetByteCount(jwtKey) < 32)
{
    throw new InvalidOperationException(
        "Jwt:Key must be at least 32 bytes.");
}

// Database connection
builder.Services.AddSingleton(_ =>
    NpgsqlDataSource.Create(cs));

// Password hashing
builder.Services.AddScoped<
    IPasswordHasher<object>,
    PasswordHasher<object>>();

// CORS
builder.Services.AddCors(options =>
{
    options.AddPolicy("NutriFitoraCors", policy =>
    {
        policy.WithOrigins(
                "http://localhost:8081",
                "http://localhost:19006",
                "http://localhost:3000"
            )
            .AllowAnyHeader()
            .AllowAnyMethod();
    });
});
// JWT authentication
var key = new SymmetricSecurityKey(
    Encoding.UTF8.GetBytes(jwtKey));

builder.Services
    .AddAuthentication(
        JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
        options.TokenValidationParameters =
            new TokenValidationParameters
            {
                ValidateIssuer = true,
                ValidIssuer =
                    builder.Configuration["Jwt:Issuer"]
                    ?? "Nutritionist.Api",

                ValidateAudience = true,
                ValidAudience =
                    builder.Configuration["Jwt:Audience"]
                    ?? "Nutritionist.Pwa",

                ValidateIssuerSigningKey = true,
                IssuerSigningKey = key,

                ValidateLifetime = true,
                ClockSkew = TimeSpan.FromSeconds(30)
            });

builder.Services.AddAuthorization();

var app = builder.Build();

// Swagger
app.UseSwagger();
app.UseSwaggerUI();

// Middleware
app.UseHttpsRedirection();
app.UseRouting();
app.UseCors("NutriFitoraCors");
app.UseAuthentication();
app.UseAuthorization();
app.MapControllers();

// API homepage
app.MapGet("/", () => Results.Ok(new
{
    message = "NutriFitora API is running successfully",
    status = "Online",
    version = "1.0"
}));

// Health endpoint
app.MapGet("/health", () => Results.Ok(new
{
    status = "Healthy",
    service = "NutriFitora API"
}));

app.Run();
