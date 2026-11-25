using System.Text;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Identity;
using Microsoft.IdentityModel.Tokens;
using Npgsql;

var builder = WebApplication.CreateBuilder(args);

// =====================================================
// 1. Controllers and Swagger
// =====================================================

builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

// =====================================================
// 2. Neon PostgreSQL configuration
// =====================================================

// Render environment variables:
// Neon__Host, Neon__Database, Neon__Username, Neon__Password

var neonHost = builder.Configuration["Neon:Host"]
    ?? throw new InvalidOperationException("Missing Neon:Host configuration.");

var neonDatabase = builder.Configuration["Neon:Database"]
    ?? throw new InvalidOperationException("Missing Neon:Database configuration.");

var neonUsername = builder.Configuration["Neon:Username"]
    ?? throw new InvalidOperationException("Missing Neon:Username configuration.");

var neonPassword = builder.Configuration["Neon:Password"]
    ?? throw new InvalidOperationException("Missing Neon:Password configuration.");

if (string.IsNullOrWhiteSpace(neonHost) ||
    string.IsNullOrWhiteSpace(neonDatabase) ||
    string.IsNullOrWhiteSpace(neonUsername) ||
    string.IsNullOrWhiteSpace(neonPassword))
{
    throw new InvalidOperationException(
        "Neon database configuration cannot be empty.");
}

// Construct the connection string safely, including passwords with special characters.
var neonConnectionBuilder = new NpgsqlConnectionStringBuilder
{
    Host = neonHost,
    Port = 5432,
    Database = neonDatabase,
    Username = neonUsername,
    Password = neonPassword,
    SslMode = SslMode.VerifyFull,
    Pooling = true,
    Timeout = 15,
    CommandTimeout = 30
};

// Npgsql without Entity Framework.
builder.Services.AddSingleton<NpgsqlDataSource>(_ =>
    NpgsqlDataSource.Create(neonConnectionBuilder.ConnectionString));

Console.WriteLine("Neon PostgreSQL configuration initialized.");

// =====================================================
// 3. Password hashing
// =====================================================

builder.Services.AddScoped<IPasswordHasher<object>, PasswordHasher<object>>();

// =====================================================
// 4. CORS configuration
// =====================================================

// Explicitly allow the deployed Render frontend and local development sites.
// Additional origins can be supplied through Render's Cors__AllowedOrigins
// environment variable (comma-separated, without trailing slashes).
var allowedOrigins = new List<string>
{
    "https://nutrifitora-client-web.onrender.com",
    "http://localhost:8081",
    "http://localhost:19006",
    "http://localhost:3000",
    "http://127.0.0.1:8081"
};

var configuredOrigins = builder.Configuration["Cors:AllowedOrigins"];
if (!string.IsNullOrWhiteSpace(configuredOrigins))
{
    allowedOrigins.AddRange(configuredOrigins.Split(
        ',',
        StringSplitOptions.RemoveEmptyEntries |
        StringSplitOptions.TrimEntries));
}

var normalizedOrigins = allowedOrigins
    .Select(origin => origin.Trim().TrimEnd('/'))
    .Where(origin => !string.IsNullOrWhiteSpace(origin))
    .Distinct(StringComparer.OrdinalIgnoreCase)
    .ToArray();

builder.Services.AddCors(options =>
{
    options.AddPolicy("NutriFitoraCors", policy =>
    {
        policy.WithOrigins(normalizedOrigins)
              .AllowAnyHeader()
              .AllowAnyMethod();
    });
});

// =====================================================
// 5. JWT configuration
// =====================================================

var jwtKey = builder.Configuration["Jwt:Key"]
    ?? throw new InvalidOperationException("Missing Jwt:Key configuration.");

if (Encoding.UTF8.GetByteCount(jwtKey) < 32)
{
    throw new InvalidOperationException(
        "Jwt:Key must contain at least 32 UTF-8 bytes.");
}

var jwtIssuer = builder.Configuration["Jwt:Issuer"] ?? "Nutritionist.Api";
var jwtAudience = builder.Configuration["Jwt:Audience"] ?? "Nutritionist.Pwa";

var signingKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtKey));

builder.Services
    .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = jwtIssuer,
            ValidateAudience = true,
            ValidAudience = jwtAudience,
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = signingKey,
            ValidateLifetime = true,
            RequireExpirationTime = true,
            ClockSkew = TimeSpan.FromSeconds(30)
        };
    });

builder.Services.AddAuthorization();

// =====================================================
// 6. Build application
// =====================================================

var app = builder.Build();

// =====================================================
// 7. Exception handling
// =====================================================

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler(errorApp =>
    {
        errorApp.Run(async context =>
        {
            var exception = context.Features.Get<
                Microsoft.AspNetCore.Diagnostics.IExceptionHandlerFeature>()?.Error;

            var logger = context.RequestServices
                .GetRequiredService<ILogger<Program>>();

            if (exception != null)
            {
                logger.LogError(
                    exception,
                    "Unhandled exception on {Path}",
                    context.Request.Path);
            }

            context.Response.StatusCode = 500;
            await context.Response.WriteAsJsonAsync(new
            {
                message = "Internal server error."
            });
        });
    });
}

// =====================================================
// 8. Swagger
// =====================================================

// Public Swagger is retained from the existing configuration.
// Consider restricting it for production use.
app.UseSwagger();
app.UseSwaggerUI();

// =====================================================
// 9. Middleware
// =====================================================

// Render terminates public HTTPS at its reverse proxy.
app.UseRouting();
app.UseCors("NutriFitoraCors"); // Must run before authentication/authorization.
app.UseAuthentication();
app.UseAuthorization();
app.MapControllers();
app.MapGet("/api/version", () => new
{
    version = "nutritionist-dashboard-v1",
    application = "Nutritionist.Api"
});

// =====================================================
// 10. API homepage
// =====================================================

app.MapGet("/", () => Results.Ok(new
{
    message = "NutriFitora API is running successfully",
    status = "Online",
    version = "1.0"
}));

// =====================================================
// 11. Health endpoint
// =====================================================

app.MapGet("/health", () => Results.Ok(new
{
    status = "Healthy",
    service = "NutriFitora API",
    environment = app.Environment.EnvironmentName
}));

// =====================================================
// 12. Start application
// =====================================================

app.Run();
