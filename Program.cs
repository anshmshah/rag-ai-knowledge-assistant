using LocalRagAPI.Services;
using LocalRagAPI.Data;
using LocalRagAPI.Repositories;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;
using System.Text;

var builder = WebApplication.CreateBuilder(args);

// -------------------- SERVICES --------------------

builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(c =>
{
    c.SwaggerDoc("v1", new() { Title = "LocalRagAPI", Version = "v1" });

    c.AddSecurityDefinition("Bearer", new Microsoft.OpenApi.Models.OpenApiSecurityScheme
    {
        Name = "Authorization",
        Type = Microsoft.OpenApi.Models.SecuritySchemeType.Http,
        Scheme = "bearer",
        BearerFormat = "JWT",
        In = Microsoft.OpenApi.Models.ParameterLocation.Header,
        Description = "Enter 'Bearer {token}'"
    });

    c.AddSecurityRequirement(new Microsoft.OpenApi.Models.OpenApiSecurityRequirement
    {
        {
            new Microsoft.OpenApi.Models.OpenApiSecurityScheme
            {
                Reference = new Microsoft.OpenApi.Models.OpenApiReference
                {
                    Type = Microsoft.OpenApi.Models.ReferenceType.SecurityScheme,
                    Id = "Bearer"
                }
            },
            new string[] {}
        }
    });
});

// -------------------- HTTP CLIENTS --------------------

builder.Services.AddHttpClient<ILLMService, GroqLLMService>()
    .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler { MaxConnectionsPerServer = 50 });

builder.Services.AddHttpClient<JinaEmbeddingService>()
    .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler { MaxConnectionsPerServer = 50 });

builder.Services.AddHttpClient<JinaRerankerService>()
    .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler { MaxConnectionsPerServer = 50 });

// -------------------- SINGLETONS --------------------

builder.Services.AddSingleton<VectorStore>();
builder.Services.AddSingleton<ChatMemory>();
builder.Services.AddSingleton<QdrantService>();
builder.Services.AddSingleton<PromptBuilderService>();

// -------------------- BACKGROUND SERVICES --------------------

builder.Services.AddSingleton<LocalRagAPI.Services.DocumentIngestionQueue>();
builder.Services.AddSingleton<LocalRagAPI.Services.IngestionJobStore>();
builder.Services.AddScoped<LocalRagAPI.Services.DocumentProcessor>();
builder.Services.AddHostedService<LocalRagAPI.Services.DocumentIngestionWorker>();

// -------------------- DATABASE --------------------

var connectionString = builder.Configuration.GetConnectionString("Default");

if (string.IsNullOrEmpty(connectionString))
{
    throw new Exception("Database connection string is not configured.");
}

builder.Services.AddDbContext<ApplicationDbContext>(options =>
    options.UseNpgsql(connectionString));

// -------------------- REPOSITORIES --------------------

builder.Services.AddScoped<IUserRepository, UserRepository>();
builder.Services.AddScoped<IDocumentRepository, DocumentRepository>();
builder.Services.AddScoped<IChatSessionRepository, ChatSessionRepository>();
builder.Services.AddScoped<IMessageRepository, MessageRepository>();
builder.Services.AddScoped<IQueryLogRepository, QueryLogRepository>();

// -------------------- AUTH --------------------

// ?? Require explicit config
var authEnabled = builder.Configuration.GetValue<bool?>("Auth:Enabled");

if (authEnabled == null)
{
    throw new Exception("Auth:Enabled is not configured.");
}

if (authEnabled.Value)
{
    var jwtKey = builder.Configuration["Jwt:Key"];
    var issuer = builder.Configuration["Jwt:Issuer"];
    var audience = builder.Configuration["Jwt:Audience"];

    if (string.IsNullOrEmpty(jwtKey))
        throw new Exception("JWT Key is not configured.");

    if (string.IsNullOrEmpty(issuer))
        throw new Exception("JWT Issuer is not configured.");

    if (string.IsNullOrEmpty(audience))
        throw new Exception("JWT Audience is not configured.");

    builder.Services.AddAuthentication(options =>
    {
        options.DefaultAuthenticateScheme = JwtBearerDefaults.AuthenticationScheme;
        options.DefaultChallengeScheme = JwtBearerDefaults.AuthenticationScheme;
    })
    .AddJwtBearer(options =>
    {
        // ?? MUST be true in production
        options.RequireHttpsMetadata = true;

        options.SaveToken = true;

        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidateAudience = true,
            ValidateIssuerSigningKey = true,
            ValidateLifetime = true, // ? important
            ClockSkew = TimeSpan.FromMinutes(2),

            ValidIssuer = issuer,
            ValidAudience = audience,
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtKey))
        };

        // SSE support (EventSource)
        options.Events = new JwtBearerEvents
        {
            OnMessageReceived = context =>
            {
                var accessToken = context.Request.Query["access_token"].FirstOrDefault();
                if (!string.IsNullOrEmpty(accessToken))
                {
                    context.Token = accessToken;
                }
                return Task.CompletedTask;
            }
        };
    });
}

// -------------------- BUILD APP --------------------

ModelTestService.TestModel();

var app = builder.Build();

// -------------------- MIDDLEWARE --------------------

app.UseMiddleware<LocalRagAPI.Middleware.ErrorHandlingMiddleware>();
app.UseMiddleware<LocalRagAPI.Middleware.RequestLoggingMiddleware>();

// Safe Qdrant init (won’t crash app)
using (var scope = app.Services.CreateScope())
{
    var qdrant = scope.ServiceProvider.GetRequiredService<QdrantService>();

    try
    {
        await qdrant.InitializeCollection();
    }
    catch (Exception ex)
    {
        Console.WriteLine("Qdrant init failed: " + ex.Message);
    }
}

app.UseDefaultFiles();
app.UseStaticFiles();

// Swagger only in development
if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseHttpsRedirection();

if (authEnabled.Value)
{
    app.UseAuthentication();
}

app.UseAuthorization();

app.MapControllers();

app.Run();



//using LocalRagAPI.Services;
//using LocalRagAPI.Data;
//using LocalRagAPI.Repositories;
//using Microsoft.EntityFrameworkCore;
//using Microsoft.AspNetCore.Authentication.JwtBearer;
//using Microsoft.IdentityModel.Tokens;
//using System.Text;

//var builder = WebApplication.CreateBuilder(args);

//// Add services to the container.

//builder.Services.AddControllers();
//// Learn more about configuring Swagger/OpenAPI at https://aka.ms/aspnetcore/swashbuckle
//builder.Services.AddEndpointsApiExplorer();
//builder.Services.AddSwaggerGen(c =>
//{
//    c.SwaggerDoc("v1", new() { Title = "LocalRagAPI", Version = "v1" });

//    c.AddSecurityDefinition("Bearer", new Microsoft.OpenApi.Models.OpenApiSecurityScheme
//    {
//        Name = "Authorization",
//        Type = Microsoft.OpenApi.Models.SecuritySchemeType.Http,
//        Scheme = "bearer",
//        BearerFormat = "JWT",
//        In = Microsoft.OpenApi.Models.ParameterLocation.Header,
//        Description = "Enter 'Bearer {token}'"
//    });

//    c.AddSecurityRequirement(new Microsoft.OpenApi.Models.OpenApiSecurityRequirement
//    {
//        {
//            new Microsoft.OpenApi.Models.OpenApiSecurityScheme
//            {
//                Reference = new Microsoft.OpenApi.Models.OpenApiReference
//                {
//                    Type = Microsoft.OpenApi.Models.ReferenceType.SecurityScheme,
//                    Id = "Bearer"
//                }
//            },
//            new string[] {}
//        }
//    });
//});
//// Configure HTTP clients with increased connection limits for throughput
//builder.Services.AddHttpClient<ILLMService, GroqLLMService>()
//    .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler { MaxConnectionsPerServer = 50 });

//builder.Services.AddSingleton<VectorStore>();
//builder.Services.AddSingleton<ChatMemory>();

//builder.Services.AddHttpClient<JinaEmbeddingService>()
//    .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler { MaxConnectionsPerServer = 50 });

//builder.Services.AddHttpClient<JinaRerankerService>()
//    .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler { MaxConnectionsPerServer = 50 });

//builder.Services.AddSingleton<QdrantService>();
//builder.Services.AddSingleton<PromptBuilderService>();
//// Ingestion queue and job store for background processing
//builder.Services.AddSingleton<LocalRagAPI.Services.DocumentIngestionQueue>();
//builder.Services.AddSingleton<LocalRagAPI.Services.IngestionJobStore>();
//builder.Services.AddScoped<LocalRagAPI.Services.DocumentProcessor>();
//builder.Services.AddHostedService<LocalRagAPI.Services.DocumentIngestionWorker>();

//// -------- Phase1: EF Core (Postgres) and Auth wiring (optional) --------
//var connectionString = builder.Configuration.GetConnectionString("Default");

//if (string.IsNullOrEmpty(connectionString))
//{
//    throw new Exception("Database connection string is not configured.");
//}

//builder.Services.AddDbContext<ApplicationDbContext>(options =>
//    options.UseNpgsql(connectionString));

//builder.Services.AddScoped<IUserRepository, UserRepository>();
//builder.Services.AddScoped<IDocumentRepository, DocumentRepository>();
//builder.Services.AddScoped<IChatSessionRepository, ChatSessionRepository>();
//builder.Services.AddScoped<IMessageRepository, MessageRepository>();
//builder.Services.AddScoped<IQueryLogRepository, QueryLogRepository>();

//var authEnabled = builder.Configuration.GetValue<bool>("Auth:Enabled", false);

//if (authEnabled)
//{
//    var jwtKey = builder.Configuration["Jwt:Key"];
//    var issuer = builder.Configuration["Jwt:Issuer"];
//    var audience = builder.Configuration["Jwt:Audience"];

//    if (string.IsNullOrEmpty(jwtKey))
//        throw new Exception("JWT Key is not configured.");

//    if (string.IsNullOrEmpty(issuer))
//        throw new Exception("JWT Issuer is not configured.");

//    if (string.IsNullOrEmpty(audience))
//        throw new Exception("JWT Audience is not configured.");

//    builder.Services.AddAuthentication(options =>
//    {
//        options.DefaultAuthenticateScheme = JwtBearerDefaults.AuthenticationScheme;
//        options.DefaultChallengeScheme = JwtBearerDefaults.AuthenticationScheme;
//    })
//    .AddJwtBearer(options =>
//    {
//        options.RequireHttpsMetadata = false;
//        options.SaveToken = true;
//        options.TokenValidationParameters = new TokenValidationParameters
//        {
//            ValidateIssuer = true,
//            ValidateAudience = true,
//            ValidateIssuerSigningKey = true,
//            ValidIssuer = issuer,
//            ValidAudience = audience,
//            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtKey))
//        };

//        // Support JWT in query string for EventSource (SSE) clients
//        options.Events = new JwtBearerEvents
//        {
//            OnMessageReceived = context =>
//            {
//                var accessToken = context.Request.Query["access_token"].FirstOrDefault();
//                if (!string.IsNullOrEmpty(accessToken))
//                {
//                    context.Token = accessToken;
//                }
//                return Task.CompletedTask;
//            }
//        };
//    });
//}

//ModelTestService.TestModel();
//var app = builder.Build();

//// Add middleware for request logging and global error handling
//app.UseMiddleware<LocalRagAPI.Middleware.ErrorHandlingMiddleware>();
//app.UseMiddleware<LocalRagAPI.Middleware.RequestLoggingMiddleware>();

//using (var scope = app.Services.CreateScope())
//{
//    var qdrant = scope.ServiceProvider.GetRequiredService<QdrantService>();
//    await qdrant.InitializeCollection();
//}

//app.UseDefaultFiles();
//app.UseStaticFiles();


//// Configure the HTTP request pipeline.
//if (app.Environment.IsDevelopment())
//{
//    app.UseSwagger();
//    app.UseSwaggerUI();
//}

//app.UseHttpsRedirection();

//if (authEnabled)
//{
//    app.UseAuthentication();
//}

//app.UseAuthorization();

//app.MapControllers();

//app.Run();
