using Microsoft.Extensions.AI;
using AIResumeChatApp.Components;
using AIResumeChatApp.Services;
using AIResumeChatApp.Services.Ingestion;
using OpenAI;
using System.ClientModel;
using OllamaSharp;
using Microsoft.Data.Sqlite;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddRazorComponents().AddInteractiveServerComponents();

// --- GitHub Models client (unchanged) ---
var credential = new ApiKeyCredential(
    builder.Configuration["GitHubModels:Token"]
    ?? throw new InvalidOperationException("Missing configuration: GitHubModels:Token. See the README for details.")
);

var openAIOptions = new OpenAIClientOptions
{
    Endpoint = new Uri("https://models.inference.ai.azure.com")
};

var ghModelsClient = new OpenAIClient(credential, openAIOptions);
var chatClient = ghModelsClient.GetChatClient("gpt-4o-mini").AsIChatClient();
var embeddingGenerator = ghModelsClient.GetEmbeddingClient("text-embedding-3-small").AsIEmbeddingGenerator();

// --- Writable, persistent path on Azure App Service ---
var home = Environment.GetEnvironmentVariable("HOME") ?? AppContext.BaseDirectory;
// %HOME% -> D:\home or C:\home on Windows App Service
var dataDir = Path.Combine(home, "data", "AIResumeChatApp");
Directory.CreateDirectory(dataDir);

// Vector DB under HOME\data
var vectorStorePath = Path.Combine(dataDir, "vector-store.db");
// Optional: Shared cache & pooling can improve concurrent access a bit
var vectorStoreConnectionString = $"Data Source={vectorStorePath};Cache=Shared;Pooling=True";

// Register your SQLite-backed collections
builder.Services.AddSqliteCollection<string, IngestedChunk>("data-airesumechatapp-chunks", vectorStoreConnectionString);
builder.Services.AddSqliteCollection<string, IngestedDocument>("data-airesumechatapp-documents", vectorStoreConnectionString);

// App services
builder.Services.AddScoped<DataIngestor>();
builder.Services.AddSingleton<SemanticSearch>();
builder.Services.AddChatClient(chatClient).UseFunctionInvocation().UseLogging();
builder.Services.AddEmbeddingGenerator(embeddingGenerator);

var app = builder.Build();

// Configure the HTTP request pipeline.
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    app.UseHsts();
}

app.UseHttpsRedirection();
app.UseAntiforgery();
app.UseStaticFiles();

app.MapRazorComponents<App>()
   .AddInteractiveServerRenderMode();

// ---- One-time SQLite tuning (optional but recommended on SMB-backed storage) ----
// Set WAL mode to reduce file locking contention.
try
{
    using var conn = new SqliteConnection(vectorStoreConnectionString);
    await conn.OpenAsync();
    using var cmd = conn.CreateCommand();
    cmd.CommandText = "PRAGMA journal_mode=WAL;";
    await cmd.ExecuteNonQueryAsync();
}
catch
{
    // ignore if initialization fails; the app can still run
}

// ---- Ingestion source paths ----
// If you only READ PDFs that ship with the app, wwwroot is fine (even if read-only).
// If you want to DROP new PDFs at runtime, use a writable HOME-based folder instead.
var packagedPdfDir = Path.Combine(app.Environment.WebRootPath, "Data");
var writablePdfDir = Path.Combine(dataDir, "Content"); // e.g., D:\home\data\AIResumeChatApp\Content
Directory.CreateDirectory(writablePdfDir);

// Prefer the writable folder if it has files; otherwise fall back to packaged PDFs
var pdfSourceDir = Directory.EnumerateFiles(writablePdfDir, "*.pdf", SearchOption.AllDirectories).Any()
    ? writablePdfDir
    : packagedPdfDir;

// Important: only ingest trusted content
await DataIngestor.IngestDataAsync(app.Services, new PDFDirectorySource(pdfSourceDir));

app.Run();
