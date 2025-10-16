using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
// Learn more about configuring Swagger/OpenAPI at https://aka.ms/aspnetcore/swashbuckle
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();
builder.Services.AddHttpClient();

var app = builder.Build();

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseHttpsRedirection();

app.MapGet("/me", async (IHttpClientFactory httpClientFactory) =>
{
    var client = httpClientFactory.CreateClient();
    var catfact = await client.GetFromJsonAsync<object>("https://catfact.ninja/fact");
    var result = JsonSerializer.Deserialize<CatFact>(catfact.ToString() ?? string.Empty);

    var user = new {
        Email = "eugeneatinbire@gmail.com",
        Name = "Eugene Atinbire",
        Stack = "C#, .Net"
    };

    var profile =  new {
        Status = "success",
        User = user,
        Timestamp = DateTime.UtcNow,
        fact = result.fact
    };
    
    return profile;
})
.WithName("me")
.WithOpenApi();

app.Run();
