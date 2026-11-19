using Content.Api;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddContentApi(builder.Configuration);

var app = builder.Build();
app.UseContentApi();
app.Run();

/// <summary>Entry point; public so integration tests can host the API in-process.</summary>
public partial class Program;
