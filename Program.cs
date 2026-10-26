using Astra.Api.Middleware;

var builder = WebApplication.CreateBuilder(args);
builder.AddAstraServices();

var app = builder.Build();
app.UseMiddleware<ApiExceptionMiddleware>();
if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
    app.UseSwagger();
    app.UseSwaggerUI();
}
if (app.Configuration.GetValue<bool>("Https:Redirect"))
    app.UseHttpsRedirection();
app.UseRouting();
app.UseCors("ReactPolicy");
app.UseAuthentication();
app.UseMiddleware<WorkspaceAccessMiddleware>();
app.UseAuthorization();
app.MapControllers();
app.Run();

public partial class Program;
