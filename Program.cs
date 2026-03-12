using LocalRagAPI.Services;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.

builder.Services.AddControllers();
// Learn more about configuring Swagger/OpenAPI at https://aka.ms/aspnetcore/swashbuckle
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();
builder.Services.AddHttpClient<ILLMService, GroqLLMService>();
builder.Services.AddSingleton<VectorStore>();
builder.Services.AddSingleton<ChatMemory>();
//builder.Services.AddSingleton<IEmbeddingService, JinaEmbeddingService>();
builder.Services.AddHttpClient<JinaEmbeddingService>();
builder.Services.AddHttpClient<JinaRerankerService>();
builder.Services.AddSingleton<QdrantService>();
builder.Services.AddSingleton<PromptBuilderService>();

ModelTestService.TestModel();
var app = builder.Build();

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
