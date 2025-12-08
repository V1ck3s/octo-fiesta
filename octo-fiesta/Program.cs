using octo_fiesta.Models;
using octo_fiesta.Services;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.

builder.Services.AddControllers();
builder.Services.AddHttpClient();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

// Configuration
builder.Services.Configure<SubsonicSettings>(
    builder.Configuration.GetSection("Subsonic"));

// Services métier
builder.Services.AddSingleton<ILocalLibraryService, LocalLibraryService>();
builder.Services.AddScoped<IMusicMetadataService, DeezerMetadataService>();
builder.Services.AddScoped<IDownloadService, DeezerDownloadService>();

builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy =>
    {
        policy.AllowAnyOrigin()
            .AllowAnyMethod()
            .AllowAnyHeader()
            .WithExposedHeaders("X-Content-Duration", "X-Total-Count", "X-Nd-Authorization");
    });
});

var app = builder.Build();

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseHttpsRedirection();

app.UseAuthorization();

app.UseCors();

app.MapControllers();

app.Run();