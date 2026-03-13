using LocalRagAPI.Services;
using LocalRagAPI.Data;
using LocalRagAPI.Repositories;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;
using System.Text;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.

builder.Services.AddControllers();
// Learn more about configuring Swagger/OpenAPI at https://aka.ms/aspnetcore/swashbuckle
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
// Configure HTTP clients with increased connection limits for throughput
builder.Services.AddHttpClient<ILLMService, GroqLLMService>()
    .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler { MaxConnectionsPerServer = 50 });

builder.Services.AddSingleton<VectorStore>();
builder.Services.AddSingleton<ChatMemory>();

builder.Services.AddHttpClient<JinaEmbeddingService>()
    .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler { MaxConnectionsPerServer = 50 });

builder.Services.AddHttpClient<JinaRerankerService>()
    .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler { MaxConnectionsPerServer = 50 });

builder.Services.AddSingleton<QdrantService>();
builder.Services.AddSingleton<PromptBuilderService>();
// Ingestion queue and job store for background processing
builder.Services.AddSingleton<LocalRagAPI.Services.DocumentIngestionQueue>();
builder.Services.AddSingleton<LocalRagAPI.Services.IngestionJobStore>();
builder.Services.AddScoped<LocalRagAPI.Services.DocumentProcessor>();
builder.Services.AddHostedService<LocalRagAPI.Services.DocumentIngestionWorker>();

// -------- Phase1: EF Core (Postgres) and Auth wiring (optional) --------
var connectionString = builder.Configuration.GetConnectionString("Default")
                       ?? builder.Configuration["ConnectionStrings:Default"]
                       ?? "Host=localhost;Database=localrag;Username=postgres;Password=ansh";

builder.Services.AddDbContext<ApplicationDbContext>(options =>
    options.UseNpgsql(connectionString));

builder.Services.AddScoped<IUserRepository, UserRepository>();
builder.Services.AddScoped<IDocumentRepository, DocumentRepository>();
builder.Services.AddScoped<IChatSessionRepository, ChatSessionRepository>();
builder.Services.AddScoped<IMessageRepository, MessageRepository>();
builder.Services.AddScoped<IQueryLogRepository, QueryLogRepository>();

var authEnabled = builder.Configuration.GetValue<bool>("Auth:Enabled", false);

if (authEnabled)
{
    var jwtKey = builder.Configuration["Jwt:Key"] ?? "THIS_IS_A_SUPER_SECRET_KEY_FOR_LOCAL_RAG_API_2026_123456";
    var issuer = builder.Configuration["Jwt:Issuer"] ?? "localrag";
    var audience = builder.Configuration["Jwt:Audience"] ?? "localrag";

    builder.Services.AddAuthentication(options =>
    {
        options.DefaultAuthenticateScheme = JwtBearerDefaults.AuthenticationScheme;
        options.DefaultChallengeScheme = JwtBearerDefaults.AuthenticationScheme;
    })
    .AddJwtBearer(options =>
    {
        options.RequireHttpsMetadata = false;
        options.SaveToken = true;
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidateAudience = true,
            ValidateIssuerSigningKey = true,
            ValidIssuer = issuer,
            ValidAudience = audience,
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtKey))
        };

        // Support JWT in query string for EventSource (SSE) clients
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

ModelTestService.TestModel();
var app = builder.Build();

// Add middleware for request logging and global error handling
app.UseMiddleware<LocalRagAPI.Middleware.ErrorHandlingMiddleware>();
app.UseMiddleware<LocalRagAPI.Middleware.RequestLoggingMiddleware>();

using (var scope = app.Services.CreateScope())
{
    var qdrant = scope.ServiceProvider.GetRequiredService<QdrantService>();
    await qdrant.InitializeCollection();
}

app.UseDefaultFiles();
app.UseStaticFiles();


// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseHttpsRedirection();

if (authEnabled)
{
    app.UseAuthentication();
}

app.UseAuthorization();

app.MapControllers();

app.Run();
