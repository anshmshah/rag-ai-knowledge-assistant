using LocalRagAPI.Services;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.

builder.Services.AddControllers();
// Learn more about configuring Swagger/OpenAPI at https://aka.ms/aspnetcore/swashbuckle
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();
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

app.UseAuthorization();

app.MapControllers();

app.Run();
